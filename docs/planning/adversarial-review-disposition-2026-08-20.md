# Disposition of the 2026-08-20 adversarial architecture review

## Purpose

An adversarial working review attempted to falsify the design. The raw draft was
removed after disposition because it duplicated stale code paths, line numbers,
and abandoned technology conclusions. This durable record separates findings
accepted into the architecture from claims corrected by source inspection and
questions that remain experimental gates.

The review was assessed against the decompiled installed Sea Power build
`0.8.2-23444+s358`, the repository implementation, and the current architecture.
A future game build requires the relevant evidence to be regenerated.

## Accepted and incorporated

- Sea Power is nondeterministic by default. `GameTime` changes
  `Time.fixedDeltaTime` from observed FPS, simulation work spans frame/fixed
  callbacks, RNG state is process-global or unseeded in relevant paths, and
  spawn/registration order affects iteration. Hot replicas therefore require
  the controlled [deterministic runtime adapter](../architecture/deterministic-runtime-adapter.md).
- Pause and `Time.timeScale` are process-global. The design now has one tempo per
  synchronization domain and no per-assignment time scale.
- The terminal destruction path is not quiet removal. Native
  `ObjectBase.Disable/Enable` is a reversible, save-aware suspension candidate,
  but full memory-releasing dematerialization remains a separate experiment and
  exit gate.
- Sea Power state is highly coupled through task forces, sensors, plots, weapons,
  guidance, aircraft hosts, and physical relationships. The scheduler moves
  interaction islands, with actual split thresholds determined experimentally.
- A player Sea Power process containing true enemy objects leaks ground truth
  regardless of UI filtering. Trusted full-state workers, neutral workers, or a
  proven filtered proxy object are required; own-coalition assignment alone does
  not solve a playable engagement containing real enemies.
- The generic companion command passthrough was removed in the first P0
  hardening slice. Local commands now use an allow-list, authentication, bounded
  frames/queues, current-user-only pipe access, sessions, and sanitized wire
  errors; their adversarial integration tests remain a P0 gate.
- Compatibility must cover managed assembly hashes and simulation-affecting
  settings, not only the executable/game label and catalog.
- The main-thread test must compare the executing thread with a captured Unity
  main-thread identity; queueing alone is not proof.
- Durable state needs a compare-and-swap writer epoch from the first campaign
  store, before later high-availability work.
- Basic tactical-result plausibility belongs before trusted multiplayer accepts
  client outcomes, with deeper hostile-client hardening later.
- Synchronous materialization cost, event rate, tactical bubble size, and
  outcome/checkpoint volume require early benchmarks.

## Corrected or narrowed claims

### Floating origin exists

The review's claim that Sea Power never rebases world coordinates is incorrect.
`RenderPosition.checkAndPerformRelocation()` invokes the relocation system, which
moves units, projectiles, terrain, and effects. The valid constraint is narrower:
one process still appears to have one global tactical center/bubble, so supported
radius and multi-area behavior must be measured before assigning distant islands
to one client session.

### Active weapons are represented in native saves

The review's categorical claim that the native save path does not persist
projectiles is incorrect. Decompiled save/load paths enumerate task-force
weapons, and `WeaponBase` plus missile, torpedo, and bomb types contain save/load
state. That does not prove complete, portable, mid-flight restoration. An active
weapon round-trip remains a mandatory experiment; it is not a permanent handoff
fence unless the experiment fails.

### Native disable is suspension, not destruction or dematerialization

The review overlooked `ObjectBase.Disable/Enable`. Disable removes a unit from
active object/task-force collections, disables its ECS entity, deactivates its
root, and is represented in native saves without setting the destroyed flag.
However, the constructed object, systems, inactive root, and `GameMain` entry
remain allocated. Test this native path first for safe load shedding; separately
prove any deeper teardown needed to reclaim memory.

### Hot redundancy is conditional, not already falsified

Default Unity execution cannot support deterministic hot Shadows merely by
sharing a seed. The project is explicitly proposing a controlled clock,
scheduler, RNG, ordering, and checkpoint adapter. Hot redundancy remains only if
D0-D5 pass on the pinned build; otherwise the architecture uses a single primary,
cold restore, fencing, and Operational fallback.

### Delaying display is not a free commit protocol

Sea Power applies and renders many tactical effects inside its own simulation.
The review's suggested “commit before display” rule would require invasive hooks
or a different write-ahead acceptance model. Outcome capture, commitment,
rollback visibility, and what the player may observe before durability are a P4
experiment, not an assumed implementation detail.

### Preparation must not suspend authority safety

Synchronous materialization may take seconds, but a target in `Preparing` does
not yet own an active primary lease. The fix is to prepare before activation and
retain/fence the source correctly, not to suspend lease expiry.

### The two-process experiment has a prerequisite

Simultaneous replica tests are meaningful only after process-local pipe names,
ports, logs, save/profile paths, and game-instance state can be isolated. Baseline
repeat runs can proceed first; multi-instance isolation gates paired tests.

## Open experimental gates

| Question | Evidence required | Decision affected |
| --- | --- | --- |
| Can the runtime adapter make supported tactical state convergent? | D0-D5 call-site, clock, RNG, ordering, cross-FPS/hardware, checkpoint, and failover suite | Hot Shadow and promotable Observer support |
| Can an active weapon survive a native save/load or portable handoff? | Mid-flight missile, torpedo, bomb, guidance, target, and outcome round trips | Weapon fence and island migration rules |
| Can native Disable/Enable suspend an island safely, and can deeper teardown release it? | No destruction/loss event, formation/contact and hosted-child behavior defined, save/load valid, plus measured CPU/memory relief for each level | Load shedding and continuous tactical dematerialization |
| Can a filtered proxy coexist with tactical enemies safely? | Proxy renders authorized track state but has no AI, sensor, weapon, target, or hidden truth behavior | Fog-of-war multiplayer topology |
| What is one Sea Power process's usable tactical bubble? | Floating-origin relocation and distant-island experiments with sensors, physics, terrain, and effects | Session geography and assignment capacity |
| Can `GameTime.time` be controlled and periodically rebased? | Write/rebase experiment across absolute timers and long sessions | Deterministic clock compatibility |
| What tactical outcomes can be durably accepted before display? | Instrument launch, hit, damage, destruction, UI/audio, rollback, and reconnect timelines | P4 event/WAL semantics and bounded-loss claim |
| What load and coupling thresholds are safe? | Materialization latency, event rate, island churn, checkpoint cost, and stress benchmarks | Scheduler, reserve, and global tempo policy |

## Normative consequence

The architecture proceeds with fixed logical steps, coordinator-ordered global
tempo, external durable authority, epoch fencing, whole-island scheduling, and
conservative fallback. No corrected review claim is used as an excuse to skip an
experiment, and no unverified review assertion is treated as a permanent product
constraint.

## Subsequent local-first technology decisions

The review's mandatory-PostgreSQL implication is superseded, while its gRPC
recommendation is retained and narrowed to the machine protocol:

- Companion/service/worker traffic uses generated ASP.NET Core gRPC/Protobuf
  clients and services. Player-facing browser/native UI projections use SignalR
  with MessagePack; SignalR is not the canonical simulation/control protocol.
- SQLite WAL plus a content-addressed local blob directory is the supported
  single-player, private-co-op, and basic dedicated persistence stack. A clean
  local installation cannot require PostgreSQL, Redis, object storage, Docker,
  or another managed service.
- PostgreSQL, external object storage, raw WebSockets, extra streams/connections,
  and UDP/QUIC remain optional adapters gated by P7/P8 profiling or explicit
  coordinator-availability requirements.
