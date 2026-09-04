# Crimean Tom

Crimean Tom is pivoting to an operations campaign for Sea Power.

The campaign runs between battles. It prepares a complete, ordinary Sea Power
scenario, lets Sea Power own the battle, then imports the after-action result to
update the persistent force. There is no background war simulation, live unit
streaming, companion process, or mid-battle campaign synchronization.

The repository is intentionally small while that workflow is rebuilt:

- `src/CrimeanTom.Operations` owns campaign state and the scenario/result boundary.
- `tests/CrimeanTom.Operations.IntegrationTests` exercises one complete campaign
  turn instead of maintaining many narrow tests.
- `references/ui/campaign-monitor.css` preserves the useful visual language from
  the retired campaign monitor. It is reference material, not a runnable app.

Build and test with:

```powershell
dotnet test CrimeanTom.slnx
```

## First playable slice

The current trial creates one persistent frigate, generates a standalone surface
battle, and writes an editable after-action report. Run these commands from the
repository root:

```powershell
dotnet run --project src/CrimeanTom.Operations.Cli -- init
dotnet run --project src/CrimeanTom.Operations.Cli -- prepare
dotnet run --project src/CrimeanTom.Operations.Cli -- install
```

Launch Sea Power and select `Crimean Tom Operations - Surface Trial` from user
missions. After the battle, edit `artifacts/demo-campaign/after-action.json` with
the frigate's remaining condition, fuel, and ammunition. Then apply it:

```powershell
dotnet run --project src/CrimeanTom.Operations.Cli -- resolve
dotnet run --project src/CrimeanTom.Operations.Cli -- status
```

Result capture is manual in this first slice. The next game-side component should
write that after-action file from the completed tactical session.

The first game-side adapter now does that for the surface trial. Build and deploy
it before launching Sea Power:

```powershell
dotnet build CrimeanTom.SeaPower.slnx
```

When the Sea Power debrief opens, the adapter matches the campaign frigate by its
database type and updates `after-action.json`. It currently records the mission
result and maps Sea Power's intact, damaged, and destroyed states to 100, 50, and
0 condition. Fuel and ammunition remain at their prepared values until the next
adapter pass reads those systems directly.

The retired implementation and its documentation remain available on
`codex/archive-server-simulation-2026-09-04`.
