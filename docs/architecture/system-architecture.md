# System architecture

## Scope

This document defines the active architecture for a local-first dynamic
campaign. The first product target is one campaign application, one campaign
service, and one Sea Power tactical process on one machine. Private multiplayer
extends the same campaign service after the local player loop works.

[Distributed Operational workers, hot replicas, and live tactical migration](../future/distributed-simulation.md)
are not active architecture.

## Product boundary

The campaign application is the product shell. It owns:

- campaign creation, loading, saving, and time controls;
- the strategic map and coalition order of battle;
- orders, objectives, intelligence, logistics, and campaign results;
- tactical-entry requests and return summaries; and
- local hosting and later private multiplayer sessions.

Sea Power is the tactical engine. It owns:

- the local battle simulation;
- control of the player's campaign-owned task force;
- local terrain, sensors, weapons, damage, and tactical AI; and
- the tactical map, augmented with campaign-backed friendly units and hostile
  tracks.

Sea Power does not own the campaign save, strategic AI, or global campaign UI.

The campaign service is also the target full-fidelity simulator for every
entity outside a Sea Power lease. It may omit Unity presentation work and sleep
mechanics that cannot affect the current state, but it may not replace relevant
combat with a less accurate outcome model. Sea Power remains the reference for
the pinned game build. The parity contract and current mechanic gaps are in
[full-fidelity server simulation](full-fidelity-server-simulation.md).

## Active process topology

```text
Campaign application
  | HTTP now; SignalR later for realtime multiplayer updates
  v
Campaign service
  |- command validation and durable journal
  |- campaign clock and in-process Operational simulation
  |- strategic and operational AI
  |- coalition projections and player sessions
  `- SQLite WAL plus content-addressed local files
  |
  | authenticated typed companion protocol
  v
Local companion
  |
  | current-user named pipe
  v
Sea Power Runtime Bridge
  `- Sea Power tactical process
```

The companion isolates the campaign service from Unity lifecycle and thread
constraints. The Runtime Bridge performs typed game inspection and mutation on
the correct Unity thread.

## Runtime projection boundary

The companion and Runtime Bridge use separate logical paths for control and
projection work. They may share one named-pipe connection, but they do not share
one synchronous execution contract.

Control commands cover scenario transitions, pause, tactical entry, checkpoint,
return, and explicit player focus. They use priorities, deadlines, command IDs,
and idempotency keys. Retrying a command after an unknown result must not repeat
the game mutation.

Projection traffic carries map and presentation state. A game-pipe session starts
a new projection generation. Its first revision is a full snapshot; later
revisions contain stable-identity upserts and explicit removals. The bridge
acknowledges receipt before it finishes every Unity-facing change, then applies
that revision over later frames. It reports busy rather than replacing unfinished
projection work. Projection cannot cancel a control command or inherit its
priority.

The target Runtime Bridge design divides this work into three responsibilities.
The current code keeps them in one adapter while the behavior settles:

- the projection store retains the latest authorized presentation record for
  each campaign identity;
- the map projection controller chooses visible symbols, clusters dense areas,
  performs hit testing, and changes the Noesis collection under a frame budget;
  and
- the local materialization controller owns selection, relocation, terrain
  readiness, visual-model creation, camera focus, cancellation, and failure.

Sea Power's `InteractionState.MouseAction` selects an `IMapSelectable` by setting
its `Selected` property. Campaign symbols use that native transition to publish
one typed selection event. The viewport hit-radius path exists only for Noesis
wrappers that hide the original symbol item. A typed focus command may start the
same operation without mouse input.

Each game-pipe session has a generation. A new generation cancels the old
campaign transport stream, invalidates pending responses, and opens a new stream
with the new game-session identity. The companion then replays the latest
projection as a full snapshot before returning to differential updates.
This rule covers
game relaunch, bridge reload, companion restart, and a dropped pipe. A periodic
refresh remains a fallback if the immediate replay fails.

## State ownership

The campaign service owns canonical campaign truth. Sea Power receives a fenced
lease for one interaction island. During that lease:

- Sea Power advances the leased entities;
- the campaign service does not advance those entities;
- the campaign service continues advancing unrelated entities; and
- presentation proxies remain non-authoritative.

