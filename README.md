# Crimean Tom

Crimean Tom is a Sea Power mod for a lightweight mercenary operations campaign. Sea Power remains the tactical engine. The mod generates complete standalone battles, imports their results, and manages the player's persistent task force between missions.

The current implementation contains:

- native Task Force Mode commander and fleet setup;
- coalition-aware ship selection and programmatic discounts;
- generated fleet-clash, route-escort, route-interdiction, and strait-passage scenarios;
- direct scenario launchers for testing generated battles;
- fixed-per-turn contract briefings with risk-based pay and enemies that do not scale to the deployed fleet;
- victory-based salvage, a limited ship market, free group assignments, and acquisition/refit delays;
- an F8 autoresolve tool for exercising campaign management without fighting a battle.

There is no campaign server, companion application, live unit streaming, background war simulation, custom strategic map, or generated artwork.

## Start here

Read [docs/development-guide.md](docs/development-guide.md) before changing the project. It describes the architecture, native integration points, build and verification workflow, current campaign state, and known risks.

Design references:

- [docs/fleet-generation.md](docs/fleet-generation.md)
- [docs/mercenary-naval-contracts.md](docs/mercenary-naval-contracts.md)
- [docs/sea-power-mission-tools.md](docs/sea-power-mission-tools.md)

## Build and deploy

The project targets the latest installed .NET 10 SDK while producing a `netstandard2.1` assembly compatible with Sea Power. On Windows, a successful build also deploys the DLL and campaign content to the configured Sea Power installation.

```powershell
dotnet build CrimeanTom.slnx
```

The default paths expect the Steam installation of Sea Power and the Workshop installation of AnchorChain. Override the `SeaPowerDir` or `AnchorChainDll` MSBuild properties if needed.

## Contributing to AnchorChain

[external/AnchorChain](external/AnchorChain) is a Git submodule pointing to the upstream source. Initialize it after cloning this repository:

```powershell
git submodule update --init --recursive
```

Make fixes on a branch inside that submodule and commit them there. Push to upstream if you have access, or to your fork for a pull request. After the commit is available remotely, stage `external/AnchorChain` in this repository to record the new revision.

The mod still builds against the installed Workshop DLL. To test a locally built AnchorChain, override `AnchorChainDll` and install the matching DLL in the game separately.

The retired server-simulation implementation remains on `codex/archive-server-simulation-2026-09-04`.
