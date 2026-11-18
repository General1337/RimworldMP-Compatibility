using System.Text;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace PerspectiveShiftMultiplayer;

// The host: every player's inhabited colonist is simulated here, with that
// player's input, by Perspective Shift's own code (Remote). Perspective
// Shift's own buttons work for players: the host applies a player's
// "Inhabit character", "Swap" or "Return to director" to that player.
internal static class HostSide
{
    internal static bool InSession => Multiplayer.InSession;
    internal static bool IsPlayerGame => Multiplayer.InSession && !Multiplayer.IsHost;
    internal static bool Active => InSession && !IsPlayerGame;

    static readonly Dictionary<int, Remote> remotes = new();
    internal static IReadOnlyDictionary<int, Remote> Remotes => remotes;
    static readonly Dictionary<int, string> keys = new();
    static bool internalCall, dirty;
    internal static bool Internal => internalCall;
    static double lastPoll, lastBroadcast, lastPose;
    static int hostPawn = -1;
    static readonly List<(int Pawn, string Key, string Name)> loaded = new();
    // Colonists this add-on drafted when their player dropped out: undrafted
    // again when the player comes back.
    static readonly HashSet<int> draftedWhileAway = new();
    static double loadedUntil;
    static readonly Dictionary<(int, string), (int Second, int Count)> rate = new();
    internal static string? ClickAction, KeyAction;
    internal static int ClicksArrived; internal static string LastClick = "";
    // Development counters (read with a reflection probe).
    const double Grace = 60, LoadedGrace = 120;

    static double Now => Time.realtimeSinceStartup;
    static bool Limited(int slot, string what, int perSecond)
    {
        int second = (int)Now;
        var (at, count) = rate.TryGetValue((slot, what), out var had) ? had : (second, 0);
        if (at != second) { at = second; count = 0; }
        rate[(slot, what)] = (at, ++count);
        return count > perSecond;
    }
    static string KeyOf(int slot)
    {
        if (!keys.TryGetValue(slot, out var key) || key.Length == 0) keys[slot] = key = Multiplayer.PlayerKey(slot);
        return key;
    }
    static string NameOf(int slot) => Multiplayer.Player(slot)?.Name is { Length: > 0 } n ? n : "#" + slot;
    static bool quiet;
    internal static void Tell(int slot, string text) { if (!quiet) Multiplayer.AsPlayer(slot, () => Messages.Message(text, MessageTypeDefOf.RejectInput, false)); }
    internal static Remote? RemoteOf(Pawn pawn) => remotes.Values.FirstOrDefault(r => r.Pawn == pawn);

    internal static void Reset() { remotes.Clear(); keys.Clear(); Holders.All.Clear(); loaded.Clear(); hostPawn = -1; Swaps.Rebuild(); }

    // ---- Perspective Shift's own entry points, per player ----
    internal static bool SetAvatarPrefix(Pawn pawn, bool showMessage)
    {
        if (internalCall || !Active) return true;
        int slot = Remote.Current?.Slot ?? Multiplayer.CurrentPlayer;
        if (slot == Multiplayer.LocalPlayer)
        {
            if (Holders.OfPawn(pawn) is { } held && held.Slot != slot) { Tell(slot, Holders.Veto(slot, pawn)!); return false; }
            return true;
        }
        Inhabit(slot, pawn, showMessage);
        return false;
    }
    internal static bool ClearAvatarPrefix()
    {
        if (internalCall || !Active) return true;
        if (Remote.Current is { } r) { Release(r.Slot, null); return false; }
        int slot = Multiplayer.CurrentPlayer;
        if (slot == Multiplayer.LocalPlayer) return true;
        Release(slot, null);
        return false;
    }
    internal static bool RevokeControlPrefix(Pawn pawn, DamageInfo? dinfo, Hediff? hediff)
    {
        if (internalCall || !Active) return true;
        if (RemoteOf(pawn) is not { } r)
        {
            // The host's own colonist downed or killed while a player's command
            // runs: Perspective Shift's own handling, as the host's (its return
            // would otherwise read as that player's).
            if (Multiplayer.CurrentPlayer == Multiplayer.LocalPlayer || pawn != Ps.PawnOf(Ps.Own)) return true;
            internalCall = true;
            try { Ps.Call(Ps.RevokeControl, null, pawn, dinfo, hediff); }
            finally { internalCall = false; }
            return false;
        }
        if (Ps.Authentic)
        {
            // Authentic: a downed character stays yours; a dead or kidnapped one
            // ends your character, and you keep watching (decision 3).
            if (pawn.Dead || pawn.IsKidnapped())
            {
                string key = KeyOf(r.Slot);
                if (key.Length > 0) Holders.DeadKeys.Add(key);
                Release(r.Slot, "PSMP_CharacterDied".Translate(pawn.LabelShort));
            }
            return false;
        }
        Release(r.Slot, "PS_LostControl".Translate());
        return false;
    }

