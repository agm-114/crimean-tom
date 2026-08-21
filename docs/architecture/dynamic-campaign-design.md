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

The representative Phase 5 campaign is **Fulda Gap Gone Hot — Global War**, a
deterministic alternate-1989 conflict anchored on the Central Front but fought
across the world ocean. It is intentionally one continuous campaign rather than
a sequence of generated scenarios. The 32-region opening graph spans the US
East Coast and Atlantic reinforcement route; GIUK, Norway, the Barents, North
Sea, BALTAP and Fulda; the South Atlantic and Patagonia; the Mediterranean,
Levant, Red Sea, Gulf and Indian Ocean; and the South China Sea, Taiwan, Korea,
Japan, Okhotsk,
North Pacific, South Pacific and Australian approaches.

The 60-entity opening order of battle uses catalog IDs present in the installed
Sea Power 0.8.2 content for the United States, Soviet/Warsaw Pact, UK, Canada,
West Germany, France, Spain, Portugal, Norway, Israel, Iran, Libya, India,
Pakistan, China, Taiwan, North Korea, South Korea, Japan, Australia and Vietnam.
Argentina is an explicit
alternate-history Soviet logistics partner. Because the current catalog has no
Argentine nation prefix, its operational sealift uses available civilian and
Soviet cargo hulls and is labelled as a substitution. Nearby late-Cold-War
catalog units may be used where the game does not contain an exact September
1989 hull; these are scenario substitutions, not claims about historical
deployment.

Sea Power is authoritative only where it is a credible tactical resolver. Naval
and air interactions around convoy protection, reinforcement, sea control, and
the northern flank may promote to Tactical fidelity. Central Front land combat
is campaign-service abstraction: those formations have `LandAbstract` simulation
domain and a hard `Operational` maximum-fidelity ceiling. They may affect ports,
reinforcement demand, objectives, readiness, and the naval campaign, but they
cannot be materialized into Sea Power or masquerade as a tactically simulated
land battle. The monitor must display this boundary explicitly.

`POST /control/campaigns/{campaignId}/bootstrap/fulda-gap` creates the fixed
32-region, 60-entity opening state and returns its committed head and canonical
hash. The operation is authenticated, refuses to overwrite a non-empty campaign,
and uses stable campaign-derived IDs so the same campaign ID and content version
reproduce the same world.

The opening GIUK force keeps USS Pharris as a singleton surface picket using the
catalog-correct `usn_ff_knox_84 / Variant43 / Default` profile that the current
adapter can materialize safely. USS Honolulu is a separate singleton submarine
patrol until live detection, formation, weapon or physical relationships couple
it to another force. The shipped tactical-window mission represents that later
local event with Pharris and Honolulu against a Victor III and Oscar I. It does
not materialize the other 56 campaign entities into the Unity scene; they remain
under operational authority until their own interaction islands need tactical
resolution. Unsupported mixed groups continue to fail closed.

Operational firing envelopes are not theater-wide constants. At bootstrap the
service reads each unit's selected Sea Power loadout, magazines and hardpoints,
resolves date-based 1989 ammunition, and snapshots each weapon's native target
class and launch, projectile, or endurance range into canonical campaign state.
Operational combat can select only a compatible snapshotted weapon whose range
contains the rounded great-circle separation. Missing or unrecognized catalog
data fails closed. The only authored exception is the explicitly labelled
`abstract-corps-fires` envelope for campaign-only land formations, which cannot
be represented by Sea Power at Tactical fidelity.

The repository also ships **Crimean Tom - Tactical Worker Container**, a
non-resolving Sea Power mission shell. It has no campaign objectives, victory
conditions, hostile force, or local combat resolver. A stationary process-local
survey launch exists only to make Sea Power expose a valid player task force;
it has no campaign identity and never enters the service journal. The bridge
materializes fenced campaign interaction islands into this paused shell while
the external service continues to own time, objectives, force state, and durable
outcomes. This replaces engagement-specific scenarios as the intended worker
boundary; authored tactical-window missions remain useful demonstrations and
compatibility fixtures.

### Operator monitor

