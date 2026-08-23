# Federated simulation protocol

This document specifies how several clients can simulate the same campaign
entity while retaining one coherent outcome. It is the target distributed
architecture. The repository implements authority epochs, assignment roles,
hash-disagreement fencing, cold checkpoint reconstruction, and migration
control-plane primitives, but not qualified hot replica convergence or live
two-Sea-Power failover. See [Implemented architecture map](implementation-map.md).

Sea Power replicas are promotion eligible only when running a validated version
of the [Deterministic tactical runtime adapter](deterministic-runtime-adapter.md).
Default Unity execution may be used by a single trusted primary, but it cannot
support a hot-shadow claim merely because both clients received the same seed.

## Invariants

1. Every entity and interaction island has a stable campaign ID.
2. At most one primary may commit events for an assignment and authority epoch.
3. Every committed event is typed, ordered, idempotent, and attributable.
4. A replica starts from a known checkpoint and event cursor.
5. Promotion creates a new epoch; events from older epochs are fenced out.
6. A render proxy cannot be promoted because it lacks complete simulation state.
7. Losing a client may reduce fidelity, but must not stop the theater clock.
8. Load never changes the fixed logical step. The coordinator may change the
   whole synchronization domain's target wall-time tempo at an ordered tick.

These are stronger and more useful than trying to make every peer a writer.
The campaign service coordinates authority but need not run every detailed unit
or act as the sole tactical host.

## Assignment model

An assignment contains:

```text
AssignmentId
EntityOrIslandId
Role                 Primary | Shadow | Observer | Proxy
AuthorityEpoch
EffectiveTick
LeaseExpiresAt
CheckpointId
EventCursor
SimulationVersion
GameBuild
CatalogFingerprint
RequiredFidelity
InformationScope
SynchronizationDomainId
FixedStepDuration
TempoPolicyVersion
```

`InformationScope` is essential: an observer receives only contacts and hidden
state its player is entitled to know. A promotion candidate may keep encrypted
or service-held secret state separate from the player's render-facing view.

The current local POC does not persist `RenderProxy` as an assignment. It uses a
separate, presentation-only Runtime Bridge registry sourced from the trusted
campaign view, which is stricter than giving a proxy an authority epoch or
checkpoint lifecycle. That local path is documented in
[Implemented architecture map](implementation-map.md); a remote implementation
must replace its privileged diagnostic read with a coalition-scoped projection.

Assignments are leases, not permanent ownership. Clients renew them with a
heartbeat containing simulation progress and health. A primary stops publishing
when its lease expires even if its local simulation can continue visually.

### Assignment state machine

```text
Requested -> Preparing -> Ready -> Active -> Quiescing -> Fenced -> Released
                 |          |        |           |          |
                 +----------+--------+-----------+----------+-> Failed
```

- **Requested:** the scheduler has selected roles but no client owns work.
- **Preparing:** compatible clients fetch the checkpoint/catalog and materialize.
- **Ready:** a replica has acknowledged stable-ID mapping, cursor, hash, and role.
- **Active:** the service has committed the effective tick and, for a primary,
  the lease/epoch from which event publication is allowed.
- **Quiescing:** the service has named a final tick and accepts no later inputs.
- **Fenced:** the role can no longer publish, even if the client remains alive.
- **Released:** portable state is committed and owned Unity objects may be removed.
- **Failed:** preparation or execution failed; cleanup and source-engine recovery
  are required before reassignment.

Only a service commit changes these states. A companion or plugin acknowledgement
is evidence for a transition, not the transition itself.

Replica convergence health has a separate state machine:

```text
Synchronizing -> Healthy -> Lagging -> Diverged
                     |          |          |
                     +----------+----------+-> Unavailable
```

Only `Healthy` full replicas are normally promotion eligible. A `Lagging`
replica may recover by event catch-up. A `Diverged` replica requires diagnosis
and correction/checkpoint reload before it can become healthy again.

This convergence state is independent of client load state and simulation-source
eligibility. A convergent replica on an overloaded client may still be drained;
a player's client can become `Disabled` as a simulation source while remaining
connected for commands and filtered presentation. Those states and transitions
are defined in [Load management](load-management.md).

## Observer accuracy and hidden state

When a player observes a unit, their Sea Power client may simulate it locally
to obtain smoother movement, sensor presentation, animation, and short-horizon
prediction than network snapshots alone provide. That replica is still not an
additional writer.

There are two observer forms:

1. **Filtered observer:** receives only player-authorized state. It can improve
   presentation but cannot be promoted when missing hidden damage, orders,
   contacts, weapons, or AI state would change outcomes.
2. **Full-state observer:** receives the same canonical checkpoint and ordered
   inputs as a shadow, while the local UI receives a separately filtered
   projection. It may be promotion eligible in a trusted deployment.

