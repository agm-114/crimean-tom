---
name: sea-power-testing
description: Launch, smoke-test, and diagnose Sea Power when Crimean Tom changes need proof inside the installed game. Use for generated-scenario checks, mod load markers, player-visible UI verification, Player.log failures, and stale deployments. Do not use for ordinary compile-only work.
---

# Sea Power testing

Test the smallest slice that proves the requested behavior. A successful build is
not proof that Sea Power loaded a mod or accepted a generated scenario.

## Start with the local preflight

Run repository tests before launching the game:

```powershell
dotnet test CrimeanTom.slnx
```

Then inspect the installation and current process without changing either:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .agents/skills/sea-power-testing/scripts/sea-power-test.ps1 -Action Status
```

The helper respects `SEA_POWER_DIR` and otherwise uses the default Steam install.
It never stops a running game, deploys files, or changes Mod Manager state.

## Launch and require fresh evidence

Launch through Steam app ID `1286220`. Do not start `Sea Power.exe` directly.
Steamworks may restart a direct launch, which makes startup evidence ambiguous.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .agents/skills/sea-power-testing/scripts/sea-power-test.ps1 -Action Launch
```

When a mod has a startup marker, require it in the fresh `Player.log`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .agents/skills/sea-power-testing/scripts/sea-power-test.ps1 `
  -Action Launch `
  -RequirePattern 'Crimean Tom Operations.*loaded'
```

Do not reuse a marker from an earlier run. Treat a launched process without a
fresh log and required marker as a failed test.

## Pick the test mode

- For a generated scenario or player-visible flow, read
  [references/live-ui.md](references/live-ui.md).
- For a load failure or runtime exception, inspect the latest log with
  `sea-power-test.ps1 -Action Logs`. Search around the first relevant exception,
  not every warning in the file.
- For deployment, use the repository's current deployment command if one exists.
  If it does not, inspect the target format and install location before copying.
  Record every file created and remove only test files created during the task.

## Boundaries

- Do not restore the retired server, companion, streaming protocol, or background
  simulation to make testing easier.
- Do not stop an existing Sea Power process, toggle user mods, overwrite saves,
  or change game settings unless the user authorized that action.
- Prefer typed test hooks when the new mod exposes them. Do not build a network
  control service solely for test automation.
- Close Sea Power after testing only when the agent launched it and the user does
  not need it left open.

Report the game version, tested scenario, build or artifact identity, first
failed step, and relevant fresh log lines. Say plainly when a result came only
from a build or static check and was not verified in Sea Power.
