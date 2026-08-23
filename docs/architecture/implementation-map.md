# Implemented architecture map

## Purpose and status

This document maps the code that exists in the repository as of 23 August 2026.
It complements [System architecture](system-architecture.md), which describes
the normative target. A target component or transport must not be inferred to
exist merely because it appears in a design diagram.

The canonical durable boundary is the campaign journal. Sea Power, the
companion, the operator monitor, and Operational workers are clients of that
boundary; none may allocate campaign time, authority epochs, or committed
history independently.

## Runtime topology

```text
                                      HTTP operator monitor
                                      (2 second polling)
                                                |
                                                v
+--------------------+   gRPC stream   +--------------------------+
| Operational worker |<-------------->| Campaign ServiceHost     |
| placement/health   |                 | coordinator + scheduler  |
| (ticks not here)   |                 | + in-process simulation  |
+--------------------+                 +------------+-------------+
                                                   |
                                     SQLite WAL + content-addressed blobs
                                                   |
                                            durable campaign head
                                                   ^
                                                   |
+-------------------+   named pipe   +-------------+------------+
| Sea Power +       |<-------------->| Companion                |
| Runtime Bridge    |                | gRPC campaign client +   |
| Unity main thread |                | authenticated local HTTP |
+-------------------+                +--------------------------+
```

The browser monitor is currently an operator diagnostic application. It uses
loopback same-origin HTTP snapshots and polling, not SignalR. Loading the hosted
monitor issues a read cookie without establishing an operator identity; writes
require the bearer token and therefore work through the Vite development proxy,
but not from the hosted production monitor. The player-session and
coalition-projection HTTP APIs exist, but there is no complete player command UI
or realtime player client.

## Project ownership

| Project | Implemented responsibility | Important boundary |
| --- | --- | --- |
| `SeaPower.RuntimeBridge` | AnchorChain-loaded Unity adapter; capability/world extraction; typed UI automation; controlled surface-unit mutation; checkpoints; collider capture; experimental tick/save probes | Runs on Unity's main thread and contains no campaign persistence or remote credentials |
| `CrimeanTom.Contracts` | Bounded local pipe envelopes and payloads shared by Mono and .NET 8 | No Sea Power types or campaign policy |
| `CrimeanTom.LoadAcceleration` | Installed-data lookup, persistent file hashes, and versioned collider geometry cache | Portable .NET Standard code shared by bridge, companion, and campaign engine |
| `CrimeanTom.Companion` | Current-user named-pipe broker, authenticated loopback HTTP, durable outbound spool, campaign gRPC client, checkpoint/content transfer, port-operations projection | Never owns canonical campaign truth; currently holds the shared campaign-service bearer token |
| `CrimeanTom.Campaign.Contracts` | Stable IDs, quantized values, commands, events, fidelity and authority types | No Unity or persistence implementation |
| `CrimeanTom.Campaign.Domain` | Immutable canonical state, invariants, coalition projections | Pure deterministic state model |
| `CrimeanTom.Campaign.Engine` | Command reduction, deterministic simulation, canonical serialization, operational combat, optional Bepu collider-assisted terminal resolution | Does not write storage or call Sea Power |
| `CrimeanTom.Campaign.Service` | SQLite WAL journal, writer epoch, snapshot primitives, content-addressed blobs, integrity verification, backup and side-directory restore | Sole durable history implementation; ServiceHost snapshots every 100 commits but has no recovered-state cache |
| `CrimeanTom.Campaign.Transport` | Generated bidirectional gRPC/Protobuf companion and worker protocol | Machine/control transport only; not a player UI protocol |
| `CrimeanTom.Campaign.ServiceHost` | ASP.NET Core host, coordinator, scheduler, migration fences, sessions, HTTP control/projection APIs, worker supervisor, unattended in-process simulation | Sole allocator of campaign time, authority, and commits |
| `CrimeanTom.Campaign.OperationalWorker` | Campaign-spawned process identity, health, capacity, ordered stream participation, assignment fencing | Does not execute `CampaignSimulation`; receives the shared service token and its health/capacity are self-reported |
| `CrimeanTom.CallsiteInventory` | Pinned Sea Power deterministic-hook/call-site inspection | Development diagnostic only |
| `CrimeanTom.Campaign.PersistenceProbe` | Abrupt-exit and writer-lock persistence fault probe | Development/qualification tool only |
| `apps/campaign-monitor` | Babylon/TypeScript operator map, hierarchy, telemetry, scheduler and outcome display, Fulda seed convenience | Read-mostly operator surface; not the multiplayer client |