Every durable mutation includes the applicable campaign, entity or island,
revision, authority-epoch, and idempotency fields. The service accepts a tactical
result once or rejects it. Transport delivery may repeat without repeating the
campaign effect.

Authority results enter the companion's durable outbox before the companion
writes them to the campaign stream. A Sea Power game-session change first stops
new authority work, then gives the current service-frame handler or periodic
state capture up to 15 seconds to finish and stage its result. Only then does it
cancel the old campaign stream. The outbox write has its own 15-second disk
timeout and does not inherit game-session cancellation. A failed wire write can
therefore replay the staged frame, while a stuck handler still reaches a bounded
fence. Session cancellation is transport control flow. It must not become an
`AssignmentPrepared` or `FinalCheckpointStaged` failure for an effect that may
already have reached Sea Power.

The player API derives coalition authority from the durable local player
profile. A client does not select its own coalition or command scope in an order
request. Each player order names a friendly group, expected group revision, and
idempotency key. Reusing a key with a different payload conflicts; repeating the
same payload is a duplicate success.

The journal, not any process memory or UI cache, determines the accepted
timeline.

## Identity and representation

Each campaign entity has one stable identity across Strategic, Operational, and
Tactical fidelity. A Sea Power object, map record, and visual proxy are
representations of that identity, not new campaign entities.

The supported representation states are:

```text
campaign record
  -> map record
  -> optional visual proxy
  -> leased Sea Power object
  -> accepted checkpoint and campaign record
```

A visual proxy has no AI, sensors, weapons, collision, damage authority, or
publication rights. Promotion hides the matching proxy before the tactical
object becomes player-visible. Demotion preserves the map record. Only the
currently selected inert world model is retained; deselection releases its
loaded model and materials while leaving the lightweight map record available.

There are two tactical-entry scopes:

- The campaign application may enter a whole selected group as one atomic
  interaction island. Every required member and coupled dependency must pass
  profile and capacity checks before that batch activates.
- Selecting a non-local friendly on Sea Power's map promotes that campaign
  entity only, plus any host or live coupling dependency required to represent
  it safely. Other members of its Operational formation do not block the
  selected entity because they exceed local capacity or lack a runtime profile.

If the selected entity is inside the active tactical locality, promotion adds
its native object to the current player task force without releasing the units
already under player control. Only a runtime preflight that identifies a distant
floating-origin rebase may start the checkpoint, suspension, and locality
handoff sequence.

The second path is a representation transition, not a campaign regrouping. The
entity keeps its campaign group, orders, identity, and revision. Its inert model
can appear immediately, but it must be replaced by a leased native Sea Power
object before native status, weapons, sensors, AI, or orders are exposed. Native
creation and native readiness are separate states. The bridge suppresses the
proxy after the native constructor succeeds while tactical time remains paused;
the companion does not checkpoint, focus, or declare the assignment ready until
Sea Power reports the object initialized, active, and player-orderable.

The assignment pins the exact unit type, variant, and loadout used for native
construction. Until campaign saves carry an explicit variant for every entity,
the Runtime Bridge advertises compatible materialization profiles in canonical
preference order and the service selects the first exact unit/loadout match.
Sorting that list is not presentation cleanup: it can silently select a
different variant and make checkpoint identity impossible to restore.

Portable pose is defined through Sea Power's public position, altitude, and
depth behavior, not raw transform height. Applying a checkpoint may require one
raw-height correction after native readback; for example, Sea Power subtracts a
submarine's center-to-keel distance in `ObjectBase.getHeight()`. The final public
readback must match before activation.

Materialization is transactional through that readiness boundary. If readiness
fails before the first destination checkpoint, the companion sends a rollback
under the same assignment, authority epoch, and realized revision. The bridge
removes a newly constructed object through Sea Power's native destruction path
with scoring disabled. If the transaction reused a suspended predecessor, the
bridge restores its prior portable checkpoint and suspension instead. A
successful rollback removes the failed materialization receipt so the same
assignment effect can be attempted again, then forces projection reconciliation.
Capturing the first destination checkpoint closes this rollback window.

Normal unload also requires positive evidence. The tactical-worker projection
keeps the latest released assignment per entity as a reclamation receipt. The
companion may suspend a local object when current authority is absent, but it
may destroy a checkpointed object only when its exact assignment ID appears as
`Released`. Destruction removes Sea Power's disabled-task-force reference and
uses the native clean-departure event so the unit is neither saved nor recorded
as a combat loss. A released pre-checkpoint remnant follows the rollback path;
a detached uncheckpointed record releases only its bridge ledger entry.

