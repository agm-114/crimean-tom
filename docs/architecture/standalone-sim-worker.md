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
- receives **Primary** (or later Shadow) only for islands the scheduler has
  decided need no player Sea Power;
- is spawned, drained, and stopped by the campaign coordinator, not by Steam
  or a player's launch path.

Player Sea Power clients remain the only place Tactical Unity physics, sensors,
weapons, and AI run. Standalone workers do not replace that.

## Two tracks (do not mix)

### Track A — Sea-Power-free Operational worker (default)

A .NET 8 process, same stack as the campaign service, speaking the existing
worker gRPC hello/heartbeat/assignment protocol. It hosts a **shard of the
campaign engine**: Operational and Strategic islands only.

This is the right first spawn target because:

- `LandAbstract` and any ceiling below Tactical must never enter Sea Power;
- the kernel already resolves detection, uncertainty, expenditure, damage, and
  withdrawal without Unity;
- Steam, GPU, shared `PlayerPrefs`, and second-instance fights do not apply;
- the coordinator can scale unattended work off the service process without
  pretending Sea Power is headless.

**Must not own:** lease/epoch allocation, journal writer identity, player
commands, Sea Power materialization.

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
  later `sea-power-farm` (Track B), capacity, reserve, catalog fingerprint;
- hello advertises `workerKind`, `playerSessionBound=false`, health, eligibility,
  and exact capability (Operational-only vs Sea Power tactical profiles);
- scheduler places islands whose desired fidelity is at most Operational onto
  Track A workers in preference to crowding a player client or the service
  process;
- islands that require Tactical stay on a player client, stay Operational until
  a client exists, or wait for an authorized Track B spawn;
- drain/stop must fence Primary, commit or reject in-flight work, then kill only
  the recorded child PIDs.

The service remains the only writer of leases, epochs, and committed events.
A standalone worker is a simulation source, not a second coordinator.

## Scheduler rules

1. No player interest and ceiling ≤ Operational → Track A (or in-process kernel
   until Track A is implemented).
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
| A1 | Coordinator can spawn/stop a Track A process bound to one campaign | Hello with `playerSessionBound=false`; kill is PID-fenced; service restart does not leak children |
| A2 | Scheduler assigns non-player Operational islands to Track A | Broad kernel/protocol story: island leaves in-process kernel, outcomes still journaled, player client never required |
| A3 | Scale-out: more than one Track A worker, sticky ownership, drain | Placement uses advertised capacity; overlapping spawn is rejected |
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
