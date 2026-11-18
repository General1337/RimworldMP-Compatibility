using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using Verse;

namespace AllowToolMultiplayer;

[StaticConstructorOnStartup]
internal static class Startup
{
    const string Owner = "rimworldmultiplayer.allowtool";
    const int MaxCells = 4096;
    static readonly Guid GameModule = new("71185080-36f8-4a50-863e-e15aba81b822");
    static readonly Guid AllowToolModule = new("8a098910-d6a8-4b18-8090-b9d6c096e9ee");
    static readonly MethodInfo RectCells = AccessTools.DeclaredPropertyGetter(typeof(CellRect), nameof(CellRect.Cells));
    static Type tool = null!;
    static MethodInfo dragger = null!, selectedArea = null!;

    static Startup()
    {
        if (!Multiplayer.Available) return;
        tool = AccessTools.TypeByName("AllowTool.Designator_HaulUrgently");
        if (tool == null) return;
        var module = tool.Module.ModuleVersionId;
        if (module != AllowToolModule || typeof(Designator).Module.ModuleVersionId != GameModule)
        {
            Log.Warning("[Allow Tool Multiplayer] Native edition differs. Area drag hook skipped.");
            return;
        }

        var selectable = tool.BaseType;
        var multi = AccessTools.DeclaredMethod(selectable, nameof(Designator.DesignateMultiCell), new[] { typeof(IEnumerable<IntVec3>) });
        dragger = AccessTools.DeclaredPropertyGetter(selectable?.BaseType, "Dragger");
        selectedArea = dragger == null ? null! : AccessTools.DeclaredPropertyGetter(dragger.ReturnType, "SelectedArea");
        var cell = AccessTools.DeclaredMethod(selectable?.BaseType, nameof(Designator.CanDesignateCell), new[] { typeof(IntVec3) });
        if (multi == null || multi.ReturnType != typeof(void) || multi.IsStatic ||
            dragger == null || dragger.IsStatic || selectedArea == null || selectedArea.IsStatic ||
            selectedArea.ReturnType != typeof(CellRect) || RectCells == null ||
            cell == null || cell.IsStatic || cell.ReturnType != typeof(AcceptanceReport))
        {
            Log.Warning("[Allow Tool Multiplayer] Native area drag signature differs. Hook skipped.");
            return;
        }

        var harmony = new Harmony(Owner);
        try
        {
            var prefix = new HarmonyMethod(typeof(Startup), nameof(CaptureGuestCells))
            {
                priority = Priority.First,
                before = new[] { "Rimworld.Multiplayer" }
            };
            harmony.Patch(multi, prefix: prefix, transpiler: new HarmonyMethod(typeof(Startup), nameof(UseAuthorizedCells)));
            harmony.Patch(cell, postfix: new HarmonyMethod(typeof(Startup), nameof(CheckRelayedCell)));
            Log.Message($"[Allow Tool Multiplayer] Area drag hook ready for {module}.");
        }
        catch (Exception error)
        {
            harmony.Unpatch(multi, HarmonyPatchType.All, Owner);
            harmony.Unpatch(cell, HarmonyPatchType.All, Owner);
            Log.Warning($"[Allow Tool Multiplayer] Area drag hook failed: {error}");
        }
    }

    // Rimworld Multiplayer's ordinary relay runs after this prefix and receives the exact cells
    // the guest's native Allow Tool dragger would have enumerated.
    static void CaptureGuestCells(Designator __instance, object[] __args)
    {
        if (__instance.GetType() != tool || !Multiplayer.InSession || Multiplayer.IsHost ||
            Multiplayer.Player(Multiplayer.LocalPlayer)?.Colony != 0 || Find.CurrentMap is not { } map)
            return;

        var context = dragger.Invoke(__instance, null);
        var rect = ((CellRect)selectedArea.Invoke(context, null)!).ClipInsideMap(map);
        if ((long)rect.Width * rect.Height > MaxCells)
        {
            Messages.Message("CT.Old.DesignateCellLimit".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            __args[0] = Array.Empty<IntVec3>();
            return;
        }
        __args[0] = rect.Cells.ToArray();
    }

    static IEnumerable<CodeInstruction> UseAuthorizedCells(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var sites = code.Select((instruction, index) => (instruction, index))
            .Where(row => row.instruction.Calls(RectCells)).Select(row => row.index).ToArray();
        if (sites.Length != 1 || code[sites[0]].blocks.Count != 0)
            throw new InvalidOperationException("Allow Tool must enumerate one native drag rectangle.");

        int site = sites[0];
        var loadTool = new CodeInstruction(OpCodes.Ldarg_0);
        loadTool.labels.AddRange(code[site].labels);
        code[site].labels.Clear();
        code[site].opcode = OpCodes.Call;
        code[site].operand = AccessTools.DeclaredMethod(typeof(Startup), nameof(ReplayCells));
        code.Insert(site, loadTool);
        code.Insert(site + 1, new CodeInstruction(OpCodes.Ldarg_1));
        return code;
    }

    static IEnumerable<IntVec3> ReplayCells(ref CellRect nativeArea, Designator instance, IEnumerable<IntVec3> cells)
    {
        // Use the exact list checked by Rimworld Multiplayer. Rebuilding a rectangle could
        // reintroduce cells that its host-side filter removed.
        return RemoteSharedDrag(instance) ? cells : nativeArea.Cells;
    }

    // The custom dragger leaves the vanilla cell test false. The native area
    // loop still validates each item using CanDesignateThing on the host.
    static void CheckRelayedCell(Designator __instance, IntVec3 __0, ref AcceptanceReport __result)
    {
        if (RemoteSharedDrag(__instance) && Find.CurrentMap is { } map && __0.InBounds(map))
            __result = true;
    }

    static bool RemoteSharedDrag(Designator instance)
    {
        if (instance.GetType() != tool || !Multiplayer.InSession || !Multiplayer.IsHost) return false;
        int acting = Multiplayer.CurrentPlayer;
        return acting != Multiplayer.LocalPlayer && Multiplayer.Player(acting)?.Colony == 0;
    }
}
