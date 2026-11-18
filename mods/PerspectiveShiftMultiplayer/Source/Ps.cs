using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PerspectiveShiftMultiplayer;

// Perspective Shift, bound by name at start. Nothing here is compiled against
// it, so a changed Perspective Shift turns this add-on off with a log line
// naming what is missing, instead of breaking the game.
internal static class Ps
{
    internal static Type State = null!, Avatar = null!, Mode = null!;
    static FieldInfo avatar = null!, cameraLock = null!, pendingDeath = null!, authenticId = null!, mode = null!,
        shownControls = null!, allowDirector = null!, drawingTopRight = null!, isActiveFrame = null!, settings = null!;
    internal static FieldInfo Pawn = null!, MoveInput = null!, Sprinting = null!, Walking = null!, Physics = null!,
        Lean = null!, Aim = null!;
    internal static MethodInfo SetAvatarMethod = null!, ClearAvatarMethod = null!, RevokeControl = null!, CleanupPawnState = null!,
        StateTick = null!, StateUpdate = null!, StateOnGUI = null!, ControlsFrozenGetter = null!,
        UpdatePhysics = null!, AvatarTick = null!, RenderPawn = null!, UpdateInput = null!, RotateTowardsMouse = null!,
        HandleSelectorClick = null!, HandleHoldToFire = null!, HandleFiring = null!, HandleEat = null!, HandleRecreation = null!,
        IsMouseOverUI = null!, IsMouseOverColonistBar = null!, MouseIsOverPawn = null!, DrawPlayerGizmos = null!;
    internal static MethodInfo? UpdateVehiclePhysics;
    internal static ConstructorInfo NewAvatar = null!;
    internal static Type[] Dialogs = Array.Empty<Type>();
    internal static Assembly Assembly = null!;
    internal static readonly List<string> Missing = new();

