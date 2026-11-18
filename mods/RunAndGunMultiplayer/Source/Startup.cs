using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using Verse;

namespace RunAndGunMultiplayer;

// Held-out example: game/Harmony/public-library references only. The native
// comp still decides weapon eligibility and the original mod fires the shots.
[StaticConstructorOnStartup]
public static class Startup
{
    static Type? compType;
    static MethodInfo? valid;
    static PropertyInfo? enabled;
    static string? action;
    static readonly HashSet<MethodInfo> localActions=new();
    static Startup()
    {
        if(!Multiplayer.Available)return;
        compType=GenTypes.GetTypeInAnyAssembly("RunAndGun.CompRunAndGun");
        var ui=GenTypes.GetTypeInAnyAssembly("RunAndGun.Harmony.Pawn_DraftController_GetGizmos_Patch");
        if(compType==null||ui==null)return;
        if(!Multiplayer.Supports(nameof(Multiplayer.RegisterCompatibilityFor))||!Multiplayer.Supports(nameof(Multiplayer.ControlRefusal)))
        {Log.Error("[RunAndGunMultiplayer] Required player action support is unavailable.");return;}
        valid=AccessTools.Method(compType,"IsValid",Type.EmptyTypes);
        enabled=AccessTools.Property(compType,"isEnabled");
        var gizmos=AccessTools.Method(ui,"Postfix",new[]{typeof(IEnumerable<Gizmo>),typeof(Pawn)});
        if(valid?.ReturnType!=typeof(bool)||valid.IsStatic||enabled?.PropertyType!=typeof(bool)||enabled.SetMethod==null||gizmos?.ReturnType!=typeof(IEnumerable<Gizmo>))
        {Log.Error("[RunAndGunMultiplayer] Target methods changed; hooks were not installed.");return;}
        var player=AccessTools.PropertyGetter(typeof(FactionManager),"OfPlayer");
        if(PatchProcessor.GetOriginalInstructions(valid).Count(i=>i.Calls(player))!=1)
        {Log.Error("[RunAndGunMultiplayer] Player-faction check changed; hooks were not installed.");return;}
        action=Multiplayer.RegisterAction(typeof(Startup),nameof(SetMovingFire),policy:ActionPolicy.CancelNullArguments);
        if(string.IsNullOrEmpty(action)){Log.Error("[RunAndGunMultiplayer] Host action could not be registered.");return;}
        Multiplayer.RegisterLocalMethod(typeof(Startup).GetMethod(nameof(RequestToggle),BindingFlags.Static|BindingFlags.NonPublic)!);
        Multiplayer.RegisterCompatibilityFor(typeof(Startup),"memegoddess.runandgun");
        var harmony=new Harmony("rimworldmultiplayer.runandgun");
        harmony.Patch(valid,transpiler:new HarmonyMethod(typeof(Startup),nameof(PlayerCheck)));
        harmony.Patch(gizmos,postfix:new HarmonyMethod(typeof(Startup),nameof(Buttons)));
        Log.Message("[RunAndGunMultiplayer] Public action and player-faction hooks installed.");
    }
    static ThingComp Comp(Pawn pawn)=>pawn.AllComps.First(compType!.IsInstanceOfType);
    static void RequestToggle(Pawn pawn)
    {
        if(!Multiplayer.InSession||!Multiplayer.CanControl(Multiplayer.LocalPlayer,pawn))return;
        bool next=!(bool)enabled!.GetValue(Comp(pawn))!;
        Multiplayer.RequestAction(action!,1,new object?[]{pawn,next},(status,_,reason)=>
        {if(status is ActionStatus.Rejected or ActionStatus.Faulted)Log.Warning("[RunAndGunMultiplayer] Toggle refused: "+reason);});
    }
    static void SetMovingFire(Pawn pawn,bool value)
    {
        string refusal=Multiplayer.ControlRefusal(Multiplayer.CurrentPlayer,pawn);
        if(refusal.Length!=0)throw new InvalidOperationException(refusal);
        var comp=Comp(pawn);
        if(!pawn.Drafted||!(bool)valid!.Invoke(comp,null)!)throw new InvalidOperationException("Moving fire is unavailable for this pawn.");
        enabled!.SetValue(comp,value);
    }
    static IEnumerable<Gizmo> Buttons(IEnumerable<Gizmo> __result,Pawn __1)
    {
        foreach(var gizmo in __result)
        {
            if(Multiplayer.InSession&&gizmo is Command_Toggle toggle&&toggle.toggleAction?.Method.DeclaringType?.Assembly==compType!.Assembly)
            {
                var pawn=__1;
                Action request=()=>RequestToggle(pawn);
                // The actual command delegate must stay on this game. Its
                // explicit API request is the one and only gameplay path.
                if(localActions.Add(request.Method))
                {
                    string? result=Multiplayer.RegisterLocalMethod(request.Method);
                    if(result?.StartsWith("installed|",StringComparison.Ordinal)!=true)
                        throw new InvalidOperationException("Local command declaration failed: "+result);
                }
                toggle.toggleAction=request;
                string refusal=Multiplayer.ControlRefusal(Multiplayer.LocalPlayer,pawn);
                if(refusal.Length!=0)toggle.Disable(refusal);
            }
            yield return gizmo;
        }
    }
    static Faction PlayerFaction(FactionManager manager,ThingComp comp)=>Multiplayer.InSession&&comp.parent is Pawn {Faction:{IsPlayer:true} faction}?faction:manager.OfPlayer;
    static IEnumerable<CodeInstruction> PlayerCheck(IEnumerable<CodeInstruction> instructions)
    {
        var rows=instructions.Select(i=>new CodeInstruction(i)).ToList();
        var player=AccessTools.PropertyGetter(typeof(FactionManager),"OfPlayer");
        var places=rows.Select((row,index)=>(row,index)).Where(x=>x.row.Calls(player)).Select(x=>x.index).ToArray();
        if(places.Length!=1)throw new InvalidOperationException("RunAndGun player-faction check changed");
        int at=places[0];
        var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(rows[at].labels);load.blocks.AddRange(rows[at].blocks);
        rows[at].labels.Clear();rows[at].blocks.Clear();
        rows[at]=new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(Startup),nameof(PlayerFaction)));
        rows.Insert(at,load);
        return rows;
    }
}
