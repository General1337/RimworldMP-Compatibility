using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace ProgressionScenariosMultiplayer;

// Progression patches the base scenario page's drawing body. A page that
// supplies its own drawing still calls native Page.GetMainRect, but misses
// Progression's sidebar. Restore that native mod UI at that native boundary.
// No Rimworld Multiplayer types, private multiplayer state, scenario transport or permission replacement.
[StaticConstructorOnStartup]
internal static class Startup
{
    static readonly AccessTools.FieldRef<Page_SelectScenario, Scenario?> current =
        AccessTools.FieldRefAccess<Page_SelectScenario, Scenario?>("curScen");
    static readonly AccessTools.FieldRef<ScenPart_PlayerFaction, FactionDef> faction =
        AccessTools.FieldRefAccess<ScenPart_PlayerFaction, FactionDef>("factionDef");
    static FieldInfo? selected;
    static Func<Rect, Page_SelectScenario, Rect>? adjust;
    [ThreadStatic] static int nativeDrawing;

    static Startup()
    {
        if (!Multiplayer.Available || Multiplayer.HostApiVersion < 5) return;
        var sidebar = AccessTools.TypeByName("ProgressionScenarios.TechLevelSidebar");
        var filter = AccessTools.TypeByName("ProgressionScenarios.ScenarioLister_ScenariosInCategory_Patch");
        if (sidebar == null || filter == null) return;
        var draw = AccessTools.DeclaredMethod(sidebar, "AdjustMainRect", new[] { typeof(Rect), typeof(Page_SelectScenario) });
        selected = AccessTools.DeclaredField(filter, "selectedLevel");
        var rect = AccessTools.DeclaredMethod(typeof(Page), "GetMainRect", new[] { typeof(Rect), typeof(float), typeof(bool) });
        var native = AccessTools.DeclaredMethod(typeof(Page_SelectScenario), nameof(Page_SelectScenario.DoWindowContents), new[] { typeof(Rect) });
        if (draw?.ReturnType != typeof(Rect) || selected?.FieldType != typeof(TechLevel) || !selected.IsStatic || rect == null || native == null)
        { Log.Warning("[Progression Scenarios Multiplayer] Native selector layout changed; adapter was not installed."); return; }
        adjust = (Func<Rect, Page_SelectScenario, Rect>)draw.CreateDelegate(typeof(Func<Rect, Page_SelectScenario, Rect>));
        var harmony = new Harmony("rimworldmultiplayer.examples.progression-scenarios");
        harmony.Patch(native, prefix: new HarmonyMethod(typeof(Startup), nameof(EnterNative)),
            finalizer: new HarmonyMethod(typeof(Startup), nameof(LeaveNative)));
        harmony.Patch(rect, postfix: new HarmonyMethod(typeof(Startup), nameof(InheritedRect)));
        Log.Message("[Progression Scenarios Multiplayer] Native inherited-page tech selector ready.");
    }

    // Base drawing already has Progression's own transpiler. A derived page
    // calling base drawing must not receive the sidebar twice; unwind on faults.
    static void EnterNative(out int __state) { __state = nativeDrawing; nativeDrawing++; }
    static Exception? LeaveNative(Exception? __exception, int __state)
    { nativeDrawing = __state; return __exception; }

    static void InheritedRect(Page __instance, ref Rect __result)
    {
        if (nativeDrawing != 0 || __instance is not Page_SelectScenario page ||
            page.GetType() == typeof(Page_SelectScenario) || adjust == null || selected == null) return;
        using (Multiplayer.LocalScope())
        {
            // The ordinary new-colony flow selects a tech level first. An
            // inherited chooser starts at its current native scenario instead.
            if ((TechLevel)selected.GetValue(null)! == TechLevel.Undefined &&
                current(page)?.AllParts.OfType<ScenPart_PlayerFaction>().FirstOrDefault() is { } part)
                selected.SetValue(null, faction(part).techLevel);
            __result = adjust(__result, page);
        }
    }
}
