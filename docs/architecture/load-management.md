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

Campaign-owned [standalone simulation workers](standalone-sim-worker.md) are a
planned `Eligible` simulation source with no player session. They are not
implemented. They are not headless Sea Power. Until Track A exists, unattended
Operational islands remain in the campaign service process.

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

The connected companion now publishes its player task-force ID and exact
materialization profiles with its runtime and catalog fingerprints. Tactical
selection is rejected for the whole island when the worker is absent, the
loaded mission has no player task force, or any member lacks a supported
profile. This prevents a surface-only adapter from silently promoting mixed
surface/submarine or otherwise unsupported task forces.

Schema v6 is the first multi-container ownership foundation. The service keeps
concurrent companion sessions by stable client instance rather than replacing
the prior connection globally, persists the selected worker ID on every new
assignment, routes assignments and fences only to that owner, rejects tactical
events returned by a different worker, and broadcasts committed campaign events
to every connected worker for catch-up. A backpressured recipient is disconnected
and must replay from the durable journal; it cannot stall canonical commits or
another worker. Diagnostics expose all connected worker sessions and their live
resource envelopes. Each companion also advertises a configurable tactical
object ceiling and reserved headroom on hello and every heartbeat. Invalid
declarations fail closed to zero capacity. The current development defaults are
four objects with one reserved slot, grounded only in the live
four-independent-surface probe; they are not a mixed-combat calibration.

Worker selection is deterministic and load-aware. Scheduler evaluation now
aggregates materialized, active, retained and recovery-required counts plus
coupling graphs across every campaign worker. New complete islands select one
compatible destination with local headroom, preserve existing island affinity,
reserve the whole island against that destination before queuing, and use
normalized utilization, recovery debt, load, then stable client ID as ordered
tie-breakers. Admission is bounded by the smaller of the campaign-safe budget
and `worker capacity - worker reserve`; runtime materialization and live
assignment ownership both count as occupied, preventing stale heartbeats from
overbooking a destination. The monitor exposes declared capacity, reserve and
remaining ordinary-promotion headroom. It also reports the portion of physical
headroom protected by reserve as migration-overlap headroom. That value is an
admission envelope, not permission to create a second publisher. The coordinator
can now reserve that envelope for a non-publishing `Shadow` on a distinct
connected compatible destination: complete currently-active, quiescing, or
quiesced islands are admitted against destination `MigrationOverlapHeadroom`
only, source `Primary` ownership stays put, and the service first commits
`Shadow`/`Preparing` assignments. The companion materializes each queued Shadow
from a checkpoint, captures its realized state, suspends the native object, and
only then reports readiness so the coordinator can commit `Prepared`. Idempotent
repeats do not create another live Shadow. Authenticated
`POST /control/campaigns/{campaignId}/migrate-tactical-island` stages each
source final checkpoint onto that still-suspended Shadow, waits for matching
destination readiness, commits the existing atomic `migrate-tactical-island`
command, and only afterward queues post-commit activation grants. Matching
staged hashes live with the coordinator process only before that commit;
companions durably resend staging frames across a pre-commit restart. After
commit, grants reconstruct from the journal migration event and durable source
checkpoints, including after service restart and later head advances. Bounded
migration orchestration admission and durable campaign-scoped post-commit cooldown
are now enforced around the authenticated migrate-tactical-island path: one
in-flight orchestration per campaign and per shared source/destination worker,
explicit `migration-orchestration-busy` rejection before any staging, and
cooldown reconstructed from the latest `TacticalIslandMigratedEvent` in the
campaign journal rather than process-local clocks. Companions now declare
per-worker `health` and `eligibility` on hello and every heartbeat. Invalid values
fail closed to `Quarantined`. Empty values from older workers default to
`Healthy`/`Eligible`. Only `Healthy`+`Eligible` workers receive new Primary
assignments or Shadow overlap; `Pressured` workers keep existing work but receive
no speculative load; `Overloaded`, `Draining`, `Quarantined`, `Offline`, or
non-`Eligible` owners evacuate quiet islands. Live two-container
Sea Power verification remains open. The campaign kernel now has one atomic
command/event that can migrate a complete singleton or multi-entity island:
source assignments must be live `Primary`/`Quiesced`, destinations must be
non-publishing `Shadow`/`Prepared` with a strictly higher epoch and a different
worker, and one committed event releases the source while promoting the
destination to `Primary`/`Active` without changing Tactical fidelity. The
validator still allows only one live Primary. The coordinator now schedules
that commit only after every destination member reports a matching staged
final checkpoint; activation grants are queued after the durable event, never
before it, and never from Shadow. Bounded concurrent migration admission and
durable cooldown are now implemented around the coordinator migrate path; live
two-container verification remains open.