Scene loss is also an unload state. A bridge-owned record whose native object
has disappeared remains `detached-after-scene-loss` when it holds the current
authoritative checkpoint. Fencing that record reuses the checkpoint at its
existing revision and does not attempt to pause a tactical mission that no
longer exists. The campaign can then commit and positively authorize ledger
reclamation. A detached record without a matching checkpoint cannot publish a
result and follows explicit recovery or rollback instead.

Unity object references require Unity equality checks at this boundary. A
destroyed `UnityEngine.Object` can retain a non-null managed wrapper while
comparing equal to null. Readiness, control, checkpoint capture, release,
resource reporting, projection scans, and diagnostics must therefore treat
`object == null` as detached; ordinary C# pattern-null checks can expose stale
stores and control state or dereference destroyed scene objects. Detached
post-commit ledger reclamation is not scene-bound and must not enter the
tactical-pause guard.

## Campaign time and tactical leases

One campaign quantum is one simulated second. The default local clock advances
ten quanta every one wall-clock second, for a target tempo of 10x. The service
returns that clock contract with every player campaign view, and the application
shows both simulated date/time and target multiplier. Operators may override the
tick interval and quanta per tick for tests or calibration; an override changes
the declared tempo rather than silently changing the meaning of a quantum. Sea
Power may use its supported native fixed timestep inside the tactical process.

A tactical session sends heartbeats. Healthy heartbeats renew the island lease.
The rest of the campaign continues in parallel. The service marks a session
stale after eight seconds without an eligible heartbeat, then keeps its island
fenced for a 12-second wall-clock grace period. Reconnecting with the same
campaign and client identity during grace restores the live lease. After grace,
the service fences the stale epoch and uses the last accepted checkpoint or an
explicit Operational fallback.

Loading and inspecting a campaign never advances it. A new or restored campaign
starts paused. Tactical pause and campaign pause are separate states unless a
ruleset explicitly couples them and tells the player.

## Simulation location and interaction islands

Strategic state represents theater intent and aggregated command. Operational
state means that the campaign service advances an individual entity outside Sea
Power. Tactical state means that a leased native Sea Power object advances it.
These names describe authority and presentation, not acceptable differences in
combat mechanics. The server target is full consequential parity with Sea
Power for every supported platform and state.

The server can sleep collision, sensing, AI, or other mechanics only while a
dependency and next-event calculation proves that they cannot change the
result. It must retain resumable state and wake the mechanic before proximity,
orders, emissions, weapons, damage, environment, resource thresholds, hosted
operations, or authority transfer make it relevant. The full rules are in
[full-fidelity server simulation](full-fidelity-server-simulation.md).

The interaction island begins with the campaign-owned player task force and
includes entities coupled by live sensor, weapon, targeting, formation,
support, or collision relationships. Sea Power receives the whole island under
one epoch. The service must not split a consequential relationship between
authorities unless a mechanic-specific boundary adapter has passed matched
parity tests. Presentation tracks may cross without granting authority. An
in-flight weapon, its guidance dependencies, and its target remain together
until lossless transfer or an equivalent adapter exists. The service derives
membership through the
[interaction-island registry](interaction-islands.md). Handoff batches remain
transaction boundaries; several independently prepared batches can join one
island through host-child or live coupling relationships.

Sea Power hosts one active native locality inside one floating-origin envelope.
Campaign map records may span the theater, but native simulation objects may
not remain active thousands of kilometers from the current origin. Before a
selection moves the origin outside the current locality, the companion must
quiesce the old island, capture an accepted checkpoint, fence its epoch, and
remove or suspend its native objects. It can then relocate the origin and
promote the new island. A failed handoff keeps the old authority decision and
the new entity's map record; it must not leave two active localities.

This spatial rule also applies to authored mission objects. They are bootstrap
content, not permanent campaign residents. An authored object outside the
active locality must be suspended or removed before a distant rebase, even when
it is friendly or was present when the scenario loaded.

Strategic radars, high-end air defenses, and ballistic missile systems may
request persistent Tactical residency after those families are portable. Until
then, they remain present in campaign truth and on authorized maps while the
Operational model advances them.

