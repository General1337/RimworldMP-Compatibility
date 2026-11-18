using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimworldMultiplayer.Api;
using UnityEngine;
using Verse;

namespace PerspectiveShiftMultiplayer;

// Perspective Shift asks the inhabited colonist's player to answer a romantic
// advance or a marriage proposal in its own window. For a player's colonist
// the window opens on that player's screen, not the host's: the host sends the
// question, the player's answer comes back as an action and the host applies
// it. Nobody is paused while they decide; after 30 seconds, a disconnect or a
// release the host goes with their gut. The host's own colonist keeps its
// window when a player's colonist makes the advance.
internal static class Choices
{
    static Type? dialog;
    static FieldInfo? skip, forced;
    static MethodInfo? execute;
    static ConstructorInfo? make;
    internal static bool On => dialog != null;
    internal static string? Action;
    const string Ask = "psmp.choice", Drop = "psmp.choice-close";
    const double Wait = 30;
    static double Now => Time.realtimeSinceStartup;
    static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> trackerPawn = AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");

    sealed class Pending { internal int Id, Slot; internal Pawn Initiator = null!, Recipient = null!; internal InteractionDef Def = null!; internal double Until; }
    static readonly Dictionary<int, Pending> pending = new();
    static int next = 1;
    // Player games: the windows this add-on opened, by the host's question.
    static readonly Dictionary<Window, int> open = new();
    // Development counters (read with a reflection probe).
    internal static int Asked, Answered, TimedOut; internal static string Last = "";

    internal static bool Bind()
    {
        dialog = Ps.Assembly.GetType("PerspectiveShift.Dialog_InteractionChoice", false);
        skip = AccessTools.Field(Ps.State, "skipDialog");
        forced = AccessTools.Field(Ps.State, "forcedInteraction");
        execute = dialog == null ? null : AccessTools.Method(dialog, "Execute");
        make = dialog == null ? null : AccessTools.Constructor(dialog, new[] { typeof(Pawn), typeof(Pawn), typeof(InteractionDef) });
        if (dialog != null && skip != null && forced != null && execute != null && make != null) return true;
        dialog = null;
        Log.Warning("[Perspective Shift - Multiplayer] Romance choices for players are off: this Perspective Shift version's choice window is not the one this add-on knows.");
        return false;
    }

