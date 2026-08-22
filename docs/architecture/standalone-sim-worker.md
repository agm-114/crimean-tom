# Campaign-owned standalone simulation workers

This is a delivery plan, not an implemented worker. No spawn API, process, or
scheduler change is claimed here.

The campaign should be able to **create and destroy simulation capacity that no
player Sea Power window owns**. Today that work either stays inside the campaign
service process or requires a human to start `Sea Power.exe` with
`pnpm worker:start`. Those are not the same thing, and a full Unity window is
not a headless sim.

## Why this exists

Crimean Tom's theater is larger than one tactical scene. Islands with no player
looking at them still have to move, detect, spend, and sometimes fight. The
durable owner of that work is the campaign journal. The question is only **which
process is allowed to advance assigned islands**.

| Kind | What it is today | Who starts it | Owns a Sea Power window? |
| --- | --- | --- | --- |
| Player tactical client | `Sea Power.exe` + companion | Player / operator (`pnpm dev`, named worker scripts) | Yes |
| In-process Operational kernel | Campaign engine inside the service | The service process itself | No |
| Campaign-owned standalone sim worker | **Not implemented** | Coordinator (planned) | No, unless a later Sea Power farm track is separately proven |

[System architecture](system-architecture.md) already lists an **Operational
worker**: a .NET process that runs deterministic unattended simulation for
assigned shards and must not allocate authority. This plan is that component,
plus an explicit spawn/lifecycle owned by the coordinator.

It is **not** a headless Sea Power instance. Installed Sea Power is a Unity
game with a window, Steam, GPU, camera origin, and a full instantiated
`ObjectBase` set. Named workers on this host are ordinary game processes. See
[Tactical bubble](../research/tactical-bubble.md).

## What "not owned by any game instance" means

A standalone sim worker:

- has **no player session**, no HUD/camera authority, and no filtered player
  projection duties;
- cannot be a `RenderProxy` or Observer for a human (those remain client roles);
- receives fenced Operational work leases only for islands the scheduler has
  decided need no player Sea Power. Tactical `Primary`/`Shadow` roles remain on
  the Sea Power assignment path;
- is spawned, drained, and stopped by the campaign coordinator, not by Steam
  or a player's launch path.

Player Sea Power clients remain the only place Tactical Unity physics, sensors,
weapons, and AI run. Standalone workers do not replace that.

## Two tracks (do not mix)

### Track A — Sea-Power-free Operational worker (default)

A .NET 8 process, same stack as the campaign service, using the existing
bidirectional gRPC transport and its ordered-session/reconnect conventions. It
hosts a **shard of the campaign engine**: Operational and Strategic islands
only.

The current wire contract is not already generic enough for Track A:
`CompanionHello`, tactical capacity, and `TacticalAssignment` describe a Sea
Power companion, and the current state validator permits active assignments
only at Tactical fidelity. A1 therefore requires an explicit protocol addition
for operational-worker hello, work leases, and results. Track A must not encode
Operational work as a fake `TacticalAssignment` or invent a game session ID.

This is the right first spawn target because:

- `LandAbstract` and any ceiling below Tactical must never enter Sea Power;
- the kernel already resolves detection, uncertainty, expenditure, damage, and
  withdrawal without Unity;
- Steam, GPU, shared `PlayerPrefs`, and second-instance fights do not apply;
- the coordinator can scale unattended work off the service process without
  pretending Sea Power is headless.

**Must not own:** lease/epoch allocation, canonical campaign-clock allocation,
journal writer identity, player commands, Sea Power materialization.

### Track B — Unattended Sea Power farm (optional, later)

Coordinator-launched `Sea Power.exe` + companion with **no player session**,
used only when an island needs Tactical fidelity and no player client is
present. This is still a Unity window until a headless/game-server mode is
proven on the pinned build. It inherits every named-worker constraint already
on disk: Steam `RestartAppIfNecessary`, shared native profile paths, one camera
origin, instantiated-set cost, thin portable checkpoints.

Track B is forbidden as the default spawn. It is a separate evidence gate after
Track A exists, live two-container migrate is honest, and the empty tactical
shell can load without a human.

Do not describe Track B as headless. Do not use it for `LandAbstract`.

## Spawn and lifecycle (planned contract)

Coordinator-owned, journaled, fail-closed:

```text
Requested -> Spawning -> Hello -> Eligible -> Assigned
                |                      |
                +--------+-------------+-> Draining -> Stopped
                         |
                         +-> Failed
```

Planned operator/control surface (names are indicative):

- `POST /control/campaigns/{id}/sim-workers` — kind `operational` (Track A) or
  later `sea-power-farm` (Track B), capacity, reserve, and required build
  fingerprints. This is an authenticated coordinator operation, not a remote
  process-execution endpoint: worker executable, arguments, environment, and
  working directory come from an allow-listed local profile and are never
  caller-supplied strings;