    internal static void Inhabit(int slot, Pawn pawn, bool showMessage)
    {
        if (Limited(slot, "inhabit", 2)) return;
        string key = KeyOf(slot);
        if (pawn == null || !pawn.Spawned && !Ps.InVehicle(pawn) || pawn.Dead) { Tell(slot, "PSMP_CannotInhabit".Translate()); return; }
        if (Multiplayer.Player(slot) is { IsSpectator: true }) { Tell(slot, "PSMP_Spectating".Translate()); return; }
        if (Ps.Authentic && key.Length > 0 && Holders.DeadKeys.Contains(key)) { Tell(slot, "PSMP_YourCharacterDied".Translate()); return; }
        // The host's own colonist this very frame too, not only from the last frame's table.
        SyncHostAvatar();
        if (Holders.OfPawn(pawn) is { } held && held.Slot != slot) { Tell(slot, Holders.Veto(slot, pawn)!); return; }
        if (Multiplayer.ControlRefusal(slot, pawn) is { Length: > 0 } refusal) { Tell(slot, refusal); return; }
        if (pawn.InMentalState) { Tell(slot, "PawnIsInMentalState".Translate(pawn)); return; }
        if (Ps.Authentic && key.Length > 0 && Holders.AuthenticByKey.TryGetValue(key, out int mine) && mine != pawn.thingIDNumber)
        { Tell(slot, "PS_AuthenticLockedReason".Translate()); return; }
        if (remotes.TryGetValue(slot, out var had))
        {
            if (had.Pawn == pawn) return;
            Release(slot, null);
        }
        var cameraLock = Ps.CameraLock; bool pending = Ps.PendingDeath; int authentic = Ps.AuthenticId;
        object? made = null;
        var previous = Ps.Push(null, "inhabit");
        try
        {
            internalCall = true;
            Multiplayer.AsPlayer(slot, () => Ps.Call(Ps.SetAvatarMethod, null, pawn, showMessage));
            made = Ps.Current;
        }
        finally
        {
            internalCall = false; Ps.Pop(previous); Ps.CameraLock = cameraLock; Ps.PendingDeath = pending; Ps.AuthenticId = authentic;
        }
        if (made == null) return;
        remotes[slot] = new Remote { Slot = slot, Avatar = made };
        Holders.All.RemoveAll(h => h.Slot == slot || h.PawnId == pawn.thingIDNumber);
        Holders.All.Add(new Holder { PawnId = pawn.thingIDNumber, Slot = slot, Key = key, Name = NameOf(slot) });
        if (Ps.Authentic && key.Length > 0 && !Holders.AuthenticByKey.ContainsKey(key)) Holders.AuthenticByKey[key] = pawn.thingIDNumber;
        dirty = true; Swaps.Rebuild();
    }

    internal static void Release(int slot, string? why)
    {
        if (!remotes.Remove(slot, out var r)) return;
        var pawn = r.Pawn;
        try
        {
            internalCall = true;
            using (r.Enter()) if (pawn != null && !pawn.Destroyed) Ps.Call(Ps.CleanupPawnState, null, pawn);
        }
        catch (Exception e) { Log.Warning("[Perspective Shift - Multiplayer] Releasing " + pawn?.LabelShort + ": " + e.Message); }
        finally { internalCall = false; }
        Holders.All.RemoveAll(h => h.PawnId == pawn?.thingIDNumber);
        // A colonist this add-on drafted while its player was away goes back as it was.
        Undraft(pawn);
        if (why != null) Multiplayer.AsPlayer(slot, () => Messages.Message(why, MessageTypeDefOf.NegativeEvent, false));
        dirty = true; Swaps.Rebuild();
    }

    // The host's own button on a colonist a player plays (decision 5).
    internal static void ForceRelease(Pawn pawn)
    {
        if (RemoteOf(pawn) is { } r) Release(r.Slot, "PSMP_ReleasedByHost".Translate(pawn.LabelShort));
        else if (Holders.OfPawn(pawn) is { } waiting) { Holders.All.Remove(waiting); loaded.RemoveAll(l => l.Pawn == pawn.thingIDNumber); dirty = true; }
    }

