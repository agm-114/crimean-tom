# Crimean Tom system architecture

## Status and scope

This is the normative target architecture for Crimean Tom. It describes a
continuous campaign whose detailed tactical work can be distributed across Sea
Power clients. It supersedes any earlier assumption that the campaign normally
stops to generate an isolated Sea Power mission.

The current code implements only the first local, read-only control-plane
slice. Statements marked **decision** are architectural commitments. Statements
marked **validation gate** require measurement against the installed game
before their constants or field mappings become contractual.

The findings from the 2026-08-20 adversarial review are tracked in its
[disposition record](../planning/adversarial-review-disposition-2026-08-20.md); the review
itself is evidence and critique, not a normative replacement for this document.

## Product goals

Crimean Tom must:

- advance a large air, sea, and land theater continuously;
- preserve stable unit identity, state, orders, logistics, and intelligence;
- simulate most entities outside Unity at strategic or operational fidelity;
- materialize selected entities into running Sea Power clients without starting
  a new campaign timeline;
- let players observe and command a shared real-time world;
- distribute high-fidelity work among several clients;
- allow redundant simulation of important or observed units;
- recover quickly when a primary client becomes unhealthy;
- continue unrelated theater regions when a client or interaction island fails;
- preserve correctness under load by draining, reassigning, degrading, or
  globally slowing work without changing the fixed logical step;
- produce replayable, attributable, versioned campaign history;
- keep the in-game DLL small enough to diagnose after Sea Power updates.

## Explicit non-goals for the first playable release

- Bit-identical Unity lockstep across machines.
- A fully peer-to-peer consensus system with no ordering service.
- Trusting arbitrary clients to decide campaign truth without validation.
- Replacing Sea Power's renderer, physics, sensors, weapons, or damage model.
- Keeping every campaign entity instantiated in Unity.
- Seamless compatibility across unknown game or mod versions.
- Reproducing every private Unity object in the portable save schema.
- Competitive anti-cheat strong enough for untrusted public matchmaking.

These can be revisited after the continuous single-player and trusted-co-op
vertical slices are proven.

## Architectural decisions

1. **One continuous campaign timeline.** Tactical fidelity is a state of an
   entity, not a separate mission result that replaces campaign time.
2. **External durable authority.** The campaign service owns committed state,
   event order, leases, authority epochs, and persistence.
3. **Distributed tactical execution.** The service coordinates Sea Power
   clients but does not need to perform their detailed simulation.
4. **One publisher, many simulators.** Several clients may simulate an entity,
   but only the primary for the current epoch can propose authoritative events.
5. **Interaction islands are the authority unit.** Tightly coupled units,
   sensors, targets, and in-flight weapons move together when necessary.
6. **The plugin is an adapter.** Unity access and game-version-specific mapping
   stay in the plugin; remote networking and campaign policy do not.
7. **The companion is the client-side control plane.** It owns remote transport,
   buffering, authentication, diagnostics, and the local named-pipe session.
8. **External simulation is deterministic; tactical replicas use a controlled
   runtime.** The campaign engine supports exact replay. Sea Power is
   nondeterministic by default, so hot replicas require the validated Crimean Tom
   clock/scheduling/RNG/order adapter plus checkpoints, hashes, and correction.
9. **Events are idempotent and fenced.** Stable IDs, expected revisions, and
   authority epochs prevent duplicate or stale outcomes from committing.
10. **Information scope is separate from simulation state.** A player-visible
    replica must not receive hidden information merely because it simulates a
    nearby unit.
11. **Load changes placement and pace, not timestep semantics.** Under pressure,
    the coordinator removes optional work, drains or moves whole interaction
    islands, uses Operational fallback, and only then reduces the synchronization
    domain's global target tempo. It never stretches or skips logical ticks.
12. **The supported baseline is local-first.** One person can run the campaign
    service, operational simulation, persistence, companion, and game on one
    machine without Docker, PostgreSQL, Redis, or another managed service.

## Logical topology

