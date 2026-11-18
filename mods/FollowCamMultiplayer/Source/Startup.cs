using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace FollowCamMultiplayer;

// Complements the FollowCam script pack. Only settings and messages belonging
// to this screen are changed here; pawn simulation remains on the host.
[StaticConstructorOnStartup]
internal static class Startup
{
    static readonly string[] Personal = {
        "UseResponsiveFollow", "UseTimeBasedInterval", "UseDeadzoneFollow", "TimeBasedIntervalSeconds", "DeadzoneRange",
        "InterruptOnKeyboardMove", "InterruptOnMMB", "InterruptOnZoom", "InterruptOnEdgeScroll", "InterruptOnCameraJump",
        "AutoFollowOnDamage", "AutoFollowOnDowned", "ThreatFollowDuration", "ShowFollowPawnGizmo", "PersistentFollowStatus",
        "ShowFollowMessages", "RMCFollow", "ExtendedZoom", "ExtendedZoomMin", "ExtendedZoomMax", "CycleColonists",
        "CycleHostileHumans", "CycleNeutralHumans", "CycleTamedAnimals", "CycleWildAnimals", "CycleHostileMechs",
        "CycleColonyMechs", "AutoCycleColonists", "RememberAutoCycle", "AutoCycleIntervalSeconds", "ClearSelectAftCycle",
        "ExpandCameraModes", "ShowAdvancedInterruptSettings", "ExpandThreatAutoFollow", "ExpandMisc", "ExpandCycle", "ExpandZoom"
    };
    static readonly HashSet<MethodInfo> localButtons = new();
    static Assembly? followAssembly;
    static FieldInfo? instance;
    static MethodInfo? manualCycle;