Assignment preparation now carries the entity's latest durable checkpoint when
one exists. The companion authenticates the download and verifies its content
hash; the destination bridge verifies checkpoint lineage and the canonical
state hash before reconstructing the supported portable surface fields. This
closes the checkpoint transport/reconstruction plumbing but does not by itself
make an island migration-safe. Automatic moves remain disabled where fuel,
stores, damage, sensors, formations, hosted aircraft, or other unsupported state
would matter, and the coordinator still needs cooldown and bounded concurrent
migration. Overlap budgets, quiesce fencing, final-checkpoint staging, and
post-commit activation grants are now in the coordinator path.

The service automatically creates and queues deterministic, batch-linked
Preparing assignments for a selected, fully representable island. Manual and
automatic handoffs both stage Strategic entities at Operational before the
reservation, so rejection or a pre-checkpoint crash has one safe fallback
fidelity. Each group member becomes Prepared independently, but a single
campaign event changes the complete batch to Active and Tactical.

Active island assignments enter Quiescing when a connected worker is
draining, ineligible, overloaded, quarantined, offline by policy, or transport
backlog-saturated. The companion persists its game-side revision independently
of campaign revision and resolves it only for scheduler-issued fences. This lets
the automatic drain reuse the exact final-checkpoint and retained-suspension
path without weakening manual expected-revision checks. Batched assignments
hold Tactical fidelity in Quiesced state until one event releases and demotes
the entire island. A disconnected active worker remains fenced Tactical until
reconnect or bounded expiry fallback; the service does not invent a checkpoint.

The latest scheduler envelope is a canonical campaign event and snapshot field,
so health, eligibility, interest, capacity, backlog, request identity, and the
stable-sample count survive service restart. An active quiet island requires two
identical healthy interest-loss samples before quiescing; load shedding and
explicit drain remain immediate. Longer health history, cooldowns, bounded
concurrent migration, and tempo events remain open. Coordinator destination
preparation can now reserve overlap headroom and queue non-publishing Shadows;
final-checkpoint staging, matching destination readiness, the atomic Shadow
promotion event, and post-commit activation grants are implemented. Bounded
concurrent migration, cooldown, and live two-container verification remain open.

The first reproducible operational baseline on the Ryzen 9 9900X development
host sustains 35.9 quanta/s for 300 entities and 180 groups, clearing the healthy
30/s target. A 600-entity/360-group shape sustains 18.5/s and is therefore an
overloaded envelope, not a supported healthy claim. These measurements cover the
external operational kernel only; tactical client and backlog thresholds still
require their own calibration. See
[Phase 5 operational-load evidence](../evidence/phase-5-load-evidence-2026-08-21.md).

The first live tactical burst on the same host schedules four independent Knox
islands into one Sea Power client. Initial materialization and checkpointing
reached four Active assignments in 4.02 seconds; a two-sample drain released all
four in 2.01 seconds; retained checkpoint re-promotion restored all four in 1.32
seconds without changing their game object IDs or creating recovery debt. This
supports a four-entity independent-surface capacity claim only. Active combat
tick throughput and mixed-profile capacity remain unmeasured. See
[Phase 5 live scheduler evidence](../evidence/phase-5-live-evidence-2026-08-21.md).

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
