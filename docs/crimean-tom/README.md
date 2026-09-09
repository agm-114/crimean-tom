# Crimean Tom

Crimean Tom manages a mercenary operations campaign between Sea Power battles.
It generates complete standalone missions, imports their results and manages the
player's persistent task force. It has no campaign server, companion process,
background simulation, live unit streaming or custom strategic map.

The module includes native commander and fleet setup, coalition-aware unit
selection, generated scenarios, campaign contracts, salvage, a ship market,
group assignments and readiness management. Standalone scenarios offer more
families and variants than the campaign currently uses. F8 opens the campaign
autoresolve diagnostic.

## Documentation

- [Development guide](development-guide.md): architecture, runtime flow, verification and known limitations.
- [Fleet generation](fleet-generation.md): force construction and implemented scenario variants.
- [Naval contracts](mercenary-naval-contracts.md): mission-family design catalog.
- [Scenario and campaign plan](scenario-and-campaign-generation-plan.md): generation requirements and acceptance checklist.
- [Sea Power mission tools](sea-power-mission-tools.md): native format and mission-editor capabilities used by this module.
- [Refactoring backlog](refactoring/README.md): candidates to validate before implementing.

## Build and deploy

Run from the repository root. Build without touching the game:

```powershell
dotnet build src/CrimeanTom.SeaPower/CrimeanTom.SeaPower.csproj -p:DeployToGame=false
```

Build and deploy this module on Windows:

```powershell
dotnet build src/CrimeanTom.SeaPower/CrimeanTom.SeaPower.csproj -p:DeployToGame=true
```

Crimean Tom also deploys by default when `DeployToGame` is omitted. Its DLL goes
to `Sea Power_Data/StreamingAssets/CrimeanTomOperations`; campaign content goes
to `Sea Power_Data/StreamingAssets/user/campaigns/crimean-tom-operations`.
See the [repository README](../../README.md) for shared prerequisites and
AnchorChain setup.

The retired server-simulation implementation remains on
`codex/archive-server-simulation-2026-09-04`.
