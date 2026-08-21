# Dynamic campaign architecture

## Goal

Run one continuously advancing theater without requiring one Sea Power process
to own the entire battle. The external campaign service is the durable source
of truth. Connected Sea Power clients are elastic high-fidelity workers and
live player views: the service streams the entities each client needs, receives
orders and tactical events, and can move simulation responsibility between
clients without ending the campaign.

This supersedes the earlier design in which the campaign generated an isolated
mission for every important engagement. Importing or generating a conventional
scenario remains useful as a compatibility and recovery path, not the normal
runtime boundary.

## System boundary

```text
                         durable event log + snapshots
                                      |
                                      v
                         campaign coordinator/service
                        /             |              \
              assignments       strategic sim       catalog
                     /                |                 \
                    v                 v                  v
             local companion <---- remote protocol ---- workers
                    |
              local named pipe
                    |
                    v
       thin Sea Power plugin -> Unity main-thread adapter -> tactical world
```

The standalone .NET service owns campaign time, persistence, strategic rules,
assignment leases, authority epochs, and committed events. It can run abstract
entities much faster and more cheaply than Unity. The in-game DLL should contain
only protocol handling, stable-ID mapping, state extraction, and carefully
bounded Unity commands. Networking, persistence, scheduling, reconciliation,
and most validation belong in the companion or service.

## Fulda Gap proving campaign

The representative Phase 5 campaign is **Fulda Gap Gone Hot**, a deterministic
1989 Central Front and North Atlantic reinforcement crisis. It is intentionally
one continuous campaign rather than a sequence of generated scenarios. The
initial graph spans the United States East Coast, Atlantic convoy route, GIUK
Gap, Norwegian and Barents Seas, North Sea, German Bight, BALTAP and Baltic,
NORTHAG, and the Fulda corridor. Its opening forces include NATO and Warsaw Pact
surface groups, submarines, convoy and replenishment ships, coastal missile
forces, and two army-level land formations.

Sea Power is authoritative only where it is a credible tactical resolver. Naval
and air interactions around convoy protection, reinforcement, sea control, and
the northern flank may promote to Tactical fidelity. Central Front land combat
is campaign-service abstraction: those formations have `LandAbstract` simulation
domain and a hard `Operational` maximum-fidelity ceiling. They may affect ports,
reinforcement demand, objectives, readiness, and the naval campaign, but they
cannot be materialized into Sea Power or masquerade as a tactically simulated
land battle. The monitor must display this boundary explicitly.

`POST /control/campaigns/{campaignId}/bootstrap/fulda-gap` creates the fixed
12-region, 15-entity opening state and returns its committed head and canonical
hash. The operation is authenticated, refuses to overwrite a non-empty campaign,
and uses stable campaign-derived IDs so the same campaign ID and content version
reproduce the same world.

### Operator monitor

The campaign service hosts a Tailwind CSS and Babylon.js monitor at `/monitor/`.
It renders the region graph, bases, unit domains and fidelity, task-force
membership, active operational outcomes, damage, and service/head status. The
same information is also represented as semantic HTML unit and outcome lists so
the 3D canvas is not the only way to inspect the campaign.

The visual language is a dense Cold War command plot, not a general-purpose
dashboard. A dominant black wireframe plotting volume shows the theater graph,
region nodes, bases, operational effects, and domain-specific wireframe unit
glyphs. Square black-and-white registers frame the map; friendly green, hostile
red, warning amber, and a white selection state are the only semantic accents.
The shell avoids gradients, blur, rounded cards, and decorative chrome so every
line, label, and highlight carries operator information.

The static shell contains no campaign data and may load unauthenticated on the
loopback listener. When the host serves its entry document it issues a random,
HttpOnly, `SameSite=Strict` session cookie valid only for that service process.
The browser therefore authenticates health, diagnostics, bootstrap, and control
requests without exposing or asking the operator to paste the service bearer
token. CLI and companion clients continue to use the current-user-only token
file. The monitor credential is neither available to JavaScript nor persisted
across browser/service sessions, and cross-site requests do not receive it.

