# Sea Power mods

This repository contains independent Sea Power mods. Each module owns its DLL and deployment folder.

| Module | Source | Documentation |
|---|---|---|
| Crimean Tom | [CrimeanTom.SeaPower](src/CrimeanTom.SeaPower) | [Campaign and scenario generation](docs/crimean-tom/README.md) |
| Deck Tweaks | [FlightDeckTweaks](src/FlightDeckTweaks) | [Aircraft preparation](docs/flight-deck-tweaks/README.md) |
| UNREP | [UNREP](src/UNREP) | [Supplies tab](docs/unrep/README.md) |
| Sea Power CLI | [Game bridge](src/SeaPower.GameBridge), [CLI](src/SeaPower.Cli) | [UI and simulator inspection](docs/seapower-cli/README.md) |
| Loading Profiler | [Profiler](src/SeaPower.LoadingProfiler), [bootstrap](src/SeaPower.LoadingProfiler.Bootstrap) | [Startup and scenario timings](docs/loading-profiler/README.md) |
| Quickload | [SeaPower.StartupTweaks](src/SeaPower.StartupTweaks) | [Deferred startup loading](docs/startup-tweaks/README.md) |

Read [AGENTS.md](AGENTS.md) and the relevant module's docs before changing it. Campaign design and refactoring suggestions do not apply to Deck Tweaks.

## Build and deploy

The projects use the repository's .NET 10 SDK. Mods target `netstandard2.1` for Sea Power; the standalone CLI targets `net10.0` without game dependencies. Run commands from the repository root. Build all modules without deploying:

```powershell
dotnet build CrimeanTom.slnx -p:DeployToGame=false
```

The default paths expect the Steam installation of Sea Power and the Workshop installation of AnchorChain. Override the `SeaPowerDir` or `AnchorChainDll` MSBuild properties if needed.

On Windows, mod builds deploy by default, including a plain solution build. Close Sea Power before building, or use `-p:DeployToGame=false` for a compile-only build. The CLI remains a standalone tool and is not copied into the game. Deployment does not launch the game or change Mod Manager selections. See each module's docs for its destination. Use the [Sea Power testing workflow](.agents/skills/sea-power-testing/SKILL.md) when runtime verification is needed.

## Browse the installed game code

```powershell
./scripts/decompile-game.ps1
./scripts/decompile-game.ps1 -Assemblies Seapower-Scripts,Noesis.NoesisGUI
./scripts/decompile-game.ps1 -AllManaged
```

Requires `ilspycmd`, installed with `dotnet tool install --global ilspycmd`.
The default exports the complete `Seapower-Scripts`, `Assets`, `Libraries` and
`Plugins` assemblies as C# projects. `-AllManaged` also exports managed engine
and third-party dependencies. Native engine code is not C# and is not decompiled.
Override `-GameDirectory` or set `SEA_POWER_DIR` for another installation.

Output lives under ignored `decompiled/game/<assembly>/<hash>/`, with folders by
namespace. Each export records its source SHA-256 and ILSpy version. Unchanged
exports are skipped; updates get separate directories, preserving older results.
Search with `rg -n 'WaitForLoadingDone' decompiled/game/Seapower-Scripts`.
Generated code is an approximation for local inspection, not source to publish
or a guarantee that the generated project will build. The script does not launch
or modify the game.

## Contributing to AnchorChain

[external/AnchorChain](external/AnchorChain) is a Git submodule pointing to the upstream source. Initialize it after cloning this repository:

```powershell
git submodule update --init --recursive
```

Make fixes on a branch inside that submodule and commit them there. Push to upstream if you have access, or to your fork for a pull request. After the commit is available remotely, stage `external/AnchorChain` in this repository to record the new revision.

The mods build against the installed Workshop DLL. To test a locally built AnchorChain, override `AnchorChainDll` and install the matching DLL in the game separately.
