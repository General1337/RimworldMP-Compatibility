using System;
using System.Collections.Generic;
using System.Reflection;

namespace RimworldMultiplayer.Api
{
    /// <summary>How a declared action may be used.</summary>
    [Flags]
    public enum ActionPolicy
    {
        None = 0,
        /// <summary>Only the host may run it.</summary>
        HostOnly = 1,
        /// <summary>Only in RimWorld's developer mode.</summary>
        DebugOnly = 2,
        /// <summary>Cancelled when nothing is selected on the map.</summary>
        RequireSelection = 4,
        /// <summary>Cancelled when an argument is null.</summary>
        CancelNullArguments = 8,
    }

    /// <summary>A player in the game. Never carries an identity, Steam ID or address.</summary>
    public sealed class MpPlayer
    {
        /// <summary>The player's slot; the host is 0. Stable for the session, not across sessions: use <see
        /// cref="Multiplayer.PlayerKey"/> to save.</summary>
        public int Slot { get; }
        /// <summary>The name the player chose.</summary>
        public string Name { get; }
        /// <summary>The player's color index (-1: none).</summary>
        public int Color { get; }
        /// <summary>True for the host.</summary>
        public bool IsHost { get; }
        /// <summary>True for a spectator, who watches and may not change the world.</summary>
        public bool IsSpectator { get; }
        /// <summary>True while connected.</summary>
        public bool Connected { get; }
        /// <summary>The player's colony in separate colonies (0: shared colony).</summary>
        public int Colony { get; }
        internal MpPlayer(int slot, string name, int color, bool host, bool spectator, bool connected, int colony)
        { Slot = slot; Name = name; Color = color; IsHost = host; IsSpectator = spectator; Connected = connected; Colony = colony; }
        /// <inheritdoc/>
        public override string ToString() => Name + " (" + Slot + ")";
    }

    /// <summary>The outcome of an action request.</summary>
    public enum ActionStatus { Pending, Completed, Rejected, Faulted, Uncertain }

    /// <summary> Make a mod work in Rimworld Multiplayer. Ship this library with your mod and call these from
    /// your mod's constructor or a [StaticConstructorOnStartup] class. Without the multiplayer mod every call
    /// does nothing, so the same build works in single player. In multiplayer only the host simulates the
    /// world; players' games show the host's world. Most mods work unchanged. Declare what the multiplayer
    /// mod cannot see on its own: a method your UI calls that changes the world (an action), and static
    /// fields whose host value players need (shared state). </summary>
    public static class Multiplayer
    {
        /// <summary>The API version this library was built for.</summary>
        public const int ApiVersion = 6;

        /// <summary>Whether the loaded host API actually has a public member.</summary>
        public static bool Supports(string member) => !string.IsNullOrEmpty(member) && member.Length <= 128 && Api is Type type &&
            Array.Exists(type.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == member);

        /// <summary>Why a pawn cannot be a travel beacon for the caster. This is destination permission, not
        /// caster control or native eligibility.</summary>
        public static string TravelRefusal(object caster, object beacon) => !Available ? "" :
            Supports(nameof(TravelRefusal)) ? Call(nameof(TravelRefusal), caster, beacon) as string ?? "Travel permission unavailable" : "Allied travel is unavailable";

        /// <summary>Filters native-eligible travel companions so foreign pawns standing nearby are not taken.
        /// No multiplayer: preserves native behavior.</summary>
        public static bool CanTravelTogether(object caster, object passenger) => !Available ||
            Supports(nameof(CanTravelTogether)) && Call(nameof(CanTravelTogether), caster, passenger) is true;

        /// <summary>Declares your verb type (and derived types) whose IDs differ on each game. The host
        /// resolves the existing verb under its caster, never a client-created copy. Native tracker IDs are
        /// preserved. Returns status|reason; null without multiplayer.</summary>
        public static string? RegisterRoutedVerb(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!Available) return null;
            if (!Has(6) || !Supports(nameof(RegisterRoutedVerb))) return "refused|Host lacks RegisterRoutedVerb";
            return Call("RegisterRoutedVerb", type) as string;
        }

