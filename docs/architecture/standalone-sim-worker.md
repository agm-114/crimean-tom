# Campaign-owned standalone simulation workers

Track A is implemented: the campaign service can spawn and stop Sea-Power-free
Operational workers, and the scheduler can assign non-player islands to them.
Track B (unattended Sea Power farm windows) is not implemented.

Crimean Tom workers are ordinary processes. Installed Sea Power is a Unity game
with a window. A standalone Track A worker is **not** a headless Sea Power
instance. See [Tactical bubble](../research/tactical-bubble.md).

## Why this exists

Crimean Tom's theater is larger than one tactical scene. Islands with no player
looking at them still have to move, detect, spend, and sometimes fight. The
durable owner of that work is the campaign journal. The question is only
**which process is recorded as the placement target and, in a later slice,
executes leased work**.

| Kind | What it is | Who starts it | Owns a Sea Power window? |
| --- | --- | --- | --- |
| Player tactical client | `Sea Power.exe` + companion | Player / operator (`pnpm dev`, named worker scripts) | Yes |
| In-process Operational kernel | Campaign engine inside the service | The service process itself | No |
| Campaign-owned Track A worker | `CrimeanTom.Campaign.OperationalWorker` | Coordinator `POST /control/campaigns/{id}/sim-workers` | No |

The journal writer still **resolves** Operational ticks in-process. Track A is
the campaign-owned **placement, capacity, and fencing target** in this slice.
Sharding `CampaignSimulation` into the worker process is a follow-on. Assigned
Operational islands are not frozen the way Sea Power Tactical islands are, so
theater time does not stop when Track A owns them.

## What "not owned by any game instance" means

A Track A worker:

- has **no player session**, no HUD/camera authority, and no filtered player
  projection duties (`playerSessionBound=false`, `workerKind=operational`);
- cannot be a `RenderProxy` or Observer for a human (those remain client roles);
- receives a placement/fencing **Primary** assignment only for islands whose
  desired fidelity is at most Operational. In this slice the service still
  executes their ticks in-process;
- is spawned, drained, and stopped by the campaign coordinator.

Player Sea Power clients remain the only place Tactical Unity physics, sensors,
weapons, and AI run. Track A does not replace that.

## Two tracks (do not mix)

### Track A: Sea-Power-free Operational worker (implemented)

A .NET 8 process, same stack as the campaign service, reusing the existing
ordered gRPC stream and heartbeat conventions. Its control-plane placement is a
distinct `OperationalAssignment`, not a `TacticalAssignment`. Hello must not
advertise a Sea Power runtime fingerprint, player task force, or materialization
profile. The process does not yet host a shard of `CampaignSimulation`; the
service remains the executor as well as the journal writer.

**Must not own:** lease/epoch allocation, canonical campaign-clock allocation,
journal writer identity, player commands, Sea Power materialization.

### Track B: Unattended Sea Power farm (not implemented)

Coordinator-launched `Sea Power.exe` + companion with **no player session**.
Still a Unity window. Forbidden as the default spawn. Do not describe it as
headless. Do not use it for `LandAbstract`.

## Spawn and lifecycle

```text
Requested -> Spawning -> Hello -> Eligible -> Assigned
                |                      |
                +--------+-------------+-> Draining -> Stopped
                         |
                         +-> Failed
```

Control surface:

- `POST /control/campaigns/{id}/sim-workers`: kind `operational` only. Capacity
  and reserve are the assigned-island budget. Overlapping spawn for the same
  worker id is rejected.
- `GET /control/campaigns/{id}/sim-workers`: recorded children and liveness.
- `DELETE /control/campaigns/{id}/sim-workers/{workerId}`: PID/start/executable-
  fenced kill and immediate session removal, then release the control-plane
  placements. A3 must replace this with bounded work-result drain before kill.
- Hello advertises `workerKind=operational` and `playerSessionBound=false`, plus
  a per-spawn ID and credential that must match the exact live supervisor
  record. Unsolicited Operational hellos fail closed.
- Service start reaps leftover children; Windows job objects kill children when
  the service process dies.
- Scheduler places islands whose desired fidelity is at most Operational onto
  Track A workers. Sticky ownership. Drain/evacuation yields those assignments.
- Islands that require Tactical stay on a player client, stay Operational on
  Track A / the in-process kernel until a client exists, or wait for Track B.
- Track A workers reject Sea Power tactical frames, checkpoints, and activation
  grants.

The service remains the only writer of leases, epochs, and committed events.

