using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimworldMultiplayer.Api;
using Verse;

namespace PawnRulesMultiplayer;

// Native registry reloads replace preset records while the editor keeps its
// selection and draft. Resolve the selection only at the actual native commit;
// preserve the draft, then use the native selection callback to bind the pawn.
[StaticConstructorOnStartup]
internal static class Startup
{
    static Type? registry;
    static FieldInfo? picker, instance;
    static PropertyInfo? selected, presetType, named;
    static MethodInfo? presets, applySelection;

    static Startup()
    {
        if (!Multiplayer.Available || Multiplayer.HostApiVersion < 5) return;
        registry = AccessTools.TypeByName("PawnRules.Data.Registry");
        var rules = AccessTools.TypeByName("PawnRules.Data.Rules");
        var window = AccessTools.TypeByName("PawnRules.Interface.Dialog_Rules");
        if (registry == null || rules == null || window == null) return;
        picker = AccessTools.Field(window, "_preset");
        instance = AccessTools.Field(registry, "_instance");
        selected = picker == null ? null : AccessTools.Property(picker.FieldType, "Selected");
        presetType = AccessTools.DeclaredProperty(rules, "Type");
        named = AccessTools.Property(rules, "IsPreset");
        applySelection = AccessTools.DeclaredMethod(window, "UpdateSelected", Type.EmptyTypes);
        var commit = AccessTools.DeclaredMethod(window, "CommitTemplate", Type.EmptyTypes);
        var definitions = registry.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "GetPresets" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1).ToArray();
        if (picker == null || instance == null || selected?.PropertyType != rules || selected.SetMethod == null ||
            presetType == null || named?.PropertyType != typeof(bool) || applySelection == null || commit == null || definitions.Length != 1)
        { Log.Warning("[Pawn Rules Multiplayer] Native preset methods changed; adapter was not installed."); return; }
        presets = definitions[0].MakeGenericMethod(rules);
        new Harmony("rimworldmultiplayer.examples.pawnrules").Patch(commit,
            prefix: new HarmonyMethod(typeof(Startup), nameof(Before)),
            postfix: new HarmonyMethod(typeof(Startup), nameof(After)));
        Log.Message("[Pawn Rules Multiplayer] Native preset commit adapter ready.");
    }

    static bool Before(object __instance, out bool __state)
    {
        __state = false;
        var listing = picker!.GetValue(__instance);
        var old = selected!.GetValue(listing);
        if (old == null || named!.GetValue(old) is not true) return true;
        var roots = Find.WorldObjects.AllWorldObjects.Where(w => w.GetType() == registry).Take(2).ToArray();
        if (roots.Length != 1 || !ReferenceEquals(roots[0], instance!.GetValue(null))) return Refused();
        if (old is not ILoadReferenceable reference || presets!.Invoke(null, new[] { presetType!.GetValue(old) }) is not IEnumerable values)
            return Refused();
        string id = reference.GetUniqueLoadID();
        var matches = values.Cast<object>().Where(v => v.GetType() == old.GetType() && v is ILoadReferenceable r && r.GetUniqueLoadID() == id).Take(2).ToArray();
        if (matches.Length != 1) return Refused();
        selected.SetValue(listing, matches[0]);
        __state = true;
        return true;
    }
    static bool Refused()
    {
        Log.Warning("[Pawn Rules Multiplayer] Preset is no longer uniquely present in the current registry; commit refused.");
        return false;
    }
    static void After(object __instance, bool __state)
    {
        if (__state) applySelection!.Invoke(__instance, null);
    }
}