        const string ApiType = "RimworldMultiplayer.Compat.CompatApi";
        // Only RimWorld Multiplayer's own assembly answers: another mod defining a type of the
        // same name can't receive the declarations (and functions) mods pass to this library.
        const string ApiAssembly = "ColonyTogether.Prototype";
        static Type? api;
        static readonly Dictionary<string, MethodInfo?> methods = new Dictionary<string, MethodInfo?>();

        // Found by name each call until found, so the order mods load in does not matter.
        static Type? Api
        {
            get
            {
                if (api != null) return api;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t;
                    try { if (assembly.GetName().Name != ApiAssembly) continue; t = assembly.GetType(ApiType, false); } catch { continue; }
                    if (t != null) { api = t; break; }
                }
                return api;
            }
        }

        static object? Call(string name, params object?[] args)
        {
            var type = Api;
            if (type == null) return null;
            MethodInfo? method;
            lock (methods)
                if (!methods.TryGetValue(name, out method))
                    methods[name] = method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            if (method == null) throw new NotSupportedException($"Rimworld Multiplayer has no {name}.");
            try { return method.Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        /// <summary>True when Rimworld Multiplayer is loaded.</summary>
        public static bool Available => Api != null;

        /// <summary>The API version the loaded Rimworld Multiplayer offers (0 when not loaded).</summary>
        public static int HostApiVersion => Api?.GetField("Version")?.GetValue(null) is int v ? v : 0;

        /// <summary>True in a multiplayer game.</summary>
        public static bool InSession => Call("InSession") is bool b && b;

        /// <summary>True on the host, and always in single player.</summary>
        public static bool IsHost => !(Call("IsHost") is bool b) || b;

        /// <summary> Declares a static method as an action: when a player calls it, the call goes to the
        /// host, which runs it for everyone. Arguments must be things the multiplayer mod can send: things,
        /// pawns, maps, defs, numbers, strings, enums and lists of these. Returns the action id (null without
        /// Rimworld Multiplayer). Throws ArgumentException with the reason when the method cannot be an
        /// action. </summary>
        public static string? RegisterAction(MethodInfo method, string name, int version = 1, ActionPolicy policy = ActionPolicy.None)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return Call("RegisterAction", method, name, version, (int)policy) as string;
        }

        /// <summary>Declares <paramref name="type"/>.<paramref name="methodName"/> (a static method) as an
        /// action named after the method.</summary>
        public static string? RegisterAction(Type type, string methodName, int version = 1, ActionPolicy policy = ActionPolicy.None)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new ArgumentException($"{type.FullName} has no static method {methodName}");
            return RegisterAction(method, methodName, version, policy);
        }

        /// <summary> Declares a method your UI calls that changes the world (static or instance, returning
        /// void): on a player's game the call is sent to the host instead of run, and the host runs it with
        /// its own objects. This is the usual way to make a mod's buttons work for players. Returns
        /// "status|reason" (status installed, unsupported, conflict or failed), or null without Rimworld
        /// Multiplayer. </summary>
        public static string? RegisterSyncMethod(MethodInfo method, ActionPolicy policy = ActionPolicy.None)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return Call("RegisterSyncMethod", method, (int)policy) as string;
        }

        /// <summary>Declares <paramref name="type"/>.<paramref name="methodName"/> as a sync method (see the
        /// other overload). Overloads need the MethodInfo form.</summary>
        public static string? RegisterSyncMethod(Type type, string methodName, ActionPolicy policy = ActionPolicy.None)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new ArgumentException($"{type.FullName} has no method {methodName}");
            return RegisterSyncMethod(method, policy);
        }

        /// <summary> Declares a method that runs on each player's own game: it changes only that player's
        /// screen or own state (a view, a camera, a filter, a panel). A player's button or menu option whose
        /// code calls it, and otherwise only reads, draws, selects or moves the camera, runs on that player's
        /// game instead of the host. Returns "status|reason", or null without it. </summary>
        public static string? RegisterLocalMethod(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return Call("RegisterLocalMethod", method) as string;
        }

        /// <summary>Declares <paramref name="type"/>.<paramref name="methodName"/> as a local method (see the
        /// other overload).</summary>
        public static string? RegisterLocalMethod(Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new ArgumentException($"{type.FullName} has no method {methodName}");
            return RegisterLocalMethod(method);
        }

        /// <summary> Declares an instance field of your type that belongs to each player: every game keeps
        /// its own value and the host's is never copied over it (a viewed level, a chosen tab). Not for
        /// gameplay state. Returns "status|reason", or null without it. </summary>
        public static string? RegisterLocalField(Type owner, string field) => Call("RegisterLocalField", owner, field) as string;

        /// <summary> Declares your game, world or map component as each player's own presentation (a camera
        /// follower, a view helper): its tick runs on every player's game and its buttons on the pressing
        /// player's game. Gameplay calls inside it still stop on players' games; a tick that fails or keeps
        /// running long is switched off. A component that saves fields is refused; declare its view fields
        /// with <see cref="RegisterLocalField"/>. Returns "status|reason"; null without multiplayer.
        /// </summary>
        public static string? RegisterLocalComponent(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!Available) return null;
            if (!Has(6) || !Supports(nameof(RegisterLocalComponent))) return "refused|Local components need a host with RegisterLocalComponent";
            return Call(nameof(RegisterLocalComponent), type) as string;
        }

        /// <summary> Declares unsaved fields of your map component as a cache each game builds for itself
        /// from the world (lit cells, water cells, a lookup grid): the host's values are never copied over
        /// them. On a player's game, <paramref name="rebuild"/> runs with the component once the host's
        /// world, or a map the host added, has finished loading, and <paramref name="cellChanged"/> runs with
        /// the component and a cell's x and z when that cell's terrain changes. Either may be null. A field
        /// the component saves is refused. Returns "status|reason"; null without multiplayer. </summary>
        public static string? RegisterLocalCache(Type owner, string[] fields, Action<object>? rebuild = null, Action<object, int, int>? cellChanged = null)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            if (!Available) return null;
            if (!Has(6) || !Supports(nameof(RegisterLocalCache))) return "refused|Local caches need a host with RegisterLocalCache";
            return Call(nameof(RegisterLocalCache), owner, fields, rebuild, cellChanged) as string;
        }

        /// <summary> For one exact type of yours (a thing, comp, or game, world or map component), callbacks
        /// around the moment a player's game loads the host's saved data into an object of that type. Your
        /// load code (PostLoadInit, FinalizeInit, LoadedGame) does not run again there, so derived data built
        /// from saved fields (a static index, a lookup table) would keep the player's old answer. <paramref
        /// name="beforeApply"/> runs right before the load, while the object holds the player's previous
        /// values: release what the load replaces. <paramref name="afterApply"/> runs right after the load
        /// (before the object is spawned again when the reload moved it), and also for an object the host's
        /// data created: rebuild derived data from the host's values. When beforeApply ran, afterApply always
        /// runs, also when the load failed. Either may be null, not both. Neither runs on the host or in
        /// single player. Only rebuild derived data. A callback that throws three times, or keeps taking
        /// longer than a few milliseconds, is switched off for the session. Returns "status|reason"; null
        /// without multiplayer. </summary>
        public static string? RegisterApplyCallbacks(Type type, Action<object>? beforeApply = null, Action<object>? afterApply = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (beforeApply == null && afterApply == null) throw new ArgumentException("Give a before or an after callback");
            if (!Available) return null;
            if (!Has(6) || !Supports(nameof(RegisterApplyCallbacks))) return "refused|Apply callbacks need a host with RegisterApplyCallbacks";
            return Call(nameof(RegisterApplyCallbacks), type, beforeApply, afterApply) as string;
        }

        /// <summary> Declares an instance field (number, flag, choice, text or def) of a thing, comp, or map,
        /// world or game component that your windows or inspect tabs edit directly in their drawing code. A
        /// player's edit is sent to the host, which sets the field on the same object. Returns
        /// "status|reason", or null without Rimworld Multiplayer. </summary>
        public static string? RegisterSyncField(Type owner, string field) => Call("RegisterSyncField", owner, field) as string;

        /// <summary> Declares an instance field players need with the host's value even though it is not
        /// saved, overriding any automatic rule that kept it per player. </summary>
        public static string? RegisterHostField(Type owner, string field) => Call("RegisterHostField", owner, field) as string;

        /// <summary>Prepares a null saved cache through its caching property before multiplayer capture. The
        /// initializer runs only on the host; existing references are preserved.</summary>
        public static string? RegisterSavedFieldInitializer(Type owner,string field,string property) => !Available ? null : Supports(nameof(RegisterSavedFieldInitializer)) ? Call("RegisterSavedFieldInitializer",owner,field,property) as string : "refused|Host lacks RegisterSavedFieldInitializer";
        /// <summary>Declares the exact saved identity field, XML label and literal prefix used by a mod
        /// record. Native reuse preserves references during incremental loads.</summary>
        public static string? RegisterSavedIdentity(Type owner,string field,string label,string prefix) => !Available ? null : Supports(nameof(RegisterSavedIdentity)) ? Call("RegisterSavedIdentity",owner,field,label,prefix) as string : "refused|Host lacks RegisterSavedIdentity";
        /// <summary>Restores only the declared native weak-list registration for a reused record whose
        /// constructor is skipped.</summary>
        public static string? RegisterSavedWeakRegistry(Type owner,string field) => !Available ? null : Supports(nameof(RegisterSavedWeakRegistry)) ? Call("RegisterSavedWeakRegistry",owner,field) as string : "refused|Host lacks RegisterSavedWeakRegistry";
        /// <summary>explicit full-registry Game/World snapshot owner for record admission and replica
        /// lifetime.</summary>
        public static string? RegisterSavedRegistryOwner(Type owner,string field,Type snapshotOwner) => !Available ? null : Supports(nameof(RegisterSavedRegistryOwner)) ? Call("RegisterSavedRegistryOwner",owner,field,snapshotOwner) as string : "refused|Host lacks RegisterSavedRegistryOwner";

        /// <summary> Declares a method of yours that must never run on a player's game: code that assumes it
        /// runs the world (replies, jobs, caches only the host builds). On a player the call is skipped (a
        /// method that returns something returns its type's default); the host runs it as usual. Returns
        /// "status|reason", or null without Rimworld Multiplayer. </summary>
        public static string? RegisterHostOnlyMethod(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return Call("RegisterHostOnlyMethod", method) as string;
        }

        /// <summary>Declares <paramref name="type"/>.<paramref name="methodName"/> as host-only (see the
        /// other overload).</summary>
        public static string? RegisterHostOnlyMethod(Type type, string methodName)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new ArgumentException($"{type.FullName} has no method {methodName}");
            return RegisterHostOnlyMethod(method);
        }

        /// <summary> Declares which colony owns your thing (building, item or pawn of <paramref name="type"/>
        /// or a subtype) when its faction does not say. <paramref name="owner"/> returns a pawn or thing
        /// (whose colony owns it) or a colony faction, or null for no answer. Multiplayer's ownership checks
        /// then follow it. Returns "status|reason", or null without Rimworld Multiplayer. </summary>
        public static string? RegisterOwnerResolver(Type type, Func<object, object?> owner)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            return Call("RegisterOwnerResolver", type, owner) as string;
        }

        /// <summary> Declares that an instance field of your thing type holds what owns it: a pawn or thing
        /// (whose colony owns it) or a colony faction. </summary>
        public static string? RegisterOwnerField(Type type, string field) => Call("RegisterOwnerField", type, field) as string;

        /// <summary> Lets your sync methods take arguments of your own type. On a player <paramref
        /// name="write"/> turns the object into text (at most 16384 characters); on the host <paramref
        /// name="read"/> turns the text back into the host's object, for example by an ID. </summary>
        public static string? RegisterArgumentType(Type type, Func<object, string> write, Func<string, object> read)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (read == null) throw new ArgumentNullException(nameof(read));
            return Call("RegisterArgumentType", type, write, read) as string;
        }

        /// <summary>Asks the host to run a declared action with these arguments.</summary>
        public static void RequestAction(string id, int version, object?[] arguments, Action<ActionStatus, object?, string>? completed = null)
        {
            if (Api == null) { completed?.Invoke(ActionStatus.Rejected, null, "Rimworld Multiplayer is not loaded"); return; }
            Action<int, object?, string>? relay = completed == null ? null : (s, v, r) => completed((ActionStatus)s, v, r);
            Call("RequestAction", id, version, arguments, relay);
        }

        /// <summary>Declares static fields of plain values whose host value every player receives.</summary>
        public static void RegisterSharedStatic(Type owner, int version, params string[] fields) => Call("RegisterSharedStatic", owner, version, fields);

        /// <summary>Declares static collections or records of plain data whose host value every player
        /// receives.</summary>
        public static void RegisterSharedStaticData(Type owner, int version, params string[] fields) => Call("RegisterSharedStaticData", owner, version, fields);

        // ---- Players and control --------------------------------

        static bool Has(int version) => HostApiVersion >= version;

        /// <summary> The player slot a command, button, order or action on the host is applied for (the host
        /// is 0). On a player's game, that player. 0 without Rimworld Multiplayer. </summary>
        public static int CurrentPlayer => Has(5) && Call("CurrentPlayer") is int i ? i : 0;

        /// <summary>This game's own player slot (the host is 0; 0 in single player).</summary>
        public static int LocalPlayer => Has(5) && Call("LocalPlayer") is int i ? i : 0;

        /// <summary>Everyone in the game. Single player: one host entry.</summary>
        public static IReadOnlyList<MpPlayer> Players
        {
            get
            {
                var list = new List<MpPlayer>();
                if (Has(5) && Call("Players") is string[] rows)
                    foreach (var row in rows)
                    {
                        var f = row.Split('\t');
                        if (f.Length < 7 || !int.TryParse(f[0], out int slot)) continue;
                        int.TryParse(f[2], out int color); int.TryParse(f[6], out int colony);
                        list.Add(new MpPlayer(slot, f[1], color, f[3] == "1", f[4] == "1", f[5] == "1", colony));
                    }
                if (list.Count == 0) list.Add(new MpPlayer(0, "", -1, true, false, true, 0));
                return list;
            }
        }

        /// <summary>The player in that slot, or null.</summary>
        public static MpPlayer? Player(int slot) { foreach (var p in Players) if (p.Slot == slot) return p; return null; }

        /// <summary> A per-world key for a player, to save things per player by: the same across reconnects
        /// and reloads of this world, never the identity. The host knows every player's, a player's game only
        /// its own; "" when unknown or without Rimworld Multiplayer. </summary>
        public static string PlayerKey(int slot) => Has(5) ? Call("PlayerKey", slot) as string ?? "" : "";

        /// <summary> Why that player may not control that thing (a mod's veto, or another colony's), or ""
        /// when they may. "" without Rimworld Multiplayer or outside a multiplayer game. In a multiplayer
        /// game whose Rimworld Multiplayer cannot answer, refuses: a permission that cannot be checked is
        /// never reported as granted. </summary>
        public static string ControlRefusal(int player, object thing) => PermissionQuery("ControlRefusal", player, thing);

        const string PermissionUnavailable = "Rimworld Multiplayer cannot check this permission";

        // Single player (no multiplayer, or not in a game) keeps native behavior: allowed.
        // A session whose host lacks the query refuses instead of answering "allowed".
        static string PermissionQuery(string name, int player, object thing)
        {
            if (!Available) return "";
            if (Has(5) && Supports(name))
                try { return Call(name, player, thing) as string ?? PermissionUnavailable; }
                catch (NotSupportedException) { }
            return InSession ? PermissionUnavailable : "";
        }

        /// <summary>True when that player may control that thing.</summary>
        public static bool CanControl(int player, object thing) => ControlRefusal(player, thing).Length == 0;

        /// <summary> Host: why that player's colonist may not use that thing the way it is normally used
        /// (take an item, pass a door, use a building or shelf), or "" when it may. In separate colonies this
        /// is the ownership rule's answer, ally permissions included. Check it before your mod acts on a
        /// thing for a player. "" without Rimworld Multiplayer or outside a multiplayer game; in a game whose
        /// Rimworld Multiplayer cannot answer, a refusal. </summary>
        public static string UseRefusal(int player, object thing) => PermissionQuery("UseRefusal", player, thing);

        /// <summary> A veto on controlling things: gets (player slot, thing) and returns why not, or null.
        /// The host refuses that player's commands for the thing, and every game disables the player's
        /// buttons and right-click orders for it. Your own mod's buttons and actions are not stopped by your
        /// own veto. Returns "status|reason", or null. </summary>
        public static string? RegisterControlVeto(Func<int, object, string?> veto)
        {
            if (veto == null) throw new ArgumentNullException(nameof(veto));
            return Has(5) ? Call("RegisterControlVeto", veto) as string : null;
        }

        /// <summary> Names the thing your own button grid stands for while you draw it, whatever is selected
        /// (return null when not drawing it): a player's press of one of its buttons is sent for that thing.
        /// </summary>
        public static string? RegisterGizmoSubject(Func<object?> subject)
        {
            if (subject == null) throw new ArgumentNullException(nameof(subject));
            return Has(5) ? Call("RegisterGizmoSubject", subject) as string : null;
        }

        /// <summary> A small input stream from players to the host (movement, aim). The host's handler gets
        /// (player slot, payload) on its main thread. The newest value replaces older ones still waiting; at
        /// most 512 characters and 40 a second per player. Check everything: it comes from another computer.
        /// </summary>
        public static string? RegisterInputChannel(string channel, Action<int, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Has(5) ? Call("RegisterInputChannel", channel, handler) as string : null;
        }

        /// <summary>Sends input to the host; on the host the handler runs at once for the host's slot. False
        /// when not sent.</summary>
        public static bool SendInput(string channel, string payload) => Has(5) && Call("SendInput", channel, payload) is bool b && b;

        /// <summary>Receives the host's messages on a channel, on every game (the host's own
        /// included).</summary>
        public static string? RegisterPlayerHandler(string channel, Action<string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Has(5) ? Call("RegisterPlayerHandler", channel, handler) as string : null;
        }

        /// <summary>Host only: a message to one player's game (slot). At most 16384 characters. False when
        /// not sent.</summary>
        public static bool SendToPlayer(int slot, string channel, string payload) => Has(5) && Call("SendToPlayer", slot, channel, payload) is bool b && b;

        /// <summary>Host only: a message to every game, the host's included.</summary>
        public static bool SendToAll(string channel, string payload) => Has(5) && Call("SendToPlayer", -1, channel, payload) is bool b && b;

        /// <summary> Host: RimWorld messages shown while <paramref name="show"/> runs appear on that player's
        /// screen only (feedback from tick code about one player's thing). Elsewhere, and without Rimworld
        /// Multiplayer, it just runs. </summary>
        public static void AsPlayer(int slot, Action show)
        {
            if (show == null) throw new ArgumentNullException(nameof(show));
            if (Has(5)) Call("AsPlayer", slot, show); else show();
        }

        sealed class NoScope : IDisposable { public void Dispose() { } }

        /// <summary> Host: like <see cref="AsPlayer"/> until the returned scope is disposed, for a Harmony
        /// prefix and finalizer around someone else's method. Elsewhere it does nothing. </summary>
        public static IDisposable MessagesFor(int slot) => Has(5) && Call("PersonalMessages", slot) is IDisposable d ? d : new NoScope();

        /// <summary> On a player's game, the writes your code makes until the scope is disposed are that
        /// game's own (a camera, a panel, per-player state, another mod's per-player state your code drives):
        /// Rimworld Multiplayer does not treat them as a click to send or undo. Your handlers of the host's
        /// messages already run this way. Never for gameplay changes. </summary>
        public static IDisposable LocalScope() => Has(5) && Call("LocalScope") is IDisposable d ? d : new NoScope();

        /// <summary> Fields of your mod's settings that stay each player's own (zoom, layout, cursor style):
        /// players are not asked to match the host's values for them. </summary>
        public static string? RegisterPersonalSettings(Type settings, params string[] fields)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return Has(5) ? Call("RegisterPersonalSettings", settings, fields) as string : null;
        }

        /// <summary> Your mod brings its own multiplayer support for the mod with this package ID, so
        /// Rimworld Multiplayer's built-in handling of that mod stands aside. <paramref name="owner"/> is any
        /// type of your mod. </summary>
        public static string? RegisterCompatibilityFor(Type owner, string packageId)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            return Has(5) ? Call("RegisterCompatibilityFor", owner, packageId) as string : null;
        }

        /// <summary> Every declaration made so far and whether it works, one "kind|mod|member|status|reason"
        /// line each. Use it in development to check your mod's declarations. </summary>
        public static string[] Declarations() => Call("Declarations") as string[] ?? new string[0];
    }
}
