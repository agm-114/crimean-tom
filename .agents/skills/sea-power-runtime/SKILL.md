---
name: sea-power-runtime
description: Build, deploy, launch, verify, and diagnose the Sea Power Runtime Bridge AnchorChain plugin used by Crimean Tom. Use for runtime mod checks, Player.log or BepInEx failures, stale deployed DLLs, and confirming that a new plugin build actually loaded. Do not use for ordinary compile-only C# work that needs no game interaction.
---

# Sea Power Runtime

Use the repository automation as the authority for the local development loop:

```powershell
pnpm status
pnpm dev
```

`pnpm dev` restarts Sea Power as needed, builds and deploys the plugin, launches
the game, and waits for a fresh health marker in `Player.log`. Treat a process
launch without that marker as a failure.

## Operating constraints

- Respect `SEA_POWER_DIR`; otherwise use the default Steam installation encoded
  in `scripts/sea-power.ps1`.
- Launch through Steam app ID `1286220`. Do not start `Sea Power.exe` directly;
  Steamworks restarts direct launches and makes loader verification ambiguous.
- Let the repository commands handle deployment, restart, and preflight checks.
- Verify a new build only after a fresh game launch.

## Choose the narrowest command

- `pnpm build`: compile and automatically deploy, then compare hashes.
- `pnpm deploy`: stop the game if needed and redeploy an existing build.
- `pnpm launch`: launch and require a fresh plugin health marker.
- `pnpm verify`: compare hashes and inspect the current log without launching.
- `pnpm logs`: show relevant recent runtime diagnostics.
- `pnpm status`: inspect paths, process state, hashes, and marker state.

Read [references/runtime-debugging.md](references/runtime-debugging.md) when a
launch check fails, the health marker is missing, or runtime behavior needs
diagnosis beyond the standard loop.

Read [references/ui-testing.md](references/ui-testing.md) only when the task
requires player-visible menu, mission, campaign, or editor verification.
