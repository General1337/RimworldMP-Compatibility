using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace PerspectiveShiftMultiplayer;

// Perspective Shift's storage menu for a player's colonist. Its window holds
// the item picked up with the mouse, so a window on the wrong game could lose
// or copy items. Here the host keeps the real window, unshown, and only it
// holds the item; the player's screen shows a copy that sends each click to
// the host as an action, and the host answers with the held item and the slot
// order. Closing, a disconnect, a release, walking away and a save put a held
// item down next to the colonist, as Perspective Shift does on closing.
internal static class Storage
{
    static Type? dialog;
    static FieldInfo? storageField, slotField, cursorField, keepTex, holdTex, equipTex, wearTex, idsField, gapsField;
    static MethodInfo? handleClick, makeWearJob;
    static ConstructorInfo? make;
    internal static bool On => dialog != null;
    internal static string? Action;
    const string Channel = "psmp.storage";
    const float Reach = 3f;
    static double Now => Time.realtimeSinceStartup;

    internal static bool Bind()
    {
        dialog = Ps.Assembly.GetType("PerspectiveShift.Dialog_StorageMenu", false);
        var comp = Ps.Assembly.GetType("PerspectiveShift.CompStorageSlotOrder", false);
        if (dialog != null && comp != null)
        {
            storageField = AccessTools.Field(dialog, "storage"); slotField = AccessTools.Field(dialog, "slotComp"); cursorField = AccessTools.Field(dialog, "cursorItem");
            keepTex = AccessTools.Field(dialog, "Keep"); holdTex = AccessTools.Field(dialog, "Hold"); equipTex = AccessTools.Field(dialog, "Equip"); wearTex = AccessTools.Field(dialog, "Wear");
            idsField = AccessTools.Field(comp, "thingIDToSlot"); gapsField = AccessTools.Field(comp, "gapSlots");
            handleClick = AccessTools.Method(dialog, "HandleClick");
            makeWearJob = AccessTools.Method(Ps.Avatar, "TryMakeWearOrEquipJob");
            make = AccessTools.Constructor(dialog, new[] { typeof(Building_Storage) });
            if (new object?[] { storageField, slotField, cursorField, keepTex, holdTex, equipTex, wearTex, idsField, gapsField, handleClick, makeWearJob, make }.All(x => x != null)
                && AccessTools.Method(dialog, "DrawActionButton") != null)
                return true;
        }
        dialog = null;
        Log.Warning("[Perspective Shift - Multiplayer] The storage menu for players is off: this Perspective Shift version's storage menu is not the one this add-on knows.");
        return false;
    }

