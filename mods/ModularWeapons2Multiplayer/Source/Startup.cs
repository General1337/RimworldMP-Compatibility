using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using Verse;

namespace ModularWeapons2Multiplayer;

// MW2 queues a blocking long event in each non-saving ExposeData phase.
// Replica updates can repeat those phases during ordinary play. Keep the
// completed load's real refreshes, on the main thread after loading, without
// opening a new loading screen for each weapon. No optional-mod DLL reference
// or Rimworld Multiplayer implementation type is used.
[StaticConstructorOnStartup]
internal static class Startup
{
    const int Limit = 4096;
    static readonly object gate = new();
    static readonly Dictionary<string, Action> pending = new(StringComparer.Ordinal);
    static Game? world;
    internal static long Scheduled, Applied, Superseded, Faulted;
    internal static int PendingCount;

    static Startup()
    {
        var type = AccessTools.TypeByName("ModularWeapons2.MW2Mod");
        var method = type == null ? null : AccessTools.DeclaredMethod(type, "JustQueueLongEvent", new[] { typeof(Action), typeof(string) });
        if (method == null || method.ReturnType != typeof(void) || !method.IsStatic)
        {
            Log.Warning("[MW2 Multiplayer] Expected load-refresh entry point is absent; no hook installed.");
            return;
        }
        var harmony = new Harmony("rimworldmultiplayer.modularweapons2");
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(Startup), nameof(BeforeQueue)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootUpdate)),
            postfix: new HarmonyMethod(typeof(Startup), nameof(AfterUpdate)));
    }

    static bool LoadRefresh(string key) => key.StartsWith("MW2_RefleshParts_OnExpose:", StringComparison.Ordinal)
        || key.StartsWith("MW2ExposeAbilityCharges_", StringComparison.Ordinal);

    static bool BeforeQueue(Action action, string key)
    {
        if (!Multiplayer.InSession || Multiplayer.IsHost || !LoadRefresh(key)) return true;
        var phase = Scribe.mode;
        if (phase != LoadSaveMode.LoadingVars && phase != LoadSaveMode.ResolvingCrossRefs && phase != LoadSaveMode.PostLoadInit) return true;
        // Partial phases have not resolved the weapon's references. MW2 also
        // requests these same refreshes in PostLoadInit; retain that final pair.
        if (phase != LoadSaveMode.PostLoadInit) return false;
        var current = Current.Game;
        if (current == null) return true;
        lock (gate)
        {
            if (!ReferenceEquals(world, current)) { pending.Clear(); world = current; }
            if (pending.Count >= Limit && !pending.ContainsKey(key)) return true;
            if (pending.Remove(key)) Superseded++;
            // Reinsert in the last load's order: parts before ability charges.
            pending.Add(key, action);
            PendingCount = pending.Count;
            Scheduled++;
        }
        return false;
    }

    static void AfterUpdate()
    {
        Action[] ready;
        lock (gate)
        {
            if (!Multiplayer.InSession || Multiplayer.IsHost || !ReferenceEquals(world, Current.Game))
            {
                pending.Clear(); PendingCount = 0; world = Current.Game;
                return;
            }
            if (Scribe.mode != LoadSaveMode.Inactive || LongEventHandler.AnyEventNowOrWaiting || pending.Count == 0) return;
            ready = pending.Values.ToArray();
            pending.Clear(); PendingCount = 0;
        }
        foreach (var refresh in ready)
        {
            try { refresh(); Applied++; }
            catch (Exception error)
            {
                Faulted++;
                Log.Error("[MW2 Multiplayer] Replica weapon refresh failed: " + error);
            }
        }
    }
}
