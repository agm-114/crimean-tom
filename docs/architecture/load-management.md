# Load management and simulation-source control

## Purpose

The deterministic runtime removes Sea Power's FPS-driven timestep adjustment.
That is a correctness requirement, but it also removes a mechanism the game used
to trade simulation accuracy for throughput. Crimean Tom must respond to load by
reducing, moving, or degrading work without changing what one logical tick means.

This document defines the coordinator-owned load controller, client eligibility,
island migration, and global tempo policy. It complements the
[deterministic runtime adapter](deterministic-runtime-adapter.md) and the
[federated simulation protocol](federated-simulation.md).

## Non-negotiable invariants

- `FixedStepDuration` is fixed for a simulation version and synchronization
  domain. Overload never enlarges it, stretches it according to FPS, or skips
  logical ticks.
- `TargetTicksPerWallSecond` controls how quickly fixed logical ticks are
  consumed. It may change globally without changing simulation results.
- Every tempo change is a coordinator-ordered event with an effective tick.
- One synchronization domain has one tempo. A client, assignment, or island
  cannot unilaterally choose its own `Time.timeScale`.
- The current assignment epoch and lease fence every authoritative publication.
  A slow or disabled client cannot publish beyond its lead window or after its
  lease expires.
- Load shedding never drops committed events, lease/fence traffic, primary
  command acknowledgements, or information-scope filtering.
- Tightly coupled work moves as an interaction island. Moving one ship out of a
  coupled task force merely to lower load is unsafe unless the coupling
  experiments prove that split valid.

External strategic and operational engines may use event-driven execution, but
their visible progress is still mapped to the same canonical campaign clock.

## Coordinator load controller

The campaign coordinator owns a `LoadController` and assignment scheduler. Each
companion reports raw observations; the coordinator derives state using measured
baselines, exponentially weighted trends, hysteresis, and cooldowns. Thresholds
are versioned campaign policy produced by benchmarks, not hard-coded guesses.

A health report can include:

- completed tick, tick lag, observed tick throughput, and missed step deadlines;
- presentation frame rate and frame-time percentiles;
- CPU, GPU, memory, allocation, and process responsiveness;
- local pipe, remote transport, command, event, and checkpoint queue depth;
- checkpoint capture/apply time and materialization stalls;
- network round-trip, loss, retransmission, and durable-spool backlog;
- deterministic adapter mode, hook health, hash status, and recent divergence;
- user pause, scene transition, and game/companion connectivity state.

Self-reported capacity is a scheduling hint, not proof. The service also measures
acknowledgement latency, tick completion, queue growth, and missed grants. In an
untrusted deployment it applies the client trust model before accepting capacity
or health claims.

### Client health state

| State | Meaning | Scheduler behavior |
| --- | --- | --- |
| `Healthy` | Meets measured throughput, latency, and adapter requirements with reserve | May receive roles allowed by its eligibility |
| `Pressured` | Approaching a limit or showing a sustained adverse trend | Receives no speculative load; optional work is reduced |
| `Overloaded` | Cannot meet required tick/control objectives | Existing authority is drained or migrated |
| `Draining` | Coordinator is deliberately removing simulation responsibility | No new work; completes bounded handoffs only |
| `Quarantined` | Divergence, compatibility, integrity, or repeated health failure | No simulation authority until revalidated |
| `Offline` | Session is unavailable | Fence leases and recover assigned islands |

Health and simulation-source eligibility are separate:

| Eligibility | Meaning |
| --- | --- |
| `Eligible` | May receive new Primary, Shadow, or full Observer assignments compatible with its capabilities |
| `DrainOnly` | Receives no new simulation work; existing work is completed or migrated |
| `Disabled` | Remains connected for player commands, UI, and filtered Proxy presentation but cannot be a simulation source |
| `Quarantined` | Cannot receive simulation roles until compatibility, integrity, and health validation pass |

