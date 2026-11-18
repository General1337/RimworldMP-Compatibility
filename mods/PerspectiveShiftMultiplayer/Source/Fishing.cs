using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PerspectiveShiftMultiplayer;

// Perspective Shift's fishing minigame for a player's colonist. The host runs
// the fishing job as usual (casting, waiting, the bite, the hook on the
// player's click). It tells the player when a fish bites, and when the fish is
// hooked the reel window opens on the player's screen instead of the host's.
// The player reels with their own mouse; the result comes back as an action,
// which the host accepts only while that colonist is reeling and not sooner
// than a real reel can end. The catch itself is made on the host.
internal static class Fishing
{
    static Type? driver, dialog;
    static FieldInfo? stageField, dialogField, windowPawn, windowCell, resolvedField, wonField, timerField;
    static MethodInfo? stopLoop, resolveCatch;
    static ConstructorInfo? make;
    internal static bool On => dialog != null;
    internal static string? Action;
    const string Channel = "psmp.fish";
    const int Biting = 1, Hooked = 2, Reeling = 3;
    // The quickest a reel can be won or lost: half the bar at the best gain
    // rate after the start grace (Perspective Shift's own numbers).
    const double Quickest = 0.8 + 0.5 / 0.33 - 0.2, Longest = 120;
    static double Now => Time.realtimeSinceStartup;

    internal static bool Bind()
    {
        driver = Ps.Assembly.GetType("PerspectiveShift.JobDriver_PSFishMinigame", false);
        dialog = Ps.Assembly.GetType("PerspectiveShift.Dialog_FishingMinigame", false);
        var utility = Ps.Assembly.GetType("PerspectiveShift.PSFishingUtility", false);
        if (driver != null && dialog != null && utility != null)
        {
            stageField = AccessTools.Field(driver, "stage"); dialogField = AccessTools.Field(driver, "dialog");
            windowPawn = AccessTools.Field(dialog, "pawn"); windowCell = AccessTools.Field(dialog, "cell");
            resolvedField = AccessTools.Field(dialog, "resolved"); wonField = AccessTools.Field(dialog, "won"); timerField = AccessTools.Field(dialog, "resultTimer");
            stopLoop = AccessTools.Method(dialog, "StopReelLoop");
            resolveCatch = AccessTools.Method(utility, "ResolveCatch", new[] { typeof(Pawn), typeof(IntVec3) });
            make = AccessTools.Constructor(dialog, new[] { typeof(Pawn), typeof(IntVec3) });
            if (new object?[] { stageField, dialogField, windowPawn, windowCell, resolvedField, wonField, timerField, stopLoop, resolveCatch, make }.All(x => x != null)
                && AccessTools.PropertyGetter(dialog, "CanKeepFishing") != null && AccessTools.Method(dialog, "Resolve") != null)
                return true;
        }
        driver = dialog = null;
        Log.Warning("[Perspective Shift - Multiplayer] The fishing minigame for players is off: this Perspective Shift version's fishing is not the one this add-on knows.");
        return false;
    }

