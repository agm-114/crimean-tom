# Crimean Tom implementation plan

## Purpose

This plan converts [System architecture](../architecture/system-architecture.md) into ordered,
reviewable work. It intentionally stops at gates. A later phase must not build
on an unproven game mapping, state contract, or failure assumption.

The [adversarial review disposition](adversarial-review-disposition-2026-08-20.md)
records which findings changed this plan, which source claims were corrected,
and which disagreements remain mandatory experiments.

The [current implementation handoff](current-handoff.md) records the latest
verified repository state and the exact next implementation slice.

Phases 0 through 4 are complete, and Phase 5 is the next implementation phase.
The local
control plane, authenticated bounded transport, Unity-main-thread dispatch,
catalog/provenance reads, canonical world snapshots, typed scenario/campaign
navigation, and disposable port-maintenance draft flow have live evidence on
game build `0.8.2-23444+s358`. Runtime authority and external campaign commit
remain disabled by default. The first opt-in, ownership-scoped surface-unit
materializer now has live dry-run, create, read-back, and idempotent-replay
evidence. One broad integration scenario crosses the real companion,
HTTP authentication, named pipe, mock game peer, catalogs, UI actions, and port
draft validation, and the mutation admission path.

## Delivery rules

- Keep every phase vertically testable and revertible.
- Add one mutation family at a time behind negotiated capabilities.
- Preserve a working read-only inspection path throughout development.
- Never call a feature complete from compilation or a load marker alone.
- Store fixtures and generated artifacts without proprietary game binaries.
- Pin game build and catalog fingerprints in all integration evidence.
- Treat stable identity, idempotency, and authority fencing as prerequisites,
  not later cleanup.
- Prefer controlled experiments against disposable Crimean Tom entities and
  saves. Never mutate unrelated local mods or user campaigns during a test.
- Benchmark before fixing scale, timing, checkpoint, or timeout constants.
- Keep the default application runnable on one machine with packaged .NET,
  SQLite, and local files. External databases, brokers, object stores, and
  containers are optional scale adapters, never baseline requirements.
- After each phase, conduct the adversarial review at the end of this document.
- Prefer a few broad vertical integration scenarios over collections of narrow
  unit tests. Working campaign behavior is the primary goal; tests exist to
  protect complete flows and high-risk boundaries.

## Workstreams

The phases combine seven continuous workstreams:

| Workstream | Outcome |
| --- | --- |
| Game adapter | Known, versioned mappings between portable state and Sea Power |
| Campaign engine | Deterministic domain state, reducers, systems, and replay |
| Control/network | Safe local and remote sessions with flow control and fencing |
| Persistence | Transactional journal, snapshots, checkpoints, and recovery |
| Federation | Assignment, replication, divergence, scheduling, and failover |
| Player experience | Orders, projections, late join, feedback, and graceful degradation |
| Verification/operations | Tests, benchmarks, diagnostics, migrations, and release evidence |

## Phase 0 - freeze and harden the control baseline

### Objective

Retain a boring, reliable diagnostic channel before adding game mutation.

### Existing deliverables

- `SeaPower.RuntimeBridge`, the generically named thin in-game adapter; Crimean
  Tom naming begins at the contracts, companion, campaign, and networking layers.
- `CrimeanTom.Contracts` local envelope/payload project.
- `CrimeanTom.Companion` named-pipe broker and localhost control API.
- Thin plugin reconnect loop and Unity-main-thread command queue.
- Read-only ping, capability, and scene-state commands.
- Build/deploy/Steam launch automation and DLL hash verification.
- Live plugin and pipe smoke commands.

### Implemented hardening slice

- The generic command-type HTTP passthrough is removed. Only explicit read-only
  ping, capabilities, and scene endpoints are exposed, all behind a per-run
  current-user control token.
- The named pipe uses current-user-only access, an explicit accepted/rejected
  handshake, and a newly issued session ID that fences commands and responses.
- Newline JSON is replaced by strict UTF-8, length-prefixed frames capped at
  256 KiB. Game inbound/outbound queues and companion pending commands are
  bounded; queue saturation fails closed.
- Commands carry deadlines and priority metadata. Expired commands fail before
  touching game state. Caller cancellation and timeouts transmit a correlated,
  session-fenced cancel frame that removes work still queued for Unity.
- Both the Unity command queue and game response queue use a bounded weighted
  schedule across negotiated Bulk (`-100`), Normal (`0`), and Control (`100`)
  lanes. Control bypasses bulk load while the schedule still guarantees normal
  and bulk opportunities under sustained control traffic.
- Internal exceptions are logged locally under an error ID. Only structured,
  sanitized errors cross the pipe.
- Ping and scene responses report both the initialized Unity main-thread ID and
  the actual executing thread ID, plus an explicit equality result.
- Capabilities include a canonical SHA-256 runtime fingerprint over the game
  executable, plugin, required managed simulation assemblies, and currently
  loaded simulation-affecting options. File hashing runs off Unity's main
  thread and reports `pending`, `ready`, or sanitized `failed` state.
- Process, scene, timing, catalog/mod slots, and the deterministic-hook manifest
  are explicit capability fields. Missing catalog/mod fingerprints,
  unverified hooks, unavailable options, pending file identity, and disabled
  mutation each make authority eligibility fail closed with named blockers.
- Protocol tests cover every v1 envelope/payload plus normal, oversized,
  truncated, invalid-UTF-8, and malformed-JSON inputs.
- Companion health exposes an explicit connection state, monotonically
  increasing session generation, and transition timestamp. When a session
  ends, the plugin purges commands and responses still queued for that session
  so reconnect cannot execute stale authorized work.
- Real named-pipe adversarial tests cover rejected handshakes, oversized
  handshake frames, wrong-session responses, three consecutive disconnect and
  reconnect generations, pending-command saturation, caller cancellation,
  absolute deadline cancellation, pending-slot release, and graceful companion
  shutdown. Direct middleware tests reject missing and invalid control tokens
  without invoking an endpoint. A real Kestrel host repeats missing, invalid,
  and valid token probes on a temporary loopback port.
- Token creation applies its current-user-only ACL atomically and rejects an
  existing token not owned by the current Windows user. Companion lifecycle
  integration covers a recycled unrelated PID, stale token/log cleanup, safe
  startup, and clean stop.
- A one-shot runtime self-test invokes the actual dispatcher from a managed
  worker thread and requires `wrong-execution-thread`; pending or sanitized
  failed results remain authority blockers and fail the live control smoke.
- A live game negotiated the hardened session, completed ping/capability/scene
  commands on the captured Unity main thread, and reconnected with a new session
  ID through three consecutive companion-only restart/control cycles. A fresh
  deployed launch on game build
  `0.8.2-23444+s358` also returned ready executable/plugin/eight-assembly file
  identity, a stable 64-character capability fingerprint, live settings/timing,
  and the expected fail-closed authority blockers. The same companion then
  survived two consecutive full game-process restarts with fenced generations
  and stable fingerprints. Abrupt validated companion and game termination each
  recovered through a fresh session and passed the full smoke.
