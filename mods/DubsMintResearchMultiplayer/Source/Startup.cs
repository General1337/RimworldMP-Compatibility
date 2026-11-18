using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace DubsMintResearchMultiplayer;

[StaticConstructorOnStartup]
internal static class Startup
{
    const string Owner = "rimworldmultiplayer.dubsmintresearch";
    static readonly MethodInfo ManagerGetter = AccessTools.DeclaredPropertyGetter(typeof(Find), nameof(Find.ResearchManager));
    static readonly FieldInfo CurrentProject = AccessTools.DeclaredField(typeof(ResearchManager), "currentProj");
    static FieldInfo targetProject = null!;
    static MethodInfo clear = null!, roll = null!;
    static string? action;

    static Startup()
    {
        if (!Multiplayer.Available) return;
        var window = AccessTools.TypeByName("DubsMintMenus.MainTabWindow_MintResearch");
        if (window?.Module.ModuleVersionId != new Guid("f98d42ca-ae71-4586-b602-bf5df323a5ba") ||
            typeof(ResearchManager).Module.ModuleVersionId != new Guid("71185080-36f8-4a50-863e-e15aba81b822"))
        {
            Log.Warning("[Dubs Mint Research Multiplayer] Native edition differs; no cancel hook installed.");
            return;
        }

        var commands = AccessTools.Inner(window, "MysterBox");
        var finish = AccessTools.TypeByName("DubsMintMenus.HarmonyPatches+Patch_FinishProject");
        var draw = AccessTools.DeclaredMethod(window, "DrawResearchList",
            new[] { typeof(Rect), typeof(Rect), typeof(int).MakeByRefType(), typeof(List<ResearchProjectDef>), typeof(bool) });
        targetProject = finish == null ? null! : AccessTools.DeclaredField(finish, "TargetProject");
        clear = commands == null ? null! : AccessTools.DeclaredMethod(commands, "NullOff", Type.EmptyTypes);
        roll = commands == null ? null! : AccessTools.DeclaredMethod(commands, "RollQueue", Type.EmptyTypes);
        if (ManagerGetter == null || CurrentProject?.FieldType != typeof(ResearchProjectDef) ||
            targetProject?.FieldType != typeof(ResearchProjectDef) || !targetProject.IsStatic ||
            clear?.IsStatic != true || clear.ReturnType != typeof(void) ||
            roll?.IsStatic != true || roll.ReturnType != typeof(void) || draw == null ||
            CancelSites(PatchProcessor.GetOriginalInstructions(draw)).Count != 2)
        {
            Log.Warning("[Dubs Mint Research Multiplayer] Native cancel controls changed; no hook installed.");
            return;
        }

        action = Multiplayer.RegisterAction(typeof(Startup), nameof(ApplyCancel));
        if (string.IsNullOrEmpty(action)) return;
        new Harmony(Owner).Patch(draw, transpiler: new HarmonyMethod(typeof(Startup), nameof(ReplaceCancel)));
        Log.Message("[Dubs Mint Research Multiplayer] Native cancel adapter ready.");
    }

    // Match both active/target close branches. Pending-row removal and the
    // separate drag path call native methods exactly as before.
    static List<int> CancelSites(IReadOnlyList<CodeInstruction> code)
    {
        var sites = new List<int>();
        for (int i = 0; i + 5 < code.Count; i++)
        {
            if (code[i].Calls(ManagerGetter) &&
                code[i + 1].opcode == OpCodes.Ldnull &&
                code[i + 2].opcode == OpCodes.Stfld && Equals(code[i + 2].operand, CurrentProject) &&
                code[i + 3].opcode == OpCodes.Ldnull &&
                code[i + 4].opcode == OpCodes.Stsfld && Equals(code[i + 4].operand, targetProject) &&
                code[i + 5].Calls(roll))
                sites.Add(i);
        }
        return sites;
    }

    static IEnumerable<CodeInstruction> ReplaceCancel(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var sites = CancelSites(code);
        if (sites.Count != 2 || sites.Any(i => code.Skip(i + 1).Take(5).Any(x => x.labels.Count != 0 || x.blocks.Count != 0)))
            throw new InvalidOperationException("Dubs Mint active cancel branches no longer match.");

        var cancel = AccessTools.DeclaredMethod(typeof(Startup), nameof(CancelFromUi));
        foreach (int i in sites)
        {
            code[i].opcode = OpCodes.Call;
            code[i].operand = cancel;
            for (int offset = 1; offset <= 5; offset++)
            {
                code[i + offset].opcode = OpCodes.Nop;
                code[i + offset].operand = null;
            }
        }
        return code;
    }

    static ResearchProjectDef? Current() => (ResearchProjectDef?)CurrentProject.GetValue(Find.ResearchManager);
    static ResearchProjectDef? Target() => (ResearchProjectDef?)targetProject.GetValue(null);

    static void CancelFromUi()
    {
        var current = Current();
        var target = Target();
        if (Multiplayer.IsHost)
            ApplyCancel(current, target);
        else
            Multiplayer.RequestAction(action!, 1, new object?[] { current, target });
    }

    // The public action runs in the requesting member's normal research
    // scope. A delayed click cannot cancel a different active project.
    static bool ApplyCancel(ResearchProjectDef? current, ResearchProjectDef? target)
    {
        if ((current == null && target == null) || Current() != current || Target() != target)
            return false;
        clear.Invoke(null, null);
        roll.Invoke(null, null);
        return true;
    }
}