Keeping full hidden state in the same untrusted client process creates a cheat
risk even if the UI does not expose it. Public deployments should use a trusted
worker, server-side operational fallback, or accept that such an observer cannot
be a hot failover candidate. This is a product trust decision, not something
encryption inside the same game process can solve.

Observer-generated results are advisory discrepancy or outcome proposals. They
can trigger a primary recheck, subsystem-hash comparison, or scheduler decision,
but cannot directly change committed campaign state. If two simulations differ,
the service follows reconciliation policy; it does not choose whichever answer
is more favorable to the observing player.

## Replica reports

At a configured canonical tick, full replicas report:

- assignment and authority epoch;
- last applied command/event cursor;
- simulated tick and wall-clock timestamp;
- canonical state hash and checkpoint ID;
- process/game/plugin/protocol/catalog versions;
- deterministic runtime mode, hook manifest, logical tick, fixed step, and RNG
  manifest versions;
- observed tick throughput and lag, frame-time/render rate, queue and durable
  spool depth, CPU/GPU/memory pressure, checkpoint/apply cost, and round-trip
  latency;
- readiness to accept promotion;
- recent errors and whether required hidden state is complete.

Hashing uses a canonical, quantized representation: stable field order, fixed
units, explicit rounding, and no transient Unity instance IDs. Hashes are useful
for detecting divergence, not for proving which two-replica result is correct.
With only two disagreeing replicas, the coordinator must resync or use an
independent rule; majority voting requires at least three independent replicas.

## Event publication

The primary turns tactical outcomes into proposed events such as:

- order accepted/rejected;
- movement checkpoint;
- sensor observation or contact update;
- weapon launched, impacted, missed, or terminated;
- damage/status/store delta;
- unit destroyed, abandoned, landed, recovered, or withdrawn;
- interaction-island merge or split request.

Each proposal carries the assignment ID, authority epoch, source tick, causal
command/event cursor, event ID, and expected entity revision. The service
rejects stale epochs, duplicate IDs, invalid revisions, impossible information,
and events outside the assignment's capability. Accepted events enter the
durable ordered log and are broadcast to relevant replicas.

Shadows and observers do not publish authoritative outcomes. They report hashes,
checkpoints, health, and optionally typed discrepancy or adjudication proposals.

## Health and failover

A primary is unhealthy when its lease or heartbeat expires, it falls too far
behind campaign time, its pipe/game process disconnects, its version changes,
or repeated hashes violate tolerance. Promotion should normally rank candidates
by:

1. exact protocol, simulation, game-build, and catalog compatibility;
2. validated deterministic runtime/hook/RNG manifest compatibility;
3. completeness of required hidden state;
4. position within the allowed tick/event lag;
5. agreement with the last committed canonical checkpoint hash;
6. freshest checkpoint and event cursor;
7. stable tick throughput, low queue pressure, and network latency;
8. player-observation locality and available compute.

Heartbeat interval, expiry grace, maximum tick lag, checkpoint staleness, and
hash tolerance are versioned campaign policy. Their initial values must come
from the Phase 1/P6 measurements in [Implementation plan](../planning/implementation-plan.md),
not arbitrary networking folklore. Independently of those values, lease expiry
is evaluated using service time and event acceptance always checks the current
durable epoch.

Promotion sequence:

1. Stop accepting events from the old epoch and mark its lease fenced.
2. Select a promotion-eligible shadow or full-state observer.
3. Bring it to the committed event cursor, resyncing from a checkpoint if needed.
4. Issue a new lease with `AuthorityEpoch + 1` and an effective tick.
5. Allow the new primary to publish only from that effective boundary.
6. Rebase other replicas and downgrade or disconnect the former primary if it
   later returns.

The authority change and effective tick are one durable transaction with the
epoch fence. Conceptually:

```text
begin transaction
  require current epoch == E
  fence assignment at committed cursor C
  create replacement lease at epoch E + 1, effective tick T
  append AuthorityPromoted event
commit
```

An event proposal is accepted only if its assignment, epoch, lease, source tick,
causal cursor, and expected entity revision are valid in the same transaction
that appends it. Checking first and appending later would leave a promotion race.

If no eligible full replica exists, the service keeps advancing the entity with
its operational resolver, freezes only that interaction island for a bounded
period, or restores from the latest checkpoint. The rest of the theater does
not stop. A lost render proxy has no simulation consequence.

Keeping a warm shadow for important islands makes promotion a cursor catch-up
rather than a cold Unity spawn. An observing player is a good shadow candidate
because the unit is already materialized locally, but only when the client also
has the full authoritative state required for simulation.

## Divergence and resynchronization