- The actual broker rejects a Windows anonymous security token at the kernel
  ACL before handshake and remains available to the current user. A named-
  second-user proof still requires credentials or a pre-authorized helper
  unavailable to the current medium-integrity session.

### Remaining hardening work

- Populate the currently empty catalog/mod fingerprints and replace the
  `not-installed` logical-time, RNG, and stable-order hook entries with verified
  method fingerprints as those P1 systems are built.
- Do not introduce a durable spool until it has an explicit size and eviction
  policy. Add deadline and deeper fault-injection coverage beyond the current
  weighted queue, saturation, malformed-frame, reconnect, and cancellation
  tests.
- Extend fault injection to deterministic interruption during an active write
  or command execution when a safe suspension hook exists.
- Complete the named-second-user pipe ACL proof when a disposable credential or
  pre-authorized helper is available; anonymous-token kernel rejection is
  covered.
- Pin the bridge's compiler/SDK and trial C# 13 with only source-level compiler
  metadata shims. Run compile, IL/API compatibility, clean-load, and command
  smoke tests; any feature requiring a newer runtime API remains prohibited.
- Document the BepInEx/AnchorChain prerequisite only in their edge-case guides.

### Exit gate P0

- One command cannot crash or stall Unity's frame loop.
- Unsupported, malformed, oversized, timed-out, and disconnected requests return
  deterministic errors.
- No generic command-type route, stack trace, unbounded frame, or unbounded queue
  remains reachable before mutation is enabled.
- Pipe ACL/token tests reject another user/session and an unauthenticated local
  caller; the main-thread test fails when deliberately executed off-thread.
- Restarting either side reconnects without duplicate responses or orphan tasks.
- The deployed directory contains only the plugin DLL and PDB.
- CI/local tests pass without Sea Power; the live smoke passes on the pinned game
  build.

## Phase 1 - inventory, identity, and observation

### Objective

Build an evidence-backed read model before attempting to create or mutate units.

### 1A. Catalog pipeline

- Define normalized IDs for countries, platforms, variants, loadouts, weapons,
  sensors, magazines, aircraft, facilities, and relevant doctrines.
- Extract from the game's winning virtual-file view where possible, not just the
  `original` directory.
- Record source path/mod provenance for each normalized record.
- Normalize units, dates, enum values, aliases, and missing/default fields.
- Produce canonical ordering and a SHA-256 catalog/mod fingerprint.
- Generate a human-readable validation report for duplicates, dangling IDs,
  unsupported types, and conflicting overrides.
- Keep generated catalog output git-ignored; keep schemas and small synthetic
  fixtures tracked.

### 1B. Stable live identity

- Enumerate task forces and supported live object types on Unity's main thread.
- Define stable-ID attachment for Crimean-Tom-owned entities using the least
  invasive supported metadata seam.
- Define read-only surrogate identity for pre-existing scenario entities.
- Rebuild the stable-ID/live-object index on scene/load transitions.
- Detect duplicate, missing, destroyed, replaced, and stale mappings.
- Never use Unity instance ID or list position as durable identity.

### 1C. Canonical read model

- Extract scene/time/pause/time-scale state.
- Extract entity kind/type/variant/loadout, coalition, group and parent links.
- Extract position, orientation, course, speed, altitude/depth, and lifecycle.
- Extract damage/system status, fuel, magazines/stores, hosted aircraft, readiness.
- Extract current orders/waypoints and only the sensor/contact state appropriate
  to the requested information scope. Schema 7 now uses the player plotting
  table and deliberately omits true target references and coordinates.
- Define quantization, null/unknown semantics, and provenance per field.
- Split canonical state into subsystem hashes so divergence can be localized.

### 1D. Instrumentation experiments

- Measure which fields can be read safely in Update versus FixedUpdate.
- Measure object counts, extraction time, allocation, and frame impact.
- Observe pause, time compression, mission load/unload, save/load, destruction,
  weapon-in-flight, flight-deck, and hosted-aircraft transitions.
- Establish initial checkpoint cadence and maximum read budget from evidence.
- Measure tactical event rate, checkpoint growth, pipe/remote backlog, and
  synchronous materialization stalls for representative surface, air, submarine,
  and weapon-heavy scenarios.
- Measure floating-origin relocation and the usable single-process tactical
  bubble with distant terrain, sensors, weapons, effects, and physics.
- Test native save/load with a missile, torpedo, and bomb in flight, including
  guidance/target links and final outcome.
- Blind-fire representatives for all three weapon families now survive the
  pinned native round trip and resume their native lifecycle. A targeted RIM-66G
  also restored its target link, resumed guidance, and destroyed that target.
  Torpedo/bomb target equivalence remains open, and an unrelated submarine
  magazine-count change prevents treating the native save as authoritative
  full-state persistence.
- Record unsupported or lossy fields rather than inventing mappings.

### 1E. Bridge-driven UI automation

- Prefer Sea Power view-model commands, navigation services, and supported game
  methods executed on Unity's main thread over coordinate-based desktop input.
- Add narrowly typed, allow-listed actions only when a normal feature or live
  integration scenario encounters that screen; do not stop campaign work to
  build a complete menu-button catalog.
- Give every action explicit scene/UI preconditions, a bounded deadline, and a
  machine-readable postcondition so the caller can tell whether navigation
  actually completed.
- Expose discovered actions through a dedicated local automation capability.
  Do not add a generic reflection call, arbitrary element lookup, coordinate
  click, or unrestricted command passthrough to the bridge.
- Keep local test/navigation automation separate from campaign simulation
  authority. It requires the authenticated local control channel and is never
  accepted from a remote player or campaign worker.
- Fingerprint the underlying game methods/view models. Disable an action when
  the pinned seam changes instead of guessing against a new game build.
- Let the live integration path progressively replace desktop clicks: scenario
  selection and briefing progression first, then only the dialogs and panels
  needed by later campaign-facing tests.
- Retain desktop Computer Use as the discovery and visual-verification fallback,
  capturing fresh state before each remaining input.

### 1F. Deterministic tactical runtime adapter

- Complete the time/RNG/update/coroutine/job/order call-site inventory in
  [Deterministic tactical runtime adapter](../architecture/deterministic-runtime-adapter.md).
- Measure default repeat-run and two-process divergence before changing behavior.
- Build isolated multi-instance pipe, port, log, save/profile, and game-instance
  state before treating simultaneous two-process results as valid evidence.
- Prototype coordinator-issued ticks and a fixed logical step independent of FPS.
- Inventory and seed every known RNG source; normalize materialization and update
  ordering, then repeat the divergence suite.
- Progress to keyed RNG and controlled scheduling only for call sites the first
  prototype shows are consequential.
