# Phase 5 migration activation recovery — 21 August 2026

## Disposition

Cross-worker island migration **committed successfully** on campaign `08eed6175b1a4e1abf50004d4e5808c8` at commit **252**, but **destination activation did not converge** after a runtime DLL deploy emptied both worker resource surfaces and restarted game pipe sessions.

This is an honest partial live result: campaign authority moved, worker-a retained a suspended source record, worker-b durable companion state still has assignment, staged checkpoint hash, committed migration, and activation grant — but replay failed before registry convergence because owned mutations were not armed for the new session and the destination runtime no longer retained the staged owned record.

## Live evidence (commit 252)

| Item | Value |
| --- | --- |
| Campaign | `08eed6175b1a4e1abf50004d4e5808c8` |
| Migration commit | `252` |
| Destination Primary | `assignment:84ab0b5a494098d4b8371d69a01da596`, epoch `4`, Active on worker-b (`e3b643…`) |
| Source Primary | `assignment:b60081c1cc157cbe9cb60163d54e6125`, epoch `3`, Released on worker-a |
| Game entity | `unit:crimean-tom:a2d904987998596a48868b21ca66d3ec` |
| Destination runtime record | empty after game restart (expected before recovery) |
| Source runtime record | empty after game restart (safe for released Primary) |
| Companion durable cursor (worker-b) | `252` |
| Prior failure | `mutation-disabled: Owned mutations are not armed for the current pipe session.` then missing owned record on unsuspend |

### Root cause

1. Activation replay attempted owned mutations before arming the new game pipe session.
2. Activation replay assumed the destination runtime still held the suspended retained record from pre-restart staging. After game restart the resource surface was empty and recovery did not re-stage from the committed checkpoint content store.

The protocol integration fake transport did not enforce mutation arming or empty-runtime restage, so the defect escaped broad story coverage until live replay after DLL deploy.

## Fix landed in workspace (not deployed to live companions in this pass)

1. `OwnedMutationContracts.ResolveActivationIdempotencyKey` converts staging keys to canonical `{migrationBase}-{entityId}-activate` form within budget.
2. `OwnedMutationContracts.ResolveStagingIdempotencyKey` resolves the bounded staging key from durable staging state or activation grant keys.
3. Coordinator activation grants use the resolver instead of blind suffix concatenation.
4. Companion activation recovery waits for mission scene materialization readiness, arms owned mutations for the current game session and runtime fingerprint, inspects exact owned resources, re-stages the committed checkpoint from campaign content when the exact assignment/entity/epoch record is absent, unsuspends when suspended, converges when already active, and rejects conflicting records.
5. Runtime bridge treats already-enabled unsuspend as idempotent replay only after `ResolveOwned` revision/authority fencing succeeds.
6. Broad protocol integration covers bounded keys, invalid payload enforcement, legacy normalization, destination runtime restart with empty resources, mutation arming, re-stage from content, activation, durable grant replay convergence, and source released-worker empty safety via fake runtime validation.
7. Migration proof recovery waits for both named campaign connections and exact active destination materialization; verifies the released source is not runtime-active; never initiates another migration.

## Recovery plan (operator)

**Do not fabricate campaign state.** Backward-compatible companion recovery converges from the persisted legacy grant and durable staging sidecar without creating a new migration. Apply rebuilt companion artifacts, then let durable grant replay recover the destination runtime. Restart the campaign service only if a new grant must be generated; for commit 252 the existing grant should replay after companion restart.

1. Build isolated artifacts without deploying RuntimeBridge into live Sea Power while games run:
   ```powershell
   dotnet build src/CrimeanTom.Companion/CrimeanTom.Companion.csproj --configuration Debug --artifacts-path .run/build-artifacts/companion
   dotnet build src/SeaPower.RuntimeBridge/SeaPower.RuntimeBridge.csproj --configuration Debug -p:CopyPluginToGame=false --artifacts-path .run/build-artifacts/runtimebridge
   pnpm test:campaign
   pnpm test:protocol
   ```
2. Deploy rebuilt `CrimeanTom.Companion` to **worker-b** layout (same campaign binding and state directory). Deploy RuntimeBridge only during a controlled worker-b game restart window if runtime behavior changes are required.
3. Restart **worker-b companion only** (leave campaign service, worker-a, and games running if desired).
4. Ensure worker-b tactical mission scene is loaded and materialization-ready (valid player task force, managers available).
5. Confirm worker-b reconnects at durable cursor `252`, receives activation grant replay, arms mutations for session `a965…`, re-stages from staged checkpoint hash, and unsuspends `assignment:84ab0b5a…` / `unit:crimean-tom:a2d904…` epoch `4`.
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

7. Confirm campaign registry lists **both** worker-a and worker-b sessions, worker-b Primary Active at destination assignment/epoch, and worker-b `/game/mutations/surface/resources` shows the entity `active` at the expected assignment/epoch/revision. Confirm worker-a released source assignment has **no** active owned record (empty resources are safe).

## Quota status

No quota exhaustion during this fix pass.