    internal static void Patch(Harmony harmony)
    {
        if (!On) return;
        try
        {
            harmony.Patch(handleClick, prefix: new HarmonyMethod(typeof(Storage), nameof(ClickPrefix)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(dialog, "DrawActionButton"), prefix: new HarmonyMethod(typeof(Storage), nameof(ButtonPrefix)) { priority = Priority.First });
            harmony.Patch(AccessTools.DeclaredMethod(dialog, nameof(Window.PreClose)), prefix: new HarmonyMethod(typeof(Storage), nameof(ClosingPrefix)) { priority = Priority.First });
            // A save keeps no item that only a window holds.
            harmony.Patch(AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame)), prefix: new HarmonyMethod(typeof(Storage), nameof(BeforeSave)));
        }
        catch (Exception e) { dialog = null; Log.Warning("[Perspective Shift - Multiplayer] The storage menu for players is off: " + e.Message); }
    }

    internal static void Register()
    {
        if (!On) return;
        Action = Multiplayer.RegisterAction(typeof(Storage), nameof(ActStorage));
        Multiplayer.RegisterPlayerHandler(Channel, OnMessage);
    }

    internal static void Reset() { sessions.Clear(); mine.Clear(); }

    static Thing? Cursor(Window w) => cursorField!.GetValue(w) as Thing is { Destroyed: false } t ? t : null;
    static Building_Storage StorageOf(Window w) => (Building_Storage)storageField!.GetValue(w)!;

    // ---- host ----
    sealed class Session { internal int Id, Slot; internal Window Window = null!; internal Building_Storage Storage = null!; internal Remote Remote = null!; }
    static readonly Dictionary<int, Session> sessions = new();
    static int next = 1;
    // Development counters (read with a reflection probe).
    internal static int Opened, Actions, Refused, Closed; internal static string Last = "";

    // Perspective Shift opening the menu for a player's colonist (from Swap.AddWindow).
    internal static bool TakeOver(Remote r, Window window)
    {
        if (!On || window.GetType() != dialog || r.Slot < 0) return false;
        End(r.Slot, false);
        // Separate colonies: another colony's shelf is theirs to allow.
        if (Multiplayer.UseRefusal(r.Slot, StorageOf(window)) is { Length: > 0 } why) { HostSide.Tell(r.Slot, why); Refused++; Last = "shelf refused for " + r.Slot; return true; }
        var s = new Session { Id = next++, Slot = r.Slot, Window = window, Storage = StorageOf(window), Remote = r };
        if (!Multiplayer.SendToPlayer(r.Slot, Channel, "open;" + s.Id + ";" + s.Storage.thingIDNumber)) { Last = "could not reach " + r.Slot; return false; }
        sessions[r.Slot] = s; Opened++; Last = "open #" + s.Id + " for " + r.Slot;
        SendState(s);
        return true;
    }

    // kind 0: a slot click (button, slot, the item the player saw there or -1);
    // kind 1: Keep (0), Hold (1) or Equip/Wear (2) with a button; kind 2: close.
    internal static void ActStorage(int id, int kind, int a, int b, int c)
    {
        int slot = Multiplayer.CurrentPlayer;
        if (!HostSide.Active || !sessions.TryGetValue(slot, out var s) || s.Id != id) { Refused++; Last = "refused for " + slot + " #" + id; return; }
        if (kind == 2) { End(slot, false); return; }
        if (!Usable(s)) { End(slot, true); return; }
        Actions++;
        bool close = false;
        using (s.Remote.Enter())
            Multiplayer.AsPlayer(slot, () =>
            {
                if (kind == 0) Click(s, a, b, c);
                else close = Button(s, a, b);
            });
        if (close) End(slot, true);
        else SendState(s);
    }

    static bool Usable(Session s)
    {
        var pawn = s.Remote.Pawn;
        return pawn is { Spawned: true, Dead: false, Downed: false } && s.Storage is { Spawned: true } && pawn.Map == s.Storage.Map
            && HostSide.Remotes.TryGetValue(s.Slot, out var r) && r == s.Remote
            && s.Storage.OccupiedRect().ClosestCellTo(pawn.Position).DistanceTo(pawn.Position) <= Reach + Ps.Setting<float>("grabRange");
    }

    static void Click(Session s, int button, int slotIdx, int seenId)
    {
        var storage = s.Storage;
        var cells = storage.AllSlotCellsList();
        int perCell = storage.def.building.maxItemsInCell;
        if (button is not (0 or 1) || slotIdx < 0 || slotIdx >= cells.Count * perCell) return;
        var comp = slotField!.GetValue(s.Window);
        Ps.Call(AccessTools.Method(comp!.GetType(), "ReconcileStaleEntries"), comp, storage);
        var items = Ps.Call<Thing[]>(AccessTools.Method(comp.GetType(), "BuildSlotArray"), comp, storage);
        var item = items[slotIdx];
        // What the player saw there must be what is there; their copy is a frame behind.
        if ((item?.thingIDNumber ?? -1) != seenId) { Last = "slot " + slotIdx + " changed"; return; }
        if (item != null && Multiplayer.UseRefusal(s.Slot, item) is { Length: > 0 } why) { HostSide.Tell(s.Slot, why); Refused++; Last = "item refused"; return; }
        Ps.Call(handleClick!, s.Window, item, button, cells[slotIdx / perCell], slotIdx);
    }

    // Perspective Shift's Keep / Hold / Equip buttons, on the host's window.
    static bool Button(Session s, int which, int button)
    {
        var w = s.Window; var pawn = s.Remote.Pawn;
        var cursor = Cursor(w);
        if (cursor == null) return false;
        switch (which)
        {
            case 0:
            {
                int count = Math.Min(button == 1 ? 1 : cursor.stackCount, MassUtility.CountToPickUpUntilOverEncumbered(pawn, cursor));
                if (count <= 0) { Messages.Message("PS_CannotCarryMoreWeight".Translate(), MessageTypeDefOf.RejectInput, false); return false; }
                var item = cursor.SplitOff(count);
                if (item == cursor) cursor = null;
                if (pawn.inventory.innerContainer.TryAdd(item, true)) item.def.soundDrop.PlayOneShot(pawn);
                else if (!item.Destroyed && item.stackCount > 0) { if (cursor == null) cursor = item; else cursor.TryAbsorbStack(item, true); }
                cursorField!.SetValue(w, cursor is { stackCount: > 0 } ? cursor : null);
                return false;
            }
            case 1:
            {
                var item = cursor.SplitOff(button == 1 ? 1 : cursor.stackCount);
                if (item == cursor) cursor = null;
                int had = item.stackCount;
                int added = pawn.carryTracker.TryStartCarry(item, had, reserve: false);
                if (added > 0) item.def.soundDrop.PlayOneShot(pawn);
                if (added < had && !item.Destroyed && item.stackCount > 0) { if (cursor == null) cursor = item; else cursor.TryAbsorbStack(item, true); }
                cursorField!.SetValue(w, cursor is { stackCount: > 0 } ? cursor : null);
                return true;
            }
            case 2:
            {
                if (!cursor.def.IsWeapon && cursor is not Apparel) return false;
                cursorField!.SetValue(w, null);
                if (!cursor.Spawned) GenSpawn.Spawn(cursor, pawn.Position, pawn.Map, WipeMode.Vanish);
                var args = new object?[] { pawn, cursor, null };
                if ((bool)makeWearJob!.Invoke(null, args)! && args[2] is Job job) pawn.jobs.TryTakeOrderedJob(job);
                return true;
            }
        }
        return false;
    }

    // The menu's end: Perspective Shift's own closing puts a held item down.
    static void End(int slot, bool tell)
    {
        if (!sessions.Remove(slot, out var s)) return;
        Closed++; Last = "closed #" + s.Id;
        try
        {
            var pawn = s.Remote.Pawn;
            if (Cursor(s.Window) is { } held && pawn is { Spawned: true }) using (s.Remote.Enter()) s.Window.PreClose();
            else if (Cursor(s.Window) is { } lost && s.Storage is { Spawned: true }) GenPlace.TryPlaceThing(lost, s.Storage.Position, s.Storage.Map, ThingPlaceMode.Near);
        }
        catch (Exception e) { Log.Warning("[Perspective Shift - Multiplayer] Closing a player's storage menu: " + e.Message); }
        // Still held (closing failed, or colonist and shelf are both gone): down
        // wherever the colonist is, else into their inventory; never dropped.
        if (Cursor(s.Window) is { Spawned: false } left)
        {
            var pawn = s.Remote.Pawn;
            try
            {
                if (pawn?.MapHeld is { } map) GenPlace.TryPlaceThing(left, pawn.PositionHeld, map, ThingPlaceMode.Near);
                else if (s.Storage.MapHeld is { } map2) GenPlace.TryPlaceThing(left, s.Storage.PositionHeld, map2, ThingPlaceMode.Near);
                if (!left.Spawned && !left.Destroyed && pawn?.inventory != null) pawn.inventory.innerContainer.TryAdd(left, true);
            }
            catch (Exception e) { Log.Warning("[Perspective Shift - Multiplayer] Putting down a held item: " + e.Message); }
        }
        cursorField!.SetValue(s.Window, null);
        if (tell) Multiplayer.SendToPlayer(s.Slot, Channel, "close;" + s.Id);
    }

    // Every frame: menus nobody can use any more close.
    internal static void Frame()
    {
        if (sessions.Count == 0) return;
        foreach (var s in sessions.Values.ToArray())
            if (!Usable(s)) End(s.Slot, true);
    }
    static void BeforeSave()
    {
        if (!HostSide.Active) return;
        foreach (var slot in sessions.Keys.ToArray()) End(slot, true);
    }

    // The held item and the slot order, for the player's copy.
    static void SendState(Session s)
    {
        var b = new StringBuilder("state;").Append(s.Id).Append(';');
        if (Cursor(s.Window)?.GetInnerIfMinified() is { } t) b.Append(t.def.defName).Append(',').Append(t.Stuff?.defName ?? "").Append(',').Append(t.stackCount).Append(',').Append(t.TryGetQuality(out var q) ? (int)q : -1);
        b.Append(';');
        var comp = slotField!.GetValue(s.Window)!;
        b.Append(string.Join(",", ((IDictionary<int, int>)idsField!.GetValue(comp)!).Select(kv => kv.Key + ":" + kv.Value)));
        b.Append(';').Append(string.Join(",", (IEnumerable<int>)gapsField!.GetValue(comp)!));
        Multiplayer.SendToPlayer(s.Slot, Channel, b.ToString());
    }

    // ---- player ----
    static readonly Dictionary<Window, int> mine = new();
    static bool closingByHost;

    static void OnMessage(string text)
    {
        if (!HostSide.IsPlayerGame || !On) return;
        var f = text.Split(';');
        if (f.Length < 2 || !int.TryParse(f[1], out int id)) return;
        switch (f[0])
        {
            case "open" when f.Length >= 3 && int.TryParse(f[2], out int building):
            {
                foreach (var old in mine.Keys.ToArray()) Close(old);
                var storage = Find.Maps.SelectMany(m => m.listerThings.AllThings).OfType<Building_Storage>().FirstOrDefault(x => x.thingIDNumber == building);
                if (storage == null) return;
                var w = (Window)make!.Invoke(new object[] { storage });
                w.forcePause = false;
                mine[w] = id;
                Find.WindowStack.Add(w);
                break;
            }
            case "state" when f.Length >= 5:
            {
                var w = mine.FirstOrDefault(kv => kv.Value == id).Key;
                if (w == null) return;
                cursorField!.SetValue(w, Shown(f[2]));
                var comp = slotField!.GetValue(w)!;
                var ids = (IDictionary<int, int>)idsField!.GetValue(comp)!; ids.Clear();
                foreach (var pair in f[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (pair.Split(':') is { Length: 2 } p && int.TryParse(p[0], out int k) && int.TryParse(p[1], out int v)) ids[k] = v;
                var gaps = (ISet<int>)gapsField!.GetValue(comp)!; gaps.Clear();
                foreach (var g in f[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) if (int.TryParse(g, out int n)) gaps.Add(n);
                break;
            }
            case "close":
                foreach (var w in mine.Where(kv => kv.Value == id).Select(kv => kv.Key).ToArray()) Close(w);
                break;
        }
    }
    static void Close(Window w) { closingByHost = true; try { w.Close(false); } finally { closingByHost = false; } mine.Remove(w); }

    // A picture of the host's held item (a minified building shows the building):
    // never spawned, never saved, and made without the game's thing maker, so it
    // takes no thing ID and runs no setup on this game.
    static Thing? Shown(string row)
    {
        var f = row.Split(',');
        if (f.Length < 4 || DefDatabase<ThingDef>.GetNamedSilentFail(f[0]) is not { } def || def.thingClass == null) return null;
        var stuff = f[1].Length > 0 ? DefDatabase<ThingDef>.GetNamedSilentFail(f[1]) : null;
        try
        {
            var t = (Thing)Activator.CreateInstance(def.thingClass);
            t.def = def;
            if (def.MadeFromStuff) t.SetStuffDirect(stuff ?? GenStuff.DefaultStuffFor(def));
            if (t is ThingWithComps withComps) withComps.InitializeComps();
            if (def.useHitPoints) t.HitPoints = t.MaxHitPoints;
            t.stackCount = Math.Max(1, int.TryParse(f[2], out int n) ? n : 1);
            if (int.TryParse(f[3], out int q) && q >= 0) t.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)q, null);
            return t;
        }
        catch (Exception e) { Log.Warning("[Perspective Shift - Multiplayer] Showing a held " + def.defName + ": " + e.Message); return null; }
    }

    static void Send(Window w, int kind, int a, int b, int c)
    {
        if (Action == null || !mine.TryGetValue(w, out int id)) return;
        Multiplayer.RequestAction(Action, 1, new object?[] { id, kind, a, b, c }, (status, _, why) => Last = "sent " + status + " " + why);
    }
    // A slot click: to the host, with the item this screen shows there.
    static bool ClickPrefix(Window __instance, Thing clickedItem, int button, int slotIdx)
    {
        if (!HostSide.IsPlayerGame || !mine.ContainsKey(__instance)) return true;
        Send(__instance, 0, button, slotIdx, clickedItem?.thingIDNumber ?? -1);
        return false;
    }
    // Keep, Hold and Equip/Wear: to the host.
    static void ButtonPrefix(Window __instance, Texture2D icon, ref Action<int> onClick)
    {
        if (!HostSide.IsPlayerGame || !mine.ContainsKey(__instance)) return;
        int which = icon == (Texture2D)keepTex!.GetValue(null)! ? 0 : icon == (Texture2D)holdTex!.GetValue(null)! ? 1 : 2;
        var w = __instance;
        onClick = button => Send(w, 1, which, button, 0);
    }
    // Closing the copy: the host closes the real menu (and puts a held item down).
    static void ClosingPrefix(Window __instance)
    {
        if (!HostSide.IsPlayerGame || !mine.ContainsKey(__instance)) return;
        cursorField!.SetValue(__instance, null);
        if (!closingByHost) Send(__instance, 2, 0, 0, 0);
        mine.Remove(__instance);
    }
}