The broad integration suites are intentionally cross-component:
`CrimeanTom.Campaign.IntegrationTests` exercises the deterministic campaign
story and `CrimeanTom.Protocol.Tests` exercises the real host, companion,
persistence, worker, and bridge-facing contracts.

## Implemented data flows

### Campaign command and persistence

Authenticated control HTTP reaches `CampaignCoordinator`, which validates a
typed `CampaignCommand`, reduces it through `CampaignKernel`, and appends the
command/events under a compare-and-swap writer epoch in `SqliteCampaignJournal`.
Snapshot APIs accelerate recovery but do not replace the ordered event head.
ServiceHost saves a canonical snapshot every 100 commits, bounding the ordinary
replay tail while retaining the append-only journal. The coordinator still has
no recovered-head cache, and every append performs another recovery inside its
transaction before accepting the canonical event. Long-journal throughput has
not been qualified.

### Unattended Operational simulation

`CampaignUnattendedSimulationHostedService` advances campaigns without Sea
Power. `CampaignSimulation` resolves movement, sustainment, intelligence,
weather effects, doctrine, sensors, in-flight ordnance, countermeasures, point
defense, damage, and withdrawal. The service process still executes all ticks,
including islands placed on Track A Operational workers.

Optional collider-assisted terminal resolution loads schema-v2, root-oriented
meter geometry from `ColliderGeometryCache` and raycasts primitive/bounds
silhouettes with BepuPhysics. It is an analogue hit silhouette, not Unity PhysX,
Blastzone armor, or penetration simulation. Without an enabled valid catalog,
the existing deterministic probabilistic path remains authoritative.

### Tactical materialization and return

The coordinator assigns and fences complete interaction islands. The companion
translates assignment/checkpoint frames to allow-listed Runtime Bridge commands.
Destroyed and retired entities remain in canonical history but are excluded
from scheduler island construction, so later interest-policy changes cannot
attempt invalid fidelity transitions or place terminal entities with workers.
The implemented portable mutation family is owned surface units. Preparation,
staging, activation grants, quiesce/release, and late-epoch rejection exist;
live two-Sea-Power migration is not qualified. No evidence yet demonstrates a
tactical loss against a campaign-owned hostile being committed back into the
journal.

The companion heartbeat now renews a process-local tactical ownership lease.
While that lease is live, unattended campaign time continues and the kernel
advances unrelated entities, but the active tactical entity remains fenced from
the analogue and is not reclaimed by the fixed campaign-time deadline. After
the heartbeat becomes stale, the existing 120-quanta bound remains the cold
Operational fallback.

For the owned-surface POC, the companion captures a lightweight checkpoint
readback every two seconds and publishes a durable `StateSync` proposal only
when its stable state fingerprint changes. The coordinator commits observed
kinematics, telegraph, fuel/stores fractions, damage, and destruction at the
current campaign head. Checkpoint capture is read-only and is allowed while Sea
Power is unpaused; ordered lifecycle mutations temporarily pause and restore
play. This is not complete portable tactical state: magazines, sensors,
formations, hosted aircraft, and broader entity families remain fenced or
unsupported.

