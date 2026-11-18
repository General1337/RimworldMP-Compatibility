using System.Reflection;
using HarmonyLib;
using RimworldMultiplayer.Api;
using Verse;
using Verse.AI;

namespace VoidContactMultiplayer;

// An unanswered native contact already owns the negotiator's next decision.
// Leave it pending rather than creating more automatic contact jobs, which
// the native driver immediately rejects against the existing contactee.
[StaticConstructorOnStartup]
internal static class Startup
{
    static Type? dialog;
    static FieldInfo? contacter;
    static Startup()
    {
        if (!Multiplayer.Available || Multiplayer.HostApiVersion < 5) return;
        dialog = AccessTools.TypeByName("VoidEvents.Dialog_VoidContact");
        var giver = AccessTools.TypeByName("VoidEvents.JobGiver_ContactColonist");
        if (dialog == null || giver == null) return;
        contacter = AccessTools.Field(dialog, "contacter");
        var method = AccessTools.DeclaredMethod(giver, "TryGiveJob", new[] { typeof(Pawn) });
        if (!typeof(Window).IsAssignableFrom(dialog) || contacter?.FieldType != typeof(Pawn) || method?.ReturnType != typeof(Job))
        { Log.Warning("[VOID Contact Multiplayer] Native contact methods changed; adapter was not installed."); return; }
        new Harmony("rimworldmultiplayer.examples.voidcontact").Patch(method,
            prefix: new HarmonyMethod(typeof(Startup), nameof(Before)));
        Log.Message("[VOID Contact Multiplayer] Pending contact job adapter ready.");
    }
    static bool Before(Pawn pawn, ref Job? __result)
    {
        if (!Multiplayer.IsHost || Find.WindowStack == null) return true;
        foreach (var window in Find.WindowStack.Windows)
            if (window.GetType() == dialog && ReferenceEquals(contacter!.GetValue(window), pawn))
            {
                __result = null;
                return false;
            }
        return true;
    }
}