    internal static void Patch(Harmony harmony)
    {
        if (!On) return;
        try
        {
            // Before Perspective Shift's own prefix, which opens the window on this screen.
            harmony.Patch(AccessTools.Method(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith)),
                prefix: new HarmonyMethod(typeof(Choices), nameof(Prefix)) { priority = Priority.High });
            harmony.Patch(execute, prefix: new HarmonyMethod(typeof(Choices), nameof(AnswerPrefix)) { priority = Priority.First });
        }
        catch (Exception e) { dialog = null; Log.Warning("[Perspective Shift - Multiplayer] Romance choices for players are off: " + e.Message); }
    }

    internal static void Register()
    {
        if (!On) return;
        Action = Multiplayer.RegisterAction(typeof(Choices), nameof(ActChoice));
        Multiplayer.RegisterPlayerHandler(Ask, OnAsk);
        Multiplayer.RegisterPlayerHandler(Drop, OnDrop);
    }

    internal static void Reset() { pending.Clear(); open.Clear(); }

    static bool Skipping => (bool)skip!.GetValue(null)!;

    // ---- host ----
    static bool Prefix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, ref bool __result)
    {
        if (!HostSide.Active || recipient == null || Skipping || intDef != InteractionDefOf.RomanceAttempt && intDef != InteractionDefOf.MarriageProposal) return true;
        var initiator = trackerPawn(__instance);
        if (initiator == null || !__instance.CanInteractNowWith(recipient, intDef)) return true;
        if (HostSide.RemoteOf(recipient) is { Slot: >= 0 } r)
        {
            // One question per pair at a time; the asker waits for the answer.
            if (pending.Values.Any(p => p.Recipient == recipient || p.Initiator == initiator)) { __result = false; return false; }
            var ask = new Pending { Id = next++, Slot = r.Slot, Initiator = initiator, Recipient = recipient, Def = intDef, Until = Now + Wait };
            if (Action == null || !Multiplayer.SendToPlayer(r.Slot, Ask, ask.Id + ";" + initiator.thingIDNumber + ";" + recipient.thingIDNumber + ";" + intDef.defName)) return true;
            pending[ask.Id] = ask; Asked++; Last = "asked " + r.Slot + " #" + ask.Id;
            __result = true;
            return false;
        }
        // The host's own colonist, approached by a player's colonist: that
        // colonist is in Perspective Shift's slot for the call, so its own
        // check would miss the host's.
        var own = Ps.Own;
        if (own != null && Ps.PawnOf(own) == recipient && !ReferenceEquals(Ps.Current, own))
        {
            var remote = Remote.Current; Remote.Current = null;
            var previous = Ps.Push(own, "choice");
            try { Find.WindowStack.Add((Window)make!.Invoke(new object[] { initiator, recipient, intDef })); }
            finally { Ps.Pop(previous); Remote.Current = remote; }
            __result = true;
            return false;
        }
        return true;
    }

    // A player's answer (0 go with your gut, 1 accept, 2 refuse).
    internal static void ActChoice(int id, int outcome)
    {
        int slot = Multiplayer.CurrentPlayer;
        if (!HostSide.Active || !pending.TryGetValue(id, out var ask) || ask.Slot != slot) { Last = "answer refused for " + slot + " #" + id; return; }
        pending.Remove(id); Answered++;
        Resolve(ask, Mathf.Clamp(outcome, 0, 2));
    }

    static void Resolve(Pending ask, int outcome)
    {
        var (a, b) = (ask.Initiator, ask.Recipient);
        if (a is not { Spawned: true, Dead: false } || b is not { Spawned: true, Dead: false } || a.MapHeld != b.MapHeld) { Last = "#" + ask.Id + " gone"; return; }
        bool wasSkip = Skipping; var wasForced = forced!.GetValue(null);
        skip!.SetValue(null, true); forced.SetValue(null, Enum.ToObject(forced.FieldType, outcome));
        bool done;
        try { done = a.interactions.TryInteractWith(b, ask.Def); }
        finally { skip.SetValue(null, wasSkip); forced.SetValue(null, wasForced); }
        Last = "#" + ask.Id + " outcome " + outcome + (done ? " applied" : " not possible now");
        if (!done && ask.Slot >= 0) Multiplayer.AsPlayer(ask.Slot, () => Messages.Message("PSMP_ChoiceTooLate".Translate(a.LabelShort), MessageTypeDefOf.RejectInput, false));
    }

    // Every frame: questions nobody can answer any more get the gut answer.
    internal static void Frame()
    {
        if (pending.Count == 0) return;
        foreach (var ask in pending.Values.ToArray())
        {
            bool mine = HostSide.Remotes.TryGetValue(ask.Slot, out var r) && r.Pawn == ask.Recipient;
            if (mine && Now < ask.Until) continue;
            pending.Remove(ask.Id); TimedOut++;
            Multiplayer.SendToPlayer(ask.Slot, Drop, ask.Id.ToString());
            Resolve(ask, 0);
        }
    }

    // ---- player ----
    static void OnAsk(string text)
    {
        if (!HostSide.IsPlayerGame || !On) return;
        var f = text.Split(';');
        if (f.Length < 4 || !int.TryParse(f[0], out int id) || !int.TryParse(f[1], out int a) || !int.TryParse(f[2], out int b)) return;
        var def = DefDatabase<InteractionDef>.GetNamedSilentFail(f[3]);
        if (def == null || PlayerSide.FindPawn(a) is not { } initiator || PlayerSide.FindPawn(b) is not { } recipient) return;
        var window = (Window)make!.Invoke(new object[] { initiator, recipient, def });
        // The host doesn't pause for it; neither does this game.
        window.forcePause = false;
        open[window] = id;
        Find.WindowStack.Add(window);
    }
    static void OnDrop(string text)
    {
        if (!HostSide.IsPlayerGame || !int.TryParse(text, out int id)) return;
        foreach (var w in open.Where(kv => kv.Value == id).Select(kv => kv.Key).ToArray()) { open.Remove(w); w.Close(false); }
    }
    // The player's click in the window: an answer to the host, nothing here.
    static bool AnswerPrefix(Window __instance, object outcome)
    {
        if (!HostSide.IsPlayerGame) return true;
        if (open.TryGetValue(__instance, out int id))
        {
            open.Remove(__instance);
            if (Action != null) Multiplayer.RequestAction(Action, 1, new object?[] { id, Convert.ToInt32(outcome) }, (status, _, why) => Last = "answer " + status + " " + why);
        }
        __instance.Close();
        return false;
    }
}