- Record hook/method fingerprints and refuse controlled mode on mismatch.
- Determine whether `GameTime.time` can be controlled and safely rebased across
  absolute timers during long-running sessions.
- Keep all behavior-changing hooks in `ControlledExperimental` mode during P1;
  they cannot receive campaign authority.

### 1G. Port-operations maintenance projection

- Keep `crimean-tom-port-operations` as a disposable Task Force Mode UI shell,
  never as the authoritative campaign or a battle-resolution path.
- Extend the typed campaign read to extract the complete maintenance draft:
  stable unit identity, selected variant/loadout, points, repair state, rearm
  state, hosted-air assignment, and reinforcement availability.
- Add typed add/remove/refit/repair/rearm/confirm actions only when the vertical
  port flow reaches each stable public seam. Every action retains explicit
  preconditions and postconditions; there is no generic reflection or button
  passthrough.
- Define a versioned projection file and generator that converts an external
  port-state revision into the repository-owned campaign roster/session inputs.
- Treat Sea Power restart/reload as an expected materialization boundary when
  authored campaign data changes. Keep external theater time and durable entity
  identity independent of that process lifecycle.
- Import the edited native draft without committing it, validate it in the
  companion/service against expected campaign revision, budget, availability,
  port capacity, and idempotency, then commit one external transaction.
- Fence only conflicting administration for that force. Unrelated theater
  movement, logistics, and combat continue in the external simulation.
- Replace the temporary built-in mission context with an intentionally empty or
  non-resolving maintenance container once the UI dependencies are known. Until
  then, block Play/completion results from entering the campaign event stream.
- Prove the slice with one broad scenario: dock a fleet, generate the projection,
  restart/reload, refit and add a reinforcement, import/validate/commit, reload,
  and verify the new force while unrelated theater entities advance.

### 1H. Iteration load acceleration

- Keep load acceleration in an optional, reusable library; a cache failure must
  fall back to native/full work and must never prevent Sea Power from starting.
- Version every cache and bind reusable entries to source-file identity metadata
  plus the resulting executable, assembly, catalog, and mod-set fingerprints.
- Instrument cache hits, misses, entry count, and load/save state in runtime
  capabilities without including volatile cache counters in compatibility hashes.
- Provide disable/refresh modes for full-verification runs.
- Profile native load-screen stages before patching or replacing a Sea Power
  loader. Enable deeper acceleration only behind a pinned method fingerprint and
  prove identical realized catalogs/state with the optimization on and off.

### Exit gate P1

- A representative surface ship, aircraft, submarine, facility, task force, and
  weapon can be enumerated with stable session identity.
- Repeated extraction at the same quiescent state produces the same canonical
  serialization and hashes.
- Scene reload invalidates/rebuilds mappings without stale-object access.
- Live scenarios use bridge-driven UI actions for every previously discovered
  stable navigation seam; remaining coordinate inputs are documented discovery
  gaps rather than silently becoming permanent automation.
- The catalog report resolves every type used in test scenarios and fingerprints
  the active game/mod view.
- Extraction stays within the measured per-frame budget.
- Every portable field is classified as exact, quantized, derived, unknown, or
  unsupported.
- Default nondeterminism has a published baseline, and the controlled adapter has
  published tick/time/RNG/order results on the pinned build.
- Every simulation-critical time/RNG/update call site is classified; unknown
  promotion-critical paths block hot-replica validation.
- The native active-weapon save/load, tactical-bubble, `GameTime.time` rebase,
  event-rate, and multi-instance-isolation experiments publish their evidence and
  explicit blockers for later phases.
- The port-operations shell can expose a populated external fixture and return a
  typed, uncommitted maintenance draft without creating a competing campaign
  outcome or treating a native save as authoritative.

## Phase 2 - bounded mutation and materialization

### Objective

Prove reversible ownership-scoped mutation for one narrow entity family.

Current implementation status (20 August 2026): the Knox vertical slice now
materializes, checkpoints, applies fenced updates, suspends outside the active
world, restores a retained object, and reconstructs from the checkpoint after
scene reload. The lifecycle claim is explicitly narrowed to retained native
suspension because the pinned runtime exposes no safe memory-releasing quiet
teardown. Eleven live fault-injection boundaries, scheduler resource accounting,
and fenced detached-recovery cleanup complete exit gate P2 for this narrow
family.

### 2A. Mutation safety framework

- Add an allow-listed mutating capability disabled by default.
- Require session, assignment, authority epoch, expected revision, and idempotency
  key on every mutation.
- Validate payloads in companion and plugin before touching game state.
- Execute mutations only on Unity's main thread. Admission and multi-step work use
  bounded budgets, but synchronous game constructors are measured as blocking
  operations rather than falsely described as yieldable per-frame work.
- Track every created object by assignment and stable ID.
- Add dry-run validation and structured partial-failure reports.
- Define rollback/cleanup that cannot delete non-Crimean-Tom objects.

### 2B. First surface-unit materialization

- Select one well-understood surface platform and loadout fixture.
- Spawn through the game's supported construction path.
- Attach stable identity and task-force membership.
- Apply position/course/speed and supported readiness/stores/damage state.
- Read the object back through the canonical extractor.
- Compare requested versus realized state field by field and hash supported state.
- Publish a per-field tolerance artifact with unit, quantization, rationale,
  observed distribution, and whether the field is promotion-critical.
- Benchmark synchronous creation by platform/type and island size, including
  frame stall, lease/control responsiveness, allocation, and rollback time.

### 2C. Lifecycle operations

- Apply a bounded movement/order update.
- Apply a bounded state correction at a safe tick.
- Capture a portable checkpoint.
- Test native `ObjectBase.Disable/Enable` first: no destruction/loss event,
  formation/task-force/contact and hosted-child behavior defined, subsequent
  save/load valid, and CPU/memory relief measured.
- If reclaiming the retained GameObject/systems is required, run a separate
  memory-releasing quiet-removal spike before claiming full dematerialization.
  Otherwise describe the supported lifecycle as suspension, not removal.
- Rematerialize from the checkpoint and compare canonical state.
- Repeat after scene reload and companion reconnect.

### 2D. Expand supported types incrementally

After the surface fixture passes, repeat separate gates for:

- formation/task-force relationships;
- aircraft and hosted-aircraft/flight-deck state;
- submarine depth and relevant sensor state;
- land facility/base state;
- active weapon/guidance state, only if portable reconstruction is feasible.

In-flight weapons block a handoff only for state families whose native/portable
round-trip experiment is incomplete or failed. A successful validated native
save transfer may permit whole-island handoff without field-level reconstruction.

### Exit gate P2

- A supported entity completes materialize -> inspect -> checkpoint -> remove ->
  rematerialize with stable identity and all supported fields within tolerance.
- Duplicate create/update/delete commands are idempotent.
- Stale revision/epoch commands and unsupported catalog IDs fail closed.
- Injected failure at every operation step leaves either the source state or one
  recoverable owned Unity object, never an ambiguous canonical owner.