    // ---- players' input ----
    internal static void OnInput(int slot, string payload)
    {
        if (!Active || !remotes.TryGetValue(slot, out var r)) return;
        var f = payload.Split(';');
        if (f.Length < 5 || !int.TryParse(f[2], out int flags)) return;
        var move = new Vector3(Mathf.Clamp(Holders.P(f[0]), -1, 1), 0, Mathf.Clamp(Holders.P(f[1]), -1, 1));
        if (move.sqrMagnitude > 1) move.Normalize();
        var map = r.Pawn?.MapHeld;
        float ax = Holders.P(f[3]), az = Holders.P(f[4]);
        if (map != null) { ax = Mathf.Clamp(ax, 0, map.Size.x - 0.01f); az = Mathf.Clamp(az, 0, map.Size.z - 0.01f); }
        var i = r.Input;
        i.Move = move; i.Sprint = (flags & 1) != 0; i.Walk = (flags & 2) != 0; i.Frozen = (flags & 4) != 0; i.Fire = (flags & 8) != 0;
        i.Aim = new Vector3(ax, 0, az); i.At = Now;
    }

    // Clicks: Perspective Shift's own click handling, on the host, for that
    // player's colonist; its own range checks use the host's positions.
    internal static void ActClick(int button, int clicks, IntVec3 cell, float x, float z, bool shift)
    {
        int slot = Multiplayer.CurrentPlayer;
        ClicksArrived++;
        if (!Active || !remotes.TryGetValue(slot, out var r) || button is not (0 or 1) || Limited(slot, "click", 20)) { LastClick = "refused for " + slot; return; }
        LastClick = "applied for " + slot + " at " + cell;
        var map = r.Pawn?.MapHeld;
        if (map == null || !cell.InBounds(map)) return;
        // Perspective Shift picks up, opens and uses what is in the clicked cell;
        // in separate colonies another colony's things are theirs to allow.
        // Shooting (drafted) and a gunner's aim are attacks, not use.
        if (!(button == 0 && r.Pawn!.Drafted) && !Ps.InVehicle(r.Pawn))
            foreach (var thing in cell.GetThingList(map))
            {
                if (thing is Pawn) continue;
                if (Multiplayer.UseRefusal(slot, thing) is { Length: > 0 } why) { Tell(slot, why); LastClick += " -> refused: " + why; return; }
            }
        using (r.Enter())
            Multiplayer.AsPlayer(slot, () =>
            {
                r.UseClick(button, Mathf.Clamp(clicks, 1, 3), cell, new Vector3(Mathf.Clamp(x, 0, map.Size.x), 0, Mathf.Clamp(z, 0, map.Size.z)), shift);
                try
                {
                    bool used = Ps.Call<bool>(Ps.HandleSelectorClick, r.Avatar);
                    var p = r.Pawn;
                    LastClick += " -> " + (used ? "used" : "not used") + " (pawn at " + p.PositionHeld + (p.InMentalState ? ", mental" : "") + (p.Drafted ? ", drafted" : "")
                        + (Find.TickManager.Paused ? ", paused" : "") + (Ps.InVehicle(p) ? ", in vehicle" : "") + ")";
                }
                finally { r.Event = null; }
            });
    }
    internal static void ActKey(int which)
    {
        int slot = Multiplayer.CurrentPlayer;
        if (!Active || !remotes.TryGetValue(slot, out var r) || Limited(slot, "key", 4)) return;
        var def = DefDatabase<KeyBindingDef>.GetNamedSilentFail(which == 0 ? "PS_EatFood" : "PS_DoRecreation");
        if (def == null) return;
        using (r.Enter())
            Multiplayer.AsPlayer(slot, () =>
            {
                // Perspective Shift uses up the key's event after starting the job;
                // a player's action runs outside the host's GUI, so it gets its own.
                r.UseAim(); r.KeyDown = def; r.Event = new Event { type = EventType.KeyDown };
                try { Ps.Call(which == 0 ? Ps.HandleEat : Ps.HandleRecreation, r.Avatar); }
                finally { r.KeyDown = null; r.Event = null; }
            });
    }

