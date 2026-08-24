# Phase 5 named tactical worker evidence, 21 August 2026

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
| Plugin wait | Full 120s timeout. The harness did not detect the early game exit. |
| Two-container claim | **Not valid** from this attempt |

Isolated worker log excerpt (classification target):

```text
[Steamworks.NET] Shutting down because RestartAppIfNecessary returned true. Steam will restart the application.
```

## Harness hardening (post first attempt)

1. Per-process `SteamAppId` / `SteamGameId` (default `1286220`, `-SteamAppId` override)
   only on the worker game's `ProcessStartInfo`. It does not write `steam_appid.txt`
   or change the install or global environment.
2. `Wait-ForPluginMarker` validates the recorded game PID/metadata each poll and fails
   immediately with a concise log tail.
3. Failure messages classify `RestartAppIfNecessary` when present in the worker log.
4. JSON metadata timestamps from `ConvertFrom-Json` are compared as UTC without lossy
   `[string]` casts that coerce `DateTime` to local culture strings.
5. `-WorkersRunRoot` on live-proof defaults to repo `.run\workers` but accepts an external
   workers root for worktree verification against a live main deployment.

## Coexistence proof (21 Aug 2026, campaign `08eed6175b1a4e1abf50004d4e5808c8`)

Run root: `C:\Users\Varun\repos\crimean-tom\.run\workers`

| Worker | Game PID | Companion PID | Game session | Client instance | Campaign session |
| --- | --- | --- | --- | --- | --- |
| `worker-a` | 47840 | 35088 | `75189b90542e446482ecdddd7a48dd88` | `39d074634b5d40d380a1191c33c109da` | `67d7dec5de9a4557b8c96bb731e30246` |
| `worker-b` | 46160 | 42392 | `d1269ddd9d6748faac76d4d05ee3185c` | `e3b6431ff5d74518ac47469734483ea1` | `a2a3074e3b7b4a698a87de28bd4e1931` |

Read-only verification from the `phase5-named-workers` worktree:

```powershell
pnpm worker:status -- -WorkerId worker-a `
  -RunDirectory C:\Users\Varun\repos\crimean-tom\.run\workers\worker-a
pnpm worker:status -- -WorkerId worker-b `
  -RunDirectory C:\Users\Varun\repos\crimean-tom\.run\workers\worker-b
pnpm worker:live-proof -- `
  -WorkersRunRoot C:\Users\Varun\repos\crimean-tom\.run\workers `
  -CampaignId 08eed6175b1a4e1abf50004d4e5808c8 `
  -CampaignHttpUrl http://127.0.0.1:5081 `
  -CampaignTokenFile C:\Users\Varun\repos\crimean-tom\.run\campaign-service\service.token `
  -WorkerIds worker-a,worker-b
```

Result: `LIVE_PROOF_SUCCEEDED: two distinct real campaign workers coexist.`

**Coexistence of two real runtime+companion workers is proven** on this host.
**Cross-worker tactical-island migration remains unproven**. Do not claim
two-container migration or automatic rebalance until `pnpm worker:migration-proof -Execute`
succeeds on the live host.

Migration proof driver (21 Aug 2026): `scripts/sea-power-worker-migration-proof.ps1`
with shared helpers in `scripts/sea-power-worker-proof-common.ps1`. Preflight on
campaign `08eed6175b1a4e1abf50004d4e5808c8` is **blocked** because USS Pharris
Primary epoch 2 is still owned by the disconnected default companion client
(`7cb34046…`), not `worker-a` or `worker-b`. See
`docs/evidence/phase-5-migration-proof-2026-08-21.md`.
