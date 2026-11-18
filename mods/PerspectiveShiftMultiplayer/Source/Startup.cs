using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace PerspectiveShiftMultiplayer;

public sealed class Settings : ModSettings
{
    // Whether this player has seen Perspective Shift's controls list. In
    // Perspective Shift it is saved with the world, so every player would get
    // the host's; here it is each player's own.
    public bool seenControls;
    internal static Settings Get = null!;
    public override void ExposeData() => Scribe_Values.Look(ref seenControls, "seenControls", false);
}

public sealed class PerspectiveShiftMultiplayerMod : Mod
{
    public PerspectiveShiftMultiplayerMod(ModContentPack content) : base(content)
    {
        Settings.Get = GetSettings<Settings>();
        LongEventHandler.ExecuteWhenFinished(Startup.Init);
    }
}

// Built only on the public RimworldMultiplayer.Api library, Harmony and
// Perspective Shift, bound by name. See README.md.
internal static class Startup
{
    internal static bool On;

    // Perspective Shift settings that are each player's own taste, not rules.
    static readonly string[] Personal =
    {
        "zoomSpeed", "minZoom", "maxZoom", "cameraEasing", "gizmoCorner", "disableCustomGizmos", "pinnedNeeds",
        "enableDamageScreenEffect", "damageEffectStyle", "showControlsOnFirstInhabit", "avatarUIScale", "sprintMode", "sneakMode",
        "holdToFire", "disableDoubleClickEquip", "disableDoubleClickEat", "disableDoubleClickDrug", "disableDoubleClickRead",
        "weaponTooltips", "apparelTooltips", "eatTooltips", "drugTooltips", "bookTooltips", "harvestTooltips", "billTooltips",
        "customCursors", "haulingCursor", "mineCursor", "smoothCursor", "buildCursor", "chopCursor", "harvestCursor", "cutCursor",
        "plantCursor", "plowCursor", "traverseCursor", "reloadCursors", "openCursor", "sleepCursor", "recreationCursor", "researchCursor", "cookCursor",
    };

    internal static void Init()
    {
        if (!Ps.Bind())
        {
            if (Ps.Assembly != null) Log.Warning("[Perspective Shift - Multiplayer] This Perspective Shift version is not supported; players cannot inhabit colonists. Missing: " + string.Join(", ", Ps.Missing));
            return;
        }
        if (!Multiplayer.Available) return;
        // Optional parts: each stays off by itself if Perspective Shift changed it.
        Choices.Bind();
        Fishing.Bind();
        Storage.Bind();
        if (Multiplayer.HostApiVersion < 5) { Log.Warning("[Perspective Shift - Multiplayer] Rimworld Multiplayer lacks what this add-on needs."); return; }
        var harmony = new Harmony("RimworldMultiplayer.PerspectiveShift");
        try { Patch(harmony); }
        catch (Exception e) { Log.Error("[Perspective Shift - Multiplayer] Could not start; players cannot inhabit colonists: " + e); harmony.UnpatchAll(harmony.Id); return; }
        // Declared only once everything above worked: until then Rimworld
        // Multiplayer's own host-only handling of Perspective Shift stays on.
        Multiplayer.RegisterControlVeto(Holders.Veto);
        // Perspective Shift's own panel of the inhabited colonist's buttons:
        // a press in it is for that colonist, whatever else is selected.
        // In a vehicle, the panel shows the vehicle's buttons.
        Multiplayer.RegisterGizmoSubject(() => InPanel > 0 ? (Ps.InVehicle(Ps.PawnOf(Ps.Current), out var vehicle, out _, out _) ? vehicle : Ps.PawnOf(Ps.Current)) : null);
        Multiplayer.RegisterInputChannel("psmp.input", HostSide.OnInput);
        Multiplayer.RegisterPlayerHandler("psmp.holders", PlayerSide.OnHolders);
        Multiplayer.RegisterPlayerHandler("psmp.pose", PlayerSide.OnPose);
        HostSide.ClickAction = Multiplayer.RegisterAction(typeof(HostSide), nameof(HostSide.ActClick));
        HostSide.KeyAction = Multiplayer.RegisterAction(typeof(HostSide), nameof(HostSide.ActKey));
        Choices.Register();
        Fishing.Register();
        Storage.Register();
        Multiplayer.RegisterPersonalSettings(Ps.Settings.GetType(), Personal);
        Multiplayer.RegisterCompatibilityFor(typeof(Startup), "ferny.perspectiveshift");
        On = true;
        var problems = Multiplayer.Declarations().Where(d => d.Contains("|rimworldmultiplayer.perspectiveshift|", StringComparison.OrdinalIgnoreCase) && !d.Contains("|installed|")).ToArray();
        Log.Message("[Perspective Shift - Multiplayer] Ready: every player can inhabit their own colonist" + (problems.Length > 0 ? "; declarations refused: " + string.Join("; ", problems) : "."));
    }