    // ---- every tick and frame ----
    internal static void Tick()
    {
        if (!Active || remotes.Count == 0) return;
        foreach (var r in remotes.Values.ToArray())
        {
            // In a vehicle the colonist is not spawned; Perspective Shift drives and aims it.
            if (r.Pawn is not { } p || !p.Spawned && !Ps.InVehicle(p)) continue;
            try { using (r.Enter()) Multiplayer.AsPlayer(r.Slot, () => Ps.Call(Ps.AvatarTick, r.Avatar)); }
            catch (Exception e) { Problem("tick", e); }
        }
    }
    internal static void Frame()
    {
        if (!Active) return;
        SyncHostAvatar();
        bool paused = Find.TickManager?.Paused ?? true;
        foreach (var r in remotes.Values.ToArray())
        {
            var pawn = r.Pawn;
            if (pawn == null || pawn.Destroyed || pawn.Dead && !Ps.Authentic) { Release(r.Slot, pawn?.Dead == true ? "PS_LostControl".Translate().Resolve() : null); continue; }
            bool spawned = pawn.Spawned;
            if (!spawned && !Ps.InVehicle(pawn)) continue;
            try
            {
                using (r.Enter())
                    Multiplayer.AsPlayer(r.Slot, () =>
                    {
                        if (spawned) Ps.Call(Ps.RenderPawn, r.Avatar);
                        if (paused) return;
                        r.UseAim();
                        // In a vehicle: Perspective Shift's own steering. Its frame
                        // first asks for the colonist's map, which a colonist inside
                        // a vehicle does not have, so the steering is called directly.
                        if (!spawned && Ps.UpdateVehiclePhysics != null && Ps.InVehicle(pawn, out var vehicle, out bool driver, out _))
                            Ps.Call(Ps.UpdateVehiclePhysics, r.Avatar, vehicle, driver);
                        else Ps.Call(Ps.UpdatePhysics, r.Avatar);
                        // Hold to fire: the player's button, the host's shot.
                        if (r.Input.Fire && r.Input.Fresh && !r.Input.Frozen && pawn.Drafted) Ps.Call(Ps.HandleFiring, r.Avatar);
                    });
            }
            catch (Exception e) { Problem("frame", e); }
        }
        Choices.Frame();
        Fishing.Frame();
        Storage.Frame();
        if (Now - lastPoll >= 1) { lastPoll = Now; Poll(); }
        if (dirty || Now - lastBroadcast >= 2) { dirty = false; lastBroadcast = Now; Multiplayer.SendToAll("psmp.holders", Holders.Encode(PerSlot)); }
        if (Now - lastPose >= 0.05) { lastPose = Now; SendPoses(); }
    }
    static (int, bool) PerSlot(int slot)
    {
        string key = slot == Multiplayer.LocalPlayer ? "" : KeyOf(slot);
        return key.Length == 0 ? (-1, false) : (Holders.AuthenticByKey.TryGetValue(key, out int id) ? id : -1, Holders.DeadKeys.Contains(key));
    }
    static readonly HashSet<string> logged = new();
    static void Problem(string where, Exception e) { if (logged.Add(where + e.GetType().Name)) Log.Warning("[Perspective Shift - Multiplayer] " + where + ": " + e); }

    // The host's own inhabited colonist is in the table too: no one else may take it.
    static void SyncHostAvatar()
    {
        var pawn = Ps.PawnOf(Ps.Own);
        int id = pawn?.thingIDNumber ?? -1;
        if (id == hostPawn) return;
        int me = Multiplayer.LocalPlayer;
        Holders.All.RemoveAll(h => h.Slot == me);
        if (pawn != null) Holders.All.Add(new Holder { PawnId = id, Slot = me, Name = NameOf(me) });
        hostPawn = id; dirty = true; Swaps.Rebuild();
    }

