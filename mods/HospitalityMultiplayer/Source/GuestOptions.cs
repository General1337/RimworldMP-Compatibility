using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimworldMultiplayer.Api;
using Verse;

namespace HospitalityMultiplayer;

[StaticConstructorOnStartup]
internal static class GuestOptions
{
    private static readonly Type? GuestType = GenTypes.GetTypeInAnyAssembly("Hospitality.CompGuest");
    private static readonly FieldInfo? Entertain = GuestType?.GetField("entertain");
    private static readonly FieldInfo? MakeFriends = GuestType?.GetField("makeFriends");
    private static readonly FieldInfo? VisitorGroup = GuestType is null ? null : AccessTools.Field(GuestType, "lord");
    private static readonly string? ActionId;

    static GuestOptions()
    {
        if (!Multiplayer.Available || GuestType is null)
            return;

        var tab = GuestType.Assembly.GetType("Hospitality.ITab_Pawn_Guest");
        var draw = tab is null ? null : AccessTools.DeclaredMethod(tab, "FillTabGuest", new[] { typeof(UnityEngine.Rect) });
        if (draw is null || Entertain?.FieldType != typeof(bool) || MakeFriends?.FieldType != typeof(bool) ||
            VisitorGroup is null)
        {
            Log.Error("[HospitalityMultiplayer] The guest tab or guest option fields have changed.");
            return;
        }

        var writes = PatchProcessor.GetOriginalInstructions(draw);
        if (writes.Count(instruction => instruction.StoresField(Entertain)) != 1 ||
            writes.Count(instruction => instruction.StoresField(MakeFriends)) != 1)
        {
            Log.Error("[HospitalityMultiplayer] The guest tab no longer writes each option once.");
            return;
        }

        ActionId = Multiplayer.RegisterAction(typeof(GuestOptions), nameof(SetOption), policy: ActionPolicy.CancelNullArguments);
        if (string.IsNullOrEmpty(ActionId))
        {
            Log.Error("[HospitalityMultiplayer] The guest option action could not be registered.");
            return;
        }

        Multiplayer.RegisterCompatibilityFor(typeof(GuestOptions), "orion.hospitality");
        new Harmony("rimworldmultiplayer.hospitality").Patch(draw,
            transpiler: new HarmonyMethod(typeof(GuestOptions), nameof(RouteWrites)));
    }

    private static IEnumerable<CodeInstruction> RouteWrites(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            var replacement = instruction.StoresField(Entertain) ? nameof(WriteEntertain)
                : instruction.StoresField(MakeFriends) ? nameof(WriteMakeFriends) : null;
            if (replacement is null)
            {
                yield return instruction;
                continue;
            }

            yield return new CodeInstruction(instruction)
            {
                opcode = OpCodes.Call,
                operand = AccessTools.DeclaredMethod(typeof(GuestOptions), replacement)
            };
        }
    }

    private static void WriteEntertain(ThingComp guest, bool value) => Write(guest, Entertain!, true, value);
    private static void WriteMakeFriends(ThingComp guest, bool value) => Write(guest, MakeFriends!, false, value);

    private static void Write(ThingComp guest, FieldInfo field, bool entertain, bool value)
    {
        if ((bool)field.GetValue(guest)! == value)
            return;
        if (!Multiplayer.InSession || Multiplayer.IsHost)
        {
            field.SetValue(guest, value);
            return;
        }

        if (guest.parent is Pawn pawn)
            Multiplayer.RequestAction(ActionId!, 1, new object?[] { pawn, entertain, value }, (status, _, reason) =>
            {
                if (status is ActionStatus.Rejected or ActionStatus.Faulted)
                    Log.Warning("[HospitalityMultiplayer] Guest option was refused: " + reason);
            });
    }

    private static void SetOption(Pawn pawn, bool entertain, bool value)
    {
        var refusal = Multiplayer.ControlRefusal(Multiplayer.CurrentPlayer, pawn);
        if (refusal.Length != 0)
            throw new InvalidOperationException(refusal);

        var guest = pawn.AllComps.FirstOrDefault(GuestType!.IsInstanceOfType);
        if (!pawn.Spawned || pawn.Dead || pawn.Faction?.IsPlayer != false ||
            guest is null || VisitorGroup!.GetValue(guest) is null)
            throw new InvalidOperationException("The pawn is no longer a visiting guest.");

        (entertain ? Entertain! : MakeFriends!).SetValue(guest, value);
    }
}