    internal static void Patch(Harmony harmony)
    {
        if (!On) return;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.IsOpen), new[] { typeof(Window) }), postfix: new HarmonyMethod(typeof(Fishing), nameof(IsOpenPostfix)));
            harmony.Patch(AccessTools.PropertyGetter(dialog, "CanKeepFishing"), prefix: new HarmonyMethod(typeof(Fishing), nameof(CanKeepPrefix)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(dialog, "Resolve"), prefix: new HarmonyMethod(typeof(Fishing), nameof(ResolvePrefix)) { priority = Priority.First });
            harmony.Patch(AccessTools.DeclaredMethod(dialog, nameof(Window.PreClose)), postfix: new HarmonyMethod(typeof(Fishing), nameof(Closed)));
            foreach (var name in new[] { nameof(Window.PreOpen), nameof(Window.WindowUpdate), nameof(Window.PreClose) })
                harmony.Patch(AccessTools.DeclaredMethod(dialog, name), transpiler: new HarmonyMethod(typeof(Fishing), nameof(SpeedTranspiler)));
        }
        catch (Exception e) { dialog = null; Log.Warning("[Perspective Shift - Multiplayer] The fishing minigame for players is off: " + e.Message); }
    }

    internal static void Register()
    {
        if (!On) return;
        Action = Multiplayer.RegisterAction(typeof(Fishing), nameof(ActReel));
        Multiplayer.RegisterPlayerHandler(Channel, OnMessage);
    }

    internal static void Reset() { reels.Clear(); held.Clear(); stages.Clear(); mine.Clear(); shown.Clear(); }

    // ---- host ----
    sealed class Reel { internal int Id, Slot; internal Pawn Pawn = null!; internal IntVec3 Cell; internal Window Window = null!; internal object? Driver; internal double Since; }
    static readonly Dictionary<int, Reel> reels = new();
    // Reel windows standing for one open on a player's screen: "open" for the job.
    static readonly HashSet<Window> held = new();
    static readonly Dictionary<int, int> stages = new();
    static int next = 1;
    // Development counters (read with a reflection probe).
    internal static int Bites, Started, Landed, Escaped, Refused; internal static string Last = "";

    static int StageOf(object d) => Convert.ToInt32(stageField!.GetValue(d));

    // Perspective Shift opening the reel window for a player's colonist.
    internal static bool TakeOver(Window window)
    {
        if (!On || !HostSide.Active || window.GetType() != dialog) return false;
        var pawn = windowPawn!.GetValue(window) as Pawn;
        if (pawn == null || HostSide.RemoteOf(pawn) is not { Slot: >= 0 } r) return false;
        var cell = (IntVec3)windowCell!.GetValue(window)!;
        var reel = new Reel { Id = next++, Slot = r.Slot, Pawn = pawn, Cell = cell, Window = window, Driver = pawn.jobs?.curDriver, Since = Now };
        if (!Multiplayer.SendToPlayer(r.Slot, Channel, "reel;" + reel.Id + ";" + pawn.thingIDNumber + ";" + cell.x + ";" + cell.z)) return false;
        reels[reel.Id] = reel; held.Add(window); Started++; Last = "reel #" + reel.Id + " for " + r.Slot;
        return true;
    }
    // A reel window for any player's colonist (even one whose player just
    // dropped out): never shown on the host's screen, where it would pause the game.
    internal static bool ForPlayer(Window window)
        => On && HostSide.Active && window.GetType() == dialog && windowPawn!.GetValue(window) is Pawn pawn && HostSide.RemoteOf(pawn) != null;
    static void IsOpenPostfix(Window window, ref bool __result) { if (held.Count > 0 && window != null && held.Contains(window)) __result = true; }

    // The player's result: accepted only while that colonist reels.
    internal static void ActReel(int id, bool won)
    {
        int slot = Multiplayer.CurrentPlayer;
        if (!HostSide.Active || !reels.TryGetValue(id, out var reel) || reel.Slot != slot) { Refused++; Last = "result refused for " + slot + " #" + id; return; }
        var d = reel.Pawn.jobs?.curDriver;
        bool reeling = d != null && d == reel.Driver && StageOf(d) == Reeling;
        End(reel, false);
        // A catch can't come sooner than a real reel ends; giving up can.
        if (!reeling || won && Now - reel.Since < Quickest) { Refused++; Last = "#" + id + (reeling ? " too quick" : " not reeling"); return; }
        var pawn = reel.Pawn;
        Multiplayer.AsPlayer(slot, () =>
        {
            pawn.skills?.Learn(SkillDefOf.Animals, won ? 260f : 60f);
            if (won) resolveCatch!.Invoke(null, new object[] { pawn, reel.Cell });
            else Messages.Message("PS_FishingEscapedMessage".Translate(), pawn, MessageTypeDefOf.RejectInput, false);
        });
        if (won) Landed++; else Escaped++;
        Last = "#" + id + (won ? " landed" : " escaped");
    }

    static void End(Reel reel, bool tell)
    {
        reels.Remove(reel.Id); held.Remove(reel.Window);
        if (tell) Multiplayer.SendToPlayer(reel.Slot, Channel, "end;" + reel.Id);
    }

    // Every frame: the bite and the hook to the player; reels nobody can finish end.
    internal static void Frame()
    {
        if (!On) return;
        foreach (var r in HostSide.Remotes.Values)
        {
            if (r.Slot < 0 || r.Pawn is not { } pawn) continue;
            int stage = pawn.jobs?.curDriver is { } d && d.GetType() == driver ? StageOf(d) : -1;
            if (stages.TryGetValue(pawn.thingIDNumber, out int was) && was == stage) continue;
            stages[pawn.thingIDNumber] = stage;
            if (stage == Biting) Bites++;
            Multiplayer.SendToPlayer(r.Slot, Channel, "stage;" + pawn.thingIDNumber + ";" + stage);
        }
        foreach (var reel in reels.Values.ToArray())
        {
            bool mine = HostSide.Remotes.TryGetValue(reel.Slot, out var r) && r.Pawn == reel.Pawn;
            if (mine && reel.Pawn.jobs?.curDriver == reel.Driver && Now - reel.Since < Longest) continue;
            Last = "#" + reel.Id + " ended without a result";
            End(reel, true);
        }
    }

    // ---- player ----
    static readonly Dictionary<Window, int> mine = new();
    // Stages the host sent for colonists whose job this game's copy doesn't show.
    static readonly Dictionary<int, int> shown = new();
    static SoundDef? Sound(string name) => DefDatabase<SoundDef>.GetNamedSilentFail(name);

    static void OnMessage(string text)
    {
        if (!HostSide.IsPlayerGame || !On) return;
        var f = text.Split(';');
        if (f.Length >= 3 && f[0] == "stage" && int.TryParse(f[1], out int id) && int.TryParse(f[2], out int stage))
        {
            var pawn = PlayerSide.FindPawn(id);
            if (pawn?.jobs?.curDriver is { } d && d.GetType() == driver) { stageField!.SetValue(d, Enum.ToObject(stageField.FieldType, Math.Max(stage, 0))); shown.Remove(id); }
            else if (stage >= 0) shown[id] = stage; else shown.Remove(id);
            if (stage == Biting) Sound("PS_FishBite")?.PlayOneShotOnCamera();
            else if (stage == Hooked) Sound("PS_FishHit")?.PlayOneShotOnCamera();
        }
        else if (f.Length >= 5 && f[0] == "reel" && int.TryParse(f[1], out int reel) && int.TryParse(f[2], out int who)
                 && int.TryParse(f[3], out int x) && int.TryParse(f[4], out int z) && PlayerSide.FindPawn(who) is { } pawn)
        {
            var window = (Window)make!.Invoke(new object[] { pawn, new IntVec3(x, 0, z) });
            mine[window] = reel;
            Find.WindowStack.Add(window);
        }
        else if (f.Length >= 2 && f[0] == "end" && int.TryParse(f[1], out int over))
        {
            foreach (var w in mine.Where(kv => kv.Value == over).Select(kv => kv.Key).ToArray())
                if (!(bool)resolvedField!.GetValue(w)!) { hostClosing = true; try { w.Close(false); } finally { hostClosing = false; } }
        }
    }

    // The host decides when fishing stops; this game's copy of the job may lag.
    static bool CanKeepPrefix(Window __instance, ref bool __result)
    {
        if (!HostSide.IsPlayerGame || !mine.ContainsKey(__instance)) return true;
        __result = true;
        return false;
    }
    // The reel's end on a player's game: the window shows it, the host makes the catch.
    static bool ResolvePrefix(Window __instance, bool success)
    {
        if (!HostSide.IsPlayerGame || !mine.TryGetValue(__instance, out int id)) return true;
        stopLoop!.Invoke(__instance, null);
        resolvedField!.SetValue(__instance, true); wonField!.SetValue(__instance, success); timerField!.SetValue(__instance, success ? 0.55f : 1.1f);
        if (!success) Sound("PS_FishEscape")?.PlayOneShotOnCamera();
        if (Action != null) Multiplayer.RequestAction(Action, 1, new object?[] { id, success }, (status, _, why) => Last = "result " + status + " " + why);
        return false;
    }
    // The player closing their reel before it ends gives the fish up: the host
    // ends the fishing now instead of waiting out the reel.
    static bool hostClosing;
    static void Closed(Window __instance)
    {
        if (!mine.TryGetValue(__instance, out int id)) return;
        mine.Remove(__instance);
        if (!hostClosing && !(bool)resolvedField!.GetValue(__instance)! && Action != null)
            Multiplayer.RequestAction(Action, 1, new object?[] { id, false }, (status, _, why) => Last = "gave up " + status + " " + why);
    }

    // The reel window holds the game at normal speed; a player's game leaves
    // speed to the host.
    public static void SetSpeed(TickManager manager, TimeSpeed speed) { if (!HostSide.IsPlayerGame) manager.CurTimeSpeed = speed; }
    static IEnumerable<CodeInstruction> SpeedTranspiler(IEnumerable<CodeInstruction> code)
    {
        var setter = AccessTools.PropertySetter(typeof(TickManager), nameof(TickManager.CurTimeSpeed));
        var ours = AccessTools.Method(typeof(Fishing), nameof(SetSpeed));
        foreach (var i in code)
        {
            if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && Equals(i.operand, setter)) { i.opcode = OpCodes.Call; i.operand = ours; }
            yield return i;
        }
    }

    // "!" and "HIT!" over a colonist whose fishing job this game's copy doesn't show.
    internal static void OnGUI()
    {
        if (shown.Count == 0 || Find.CurrentMap is not { } map) return;
        foreach (var (id, stage) in shown)
        {
            if (stage is not (Biting or Hooked)) continue;
            var pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
            if (pawn == null) continue;
            var at = (pawn.DrawPos + new Vector3(0f, 0f, 0.95f)).MapToUIPosition();
            var font = Text.Font; var anchor = Text.Anchor;
            Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(1f, 0.86f, 0.35f);
            Widgets.Label(new Rect(at.x - 60f, at.y - 18f, 120f, 36f), stage == Biting ? "!" : "PS_FishingHit".Translate().Resolve());
            GUI.color = Color.white; Text.Font = font; Text.Anchor = anchor;
        }
    }

    // ---- Perspective Shift's right-click menu for a player's colonist ----
    // The host builds and runs a player's menu choice with that player's
    // colonist in Perspective Shift's avatar slot, as the player's own game
    // did: fishing, chatting and the other choices it offers its avatar.
    internal struct Scope { internal bool On; internal object? Previous; }
    static object? MenuAvatar(List<Pawn>? pawns)
    {
        if (!HostSide.Active) return null;
        int slot = Multiplayer.CurrentPlayer;
        if (slot == Multiplayer.LocalPlayer || !HostSide.Remotes.TryGetValue(slot, out var r)) return null;
        if (pawns != null && (pawns.Count != 1 || pawns[0] != r.Pawn)) return null;
        return r.Avatar;
    }
    internal static void MenuBegin(List<Pawn> selectedPawns, out Scope __state)
    {
        __state = default;
        if (MenuAvatar(selectedPawns) is { } avatar && !ReferenceEquals(Ps.Current, avatar)) { __state.On = true; __state.Previous = Ps.Push(avatar, "menu"); }
    }
    internal static void ChosenBegin(out Scope __state)
    {
        __state = default;
        if (MenuAvatar(null) is { } avatar && !ReferenceEquals(Ps.Current, avatar)) { __state.On = true; __state.Previous = Ps.Push(avatar, "menu"); }
    }
    internal static void ScopeEnd(Scope __state) { if (__state.On) Ps.Pop(__state.Previous); }
}