A client may request `DrainOnly`, for example after detecting thermal throttling
or a user pause. Only the coordinator changes canonical assignments. Disabling a
client as a simulation source must not disconnect its player.

## Load-shedding ladder

The controller applies the least disruptive action that restores headroom:

1. Reduce noncanonical presentation and diagnostic work: render quality,
   effects, optional UI polling, debug telemetry, and optional extraction/hash
   frequency. Never reduce lease/fence checks or mandatory correctness reports.
2. Remove optional Shadow or full Observer replicas, or replace an Observer with
   a visibility-filtered Proxy when that is safe.
3. Mark the client `DrainOnly` so it receives no new islands.
4. Migrate quiescent, non-engaged interaction islands to healthy clients with
   measured reserve capacity.
5. Demote suitable islands to the deterministic external Operational engine.
6. Migrate an engaged island only at a validated safe checkpoint/quiesce
   boundary. If no safe boundary exists, keep it in place until the interaction
   resolves or use a validated native-save transfer path.
7. Reduce the synchronization domain's global `TargetTicksPerWallSecond`.
8. Suspend only the unsafe island as a bounded last resort, then restore,
   operationally resolve, or force-resolve it according to campaign policy.

The order is a default policy, not a promise to perform pointless intermediate
steps. An imminent process failure may trigger immediate fencing and failover.
The coordinator records the pressure signal, selected action, rejected
alternatives, affected islands, and recovery result.

### Implemented Phase 5 scheduler slice

The service currently accepts a scheduler envelope containing observed and
controlled entity IDs, health, simulation-source eligibility, tactical entity
capacity, and transport backlog. Healthy sources target 30 ticks per wall
second, pressured/healthy-backlogged sources target 20, and worse health targets
10, while the simulation fixed step remains 33,333 microseconds.

Promotion consumes capacity by complete island. Non-eligible, overloaded,
draining, quarantined, offline, or backlog-saturated sources request Operational
fallback for suitable islands without raising already-Strategic work. Active
authority and active-interaction fences win over that fallback. Every actual
entity transition is a normal revisioned, idempotent campaign command with its
scheduler reason in the durable event.

The request envelope is retained only for the running service's diagnostic view;
the resulting entity fidelity is durable. Persisted health history, hysteresis,
cooldowns, reserve and destination accounting, migration, and tempo events are
still target architecture rather than implemented claims.

### Avoiding cascade failure

- Keep explicit reserve capacity for promotion, catch-up, and migration.
- Do not place new work on `Pressured`, `Overloaded`, `Draining`, or
  `Quarantined` clients.
- Limit concurrent preparations and migrations per source, destination, and
  synchronization domain.
- Require destination headroom after accounting for checkpoint application and
  temporary overlap.
- Use assignment affinity, minimum residence time, hysteresis, and cooldown to
  avoid ping-pong movement.
- Cancel or re-plan a migration if the destination becomes pressured before
  activation.
- Prefer demoting suitable low-priority islands to Operational over moving them
  repeatedly between marginal clients.

The assignment unit is the complete interaction island. A task force is the
minimum likely practical unit for many Sea Power interactions, but that remains
an experiment result rather than an architectural constant.

## Drain and migration protocol

A planned move preserves one publisher and one committed history:

1. The scheduler selects the destination, required replica roles, compatibility
   manifest, and reserve budget.
2. The destination enters `Preparing` from a committed checkpoint while the
   source continues only within its permitted lead window.
3. The coordinator names quiesce tick `T` and stops accepting inputs after `T`
   for the old assignment epoch.
4. The source commits events and a final checkpoint through `T`.
5. The destination applies the final checkpoint/delta and confirms checkpoint
   hash, event cursor, stable-ID mapping, and `Ready` state at `T`. It still has
   no authority to publish.
6. In one durable transition, the coordinator fences the old epoch and activates
   the replacement assignment/epoch after `T`.
7. The destination advances after `T`; after release commits, the source may
   natively disable assignment-owned objects. It destroys retained roots/systems
   only if the deeper quiet-removal path is separately proven safe.

