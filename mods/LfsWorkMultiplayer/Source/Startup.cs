using HarmonyLib;
using RimworldMultiplayer.Api;
using Verse;

namespace LfsWorkMultiplayer;

[StaticConstructorOnStartup]
internal static class Startup
{
    static Startup()
    {
        if (!Multiplayer.Available || Multiplayer.HostApiVersion < 5) return;
        var template = AccessTools.TypeByName("UIRethemed.Work.WorkTemplate");
        var definition = AccessTools.TypeByName("UIRethemed.Work.WorkTemplateDef");
        if (template?.Module.ModuleVersionId != new Guid("3ef3d790-4b43-4066-b0e0-b858b81a6039") ||
            typeof(Def).Module.ModuleVersionId != new Guid("71185080-36f8-4a50-863e-e15aba81b822") ||
            definition?.Module != template.Module || !typeof(Def).IsAssignableFrom(definition))
        {
            Log.Warning("[LFS Work Multiplayer] Native edition differs; no stock template hook installed.");
            return;
        }

        var factory = AccessTools.DeclaredMethod(template, "MadeFrom", new[] { definition });
        if (factory?.IsStatic != true || factory.ReturnType != template)
        {
            Log.Warning("[LFS Work Multiplayer] Native stock template factory changed; no hook installed.");
            return;
        }

        new Harmony("rimworldmultiplayer.lfswork").Patch(factory,
            prefix: new HarmonyMethod(typeof(Startup), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(Startup), nameof(End)));
        Log.Message("[LFS Work Multiplayer] Stock template adapter ready.");
    }

    // The pinned factory only constructs an unpublished template from a Def.
    // Keep its initialization writes local until the fully built value returns.
    static void Begin(out IDisposable? __state) =>
        __state = !Multiplayer.IsHost && Multiplayer.InSession ? Multiplayer.LocalScope() : null;

    static void End(IDisposable? __state) => __state?.Dispose();
}