Development uses `pnpm campaign:dev`: `dotnet watch` restarts the service while
Vite supplies frontend HMR at port 5173. Its loopback proxy reads the protected
service token on the developer's behalf and adds it only to proxied API calls.
The backend watcher excludes generated static content; Vite owns that tree, so
a production monitor build cannot overflow the backend file watcher.

The current scheduler projection also shows health and simulation-source
eligibility, backlog and tactical capacity, fixed-step target tempo, load action,
interaction-island members, coupling score, current/desired fidelity, and the
reason for each choice. `POST /control/campaigns/{campaignId}/schedule` accepts
the current observed/controlled entity set and client load envelope, then commits
required fidelity transitions through the campaign journal before returning the
applied diagnostic view.

At companion handshake the game advertises the exact player task-force ID and
the bounded unit/variant/loadout profiles its runtime adapter can materialize.
The scheduler requires one connected worker, a player task force, and support
for every entity in an interaction island before it can request Tactical
fidelity. An offline worker, missing task force, or partially unsupported island
stays Operational with an explicit diagnostic reason.

For the first executable scheduler-to-game slice, a representable singleton
island is durably reserved and queued to the companion directly by the schedule
operation. A ready checkpoint then performs the existing atomic
assignment/entity activation. Multi-entity islands currently report
`atomic-island-activation-unavailable` and remain Operational; they will not be
partially promoted while batch-ready state and one-event island activation are
still being implemented.

The local named pipe is a trust and threading boundary. Pipe I/O happens away
from Unity; every game read or mutation is marshalled onto Unity's main thread.
The companion exposes a versioned local API today and can later connect to the
remote campaign transport.

## Native maintenance UI as a projection

Crimean Tom may reuse Sea Power's Task Force Mode screens for bounded port
administration: fleet composition, refit/loadout choices, repairs, rearming,
and reinforcements. This is a projection and draft editor over external campaign
state, not a campaign authority or tactical resolver.

The service snapshots a durable campaign revision and materializes only the
eligible docked force, local pools, budgets, and policy into a disposable
repository-owned campaign shell. Because Sea Power loads authored campaign and
roster data at campaign startup, changing that projection may require a
controlled game restart. The restart is an adapter lifecycle operation; it does
not restart theater time, entity identity, or the external campaign journal.

The bridge reads the player's proposed maintenance draft through typed,
version-pinned view-model seams. The companion converts it to portable commands,
and the service validates costs, capacity, location, availability, expected
revision, and idempotency before committing one transaction. Rejection leaves
the authoritative state unchanged and causes the projection to be refreshed.
Native saves may support the UI session, but they are never the durable source
of truth.

Any authored mission attached to this shell is UI context only. Its Play,
completion, objective, and after-action paths are outside the authority boundary
and cannot produce campaign outcomes. The rest of the theater continues at
Strategic or Operational fidelity while a player edits this administrative
draft; only that player's conflicting maintenance transaction is fenced.

## One world, several fidelity levels

Fidelity is selected per entity or interaction island and may change while the
campaign continues:

1. **Strategic** — the service advances routes, logistics, readiness, broad
   detection, weather, and scheduled operations without a game client.
2. **Operational** — a deterministic resolver advances unattended combat at a
   richer level but still without Unity.
3. **Tactical** — one or more Sea Power clients simulate the same unit or
   tightly interacting group at full Sea Power fidelity.

A **Render proxy** is a replica role, not a fourth canonical fidelity level. It
materializes only authorized presentation state and cannot adjudicate outcomes
or take authority.

The service can pipe a strategic entity into a client when a player approaches,
observes it, controls it, or when an interaction requires better adjudication.
It can dematerialize the entity after producing a committed checkpoint and
return it to a cheaper fidelity level. The theater clock and identity never
restart during that transition.

Dense interactions should be assigned as an **interaction island**—usually a
task force, aircraft package, missile engagement, or sensor/weapon bubble.
Partitioning ships that exchange tightly coupled sensor and weapon state across
different authorities would create more synchronization cost than it saves.

The implemented P5 slice uses a conservative first coupling graph: a task force
is indivisible, ungrouped entities are singleton islands, and opposing groups in
a region with recent expenditure, damage, or destruction outcomes merge for a
20-quanta interaction fence. Active tactical assignments and recently engaged
tactical islands cannot be demoted by ordinary load pressure. This does not yet
claim safe weapon/guidance, hosted-aircraft, or physical-dependency splitting;
those boundaries remain fenced until live adapter evidence extends the graph.

