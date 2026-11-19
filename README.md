# Rimworld Multiplayer compatibility

Make your mod, or someone else's, work in [Rimworld Multiplayer](https://steamcommunity.com/sharedfiles/filedetails/?id=3825809806). This repository has the
library mods use to talk to Rimworld Multiplayer, an example and a growing set of
compatibility add-ons for popular mods.

Mod creators that want to build multiplayer compatiblity natively into their mod should use this library
Building separate compatibility should also use this library to make compatibility patches for a mod. Either give your players your compatibility mod, or upload it yourself to Steam Workshop and have them subscribe to it.
Please submit your compatibility patches to this github, where it will be reviewed and if accepted become apart of the main mods compatibility list.

License: MIT (see LICENSE).

## What is here

| Folder | What it is |
|---|---|
| `src/RimworldMultiplayer.Api` | The library. Reference it and ship its DLL with your mod.  (If you are a mod creator, this will allow your mod to hook into RimWorld Multiplayer) |
| `examples/ExampleCompat` | The smallest working mod: one button that syncs. |
| `mods/` | Compatibility add-ons for other mods. |

## How it works

### 1. Make your mod compatible

- **Your own mod:** add `src/RimworldMultiplayer.Api` to your project and ship
  `RimworldMultiplayer.Api.dll` in your mod's `Assemblies` folder.
- **Someone else's mod:** copy `examples/ExampleCompat` to `mods/<ModName>Multiplayer` and build a
  small add-on that targets that mod by name. Its author doesn't need to change anything.

Then declare what the mod does, for example a button that changes the world:

```csharp
if (Multiplayer.Available)
    Multiplayer.RegisterSyncMethod(typeof(MyComp), nameof(MyComp.SetMode));
```

Without Rimworld Multiplayer every call does nothing, so one build works in single player too.
[How to make a mod work](CONTRIBUTING.md#how-to-make-a-mod-work) explains which call to use for what.

### 2. Share it however you like

A built add-on is an ordinary RimWorld mod. Give the folder to your friends, or upload it to the
Steam Workshop yourself. Everyone in the game installs it. See
[Sharing your add-on](CONTRIBUTING.md#sharing-your-add-on).

### 3. Optional: send it in

Open a pull request with your add-on. Once it's reviewed and accepted it becomes a permanent part of
the official compatibility list.

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The game and Harmony
references come from NuGet packages, so you don't need RimWorld installed to build.

```
dotnet build src/RimworldMultiplayer.Api/RimworldMultiplayer.Api.csproj -c Release
dotnet build mods/RunAndGunMultiplayer/Source -c Release
```

Each add-on's DLLs land in its `Assemblies/` folder.

## Compatible mods

Install an add-on on the host and on every player's game, loaded after the mod it supports and
Rimworld Multiplayer.

