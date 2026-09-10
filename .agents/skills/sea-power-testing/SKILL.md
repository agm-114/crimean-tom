---
name: sea-power-testing
description: Verify Sea Power mods, generated scenarios, live UI behavior, startup failures, and deployments using seapower-cli. Use when work needs evidence from the installed game, not for ordinary compile-only changes.
---

# Sea Power testing

Test the smallest slice that proves the requested behavior. A successful build is
not proof that Sea Power loaded a mod or accepted a generated scenario.

## Prepare the CLI and deployment

Read the relevant module README and [CLI documentation](../../../docs/seapower-cli/README.md).
Build touched projects with `-p:DeployToGame=false` before runtime checks. This
repository has no maintained automated test suite; do not prescribe `dotnet test`
as a preflight or add tests just to follow this skill.

```powershell
dotnet build src/SeaPower.Cli/SeaPower.Cli.csproj
./src/SeaPower.Cli/bin/Debug/net10.0/seapower-cli.exe game-status
```

Examples below use `seapower-cli` for that executable. `game-status` inspects the
installation, processes and log metadata without the bridge or a running game.
Use `--game-dir PATH` or `SEA_POWER_DIR` for a non-default installation.

Use the module's deployment command only when deployment is in scope. The game
must reload a changed DLL. `SeaPower.GameBridge` requires explicit deployment
and an `enabled.txt` beside its DLL; the CLI can be rebuilt independently.
Do not enable mods, stop an existing game or change user settings without authority.

## Startup and logs

Only launch when authorized. A request not to launch overrides this workflow.

```powershell
seapower-cli launch --confirm --timeout 90 --require-pattern 'loaded native Task Force Mode shim'
seapower-cli logs --tail 200
```

Choose a marker from the current module's source. Omit `--require-pattern` for a
stock-game startup check. `launch` opens Steam app 1286220, refuses an existing
game, and requires a process plus fresh `Player.log` content. It excludes the
old content when a log is appended. Never invoke `Sea Power.exe` directly.

Timeout or cancellation does not close a launched game. Check `game-status` and
`logs` before retrying. If Steam requests cloud-save conflict resolution, stop
and ask the user rather than selecting a save. A startup marker proves loading,
not that the scenario or campaign works.

`logs` returns the Player.log tail and BepInEx log tail when present. It works
without the bridge. Increase `--tail` or read the reported file to find the first
relevant exception and its context. An existing log is not fresh-run proof.

For scenario, flight-deck or other player-visible checks, read
[references/live-ui.md](references/live-ui.md). Use CLI controls and unit queries
for state; use screenshots for appearance and layout.

## Boundaries

- Do not restore the retired server, companion, streaming protocol, or background
  simulation to make testing easier.
- Do not overwrite user saves or installed scenarios. Record source, destination
  and hash for any disposable scenario copied into the game; remove only your copies.
- Use the CLI's existing command queue, not a new network control service.
- Close Sea Power after testing only when the agent launched it and the user does
  not need it left open.

Report the game version, tested scenario, build or artifact identity, first
failed step, and relevant fresh log lines. Say plainly when a result came only
from a build or static check and was not verified in Sea Power.