- Cleanup cannot affect scenario/user objects.
- Unsupported field loss is explicit and prevents promotion/handoff where unsafe.
- Native suspension and any deeper quiet removal each pass their safety/resource
  conditions independently of ordinary destruction, or the architecture
  explicitly narrows its lifecycle claim.
- Synchronous materialization cost and per-field tolerances are published and
  used by scheduler capacity rather than hidden behind a per-frame budget claim.

## Phase 3 - deterministic campaign kernel

### Objective

Run a persistent campaign outside Sea Power with exact replay.

### 3A. Domain and contracts

**Status: complete (20 August 2026).** The game-independent contracts, immutable
canonical domain, in-memory command/event reducer, strict versioned JSON wire
format, fixed-point units, canonical state hash, and structurally filtered
coalition projection are implemented. See the
[Phase 3A evidence](../evidence/phase-3a-kernel-evidence-2026-08-20.md).

- Create campaign domain, engine, contracts, and test projects without game
  assembly references.
- Define typed IDs, campaign time, revisions, commands, events, entity lifecycle,
  location, group/base relationships, orders, resources, intelligence, weather,
  assignments, and fidelity state.
- Define fixed/quantized units and serialization formats.
- Separate canonical state from caches and projections.

### 3B. Event journal and snapshots

**Status: complete.** The SQLite WAL schema, compare-and-swap writer
lease/epoch, transactional command/event append, durable subject revisions,
content hashes, strict idempotency behavior, ordered reads, and origin-to-head
replay are implemented. Canonical snapshots now decode strictly, validate all
domain invariants, and recover by replaying the journal tail. Adjacent
content-addressed checkpoint/catalog blobs and cross-store integrity diagnostics
are implemented. Staged atomic database-plus-blob backup and restore are also
implemented. Controlled faults and abruptly terminated helper processes cover
all fourteen declared persistence boundaries, including lost acknowledgements,
and an independent lease-holder process proves successor fencing. See the
[Phase 3B journal evidence](../evidence/phase-3b-journal-evidence-2026-08-20.md).

- Acquire a compare-and-swap writer epoch before the first durable mutation;
  require the current writer epoch in every append/lease transaction so a stale
  coordinator cannot write even in the initial SQLite deployment.
- Implement transactional append with campaign commit position and expected
  entity revisions.
- Implement idempotency keys and duplicate command/event behavior.
- Add SQLite WAL storage, migrations, snapshot creation, validation, replay,
  integrity checks, and transactional backup/export.
- Content-hash checkpoint blobs and detect corruption.
- Store checkpoint/catalog blobs in an adjacent content-addressed local directory
  and make database-plus-blob backup/restore atomic at a recorded commit position.
- Add crash injection between validation, append, snapshot, and acknowledgement.

### 3C. Core simulation

**Status: complete.** A typed simulation command/event now advances a durable
scheduled queue through region movement, sustainment, repair, attrition, AI
patrol intent, and coalition intelligence. Keyed randomness and stable sorted
merge produce identical event/state bytes with one or many workers. See the
[Phase 3C evidence](../evidence/phase-3c-simulation-evidence-2026-08-21.md).

- Campaign clock and scheduled event queue.
- Graph/region movement and cross-region transfer.
- Basic fuel/stores consumption, replenishment, readiness, repair, and attrition.
- Orders and AI intent sufficient for an unattended theater slice.
- Keyed RNG and stable deterministic merge of parallel work.
- Basic coalition intelligence estimates distinct from true state.

### 3D. Replay and invariants

**Status: complete.** The broad theater proves long-horizon snapshot/journal
replay to a pinned golden hash, strict schema/algorithm pins, conservation and
state invariants, scheduling-independent event/state bytes, and fog-of-war
sentinel exclusion. See the
[Phase 3D gate evidence](../evidence/phase-3d-gate-evidence-2026-08-21.md).

- Golden replay fixtures.
- Broad long-horizon integration checks for conservation, non-negative resources,
  valid lifecycle, reachability, idempotency, and one canonical owner/revision.
- Single-thread versus parallel-worker hash comparison.
- Schema and algorithm version pinning.

### Exit gate P3

- A multi-region synthetic theater advances without Sea Power.
- Snapshot plus journal replay produces an identical canonical hash.
- Killing the process at every persistence boundary recovers to one valid durable
  head without duplicate effects.
- Two coordinator processes cannot both acquire or use a valid writer epoch.
- Same inputs produce the same result under different worker scheduling.
- Fog-of-war projections never include synthetic true-state sentinel fields.

## Phase 4 - continuous single-client vertical slice

**Status: complete (21 August 2026).** Phase 3 provides the authoritative journaled theater;
generated authenticated duplex gRPC, durable reconnect/spooling, separate
content-addressed checkpoint HTTP, canonical assignment ownership, frozen
tactical authority, ordered movement commit, checkpointed quiesce/release, and
service-restart replay, and deterministic 120-quanta checkpoint fallback now
work in the broad integration story. Checkpointed fuel/store/damage and terminal
destruction capture are canonical, same-revision initial/final checkpoints have
distinct durable identities, and catalog mismatch fails before materialization.
Game crashes before the initial checkpoint and after an unproposed local
mutation now recover within the fixed bound without admitting local-only state.
The live Sea Power round trip and typed normal exit are complete. The service
exposes the measured display-first/provisional policy, journal durability
boundary, and fixed rollback rule at authenticated `GET /durability-policy`.
See the
[Phase 4 transport evidence](../evidence/phase-4-transport-evidence-2026-08-21.md).

### Objective

Move one real entity between the external campaign and a running Sea Power
client without creating a separate campaign timeline.

### Work

- Add typed bidirectional ASP.NET Core gRPC/Protobuf service-to-companion
  transport for one trusted local client. Generate both client and service C#
  types from versioned `.proto` contracts.
- Restart interrupted streams from an explicit campaign commit cursor and treat
  the durable journal as authoritative; transport reconnection is not durability.
- Keep checkpoints/catalogs on authenticated content-addressed HTTP endpoints so
  bulk transfer cannot monopolize the gRPC session stream.
- Implement assignment and portable checkpoint contracts.
- Start with globally paused campaign handoff at a deterministic tick.
- Bind one tactical geography center/bubble, fixed step, global target tempo, and
  pause state to the client synchronization session rather than to individual
  assignments.
- Materialize a small interaction island and activate one primary lease.
- Route ordered player commands through the service rather than committing local
  intent directly.
- Capture typed tactical events and periodic canonical checkpoints.
- Instrument when launch, impact, damage, destruction, UI, audio, and visual
  effects become visible relative to event proposal and durable commitment.
  Choose and document a feasible write-ahead/acceptance and bounded-rollback
  policy; do not assume “commit before display” is free.
- Dematerialize back to the operational engine using the quiesce protocol.
- Continue unrelated external entities during tactical simulation.
- Keep campaign objectives/triggers canonical in the external engine, or prove an
  intentionally empty tactical mission container whose local triggers cannot
  create a competing campaign outcome.