    // Who is still here; a player who left keeps their colonist, drafted, for
    // a minute (decision 4); one who comes back gets it back by key.
    static void Poll()
    {
        var players = Multiplayer.Players;
        foreach (var r in remotes.Values.ToArray())
        {
            var row = players.FirstOrDefault(p => p.Slot == r.Slot);
            var holder = Holders.All.FirstOrDefault(h => h.PawnId == r.Pawn?.thingIDNumber);
            if (row is { Connected: true }) { if (holder is { Waiting: true }) { holder.GraceUntil = 0; dirty = true; Undraft(r.Pawn); } continue; }
            if (holder == null) { Release(r.Slot, null); continue; }
            if (!holder.Waiting)
            {
                holder.GraceUntil = Now + Grace; dirty = true;
                var pawn = r.Pawn;
                if (pawn?.drafter != null && pawn.Spawned && !pawn.Downed && !pawn.Drafted) { pawn.drafter.Drafted = true; draftedWhileAway.Add(pawn.thingIDNumber); }
                remotes.Remove(r.Slot); remotes[-1000 - r.Slot] = r; r.Slot = -1000 - r.Slot;
            }
            else if (Now > holder.GraceUntil) Release(r.Slot, null);
        }
        foreach (var p in players.Where(p => p.Connected && !p.IsHost && !remotes.ContainsKey(p.Slot)))
        {
            string key = KeyOf(p.Slot);
            if (key.Length == 0) continue;
            // A returning player: their colonist from before the disconnect.
            if (remotes.Values.FirstOrDefault(r => r.Slot < 0 && Holders.All.FirstOrDefault(h => h.PawnId == r.Pawn?.thingIDNumber)?.Key == key) is { } back)
            {
                remotes.Remove(back.Slot); back.Slot = p.Slot; remotes[p.Slot] = back;
                var h = Holders.All.First(x => x.PawnId == back.Pawn.thingIDNumber); h.Slot = p.Slot; h.GraceUntil = 0; h.Name = p.Name; dirty = true; Swaps.Rebuild();
                Undraft(back.Pawn);
                continue;
            }
            // ... or from the saved game.
            // It stays theirs until it can be handed back (a colonist mid-break or
            // carried can't be inhabited yet): tried again each poll, quietly,
            // until the grace time ends.
            int at = loaded.FindIndex(l => l.Key == key);
            if (at >= 0)
            {
                var entry = loaded[at];
                var placeholder = Holders.All.FirstOrDefault(h => h.PawnId == entry.Pawn && h.Slot < 0);
                if (placeholder != null) Holders.All.Remove(placeholder);
                if (PlayerSide.FindPawn(entry.Pawn) is { } pawn)
                {
                    quiet = true;
                    try { Inhabit(p.Slot, pawn, false); }
                    finally { quiet = false; }
                }
                if (remotes.TryGetValue(p.Slot, out var got) && got.Pawn?.thingIDNumber == entry.Pawn) { loaded.RemoveAt(at); dirty = true; }
                else if (placeholder != null && !Holders.All.Any(h => h.PawnId == entry.Pawn)) Holders.All.Add(placeholder);
            }
        }
        if (loaded.Count > 0 && Now > loadedUntil) { foreach (var l in loaded) Holders.All.RemoveAll(h => h.PawnId == l.Pawn && h.Slot < 0); loaded.Clear(); dirty = true; }
    }

    static void Undraft(Pawn? pawn)
    {
        if (pawn == null || !draftedWhileAway.Remove(pawn.thingIDNumber)) return;
        if (pawn.drafter != null && pawn.Drafted) pawn.drafter.Drafted = false;
    }

    // Saved holders: waiting for their players for a while after the load.
    internal static void Loaded(List<string> rows)
    {
        Reset(); Holders.AuthenticByKey.Clear(); Holders.DeadKeys.Clear();
        foreach (var row in rows)
        {
            var f = row.Split('|');
            if (f.Length < 4 || !int.TryParse(f[1], out int id)) continue;
            if (f[0] == "h") { loaded.Add((id, f[2], f[3])); Holders.All.Add(new Holder { PawnId = id, Slot = -1, Key = f[2], Name = f[3], GraceUntil = 1 }); }
            else if (f[0] == "a") { if (id >= 0) Holders.AuthenticByKey[f[2]] = id; if (f[3] == "1") Holders.DeadKeys.Add(f[2]); }
        }
        loadedUntil = Now + LoadedGrace; dirty = true;
    }

    // Every inhabited colonist's exact spot, aim and lean, to every game (20 a second).
    static readonly StringBuilder pose = new();
    static void SendPoses()
    {
        pose.Clear();
        void Add(object? avatar)
        {
            if (Ps.PawnOf(avatar) is not { Spawned: true } pawn) return;
            var at = Ps.PhysicsOf(avatar!);
            var lean = (Vector3)Ps.Lean.GetValue(avatar)!;
            if (pose.Length > 0) pose.Append('|');
            pose.Append(pawn.thingIDNumber).Append(',').Append(at is { } p ? Holders.F(p.x) : "-").Append(',').Append(at is { } q ? Holders.F(q.z) : "-")
                .Append(',').Append(Holders.F((float)Ps.Aim.GetValue(avatar)!)).Append(',').Append(Holders.F(lean.x)).Append(',').Append(Holders.F(lean.z));
        }
        Add(Ps.Current);
        foreach (var r in remotes.Values) Add(r.Avatar);
        if (pose.Length > 0) Multiplayer.SendToAll("psmp.pose", pose.ToString());
    }
}
