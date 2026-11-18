using RimworldMultiplayer.Api;
using Verse;

// Build against the normal RimworldMultiplayer.Api library; the target mod need
// not be a compile-time dependency. Install this small adapter on host and guest.
[StaticConstructorOnStartup]
public static class AnimalGeneticsCompat
{
    static AnimalGeneticsCompat()
    {
        Multiplayer.RegisterCompatibilityFor(typeof(AnimalGeneticsCompat),"Mlie.AnimalGenetics");
        {
            var owner=GenTypes.GetTypeInAnyAssembly("AnimalGenetics.GeneticInformation");
            if(owner!=null)
            {
                Log.Message("Animal Genetics saved cache: "+Multiplayer.RegisterSavedFieldInitializer(owner,"_geneRecords","GeneRecords"));
                Log.Message("Animal Genetics saved identity: "+Multiplayer.RegisterSavedIdentity(owner,"_loadId","loadID",""));
                Log.Message("Animal Genetics saved weak registry: "+Multiplayer.RegisterSavedRegistryOwner(owner,"_instances",GenTypes.GetTypeInAnyAssembly("AnimalGenetics.AnimalGenetics")));
            }
        }
    }
}