- Define a maximum `Suspended` interval and a deterministic restore,
  Operational-resolution, or force-resolution rule when no tactical worker
  returns.
- Expose a minimal theater/assignment diagnostic view.

### Mandatory scenarios

- Surface movement with waypoint changes.
- Fuel/store expenditure without combat.
- Weapon launch and target damage/destruction.
- Player exits Sea Power normally.
- Game crashes before and after a checkpoint.
- Companion disconnects and reconnects.
- Catalog mismatch blocks the handoff.

### Exit gate P4

- Theater time and unrelated entities continue while the island is tactical.
- Stable IDs, revisions, time, supported damage, fuel, and stores survive a full
  round trip.
- Replaying committed commands/events produces the same external result.
- No local-only tactical event becomes durable after an unacknowledged crash.
- The measured player-visible outcome timeline has an explicit durability and
  rollback contract rather than an assumed display delay.
- Operational fallback resumes from the last committed checkpoint with a clear
  bounded-loss report.
- A suspended island reaches restore, Operational resolution, or explicit
  force-resolution within its policy bound; it cannot remain suspended forever.

This is the first architecture proof and the first candidate for a very small
single-player prototype.

## Phase 5 - operational simulation and dynamic fidelity

### Current implementation slice

- Schema v4 records each entity's simulation domain and maximum fidelity, plus
  contact uncertainty and recent operational outcomes.
- The kernel rejects promotions above that ceiling, and tactical handoff fails
  closed for entities that cannot be represented by Sea Power.
- The deterministic Fulda Gap Gone Hot proving campaign supplies the large,
  representative theater used while implementing the remaining Phase 5 engine,
  scheduler, monitor, and live-game flows. Central Front land formations remain
  Operational abstractions; naval and air interactions provide the tactical
  promotion surface.
- The monitor is part of the Phase 5 exit artifact, not a separate authority. It
  now exposes the campaign graph, bases, units, task forces, domains, fidelity,
  damage, recent interactions, health, journal head, scheduler health and
  eligibility, island coupling, transition reasons, and target tempo from the
  authenticated diagnostic projection. Its dominant Babylon theater surface is
  a black wireframe map with wireframe unit/domain glyphs, bounded by square
  monochrome operator registers and limited semantic color highlights.
- The unattended resolver now performs deterministic region-level detection,
  uncertainty degradation, weapons expenditure, damage, destruction, and
  whole-task-force withdrawal. Coalition projections receive opaque track IDs
  instead of hostile canonical entity IDs. These mechanics are functional but
  remain uncalibrated until compared with measured Sea Power batches.
- The first dynamic-fidelity vertical slice treats each task force as a minimum
  island, merges opposing groups involved in recent region combat, applies
  player observation/control and simulation-domain ceilings, fences active
  authority and active interactions, and journals lower-fidelity island changes.
  A requested Tactical promotion remains desired-only until a companion reports
  successful materialization and an initial checkpoint; that response atomically
  activates authority and promotes the canonical entity. Rejected or interrupted
  preparation releases without creating phantom Tactical state.
  Health, simulation-source eligibility, tactical capacity, and transport
  backlog drive load shedding; suitable quiet islands demote before the global
  target tempo falls. The scheduler input and stable-sample count are journaled
  and snapshot-recoverable. Active quiet islands require two matching healthy
  interest-loss samples before quiescing, while load shedding remains immediate.
  Process-local diagnostics now measure recovery, advancement, and scheduling
  latency/work throughput plus service memory without feeding wall time into
  simulation semantics. The fixed step remains 33,333 microseconds.
- This is not yet the P5 gate: sensor/weapon/host coupling, hysteretic split,
  client migration/reserve accounting, live Sea Power materialization,
  performance targets, and calibration remain.

### Objective

Advance unattended engagements credibly and promote/demote fidelity based on
player interest and coupling.

### Work

- Implement operational detection, contact uncertainty, maneuver, engagement,
  weapon expenditure, damage, withdrawal, and outcome events.
- Calibrate against batches of Sea Power observations without copying transient
  implementation details. Use the game's unmodified tactical model only within
  its measured valid compression range (initially no claim above 10x); separately
  validate any faster controlled-adapter execution.
- Implement scheduler inputs: proximity, player observation/control, active
  interactions, strategic importance, client capacity, and health.
- Build the interaction coupling graph with merge/split hysteresis.
- Measure whether task force is the practical minimum assignment unit and where
  sensor, weapon, host, plotting, and physics coupling force larger islands.
- Implement safe island merge/split protocols and cross-boundary deferral.
- Implement the health states, simulation-source eligibility, reserve budgets,
  load-shedding ladder, migration protocol, and global tempo controller in
  [Load management](../architecture/load-management.md).
- Add fidelity budgets and operational fallback policies. Native active-weapon
  transfer is allowed only if its P1/P2 round-trip gate passed; otherwise the
  affected boundary remains temporarily fenced.
- Add transition metrics and a campaign UI explanation for why fidelity changed.

### Exit gate P5

- A large synthetic theater meets its target wall-time budget without Unity.
- Relevant player approach/observation promotes an island to tactical fidelity.
- Leaving the area demotes it without state/time loss.
- Active weapon/guidance and physical relationships cannot be split unsafely.
- Operational outcomes fall within documented calibration envelopes for test
  cases; disagreement is visible, not hidden.
- Scheduler load shedding never silently drops committed entities or events.
- CPU/GPU/network/backlog stress drains or disables clients, migrates whole
  islands with reserve, and only then slows global tempo without changing the
  fixed step.
- Migration concurrency, destination headroom, affinity, hysteresis, and cooldown
  prevent oscillation or cascade failure in the stress suite.

## Phase 6 - redundant replicas and failover

### Objective

Allow multiple clients to simulate the same unit/island and replace an unhealthy
primary without split-brain outcomes.

Hot Shadow and promotable full-Observer work is conditional on the deterministic
runtime adapter passing its D0-D5 acceptance criteria. If it fails, this phase
retains leases, epochs, fencing, health, cold checkpoint reconstruction, and
operational fallback but makes no hot-failover claim.

### 6A. Assignment/lease service

- Implement Primary, Shadow, Observer, and Proxy roles.
- Store leases and epochs transactionally with event commitment.
- Add service-time lease expiry, heartbeats, readiness, lag, and capacity.
- Require compatibility and complete-state proof for promotion eligibility.
- Keep client load state separate from replica convergence state; implement
  `Eligible`, `DrainOnly`, `Disabled`, and `Quarantined` simulation-source
  eligibility without disconnecting the player session.

### 6B. Replication and divergence

- Broadcast the same committed checkpoint and ordered inputs to full replicas.
- Require the same validated runtime-adapter and simulation-manifest versions.
- Grant identical logical tick ranges rather than allowing FPS-driven progress.
- Collect canonical and subsystem hashes at defined ticks.
- Classify exact match, tolerated quantized drift, correctable drift, and unsafe
  divergence.
