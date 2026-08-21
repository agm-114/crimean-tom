# Phase 5 migration activation recovery — 21 August 2026

## Disposition

Cross-worker island migration **committed successfully** on campaign `08eed6175b1a4e1abf50004d4e5808c8` at commit **252**, but **destination activation did not converge** because the companion issued an oversized activation idempotency key to `set-owned-surface-suspension`.

This is an honest partial live result: campaign authority moved, worker-a retained a suspended source record, worker-b retained a suspended destination record, and worker-b replay failed before registry convergence.

## Live evidence (commit 252)

| Item | Value |
| --- | --- |
| Campaign | `08eed6175b1a4e1abf50004d4e5808c8` |
| Migration commit | `252` |
| Destination Primary | `assignment:84ab0b5a494098d4b8371d69a01da596`, epoch `4`, Active on worker-b (`e3b643…`) |
| Source Primary | `assignment:b60081c1cc157cbe9cb60163d54e6125`, epoch `3`, Released on worker-a |
| Game entity | `unit:crimean-tom:a2d904987998596a48868b21ca66d3ec` |
| Destination runtime record | `suspended-retained`, revision `3` |
| Source runtime record | `suspended-retained` (expected after release) |
| Companion durable cursor (worker-b) | `252` |
| Failure | `mutation-precondition-failed: Owned entity, assignment, and idempotency identifiers are required.` from `CampaignCompanionClient.ApplyActivationGrantAsync` |

### Root cause

`QueueActivationGrants` built activation keys as `{stagingIdempotencyKey}-{entityId}-activate`. Staging keys already include `-migrate-{entityId}-stage`, so replayed activation keys grew to **133 characters**, above the owned-mutation **128-character** budget. `RuntimeOwnedMutationAdapter` rejects overlong idempotency keys with the generic identifier precondition error.

The protocol integration fake transport did not enforce the same identifier budget, so the defect escaped broad story coverage until live replay.

## Fix landed in workspace (not deployed to live companions in this pass)

1. `OwnedMutationContracts.ResolveActivationIdempotencyKey` converts staging keys to canonical `{migrationBase}-{entityId}-activate` form within budget.
2. Coordinator activation grants use the resolver instead of blind suffix concatenation.
3. Companion activation inspects exact owned resources before dispatch; if assignment/entity/epoch is already active at revision >= durable expected revision, it persists observed revision and grant without re-unsuspending. Legacy oversized grant keys normalize via `ResolveActivationIdempotencyKey` without requiring a staged-idempotency sidecar.
4. Runtime bridge treats already-enabled unsuspend as idempotent replay only after `ResolveOwned` revision/authority fencing succeeds.
5. Broad protocol integration covers bounded-key, invalid payload enforcement, legacy normalization, and crash-after-enable replay via fake runtime validation.
6. Migration proof recovery selects the latest released Primary below destination epoch owned by requested source worker; ignores older ghost releases; verifies source exact assignment is not runtime-active; never initiates another migration.

## Recovery plan (operator)

**Do not fabricate campaign state.** Backward-compatible companion recovery should converge from the persisted legacy grant without requiring a staged-idempotency sidecar. Apply rebuilt companion artifacts, then let durable grant replay activate the destination. Restart the campaign service only if a new grant must be generated; for commit 252 the existing grant should replay after companion restart.

1. Build isolated artifacts (companion + runtime bridge) without stopping live services in the fix pass:
   ```powershell
   pnpm build
   pnpm test:protocol
   ```
2. Stop **worker-b companion only** (leave campaign service, worker-a, and games running if desired).
3. Deploy rebuilt `CrimeanTom.Companion` to worker-b layout (same campaign binding and state directory).
4. Restart worker-b companion against campaign `08eed6175b1a4e1abf50004d4e5808c8`.
5. Confirm worker-b reconnects at durable cursor `252`, receives activation grant replay, and unsuspends `assignment:84ab0b5a…` / `unit:crimean-tom:a2d904…` epoch `4` rev `3→4`.
6. Verify with read-only proof recovery:
   ```powershell
   pnpm worker:migration-proof -- `
     -CampaignId 08eed6175b1a4e1abf50004d4e5808c8 `
     -SourceWorkerId worker-a `
     -DestinationWorkerId worker-b `
     -CanonicalEntityId a2d904987998596a48868b21ca66d3ec `
     -Execute `
     -EvidencePath .run/evidence/migration-recovery-2026-08-21.json
   ```
   Expect `MIGRATION_PROOF_RECOVERY_SUCCEEDED` (or preflight `MIGRATION_PROOF_RECOVERY_PENDING` before companion restart).

7. Confirm campaign registry lists worker-b session with destination Primary Active and worker-b `/game/mutations/surface/resources` shows the entity `active` at the expected assignment/epoch/revision.

## Quota status

No quota exhaustion during this fix pass.