```text
                       operators / campaign UI
                                |
                    HTTPS API / SignalR UI
                                |
              +-----------------v------------------+
              | Campaign service / coordinator     |
              | clock, ordering, rules, scheduler, |
              | leases, epochs, validation         |
              +--------+------------------+---------+
                       |                  |
                 event journal       snapshot/catalog store
                       |                  |
          +------------+------------------+------------+
          |                      gRPC/Protobuf          |
          |                                              |
  operational workers                         client companions
  deterministic .NET                         /       |        \
                                            / local  | local    \
                                      named pipe     |      named pipe
                                          |          |          |
                                    Sea Power A  Sea Power B  Sea Power C
                                    thin plugin  thin plugin  thin plugin
```

The coordinator is logically authoritative, but it may run in the same local
Crimean Tom process as the engine and SQLite store. Its authority and storage
interfaces remain separable so a later hosted deployment can scale them without
making external infrastructure a prerequisite. This removes a detailed tactical
host bottleneck while retaining one place to fence split-brain authorities and
order events.

## Deployable components

| Component | Target | Responsibilities | Must not own |
| --- | --- | --- | --- |
| `SeaPower.RuntimeBridge` | .NET Standard 2.1, inside Sea Power | Campaign-agnostic Unity main-thread dispatch, stable-ID mapping, extraction, controlled mutation, deterministic runtime hooks, local protocol | Crimean Tom campaign policy, remote credentials, durable storage |
| `CrimeanTom.Contracts` | .NET Standard 2.1 | Dependency-free local envelope and payload types | Game references or campaign policy |
| `CrimeanTom.Companion` | .NET 8 process beside each game | Named-pipe broker, remote client, buffering, auth, health, logs, local operator API | Canonical campaign truth |
| Campaign network contracts | Shared modern .NET library | Versioned Protobuf/gRPC contracts, generated clients/services, event/command/checkpoint identifiers | Unity/game references |
| Campaign domain/engine | Pure .NET library | State, reducers, clock, deterministic systems, validation | Unity/game assemblies |
| Campaign service | ASP.NET Core service | Commands, event ordering, persistence, leases, scheduler, player sessions | Unity objects |
| Operational worker | .NET worker | Deterministic unattended simulation for assigned shards/islands | Authority allocation |
| Catalog builder | Local/versioned tool | Normalize installed game data and compute fingerprints | Live campaign mutation |
| Campaign client UI | Separate app or web UI | Theater map, orders, health, diagnostics, administration | Direct game mutation |

The Operational worker is specified as a **campaign-owned standalone process**
in [Standalone simulation workers](standalone-sim-worker.md). Track A is a .NET
8 Operational worker the service can spawn; it is not a headless Sea Power
instance. Unattended Operational islands may be assigned to Track A workers for
capacity and fencing. The journal writer still resolves Operational ticks
in-process.

## Technology and packaging baseline

The in-game bridge and the external application deliberately have different
runtime stacks:

