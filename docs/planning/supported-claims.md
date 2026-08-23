# Supported claims

Authenticated `GET /supported-claims` is the machine-readable copy of this
document. If the two disagree, the HTTP contract is wrong.

Game build in evidence: Sea Power `0.8.2-23444+s358`. A later game update can
invalidate runtime fingerprints, UI automation, and catalog hashes. Assignment
preparation and activation fail closed when the companion's current runtime or
catalog fingerprint differs from the fingerprint pinned into that assignment.
The scheduler does not yet enforce a campaign-global build fingerprint.

## Supported

- Bearer-authenticated loopback companion writes and campaign control writes.
  Hosted monitor reads use a same-origin local cookie issued on monitor entry;
  this is not an authenticated operator identity.
- Deterministic campaign kernel with a SQLite WAL journal and content-addressed
  local blobs. Ordinary play does not require Docker or an external database.
- Coalition-filtered HTTP projections that omit opposing true-state sentinels.
  A player process that still holds the full campaign view is trusted private
  co-op, not an untrusted public server.
- Live replica checkpoint SHA-256 disagreement refuses migration. There is no
  majority vote.
- `RenderProxy` cannot receive simulation authority. The only activation path is
  a fenced prepared Shadow; Observer is never a Primary activation source.
- Online backup and side-directory restore verify event/hash integrity.
- Operator pause rejects campaign time advances until resume. Drain sets
  scheduler health to Draining and eligibility to DrainOnly.
- Campaign-owned Track A Operational workers can be spawned, checked with an
  additive per-spawn identity, and used for non-player island placement/fencing.
  They also receive the shared service bearer token; health and capacity are
  self-reported. Tick execution remains in the service process.

Failover is the cold checkpoint / operational path. Hot Shadow promotion is
implemented as fenced island migration in tests with a mock destination worker;
it is not a claimed live two-process Sea Power recovery objective.

## Explicitly unsupported

- Hot-shadow failover after primary process death within a measured recovery
  objective.
- Untrusted public multiplayer and hostile-client adjudication.
- SignalR as a canonical commit path. Player envelopes never commit campaign
  outcomes directly.
- Authenticated live migration across two real Sea Power containers.
- PostgreSQL, brokers, or object storage as requirements for ordinary players.
- Out-of-process Operational tick execution or worker-computed result commits.
- Schema/algorithm upgrade of an existing campaign save.
- A bounded long-running-campaign performance envelope; published throughput
  measurements cover short, near-empty journals.
- A human-length tactical assignment under the default unattended clock.
- A tactical loss against a campaign-owned hostile committed back to the journal.
- Player-session continuity across a ServiceHost restart.
- Authenticated writes from the hosted production monitor.

## Remaining qualification

Soak campaigns, a clean-machine install/update/export runbook, and independent
hostile-client review are not claimed. Resource exhaustion and catalog fuzzing
remain follow-on work. Campaign-spawned Track A Operational workers can be
spawned, hello'd, and assigned; Operational ticks are still resolved in-process.
They are not headless Sea Power. Documentation here
is limited to behavior covered by the
broad kernel and protocol integration stories plus the live evidence already on
disk under `docs/evidence/`.
