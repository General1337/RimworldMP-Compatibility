using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RavenCacheMultiplayer;

// Keep the native dictionary, values, callbacks and exceptions. Serialize only
// its accesses and enumerations. Resource destruction remains outside the gate:
// ReleaseRuntimeResources defers/relinquishes graphics resources on the main
// thread before its dictionary removal, so locking that whole method is unsafe.
[StaticConstructorOnStartup]
internal static class Startup
{
    const string Owner = "rimworldmultiplayer.ravencache";
    static readonly object gate = new();
    static MethodInfo read = null!, write = null!, remove = null!;
    static MethodInfo readLocked = null!, writeLocked = null!, removeLocked = null!;
    static readonly Dictionary<MethodBase, int> expectedCalls = new();
    internal static bool Enabled = true, Installed;

    static Startup()
    {
        var type = AccessTools.TypeByName("RavenRace.Features.SlimeOverlay.MapComponent_SlimeOverlayRenderer");
        if (type?.Module.ModuleVersionId != new Guid("dbaf5b19-2652-4f07-824a-714d7d31e963") ||
            typeof(Map).Module.ModuleVersionId != new Guid("71185080-36f8-4a50-863e-e15aba81b822"))
        {
            Log.Warning("[Raven Cache Multiplayer] Native edition differs; no cache hooks installed.");
            return;
        }
        var cache = AccessTools.Field(type, "ComponentCache");
        var dictionary = typeof(Dictionary<,>).MakeGenericType(typeof(Map), type);
        if (cache?.FieldType != dictionary || !cache.IsStatic || !cache.IsInitOnly)
            throw new InvalidOperationException("Frozen native Raven cache shape differs");
        read = dictionary.GetMethod("TryGetValue", new[] { typeof(Map), type.MakeByRefType() })!;
        write = dictionary.GetMethod("set_Item", new[] { typeof(Map), type })!;
        remove = dictionary.GetMethod("Remove", new[] { typeof(Map) })!;
        readLocked = AccessTools.DeclaredMethod(typeof(Startup), nameof(Read)).MakeGenericMethod(type);
        writeLocked = AccessTools.DeclaredMethod(typeof(Startup), nameof(Write)).MakeGenericMethod(type);
        removeLocked = AccessTools.DeclaredMethod(typeof(Startup), nameof(Remove)).MakeGenericMethod(type);
        var ctor = AccessTools.Constructor(type, new[] { typeof(Map) });
        var get = AccessTools.DeclaredMethod(type, "GetCached", new[] { typeof(Map) });
        var release = AccessTools.DeclaredMethod(type, "ReleaseRuntimeResources", Type.EmptyTypes);
        var any = AccessTools.DeclaredProperty(type, "HasAnyOverlays")?.GetGetMethod(true);
        var rebuild = AccessTools.DeclaredMethod(type, "RebuildAllPrintedCaptures", Type.EmptyTypes);
        MethodBase[] targets = { ctor!, get!, release!, any!, rebuild! };
        if (targets.Any(m => m == null)) throw new InvalidOperationException("Native Raven cache accessors absent");
        var all = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        var accessors = all.Where(m => PatchProcessor.GetOriginalInstructions(m)
            .Any(i => i.operand is FieldInfo f && f == cache)).ToArray();
        if (accessors.Length != targets.Length || accessors.Any(m => !targets.Contains(m)))
            throw new InvalidOperationException("Native Raven cache accessor inventory differs");
        expectedCalls[ctor!] = 1; expectedCalls[get!] = 2; expectedCalls[release!] = 1;
        foreach (var (method, count) in expectedCalls)
            if (PatchProcessor.GetOriginalInstructions(method).Count(i => IsAccess(i.operand)) != count)
                throw new InvalidOperationException("Native Raven cache operation inventory differs: " + method.Name);
        var harmony = new Harmony(Owner);
        try
        {
            foreach (var method in expectedCalls.Keys)
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Startup), nameof(Rewrite)));
            foreach (var method in new[] { any, rebuild })
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(Startup), nameof(Enter)),
                    finalizer: new HarmonyMethod(typeof(Startup), nameof(Exit)));
            Installed = true;
        }
        catch { harmony.UnpatchAll(harmony.Id); throw; }
    }

    static bool IsAccess(object? operand) => Equals(operand, read) || Equals(operand, write) || Equals(operand, remove);
    static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
    {
        var code = source.ToArray(); int changed = 0;
        foreach (var i in code)
        {
            if (!IsAccess(i.operand)) continue;
            i.operand = Equals(i.operand, read) ? readLocked : Equals(i.operand, write) ? writeLocked : removeLocked;
            i.opcode = OpCodes.Call; changed++;
        }
        if (changed != expectedCalls[__originalMethod]) throw new InvalidOperationException("Composed Raven cache calls differ");
        return code;
    }
    static bool Read<T>(Dictionary<Map, T> cache, Map map, out T value) where T : class
    { if (!Enabled) return cache.TryGetValue(map, out value!); lock (gate) return cache.TryGetValue(map, out value!); }
    static void Write<T>(Dictionary<Map, T> cache, Map map, T value) where T : class
    { if (!Enabled) { cache[map] = value; return; } lock (gate) cache[map] = value; }
    static bool Remove<T>(Dictionary<Map, T> cache, Map map) where T : class
    { if (!Enabled) return cache.Remove(map); lock (gate) return cache.Remove(map); }
    static void Enter(ref bool __state)
    { if (Enabled) Monitor.Enter(gate, ref __state); }
    static Exception? Exit(Exception? __exception, bool __state)
    { if (__state) Monitor.Exit(gate); return __exception; }
}