- Implement event catch-up, correction delta, checkpoint reload, and demotion.
- Keep render projections and hidden promotion state logically separated.

### 6C. Promotion

- Fence the old epoch before selecting the new primary.
- Rank eligible replicas by compatibility, completeness, cursor/tick freshness,
  hash agreement, health, capacity, and locality.
- Activate at an explicit effective tick.
- Reject every late old-epoch event and rebase a returning primary.
- Fall back to the operational engine if no full replica is eligible.

### 6D. Load-driven reassignment and minimum validation

- Exercise prepare-at-source, quiesce tick, final checkpoint, atomic epoch fence,
  destination activation, post-commit native suspension, and—only if separately
  validated—memory-releasing quiet removal for whole islands.
- Preserve reserve capacity for failover and reject destinations that are already
  pressured or cannot afford temporary overlap.
- Apply basic kinematic, detection-range, weapon/store, and event-causality
  plausibility checks before accepting multiplayer tactical outcomes. Deeper
  hostile-client adjudication remains a later release gate.

### Adversarial failure matrix

Test primary process kill, companion kill, pipe break, remote partition,
asymmetric packet loss, stalled Unity frame loop, paused game, slow disk,
corrupt checkpoint, stale shadow, incompatible catalog, duplicate messages,
reordered messages, delayed old-primary events, and coordinator restart during
promotion. Also test CPU/GPU throttling, queue/checkpoint bursts, destination
failure during migration, every destination pressured, repeated overload, false
capacity reports, and migration storms.

### Exit gate P6

- Two clients visibly simulate the same selected unit under explicit roles.
- If the runtime adapter is validated, a warm shadow takes authority within the
  measured recovery objective without duplicate command, weapon, damage, or
  destruction events.
- If the adapter is not validated, the documented cold-checkpoint/operational
  path meets its measured recovery objective and the product makes no hot-shadow
  claim.
- The old primary can never commit after its epoch is fenced.
- A full observer may be promoted only with authorized complete hidden state.
- A proxy can never be promoted.
- Two-replica hash disagreement triggers diagnosis/resync rather than claiming a
  majority.
- Loss of every tactical replica affects only islands unsafe for operational
  fallback; the rest of the theater continues.
- A disabled simulation source remains connected for authorized commands and
  filtered presentation but receives no Primary, Shadow, or full Observer role.
- Load-driven migration preserves one publisher, one cursor, fixed-step
  semantics, and the same ordered global tempo transition on every replica.
- Impossible basic outcomes are rejected before the trusted co-op alpha claim.

## Phase 7 - realtime multiplayer experience

### Objective

Turn the federated simulation into a coherent multi-player command and
observation experience.

### 7A. Identity and sessions

- Authenticated player, coalition, operator, and worker identities.
- Reconnect tokens, session replacement, rate limits, and audit log.
- Per-player control rights and delegation over units/groups.
- Exercise gRPC stream restart, Protobuf compatibility, cursor catch-up, and
  session fencing for companions/workers over loopback, LAN, and internet-like
  fault profiles.
- Use SignalR with MessagePack for player-facing browser/native UI projections,
  notifications, groups, and reconnect. SignalR messages never commit canonical
  campaign outcomes directly.

### 7B. Information projections

- True-state store isolated from player-visible contact/intelligence state.
- Visibility-filtered snapshots, events, late join, and reconnect.
- Contact identity/confidence/provenance rules.
- Tests with sentinel secret fields at every serialization boundary.
- State the deployment trust model in product metadata and UI. A player process
  holding full opposing truth is trusted even if the visible projection is not.
- Prototype a Crimean-Tom-owned filtered proxy object containing authorized track
  presentation only, with no sensors, weapons, AI, targetability, or hidden true
  state; prove the Sea Power pipeline does not promote it into a real contact.

### 7C. Commands and prediction

- Client command capture with prediction IDs.
- Service validation, ordering, effective tick, and rejection feedback.
- Local interpolation/prediction for responsive UI.
- State-aware correction without unsafe teleport/guidance corruption.
- AI/default behavior for disconnected commanders.

### 7D. Player-facing lifecycle

- Join a running campaign and receive a coherent local tactical view.
- Observe units using proxy or observer replicas.
- Transfer control without transferring canonical authority accidentally.
- Explain degraded fidelity, resync, promotion, and unavailable commands.
- Preserve campaign play when a host/player leaves.

### Exit gate P7

- Multiple players can join, command authorized units, observe filtered contacts,
  disconnect, and rejoin without stopping campaign time.
- Unauthorized state never crosses projection boundaries in automated tests.
- A playable engagement uses either trusted/neutral full-state execution or a
  proxy path that has passed the hidden-state and game-interaction spike; assigning
  only a player's coalition is not presented as a complete solution.
- Conflicting commands have deterministic service ordering and clear feedback.
- A player/client leaving does not end the campaign or destroy its units.
- Prediction errors reconcile without changing committed history.

This phase targets trusted/private multiplayer. Untrusted public play remains
outside the release claim until Phase 9 security evidence exists.

## Phase 8 - scale, persistence, and service availability

### Objective

Move from a working trusted campaign to a long-running, recoverable deployment.

### Work

- Benchmark and tune the supported SQLite WAL journal, snapshots, indexes,
  compaction, backups, and content-addressed local blob store at the intended
  single-player/private-co-op scale.
- Keep a complete packaged local deployment as a release-tested profile with no
  Docker, external database, broker, cache, or object store.
- Add PostgreSQL and external object-storage adapters only if measured scale or
  coordinator-availability goals exceed the SQLite/local-file profile. They may
  not become requirements for ordinary players.
- Make coordinator instances stateless above storage only for the optional
  horizontally available deployment profile.
- Add leader/fencing mechanism for lease/epoch allocation before running more
  than one active coordinator writer.
- Add operational worker registration, shard assignment, and rebalancing.
- Run load tests for theater entities, commands, events, players, replicas,
  checkpoint throughput, reconnect storms, and island merge spikes.
- Add backups, restore drills, retention, compaction, and campaign export/import.
- Add operator dashboards and safe campaign pause/drain/upgrade workflows.

### Exit gate P8

- A production-sized benchmark meets explicit tick-lag, latency, bandwidth,
  storage, and recovery objectives documented from test results.
- Database/service restart and failover preserve one ordered durable head.
- Backup restoration passes event/hash verification.
- Rolling companion/service compatibility follows negotiated versions.
- Load shedding preserves authority/control traffic ahead of bulk/projection data.
- The packaged SQLite/local-file profile passes install, host, backup, restore,
  LAN join, and long-running campaign tests on a clean machine.

## Phase 9 - hardening and release qualification

### Objective

Make supported claims explicit and prove them against failure, update, and abuse.

### Work