    static HarmonyMethod H(string name, int priority = Priority.Normal) => new(AccessTools.Method(typeof(Startup), name)) { priority = priority };
    static void Patch(Harmony harmony)
    {
        Swap.Build();
        int replaced = 0;
        foreach (var m in Swap.Targets()) { harmony.Patch(m, transpiler: new HarmonyMethod(typeof(Swap), nameof(Swap.Transpiler))); replaced++; }
        harmony.Patch(Ps.SetAvatarMethod, prefix: H(nameof(SetAvatar), Priority.First));
        harmony.Patch(Ps.ClearAvatarMethod, prefix: H(nameof(ClearAvatar), Priority.First));
        harmony.Patch(Ps.RevokeControl, prefix: H(nameof(RevokeControl), Priority.First));
        harmony.Patch(Ps.CleanupPawnState, prefix: H(nameof(NotOnPlayer), Priority.First));
        harmony.Patch(Ps.StateTick, prefix: H(nameof(NotOnPlayer), Priority.First));
        harmony.Patch(Ps.AvatarTick, prefix: H(nameof(NotOnPlayer), Priority.First));
        harmony.Patch(Ps.UpdateInput, prefix: H(nameof(UpdateInput), Priority.First));
        harmony.Patch(Ps.ControlsFrozenGetter, prefix: H(nameof(ControlsFrozen), Priority.First));
        foreach (var m in new[] { Ps.IsMouseOverUI, Ps.IsMouseOverColonistBar, Ps.MouseIsOverPawn }) harmony.Patch(m, prefix: H(nameof(NotOverUI), Priority.First));
        harmony.Patch(Ps.UpdatePhysics, prefix: H(nameof(UpdatePhysics), Priority.First));
        harmony.Patch(Ps.HandleHoldToFire, prefix: H(nameof(HoldToFire), Priority.First));
        harmony.Patch(Ps.HandleSelectorClick, prefix: H(nameof(SelectorClick), Priority.First));
        harmony.Patch(Ps.HandleEat, prefix: H(nameof(EatKey), Priority.First));
        harmony.Patch(Ps.HandleRecreation, prefix: H(nameof(RecreationKey), Priority.First));
        harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)), postfix: H(nameof(AfterTick)));
        harmony.Patch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.Update)), postfix: H(nameof(AfterFrame)));
        harmony.Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)), postfix: H(nameof(Labels)));
        harmony.Patch(AccessTools.Method(typeof(PawnTweener), nameof(PawnTweener.PreDrawPosCalculation)), prefix: H(nameof(Tweener), Priority.First));
        harmony.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), prefix: H(nameof(NoWindowForPlayers), Priority.First));
        harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos)), postfix: H(nameof(OurButtons)));
        harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos)), postfix: H(nameof(ButtonsAsPlayer), Priority.Last));
        harmony.Patch(AccessTools.Method(typeof(RimWorld.Planet.World), nameof(RimWorld.Planet.World.ExposeData)), postfix: H(nameof(WorldLoaded), Priority.Last));
        harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.Dispose)), postfix: H(nameof(GameEnded)));
        // The host's own inhabited colonist's messages ("You are now ...",
        // needs, refused clicks) are the host's: they stay on its screen.
        foreach (var m in new[] { Ps.SetAvatarMethod, Ps.ClearAvatarMethod, Ps.RevokeControl, Ps.StateTick, Ps.StateUpdate, Ps.StateOnGUI, Ps.HandleSelectorClick })
            harmony.Patch(m, prefix: H(nameof(HostMessagesBegin), Priority.First + 1), finalizer: H(nameof(HostMessagesEnd)));
        // On a player's game Perspective Shift's screen code keeps this player's
        // own state (their inhabited colonist, the controls list seen).
        foreach (var m in new[] { Ps.StateUpdate, Ps.StateOnGUI, Ps.HandleSelectorClick, Ps.ClearAvatarMethod, Ps.UpdatePhysics })
            harmony.Patch(m, prefix: H(nameof(LocalBegin), Priority.First + 2), finalizer: H(nameof(LocalEnd)));
        harmony.Patch(Ps.DrawPlayerGizmos, prefix: H(nameof(PanelBegin), Priority.First), finalizer: H(nameof(PanelEnd)));
        Choices.Patch(harmony);
        Fishing.Patch(harmony);
        Storage.Patch(harmony);
        // A player's right-click menu, rebuilt and chosen on the host, with that
        // player's colonist in Perspective Shift's avatar slot.
        harmony.Patch(AccessTools.Method(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions)),
            prefix: new HarmonyMethod(typeof(Fishing), nameof(Fishing.MenuBegin)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Fishing), nameof(Fishing.ScopeEnd)));
        harmony.Patch(AccessTools.Method(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen)),
            prefix: new HarmonyMethod(typeof(Fishing), nameof(Fishing.ChosenBegin)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Fishing), nameof(Fishing.ScopeEnd)));
        // Perspective Shift locks the camera when it is moved; a move made by a
        // player's command or a player's colonist must not lock the host's.
        foreach (var name in new[] { "PerspectiveShift.CameraDriver_PanToMapLoc_Patch", "PerspectiveShift.CameraDriver_JumpToCurrentMapLoc_Patch" })
            if (AccessTools.Method(Ps.Assembly.GetType(name, false), "Postfix") is { } lockCamera)
                try { harmony.Patch(lockCamera, prefix: H(nameof(NoCameraLockForPlayers), Priority.First)); }
                catch (Exception e) { Log.Warning("[Perspective Shift - Multiplayer] Camera lock left as it is: " + e.Message); }
        int swaps = Swaps.Install(harmony);
        Log.Message("[Perspective Shift - Multiplayer] Patched " + replaced + " avatar methods (" + Swap.Replaced + " screen reads), " + swaps + " avatar checks.");
    }

    // ---- entry points ----
    static bool SetAvatar(Pawn pawn, bool showMessage) => HostSide.SetAvatarPrefix(pawn, showMessage);
    static bool ClearAvatar() => PlayerSide.Clearing || HostSide.ClearAvatarPrefix();
    static bool RevokeControl(Pawn pawn, DamageInfo? dinfo, Hediff hediff) => HostSide.RevokeControlPrefix(pawn, dinfo, hediff);
    static bool NoCameraLockForPlayers() => !HostSide.Active || Remote.Current == null && Multiplayer.CurrentPlayer == Multiplayer.LocalPlayer;
    // A player's game changes no colonist: the host does.
    static bool NotOnPlayer() => !HostSide.IsPlayerGame;
    static void AfterTick() { if (HostSide.Active) HostSide.Tick(); }
    static void AfterFrame()
    {
        if (!On || Current.ProgramState != ProgramState.Playing) return;
        Ps.CheckBalanced();
        if (HostSide.Active) HostSide.Frame();
        else using (Multiplayer.LocalScope()) PlayerSide.Frame();
    }
    static void GameEnded() { HostSide.Reset(); PlayerSide.Reset(); Choices.Reset(); Fishing.Reset(); Storage.Reset(); }
    static void WorldLoaded() { using (Multiplayer.LocalScope()) PlayerSide.WorldLoaded(); }

    internal static int InPanel;
    static void PanelBegin() => InPanel++;
    static void PanelEnd() => InPanel--;
    static void LocalBegin(out IDisposable? __state) => __state = HostSide.IsPlayerGame ? Multiplayer.LocalScope() : null;
    static void LocalEnd(IDisposable? __state) => __state?.Dispose();
    static void HostMessagesBegin(out IDisposable? __state)
    {
        __state = HostSide.Active && Remote.Current == null && !HostSide.Internal && Multiplayer.CurrentPlayer == Multiplayer.LocalPlayer
            ? Multiplayer.MessagesFor(Multiplayer.LocalPlayer) : null;
    }
    static void HostMessagesEnd(IDisposable? __state) => __state?.Dispose();

    // ---- the host running a player's colonist ----
    static bool UpdateInput(object __instance)
    {
        if (Remote.Current is not { } r || !ReferenceEquals(r.Avatar, __instance)) return true;
        var i = r.Input;
        bool fresh = i.Fresh;
        Ps.MoveInput.SetValue(__instance, fresh ? i.Move : Vector3.zero);
        Ps.Sprinting.SetValue(__instance, fresh && i.Sprint && Ps.Setting<bool>("enableSprinting"));
        Ps.Walking.SetValue(__instance, fresh && i.Walk && Ps.Setting<bool>("enableSneaking"));
        return false;
    }
    static bool ControlsFrozen(ref bool __result)
    {
        if (Remote.Current is not { } r) return true;
        var job = r.Pawn?.CurJob;
        __result = r.Input.Frozen || job != null && !job.def.playerInterruptible;
        return false;
    }
    static bool NotOverUI(ref bool __result)
    {
        if (Remote.Current == null) return true;
        __result = false;
        return false;
    }
    static bool UpdatePhysics(object __instance) => Remote.Current != null || PlayerSide.UpdatePhysicsPrefix(__instance);
    static bool HoldToFire(object __instance, bool mouseOverGizmo, bool mouseOverUI) => PlayerSide.HoldToFirePrefix(__instance, mouseOverGizmo, mouseOverUI);
    static bool SelectorClick(object __instance, ref bool __result) => Remote.Current != null || PlayerSide.SelectorClickPrefix(__instance, ref __result);
    static bool EatKey(object __instance) => Remote.Current != null || PlayerSide.KeyPrefix(__instance, 0);
    static bool RecreationKey(object __instance) => Remote.Current != null || PlayerSide.KeyPrefix(__instance, 1);

    // Another player's colonist is drawn where its player moved it.
    static readonly AccessTools.FieldRef<PawnTweener, Pawn> tweenerPawn = AccessTools.FieldRefAccess<PawnTweener, Pawn>("pawn");
    static bool Tweener(PawnTweener __instance)
    {
        if (Swaps.Count == 0) return true;
        return Swaps.Of(tweenerPawn(__instance)) is not { } avatar || Ps.PhysicsOf(avatar) == null;
    }
    // Perspective Shift's own windows for a player's colonist would open on
    // the host's screen and pause everyone (romance choice, fishing, death).
    static bool NoWindowForPlayers(Window window)
    {
        if (!HostSide.Active || !Ps.Dialogs.Contains(window.GetType())) return true;
        // The fishing reel opens on that player's screen instead.
        if (Fishing.TakeOver(window) || Fishing.ForPlayer(window)) return false;
        // Inside a player's colonist's code: the storage menu opens on that
        // player's screen (a call the call-site swap did not reach comes here).
        if (Remote.Current is { } r)
        {
            if (!Storage.TakeOver(r, window))
                RimworldMultiplayer.Api.Multiplayer.AsPlayer(r.Slot, () => Messages.Message("PSMP_NotForPlayersYet".Translate(), MessageTypeDefOf.RejectInput, false));
            return false;
        }
        return Swaps.Count == 0 || !ReferenceEquals(Ps.Current, Swaps.Of(Ps.PawnOf(Ps.Current)));
    }

    // ---- buttons ----
    // On the host, a player's press is applied to the buttons that player saw:
    // with that player's inhabited colonist (or none) in Perspective Shift's place.
    static IEnumerable<Gizmo> ButtonsAsPlayer(IEnumerable<Gizmo> __result, Pawn __instance)
    {
        if (!On || !HostSide.Active) return __result;
        int slot = Multiplayer.CurrentPlayer;
        if (slot == Multiplayer.LocalPlayer && Remote.Current == null) return __result;
        HostSide.Remotes.TryGetValue(Remote.Current?.Slot ?? slot, out var r);
        string key = Multiplayer.PlayerKey(slot);
        int authentic = key.Length > 0 && Holders.AuthenticByKey.TryGetValue(key, out int id) ? id : -1;
        return AsPlayer(__result, r?.Avatar, r != null && r.Pawn == __instance, authentic);
    }
    static IEnumerable<Gizmo> AsPlayer(IEnumerable<Gizmo> source, object? avatar, bool topRight, int authentic)
    {
        using var e = Within(avatar, topRight, authentic, source.GetEnumerator);
        while (true)
        {
            Gizmo? current = null;
            bool more = Within(avatar, topRight, authentic, () => { bool m = e.MoveNext(); if (m) current = e.Current; return m; });
            if (!more) yield break;
            yield return current!;
        }
    }
    static T Within<T>(object? avatar, bool topRight, int authentic, Func<T> run)
    {
        bool wasTop = Ps.DrawingTopRight; int wasAuthentic = Ps.AuthenticId;
        object? was = Ps.Push(avatar, "buttons"); Ps.DrawingTopRight = topRight; Ps.AuthenticId = authentic;
        try { return run(); }
        finally { Ps.Pop(was); Ps.DrawingTopRight = wasTop; Ps.AuthenticId = wasAuthentic; }
    }

    static Texture2D? release, playAs;
    static IEnumerable<Gizmo> OurButtons(IEnumerable<Gizmo> __result, Pawn __instance)
    {
        foreach (var g in __result) yield return g;
        if (!On || !HostSide.InSession) yield break;
        int slot = Multiplayer.CurrentPlayer;
        var holder = Holders.OfPawn(__instance);
        // The host releases a colonist a player plays (decision 5).
        if (HostSide.Active && slot == Multiplayer.LocalPlayer && holder != null && holder.Slot != slot)
        {
            release ??= ContentFinder<Texture2D>.Get("Gizmos/DirectorMode", false);
            yield return new Command_Action
            {
                defaultLabel = "PSMP_Release".Translate(holder.Name), defaultDesc = "PSMP_ReleaseDesc".Translate(holder.Name, __instance.LabelShort),
                icon = release, action = () => HostSide.ForceRelease(__instance),
            };
        }
        // Authentic mode: whoever has no character yet picks one. Perspective
        // Shift itself only offers that on its new-game page, so a player who
        // joins later, or a host who switched to Authentic during the game,
        // would have no way to pick.
        if (Ps.Authentic && holder == null && __instance.IsColonist && __instance.Faction == Faction.OfPlayer && !__instance.Dead)
        {
            bool hasOne, dead;
            if (HostSide.Active && slot != Multiplayer.LocalPlayer)
            {
                string key = Multiplayer.PlayerKey(slot);
                hasOne = key.Length == 0 || Holders.AuthenticByKey.ContainsKey(key) || HostSide.Remotes.ContainsKey(slot);
                dead = Holders.DeadKeys.Contains(key);
            }
            else if (HostSide.Active) { hasOne = Ps.AuthenticId >= 0 || Ps.Current != null; dead = Ps.PendingDeath; }
            else { hasOne = Holders.MyAuthentic >= 0 || Ps.Current != null; dead = Holders.MyDead; }
            if (!hasOne && !dead)
            {
                playAs ??= ContentFinder<Texture2D>.Get("Gizmos/TakeControl", false);
                var pawn = __instance;
                yield return new Command_Action
                {
                    defaultLabel = "PSMP_PlayAs".Translate(), defaultDesc = "PSMP_PlayAsDesc".Translate(pawn.LabelShort), icon = playAs,
                    action = () => Ps.Call(Ps.SetAvatarMethod, null, pawn, true),
                };
            }
        }
    }

    // ---- names over other players' colonists ----
    static void Labels()
    {
        if (!On || !HostSide.InSession || Event.current.type != EventType.Repaint || Find.CurrentMap is not { } map) return;
        if (HostSide.IsPlayerGame) Fishing.OnGUI();
        int me = Multiplayer.LocalPlayer;
        foreach (var h in Holders.All)
        {
            if (h.Slot == me && !h.Waiting) continue;
            var pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == h.PawnId);
            if (pawn == null) continue;
            var at = GenMapUI.LabelDrawPosFor(pawn, 0.95f);
            GenMapUI.DrawThingLabel(at, h.Waiting ? "PSMP_Away".Translate(h.Name).Resolve() : h.Name, h.Waiting ? Color.gray : new Color(0.55f, 0.85f, 1f));
        }
    }
}