## Map, intelligence, and proxies

The campaign application receives a coalition-filtered projection:

- all authorized friendly campaign entities; and
- only hostile or unknown tracks known to that coalition.

The Sea Power tactical map receives the same friendly continuity and filtered
contact picture. Locality does not control map visibility. Native and non-local
records must use equivalent Sea Power symbology, hover, selection, labels, and
focus behavior.

Map presentation and physical presentation are independent. Every authorized
record remains selectable even when level of detail hides its individual symbol
inside a cluster. Direct symbol clicks follow Sea Power's native
`IMapSelectable.Selected` path. A viewport candidate list resolves clicks that
arrive through a presentation wrapper against the last completed viewport pass;
it does not scan Unity objects. A spatial index can replace that fallback's
linear scan if measured click latency requires it.

Selecting a non-local friendly starts one entity-scoped materialization
operation. Its states
are `idle`, `selected`, `relocating`, `terrain-streaming`, `spawning`,
`initializing-native`, `focused`, `failed`, and `cancelled`. A newer selection
cancels the old operation. Repeated selection of the same identity resumes or
focuses the existing operation rather than creating another model. Terrain-backed
models stay hidden until placement passes. Relocation runs once for the operation.
Later terrain checks only probe readiness and never pause the battle, detach the
camera, or relocate the world again. The camera attaches only after the requested
model exists.

The bridge pushes a typed map-selection event to the companion. One promotion
orchestrator owns retries, idempotency, focus, and locality handoff. A slower
poll repairs missed events and reconnect state. Campaign heartbeats, assignment
recovery, and bulk projection refresh do not own this control path.

Selecting a hostile track uses the same interaction states but may create only a
classification-appropriate or uncertainty proxy at the estimated track
position. It must not load secret true-state data.

Map density is controlled through level-of-detail updates, clustering, and
bounded refresh rates. Viewport work first filters records against the visible
geographic bounds, then calls Sea Power's screen projection only for the
remaining records. The companion assembles campaign snapshots outside the game.
Noesis collection changes, terrain operations, model loading, and camera calls
run on the main thread through a measured work budget. Removing a physical object
never removes its authorized map record.

Projection transport is versioned and differential after the initial snapshot.
An unchanged source version does not enter the game pipe. Changed records and
explicit removals are preferred to another full-campaign snapshot; a full
snapshot is reserved for initial load and resynchronization.

## Tactical entry

Tactical entry is a transaction:

1. The campaign application requests entry for a task force or location.
2. The service computes the interaction island and reports unsupported state.
3. The service fences the island and issues an epoch-bound lease.
4. The companion prepares terrain, campaign-owned objects, map records, and
   portable state.
5. The bridge validates identity and placement, waits for Sea Power's native
   initialization and orderability conditions, and captures an initial
   checkpoint.
6. The service activates the lease, then Sea Power gives control to the player.

Failure before activation leaves the service authoritative. Terrain-backed
objects remain hidden until loaded terrain has been sampled and placement is
valid.

## Tactical return and recovery

Normal return quiesces new actions, captures a final checkpoint and typed
outcomes, and commits them once. The result includes entity fate, damage,
position, fuel, stores, ammunition, hosted assets, and other fields supported
by the portable-state contract. Unsupported fields are reported.

If Sea Power exits unexpectedly, the service keeps the last accepted checkpoint
and waits through the lease grace period. A reconnect with the same session may
resume only under a current epoch. Otherwise the service fences the old epoch
and recovers the island from the accepted checkpoint or Operational fallback.
No recovery path may leave two publishers.

Clean application exit, game restart, and service restart are active recovery
requirements. Hot failover to another running Sea Power process is not.

## Persistence

The default deployment uses SQLite WAL and content-addressed local files. The
store contains commands, events, campaign heads, snapshots, checkpoint
references, ruleset identity, and content fingerprints.

Snapshots accelerate recovery but do not replace ordered history. A successful
write requires compare-and-swap validation against the current head and writer
epoch. Backup writes to a new verified location. Restore never mutates a live
journal in place.

Save formats and campaign algorithms are versioned. An incompatible save stays
intact and receives a clear migration, compatible-version, or export path.

## Strategic and operational AI

