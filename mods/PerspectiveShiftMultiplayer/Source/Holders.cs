using System.Globalization;
using System.Text;
using RimworldMultiplayer.Api;
using Verse;

namespace PerspectiveShiftMultiplayer;

// Who plays which colonist. The host owns the table and sends it to every
// game; players draw from their copy (labels, disabled buttons, their own
// inhabited colonist). Saved with the game by player key, never by slot.
internal sealed class Holder
{
    internal int PawnId, Slot;
    internal string Key = "", Name = "";
    // A disconnected player's colonist stays theirs (drafted) until then
    // (real time); 0 while connected.
    internal double GraceUntil;
    internal bool Waiting => GraceUntil > 0;
}

internal static class Holders
{
    internal static readonly List<Holder> All = new();
    // Authentic mode: each player's one character, and whether it died.
    internal static readonly Dictionary<string, int> AuthenticByKey = new();
    internal static readonly HashSet<string> DeadKeys = new();
    // Player games: the host's playstyle and own authentic character.
    internal static int MyAuthentic = -1;
    internal static bool MyDead;

    internal static Holder? OfPawn(Pawn? pawn) => pawn == null ? null : All.FirstOrDefault(h => h.PawnId == pawn.thingIDNumber);
    internal static Holder? OfSlot(int slot) => All.FirstOrDefault(h => h.Slot == slot && !h.Waiting);

    // The control veto: someone else's character takes no orders from
    // you; the host can release it with its own button.
    internal static string? Veto(int player, object thing)
    {
        if (thing is not Pawn pawn || OfPawn(pawn) is not { } holder || holder.Slot == player && !holder.Waiting) return null;
        return "PSMP_ControlledBy".Translate(holder.Name).Resolve();
    }

    // ---- wire: host to every game ----
    internal static string Encode(Func<int, (int Authentic, bool Dead)> perSlot)
    {
        var b = new StringBuilder();
        b.Append(Ps.ModeValue).Append(';').Append(Ps.AllowDirector ? 1 : 0).Append('\n');
        foreach (var h in All)
            b.Append("h,").Append(h.PawnId).Append(',').Append(h.Slot).Append(',').Append(h.Waiting ? 1 : 0).Append(',').Append(Escape(h.Name)).Append('\n');
        foreach (var p in Multiplayer.Players)
        {
            var (authentic, dead) = perSlot(p.Slot);
            if (authentic >= 0 || dead) b.Append("a,").Append(p.Slot).Append(',').Append(authentic).Append(',').Append(dead ? 1 : 0).Append('\n');
        }
        return b.ToString();
    }
    static string Escape(string s) => s.Replace(",", " ").Replace("\n", " ");

    // Player games: the host's table.
    internal static void Decode(string text, int me)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0) return;
        var head = lines[0].Split(';');
        if (head.Length >= 2 && int.TryParse(head[0], out int mode)) { Ps.ModeValue = mode; Ps.AllowDirector = head[1] == "1"; }
        All.Clear(); MyAuthentic = -1; MyDead = false;
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            if (f.Length >= 5 && f[0] == "h" && int.TryParse(f[1], out int pawn) && int.TryParse(f[2], out int slot))
                All.Add(new Holder { PawnId = pawn, Slot = slot, GraceUntil = f[3] == "1" ? 1 : 0, Name = f[4] });
            else if (f.Length >= 4 && f[0] == "a" && int.TryParse(f[1], out int s) && s == me)
            { int.TryParse(f[2], out MyAuthentic); MyDead = f[3] == "1"; }
        }
    }

    internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    internal static float P(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && !float.IsNaN(v) && !float.IsInfinity(v) ? v : 0f;
}

// Saved on the host: holders by key, so a player who comes back to this world
// gets their colonist back; and the Authentic characters.
public sealed class HoldersComponent : GameComponent
{
    public HoldersComponent(Game game) { }
    List<string> rows = new();
    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            rows = Holders.All.Where(h => h.Key.Length > 0).Select(h => "h|" + h.PawnId + "|" + h.Key + "|" + h.Name.Replace("|", " ")).ToList();
            rows.AddRange(Holders.AuthenticByKey.Select(a => "a|" + a.Value + "|" + a.Key + "|" + (Holders.DeadKeys.Contains(a.Key) ? 1 : 0)));
            rows.AddRange(Holders.DeadKeys.Where(k => !Holders.AuthenticByKey.ContainsKey(k)).Select(k => "a|-1|" + k + "|1"));
        }
        Scribe_Collections.Look(ref rows, "psmpHolders", LookMode.Value);
        rows ??= new();
        if (Scribe.mode == LoadSaveMode.PostLoadInit && Multiplayer.IsHost && !HostSide.IsPlayerGame)
            HostSide.Loaded(rows);
    }
}
