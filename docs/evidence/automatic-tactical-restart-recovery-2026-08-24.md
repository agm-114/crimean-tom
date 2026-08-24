# Automatic tactical restart recovery, 24 August 2026

## Scope

This slice closes the protocol-level case where canonical Tactical authority
survives but the corresponding Unity object does not survive a Sea Power or
companion game-session restart. It is a real ServiceHost plus real companion
process proof with a typed fake game peer. It is not yet a live Sea Power
process-restart latency measurement.

The companion now records the game-session ID in which each owned assignment
was actually materialized. On a later heartbeat it compares the authenticated
worker projection with the game-owned resource registry. An assignment is
reported missing only when all of these are true:

1. the canonical assignment is an Active Tactical Primary or Shadow owned by
   this worker;
2. no exact local owned-resource record exists; and
3. the durable materialization marker belongs to a different game session.

The third condition prevents an ordinary Preparing/activation interval from
being mistaken for a process restart.

## Recovery transaction

The authenticated local recovery endpoint validates the reporting live worker
and every assignment owner. For each accepted missing assignment it:

1. fences the stale epoch through `Active -> Quiescing -> Released`;
2. returns the entity to Operational fidelity when no other Tactical fence
   remains;
3. evaluates only the recovered interaction islands;
4. uses aggregate campaign Tactical capacity while pinning placement to the
   reporting worker; and
5. creates and queues a fresh assignment with the worker's current runtime,
   catalog, materialization profile, and a strictly newer authority epoch.

The replacement is restored from the newest semantically valid committed
checkpoint. Candidate blobs whose payload assignment, epoch, or entity does
not match the canonical candidate are skipped. This matters because the broad
migration story intentionally stores a hash-disagreement blob that is valid
content-addressed data but is not a restorable owned-unit checkpoint.

Activation-grant recovery also stopped treating a durable grant marker as
proof that a Unity object still exists. It now returns early only when the exact
assignment, entity, epoch, and Active lifecycle are present; otherwise it
restages the committed checkpoint and recreates/reactivates the owned object.

## Defects found while qualifying the path

- A direct `Active -> Released` recovery transition violated the assignment
  state machine. Recovery now passes through `Quiescing`.
- `TacticalIslandMigratedEvent` changed source assignment, destination
  assignment, and entity revisions in canonical state without updating the
  SQLite materialized `subject_revisions` table. A later append therefore saw
  durable revision 2 while canonical state expected 3. The journal now records
  all three migration revision effects.
- Replacement scheduling initially inherited unrelated player-interest state
  and could collide with an island already staged for migration. Recovery now
  evaluates only recovered entities and keeps worker affinity explicit in the
  scheduler request hash.
- The migration commit used a per-entity final-checkpoint staging key instead
  of the original operation idempotency key. After restart recovery replaced
  the destination, replaying the committed migration was evaluated as a new
  migration against the fresh Active Primary. Migration commits now retain a
  stable operation key, consult durable history before current-plan
  validation, and report `migration-superseded` without ever reissuing an
  activation grant to a released epoch.

## Verification

| Check | Result |
| --- | ---: |
| Solution/test-project compile | Passed, zero warnings/errors |
| Campaign integration suite | 11/11 passed in 6.94 seconds |
| Protocol suite | 12/12 passed in 51.32 seconds |
| Broad real-host/companion restart story | Passed in 51 seconds |

The broad story proves initial fenced migration, destination activation,
companion stop, loss of all fake game-owned resources, restart with the same
durable companion state and a new game session, automatic stale-epoch release,
fresh assignment materialization/activation, safe historical migration replay,
and the same replay result after a ServiceHost restart. The recovered runtime
assignment ID and authority epoch are compared exactly with canonical state;
the test does not merely wait for any Active assignment.

## Remaining boundary

This is protocol qualification, not a claim of measured live Sea Power restart
recovery. A live run still needs to kill/relaunch Sea Power, observe the fresh
health marker and game-session ID, measure selection-to-materialization latency,
confirm terrain preparation for recovered land sites, and prove that no native
or proxy duplicate remains. Recovery is currently limited by the qualified
owned-unit materialization profiles and checkpoint portability; incompatible
catalog upgrades safely fall back to Operational simulation rather than
inventing Tactical state.