The campaign service hosts a Tailwind CSS and Babylon.js monitor at `/monitor/`.
It renders a Natural Earth 110m world coastline and latitude/longitude grid,
then overlays aligned national borders, the region graph, bases, unit domains
and fidelity, active
operational outcomes, damage, and service/head status in their real geographic
positions. The same information is represented as semantic HTML: the order of
battle is a collapsible coalition -> formation -> unit tree, and outcomes remain
an independently readable register, so the Babylon canvas is not the only way
to inspect the campaign. Singleton formations are flattened to coalition -> unit
rows with the formation retained as secondary context; only formations with
multiple members consume a separate expandable hierarchy level.

National borders are derived locally from the campaign order of battle and
colored friendly green or hostile red; neutral-country borders are not drawn.
The checked-in `world-atlas` geometry is modern rather than a literal 1989
political layer. The monitor therefore merges the fifteen modern successor
polygons into one USSR outline and treats unified Germany as the friendly FRG;
Peenemunde can identify the GDR in the OOB matcher, but modern geometry cannot
draw a separate East German border. This limitation is explicit rather than
silently presenting modern boundaries as exact Cold War history.

The visual language is a dense Cold War command plot, not a general-purpose
dashboard. A dominant black wireframe plotting volume shows the theater graph,
region nodes, bases, operational effects, and domain-specific wireframe unit
glyphs. Square black-and-white registers frame the map; friendly green, hostile
red, warning amber, and a white selection state are the only semantic accents.
The shell avoids gradients, blur, rounded cards, and decorative chrome so every
line, label, and highlight carries operator information.

`GET /campaigns` provides the monitor's campaign directory ordered by journal
head. The campaign-channel register can switch directly between those entries,
accept a pasted campaign ID, copy a stable `?campaign=<id>` deep link, or create
a fresh representative Fulda campaign. Campaign selection is therefore a host
relationship rather than a manually generated identifier workflow.

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
The backend watcher uses the SDK polling watcher and full process restarts on
Windows. Roslyn hot reload is deliberately disabled because Vite's generated
production chunks live under the service project and replacing those files can
crash Roslyn's hot-reload workspace. Normal frontend iteration remains on Vite
HMR; production monitor builds can update the hosted copy without killing the
combined watcher.

The two broad integration projects build into `.run/test-artifacts` through the
root `pnpm test` command. Their host/companion launch helpers resolve those
isolated binaries, so verification can run while the watched service keeps its
normal output assemblies loaded.

The current scheduler projection also shows health and simulation-source
eligibility, backlog and tactical capacity, fixed-step target tempo, load action,
interaction-island members, coupling score, current/desired fidelity, and the
reason for each choice. `POST /control/campaigns/{campaignId}/schedule` accepts
the current observed/controlled entity set and client load envelope, then commits
required fidelity transitions through the campaign journal before returning the
applied diagnostic view.

The same campaign diagnostic view includes process-local performance telemetry:
service uptime and memory, recovery/advance/schedule completion counts and
durations, completed work units, and last measured throughput. These metrics do
not enter canonical simulation decisions; they make the service-observed load
visible and provide the measurement surface for Phase 5 stress calibration.
`POST /control/campaigns/{campaignId}/bootstrap/fulda-gap/{forceMultiplier}`
replicates the representative force packages from 1x through 100x, while
`pnpm campaign:stress` creates and advances a fresh scaled campaign through the
real service/journal path.

At companion handshake the game advertises the exact player task-force ID and
the bounded unit/variant/loadout profiles its runtime adapter can materialize.
The companion refreshes that capability envelope in its campaign heartbeat, so
mission load, briefing, debrief, and scene changes update the service-side
worker snapshot without restarting the companion. The stable campaign/client
identity remains on the session while the game session, runtime and catalog
fingerprints, player task force, and supported profiles follow the live scene.
The scheduler requires a connected compatible worker, a player task force, and
support for every entity in an interaction island before it can request
Tactical fidelity. An offline worker, missing task force, or partially
unsupported island stays Operational with an explicit diagnostic reason.