Expected Unity drift is handled in escalating steps:

1. Compare quantized hashes at checkpoint ticks.
2. Request subsystem hashes to localize the difference.
3. Replay missing ordered events when cursors differ.
4. Apply a correction delta for bounded, presentation-safe drift.
5. Reload the authoritative portable checkpoint for material divergence.
6. Demote a repeatedly divergent client from promotion eligibility.

Correction should be state-aware. Teleporting a ship or missile can invalidate
physics and guidance; some differences require rematerializing the entire
interaction island at a safe tick. Clients can interpolate corrections visually
while canonical state changes at a deterministic boundary.

## Distribution and load balancing

The scheduler assigns work based on interaction, locality, fidelity, health,
and spare capacity—not simply one ship per client. It may:

- give a player's client primary authority for nearby friendly units;
- add another client's full shadow for important or heavily observed units;
- co-locate sensors, targets, weapons, and platforms in one interaction island;
- retain overlapping simulation briefly while transferring an island;
- split an island after interactions end and checkpoints agree;
- fall back to the external operational resolver when no suitable client exists.

This permits many players and avoids a single detailed host bottleneck while
preserving one committed timeline. Some redundant work is intentional: it buys
smooth observation, divergence evidence, and rapid failover.

The scheduler moves complete interaction islands rather than arbitrary
individual units. Under sustained pressure it reduces optional presentation and
replicas, marks the source `DrainOnly`, migrates or demotes suitable islands, and
only then reduces global target tempo. Destination reserve, bounded concurrent
migrations, affinity, hysteresis, and cooldown prevent cascading rebalance. The
full policy and drain protocol are in
[Load management and simulation-source control](load-management.md).

## Coordinator availability

Distributed tactical authority does not automatically make the coordinator
highly available. The durable event log, campaign clock, and lease allocator
must themselves be replicated or recoverable if seamless coordinator failover
is required. A practical progression is:

1. one durable coordinator with restart/replay;
2. a release-tested SQLite/local-file coordinator for ordinary local, LAN, and
   private dedicated campaigns;
3. optional replicated storage and stateless coordinator instances when measured
   availability requirements justify the added infrastructure;
4. leader election or a consensus-backed lease/epoch allocator before more than
   one coordinator may write.

This centralizes ordering and persistence, not expensive tactical simulation.
It also prevents split-brain primaries from both committing outcomes.

A player's Sea Power process is therefore not the campaign host. Losing it costs
only the roles assigned to that process and can be covered by shadows or
operational fallback. If the coordinator/store itself is unavailable, clients
may continue bounded visual prediction and buffer commands, but no new outcome
is committed until ordering/fencing authority returns. Allowing peers to commit
during that interval would recreate split brain.

## Protocol evolution

The current local protocol uses bounded length-prefixed JSON and an explicit
session handshake. Preserve its envelope with protocol version, message kind,
unique ID, correlation ID, session ID, deadline, priority, message type, and
payload. Expand capability negotiation before mutation so new services can work
safely with older plugins.

For remote multiplayer, use authenticated ASP.NET Core gRPC with versioned
Protobuf schemas for companion/service/worker traffic. Generated clients and
services keep both RPC directions typed. Separate bounded control, event, and
telemetry streams where their ordering requirements differ. Checkpoints and
catalogs use authenticated, content-addressed HTTP transfers so bulk bytes do
not starve lease/fence traffic.

Use SignalR with MessagePack only for player-facing realtime UI projections and
notifications. SignalR reconnect buffering and gRPC stream restart are transport
conveniences, not campaign durability: every reconnect presents its session,
assignment epoch, and committed cursor, then catches up from the journal or a
verified checkpoint. Raw WebSockets, more streams/connections, compression,
delta encoding, and UDP/QUIC remain benchmark-driven optimizations.

## Required tests

- two replicas replay the same checkpoint/events to the same canonical hash;
- duplicate and out-of-order events are rejected or applied idempotently;
- the old primary cannot publish after a higher epoch is issued;
- killing a primary promotes a warm shadow without duplicate weapon/damage events;
- a stale shadow is caught up or rejected rather than silently promoted;
- a render proxy is never eligible for promotion;
- an observer receives no unauthorized hidden state;
- partitioned clients reconnect and rebase without rewinding committed time;
- losing every tactical client falls back without stopping unrelated regions;
- game/catalog/protocol version mismatch blocks incompatible assignment.
- CPU/GPU/network/queue stress does not stretch or skip a logical step;
- a global tempo change is applied by every replica at the same effective tick;
- an overloaded client can remain connected while receiving no simulation role;
- destination failure during migration never creates two valid publishers;
- repeated overload and false health reports cannot cause an unbounded migration
  storm or bypass fencing.