- `SeaPower.RuntimeBridge` continues to target .NET Standard 2.1 and Sea Power's
  Unity/Mono runtime. The build may use a pinned newer Roslyn language version
  (C# 13 is the next compatibility target) plus source-defined compiler metadata
  shims such as `IsExternalInit` or required-member attributes. This permits
  newer syntax only when it lowers to IL and APIs supported by the installed
  game runtime; it does not pretend Sea Power runs a newer .NET runtime.
- Crimean Tom's companion, campaign service, engine, and workers use modern C#
  on .NET 8. The local distribution packages the required runtime and native
  SQLite dependency so a player does not need a development SDK or container
  runtime.
- Companion, service, and worker communication uses ASP.NET Core gRPC with
  Protobuf-generated clients and services. This is the fully typed internal
  simulation/control protocol. SignalR with MessagePack is reserved for
  player-facing realtime UI notifications and projections, where browser
  support, groups, and connection lifecycle matter more than bidirectional
  streaming and generated end-to-end RPC types.
- SQLite in WAL mode is the required persistence implementation. Content-addressed
  checkpoint/catalog blobs use an adjacent local directory by default. Both are
  included in campaign export, backup, validation, and restore workflows.

Raw WebSockets, custom UDP/QUIC, PostgreSQL, Redis, and external object storage
are optional adapters only. They enter the supported stack after profiling or
availability requirements demonstrate a need; none is required for single-player,
LAN/private co-op, or a basic dedicated campaign.

## Supported deployment modes

The same contracts support progressively larger deployments:

1. **Developer/local:** one Crimean Tom application hosts the service, engine,
   SQLite store, local blob directory, companion, and one Sea Power process on
   one machine.
2. **Single-player:** the same local package is the default; the player's client is the
   usual tactical primary, with operational fallback when Sea Power is absent.
3. **Trusted co-op:** a player-hosted or dedicated Crimean Tom application with
   SQLite serves several companions, distributed primaries, and optional shadows.
4. **Scaled dedicated campaign:** optional external storage and stateless service
   instances serve many players and operational workers after the P8 evidence gate.

The first release does not require coordinator leader election. Durable restart
and event replay are sufficient before horizontal coordinator availability.

## Canonical identity

Every durable object receives a typed stable ID independent of Unity instance
IDs and collection indices. Required namespaces include campaign, entity,
platform, group, installation, hosted-aircraft instance, assignment,
interaction island, checkpoint, command, event, player, and replica.

An entity record carries:

- stable ID and entity kind;
- catalog type/variant/loadout IDs;
- coalition and information-visibility policy;
- canonical revision and lifecycle state;
- location/kinematics at a campaign time;
- damage, systems, stores, fuel, crew, and readiness summaries;
- parent/group/base relationships;
- current orders and AI intent;
- current fidelity and interaction-island ID;
- last checkpoint and committed event cursor;
- active assignment and authority epoch, when tactical.

The plugin maintains a session map from stable IDs to live game objects. It
never persists raw Unity references. Mapping must be rebuilt and validated
after scene changes, loads, or rematerialization.

## State ownership

| State | Canonical owner | Replica behavior |
| --- | --- | --- |
| Campaign clock and committed order | Campaign service | Read-only cursor |
| Strategic logistics/readiness | Campaign engine | Tactical worker receives relevant projection |
| Player command intent | Service after validation | Clients predict only after submission |
| Tactical transient physics | Assigned Sea Power replica | Not durable until checkpoint/event projection |
| Tactical outcome events | Current primary proposes; service commits | Shadows compare and report |
| Stable unit lifecycle/revision | Campaign service | Clients apply committed events |
| Fog-of-war/intelligence | Service per side/player | Filtered projection only |
| Assignment/lease/epoch | Campaign service | Client must obey expiry/fencing |
| UI/camera/selection | Local client | Never campaign authoritative |
| Catalog fingerprint/schema versions | Service campaign metadata | Exact compatibility check |

No code path may silently update canonical campaign state from an unfenced
client message or infer authority from connection ownership.

## Time model

Campaign time is an integer logical value, not wall-clock time or a Unity frame
count. All commands, assignments, checkpoints, and events name an effective
campaign time and ordering cursor.

The engine is event-driven over a configurable time quantum. **Validation gate:**
Phase 1 instrumentation will select the initial quantum and checkpoint cadence
after measuring Sea Power update behavior and network/state sizes; they are not
hard-coded architectural constants.

For each synchronization domain the coordinator provides a time mapping, and
each assignment receives bounded grants within it:

```text
campaign epoch time
session simulation origin
assignment effective tick
fixed step duration
target ticks per wall second
tempo policy version and effective tick
maximum lead and lag
checkpoint boundaries
```

In validated redundant mode, that mapping is enforced by the
[Deterministic tactical runtime adapter](deterministic-runtime-adapter.md).
Coordinator-issued step ranges replace measured FPS and wall-clock frame time as
simulation inputs. A client consumes steps locally and reports completed ticks;
the network does not carry one message per physics step.

Rules:

- A primary may not commit beyond its permitted lead window.
- A lagging primary loses promotion preference and eventually its lease.
- Single-player global pause pauses campaign time through the service.
- Multiplayer has no unilateral global pause. A locally paused or stalled Sea
  Power client becomes pressured and may be drained or disabled as a simulation
  source while remaining connected for commands and presentation.
- `FixedStepDuration` does not change under load. Global
  `TargetTicksPerWallSecond` changes are coordinator policy and ordered events,
  not local client facts.
- Every tactical assignment in the same synchronization domain advances at the
  same global tempo; per-assignment time scale is forbidden.
- Cross-island events use source time plus a service-assigned commit position.

The complete overload, migration, client-eligibility, and global-tempo policy is
specified in [Load management and simulation-source control](load-management.md).

## Fidelity model

An entity or island is in one canonical fidelity state:

- **Strategic:** route, availability, logistics, and coarse detection.
- **Operational:** deterministic sensor/combat/adjudication model outside Unity.
- **Tactical:** at least one full Sea Power replica is assigned.
- **Suspended:** bounded recovery state for an island that cannot safely advance;
  expiry must select restore, Operational adjudication, or an explicit
  force-resolution rule rather than leave it stopped indefinitely.

`Render proxy` is a replica role, not canonical fidelity: a tactical or
operational entity can be projected into a client without giving that client
authority or complete state.

Fidelity changes happen only at an effective tick with an input checkpoint and
an acknowledged output checkpoint. No entity is simultaneously canonical in
two fidelity engines.

## Interaction islands

The scheduler builds a coupling graph. Nodes are entities; edges represent a
relationship that makes independent authority unsafe or expensive, including:

- task-force/formation control;
- active sensor tracking or shared data-link state;
- weapon-target and guidance relationships;
- aircraft host/recovery relationships;
- collision or close-formation physics;
- refueling, towing, boarding, or other physical dependency;
- explicit scenario/AI coordination requiring same-tick state.

Connected components above a policy threshold form interaction islands. The
scheduler applies hysteresis so islands do not repeatedly merge and split at a
distance boundary.

The implemented active-weapon slice publishes launch-platform/target/guidance
relationships in companion heartbeats. Hard relationships union their
canonical endpoints into one scheduler component and mark it unsafe to split.
The cache tolerates one missing sample to avoid a transient heartbeat gap
tearing down a fence. Untargeted weapons use a platform self-edge so a lone
campaign unit cannot demote while it still owns in-flight ordnance.

Merge protocol:

1. Fence new cross-island authoritative actions at merge tick `T`.
2. Commit both islands through `T`.
3. Select a destination primary and compatible shadows.
4. Build one merged checkpoint and assignment with a new epoch.
5. Materialize/acknowledge all members and resume after `T`.

Split uses the inverse operation and is forbidden while an in-flight weapon,
guidance link, physical dependency, or unresolved event crosses the boundary.

## Materialization lifecycle

### Strategic/operational to tactical

1. Scheduler selects an entity/island and target replica set.
2. Service advances and commits the source engine through handoff tick `T`.
3. Service creates a portable checkpoint, catalog/version requirements, role
   assignments, information scopes, and a new authority epoch.
4. Companions validate capacity and compatibility before accepting.
5. Plugins materialize Crimean-Tom-owned objects on Unity's main thread while the
   assignment remains `Preparing`; preparation does not grant an active lease.
6. Each full replica reports stable-ID mapping, extracted canonical hash,
   warnings, and readiness at `T`.
7. Service selects/activates the primary lease. Other replicas remain shadows
   or observers.
8. Tactical advancement begins after the activation commit.

Failure before step 7 leaves the source engine authoritative. Partial Unity
objects are cleaned up by assignment ID.

### Tactical to strategic/operational

1. Service announces quiesce tick `T` and stops accepting later commands.
2. Primary advances to `T`, resolves due interactions, and emits a final
   checkpoint plus events.
3. Service validates and commits through `T`, then fences the tactical epoch.
4. Target engine imports the portable state and reports its canonical hash.
5. Service switches fidelity and resumes time after `T`.
6. Plugins natively disable only objects belonging to the fenced assignment
   after the suspension gate passes. They release retained GameObjects/systems
   only after the separate full-removal gate. Until then, the product claims
   suspension rather than memory-releasing continuous dematerialization.

If the primary disappears, a full shadow completes the protocol. Otherwise the
service restores the last committed checkpoint and deterministically advances
or suspends only the affected island.

## Commands and events

Commands express intent; events express accepted facts.

Every command includes:

- command ID/idempotency key;
- campaign, player, and subject IDs;
- expected subject revision;
- requested/effective time;
- command type and versioned payload;
- authentication and information-scope context.

The service validates permission, lifecycle, revision, timing, rules of
engagement, and resource preconditions. It assigns an ordered position and
routes the command to the responsible engine/primary.

Every event includes:

- stable event ID and schema version;
- campaign commit position;
- entity/island ID and new revision;
- assignment ID and authority epoch when client-originated;
- source and effective times;
- causal command/event IDs;
- typed payload and compatibility metadata;
- optional canonical/subsystem hashes.

Reducers are deterministic and idempotent. Unknown required event versions stop
that consumer; they are never silently ignored.

### Delivery and consistency semantics

- Network delivery is at-least-once; exactly-once transport is not assumed.
- Stable message/event IDs plus transactional deduplication provide exactly-once
  effects at the canonical reducer.
- A success acknowledgement for a canonical command/event is sent only after
  durable commit.
- The global campaign commit position gives total durable order. Per-entity
  revisions provide conflict detection and efficient projections.
- A replica applies committed events in cursor order. Gaps trigger catch-up, not
  speculative skipping.
- Read models and player projections may lag the durable head but always expose
  their cursor so clients know the freshness of the view.
- During disconnection, a client can queue bounded player intent and presentation
  prediction. It cannot claim canonical commitment.
- Queue overflow follows explicit priority policy: lease/fence and command
  acknowledgement traffic outrank telemetry, projections, and bulk checkpoints.

## Local and remote protocols

### Plugin to companion

The implemented diagnostic protocol uses strict UTF-8 JSON in bounded,
length-prefixed frames over a current-user-only Windows named pipe. It retains a
versioned envelope, unique and correlation IDs, a negotiated session ID, message
type, deadline, priority metadata, and JSON payload. It remains local-only and
easy to inspect while game integration is unstable.

Implemented baseline:

- explicit handshake acceptance/rejection and a newly issued session ID;
- current-user-only pipe access, a 256 KiB frame cap, bounded game queues, and a
  bounded companion pending-command set;
- deadlines, sanitized structured errors, and a per-run current-user token on
  the allow-listed loopback API;
- no generic command-type passthrough; and
- negotiated transmitted cancellation plus bounded weighted Bulk, Normal, and
  Control command/response scheduling; and
- an additive runtime-capability fingerprint with background-hashed executable,
  plugin, and required managed simulation assemblies, live simulation settings,
  timing diagnostics, hook manifest, and explicit authority blockers. Dynamic
  process/scene/timing diagnostics are reported but are not compatibility-hash
  inputs.

Required evolution:

- catalog/mod fingerprint production, verified deterministic-hook method
  fingerprints, and actual simulation-option negotiation (the baseline reports
  these inputs and fails authority closed while any are unavailable);
- reconnect cursors and explicit reconnect states;
- chunked checkpoints with hashes and atomic completion;
- adversarial tests for ACL, token, framing, queue, deadline, and session fences.

### Companion to campaign service

The rationale, typedness comparison, stream shape, and local packaging contract
are detailed in [Remote transport and local-first backend stack](transport-stack.md).

**Decision:** use authenticated bidirectional ASP.NET Core gRPC streaming with
Protobuf for companion/service/worker traffic. `.proto` schemas generate both
client and service C# types, give every evolving field an explicit stable number,
and keep method names and payload shapes compile-time checked in both directions.
A separate HTTPS JSON API serves authentication, administration, health,
low-rate campaign queries, and content-addressed bulk downloads. SignalR with
MessagePack serves player-facing browser/native UI projections and notifications;
it is not the canonical worker/control protocol.

Separate gRPC methods/streams and bounded application queues cover:

- session/control, lease, and fencing messages;
- ordered commands and committed events;
- replica health/hash reports;
- authorized companion-facing projections and proxy state.

Large checkpoints and catalogs transfer over authenticated HTTP using content
hashes, resumable ranges where needed, and atomic verification. They do not
share a simulation stream. Start with distinct control/event/telemetry streams
only where their ordering differs; add further streams or a lower-level transport
only if stress tests show that lease/fence deadlines cannot be met.

The companion writes accepted outbound messages to a bounded durable spool
before acknowledging local receipt when loss would affect authority. Bulk data
must not block a lease revoke or authority fence.

Neither gRPC stream restart nor SignalR automatic/stateful reconnect is a
durability or exactly-once guarantee. Campaign commit positions, event IDs,
idempotency keys, assignment epochs, and reconnect cursors remain authoritative.

## Determinism model

The pure engine guarantees deterministic replay for an exact simulation/schema
version, catalog fingerprint, initial snapshot, and ordered input log.

Requirements:

- integer/fixed-point domain units or explicit deterministic quantization;
- stable collection ordering at reducer boundaries;
- keyed RNG derived from campaign seed, subsystem, entity, tick, and event key;
- no wall-clock, thread scheduling, process ID, or unordered hash-map iteration
  in simulation decisions;
- deterministic parallel work products followed by stable merge ordering;
- recorded external inputs and versioned algorithms;
- canonical serialization and layered state hashes.

Sea Power is not deterministic in its default mode. Hot Unity workers are
eligible only after the [Deterministic tactical runtime adapter](deterministic-runtime-adapter.md)
controls logical time, timestep, ordered inputs, known RNG sources, and stable
object/update ordering on the pinned game build. Even then, workers provide
bounded convergence rather than a platform-independent bitwise guarantee. Only
portable canonical fields are compared; transient rendering, audio, particle,
and explicitly classified presentation state is excluded. Divergence policy is
defined in [Federated simulation protocol](federated-simulation.md).

## Replica roles and authority

The complete primary/shadow/observer/proxy model, health report, promotion
sequence, and divergence handling are specified in
[Federated simulation protocol](federated-simulation.md).

Additional constraints:

- A full observer and a render-facing projection may be separate local views.
- Promotion eligibility is an explicit assignment capability, never inferred.
- The service stores the current epoch transactionally with committed events.
- Lease expiry uses service time; client clocks are diagnostic only.
- A returning old primary must re-handshake and rebase before receiving any role.
- Two disagreeing replicas detect divergence but cannot establish truth by vote.
- Redundancy policy is risk-based: player focus, active weapons, unique units,
  and weak network/health increase desired replica count.

## Persistence and recovery

The durable model contains:

- campaign metadata, versions, seed, configuration, and catalog fingerprint;
- append-only commands/events with a global campaign commit position;
- periodic campaign snapshots and per-island tactical checkpoints;
- entity revisions and materialization history;
- assignment, lease, epoch, and promotion audit history;
- player/coalition information state;
- checkpoint blobs with content hashes and reference counts.

**Decision:** use SQLite in WAL mode as the supported default for local,
single-player, private multiplayer, and a basic dedicated service, behind a
storage abstraction. It keeps one transactional campaign writer and permits
concurrent readers; it is not used as a multi-writer distributed database.
Checkpoint/catalog blobs use a content-addressed local directory. PostgreSQL and
external object storage are optional P8 adapters only when measured scale or
coordinator high-availability requirements justify their operational cost.

From the first durable campaign write, the store owns a compare-and-swap writer
epoch. A coordinator must acquire and renew that epoch transactionally; a stale
writer cannot append events or allocate authority even before multi-node leader
election is introduced.

Recovery order:

1. Load the latest valid snapshot.
2. Verify schema, simulation, and catalog compatibility.
3. Replay committed events to the durable head.
4. Expire all pre-crash tactical leases and increment affected epochs.
5. Restore operational ownership until clients reconnect and rebase.
6. Resume campaign time only after invariants and hashes pass.

No client-only event is considered committed after coordinator recovery.
The event journal therefore has zero recovery-point loss for acknowledged
canonical events. Unacknowledged Unity work since the last accepted event or
portable checkpoint may be discarded or replayed; every release must publish
the measured worst-case window. Recovery-time objectives are set only after the
fault-injection measurements in Phases 6 and 8.

## Multiplayer command path

1. Client UI or game captures player intent with a local prediction ID.
2. Companion authenticates the session and sends the command.
3. Service checks player authority and information scope, assigns effective time
   and commit order, and records acceptance/rejection.
4. Responsible engine/primary applies the ordered command.
5. Resulting committed events return to interested replicas.
6. Clients reconcile prediction and interpolate presentation-safe corrections.

Late join receives a visibility-filtered snapshot plus events after its cursor.
Reconnect uses the same path. A player disconnect does not transfer secret state
or automatically destroy their units; campaign policy issues AI/default orders
and the scheduler reassigns simulation responsibility.

## Fog of war and security

The authoritative service maintains true state and per-coalition/player
knowledge projections. It never sends true enemy state to a client solely to
make rendering or local prediction easier.

Security boundaries:

- named pipe restricted to the local user/session where Windows permits;
- companion remote identity established by TLS plus campaign/session token;
- least-privilege role claims for player, operator, and worker actions;
- message size/rate limits and bounded queues;
- strict allow-list of plugin commands and payload validation;
- ownership checks before update/delete of Unity objects;
- secrets stored outside save files, logs, and plugin payload diagnostics;
- auditable administrative changes and authority promotions;
- sanitized telemetry that does not leak opposing-force information.

Trusted co-op can accept client tactical outcomes initially. Public/untrusted
play requires stronger plausibility checks, independent shadows, server-side
secret-state adjudication, and possibly authoritative operational recomputation.
Even trusted multiplayer applies basic kinematic, detection, weapon, and resource
plausibility before accepting outcomes. A player's Sea Power process cannot be
given true enemy objects and made safe merely by filtering its UI; neutral
workers or a separately proven proxy-object path are required when hidden truth
would otherwise share that process.

## Failure behavior

| Failure | Required behavior |
| --- | --- |
| Plugin fails to load | Companion reports game unavailable; service assigns no tactical role |
| Named pipe drops | Plugin keeps game stable; companion buffers safely; lease health degrades |
| Companion exits | Plugin reconnects; service expires/fences its roles if grace elapses |
| Sea Power freezes/crashes | Promote compatible shadow or return island to operational recovery |
| Client remains responsive but overloaded | Mark DrainOnly/Disabled, move or demote whole islands, then adjust global tempo if required |
| Primary network partition | Stop accepting after lease expiry; fence old epoch before promotion |
| Shadow diverges | Remove promotion eligibility, diagnose subsystem hash, resync |
| Coordinator process exits | Recover durable head, expire leases, replay, resume safely |
| Durable store unavailable | Stop commitment and authority changes; do not invent local truth |
| Catalog/game mismatch | Reject assignment before materialization |
| Partial spawn/update | Roll back only assignment-owned objects; source engine stays authoritative |
| Checkpoint corrupt/missing | Fall back to prior verified checkpoint plus replay |
| Every tactical client lost | Continue suitable entities operationally; suspend only unsafe islands |
| Old primary returns | Reject old-epoch events; require rebase and a new assignment |
| Malformed/oversized message | Reject, record metric, throttle or disconnect sender |

## Observability

Every service, companion, and plugin log line related to distributed work should
carry campaign ID, session/client ID, assignment ID, island/entity ID, authority
epoch, event cursor, and correlation ID where applicable.

Required metrics include:

- campaign time versus wall time and backlog;
- commands/events accepted, rejected, duplicated, and late;
- active primaries/shadows/observers/proxies by client;
- heartbeat age, lease headroom, simulated tick lag, and promotion readiness;
- checkpoint size, creation/apply time, transfer rate, and failures;
- canonical and subsystem hash mismatches;
- materialization/dematerialization duration and rollback count;
- per-client frame/tick rate, pipe queue depth, remote queue depth, CPU, memory;
- interaction-island size, merge/split frequency, and cross-island deferrals;
- failover detection, promotion, and recovery time.

An operator must be able to answer who was authoritative, from which checkpoint,
under which epoch, and why a promotion occurred without inspecting raw game
memory.

## Performance and capacity policy

Capacity is controlled by budgets rather than unbounded entity counts:

- client-declared and measured tactical object/interaction budgets;
- maximum assignment, checkpoint, event, and queue sizes;
- bandwidth budgets per projection and fidelity;
- scheduler reserve capacity for promotion;
- configurable redundancy based on risk/importance;
- operational fallback when tactical capacity is exhausted.

The scheduler classifies clients independently by health and simulation-source
eligibility. A player may remain connected as UI/Proxy while `Disabled` from
Primary, Shadow, and full Observer work. The load-shedding order, migration
protocol, anti-oscillation controls, and fixed-step/global-tempo separation are
normative in [Load management and simulation-source control](load-management.md).

**Validation gate:** benchmark representative surface, air, submarine, sensor,
and missile-heavy islands before setting defaults. The plan does not claim a
player count or ship count until these benchmarks exist.

## Versioning and compatibility

Campaign metadata pins:

- campaign schema version;
- event/command contract versions;
- external simulation version;
- plugin and companion protocol/capabilities;
- Sea Power game build;
- managed simulation-assembly hashes and deterministic hook manifest;
- normalized catalog fingerprint and relevant mod fingerprint;
- simulation-affecting game options, including asset-bundle behavior, supported
  save-state flags, difficulty, and gameplay settings;
- Protobuf/gRPC service version and player-facing SignalR/MessagePack schema;
- storage schema and checkpoint-blob format versions.

Additive optional fields are forward-compatible. Required semantic changes use
new message/event versions and explicit migrations. A campaign may allow mixed
plugin versions only when capability negotiation proves the assigned feature
set is compatible. Incompatible workers can remain connected as UI-only clients
but cannot receive simulation authority.

## Configuration ownership

Simulation-affecting configuration is versioned campaign state and changes only
through ordered administrative commands. Machine-local paths, ports, log levels,
and capacity hints remain local configuration. Secrets come from protected local
or deployment configuration and never enter campaign snapshots.

## Architecture acceptance criteria

The architecture is implemented—not merely documented—when all of these hold:

- a campaign advances without Sea Power running;
- an entity can materialize into Sea Power and return without identity,
  revision, stores, damage, or time loss;
- player commands enter one ordered event history;
- two clients can simulate the same unit in defined roles under a runtime adapter
  that has passed the pinned-build determinism and failover suite;
- killing the primary promotes a warm eligible replica while old-epoch events
  remain rejected;
- an observer sees only its authorized information projection;
- a clean machine can host a complete single-player or trusted-co-op campaign
  with the packaged Crimean Tom application, SQLite, and local files only;
- disconnected tactical capacity degrades to operational fidelity without
  stopping unrelated theater regions;
- an overloaded client can be drained or disabled as a simulation source without
  ejecting its player, and whole-island migration preserves one valid publisher;
- stress never changes the fixed logical step; any global tempo change is an
  ordered event observed at the same effective tick by the whole domain;
- replay from snapshot and journal reproduces the external canonical hash;
- update/catalog mismatches fail closed before campaign corruption;
- logs and metrics reconstruct every authority and handoff decision.

The phased work and proof required for each criterion are in
[Implementation plan](../planning/implementation-plan.md).
