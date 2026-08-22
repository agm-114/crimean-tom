# Supported claims

Authenticated `GET /supported-claims` is the machine-readable copy of this
document. If the two disagree, the HTTP contract is wrong.

Game build in evidence: Sea Power `0.8.2-23444+s358`. A later game update can
invalidate runtime fingerprints, UI automation, and catalog hashes. Assignment
preparation and activation fail closed when the companion's current runtime or
catalog fingerprint differs from the fingerprint pinned into that assignment.
The scheduler does not yet enforce a campaign-global build fingerprint.

## Supported

- Loopback-authenticated companion HTTP and campaign gRPC/HTTP.
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

## Remaining qualification

Soak campaigns, a clean-machine install/update/export runbook, and independent
hostile-client review are not claimed. Resource exhaustion and catalog fuzzing
remain follow-on work. Campaign-spawned standalone simulation workers are
planned, not implemented; they are not headless Sea Power. Documentation here
is limited to behavior covered by the
broad kernel and protocol integration stories plus the live evidence already on
disk under `docs/evidence/`.
