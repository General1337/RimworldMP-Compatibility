using HarmonyLib;
using Verse;

namespace ToggleableOverlays17;

[StaticConstructorOnStartup]
internal static class FogCompatibility
{
    private static readonly Type[] FogArguments = { typeof(IntVec3) };
    private static readonly System.Reflection.MethodInfo? OldFogCheck = AccessTools.Method(
        AccessTools.TypeByName("ToggleableOverlays.Patch_ThingOverlays_ThingOverlaysOnGUI"),
        "IsFoggedFast", new[] { typeof(FogGrid), typeof(IntVec3) });
    private static readonly System.Reflection.MethodInfo NativeFogCheck =
        AccessTools.Method(typeof(FogGrid), nameof(FogGrid.IsFogged), FogArguments);

    static FogCompatibility()
    {
        var draw = AccessTools.Method(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI));
        if (OldFogCheck?.IsStatic != true || OldFogCheck.ReturnType != typeof(bool) || draw == null)
        {
            Log.Error("[ToggleableOverlays17] The expected overlay fog check was not found.");
            return;
        }

        new Harmony("rimworldmultiplayer.toggleableoverlays17").Patch(draw,
            transpiler: new HarmonyMethod(typeof(FogCompatibility), nameof(UseNativeFogCheck))
            {
                priority = Priority.Last
            });
    }

    private static IEnumerable<CodeInstruction> UseNativeFogCheck(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(OldFogCheck))
            {
                yield return new CodeInstruction(instruction)
                {
                    opcode = System.Reflection.Emit.OpCodes.Callvirt,
                    operand = NativeFogCheck
                };
                continue;
            }

            yield return instruction;
        }
    }
}