Schema v6 allows multiple tactical-container sessions to coexist. Each new
assignment records its owner's stable client instance ID; service-to-worker
assignments and fences use that affinity, inbound assignment traffic from a
non-owner is fenced, and committed campaign events fan out to all sessions.
The operator projection lists every worker and its materialized, active,
retained, recovery-required, profile, and coupling counts. Deterministic
least-loaded selection is only the placement foundation. The durable kernel can
now commit one fenced Shadow-to-Primary migration for a complete island. The
coordinator can prepare that destination against migration-overlap reserve,
queue Shadow frames without taking over publishing, stage source final
checkpoints onto the still-suspended destination, and grant unsuspend only
after the durable island-migration commit. Cooldown, bounded concurrency, and
real two-container Sea Power verification remain before this is safe as an
automatic live rebalance. The companion reconstructs and suspends queued
Shadow objects before it reports them prepared, then applies
`stage-owned-surface-checkpoint` for the source final blob and unsuspends only
on a validated activation grant.

A representable interaction island is durably reserved and queued to the
companion directly by the schedule operation. Singleton assignments activate
through the assignment event. Multi-entity assignments enter durable `Prepared`
state after their individual first checkpoints; only one campaign-scoped event
can activate every assignment and entity in the batch. Operational simulation
remains frozen during preparation and no member can become partially Tactical.

The same schedule operation drains a complete active island under load or
source-ineligibility. The companion durably tracks each in-game mutation
revision after materialization and ordered updates. Scheduler authority fences
resolve those worker-owned revisions, capture final checkpoints, and suspend
the owned objects. Group members enter `Quiesced`; one campaign event then
releases all assignments and returns every entity to Operational together.
Manual fences continue to require an exact caller-supplied revision.

A later promotion may reuse a checkpointed suspended object instead of creating
a duplicate. The bridge accepts that transfer only for a different assignment
with a strictly higher authority epoch, a zero revision in the new assignment
namespace, an exact entity/profile/task-force match, and a healthy suspended
checkpoint. It rebinds and enables the retained vessel atomically, or restores
the prior suspension on failure.

When the selected worker does not retain that process-local object, the service
attaches the latest durable content-addressed checkpoint for the entity to the
new higher-epoch assignment. The owning companion downloads the blob through
the authenticated content endpoint, verifies its blob SHA-256, and passes the
decoded payload to the runtime bridge. The bridge independently verifies the
checkpoint's internal state hash, predecessor assignment and epoch, stable
entity/profile/task-force identity, and active surface state before constructing
a new owned object. The portable subset currently applied is position, heading,
speed, and telegraph. Fuel, stores, damage, sensors, formations, and hosted
aircraft remain explicitly unsupported; an island that depends on those fields
is not yet eligible for automatic cross-worker migration.

Each campaign heartbeat also carries a bounded tactical coupling projection.
The bridge captures active launched weapons plus exact native sensor target and
guidance ownership, home-base dependencies, runtime/temporary formations,
plotting-table tracks, and collision-avoidance dependencies on Unity's main
thread. Plot records collapse to stable source/target/sensor-kind edges instead
of leaking short-lived ECS entity IDs. The companion maps campaign-owned
endpoints back to canonical entity IDs and emits unsafe relationship edges; an
untargeted weapon produces a deliberate platform self-edge, because that entity
is still unsafe to demote while its weapon is in flight. The service retains an
edge across one missed heartbeat, unions hard edges into the
interaction-island graph, and fences an already Tactical island with
`active-coupling-fence` before ordinary interest or load shedding can drain it.
The monitor exposes the fence and its evidence reason.

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

The implemented P5 slice uses a conservative coupling graph: a task force is
indivisible, ungrouped entities begin as singleton islands, opposing groups in a
region with recent expenditure, damage, or destruction outcomes merge for a
20-quanta interaction fence, and live tactical hard edges union otherwise
independent groups. Active tactical assignments and recently engaged or coupled
tactical islands cannot be demoted by ordinary load pressure. Weapon and native
collision-avoidance edges have live campaign evidence; sensor, host, plotting,
formation, and temporary-refueling adapters are also captured but still require
broader mixed-profile live fixtures before claiming calibrated split thresholds.

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
