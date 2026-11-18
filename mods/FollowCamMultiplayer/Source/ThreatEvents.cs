using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimworldMultiplayer.Api;
using Verse;

namespace FollowCamMultiplayer;

// Native injuries happen on the host. Broadcast the observation independently
// of the host's personal camera preferences; each client applies the upstream
// eligibility, cooldown and temporary-follow behavior with its own settings.
internal static class ThreatEvents
{
    const string Channel = "followcam.threat.v1";
    static FieldInfo? component, settings, damageEnabled, downedEnabled;
    static MethodInfo? eligible, damage, downed;

    internal static void Install(Harmony harmony, Type comp, Type preferences)
    {
        component = AccessTools.Field(comp, "Instance");
        settings = AccessTools.Field(AccessTools.TypeByName("FollowCam.FollowCamMod"), "Settings");
        damageEnabled = AccessTools.Field(preferences, "AutoFollowOnDamage");
        downedEnabled = AccessTools.Field(preferences, "AutoFollowOnDowned");
        eligible = AccessTools.Method(AccessTools.TypeByName("FollowCam.ThreatFollowUtility"), "ShouldTriggerThreatFollow",
            new[] { typeof(Pawn), typeof(bool) });
        damage = AccessTools.Method(comp, "TryStartDamageThreatFollow", new[] { typeof(Pawn) });
        downed = AccessTools.Method(comp, "TryStartDownedThreatFollow", new[] { typeof(Pawn) });
        var damageHook = AccessTools.Method(AccessTools.TypeByName("FollowCam.ThreatFollow_Damage_Patch"), "Postfix",
            new[] { typeof(Pawn_HealthTracker) });
        var downedHook = AccessTools.Method(AccessTools.TypeByName("FollowCam.ThreatFollow_Downed_Patch"), "Postfix",
            new[] { typeof(Pawn_HealthTracker) });
        if (component == null || settings == null || damageEnabled?.FieldType != typeof(bool) ||
            downedEnabled?.FieldType != typeof(bool) || eligible?.ReturnType != typeof(bool) ||
            damage?.ReturnType != typeof(void) || downed?.ReturnType != typeof(void) ||
            damageHook?.ReturnType != typeof(void) || downedHook?.ReturnType != typeof(void))
            throw new InvalidOperationException("Follow Cam threat event signatures changed");
        var registered = Multiplayer.RegisterPlayerHandler(Channel, Receive);
        if (registered?.StartsWith("installed|", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Follow Cam threat channel refused: " + registered);
        harmony.Patch(damageHook, postfix: new HarmonyMethod(typeof(ThreatEvents), nameof(Damaged)));
        harmony.Patch(downedHook, postfix: new HarmonyMethod(typeof(ThreatEvents), nameof(Downed)));
    }

    static void Damaged(Pawn_HealthTracker __0) => Publish(__0, "damage");
    static void Downed(Pawn_HealthTracker __0) => Publish(__0, "downed");

    static void Publish(Pawn_HealthTracker tracker, string kind)
    {
        if (!Multiplayer.InSession || !Multiplayer.IsHost) return;
        var pawn = AccessTools.Field(typeof(Pawn_HealthTracker), "pawn").GetValue(tracker) as Pawn;
        if (pawn == null || pawn.Map == null || !(bool)eligible!.Invoke(null, new object[] { pawn, true })!) return;
        // The original callback has already handled the host's preferences.
        // Receive ignores the host so it is never invoked a second time there.
        Multiplayer.SendToAll(Channel, kind + ";" + pawn.Map.uniqueID.ToString(CultureInfo.InvariantCulture) + ";" +
            pawn.thingIDNumber.ToString(CultureInfo.InvariantCulture));
    }

    static void Receive(string text)
    {
        if (!Multiplayer.InSession || Multiplayer.IsHost || Current.Game == null) return;
        var parts = text.Split(';');
        if (parts.Length != 3 || parts[0] is not ("damage" or "downed") ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mapId) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pawnId)) return;
        var pawn = Find.Maps.FirstOrDefault(map => map.uniqueID == mapId)?.mapPawns.AllPawnsSpawned
            .FirstOrDefault(p => p.thingIDNumber == pawnId);
        var current = component!.GetValue(null);
        var preferences = settings!.GetValue(null);
        if (pawn == null || current == null || preferences == null) return;
        bool enabled = (bool)(parts[0] == "damage" ? damageEnabled! : downedEnabled!).GetValue(preferences)!;
        if (!(bool)eligible!.Invoke(null, new object[] { pawn, enabled })!) return;
        using (Multiplayer.LocalScope())
            (parts[0] == "damage" ? damage! : downed!).Invoke(current, new object[] { pawn });
    }
}