## Multiple replicas of the same unit

A unit may deliberately exist in several clients. Each replica has an explicit
role:

| Role | Simulates | May publish authoritative events? | Promotion eligible? |
| --- | --- | --- | --- |
| Primary | Full assigned state | Yes, for its current authority epoch | Already authoritative |
| Shadow | Full state under the validated deterministic runtime adapter | No; publishes hashes and health | Yes, after adapter validation |
| Observer | High fidelity needed by a nearby player | No by default | Only if it holds all required hidden state |
| Render proxy | Presentation state only | No | No |

There is exactly one primary event publisher for an entity or interaction
island in an authority epoch. Redundant simulation improves accuracy and
availability, but does not allow two clients to commit competing damage,
detection, or movement results.

An observer can run the same unit at higher local accuracy so its player sees
smooth, timely behavior. Its results are advisory until it is promoted or the
primary explicitly accepts a typed proposal. Fog-of-war data must be filtered
before reaching an observer; replication is not permission to disclose hidden
enemy state.

See [Federated simulation protocol](federated-simulation.md) for leases,
health checks, promotion, reconciliation, and failure behavior.

## Strategic domain

The pure campaign core owns:

- campaign clock and deterministic event queue;
- theater regions, routes, terrain, and movement constraints;
- nations, coalitions, relations, and rules of engagement;
- bases, ports, airfields, facilities, ownership, capacity, and damage;
- stable unit-instance IDs, task forces, squadrons, and detachments;
- readiness, repair, fuel, ammunition, spares, logistics, and replacements;
- orders, patrols, escorts, convoys, objectives, and AI intent;
- intelligence estimates with uncertainty and last-seen provenance;
- weather and sea state at the resolution required by each worker;
- replica assignments, leases, epochs, checkpoints, health, and event cursors;
- the committed command/event history and campaign outcome.

Unity-only transforms, colliders, animation state, transient view models, and
live guidance objects do not belong in the durable campaign schema. A tactical
checkpoint maps those details to a versioned portable state when possible.

## Operational resolution

The unattended resolver advances fixed ten-quanta activities in stable ID order
and derives every stochastic decision from campaign seed, logical time, subject,
and purpose. Detection depends on graph proximity, simulation domain, weather,
and combat observation. A missed observation ages the last estimate and expands
its uncertainty rather than snapping to omniscient truth. Coalition projections
replace the internal hostile entity key with a campaign-and-coalition-specific
opaque track ID and expose only estimated region, coordinate, confidence,
classification, age, and uncertainty radius.

Opposing operational entities in one region can expend munitions and inflict
readiness, damage, disablement, or destruction outcomes. A heavily damaged
member causes its complete task force—not an individual ship—to receive a
withdrawal order toward a home or safe adjacent region. The aggregate state and
typed operational outcomes are committed in the same simulation event, making
expenditure and losses replayable and visible to the operator monitor. These
rules are deterministic and functional; their numerical outcome envelopes are
not considered calibrated until the Phase 5 Sea Power observation batches are
recorded and disagreement is displayed.

## Determinism and persistence

Use deterministic time units, stable same-tick ordering, and keyed seeded RNG.
Random values should derive from stable keys such as campaign seed, tick,
entity, subsystem, and event ID. A global sequential RNG makes unrelated work
or a different shard schedule change later results.

A run is reproducible from:

- protocol, schema, simulation, and game/catalog versions;
- campaign seed and initial snapshot;
- ordered player/AI command log;
- committed nondeterministic tactical events;
- authority-epoch and assignment history.

Persist periodic snapshots plus an append-only event journal. Checkpoints make
loading and worker promotion fast; the journal enables replay, audit, migration,
and desync diagnosis. Every command and event has a stable ID and revision so
reducers can be idempotent.

Sea Power is nondeterministic by default: its timing, frame/update schedule,
several RNG sources, and spawn-dependent iteration can differ between clients.
Hot replicas therefore require the
[Deterministic tactical runtime adapter](deterministic-runtime-adapter.md), which
uses coordinator-issued ticks, fixed scheduling, ordered inputs, controlled RNG,
and stable materialization order. Physics may still diverge across hardware, so
replicas also exchange canonical hashes and restore/correct from checkpoints.
If the adapter cannot meet its acceptance gate, the design falls back to one
tactical primary plus operational or cold-checkpoint recovery.

