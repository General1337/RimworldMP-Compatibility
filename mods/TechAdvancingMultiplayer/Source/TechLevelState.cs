using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimworldMultiplayer.Api;
using Verse;
using Verse.Profile;

namespace TechAdvancingMultiplayer;

public sealed class TechLevelState(World world) : WorldComponent(world)
{
    private FactionDef? faction;
    private TechLevel level;
    private static FactionDef? replicaFaction;
    private static TechLevel originalLevel;
    private static TechLevel appliedLevel;

    public override void ExposeData()
    {
        base.ExposeData();
        if (Scribe.mode == LoadSaveMode.Saving && Multiplayer.IsHost)
        {
            faction = world.factionManager.OfPlayer?.def;
            level = faction?.techLevel ?? TechLevel.Undefined;
        }

        Scribe_Defs.Look(ref faction, "playerFaction");
        Scribe_Values.Look(ref level, "playerTechLevel");
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
            Apply();
    }

    public override void FinalizeInit(bool fromLoad)
    {
        base.FinalizeInit(fromLoad);
        if (fromLoad)
            Apply();
    }

    private void Apply()
    {
        if (Multiplayer.IsHost || faction is null || !faction.isPlayer ||
            level == TechLevel.Undefined || !Enum.IsDefined(level))
            return;

        if (replicaFaction != faction)
        {
            ClearReplicaLevel();
            replicaFaction = faction;
            originalLevel = faction.techLevel;
        }

        faction.techLevel = level;
        appliedLevel = level;
    }

    internal static void ClearReplicaLevel()
    {
        if (replicaFaction is { } previous && previous.techLevel == appliedLevel)
            previous.techLevel = originalLevel;
        replicaFaction = null;
    }
}

[StaticConstructorOnStartup]
internal static class Startup
{
    static Startup()
    {
        if (!Multiplayer.Available)
            return;

        Multiplayer.RegisterCompatibilityFor(typeof(TechLevelState), "ghxx.techadvancing");
        var reset = new HarmonyMethod(typeof(TechLevelState), nameof(TechLevelState.ClearReplicaLevel));
        var harmony = new Harmony("rimworldmultiplayer.techadvancing");
        harmony.Patch(AccessTools.Method(typeof(MemoryUtility), nameof(MemoryUtility.ClearAllMapsAndWorld)), prefix: reset);
        harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.Dispose)), prefix: reset);
    }
}
