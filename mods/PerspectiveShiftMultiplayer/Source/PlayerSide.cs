using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace PerspectiveShiftMultiplayer;

// A player's game: it never simulates. It shows the player's own inhabited
// colonist with Perspective Shift's camera, needs panel, cursor and buttons,
// reads the player's keys and mouse and sends them to the host, and draws
// every inhabited colonist where the host says it is.
internal static class PlayerSide
{
    static readonly Dictionary<int, object> others = new();
    internal static IEnumerable<(Pawn, object)> Others => others.Values.Select(a => (Ps.PawnOf(a)!, a)).Where(p => p.Item1 != null);
    sealed class Pose { internal Vector3? Target; internal float Aim; internal Vector3 Lean; internal double At; internal Vector3? Shown; }
    static readonly Dictionary<int, Pose> poses = new();
    static string? lastHolders;
    static double Now => Time.realtimeSinceStartup;

    internal static void Reset() { others.Clear(); poses.Clear(); lastHolders = null; Holders.All.Clear(); }

    // A colonist on a map, or aboard a vehicle there: Vehicle Framework keeps
    // the crew in its seats' own containers, out of the map's pawn lists.
    static readonly Dictionary<Type, FieldInfo?> seatsOf = new();
    static FieldInfo? crewOf;
    internal static Pawn? FindPawn(int id)
    {
        foreach (var map in Find.Maps)
            foreach (var p in map.mapPawns.AllPawns) if (p.thingIDNumber == id) return p;
        foreach (var map in Find.Maps)
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                var type = p.GetType();
                if (type == typeof(Pawn)) continue;
                if (!seatsOf.TryGetValue(type, out var seats)) seatsOf[type] = seats = AccessTools.Field(type, "handlers");
                if (seats?.GetValue(p) is not System.Collections.IList list) continue;
                foreach (var seat in list)
                {
                    if (seat == null) continue;
                    crewOf ??= AccessTools.Field(seat.GetType(), "thingOwner");
                    if (crewOf?.GetValue(seat) is ThingOwner crew)
                        foreach (var q in crew) if (q is Pawn found && found.thingIDNumber == id) return found;
                }
            }
        return null;
    }

    // ---- from the host ----
    internal static void OnHolders(string text)
    {
        if (!HostSide.IsPlayerGame) return;
        lastHolders = text;
        Apply();
    }
    internal static void Apply()
    {
        if (lastHolders == null || Current.ProgramState != ProgramState.Playing) return;
        int me = Multiplayer.LocalPlayer;
        Holders.Decode(lastHolders, me);
        var mine = Holders.OfSlot(me) is { } h ? FindPawn(h.PawnId) : null;
        if (Ps.PawnOf(Ps.Own) != mine)
        {
            if (mine == null) { if (Ps.Depth > 0) Ps.SetOwn(null); else ClearLocal(); }
            else { Ps.SetOwn(Ps.Make(mine)); Ps.CameraLock = null; Ps.PendingDeath = false; }
        }
        Ps.AuthenticId = Holders.MyAuthentic;
        var keep = new HashSet<int>();
        foreach (var other in Holders.All.Where(x => x.Slot != me || x.Waiting))
        {
            keep.Add(other.PawnId);
            if (others.TryGetValue(other.PawnId, out var a) && Ps.PawnOf(a) != null) continue;
            if (FindPawn(other.PawnId) is { } pawn) others[other.PawnId] = Ps.Make(pawn);
        }
        foreach (var id in others.Keys.Where(k => !keep.Contains(k)).ToList()) others.Remove(id);
        Swaps.Rebuild();
    }
    static bool clearing;
    internal static bool Clearing => clearing;
    static void ClearLocal()
    {
        if (Ps.Current == null) return;
        // Perspective Shift's own return: its camera and cursor; the colonist
        // itself is the host's to change (CleanupPawnState is skipped here).
        clearing = true;
        try { Ps.Call(Ps.ClearAvatarMethod, null); }
        finally { clearing = false; }
    }

    internal static void OnPose(string text)
    {
        if (!HostSide.IsPlayerGame) return;
        foreach (var entry in text.Split('|'))
        {
            var f = entry.Split(',');
            if (f.Length < 6 || !int.TryParse(f[0], out int id)) continue;
            if (!poses.TryGetValue(id, out var pose)) poses[id] = pose = new Pose();
            pose.Target = f[1] == "-" ? null : new Vector3(Holders.P(f[1]), 0, Holders.P(f[2]));
            pose.Aim = Holders.P(f[3]); pose.Lean = new Vector3(Holders.P(f[4]), 0, Holders.P(f[5])); pose.At = Now;
        }
    }

    // ---- every frame ----
    internal static void Frame()
    {
        if (!HostSide.IsPlayerGame) return;
        float blend = 1 - Mathf.Exp(-Time.deltaTime * 18f);
        void Place(object? avatar, bool render)
        {
            if (Ps.PawnOf(avatar) is not { Spawned: true } pawn) return;
            if (!poses.TryGetValue(pawn.thingIDNumber, out var pose) || pose.Target == null || Now - pose.At > 0.6) { if (pose != null) pose.Shown = null; Ps.Physics.SetValue(avatar, null); return; }
            var target = pose.Target.Value; target.y = pawn.def.Altitude;
            // The player's own colonist leads the host's position a little in
            // the direction they are pressing, so walking answers at once; the
            // host's position still decides (decision 10: no rollback).
            if (!render && Lead(pawn) is { } lead) target += lead;
            pose.Shown = pose.Shown is { } s && (s - target).sqrMagnitude < 4 ? Vector3.Lerp(s, target, blend) : target;
            Ps.Physics.SetValue(avatar, pose.Shown);
            Ps.Aim.SetValue(avatar, pose.Aim); Ps.Lean.SetValue(avatar, pose.Lean);
            if (render) Ps.Call(Ps.RenderPawn, avatar);
        }
        // The player's own: Perspective Shift draws it and its camera follows it.
        Place(Ps.Current, false);
        foreach (var a in others.Values) Place(a, true);
        if (Ps.ShownControls && !Settings.Get.seenControls) { Settings.Get.seenControls = true; Settings.Get.Write(); }
    }

    static Vector3 lastMove; static bool lastSprint, lastWalk;
    const float LeadSeconds = 0.12f;
    static Vector3? Lead(Pawn pawn)
    {
        if (lastMove.sqrMagnitude < 0.01f || pawn.Map == null || pawn.Downed || pawn.Drafted && pawn.stances?.curStance is Stance_Busy) return null;
        float gait = lastSprint ? Ps.Setting<float>("sprintSpeedMultiplier") : lastWalk ? Ps.Setting<float>("sneakSpeedMultiplier") : 1f;
        float cellsPerSecond = 60f / Mathf.Max(pawn.TicksPerMoveCardinal, 1f) * Ps.Setting<float>("moveSpeedMultiplier") * gait * Mathf.Max(Find.TickManager.TickRateMultiplier, 0f);
        var lead = lastMove.normalized * Mathf.Min(cellsPerSecond * LeadSeconds, 0.6f);
        var at = (pawn.Position.ToVector3Shifted() + lead).ToIntVec3();
        return at.InBounds(pawn.Map) && at.Walkable(pawn.Map) ? lead : null;
    }

    // ---- the player's input, to the host ----
    static string lastSent = "";
    static double sentAt, fireAt;
    // Perspective Shift's per-frame physics, on a player's game: read the keys
    // and mouse as Perspective Shift does, send them, move nothing.
    internal static bool UpdatePhysicsPrefix(object __instance)
    {
        if (!HostSide.IsPlayerGame) return true;
        if (!ReferenceEquals(__instance, Ps.Current)) return false;
        bool frozen = Ps.Call<bool>(Ps.ControlsFrozenGetter, null) || Ps.CameraLock.HasValue || WorldRendererUtility.WorldSelected;
        if (frozen) Ps.MoveInput.SetValue(__instance, Vector3.zero);
        else Ps.Call(Ps.UpdateInput, __instance);
        var move = (Vector3)Ps.MoveInput.GetValue(__instance)!;
        lastMove = move; lastSprint = (bool)Ps.Sprinting.GetValue(__instance)!; lastWalk = (bool)Ps.Walking.GetValue(__instance)!;
        var aim = UI.MouseMapPosition();
        int flags = ((bool)Ps.Sprinting.GetValue(__instance)! ? 1 : 0) | ((bool)Ps.Walking.GetValue(__instance)! ? 2 : 0) | (frozen ? 4 : 0) | (Now - fireAt < 0.15 ? 8 : 0);
        string payload = Holders.F(move.x) + ";" + Holders.F(move.z) + ";" + flags + ";" + Holders.F(aim.x) + ";" + Holders.F(aim.z);
        if (Now - sentAt >= 1 / 30.0 && (payload != lastSent || Now - sentAt >= 0.15))
        {
            if (Multiplayer.SendInput("psmp.input", payload)) { lastSent = payload; sentAt = Now;}
        }
        return false;
    }
    // Hold to fire: noted for the input stream; the host fires.
    internal static bool HoldToFirePrefix(object __instance, bool mouseOverGizmo, bool mouseOverUI)
    {
        if (!HostSide.IsPlayerGame) return true;
        var pawn = Ps.PawnOf(__instance);
        if (Event.current?.type == EventType.Repaint && pawn is { Drafted: true } && Ps.Setting<bool>("holdToFire") && Input.GetMouseButton(0)
            && !mouseOverUI && !mouseOverGizmo && !Ps.Call<bool>(Ps.ControlsFrozenGetter, null) && !Find.Targeter.IsTargeting && !Find.TickManager.Paused)
            fireAt = Now;
        return false;
    }
    // A click on the map: Perspective Shift's own click handling runs on the
    // host for this colonist; here only what the click does to this screen.
    // Development counters (read with a reflection probe): clicks seen, sent, and why the last one was not.
    internal static int ClicksSeen, ClicksSent; internal static string LastClick = "";
    internal static bool SelectorClickPrefix(object __instance, ref bool __result)
    {
        if (!HostSide.IsPlayerGame) return true;
        var ev = Event.current;
        var pawn = Ps.PawnOf(__instance);
        if (ev == null || ev.type != EventType.MouseDown || ev.button is not (0 or 1) || pawn == null) return true;
        ClicksSeen++;
        // Perspective Shift's own early outs that come before its vehicle code
        // change nothing: let them answer.
        string? skip = Find.Targeter.IsTargeting ? "targeting" : pawn.Downed ? "downed" : pawn.InMentalState ? "mental" : null;
        if (skip != null) { LastClick = skip; return true; }
        // In a vehicle: a gunner's clicks fire (left) or stop firing (right), on
        // the host. Perspective Shift's own vehicle code never runs here: it
        // would aim this game's copy of the turret.
        if (!pawn.Spawned)
        {
            if (!Ps.InVehicle(pawn, out _, out _, out bool gunner) || !gunner) { LastClick = "in vehicle, not gunner"; __result = false; return false; }
            if (Ps.Call<bool>(Ps.IsMouseOverUI, __instance) || Ps.Call<bool>(Ps.IsMouseOverColonistBar, __instance)) { LastClick = "over-ui"; __result = false; return false; }
            var aim = UI.MouseMapPosition();
            if (HostSide.ClickAction != null)
            {
                Multiplayer.RequestAction(HostSide.ClickAction, 1, new object?[] { ev.button, ev.clickCount, UI.MouseCell(), aim.x, aim.z, ev.shift },
                    (status, _, why) => LastClick = status + " " + why);
                ClicksSent++;
            }
            ev.Use();
            __result = true;
            return false;
        }
        skip = Find.TickManager.Paused ? "paused" : Ps.CameraLock.HasValue ? "camera"
            : Ps.Call<bool>(Ps.IsMouseOverUI, __instance) ? "over-ui" : Ps.Call<bool>(Ps.IsMouseOverColonistBar, __instance) ? "colonist-bar" : null;
        if (skip != null) { LastClick = skip; return true; }
        bool consume;
        if (ev.button == 0) consume = pawn.Drafted;
        else
        {
            bool carrying = pawn.carryTracker?.CarriedThing is { } c && pawn.inventory != null && c is not (Pawn or Corpse);
            if (pawn.Drafted && !carrying && Find.Selector.SelectedObjects.Any(o => o is Pawn p && p != pawn)) { __result = false; return false; }
            consume = carrying || pawn.Drafted;
        }
        if (ev.button == 0 && pawn.Drafted && UI.MouseCell() == pawn.Position) { __result = false; return false; }
        var at = UI.MouseMapPosition();
        if (HostSide.ClickAction != null)
        {
            Multiplayer.RequestAction(HostSide.ClickAction, 1, new object?[] { ev.button, ev.clickCount, UI.MouseCell(), at.x, at.z, ev.shift },
                (status, _, why) => LastClick = status + " " + why);
            ClicksSent++;
        }
        else LastClick = "no action";
        if (consume) ev.Use();
        __result = consume;
        return false;
    }
    internal static bool KeyPrefix(object __instance, int which)
    {
        if (!HostSide.IsPlayerGame) return true;
        var def = DefDatabase<KeyBindingDef>.GetNamedSilentFail(which == 0 ? "PS_EatFood" : "PS_DoRecreation");
        if (def != null && def.KeyDownEvent && HostSide.KeyAction != null)
        {
            Multiplayer.RequestAction(HostSide.KeyAction, 1, new object?[] { which });
            Event.current?.Use();
        }
        return false;
    }

    // A world load on a player's game carries the host's inhabited colonist and
    // death screen; the player's own comes from the host's table instead.
    internal static void WorldLoaded()
    {
        if (!HostSide.IsPlayerGame || Scribe.mode is LoadSaveMode.Inactive or LoadSaveMode.Saving) return;
        try { Ps.Current = null; Ps.PendingDeath = false; Ps.ShownControls = Settings.Get.seenControls; }
        catch (Exception e) { Log.Warning("[Perspective Shift - Multiplayer] " + e.Message); }
        others.Clear(); poses.Clear();
        if (Scribe.mode == LoadSaveMode.PostLoadInit) LongEventHandler.ExecuteWhenFinished(() => { using (Multiplayer.LocalScope()) Apply(); });
    }
}
