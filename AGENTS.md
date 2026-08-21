# Repository guidance

Use `pnpm` instead of `npm` or `npx` for package management and repository commands.

Use the repository-local
[`$sea-power-runtime`](.agents/skills/sea-power-runtime/SKILL.md) skill when
building, deploying, launching, verifying, or debugging Sea Power Runtime Bridge inside Sea Power.

Minimize applications launched by agents. Launch only what the current task
needs, and safely close agent-launched applications when they are no longer in
use unless the user asked to keep them running.

Sea Power may be launched for runtime or UI work. Minimize its window whenever it is running but is not being used interactively.

During Sea Power UI work, prefer typed automation exposed by Sea Power Runtime
Bridge over desktop coordinate clicks. Use desktop input for discovery or as a fallback.
Our eventual goal is to build a dynamic campaign mod.

Prioritize getting real features working over expanding test infrastructure.
Avoid narrow unit tests. Keep a small number of broad integration tests that hit as much of the code as possible.

Work on feature branches, not `main`. Commit and push to the active branch frequently.

## Temporary: no Computer Use — use the runtime bridge

Until Computer Use is available, agents MUST inspect and drive Sea Power through
Sea Power Runtime Bridge screenshot, UI-inspection, and typed interaction tools
instead of desktop coordinate clicks. Keep expanding those plugin/companion
tools when a live UI gap blocks work. Do not treat this as a permanent
architecture change.

## Hung-command timeouts

Research commands against Sea Power assemblies can look idle or hang, especially
`ilspycmd --list` piped into `Select-String`. Never wait indefinitely:

- Wrap hang-prone commands with `scripts/invoke-with-timeout.ps1`.
- Default bounds: 60s for `ilspycmd --list` / `--type`, 600s for full decompile.
- Set the Shell tool `block_until_ms` to the same bound plus a few seconds.
- Exit code `124` and `COMMAND_TIMEOUT` mean the process was killed; treat that
  as failure and narrow the command instead of waiting.
- Pass `--disable-updatecheck` to `ilspycmd` so the CLI cannot stall on an
  update prompt.
