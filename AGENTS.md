# Repository guidance

Our eventual goal is to build a dynamic campaign mod.

Use the repository-local
[`$sea-power-runtime`](.agents/skills/sea-power-runtime/SKILL.md) skill when
building, deploying, launching, verifying, or debugging Sea Power Runtime Bridge inside Sea Power.

During Sea Power UI work, prefer typed automation exposed by Sea Power Runtime
Bridge over desktop coordinate clicks. Use desktop input for discovery or as a fallback.

Prioritize getting real features working over expanding test infrastructure.
Avoid narrow unit tests. Keep a small number of broad integration tests that hit as much of the code as possible.

Work on feature branches, not `main`. Commit and push to the active branch frequently.


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