    internal static bool Bind()
    {
        Assembly = LoadedModManager.RunningModsListForReading
            .Where(m => string.Equals(m.PackageIdPlayerFacing, "ferny.perspectiveshift", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.assemblies.loadedAssemblies).FirstOrDefault(a => a.GetType("PerspectiveShift.State", false) != null)!;
        if (Assembly == null) { Missing.Add("Perspective Shift is not loaded"); return false; }
        State = Type("PerspectiveShift.State"); Avatar = Type("PerspectiveShift.Avatar"); Mode = Type("PerspectiveShift.PlaystyleMode");
        if (State == null || Avatar == null) return false;
        avatar = Field(State, "Avatar"); cameraLock = Field(State, "CameraLockPosition"); pendingDeath = Field(State, "pendingDeathMenu");
        authenticId = Field(State, "authenticPawnId"); mode = Field(State, "CurrentMode"); shownControls = Field(State, "shownControlsDialog");
        allowDirector = Field(State, "allowDirectorInAuthentic"); drawingTopRight = Field(State, "DrawingTopRightGizmos");
        isActiveFrame = Field(State, "_isActiveCacheFrame");
        settings = Field(Type("PerspectiveShift.PerspectiveShiftMod"), "settings");
        Pawn = Field(Avatar, "pawn"); MoveInput = Field(Avatar, "moveInput"); Sprinting = Field(Avatar, "isSprinting");
        Walking = Field(Avatar, "isWalking"); Physics = Field(Avatar, "physicsPosition"); Lean = Field(Avatar, "LeanTarget"); Aim = Field(Avatar, "aimAngle");
        SetAvatarMethod = Method(State, "SetAvatar"); ClearAvatarMethod = Method(State, "ClearAvatar"); RevokeControl = Method(State, "RevokeControl");
        CleanupPawnState = Method(State, "CleanupPawnState"); StateTick = Method(State, "Tick"); StateUpdate = Method(State, "Update"); StateOnGUI = Method(State, "OnGUI");
        ControlsFrozenGetter = AccessTools.PropertyGetter(State, "ControlsFrozen"); if (ControlsFrozenGetter == null) Missing.Add("State.ControlsFrozen");
        UpdatePhysics = Method(Avatar, "UpdatePhysics"); AvatarTick = Method(Avatar, "Tick"); RenderPawn = Method(Avatar, "RenderPawn");
        UpdateInput = Method(Avatar, "UpdateInput"); RotateTowardsMouse = Method(Avatar, "RotateTowardsMouse");
        HandleSelectorClick = Method(Avatar, "HandleSelectorClick"); HandleHoldToFire = Method(Avatar, "HandleHoldToFire");
        HandleFiring = Method(Avatar, "HandleFiring"); HandleEat = Method(Avatar, "HandleEatFoodBinding"); HandleRecreation = Method(Avatar, "HandleRecreationBinding");
        IsMouseOverUI = Method(Avatar, "IsMouseOverUI"); IsMouseOverColonistBar = Method(Avatar, "IsMouseOverColonistBar"); MouseIsOverPawn = Method(Avatar, "MouseIsOverPawn"); DrawPlayerGizmos = Method(Avatar, "DrawPlayerGizmos");
        NewAvatar = AccessTools.Constructor(Avatar, new[] { typeof(Verse.Pawn) }); if (NewAvatar == null) Missing.Add("Avatar(Pawn)");
        // Optional: Vehicle Framework support in Perspective Shift.
        inVehicle = AccessTools.Method(Assembly.GetType("PerspectiveShift.ModCompatibility", false), "IsPawnInVehicle");
        UpdateVehiclePhysics = AccessTools.Method(Avatar, "UpdateVehiclePhysics", new[] { typeof(Verse.Pawn), typeof(bool) });
        damageTime = AccessTools.Field(State, "lastDamageTime") is { FieldType: var ft } f && ft == typeof(float) ? f : null;
        Dialogs = new[] { "Dialog_InteractionChoice", "Dialog_FishingMinigame", "Dialog_StorageMenu", "Dialog_YouDied", "Dialog_InhabitedControls" }
            .Select(n => Assembly.GetType("PerspectiveShift." + n, false)).OfType<Type>().ToArray();
        return Missing.Count == 0;
    }
    static Type Type(string name) { var t = Assembly.GetType(name, false); if (t == null) Missing.Add(name); return t!; }
    static FieldInfo Field(Type? t, string name) { var f = t == null ? null : AccessTools.Field(t, name); if (f == null) Missing.Add((t?.Name ?? "?") + "." + name); return f!; }
    static MethodInfo Method(Type t, string name) { var m = AccessTools.Method(t, name); if (m == null) Missing.Add(t.Name + "." + name + "()"); return m!; }

    // ---- State ----
    internal static object? Current { get => avatar.GetValue(null); set { avatar.SetValue(null, value); ResetActive(); } }

    // Every temporary swap of Perspective Shift's one avatar goes through
    // Push/Pop. When the outermost swap ends, this game's own avatar comes
    // back exactly as it was, however the inner ones nested or failed.
    static int depth; static object? owner; static string openedBy = "";
    // Perspective Shift's red damage flash is this screen's: a swapped-in
    // colonist being hit must not set it (it is put back when the swaps end).
    static FieldInfo? damageTime; static float savedDamageTime;
    internal static int Depth => depth;
    internal static object? Push(object? swapped, string site = "")
    {
        var previous = Current;
        if (depth++ == 0) { owner = previous; openedBy = site; if (damageTime != null) savedDamageTime = (float)damageTime.GetValue(null)!; }
        Current = swapped;
        return previous;
    }
    internal static void Pop(object? previous)
    {
        if (depth <= 0) { Leak("a swap ended twice"); depth = 0; Current = owner; return; }
        Current = --depth == 0 ? owner : previous;
        if (depth == 0) damageTime?.SetValue(null, savedDamageTime);
    }
    // This game's own inhabited colonist changes (the host's table): inside a
    // swap it is what the outermost swap puts back.
    internal static void SetOwn(object? value)
    {
        if (depth > 0) owner = value;
        else Current = value;
    }
    internal static object? Own => depth > 0 ? owner : Current;
    // Once a frame: no swap may still be open between frames.
    internal static void CheckBalanced()
    {
        if (depth == 0) return;
        Leak(depth + " swap(s) left open, the first by " + openedBy);
        depth = 0; Current = owner;
    }
    static bool warned;
    static void Leak(string what)
    {
        if (warned) return; warned = true;
        Log.Warning("[Perspective Shift - Multiplayer] Avatar swap " + what + "; this game's own inhabited colonist was put back. " + Environment.StackTrace);
    }
    internal static Vector3? CameraLock { get => (Vector3?)cameraLock.GetValue(null); set => cameraLock.SetValue(null, value); }
    internal static bool PendingDeath { get => (bool)pendingDeath.GetValue(null)!; set => pendingDeath.SetValue(null, value); }
    internal static int AuthenticId { get => (int)authenticId.GetValue(null)!; set => authenticId.SetValue(null, value); }
    internal static int ModeValue { get => Convert.ToInt32(mode.GetValue(null)); set => mode.SetValue(null, Enum.ToObject(Mode, value)); }
    internal static bool Authentic => mode.GetValue(null)?.ToString() == "Authentic";
    internal static bool ShownControls { get => (bool)shownControls.GetValue(null)!; set => shownControls.SetValue(null, value); }
    internal static bool AllowDirector { get => (bool)allowDirector.GetValue(null)!; set => allowDirector.SetValue(null, value); }
    internal static bool DrawingTopRight { get => (bool)drawingTopRight.GetValue(null)!; set => drawingTopRight.SetValue(null, value); }
    // State.IsActive caches its answer for a frame; a swap of the avatar must not reuse it.
    internal static void ResetActive() => isActiveFrame.SetValue(null, -999);
    internal static object Settings => settings.GetValue(null)!;
    internal static T Setting<T>(string name) => AccessTools.Field(Settings.GetType(), name)?.GetValue(Settings) is T v ? v : default!;

    // In a vehicle (Vehicle Framework): Perspective Shift's own answer, with the vehicle and whether this pawn drives or shoots.
    static MethodInfo? inVehicle;
    internal static bool InVehicle(Pawn? pawn, out Pawn? vehicle, out bool driver, out bool gunner)
    {
        vehicle = null; driver = gunner = false;
        if (pawn == null || pawn.Spawned || inVehicle == null) return false;
        var args = new object?[] { pawn, null, false, false };
        try
        {
            if (!(bool)inVehicle.Invoke(null, args)!) return false;
            vehicle = args[1] as Pawn; driver = (bool)args[2]!; gunner = (bool)args[3]!;
            return vehicle is { Spawned: true };
        }
        catch (TargetInvocationException) { return false; } // Its vehicle lookup failing means: not in a vehicle we can drive.
    }
    internal static bool InVehicle(Pawn? pawn) => InVehicle(pawn, out _, out _, out _);

    // ---- Avatar ----
    internal static Pawn? PawnOf(object? a) => a == null ? null : (Pawn?)Pawn.GetValue(a);
    internal static object Make(Pawn pawn) => NewAvatar.Invoke(new object[] { pawn });
    internal static Vector3? PhysicsOf(object a) => (Vector3?)Physics.GetValue(a);
    internal static void Call(MethodInfo m, object? target, params object?[] args)
    {
        try { m.Invoke(target, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }
    internal static T Call<T>(MethodInfo m, object? target, params object?[] args)
    {
        try { return (T)m.Invoke(target, args)!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }
}
