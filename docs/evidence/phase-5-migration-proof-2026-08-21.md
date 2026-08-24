# Phase 5 cross-worker migration proof, 21 August 2026

## Scope

Evidence for the production-safe cross-worker migration driver:

- shared helpers: `scripts/sea-power-worker-proof-common.ps1`
- coexistence gate (unchanged claim): `scripts/sea-power-worker-live-proof.ps1`
- migration driver: `scripts/sea-power-worker-migration-proof.ps1`
- pnpm wrapper: `pnpm worker:migration-proof`

This document separates **implementation** (landed in repo), **preflight** (read-only checks run on the live host), and **proven results** (end-to-end migration outcome). As of this date only implementation and preflight are complete.

## Implementation

The migration driver reuses the coexistence proof, then fail-closes on:

- exact campaign binding;
- distinct live named workers with validated Sea Power PIDs, sessions, and non-synthetic fingerprints;
- a single eligible Tactical island (explicit `-CanonicalEntityId` or auto-selected singleton island);
- source Primary ownership on the requested `-SourceWorkerId`;
- destination migration-overlap headroom on `-DestinationWorkerId`;
- connected-worker affinity, assignment lifecycle, and exact source native materialization on the Active Primary assignment (`entities[]` entry matching `assignment:{id}` / `unit:crimean-tom:{entityId}` with `lifecycleState=active`, matching `authorityEpoch`, and positive `revision`);
- deterministic `prepare-shadow` destination selection for the requested `-DestinationWorkerId` (optional API pin via `preferredDestinationClientInstanceId`; preflight mirrors coordinator admission ordering and fails closed on mismatch);
- singleton Tactical island only (`multi-entity-island-unsupported` otherwise);
- authenticated `prepare-shadow`, source `quiesce` (`assignment:{id}`, `unit:crimean-tom:{entityId}`), and `migrate-tactical-island` orchestration with separate migrate-response vs commit-view polling.

Optional repair is split and off by default:

- `-AllowGhostRelease`: advance campaign time and run `resolve-expired` to release
  only a disconnected legacy Primary. After success, the driver recomputes the
  blockers and exits `0` as a resumable repair step. The usual blocker is
  `source-preparation-required` after the island demotes to Operational. The
  driver does not retain stale blockers or exit `1`.
- `-AllowSourcePreparation`: run coordinator `prepare-source` with
  `preferredWorkerClientInstanceId` pinned to the requested `-SourceWorkerId`.
  The command finds the singleton interaction island even if it is Operational
  and has no live Primary. It waits for an Active Primary and exact native
  materialization on that worker. The materialization must match the assignment
  ID, game entity ID, `lifecycleState=active`, `authorityEpoch`, and `revision`
  in `/game/mutations/surface/resources`. Migration preflight still requires
  Tactical fidelity and exact Active source materialization.
- `-AllowSchedulerPromotion`: ask the legacy scheduler to run `schedule`. The
  result is **nondeterministic** because the API cannot pin a worker. Prefer
  `-AllowSourcePreparation`. Pass this flag explicitly. `-AllowRepair` does not
  imply it.
- `-AllowRepair`: convenience alias for `-AllowGhostRelease` plus `-AllowSourcePreparation` only (deterministic repair sequence).

Execution requires `-Execute`. Without it, the driver stops after preflight.
Exit code `0` means that migration is ready or that a requested ghost-release
step completed. A completed ghost-release step may still require more repair.
Exit code `2` means that migration is blocked.

Evidence JSON omits secrets and may be written with `-EvidencePath`.

## Preflight (live host, campaign `08eed6175b1a4e1abf50004d4e5808c8`)

Command:

```powershell
pnpm worker:migration-proof -- `
  -CampaignId 08eed6175b1a4e1abf50004d4e5808c8 `
  -CampaignHttpUrl http://127.0.0.1:5081 `
  -CampaignTokenFile C:\Users\Varun\repos\crimean-tom\.run\campaign-service\service.token `
  -WorkersRunRoot C:\Users\Varun\repos\crimean-tom\.run\workers `
  -SourceWorkerId worker-a `
  -DestinationWorkerId worker-b `
  -EvidencePath C:\Users\Varun\repos\crimean-tom\.run\evidence\migration-preflight-2026-08-21.json
```

Coexistence phase: **passed**. `worker-a` and `worker-b` have distinct game
sessions, client instance IDs, and campaign sessions. `pnpm worker:live-proof`
produced the same result.

Migration preflight: **blocked** (exit `2`).

| Blocker | Meaning |
| --- | --- |
| `source-worker-mismatch` | USS Pharris Primary epoch 2 is owned by client `7cb34046…`, not `worker-a` (`39d07463…`). |
| `source-worker-offline` | Primary owner `7cb34046…` is the disconnected default campaign companion client, not a connected named worker. |

Neither named worker reports owned campaign objects (`activeObjectCount = 0`). The tactical island exists in campaign state but is not bound to the named worker containers.

Recommended repair before `-Execute`. This deliberate mutation did not run during this implementation pass:

```powershell
# Step 1: bounded ghost release only (releases disconnected legacy Primary assignments; exit 0 when complete, with `source-preparation-required` if the island demoted to Operational)
pnpm worker:migration-proof -- ...same args... -AllowGhostRelease -CanonicalEntityId <entity-id>

# Step 2: pinned source preparation on worker-a (deterministic worker affinity; works from Operational island with no live Primary)
pnpm worker:migration-proof -- ...same args... -AllowSourcePreparation -CanonicalEntityId <entity-id>

# Legacy fallback only if prepare-source is unavailable:
pnpm worker:migration-proof -- ...same args... -AllowGhostRelease -AllowSchedulerPromotion
```

Do **not** rely on `-AllowRepair` for scheduler promotion. After repair, verify the Primary owner matches `-SourceWorkerId` and the exact source surface entity is active for that assignment before `-Execute`. After migration, verify the destination reports the new Primary assignment as the sole active authority and the source no longer retains an active surface record for the released assignment.

## Proven results

**Cross-worker source→destination final-checkpoint migration is not yet proven on this host.**

**Partial live result (21 Aug 2026, campaign `08eed6175b1a4e1abf50004d4e5808c8`, commit 252):** migration committed with destination Primary Active on worker-b and source Primary Released, but destination activation failed on oversized activation idempotency keys. See `docs/evidence/phase-5-migration-activation-recovery-2026-08-21.md` for recovery steps.

Do not claim two-container island migration until `pnpm worker:migration-proof -Execute` succeeds with post-conditions showing:

- destination Primary Active on the requested destination worker;
- source Primary Released;
- destination reports active owned campaign objects and source does not retain authority.

## Related evidence

- Coexistence only: `docs/evidence/phase-5-named-worker-evidence-2026-08-21.md`
- Protocol-level migration with a synthetic second gRPC worker: see
  [Phase 5 live evidence](phase-5-live-evidence-2026-08-21.md), in the migration
  orchestration admission and cooldown section.