- hello advertises a journaled spawn ID, worker instance ID, `workerKind`,
  `playerSessionBound=false`, health, eligibility, and exact capability
  (Operational engine/version vs Sea Power tactical profiles). A one-time spawn
  credential binds the hello to the requested campaign and expected profile;
- scheduler places islands whose desired fidelity is at most Operational onto
  Track A workers in preference to crowding a player client or the service
  process;
- islands that require Tactical stay on a player client, stay Operational until
  a client exists, or wait for an authorized Track B spawn;
- drain/stop must stop new work, fence every operational lease or Tactical
  Primary, commit or reject in-flight work, allow a bounded graceful exit, then
  terminate only the exact supervised child identity. PID alone is not an
  identity: record and verify the spawn ID, process start time, executable, and
  OS process-group/job membership so PID reuse cannot kill an unrelated process.

The service remains the only writer of leases, epochs, and committed events.
A standalone worker is a simulation source, not a second coordinator.

### Track A protocol boundary

Track A reuses the proven ordered stream, acknowledgements, reconnect cursor,
authentication, and backpressure rules, but adds discriminated Operational
messages rather than weakening the Tactical ones:

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

- One journaled spawn record maps to at most one accepted worker instance.
  Duplicate or unsolicited hellos fail closed.
- The coordinator places children in an OS job/process group, applies configured
  CPU/memory/concurrency limits, captures bounded logs, and reports pressure to
  the scheduler. Advertised island capacity is not a substitute for host
  resource limits.
- On service restart, the coordinator reconciles journaled spawn records with
  verified process identities. It either re-authenticates and re-leases a
  matching child or fences and terminates it; it never silently adopts an
  arbitrary process and never assumes an old lease survived.
- On worker crash or timeout, outstanding leases expire without committing
  partial results. The service replays the immutable input on another Track A
  worker or in-process. Journal recovery, not worker-local mutable state,
  determines canonical progress.
- Shutdown/restart tests must cover PID reuse, duplicate hello, orphan cleanup,
  crash during result publication, log saturation, and a child that ignores the
  graceful-stop deadline.

## Scheduler rules

1. No player interest and ceiling ≤ Operational → Track A operational work
   lease (or in-process kernel until Track A is implemented). Do not create an
   active `TacticalAssignment` for it.
2. Player interest and representable Tactical island → existing player client
   path. Do not steal that island onto a standalone worker while the player is
   the live view.
3. Tactical desired, no player client, Track B unauthorized → remain Operational
   in the kernel / Track A. Do not silently launch Sea Power.
4. `RenderProxy` and player Observer never attach to a standalone worker.
5. Compatibility fingerprints still fail closed. An Operational worker does not
   need a Sea Power runtime hash; a Track B farm worker does.

## Delivery slices (implementation comes later)

| Slice | Outcome | Gate before calling it done |
| --- | --- | --- |
| A0 | This document and claim language | Review only |
| A1 | Coordinator can spawn/stop a Track A process bound to one campaign | Operational hello with `playerSessionBound=false`; one-time spawn credential; process identity/job fencing; service restart does not leak or mis-adopt children |
| A2 | Scheduler assigns non-player Operational islands to Track A | Broad kernel/protocol story: distinct operational lease/result messages; island leaves in-process kernel; outcomes are validated and journaled only by the service; crash replays the same input; player client never required |
| A3 | Scale-out: more than one Track A worker, sticky leases, drain | Placement uses advertised and host-enforced capacity; duplicate instances for one spawn record and overlapping island leases are rejected |
| B0 | Track B spawn design against named-worker evidence | Written admission that it is a windowed Unity process |
| B1 | Unattended Sea Power farm spawn | Live proof: no player session, tactical shell loaded, Knox-only island, same migrate honesty as player workers |

A1–A3 must not wait on B0–B1. B1 must not start before live two-container
migrate for player workers has an honest result (success or documented
failure).

## Explicit non-claims

- Standalone workers are not implemented in this layer.
- Sea Power is not headless.
- Spawning a Unity window is not cheaper than leaving the island Operational.
- Track A does not match Sea Power tactical physics, sensors, or weapons.
- Track B does not unlock hot-shadow, untrusted MP, or a qualified combat
  bubble.
- The campaign service spawning processes does not make PostgreSQL, Docker, or
  extra brokers required for ordinary play.

## Related

- [System architecture](system-architecture.md) — Operational worker component.
- [Load management](load-management.md) — health, eligibility, island movement.
- [Federated simulation](federated-simulation.md) — roles, leases, no proxy
  promotion.
- [Dynamic campaign architecture](dynamic-campaign-design.md) — service vs
  Sea Power boundary.
- Named worker operations: [testing.md](../operations/testing.md).
