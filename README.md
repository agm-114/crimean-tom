# Sea Power mods

This repository contains independent Sea Power mods. Each module owns its DLL and deployment folder.

| Module | Source | Documentation |
|---|---|---|
| Crimean Tom | [CrimeanTom.SeaPower](src/CrimeanTom.SeaPower) | [Campaign and scenario generation](docs/crimean-tom/README.md) |
| Flight Deck Tweaks | [FlightDeckTweaks](src/FlightDeckTweaks) | [Aircraft preparation](docs/flight-deck-tweaks/README.md) |
| Sea Power CLI | [Game bridge](src/SeaPower.GameBridge), [CLI](src/SeaPower.Cli) | [UI and simulator inspection](docs/seapower-cli/README.md) |

Read [AGENTS.md](AGENTS.md) and the relevant module's docs before changing it. Campaign design and refactoring suggestions do not apply to Flight Deck Tweaks.

## Build and deploy

The projects use the repository's .NET 10 SDK. Mods target `netstandard2.1` for Sea Power; the standalone CLI targets `net10.0` without game dependencies. Run commands from the repository root. Build all modules without deploying:

```powershell
dotnet build CrimeanTom.slnx -p:DeployToGame=false
```

The default paths expect the Steam installation of Sea Power and the Workshop installation of AnchorChain. Override the `SeaPowerDir` or `AnchorChainDll` MSBuild properties if needed.

See each module's docs for its deployment command. A plain solution build still deploys Crimean Tom on Windows; Flight Deck Tweaks and Game Bridge require explicit opt-in. Use the [Sea Power testing workflow](.agents/skills/sea-power-testing/SKILL.md) when runtime verification is needed.

## Contributing to AnchorChain

[external/AnchorChain](external/AnchorChain) is a Git submodule pointing to the upstream source. Initialize it after cloning this repository:

```powershell
git submodule update --init --recursive
```

Make fixes on a branch inside that submodule and commit them there. Push to upstream if you have access, or to your fork for a pull request. After the commit is available remotely, stage `external/AnchorChain` in this repository to record the new revision.

The mods build against the installed Workshop DLL. To test a locally built AnchorChain, override `AnchorChainDll` and install the matching DLL in the game separately.
