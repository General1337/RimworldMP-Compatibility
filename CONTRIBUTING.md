# Contributing

Thanks for helping more mods work in multiplayer. There are three ways to help.

## 1. Report how a mod behaves

Open an issue. Say which mod, what you did and what the host and each player saw.
Add-ons are fixed and updated from these reports.

## 2. Improve an add-on in `mods/`

Each add-on is a small RimWorld mod built on `RimworldMultiplayer.Api`. Change it, test it with a
host and at least one player, then update its `compat.json` (`status`, `covers`, `missing`)
so it says what you actually tested.

## 3. Make a new add-on

1. Copy `examples/ExampleCompat` to `mods/<ModName>Multiplayer`.
2. Rename the project file and set `AssemblyName` and the `packageId` in `About/About.xml`.
3. Read [How to make a mod work](#how-to-make-a-mod-work) below: most mods need only a few declarations.
4. Write `compat.json`:

   ```json
   {
    "mod": "Name of the mod on the Workshop",
    "workshop": "Workshop id",
    "status": "works | partial | experiment | broken",
    "covers": "What you tested and what works.",
    "missing": "What is not covered."
   }
   ```

5. Build and test it with a host and at least one player.

## Sharing your add-on

You don't need to send anything here to use or share your add-on. After building, the add-on folder
(`About`, `Assemblies` and any `Languages`, `Patches` or `Defs`) is a complete RimWorld mod.

- **Friends:** send them the folder to put in their RimWorld `Mods` folder.
- **Steam Workshop:** set your own `packageId` and `name` in `About/About.xml` first, so your upload
  doesn't clash with anyone else's. Then put the folder in your `Mods` folder, turn on Development
  mode, open the Mods window, select your add-on and press Upload.

The host and every player need the add-on, loaded after the target mod and Rimworld Multiplayer.

## Getting on the official list

Open a pull request with your add-on folder and its `compat.json`. The build runs automatically. If your add-on is accepted it becomes a
permanent part of the official compatibility list, and you can keep your own Workshop upload.

## Rules

- Use only `RimworldMultiplayer.Api`, the game and Harmony. Don't copy code from the target mod; call
  or patch it by name.
- No DLLs in pull requests; the build makes them.
- Every change is reviewed before it is merged.
- By contributing you agree your code is released under the MIT license in LICENSE.
- Tested on a host and at least one player; what you tested is in `covers`.

## How to make a mod work

This guide is for mod authors. It explains how to make your own mod, or a patch for someone else's mod, work in Rimworld Multiplayer without any change to Rimworld Multiplayer and without asking its author. It also explains packs: C# game modes and rules the host loads and edits while the game runs.

### How multiplayer works, in one paragraph

Only the host simulates the world. Every player's game shows a copy of the host's world, kept up to date by Rimworld Multiplayer. When a player clicks something, the click is sent to the host, the host does it, and the result reaches everyone. Most mods work unchanged because their effects happen inside the host's world. A mod needs help only when its interface changes the world directly (a button that edits a pawn from its own drawing code) or when it keeps important state in static fields players need.

### Three ways to add support

| You want to | Use | Players need |
|---|---|---|
| Make your own mod work | The `RimworldMultiplayer.Api` library inside your mod | Your mod (as always) |
| Make someone else's mod work | A compatibility pack | Nothing extra: the host sends your declarations as data |
| Change rules or make a game mode | A pack | Nothing extra: packs run on the host only |

#### 1. Your own mod: the API library

Add `RimworldMultiplayer.Api.dll` (from `src/RimworldMultiplayer.Api`, netstandard2.0, no other dependencies) to your mod's `Assemblies` folder and reference it. Without Rimworld Multiplayer every call does nothing, so the same build works in single player. Call it from your mod's constructor or a `[StaticConstructorOnStartup]` class:

```csharp
using RimworldMultiplayer.Api;

[StaticConstructorOnStartup]
static class MultiplayerSupport
{
    static MultiplayerSupport()
    {
        // A button handler that changes the world: players' clicks run on the host.
        Multiplayer.RegisterSyncMethod(typeof(MyGizmos), nameof(MyGizmos.ToggleHeater));
        // Static state players need to see the host's value of.
        Multiplayer.RegisterSharedStatic(typeof(MySettings), 1, nameof(MySettings.Difficulty));
    }
}
```

What it offers:

- `RegisterSyncMethod(method, policy)`: a method your UI calls that changes the world (static or instance, returning void). On a player's game the call is sent to the host instead of run. Arguments must be things Rimworld Multiplayer can name on both games: things, pawns, maps, defs, numbers, strings, enums and lists of these. Returns `status|reason`.
- `RegisterAction(method, name, version, policy)` and `RequestAction(id, version, args, callback)`: an explicit host action you call yourself, with a result callback.
- `RegisterSharedStatic` / `RegisterSharedStaticData`: static fields (plain values, or collections and records of plain data) whose host value every player receives.
- `RegisterLocalMethod(method)`: a method that runs on each player's own game because it only changes that player's screen or own state (a viewed level, the camera, a filter). A button or menu option that calls it, and otherwise only reads, draws, selects or moves the camera, runs on the player's game instead of being sent to the host.
- `RegisterLocalField(type, field)`: an instance field that belongs to each player (a viewed level, a chosen tab). Every game keeps its own value; the host's is never copied over it. Not for gameplay state.
- `RegisterLocalComponent(type)`: a game, world or map component of yours that is each player's own presentation, such as a camera that follows a colonist. Its tick runs on every player's game and its buttons run on the pressing player's game. Calls that change the game are still stopped inside it on players' games, and a tick that throws or keeps taking longer than a few milliseconds is switched off for the session. A component that saves fields is refused, because saved component state is the host's; declare its per-player view fields with `RegisterLocalField` instead. A game component that saves nothing is already treated this way without a declaration.
- `RegisterLocalCache(type, fields, rebuild, cellChanged)`: unsaved fields of your map component that each game builds for itself from the world, such as lit cells, water cells or a lookup grid. The host's values are never copied over them. On a player's game, `rebuild(component)` runs once the host's world, or a map the host added, has finished loading, and `cellChanged(component, x, z)` runs when a cell's terrain changes there. Either callback may be null. Rebuild only from what the world shows (things, terrain, buildings); a callback that throws is recorded in the multiplayer problem log and the game carries on. A field the component saves is refused, because saved state is the host's. A compatibility pack can make the same declaration as data with `Services.Compat.LocalCache(typeFullName, fields, rebuildMethod, cellMethod)`, naming the target component's own parameterless rebuild method and its own `(int x, int z)` cell method.
- `RegisterApplyCallbacks(type, beforeApply, afterApply)`: for one exact type of yours (a thing, comp, or game, world or map component), callbacks around the moment a player's game loads the host's saved data into an object of that type. Your load code (`PostLoadInit`, `FinalizeInit`, `LoadedGame`) does not run again there, so derived data you built from saved fields, such as a static index or lookup table, would keep the player's old answer. `beforeApply(obj)` runs right before the load, while the object still holds the player's previous values: release what the load replaces. `afterApply(obj)` runs right after the load, before the object is spawned again when the reload moved it, and also for an object the host's data created: rebuild derived data from the host's values. When `beforeApply` ran, `afterApply` always runs, also when the load failed. Either may be null, not both; a subclass needs its own declaration. Neither runs on the host or in single player. Only rebuild derived data: changes to the game belong to the host. A callback that throws three times, or keeps taking longer than a few milliseconds, is switched off for the session and shown in the modder helper. A compatibility pack can make the same declaration as data with `Services.Compat.ApplyCallbacks(typeFullName, beforeMethod, afterMethod)`, naming the target type's own parameterless methods.
- `Available`, `InSession`, `IsHost`: where your code runs.
- `Declarations()`: every declaration and whether it works. Print it in development to check your mod.

Players and control, for mods where each player does something of their own. See the full example in `mods/PerspectiveShiftMultiplayer` (every player inhabits their own pawn with Perspective Shift):

- `CurrentPlayer`, `LocalPlayer`, `Players`, `Player(slot)`: who a command on the host is for, who this game is, and who is playing (`MpPlayer`: slot, name, color, host, spectator, connected, colony). Never an identity, Steam ID or address.
- `PlayerKey(slot)`: a per-world key to save things per player by. It stays the same across reconnects and reloads of that world. The host knows everyone's, a player only their own.
- `RegisterControlVeto((player, thing) => reason or null)`: things a player may not control (a pawn someone else plays). The host refuses that player's commands. Every game disables that player's buttons and right-click orders with your reason. Your own mod's buttons and actions are not stopped by your own veto. `ControlRefusal` / `CanControl` ask the same question.
- `UseRefusal(player, thing)`: on the host, why that player's pawn may not use a thing the way it is normally used (take an item, pass a door, use a building or shelf), or "" when it may. In separate colonies this is the colony ownership rule, ally permissions included. Ask it before your mod acts on things for a player: actions only check the things they name as arguments. In a multiplayer game whose Rimworld Multiplayer is too old to answer, `UseRefusal` and `ControlRefusal` return a refusal (and `CanControl` false), never "allowed"; without multiplayer they return "".
- `RegisterGizmoSubject(() => thing or null)`: the thing your own button grid stands for while you draw it (null otherwise). A player's press in it is sent for that thing.
- `RegisterInputChannel(channel, (player, text) => ...)` and `SendInput(channel, text)`: a small latest-value stream from players to the host, such as movement or aim. It is capped at 512 characters and 40 a second per player. Check everything you receive.
- `RegisterPlayerHandler(channel, text => ...)`, `SendToPlayer(slot, channel, text)`, `SendToAll(channel, text)`: messages from the host to one game or all, up to 16384 characters.
- `AsPlayer(slot, () => ...)`: RimWorld messages shown while your code runs on the host appear on that player's screen only.
- `RegisterPersonalSettings(settingsType, fields...)`: settings that stay each player's own, such as zoom or layout. Players aren't asked to match the host's values for them.
- `RegisterCompatibilityFor(ownerType, packageId)`: your mod brings its own multiplayer support for another mod, and Rimworld Multiplayer's built-in handling of that mod stands aside.

Policies: `HostOnly`, `DebugOnly`, `RequireSelection` (cancelled when nothing is selected), `CancelNullArguments`.

Several mods may ship different versions of the library; each finds Rimworld Multiplayer by name at run time, so they do not conflict. If two mods declare the same method with different policies, both declarations are refused and `Declarations()` says so.

#### 2. Someone else's mod: a compatibility pack

A folder with a `pack.json` and C# files. It declares methods of the target mod; it changes nothing else.

```json
{ "id": "you.othermodcompat", "name": "Other Mod multiplayer support", "apiVersion": 1,
  "targets": ["author.othermod"], "capabilities": [] }
```

```csharp
public sealed class Nothing { }
public sealed class OtherModCompat : GamePack<Nothing>
{
    public override void OnLoad()
    {
        Services.Compat.SyncMethod("OtherMod.ComfortGizmos", "ToggleHeater");
        // Overloaded methods: list the parameter type names.
        Services.Compat.SyncMethod("OtherMod.ComfortGizmos", "SetTarget", ["Verse.Thing", "System.Single"], policy: 8);
        // A "view level" button that should move only the clicking player's view:
        Services.Compat.LocalMethod("OtherMod.LevelView", "JumpTo");
        Services.Compat.LocalField("OtherMod.LevelMapComponent", "viewedLevel");
        // A map component whose tick only moves this player's camera:
        Services.Compat.LocalComponent("OtherMod.CameraFollower");
    }
}
```

The host loads the pack, checks every declaration (the type must belong to a mod listed in `targets`, the method must exist and be sendable), installs it, and sends the list to every player at join. Players apply it with Rimworld Multiplayer's own code; they never run your pack and do not need to install it. See a pack for CompatSample.

#### 3. Game modes and rules: packs

A pack is C# source the host's game compiles. It runs on the host only, and its effects reach players like anything else in the world. Saving a file while the game runs reloads the pack at the next folder poll and keeps its state; polling follows 120 game ticks, so pause and speed affect it.

```csharp
public sealed class StandingState { public List<int> Fallen = new(); public bool Over; }

public sealed class LastColonyStanding : GamePack<StandingState>
{
    public override void OnLoad() => Services.Events.ColonyDefeated(e => Fall(e.Colony.Id));
    public override void OnGameStart() => Game.Message("Last colony standing wins.");
    void Fall(int colony) { /* ... Game.EndGame([winner], reason) ... */ }
}
```

The full example is a pack for LastColonyStanding.

What a pack gets:

- **Events:** pawn killed, colony defeated, player joined, player left, relation changed, day started. Delivered between ticks.
- **Queries:** colonies, players, a colony's pawns, the relation between two colonies, the day, whether war is enabled. Records with stable ids, never live game objects.
- **Actions** (each needs a capability in `pack.json`): `Message` (Ui), `DeclareWar` / `MakePeace` (Relations), `GiveItems` (Items), `Kill` (PawnEffects), `EndGame` (GameControl). The game's own rules still apply: war needs the host's war setting, teams go to war together.
- **Timers:** `Services.Timers.Handle(name, handler)` in `OnLoad`, `After(ticks, name, payload)` later. Saved with the game and rebound to a reloaded pack by name.
- **State:** the `State` object, plain data only (numbers, strings, enums, lists, sets, dictionaries, classes of these). Saved with the game; survives reloads. Raise `StateSchema` and override `Migrate` when its shape changes.

Rules packs live by:

- `OnLoad` prepares: register handlers, read state. It must not change the game; throwing there keeps the previous version running.
- Each handler call has a time budget (5 ms). A runaway loop stops itself; three faults disable the pack and keep its state.
- At most 64 actions per handler call.
- API packs may use the pack API and safe .NET (collections, LINQ, math, strings). No files, network, threads, reflection or interop; the compiler refuses them with the file and line. Full-access packs (`"fullAccess": true`) may use the game's own types and are labelled as such.

### Where packs go

The host's game data folder, in `RimworldMultiplayerPacks/<pack>/`. Packs need nothing besides Rimworld Multiplayer: the pack compiler ships inside it and loads only on a host that has packs. Players need nothing extra; they receive what the host's packs declare as data, never pack code.

### Ability and travel declarations

`RegisterRoutedVerb(type)` opts a loaded mod's verb type and derived types into
caster-owned references when the mod creates different IDs on each game.
Native verb tracker IDs are retained. It names existing host objects; it does
not copy a player's verb into the host or authorize a cast.

`TravelRefusal(caster, beacon)` checks whether their colonies permit the beacon
as a destination. `CanTravelTogether(caster, passenger)` filters the native
passenger candidates to the caster's own colony. The action must separately
check caster control and the target mod's native eligibility, resource and
range rules. These queries perform no movement. With multiplayer present but
the query missing, the library refuses; without multiplayer, native behavior
is preserved.

`RegisterHostField(type, field)` explicitly makes a field authoritative even
when a patched save method prevents automatic classification. Host fields are
also excluded from ordinary player settings edits. Declare the smallest
necessary state; never mark player camera, selection or targeting as host state.
It also covers a field your mod leaves out of its own save on a class that
another object saves whole (a pawn comp's body record, for example): the
host's value then travels inside that record's save to every player. Such a
field may hold a plain value or a list or array of plain classes. Rimworld Multiplayer declares Kindred's bone dragon body this way.

See `mods/AbilityFrameworkMultiplayer`, `mods/PsycastTravelMultiplayer`
and a pack for AbilityFramework. Packs use
`Services.Compat.RoutedVerb("Namespace.VerbType")` for the same opt-in.

### Packs and the library

A compatibility pack declares, as data, everything the library declares without code of yours: `SyncMethod`, `LocalMethod`, `HostOnlyMethod`, `LocalField`, `SyncField`, `HostField`, `OwnerField`, `RoutedVerb`, `LocalComponent`, `LocalCache` (with rebuild and cell methods), `ApplyCallbacks`, `SharedStatic`, `SharedStaticData`, `PersonalSettings` and the saved-record declarations. Where the library takes a callback, a pack names the target type's own method. Retiring or changing a pack withdraws what it declared on every game; saved shared-static values stay in the save for a later matching declaration.

Declarations that run code you write need a compatibility mod with this library, installed on every game: `RegisterAction`/`RequestAction`, `RegisterArgumentType`, `RegisterOwnerResolver`, `RegisterControlVeto`, `RegisterGizmoSubject`, `RegisterInputChannel`, `RegisterPlayerHandler` and `RegisterCompatibilityFor`. Packs have their own actions, events and queries for host-side logic.

### Checking your work

- Start with the smallest real workflow that fails. Keep camera, targeting and menus local; put gameplay, costs and cooldowns on the host. Use saved-object replication first, then declare only the state it demonstrably misses.
- For an explicit action, declare its button callback local on every seat and call `RequestAction` with the desired value. On the host, validate the authenticated `CurrentPlayer`, caster control and every affected target before applying it. See `mods/RunAndGunMultiplayer`.
- Run the same interface on host and two separate player colonies. Check the native effect, payment, ownership, refusal controls and every game's display, then save/load and reconnect. A successful registration or request acknowledgement is insufficient.
- Choose a declaration pack when declarations alone suffice; choose a separate add-on when the target mod needs a narrow code adaptation. Clients receive pack declarations as data; a code add-on must be installed on every game.
- In development, print `Multiplayer.Declarations()` (library) or read the host log lines starting with `[RimworldMultiplayer] compatibility` (packs).
- A pack that fails to compile, load or run writes the reason with file and line to the host log, and keeps the last working version running.
