using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimworldMultiplayer.Api;
using Verse;

namespace MeleeAnimationMultiplayer;

// Native idle renderers contain meshes, weapon overrides and registration that
// their save does not carry. Rebuild those on the client's display tick through
// the mod's own visual methods. Never run IdleControllerComp.CompTick/skills.
[StaticConstructorOnStartup]
internal static class Startup
{
    internal static Type? Owner;
    internal static MethodInfo? ShouldBeActive, TickActive, ClearAnimation, PreDraw;
    internal static FieldInfo? Current, Vanilla, Renderers;
    internal static bool Installed;

    static Startup()
    {
        if (!Multiplayer.Available || Multiplayer.HostApiVersion < 6) return;
        Owner = AccessTools.TypeByName("AM.Idle.IdleControllerComp");
        if (Owner == null || !typeof(ThingComp).IsAssignableFrom(Owner)) return;
        ShouldBeActive = AccessTools.DeclaredMethod(Owner, "ShouldBeActive", new[] { typeof(Thing).MakeByRefType(), typeof(bool).MakeByRefType() });
        TickActive = AccessTools.DeclaredMethod(Owner, "TickActive", new[] { typeof(Thing) });
        ClearAnimation = AccessTools.DeclaredMethod(Owner, "ClearAnimation", Type.EmptyTypes);
        PreDraw = AccessTools.DeclaredMethod(Owner, "PreDraw", Type.EmptyTypes);
        Current = AccessTools.DeclaredField(Owner, "currentAnimation");
        Vanilla = AccessTools.DeclaredField(Owner, "wantsVanillaDrawThisFrame");
        Renderers = Current == null ? null : AccessTools.DeclaredField(Current.FieldType, "activeRenderers");
        if (ShouldBeActive?.ReturnType != typeof(bool) || TickActive?.ReturnType != typeof(void) ||
            ClearAnimation?.ReturnType != typeof(void) || PreDraw?.ReturnType != typeof(bool) || Current?.FieldType.FullName != "AM.AnimRenderer" || Vanilla?.FieldType != typeof(bool) ||
            Renderers == null || !Renderers.IsStatic || !typeof(IList).IsAssignableFrom(Renderers.FieldType))
        { Log.Warning("[Melee Animation - Multiplayer] Native visual methods changed; adapter was not installed."); return; }
        foreach (string field in new[] { "currentAnimation", "wantsVanillaDrawThisFrame", "drawTick" })
        {
            string? result = Multiplayer.RegisterLocalField(Owner, field);
            if (result?.StartsWith("installed|", StringComparison.Ordinal) != true)
            { Log.Warning("[Melee Animation - Multiplayer] Local visual field refused: " + result); return; }
        }
        string? tick = Multiplayer.RegisterLocalComponent(typeof(MeleePresentation));
        Installed = tick?.StartsWith("installed|", StringComparison.Ordinal) == true;
        if (!Installed) { Log.Warning("[Melee Animation - Multiplayer] Display component refused: " + tick); return; }
        // Native pawn drawing calls PreDraw independently of our local tick.
        // Its idle-only transform write is local there too. AnimRenderer's
        // shared RootTransform also drives combat positions, so its field
        // must retain its ordinary authority outside this exact idle method.
        try
        {
            new Harmony("meleeanimation.multiplayer.idle-draw").Patch(PreDraw,
                prefix: new HarmonyMethod(typeof(Startup), nameof(BeforeIdleDraw)),
                finalizer: new HarmonyMethod(typeof(Startup), nameof(AfterIdleDraw)));
        }
        catch (Exception error)
        { Installed = false; Log.Warning("[Melee Animation - Multiplayer] Local idle draw scope could not be installed: " + error); return; }
        Log.Message("[Melee Animation - Multiplayer] Local idle display adapter ready.");
    }

    static void BeforeIdleDraw(object __instance, out IDisposable? __state) =>
        __state = Installed && !Multiplayer.IsHost && __instance.GetType() == Owner ? Multiplayer.LocalScope() : null;
    static void AfterIdleDraw(IDisposable? __state) => __state?.Dispose();

    internal static void Display()
    {
        if (!Installed || Multiplayer.IsHost || Find.CurrentMap is not {} map || Find.CameraDriver is not {} camera) return;
        var visible = camera.CurrentViewRect.ExpandedBy(1);
        // This is client-owned animation data, not an edit to the host's
        // serialized animation record. Retain the native visual preparation
        // instead of queuing its saved fields for frame undo.
        using var local = Multiplayer.LocalScope();
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
        {
            if (!visible.Contains(pawn.Position)) continue;
            // Derived skill-specific controllers may replace these methods;
            // they need their own audited visual path.
            var comp = pawn.AllComps.FirstOrDefault(c => c.GetType() == Owner);
            if (comp == null) continue;
            object?[] args = { null, false };
            bool active = (bool)ShouldBeActive!.Invoke(comp, args)!;
            Vanilla!.SetValue(comp, args[1]);
            if (active)
            {
                // Native world/component loading clears its renderer registry
                // without destroying the idle controller's retained renderer.
                // Rebuild through the native idle path when registration is lost.
                if (Current!.GetValue(comp) is {} renderer && !((IList)Renderers!.GetValue(null)!).Contains(renderer))
                    ClearAnimation!.Invoke(comp, null);
                TickActive!.Invoke(comp, new[] { args[0] });
                // New renderers start at an empty transform. The native
                // draw hook need not run in the replicated pawn draw path.
                // This native visual preparation anchors them to DrawPos.
                PreDraw!.Invoke(comp, null);
            }
            else if (Current!.GetValue(comp) != null) ClearAnimation!.Invoke(comp, null);
        }
    }
}

// No saved fields: the public API owns its client-only, guarded display tick.
public sealed class MeleePresentation : GameComponent
{
    public MeleePresentation(Game game) { }
    public override void GameComponentTick() => Startup.Display();
}
