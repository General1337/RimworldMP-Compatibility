using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PerspectiveShiftMultiplayer;

// One player's latest input on the host (the input channel), checked.
internal sealed class PlayerInput
{
    internal Vector3 Move;
    internal bool Sprint, Walk, Frozen, Fire;
    internal Vector3 Aim;
    internal double At = -1;
    internal bool Fresh => Time.realtimeSinceStartup - At < 0.5;
}

// The host running Perspective Shift's code for a player's colonist. While a
// Remote is current, the mouse, keys, clicks, map, windows and sounds that
// code reads are that player's (Swap), and Perspective Shift's one avatar is
// that player's. Everything else in the game is unchanged.
internal sealed class Remote
{
    internal int Slot;
    internal object Avatar = null!;
    internal Pawn Pawn => Ps.PawnOf(Avatar)!;
    internal PlayerInput Input = new();
    // A click or key being replayed.
    internal Event? Event;
    internal IntVec3 Cell = IntVec3.Invalid;
    internal Vector3 MapPos;
    internal bool Shift;
    internal KeyBindingDef? KeyDown;

    internal static Remote? Current;

    // Enter: Perspective Shift sees this player's avatar; restored on exit,
    // also when the code throws.
    internal Scope Enter() => new(this);
    internal readonly struct Scope : IDisposable
    {
        readonly Remote? previous; readonly object? avatar; readonly Vector3? cameraLock; readonly bool topRight;
        internal Scope(Remote remote)
        {
            previous = Current; cameraLock = Ps.CameraLock; topRight = Ps.DrawingTopRight;
            Current = remote; avatar = Ps.Push(remote.Avatar, "remote"); Ps.CameraLock = null;
        }
        public void Dispose()
        {
            Current = previous; Ps.Pop(avatar); Ps.CameraLock = cameraLock; Ps.DrawingTopRight = topRight;
        }
    }

    internal void UseClick(int button, int clicks, IntVec3 cell, Vector3 at, bool shift)
    {
        Event = new Event { type = EventType.MouseDown, button = button, clickCount = Math.Max(1, clicks), shift = shift };
        Cell = cell; MapPos = at; Shift = shift;
    }
    internal void UseAim()
    {
        Event = null; KeyDown = null; Shift = false;
        MapPos = Input.Aim; Cell = Input.Aim.ToIntVec3();
    }
}

// Call-site replacements in Perspective Shift's avatar code (all methods of
// its Avatar class, its compiler-made helpers included). Outside a Remote
// they call exactly what the code called before.
internal static class Swap
{
    static Remote? R => Remote.Current;
    public static IntVec3 MouseCell() => R is { } r ? r.Cell : UI.MouseCell();
    public static Vector3 MouseMapPosition() => R is { } r ? r.MapPos : UI.MouseMapPosition();
    public static bool GetKey(KeyCode key) => R is { } r ? key is KeyCode.LeftShift or KeyCode.RightShift && r.Shift : UnityEngine.Input.GetKey(key);
    public static bool GetMouseButton(int button) => R is { } r ? button == 0 && r.Input.Fire && r.Input.Fresh : UnityEngine.Input.GetMouseButton(button);
    public static Map CurrentMap() => R is { } r ? r.Pawn.MapHeld : Find.CurrentMap;
    public static bool WorldSelected() => R == null && WorldRendererUtility.WorldSelected;
    public static bool IsTargeting(Targeter targeter) => R == null && targeter.IsTargeting;
    public static bool KeyDownEvent(KeyBindingDef def) => R is { } r ? r.KeyDown == def : def.KeyDownEvent;
    public static bool IsDown(KeyBindingDef def) => R == null && def.IsDown;
    public static Event CurrentEvent() => R is { Event: { } e } ? e : Event.current;
    static readonly List<object> none = new();
    public static List<object> SelectedObjects(Selector selector) { if (R == null) return selector.SelectedObjects; none.Clear(); return none; }
    public static bool IsSelected(Selector selector, object thing) => R == null && selector.IsSelected(thing);
    public static void Deselect(Selector selector, object thing) { if (R == null) selector.Deselect(thing); }
    // A window from a player's colonist would open on the host's screen and
    // pause everyone. The storage menu opens on that player's screen instead
    // (Storage); other windows (confirmations) are not for players yet.
    public static void AddWindow(WindowStack stack, Window window)
    {
        if (R is not { } r) { stack.Add(window); return; }
       if (Storage.TakeOver(r, window)) return;
        RimworldMultiplayer.Api.Multiplayer.AsPlayer(r.Slot, () => Messages.Message("PSMP_NotForPlayersYet".Translate(), MessageTypeDefOf.RejectInput, false));
    }
    public static void PlayOneShotOnCamera(SoundDef def, Map map) { if (R == null) def.PlayOneShotOnCamera(map); }

    static readonly Dictionary<MethodBase, MethodInfo> map = new();
    static void Add(MethodBase? from, string to) { if (from != null) map[from] = AccessTools.Method(typeof(Swap), to); }
    internal static void Build()
    {
        Add(AccessTools.Method(typeof(UI), nameof(UI.MouseCell)), nameof(MouseCell));
        Add(AccessTools.Method(typeof(UI), nameof(UI.MouseMapPosition)), nameof(MouseMapPosition));
        Add(AccessTools.Method(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetKey), new[] { typeof(KeyCode) }), nameof(GetKey));
        Add(AccessTools.Method(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetMouseButton), new[] { typeof(int) }), nameof(GetMouseButton));
        Add(AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap)), nameof(CurrentMap));
        Add(AccessTools.PropertyGetter(typeof(WorldRendererUtility), nameof(WorldRendererUtility.WorldSelected)), nameof(WorldSelected));
        Add(AccessTools.PropertyGetter(typeof(Targeter), nameof(Targeter.IsTargeting)), nameof(IsTargeting));
        Add(AccessTools.PropertyGetter(typeof(KeyBindingDef), nameof(KeyBindingDef.KeyDownEvent)), nameof(KeyDownEvent));
        Add(AccessTools.PropertyGetter(typeof(KeyBindingDef), nameof(KeyBindingDef.IsDown)), nameof(IsDown));
        Add(AccessTools.PropertyGetter(typeof(Event), nameof(Event.current)), nameof(CurrentEvent));
        Add(AccessTools.PropertyGetter(typeof(Selector), nameof(Selector.SelectedObjects)), nameof(SelectedObjects));
        Add(AccessTools.Method(typeof(Selector), nameof(Selector.IsSelected)), nameof(IsSelected));
        Add(AccessTools.Method(typeof(Selector), nameof(Selector.Deselect)), nameof(Deselect));
        Add(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), nameof(AddWindow));
        Add(AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShotOnCamera)), nameof(PlayOneShotOnCamera));
    }
    internal static int Replaced;
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
    {
        foreach (var i in code)
        {
            if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodBase m && map.TryGetValue(m, out var to)
                && (m.IsStatic || to.GetParameters().Length == m.GetParameters().Length + 1))
            {
                // Instance calls become static calls taking the instance first;
                if (m is MethodInfo mi && mi.ReturnType != to.ReturnType && !mi.ReturnType.IsAssignableFrom(to.ReturnType)) { yield return i; continue; }
                i.opcode = OpCodes.Call; i.operand = to; Replaced++;
            }
            yield return i;
        }
    }
    internal static IEnumerable<MethodBase> Targets()
    {
        var types = new List<Type> { Ps.Avatar };
        for (int n = 0; n < types.Count; n++) types.AddRange(types[n].GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null) yield return m;
    }
}