Preparation does not grant an active primary lease. A synchronous Unity
materialization stall therefore cannot be hidden by suspending lease timers.
Until native Disable/Enable behavior, deeper quiet removal, and native save/load
completeness are experimentally validated, migration support must advertise the
state families and lifecycle level it can safely carry and fall back
conservatively.

If the source crashes or cannot reach `T`, the coordinator fences it and chooses
one of:

- promote a validated compatible hot Shadow from its committed cursor;
- restore the latest committed portable/native checkpoint into a cold worker;
- import the checkpoint into the Operational engine and report the bounded loss;
- suspend only the island when its state cannot yet be projected safely.

## Global tempo controller

A synchronization domain owns:

```text
FixedStepDuration
TargetTicksPerWallSecond
TempoPolicyVersion
TempoEffectiveTick
MaximumLeadAndLag
```

`FixedStepDuration` defines simulation semantics. `TargetTicksPerWallSecond`
defines the wall-time pace at which the coordinator grants and expects those
ticks. For example, reducing tempo causes every active tactical session to run
the same fixed simulation more slowly relative to wall time; it does not create
larger physics steps.

The controller reduces global tempo only when assignment migration, optional-role
removal, presentation degradation, and Operational fallback cannot preserve the
health of required authoritative islands, or when campaign-wide network/storage
pressure makes the current pace unsafe. It uses hysteresis, a minimum residence
period, recovery headroom, and a player-visible reason. Recovery increases tempo
gradually so a brief improvement does not create oscillation.

Multiplayer does not honor a unilateral local pause. A paused client becomes
`Pressured` and then `DrainOnly` or `Disabled` if it cannot keep up. Single-player
pause and slowdown still pass through the coordinator so campaign time, tactical
workers, and external engines agree. The initial architecture uses one global
campaign synchronization domain; regional clocks are deferred because
cross-region interactions would require a new causality model.

When tactical work is active, the sustainable global tempo is bounded by every
required authoritative island. The scheduler should reassign or demote a slow
island before slowing everyone, but it must slow the domain rather than permit a
required primary to accumulate unbounded lag.

## Correctness, trust, and information security

- Metric spoofing and strategic self-shedding are protocol abuse. Compare client
  claims with service-observed progress and apply rate limits and trust policy.
- A primary never receives an unbounded future grant and cannot publish from an
  expired or fenced epoch, even while overloaded or disconnected.
- Load management validates basic kinematic, weapon, detection, and resource
  plausibility before accepting tactical outcomes in multiplayer.
- Checkpoint size, extraction time, assignment count, and migration timing can
  leak hidden force information. Visibility-scoped traffic, batching, and where
  necessary padding must be designed and tested before untrusted play.
- Operational degradation is recorded and visible to affected players and
  operators. Lower fidelity must not masquerade as a precise Sea Power outcome.

## Verification and acceptance

The load test matrix includes CPU/GPU throttling, rendering collapse, command and
event bursts, checkpoint bursts, network degradation, pipe backlog, user pause,
repeated overload, destination failure during preparation, every destination
being pressured, false client metrics, and attempted migration storms.

Acceptance requires evidence that:

- fixed step, ordered input, and canonical tick hashes remain valid under load;
- no tick is skipped or stretched and all clients apply a tempo change at the
  same effective tick;
- no event is duplicated, silently discarded, or accepted from an old epoch;
- a `Disabled` client remains connected but receives no simulation-source role;
- whole-island migration preserves the committed cursor and supported state;
- destination failure leaves either the old valid source or a fenced recovery
  path, never two active publishers;
- eligibility returns only after validation, cooldown, and sustained headroom;
- migration and checkpoint behavior reveal no unauthorized state;
- reserve capacity covers the measured promotion and migration objective;
- all thresholds, cooldowns, concurrency limits, and tempo bounds are justified
  by reproducible benchmarks.
