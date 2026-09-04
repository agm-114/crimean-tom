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

The retired implementation and its documentation remain available on
`codex/archive-server-simulation-2026-09-04`.