Each coalition director sees its own forces, public geography and bases, and
that coalition's intelligence. It creates durable objectives and group
commitments. Selection is limited by domain reach, per-objective domain
capacity, and pressure on the first route hop. These are deterministic
anti-convergence rules, not a theater route reservation system.

Committed groups form role-balanced operational packages of two to six groups.
A package chooses one adjacent assembly region, releases slower groups before
aircraft and helicopters, and persists across director passes until execution,
expiry, or loss of a required member. Package membership never overrides an
open-ended player order or a live tactical assignment.

Domain controllers translate commitments and package phases into ordinary
campaign orders:

- ships and submarines assemble, execute their assigned patrol, escort, strike,
  or ASW role, and remain on station until recovery is required;
- fixed-wing aircraft reserve fuel and time for a return to a friendly base;
- helicopters use the same cycle with a shorter operating radius and larger
  reserve;
- ground formations assemble only when that does not pull them away from the
  front, then advance or hold; and
- logistics groups carry supplies forward and return to a depot to reload.

Fixed installations defend in place. A controller owns one replaceable order
slot per group; player orders, emergency withdrawal, and tactical authority
take precedence.

This is the current Operational approximation. It does not yet model aircraft as airbase
inventory, individual launch and recovery schedules, runway capacity, tanker
tracks, route reservations, or a package success/abort state machine.

This approximation is an implementation gap, not the target architecture. The
server must eventually execute compatible tactical mechanics for supported
aircraft while retaining strategic and operational command layers above them.

AI commands enter the same validation and durable commit path as player
commands. Fixed installations cannot receive movement orders. Player-controlled
forces remain subject to the selected control policy.

The active design keeps all AI and Operational ticks in the campaign service.
Process distribution is an optimization, not a product requirement.

## Local application and private multiplayer

The local application initially hosts one player session and the campaign
service. The same API later supports invited private players.

Multiplayer adds:

- durable users, invitations, revocation, and coalition roles;
- reconnectable player sessions and control rights;
- realtime filtered projections and command results;
- conflict handling for shared units and task forces; and
- TLS and rate limits when traffic leaves loopback.

Realtime delivery is not canonical history. Every client resumes from a
campaign cursor or fetches a verified projection snapshot. Player processes do
not receive enemy true state. Public hostile-client adjudication remains outside
the private multiplayer milestone.

## Security boundary

The local named pipe is restricted to the current user. Companion, operator,
and player capabilities use distinct credentials. Browser cookies that permit
read access cannot authorize writes. Every projection is built for an explicit
coalition and role.

Runtime, catalog, ruleset, and save fingerprints fail closed at tactical
activation. Logs and screenshots remain local and must not contain reusable
credentials.

See the [threat model](../planning/threat-model.md) for current gaps.

## Performance budgets

The authored order of battle determines campaign size; an arbitrary round
entity limit does not. Most entities remain in the server simulation. The
tactical process receives a bounded interaction island, map records, and
selected visual proxies.

Projection work has its own frame budget. A full-campaign snapshot may take
several frames to appear. A queued control frame takes precedence over bulk
traffic that has not started writing, but it cannot interrupt a pipe frame
already in flight. Projection work must not repeat a scenario transition or
force the game to serialize the entire campaign as tactical results. Tests record queue depth,
control latency, records applied per frame, snapshot-to-map latency, and the
longest main-thread projection slice.

Qualification records these populations separately:

- campaign entities;
- entities advanced by the server;
- awake and sleeping server mechanics;
- scheduled events and subsystem wakeups;
- Sea Power simulated objects;
- map records;
- visual proxies; and
- active tracks and weapons.

Benchmarks report campaign throughput and parity error before comparing cost.
They also report Sea Power fixed timestep, internal time
dilation, frame time, memory, tactical-entry latency, proxy latency, and result
commit latency. The comparison baseline is a smaller all-native Sea Power
scenario with matching force composition where possible.

## Active exclusions

The active architecture does not require:

- distributed Operational workers;
- hot Unity replicas;
- live tactical migration;
- PostgreSQL, Redis, brokers, containers, or cloud services;
- whole-theater ground combat in Sea Power; or
- public adversarial multiplayer.

The archived experiments remain available at Git tag
`archive/distributed-workers-before-local-trim-2026-08-24`. They do not block
the local player loop.
