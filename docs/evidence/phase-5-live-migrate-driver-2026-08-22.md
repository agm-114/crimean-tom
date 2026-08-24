# Live two-container migrate driver, 22 August 2026

## Scope

Operator driver for the already-implemented authenticated migrate path:

- `scripts/sea-power-worker-live-migrate.ps1`
- pnpm wrapper: `pnpm worker:live-migrate`

It does **not** replace `pnpm worker:live-proof` (coexistence only) or invent a
new coordinator API. It pins source preparation onto a named worker, then calls
`scripts/sea-power-worker-migration-proof.ps1`.

## What this driver adds

The 21 August proof required a leftover campaign, a remembered entity ID, and a
manual `-AllowSourcePreparation` repair sequence because the live Primary still
sat on a disconnected default companion. This driver:

1. optionally `POST /control/campaigns` + `POST .../bootstrap` (`-CreateCampaign`);
2. selects the bootstrap `primaryEntityId` or the sole singleton island;
3. keeps ordinary preflight read-only; `-Execute` passes
   `-AllowSourcePreparation` so any required `prepare-source` pins
   `-SourceWorkerId`;
4. treats `-CreateCampaign` as an explicit, separate setup mutation that exits
   after create/bootstrap so workers can reconnect before preflight;
5. exits with the migration-proof codes (`0` preflight ready / repair step,
   `2` blocked, `1` failure, `0` with `MIGRATION_PROOF_SUCCEEDED` only after a
   complete live migrate).

## Preconditions

- Campaign service on loopback with a current-user token.
- Two named Sea Power workers already proven by `pnpm worker:live-proof`.
- Each worker bound to the **same** `CampaignId`, with a loaded player task
  force that can materialize the Knox surface family.
- `-CreateCampaign` does not restart those workers and cannot be combined with
  `-Execute`. Reconnect both workers to the returned campaign before preflight.

## Commands

```powershell
# Optional setup mutation: create/bootstrap only, then exit and reconnect workers
pnpm worker:live-migrate -- -CreateCampaign

# Read-only preflight against an existing journal (never prepares or migrates)
pnpm worker:live-migrate -- `
  -CampaignId <existing-id> `
  -SourceWorkerId worker-a `
  -DestinationWorkerId worker-b `
  -EvidencePath .run/evidence/live-migrate-preflight.json

# Mutate only after inspecting preflight. Execute may pin source preparation
# before running the migration flow.
pnpm worker:live-migrate -- `
  -CampaignId <existing-id> `
  -SourceWorkerId worker-a `
  -DestinationWorkerId worker-b `
  -Execute `
  -EvidencePath .run/evidence/live-migrate-execute.json
```

## Claim status

**Authenticated live migration across two real Sea Power processes is still
unsupported.** This layer ships the closed-loop operator path. Treat
`MIGRATION_PROOF_SUCCEEDED` plus destination Active / source Released as the
only evidence that may later lift that non-claim.

## Review verification, 23 August 2026

- `pnpm worker:live-proof` passed with two live Sea Power processes, two game
  sessions, two client IDs, and two campaign sessions on campaign
  `08eed6175b1a4e1abf50004d4e5808c8`.
- Read-only `pnpm worker:live-migrate` detected the already-committed migration
  recovery state and exited `0` with `MIGRATION_PROOF_RECOVERY_PENDING`.
- The evidence recorded `executeRequested=false` and
  `allowSourcePreparationRequested=false`; campaign commit position was `252`
  before and after preflight.
- No `-Execute` migration was attempted, so the unsupported claim remains.

The live run also exposed 4,096 legacy inbound spool records that permanently
blocked a second companion. The service now retires successfully processed
records from the pending set, bounds retained processed evidence, and abandons
unprocessed session records on disconnect so the companion can resend them.

## Related

- Coexistence: `docs/evidence/phase-5-named-worker-evidence-2026-08-21.md`
- Preflight blockers and repair: `docs/evidence/phase-5-migration-proof-2026-08-21.md`
- Partial commit-without-activation: `docs/evidence/phase-5-migration-activation-recovery-2026-08-21.md`
