using RimworldMultiplayer.Api;
using Verse;

namespace AbilityFrameworkMultiplayer;

// No Rimworld Multiplayer internals, target assembly reference, private reflection or replacement
// spell execution. The original UI, costs, target checks and host job still run.
[StaticConstructorOnStartup]
public static class Startup
{
    static Startup()
    {
        if (!Multiplayer.Available) return;
        var type = GenTypes.GetTypeInAnyAssembly("AbilityUser.Verb_UseAbility");
        if (type == null) { Log.Warning("[AbilityFrameworkMultiplayer] Ability framework is absent; no declaration installed."); return; }
        string? result = Multiplayer.RegisterRoutedVerb(type);
        Log.Message("[AbilityFrameworkMultiplayer] routed verbs: " + result);
        if (result?.StartsWith("installed|", StringComparison.Ordinal) != true)
            Log.Error("[AbilityFrameworkMultiplayer] Required routed verb support is unavailable; guest spell support is not installed.");
        var ability = GenTypes.GetTypeInAnyAssembly("AbilityUser.PawnAbility");
        var user = GenTypes.GetTypeInAnyAssembly("AbilityUser.CompAbilityUser");
        if (user != null)
            Log.Message("[AbilityFrameworkMultiplayer] host ability data: " + Multiplayer.RegisterHostField(user, "abilityData"));
        if (ability != null)
        {
            string? cooldown = Multiplayer.RegisterHostField(ability, "ticksUntilCasting");
            Log.Message("[AbilityFrameworkMultiplayer] host cooldown: " + cooldown);
            if (cooldown?.StartsWith("installed|", StringComparison.Ordinal) != true)
                Log.Error("[AbilityFrameworkMultiplayer] Required host cooldown support is unavailable.");
        }
    }
}