## Stress and global tempo

Fixing the logical step means a slow game must never compensate by enlarging the
timestep. The coordinator first removes optional presentation/replica work,
stops assigning new islands to a pressured client, moves whole interaction
islands to healthy clients with reserve, or demotes suitable work to Operational
fidelity. A player can remain connected for commands and a filtered view even
when their game is disabled as a simulation source.

If placement and fidelity changes cannot preserve required tactical work, the
coordinator reduces one global `TargetTicksPerWallSecond` through an ordered
event. Every client continues using the same `FixedStepDuration`; the campaign
simply advances more slowly relative to wall time. See
[Load management and simulation-source control](load-management.md).

## Catalog adapter

The external simulation needs a normalized catalog of game IDs, variants,
loadouts, capabilities, service dates, speeds, broad sensor/weapon roles, and
conversion rules. Generate it locally from the installed game data through a
version-specific adapter and fingerprint it.

The pure core must not reference `ObjectBase` or proprietary game assemblies.
The plugin maps catalog and stable campaign IDs to the installed version's
runtime types. A game or mod update invalidates the catalog fingerprint and
blocks incompatible authority assignment until migrated or rebuilt.

## Target projects

```text
src/
  SeaPower.RuntimeBridge/       # generic thin AnchorChain/Unity adapter
  CrimeanTom.Contracts/          # dependency-free local wire contracts
  CrimeanTom.Companion/          # local pipe broker and control/remote client
  CrimeanTom.Campaign.Domain/    # pure durable state and rules
  CrimeanTom.Campaign.Engine/    # clock, events, systems, reconciliation
  CrimeanTom.Campaign.Contracts/ # network/save DTOs and schema versions
  CrimeanTom.Campaign.Catalog/   # installed-data normalization
  CrimeanTom.Campaign.Service/   # coordinator, persistence, assignments
  CrimeanTom.Campaign.Worker/    # optional headless strategic/operational worker
  CrimeanTom.Campaign.Tests/     # replay, determinism, property, failover tests
```

The first three projects now exist. Sea Power Runtime Bridge deliberately has no
campaign product name; Crimean Tom owns the contracts, companion, campaign, and
networking layers. Together they establish the local control boundary
before game mutations or remote multiplayer are enabled.

## Delivery plan

The authoritative phase definitions, dependencies, mandatory scenarios, exit
gates, deferred-decision deadlines, and adversarial review questions are in
[Implementation plan](../planning/implementation-plan.md). Its critical path is:

1. harden the current read-only control baseline;
2. prove catalog identity and canonical extraction;
3. prove bounded, reversible materialization for one entity family;
4. build the deterministic journaled campaign kernel;
5. complete a continuous single-client campaign-to-tactical round trip;
6. add operational simulation and dynamic fidelity;
7. add redundant replicas, divergence handling, and fenced failover;
8. add realtime multiplayer and information-filtered projections;
9. add durable scale/service availability;
10. complete security, compatibility, soak, and release qualification.

No phase proceeds merely because its code compiles. Each has a measured exit
gate. Unknown timing, checkpoint, scale, and fidelity constants have explicit
evidence requirements and decision deadlines rather than placeholder defaults.

See [System architecture](system-architecture.md) for the normative ownership,
time, protocol, persistence, security, and failure contracts.

## Principal risks

- Sea Power updates change internal types, data, or lifecycle behavior.
- Unity simulations diverge despite seeded RNG.
- Stable IDs or hidden state are lost across materialization.
- A stale primary publishes after a replacement takes authority.
- Cross-island weapons and sensors create hidden coupling.
- Replication leaks information a player should not know.
- A crash occurs after tactical change but before event commitment.
- A malicious or modified client submits plausible but false outcomes.

Versioned contracts, fenced epochs, keyed RNG, canonical checkpoints, typed
idempotent events, narrow mutation surfaces, and replayable logs address the
engineering risks. Competitive trust requires additional validation; redundant
clients alone do not create consensus or prevent cheating.
