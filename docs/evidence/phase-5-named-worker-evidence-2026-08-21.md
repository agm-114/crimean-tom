# Phase 5 named tactical worker evidence — 21 August 2026

## Scope

Evidence for the experimental named-worker launcher (`scripts/sea-power-worker.ps1`)
and read-only live-proof gate (`scripts/sea-power-worker-live-proof.ps1`). This is
not simultaneous two-container island migration.

## First live host attempt (commits through `511c8a4`)

| Step | Outcome |
| --- | --- |
| `worker-a` companion start | Succeeded (companion PID 53120) |
| Direct `Sea Power.exe` launch | Unity initialized on isolated `-logFile` |
| Steamworks.NET | Shut down: `RestartAppIfNecessary returned true` |
| Default `pnpm dev` game | Remained healthy (PID 31208) |
| Transactional rollback | Stopped worker companion, removed worker game/metadata/companion PID files |
| Plugin wait | Full 120s timeout — did not detect early game exit |
| Two-container claim | **Not valid** from this attempt |

Isolated worker log excerpt (classification target):

```text
[Steamworks.NET] Shutting down because RestartAppIfNecessary returned true. Steam will restart the application.
```

## Follow-up harness changes (same branch, post-attempt)

1. Per-process `SteamAppId` / `SteamGameId` (default `1286220`, `-SteamAppId` override)
   on worker game `ProcessStartInfo` only — no `steam_appid.txt`, no install/global env mutation.
2. `Wait-ForPluginMarker` validates the recorded game PID/metadata each poll, like
   `Wait-ForGameConnection`, and fails immediately with a concise log tail.
3. Failure messages explicitly classify `RestartAppIfNecessary` when present in the worker log.

## Next experiment

Re-run `pnpm worker:start` for `worker-a` with the Steam app context change and confirm
whether the second Unity process stays alive long enough for the runtime bridge marker
and companion pipe connection. Admission still requires `pnpm worker:live-proof` with
two distinct real workers — not claimed until that gate passes on the host.
