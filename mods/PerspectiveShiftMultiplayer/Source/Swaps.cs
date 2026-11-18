using System.Reflection;
using HarmonyLib;
using Verse;

namespace PerspectiveShiftMultiplayer;

// Perspective Shift asks "is this pawn the avatar?" against its one avatar.
// For every game method Perspective Shift patches whose subject pawn is known
// (the pawn itself, its tracker's pawn, or a "pawn" argument), this add-on
// puts that pawn's player's avatar in place for the call, so another player's
// colonist gets the same treatment as the host's (no wandering off to work,
// its needs, aim and lean). Found at start from Perspective Shift's own
// patches, so a new patch of theirs is covered without a change here.
internal static class Swaps
{
    // The avatars that are not this game's own (host: players'; player: others').
    static readonly Dictionary<Pawn, object> others = new();
    internal static int Count => others.Count;
    internal static object? Of(Pawn? pawn) => pawn != null && others.Count > 0 && others.TryGetValue(pawn, out var a) ? a : null;

    internal static void Rebuild()
    {
        others.Clear();
        if (HostSide.Active) foreach (var r in HostSide.Remotes.Values) { if (r.Pawn != null) others[r.Pawn] = r.Avatar; }
        else foreach (var (pawn, avatar) in PlayerSide.Others) others[pawn] = avatar;
    }

    internal struct State { internal bool On; internal object? Previous; }
    static void Enter(Pawn? pawn, ref State state)
    {
        // Main thread only: Perspective Shift's avatar slot is one static.
        // RimWorld runs some pawn code on worker threads, and a swap there
        // showed through to the main thread (PsMp10: a player's game briefly
        // had the host's colonist as its own).
        if (others.Count == 0 || pawn == null || !UnityData.IsInMainThread || !others.TryGetValue(pawn, out var avatar)) return;
        var current = Ps.Current;
        if (ReferenceEquals(current, avatar)) return;
        state.On = true; state.Previous = Ps.Push(avatar, "check");
    }
    static void Exit(State state) { if (state.On) Ps.Pop(state.Previous); }

    static class OnPawn
    {
        [HarmonyPriority(Priority.First)] internal static void Prefix(Pawn __instance, out State __state) { __state = default; Enter(__instance, ref __state); }
        internal static void Finalizer(State __state) => Exit(__state);
    }
    static class OnArgument
    {
        [HarmonyPriority(Priority.First)] internal static void Prefix(Pawn pawn, out State __state) { __state = default; Enter(pawn, ref __state); }
        internal static void Finalizer(State __state) => Exit(__state);
    }
    static readonly Dictionary<Type, AccessTools.FieldRef<object, Pawn>> fields = new();
    static class OnTracker
    {
        [HarmonyPriority(Priority.First)]
        internal static void Prefix(object __instance, out State __state)
        {
            __state = default;
            if (others.Count == 0 || __instance == null || !fields.TryGetValue(__instance.GetType(), out var field)) return;
            Enter(field(__instance), ref __state);
        }
        internal static void Finalizer(State __state) => Exit(__state);
    }

    // Methods where the swap would be wrong: the button list (its own wrapper),
    // drawing a need on screen, the per-tick and per-frame entry points.
    static readonly HashSet<string> skipped = new() { "GetGizmos", "DrawOnGUI", "DoSingleTick", "Update", "UIRootOnGUI", "ExposeData", "LoadGame" };

    internal static int Install(Harmony harmony)
    {
        int n = 0;
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null || original.DeclaringType == null || skipped.Contains(original.Name)) continue;
            bool theirs = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Any(p => p.PatchMethod.DeclaringType?.Assembly == Ps.Assembly);
            if (!theirs || original.DeclaringType.Assembly == Ps.Assembly) continue;
            Type? kind = null;
            if (!original.IsStatic && typeof(Pawn).IsAssignableFrom(original.DeclaringType)) kind = typeof(OnPawn);
            else if (original.GetParameters().Any(p => p.Name == "pawn" && typeof(Pawn).IsAssignableFrom(p.ParameterType))) kind = typeof(OnArgument);
            else if (!original.IsStatic && AccessTools.Field(original.DeclaringType, "pawn") is { } f && typeof(Pawn).IsAssignableFrom(f.FieldType) && !original.DeclaringType.IsValueType)
            {
                if (!fields.ContainsKey(original.DeclaringType)) fields[original.DeclaringType] = AccessTools.FieldRefAccess<object, Pawn>(f);
                kind = typeof(OnTracker);
            }
            if (kind == null || original.GetMethodBody() == null || original.IsAbstract || original.ReflectedType != original.DeclaringType) continue;
            try
            {
                harmony.Patch(original, prefix: new HarmonyMethod(AccessTools.Method(kind, "Prefix")) { priority = Priority.First },
                    finalizer: new HarmonyMethod(AccessTools.Method(kind, "Finalizer")));
                n++;
            }
            catch (Exception e) { Log.Warning("[Perspective Shift - Multiplayer] No avatar swap on " + original.DeclaringType.Name + "." + original.Name + ": " + e.Message); }
        }
        return n;
    }
}
