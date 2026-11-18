using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace HegemonyMultiplayer;

// Native saved records have no ILoadReferenceable identity. Resolve the
// current saved record by its existing settlement, keep the panel's view
// references current, and send the native tax control's result as an action.
[StaticConstructorOnStartup]
internal static class Startup
{
    static Type? record;
    static PropertyInfo? component, states;
    static FieldInfo? selected, filtered, inspected, settlement;
    static FieldInfo[] rates = Array.Empty<FieldInfo>();
    static MethodInfo? slider;
    static string? action;
    static readonly Dictionary<(int Id, int Kind), float> pending = new();
    sealed record Drawing(object Window, int Id) { internal int Slider; }
    [ThreadStatic] static Drawing? drawing;

    static Startup()
    {
        if (!Multiplayer.Available || Multiplayer.HostApiVersion < 5) return;
        var tab = AccessTools.TypeByName("FactionTerritories2.VassalageTab");
        var owner = AccessTools.TypeByName("FactionTerritories2.Component");
        record = AccessTools.TypeByName("FactionTerritories2.SettlementState");
        if (tab == null || owner == null || record == null) return;
        component = AccessTools.Property(owner, "Current");
        states = AccessTools.Property(owner, "States");
        selected = AccessTools.Field(tab, "selected");
        inspected = AccessTools.Field(tab, "inspected");
        filtered = AccessTools.Field(tab, "filtered");
        settlement = AccessTools.Field(record, "settlement");
        rates = new[] { "foodTax", "materialTax", "prosperityTax" }.Select(n => AccessTools.Field(record, n)).ToArray();
        var draw = AccessTools.DeclaredMethod(tab, "DrawContents", new[] { typeof(Rect) });
        var kinds = AccessTools.Field(tab, "taxKinds")?.GetValue(null) as Array;
        slider = AccessTools.Method(typeof(Widgets), nameof(Widgets.HorizontalSlider), new[] {
            typeof(Rect), typeof(float), typeof(float), typeof(float), typeof(bool),
            typeof(string), typeof(string), typeof(string), typeof(float) });
        if (component == null || states == null || selected?.FieldType != record || inspected?.FieldType != record ||
            filtered == null || settlement == null || rates.Any(f => f?.FieldType != typeof(float)) || draw == null || slider == null ||
            kinds == null || !kinds.Cast<object>().Select(x => x.ToString()).SequenceEqual(new[] { "Food", "Materials", "Prosperity" }) ||
            PatchProcessor.GetOriginalInstructions(draw).Count(c => c.Calls(slider)) != 1)
        { Log.Warning("[Hegemony Multiplayer] Native tax controls changed; adapter was not installed."); return; }
        action = Multiplayer.RegisterAction(typeof(Startup), nameof(SetTax));
        if (string.IsNullOrEmpty(action))
        { Log.Warning("[Hegemony Multiplayer] Tax action was not registered."); return; }
        new Harmony("rimworldmultiplayer.examples.hegemony").Patch(draw,
            prefix: new HarmonyMethod(typeof(Startup), nameof(Before)),
            transpiler: new HarmonyMethod(typeof(Startup), nameof(Controls)),
            finalizer: new HarmonyMethod(typeof(Startup), nameof(After)));
        Log.Message("[Hegemony Multiplayer] Native settlement tax adapter ready.");
    }

    static object[] Saved() => component?.GetValue(null) is { } owner && states?.GetValue(owner) is IEnumerable list
        ? list.Cast<object>().ToArray() : Array.Empty<object>();
    static RimWorld.Planet.Settlement? Town(object? value) => value == null ? null : settlement?.GetValue(value) as RimWorld.Planet.Settlement;
    static object? Current(object? old, object[] live)
    {
        var town = Town(old);
        if (town == null || town.Destroyed) return null;
        var matches = live.Where(s => ReferenceEquals(Town(s), town)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    static void Before(object __instance, out Drawing? __state)
    {
        __state = drawing;
        var live = Saved();
        using (Multiplayer.LocalScope())
        {
            // These are the native panel's own retained view references.
            // Never alter saved records, caches, world membership or IDs.
            if (filtered!.GetValue(__instance) is IList list)
                for (int i = list.Count - 1; i >= 0; i--)
                    if (Current(list[i], live) is { } now) list[i] = now; else list.RemoveAt(i);
            selected!.SetValue(__instance, Current(selected.GetValue(__instance), live));
            inspected!.SetValue(__instance, Current(inspected.GetValue(__instance), live));
        }
        drawing = new(__instance, Town(selected!.GetValue(__instance))?.ID ?? -1);
    }
    static Exception? After(Exception? __exception, Drawing? __state) { drawing = __state; return __exception; }
    static IEnumerable<CodeInstruction> Controls(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.Method(typeof(Startup), nameof(TaxSlider));
        foreach (var code in instructions)
        {
            if (code.Calls(slider!)) { code.opcode = OpCodes.Call; code.operand = replacement; }
            yield return code;
        }
    }
    static float TaxSlider(Rect rect, float value, float left, float right, bool middle,
        string label, string leftLabel, string rightLabel, float roundTo)
    {
        float desired = Widgets.HorizontalSlider(rect, value, left, right, middle, label, leftLabel, rightLabel, roundTo);
        var context = drawing;
        int kind = context == null ? -1 : context.Slider++;
        if (Multiplayer.IsHost || !Multiplayer.InSession) return desired;
        if (context != null && kind is >= 0 and < 3 && context.Id >= 0)
        {
            var key = (context.Id, kind);
            // A drag draws repeatedly while its first request is in flight.
            // Wait for the authoritative value before sending another step.
            if (pending.TryGetValue(key, out float sent) && Math.Abs(sent - value) < .0001f) pending.Remove(key);
            if (Math.Abs(desired - value) > .0001f && !pending.ContainsKey(key))
            {
                pending[key] = desired;
                Multiplayer.RequestAction(action!, 1, new object?[] { context.Id, kind, value, desired },
                    (status, _, reason) =>
                    {
                        if (status == ActionStatus.Completed) return;
                        pending.Remove(key);
                        Messages.Message("Settlement tax change rejected: " + reason, MessageTypeDefOf.RejectInput, false);
                    });
            }
        }
        // The native assignment cannot change the client's saved world.
        return value;
    }
    public static void SetTax(int id, int kind, float expected, float value)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1 || !float.IsFinite(expected) || expected < 0 || expected > 1 || kind is < 0 or > 2)
            throw new ArgumentException("Invalid settlement tax");
        var matches = Saved().Where(s => Town(s) is { Destroyed: false } t && t.ID == id &&
            Find.WorldObjects.Settlements.Any(x => ReferenceEquals(x, t))).Take(2).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Settlement no longer exists");
        var state = matches[0];
        if (AccessTools.Field(record!, "vassal").GetValue(state) is not true ||
            AccessTools.Field(record!, "rebelling").GetValue(state) is true)
            throw new InvalidOperationException("Settlement is not a loyal vassal");
        float current = (float)rates[kind].GetValue(state)!;
        // Category rates inherit the settlement's base rate until first set.
        if (current < 0) current = (float)AccessTools.Field(record!, "taxRate").GetValue(state)!;
        if (Math.Abs(current - value) < .0001f) return;
        if (Math.Abs(current - expected) > .0001f) throw new InvalidOperationException("Settlement tax changed");
        rates[kind].SetValue(state, value);
    }
}