The same local POC now projects other deployed friendly maritime entities into
the player's Sea Power instance as presentation-only surface render proxies.
The companion derives the coalition from its own current or prior
Primary/Shadow assignments, reads the current trusted campaign view every two
seconds, filters to supported materialization profiles, and excludes entities
with active tactical authority on that instance. The preferred bridge provider
uses only Sea Power's base-object presentation loader: it creates the vessel
mesh hierarchy before a `Vessel`, systems, sensors, physics, unique game ID, or
`ObjectsManager` registration exists. A separate map marker and label are added
to Sea Power's drawing layer. The presentation record has no campaign authority,
checkpoint, native simulation object, collisions, or velocity and is moved only
from later campaign snapshots.

The earlier fenced native-vessel representation remains as a sticky per-process
fallback if presentation-only loading fails on a compatible game build. The
`GET /game/render-proxies` diagnostic reports the selected provider and, per
entity, whether a native object exists and whether its world model and map
annotation are visible. Promotion hides the matching proxy before owned
materialization. Demotion or reconnect containment suspends the retained native
record and permits the model/map proxy to represent campaign truth again, so a
disabled native record does not make the friendly disappear.

Typed `focus-camera-on-unit` resolves either a unique live native Sea Power
object or a unique visible presentation-only proxy. Proxy focus attaches
`RenderPosition` to the proxy model root and returns the distinct postcondition
`camera-focused-on-visual-render-proxy`; the pinned camera seam fingerprints the
pivot switch and pivot readback methods before enabling the command.

This is a trusted local projection path, not the final multiplayer transport.
The companion currently derives it from the privileged diagnostic campaign
view because it holds the shared service token. It includes only deployed
friendlies in the Maritime domain whose exact unit/loadout has a Runtime Bridge
profile; the current verified profile is the Knox-class surface vessel. It does
not project hostile contacts, uncertain tracks, aircraft, submarines, hosted
children, or arbitrary catalog types. Stale presentation records are hidden
rather than treated as recoverable canonical state. If a fence was missed
across reconnect, the companion compares owned records with the current
campaign assignment projection, briefly pauses the tactical mission, and
suspends any locally active record whose assignment is no longer authoritative
before restoring the prior play state.

### Operational worker supervision

The service spawns Track A workers with additive per-spawn credentials and
validates their process identity, while also giving them the shared master
service token. Workers self-report health/capacity and accept Operational
placement. They are control-plane targets only: immutable work leases,
worker-computed results, coordinator validation, and result commit are not yet
implemented.

### Player and operator surfaces

The operator monitor receives true diagnostic state over loopback same-origin
HTTP and polls every two seconds. The hosted read cookie is a local convenience,
not an authenticated operator principal, and the hosted UI cannot currently
authorize writes. Coalition-filtered projections and reconnectable in-memory
player-session endpoints are implemented. Player command envelopes currently
accept only `acknowledge`, `observe`, and `transfer-control`; they are bounded
in-memory presentation records and do not issue canonical campaign orders.

## Deliberate target/implementation differences

| Target architecture | Current implementation |
| --- | --- |
| SignalR/MessagePack realtime player UI | No SignalR dependency or hub; HTTP polling only |
| Campaign-owned distributed Operational execution | Worker spawn/placement exists; simulation remains in ServiceHost |
| Multi-family tactical materialization | Portable owned-surface-unit family plus visual-only friendly Knox model/map proxies, with a fenced native fallback; other families are absent |
| Complete portable tactical state | Damage, fuel, magazine, sensor, formation, and hosted-aircraft application remain explicitly unsupported/fenced |
| Hot Shadow convergence and failover | Cold checkpoint/Operational fallback only |
| Packaged player application | Developer-run .NET/TypeScript projects; no clean-machine installer/updater |
| Versioned campaign content/catalog pipeline | Installed-data readers and hard-coded Fulda seed; no authoring/package pipeline |
| Public/hostile multiplayer | Trusted private deployment with coalition-filtered projections |

See [Current code and product status](../planning/current-status.md) for the
feature gap order and [Supported claims](../planning/supported-claims.md) for the
narrow behavior the application currently advertises.