- Threat model client, companion, local pipe, remote transport, operator API,
  storage, logs, and update supply chain.
- Fuzz envelopes, contracts, checkpoint readers, and catalog input.
- Add resource exhaustion and malformed-client tests.
- Extend the P6 basic plausibility layer with hostile-client threat modeling,
  independent adjudication, timing/size side-channel mitigation, and exploit
  resistance.
- Test game/mod/catalog updates and migration or fail-closed behavior.
- Run long-duration soak campaigns and repeated materialization/failover cycles.
- Document supported game build, deployment modes, scale envelope, known lossy
  fields, recovery point, and recovery time.
- Package diagnostics and privacy-safe bug reports.

### Exit gate P9

- No unresolved critical threat or data-corruption path in the supported trust
  model.
- A full clean-machine install, update, campaign creation, play, crash recovery,
  and export/restore runbook passes.
- Compatibility failures preserve campaign data and explain recovery steps.
- Soak tests meet the declared stability and bounded-loss objectives.
- Documentation matches observed behavior and current commands.

## Campaign feature coverage

The infrastructure phases do not by themselves make a compelling campaign.
Each campaign system progresses from a deterministic skeleton in P3, through a
playable calibrated model in P5/P7, to supported content and balance in P9.

| System | P3 deterministic skeleton | P5/P7 playable capability | P9 qualification |
| --- | --- | --- | --- |
| Geography/environment | Regions, typed routes, base locations, scheduled weather | Route constraints, sea/air conditions, time-dependent effects | Theater/content validation and migration |
| Force lifecycle | Stable instances, groups, base/host links, availability | Formation, detach/merge, transit, deployment, withdrawal | Long-run conservation and edge-case coverage |
| Logistics | Fuel/stores/readiness counters and transfer events | Supply routes, capacity, replenishment, repair, replacements | Balance, bottleneck, exploit, and recovery tests |
| Air operations | Squadron/host inventory and abstract sorties | Readiness, loadouts, tasking, launch/recovery continuity | Carrier/base stress and loss/replacement campaigns |
| Orders/operations | Move, patrol, hold, escort, engage intent | Mission planning, ROE, priorities, contingencies, cancellation | Conflicting/late command UX and AI coverage |
| Intelligence | True state plus estimated contact projection | Detection, uncertainty, classification, decay, sharing | Fog-of-war leakage and deception testing |
| Combat | Typed expenditure/damage/loss events | Calibrated operational resolver and tactical promotion | Cross-fidelity outcome envelopes and exploit review |
| AI command | Deterministic basic order selection | Theater objectives, force allocation, logistics-aware planning | Long soak, difficulty, pathological strategy review |
| Objectives/progression | Event predicates and victory state | Dynamic objectives, consequences, narrative/event hooks | Content authoring, localization, save compatibility |
| Player economy/administration | Versioned policy/resources | Reinforcement, repair, allocation, optional points/budgets | Ruleset locking, migration, and abuse testing |

The first playable slice need not implement every row deeply, but no omitted
system may be represented as working. Unsupported mechanics remain explicit in
campaign metadata and UI.

## Product increments

| Increment | Phases | User-visible claim |
| --- | --- | --- |
| Diagnostic foundation | P0 | Companion can inspect a running supported game safely |
| Integration laboratory | P1-P2 | Supported test entities can round-trip under controlled tooling |
| Continuous prototype | P3-P4 | One campaign advances outside Unity and hands one island live to Sea Power |
| Single-player alpha | P5 | Dynamic fidelity and operational fallback support a small playable theater |
| Trusted co-op alpha | P6-P7 | Several clients share units, observers, failover, and ordered commands |
| Durable beta | P8 | Long-running dedicated campaigns have measured scale and recovery |
| Qualified release | P9 | Supported trust, compatibility, stability, and limitations are documented |

These labels prevent multiplayer plumbing from being mistaken for campaign
feature completeness, and prevent a broad campaign model from being mistaken
for safe distributed authority.

## Critical path

The shortest path to a valid architecture proof is:

```text
P0 control hardening
 -> P1 stable identity + canonical extraction + deterministic-adapter proof
 -> P2 one reversible materialized unit
 -> P3 deterministic journaled campaign kernel
 -> P4 continuous single-client round trip
 -> P5 dynamic fidelity
 -> P6 redundant failover
 -> P7 multiplayer
```

Work that can proceed in parallel once contracts stabilize:

- catalog extraction and synthetic campaign domain fixtures;
- storage adapter and replay test harness;
- operator diagnostics UI and structured telemetry;
- operational model research/calibration;
- threat model and protocol fuzz harness.

Game mutation must not outrun identity/extraction. Multiplayer must not outrun
fencing/persistence. Scale work must not hide correctness failures.

## Deferred decisions and their mandatory gates

These are deliberately uncommitted constants or mechanisms, not missing plan:

| Decision | Evidence required | Latest decision point |
| --- | --- | --- |
| Campaign time quantum | Sea Power timing instrumentation and engine benchmark | Before P3 clock contract freezes |
| Checkpoint cadence/size | Extraction cost, drift, bandwidth, recovery objective | Before P4 tactical events |
| Exact portable damage/store fields | Read/write round trips per platform class | Each P2 type gate |
| Suspension/dematerialization support | Native Disable/Enable, then optional deeper removal, with no loss event, dangling relationship, child loss, save corruption, or unmeasured retained cost | Before P2 lifecycle exit |
| Active-weapon handoff support | Native mid-flight save/load plus portable extraction/reconstruction experiment | Before P5 island split/migration |
| Tactical session bubble | Floating-origin relocation and distant-island sensor/physics/terrain benchmark | Before P4 session contract freezes |
| Outcome commitment/display semantics | Instrumented launch/impact/damage/destruction durability timeline | P4 exit gate |
| Island coupling thresholds | Sensor/weapon interaction traces and churn benchmark | P5 scheduler tuning |
| Load thresholds and tempo bounds | Stress throughput, queue, migration, recovery, and player-experience benchmarks | P5 load-controller defaults |
| Hot Unity replica viability | D0-D5 controlled clock/RNG/order/checkpoint suite | Before P6B begins |
| Default replica count | Failure probability, adapter cost, and recovery benchmark | P6 policy gate |
| Failover time objectives | Validated hot-shadow or cold-restore measurements under injected failures | P6 exit gate |
| Scale/player claims | End-to-end load and soak evidence | P8 exit gate |
| Filtered proxy viability | Proxy-object interaction and hidden-state sentinel spike | Before P7 information topology freezes |
| Public/untrusted trust model | Threat model and independent validation cost | P9 release scope |
| Need for PostgreSQL/external object storage | SQLite WAL and local blob benchmarks plus coordinator availability objective | P8 scale profile |
| Need for raw WebSockets, extra streams/connections, or QUIC/UDP | gRPC/Protobuf and UI SignalR latency, allocation, loss, reconnect, and control-starvation benchmarks | P7/P8 network tuning |

