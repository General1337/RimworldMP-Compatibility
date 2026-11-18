using System.Reflection;
using RimworldMultiplayer.Api;
using Verse;

namespace ExampleCompat;

// A button on every colonist that changes saved state. Without a declaration a
// player's press would change only that player's copy of the world; with
// RegisterSyncMethod the press is sent to the host, which runs it, and everyone
// sees the result. In single player (or without Rimworld Multiplayer) the call
// runs normally and the declarations do nothing.
public class CompWatchToggle : ThingComp
{
    public bool watching;

    public override IEnumerable<Gizmo> CompGetGizmosExtra()
    {
        yield return new Command_Toggle
        {
            defaultLabel = "Watch",
            isActive = () => watching,
            toggleAction = () => SetWatching(!watching),
        };
    }

    // Changes the world: declared as a sync method below.
    public void SetWatching(bool value) => watching = value;

    // Overloads: name the exact parameter types, so the declaration can never pick
    // the wrong method when someone adds an overload later.
    public void SetWatching(bool value, string reason) => watching = value;

    public override void PostExposeData() => Scribe_Values.Look(ref watching, "watching");
}

[StaticConstructorOnStartup]
public static class MultiplayerSupport
{
    static MultiplayerSupport()
    {
        if (!Multiplayer.Available)
            return;
        // Overload-selection recipe: look the method up with its parameter types and
        // pass the MethodInfo. The (type, name) form refuses a name with overloads.
        MethodInfo set = typeof(CompWatchToggle).GetMethod(nameof(CompWatchToggle.SetWatching),
            BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null)!;
        string? status = Multiplayer.RegisterSyncMethod(set);
        // Newer members: ask for the member by name, not by API number.
        if (Multiplayer.Supports(nameof(Multiplayer.RegisterApplyCallbacks)))
            Multiplayer.RegisterApplyCallbacks(typeof(CompWatchToggle), afterApply: _ => { });
        if (status == null || !status.StartsWith("ok"))
            Log.Warning("[ExampleCompat] Multiplayer declaration: " + status);
        // In development, print every declaration and whether it works.
        if (Prefs.DevMode)
            foreach (string line in Multiplayer.Declarations())
                Log.Message("[ExampleCompat] " + line);
    }
}