### Required protocol boundary for out-of-process simulation

Track A reuses the proven ordered stream, acknowledgements, reconnect cursor,
authentication, and backpressure rules, but adds discriminated Operational
messages rather than weakening the Tactical ones before it can execute ticks:

- an operational hello carries protocol/simulation/build fingerprints,
  supported fidelity ceilings and logical capacity; no fabricated Unity runtime
  hash, game session, materialization profile, or tactical capacity is sent;
- an operational work lease names one complete interaction island, authority
  epoch, input revision/hash, ordered time range, ruleset/fingerprint, deadline,
  and idempotency key;
- the result echoes that fence and supplies proposed events, output state/hash,
  consumed time range, and resource observations. It is never a journal append;
- the coordinator validates the lease, revision, hashes, event schema,
  conservation rules, and time range before committing atomically. Late,
  duplicated, partial, or post-fence results are rejected;
- retries replay the same immutable input and idempotency key. A different
  result for the same work identity quarantines the worker rather than choosing
  whichever arrived last.

`TacticalAssignment` and its materialization/checkpoint path remain the Track B
and player-client contract. Generalizing shared envelope code must not make an
Operational result eligible for Tactical activation.

### Supervision and recovery

- One durable supervisor spawn record maps to at most one accepted worker instance.
  Duplicate or unsolicited hellos fail closed.
- On Windows, the coordinator places children in a kill-on-close job object and
  captures bounded logs; job creation/configuration/assignment fails closed at
  spawn. Other hosts use explicit process-tree kill plus startup identity
  reaping and require their own crash-orphan qualification. Advertised island
  capacity is not a substitute for host CPU/memory limits; measured resource
  limits and pressure reporting remain A3 gates.
- On service restart, the coordinator kills any still-live child whose PID,
  start time, and executable match its record. It never silently adopts a child
  or assumes an old placement survived. The next scheduler pass releases
  placements whose worker session is absent before admitting replacement work.
- The control-plane slice has no worker-computed result to commit partially.
  A3 must make worker crash/timeout expire an immutable work lease and replay
  its input on another Track A worker or in-process.
- The broad integration story covers unsolicited hello, drain, exact child
  death, restart cleanup, and canonical placement release. PID-reuse injection,
  log saturation, crash during A3 result publication, and a child that ignores
  graceful stop remain later negative gates.

## Scheduler rules

1. No player interest and ceiling ≤ Operational → Track A when a worker is
   connected; otherwise an in-process placement. The service kernel executes
   both until operational work leases/results are implemented.
2. Player interest and representable Tactical island → existing player client
   path. Do not steal that island onto a standalone worker while the player is
   the live view.
3. Tactical desired, no player client, Track B unauthorized → remain Operational
   in the kernel / Track A. Do not silently launch Sea Power.
4. `RenderProxy` and player Observer never attach to a standalone worker.
5. Compatibility fingerprints still fail closed. An Operational worker does not
   send a Sea Power runtime hash.

## Delivery slices

| Slice | Outcome | Status |
| --- | --- | --- |
| A0 | This document and claim language | Done |
| A1 | Coordinator spawn/stop bound to one campaign | Done: hello `playerSessionBound=false`; per-spawn credential; PID/start/executable and job fencing; restart does not leak children |
| A2 | Scheduler places non-player Operational islands | Done for placement/fencing; tick execution and journaling remain in-process |
| A3 | Execute leased Operational work out of process | Not started: distinct lease/result protocol and deterministic replay gates above are required |
| B0 | Track B spawn design against named-worker evidence | Not started |
| B1 | Unattended Sea Power farm spawn | Not started |

## Explicit non-claims

- Sea Power is not headless.
- Track A does not match Sea Power tactical physics, sensors, or weapons.
- Track A does not run a second campaign journal or allocate authority.
- Operational ticks are still resolved by the service kernel in this slice.
- Track B does not unlock hot-shadow, untrusted MP, or a qualified combat
  bubble.
- The campaign service spawning processes does not make PostgreSQL, Docker, or
  extra brokers required for ordinary play.

## Related

- [System architecture](system-architecture.md): Operational worker component.
- [Load management](load-management.md): health, eligibility, island movement.
- [Federated simulation](federated-simulation.md): roles, leases, no proxy
  promotion.
- [Dynamic campaign architecture](dynamic-campaign-design.md): service vs
  Sea Power boundary.
- Named worker operations: [testing.md](../operations/testing.md).
