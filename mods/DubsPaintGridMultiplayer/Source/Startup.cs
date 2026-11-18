using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimworldMultiplayer.Api;
using Verse;

namespace DubsPaintGridMultiplayer;

// Exact frozen native implementation only. During debug captures every native
// reader index is in bounds and GetDepth returns this same array element. Keep
// the original quantizer, compression and text encoding. Reuse encoded text
// only after every raw byte still matches. Identical layers share one weakly
// owned snapshot. Every capture compares all cells; ordinary saves and loads
// retain the original method. No budgets or warnings change.
[StaticConstructorOnStartup]
internal static class Startup
{
    const string Owner = "rimworldmultiplayer.dubspaintgrid";
    static readonly Guid PaintModule = new("49d2da17-8c7a-4f46-850c-99aef8229aa7");
    static readonly Guid GameModule = new("71185080-36f8-4a50-863e-e15aba81b822");
    static FieldInfo? values, mapField;
    static Func<float, ushort>? quantize;
    static MethodInfo[] methods = Array.Empty<MethodInfo>();
    sealed record Cached(byte[] Raw, string Text, string Tag);
    sealed class Entry { internal Cached? Last; }
    static readonly ConditionalWeakTable<object, Entry> entries = new();
    static System.WeakReference<Cached>? recent;
    const int MaxCells = 262144;
    // Developer witness alternates the original and shortcut in one
    // paused game. The player interface exposes no new switch or control.
    internal static bool Enabled = true;
    internal static long Captures, Refused, Hits, Misses;

    static Startup()
    {
        var type = AccessTools.TypeByName("DubRoss.GridLayer");
        if (type?.Module.ModuleVersionId != PaintModule || typeof(Map).Module.ModuleVersionId != GameModule)
        {
            Log.Warning("[Dubs Paint Grid Multiplayer] Native edition differs; original grid serializer retained.");
            return;
        }
        values = AccessTools.Field(type, "<DepthGridDirect_Unsafe>k__BackingField");
        mapField = AccessTools.Field(type, "map");
        var convert = AccessTools.DeclaredMethod(type, "SnowFloatToShort", new[] { typeof(float) });
        var expose = AccessTools.DeclaredMethod(type, "ExposeData", Type.EmptyTypes);
        var get = AccessTools.DeclaredMethod(type, "GetDepth", new[] { typeof(IntVec3) });
        if (values?.FieldType != typeof(float[]) || mapField?.FieldType != typeof(Map) ||
            convert?.ReturnType != typeof(ushort) || !convert.IsStatic || expose == null || get == null)
            throw new InvalidOperationException("Frozen paint grid shape differs");
        quantize = convert.CreateDelegate<Func<float, ushort>>();
        methods = new[] { expose, get, convert,
            AccessTools.Method(typeof(MapSerializeUtility), nameof(MapSerializeUtility.SerializeUshort)),
            AccessTools.Method(typeof(MapExposeUtility), nameof(MapExposeUtility.ExposeUshort)),
            AccessTools.Method(typeof(DataSerializeUtility), nameof(DataSerializeUtility.SerializeUshort), new[] { typeof(int), typeof(Func<int, ushort>) }),
            AccessTools.Method(typeof(DataExposeUtility), nameof(DataExposeUtility.LookByteArray)),
            AccessTools.Method(typeof(CompressUtility), nameof(CompressUtility.Compress), new[] { typeof(byte[]) }),
            AccessTools.Method(typeof(DataExposeUtility), nameof(DataExposeUtility.AddLineBreaksToLongString)),
            AccessTools.Method(typeof(GenGrid), nameof(GenGrid.InBounds), new[] { typeof(IntVec3), typeof(Map) }),
            AccessTools.Method(typeof(CellIndices), nameof(CellIndices.IndexToCell)),
            AccessTools.Method(typeof(CellIndices), nameof(CellIndices.CellToIndex), new[] { typeof(IntVec3) }) };
        if (methods.Any(m => m == null)) throw new InvalidOperationException("Native map grid methods absent");
        new Harmony(Owner).Patch(expose, prefix: new HarmonyMethod(typeof(Startup), nameof(BeforeExpose)));
    }

    static bool OriginalMethods()
    {
        foreach (var method in methods)
        {
            var patches = Harmony.GetPatchInfo(method);
            if (patches == null) continue;
            if (patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers).Concat(patches.Finalizers)
                .Any(p => p.owner != Owner)) return false;
        }
        return true;
    }

    static bool BeforeExpose(object __instance)
    {
        if (!Enabled || !Multiplayer.InSession || Scribe.mode != LoadSaveMode.Saving ||
            Scribe.saver?.savingForDebug != true) return true;
        if (!OriginalMethods() || mapField!.GetValue(__instance) is not Map map ||
            values!.GetValue(__instance) is not float[] rows || rows.Length != map.info.NumCells ||
            rows.Length != map.cellIndices.NumGridCells || rows.Length > MaxCells)
        {
            Refused++;
            return true;
        }
        var raw = MemoryMarshal.AsBytes(rows.AsSpan());
        static bool Same(Cached? c, ReadOnlySpan<byte> bytes) => c != null && bytes.SequenceEqual(c.Raw);
        var entry = entries.GetOrCreateValue(__instance);
        var cached = entry.Last;
        if (!Same(cached, raw) && recent != null && recent.TryGetTarget(out var shared) && Same(shared, raw))
            cached = shared;
        if (Same(cached, raw)) Hits++;
        else
        {
            var before = raw.ToArray();
            byte[] bytes = DataSerializeUtility.SerializeUshort(rows.Length, i => quantize!(rows[i]));
            byte[] compressed = CompressUtility.Compress(bytes);
            bool smaller = compressed.Length < bytes.Length;
            string text = Convert.ToBase64String(smaller ? compressed : bytes).AddLineBreaksToLongString();
            // A changing serializer must never populate this cache or publish
            // a remembered result for an array it did not actually observe.
            if (!raw.SequenceEqual(before)) { Refused++; return true; }
            cached = new(before, text, smaller ? "depthGridDeflate" : "depthGrid");
            recent = new(cached);
            Misses++;
        }
        entry.Last = cached;
        string? encoded = cached!.Text;
        Scribe_Values.Look(ref encoded, cached.Tag);
        Captures++;
        return false;
    }
}