    static Startup()
    {
        if (!Multiplayer.Available) return;
        if (Multiplayer.HostApiVersion < 6) { Log.Warning("[Follow Cam - Multiplayer] Rimworld Multiplayer lacks what this add-on needs."); return; }
        var settings = AccessTools.TypeByName("FollowCam.FollowCamSettings");
        var comp = AccessTools.TypeByName("FollowCam.FollowCamComp");
        var show = AccessTools.Method(comp, "ShowFollowMessage", new[] { typeof(Pawn) });
        var threat = AccessTools.Method(comp, "StartTemporaryFollow", new[] { typeof(Pawn) });
        var buttons = AccessTools.Method(AccessTools.TypeByName("FollowCam.PawnGetGizmosPatch"), "Postfix");
        var menus = AccessTools.Method(AccessTools.TypeByName("FollowCam.FloatMenuMakerMap_GetOptions_Patch"), "Postfix");
        instance = AccessTools.Field(comp, "Instance");
        manualCycle = AccessTools.Method(comp, "HandleManualCycle", Type.EmptyTypes);
        if (settings == null || show == null || threat == null || buttons?.ReturnType != typeof(IEnumerable<Gizmo>) ||
            menus?.ReturnType != typeof(void) || menus.GetParameters().Length != 3 ||
            menus.GetParameters()[2].ParameterType != typeof(List<FloatMenuOption>).MakeByRefType() ||
            instance == null || manualCycle?.ReturnType != typeof(void) ||
            Personal.Any(name => settings.GetField(name) is not { IsStatic: false } field ||
                                 field.FieldType != typeof(bool) && field.FieldType != typeof(float)))
        { Log.Warning("[Follow Cam - Multiplayer] Follow Cam changed; settings/messages adapter was not installed."); return; }
        // Register the helper too: the real gizmo calls it after StartFollowing.
        var local = Multiplayer.RegisterLocalMethod(show);
        var personal = Multiplayer.RegisterPersonalSettings(settings, Personal);
        var localComponent = Multiplayer.RegisterLocalComponent(comp);
        if (local?.StartsWith("installed|", StringComparison.Ordinal) != true ||
            personal?.StartsWith("installed|", StringComparison.Ordinal) != true ||
            localComponent?.StartsWith("installed|", StringComparison.Ordinal) != true)
        { Log.Warning("[Follow Cam - Multiplayer] Declarations refused: " + local + "; " + personal + "; " + localComponent); return; }
        var harmony = new Harmony("rimworldmultiplayer.followcam.personal");
        try
        {
            followAssembly = comp!.Assembly;
            harmony.Patch(buttons, postfix: new HarmonyMethod(typeof(Startup), nameof(Buttons)));
            harmony.Patch(menus, postfix: new HarmonyMethod(typeof(Startup), nameof(Menus)));
            harmony.Patch(AccessTools.Method(typeof(RimWorld.UIRoot_Play), nameof(RimWorld.UIRoot_Play.UIRootOnGUI)),
                prefix: new HarmonyMethod(typeof(Startup), nameof(BeforeGui)) { priority = Priority.First });
            foreach (var method in new[] { show, threat })
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(Startup), nameof(BeforeMessage)),
                    finalizer: new HarmonyMethod(typeof(Startup), nameof(AfterMessage)));
            ThreatEvents.Install(harmony, comp, settings);
            Log.Message("[Follow Cam - Multiplayer] Personal settings and own-screen messages ready.");
        }
        catch (Exception error)
        {
            harmony.UnpatchAll(harmony.Id);
            Log.Error("[Follow Cam - Multiplayer] Message scopes not installed: " + error);
        }
    }

    static void BeforeMessage(out IDisposable? __state) =>
        __state = Multiplayer.InSession ? Multiplayer.MessagesFor(Multiplayer.LocalPlayer) : null;

    // A finalizer also disposes on exceptions and leaves the original exception
    // untouched. Otherwise a message failure could leave all later notices private.
    static void AfterMessage(IDisposable? __state) => __state?.Dispose();

    static void BeforeGui()
    {
        var input = Event.current;
        if (!Multiplayer.InSession || input?.type != EventType.KeyDown || GUIUtility.keyboardControl != 0 ||
            Find.CurrentMap == null || WorldRendererUtility.WorldSelected ||
            Find.WindowStack.Windows.Any(window => window.absorbInputAroundWindow)) return;
        // The upstream defaults overlap vanilla's Next/PreviousColonist.
        // Vanilla consumes those events and cancels follow before the mod's
        // late OnGUI sees them. Give the actual configured follow keys one
        // local turn first; leave text fields, dialogs and other keys alone.
        if (!KeyBindingDef.Named("FollowCam_NextColonist").JustPressed &&
            !KeyBindingDef.Named("FollowCam_PreviousColonist").JustPressed) return;
        var component = instance!.GetValue(null);
        if (component == null) return;
        using (Multiplayer.LocalScope()) manualCycle!.Invoke(component, null);
        input.Use();
    }

    static IEnumerable<Gizmo> Buttons(IEnumerable<Gizmo> __result)
    {
        foreach (var gizmo in __result)
        {
            if (Multiplayer.InSession && gizmo is Command_Action button && button.action is {} original &&
                original.Method.DeclaringType?.Assembly == followAssembly &&
                original.Method.DeclaringType?.FullName?.StartsWith("FollowCam.PawnGetGizmosPatch+", StringComparison.Ordinal) == true)
            {
                // Reuse the public local-delegate pattern from the existing
                // RunAndGun companion. The complete original callback runs on
                // this seat, including its message, with no host gizmo replay.
                button.action = Local(original);
            }
            yield return gizmo;
        }
    }

    static Action Local(Action original)
    {
        Action local = () => { using (Multiplayer.LocalScope()) original(); };
        if (localButtons.Add(local.Method))
        {
            var result = Multiplayer.RegisterLocalMethod(local.Method);
            if (result?.StartsWith("installed|", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Follow Cam local callback refused: " + result);
        }
        return local;
    }

    // __2 is the original patch method's third (ref list) argument.
    static void Menus(List<FloatMenuOption> __2)
    {
        if (!Multiplayer.InSession || __2 == null) return;
        foreach (var option in __2)
            if (option.action is {} original && original.Method.DeclaringType?.Assembly == followAssembly &&
                original.Method.DeclaringType?.FullName?.StartsWith("FollowCam.FloatMenuMakerMap_GetOptions_Patch+", StringComparison.Ordinal) == true)
                option.action = Local(original);
    }
}