Every item has a phase deadline; none may silently become a hard-coded default
before its evidence exists.

## Test portfolio

### Pure deterministic tests

- reducers and command validation;
- keyed RNG vectors;
- event ordering/idempotency/revision conflicts;
- snapshot/journal replay and migrations;
- interaction graph/island merge-split state machines;
- leases, epochs, fencing, promotion ranking, and expiry;
- visibility projections and secret-state sentinels;
- single-thread/parallel equivalence and long seeded runs.

### Protocol tests

- serialization compatibility and golden messages;
- generated gRPC client/service parity, Protobuf compatibility, bidirectional
  stream restart, durable cursor catch-up, and restart beyond transient buffers;
- strongly typed SignalR server callbacks, UI MessagePack round trips, WebSocket
  negotiation, and UI reconnect behavior;
- unknown optional versus required fields;
- fragmentation, chunk hash, reconnect cursor, and cancellation;
- malformed, oversized, truncated, duplicated, delayed, and reordered
  length-prefixed messages;
- backpressure and priority under bulk checkpoint transfer;
- authentication expiry/revocation and session replacement.

### Game integration tests

- plugin load and main-thread dispatch;
- scene/load transition and stable-ID remapping;
- each supported object type's read/write matrix;
- materialize/dematerialize/rollback;
- damage, stores, hosted aircraft, weapons, and task-force relationships;
- fixed step, global target tempo, pause, and tactical clock mapping;
- baseline versus controlled-runtime divergence across frame rates and machines;
- seeded/keyed RNG vectors and stable materialization/update order in live Unity;
- coordinator-granted tick ranges, lead/lag boundaries, and deterministic input
  application;
- repeated reconnect, game restart, and game-update compatibility.

### Distributed fault tests

- kill/freeze/partition every component at every handoff step;
- split-brain and delayed old-primary messages;
- stale/corrupt/missing checkpoint;
- divergent replicas and correction escalation;
- coordinator/store restart during append, snapshot, assignment, and promotion;
- reconnect storms and capacity exhaustion;
- total tactical loss with operational fallback;
- CPU/GPU throttling, frame collapse, event/checkpoint bursts, pipe/network
  backlog, and all-destinations-pressured behavior;
- destination failure at each migration stage, repeated overload, false metrics,
  and migration-storm suppression;
- global tempo changes at ordered ticks without fixed-step/hash changes.

### Human/UI tests

- continuous theater state is understandable before entering tactical view;
- assignment/fidelity changes do not masquerade as unit destruction;
- command acceptance/rejection and prediction correction are legible;
- observers do not learn hidden state from UI, logs, map artifacts, or timing;
- player disconnect/rejoin and controller transfer are understandable;
- operator diagnostics explain authority and recovery decisions.

## Evidence package for every phase

Each phase review should contain:

- pinned commit/worktree description, game build, plugin/companion version,
  protocol/schema/simulation versions, catalog/mod fingerprint;
- requirements-to-test matrix for that phase's exit gate;
- automated test and live integration results;
- canonical input/output fixtures and hashes where redistributable;
- performance/allocation/network/storage measurements;
- injected failure results and bounded-loss statement;
- new unsupported fields and known limitations;
- threat/privacy impact;
- rollback/recovery procedure;
- explicit approval or rejection of proceeding to the next phase.

## Adversarial review checklist

The planned review should attempt to falsify, not confirm, the design.

### Authority and split brain

- Can two primaries obtain overlapping valid leases?
- Can an event validate before an epoch changes and commit afterward?
- Can delayed, duplicated, or replayed old-primary messages mutate state?
- Can coordinator/database failover allocate the same epoch twice?
- Is promotion safe if the shadow agrees on hash but lacks hidden transient state?

### Determinism and divergence

- Which field or algorithm still depends on wall clock, frame rate, thread order,
  unordered iteration, locale, platform floating point, or a sequential RNG?
- Does canonical quantization hide a tactically meaningful divergence?
- Can a correction create impossible physics, guidance, or duplicate damage?
- With two replicas disagreeing, is any code incorrectly treating one as truth?

### Materialization and identity

- Can a partial spawn leave duplicate live objects or consume stores twice?
- Can a scene transition reuse a stale Unity reference or ID?
- Can dematerialization delete a user/scenario object?
- Which state cannot round-trip, and does that make promotion unsafe?
- What happens to in-flight weapons, hosted aircraft, sensor tracks, and damage
  over time at each handoff boundary?

### Time and causality

- Can a client run too far ahead and reveal or act on an uncommitted future?
- Can a late order be applied before an already observed outcome?
- What does pause mean with one, several, or zero tactical clients?
- Can any overload path enlarge/skip a step or let one assignment choose a local
  time scale instead of applying the global ordered tempo?
- Can island merge/split reorder causal sensor, weapon, or damage events?
- Does recovery rewind information already exposed to players?

### Security and fog of war

- Can observer/shadow state, logs, hashes, errors, timing, or checkpoint sizes
  reveal hidden enemy truth?
- Can a client request a role or entity outside its authorization?
- Can arbitrary payloads reach reflection, file paths, or unrestricted game APIs?
- Can a malicious client fabricate plausible outcomes or exhaust queues/storage?
- Are tokens or opposing-force state present in saves, crash dumps, or logs?

### Availability and degradation

- Which failures stop the whole campaign rather than one island?
- Can lease/control traffic be starved behind checkpoint or projection traffic?
- Is operational fallback valid for every currently tactical entity state?
- What is lost when all full replicas disappear just before a checkpoint?
- Can repeated unhealthy-client promotion cause oscillation or cascade failure?
- Can a player stay connected after its game is disabled as a simulation source?
- Does every migration destination retain measured reserve after temporary
  source/target overlap, and are concurrent migrations bounded?
- Can false load reports shed strategically inconvenient work or leak hidden
  force composition through migration timing/checkpoint size?

### Compatibility and operations

- Does a game/mod update change a field without changing the fingerprint?
- Can a mixed-version client receive a command it only partially understands?
- Are backups self-consistent with checkpoint blobs and event positions?
- Can operators diagnose and safely recover without editing database rows?
- Does every claimed scale/recovery number have a reproducible benchmark?

Any failed question becomes either a design change, a phase exit blocker, or an
explicitly bounded product limitation. It must not be dismissed as an edge case
without a recorded impact and fallback.

## Definition of plan completion

This planning phase is complete when:

- the system architecture has one canonical owner for every state category;
- continuous materialization, command/event flow, time, interaction islands,
  redundancy, promotion, load shedding, simulation-source disabling, global
  tempo, persistence, security, and failure behavior are defined;
- every implementation phase has prerequisites, concrete deliverables, mandatory
  scenarios, and an exit gate;
- unknown constants have evidence requirements and decision deadlines;
- adversarial review questions map to a phase or release blocker;
- repository documents no longer present generated isolated missions as the
  normal Crimean Tom architecture.
