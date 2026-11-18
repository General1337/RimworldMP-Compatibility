using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimworldMultiplayer.Api;
using Verse;

namespace PsycastTravelMultiplayer;

// This example uses only the public library, game/Harmony references and the
// target mod's public metadata. No reference to Rimworld Multiplayer's implementation assembly.
[StaticConstructorOnStartup]
public static class Startup
{
    static MethodInfo? passengers;
    static MethodInfo? canHit;
    static FieldInfo? casterField;
    static PropertyInfo? casterProperty;
    static Pawn? Caster(object ability)=>(casterField?.GetValue(ability)??casterProperty?.GetValue(ability)) as Pawn;
    static Startup()
    {
        if (!Multiplayer.Available) return;
        var type=GenTypes.GetTypeInAnyAssembly("VanillaPsycastsExpanded.Skipmaster.Ability_WorldTeleport");
        if (type==null) return;
        if (!Multiplayer.Supports(nameof(Multiplayer.TravelRefusal)) || !Multiplayer.Supports(nameof(Multiplayer.CanTravelTogether)))
        { Log.Error("[PsycastTravelMultiplayer] Allied travel needs Rimworld Multiplayer's travel queries."); return; }
        passengers=AccessTools.Method(type,"PawnsToSkip",Type.EmptyTypes);
        var beacon=AccessTools.Method(type,"AlliedPawnOnMap",new[]{typeof(Map)});
        canHit=AccessTools.Method(type,"CanHitTargetTile",new[]{typeof(GlobalTargetInfo)});
        var cast=AccessTools.Method(type,"Cast",new[]{typeof(GlobalTargetInfo[])});
        casterField=AccessTools.Field(type,"pawn");casterProperty=AccessTools.Property(type,"pawn");
        if (passengers?.ReturnType!=typeof(IEnumerable<Pawn>) || beacon?.ReturnType!=typeof(Pawn) ||
            canHit?.ReturnType!=typeof(bool) || cast?.ReturnType!=typeof(void) || cast.DeclaringType!=type || casterField==null&&casterProperty==null)
        { Log.Error("[PsycastTravelMultiplayer] Target methods changed; compatibility hooks were not installed."); return; }
        var harmony=new Harmony("rimworldmultiplayer.psycasttravel");
        harmony.Patch(passengers,postfix:new HarmonyMethod(typeof(Startup),nameof(Companions)));
        harmony.Patch(beacon,prefix:new HarmonyMethod(typeof(Startup),nameof(Beacon)));
        harmony.Patch(cast,prefix:new HarmonyMethod(typeof(Startup),nameof(BeforeCast)));
        Log.Message("[PsycastTravelMultiplayer] Allied destination and companion hooks installed.");
    }
    static bool BeforeCast(object __instance,GlobalTargetInfo[] targets)
    {
        if(!Multiplayer.InSession)return true;
        if(!Multiplayer.IsHost)return false;
        // The original Cast falls back to making a caravan if its beacon
        // vanishes. Recheck the original target predicate after warmup,
        // before it moves pawns or base.Cast charges resources/cooldown.
        var caster=Caster(__instance);
        if(caster!=null&&targets is {Length:1}&&(bool)canHit!.Invoke(__instance,new object[]{targets[0]})!)return true;
        if(caster!=null)Messages.Message("CannotHitTarget".Translate(),caster,MessageTypeDefOf.RejectInput,false);
        return false;
    }
    static bool Beacon(object __instance,Map __0,ref Pawn? __result)
    {
        if (!Multiplayer.InSession || Caster(__instance) is not {} caster) return true;
        var leaving=((IEnumerable<Pawn>)passengers!.Invoke(__instance,null)!).ToHashSet();
        __result=__0.mapPawns.AllPawnsSpawned.FirstOrDefault(p=>!p.NonHumanlikeOrWildMan()&&p.IsColonist&&p.HomeFaction is {IsPlayer:true}&&
            !leaving.Contains(p)&&Multiplayer.TravelRefusal(caster,p).Length==0);
        return false;
    }
    static IEnumerable<Pawn> Companions(IEnumerable<Pawn> __result,object __instance)
    {
        if (!Multiplayer.InSession || Caster(__instance) is not {} caster) return __result;
        return __result.Where(p=>Multiplayer.CanTravelTogether(caster,p));
    }
}
