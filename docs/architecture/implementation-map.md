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
authenticated HTTP snapshots and polling, not SignalR. The player-session and
coalition-projection HTTP APIs exist, but there is no complete player command UI
or realtime player client.

## Project ownership

| Project | Implemented responsibility | Important boundary |
| --- | --- | --- |
| `SeaPower.RuntimeBridge` | AnchorChain-loaded Unity adapter; capability/world extraction; typed UI automation; controlled surface-unit mutation; checkpoints; collider capture; experimental tick/save probes | Runs on Unity's main thread and contains no campaign persistence or remote credentials |
| `CrimeanTom.Contracts` | Bounded local pipe envelopes and payloads shared by Mono and .NET 8 | No Sea Power types or campaign policy |
| `CrimeanTom.LoadAcceleration` | Installed-data lookup, persistent file hashes, and versioned collider geometry cache | Portable .NET Standard code shared by bridge, companion, and campaign engine |
| `CrimeanTom.Companion` | Current-user named-pipe broker, authenticated loopback HTTP, durable outbound spool, campaign gRPC client, checkpoint/content transfer, port-operations projection | Never owns canonical campaign truth |
| `CrimeanTom.Campaign.Contracts` | Stable IDs, quantized values, commands, events, fidelity and authority types | No Unity or persistence implementation |
| `CrimeanTom.Campaign.Domain` | Immutable canonical state, invariants, coalition projections | Pure deterministic state model |
| `CrimeanTom.Campaign.Engine` | Command reduction, deterministic simulation, canonical serialization, operational combat, optional Bepu collider-assisted terminal resolution | Does not write storage or call Sea Power |
| `CrimeanTom.Campaign.Service` | SQLite WAL journal, writer epoch, snapshots, content-addressed blobs, integrity verification, backup and side-directory restore | Sole durable history implementation in the supported profile |
| `CrimeanTom.Campaign.Transport` | Generated bidirectional gRPC/Protobuf companion and worker protocol | Machine/control transport only; not a player UI protocol |
| `CrimeanTom.Campaign.ServiceHost` | ASP.NET Core host, coordinator, scheduler, migration fences, sessions, HTTP control/projection APIs, worker supervisor, unattended in-process simulation | Sole allocator of campaign time, authority, and commits |
| `CrimeanTom.Campaign.OperationalWorker` | Campaign-spawned process identity, health, capacity, ordered stream participation, assignment fencing | Does not yet execute `CampaignSimulation` work or commit results |
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
Snapshots accelerate recovery but do not replace the ordered event head.

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
The implemented portable mutation family is owned surface units. Preparation,
staging, activation grants, quiesce/release, and late-epoch rejection exist;
live two-Sea-Power migration is not qualified.

### Operational worker supervision

The service spawns Track A workers with per-spawn credentials and validates
their process identity. Workers advertise capacity and accept Operational
placement. They are control-plane targets only: immutable work leases,
worker-computed results, coordinator validation, and result commit are not yet
implemented.

### Player and operator surfaces

The operator monitor receives true diagnostic state over same-origin HTTP and
polls every two seconds. Coalition-filtered projections and reconnectable
player-session endpoints are implemented. Player command envelopes currently
accept only `acknowledge`, `observe`, and `transfer-control`; they are bounded
in-memory presentation records and do not issue canonical campaign orders.

## Deliberate target/implementation differences

| Target architecture | Current implementation |
| --- | --- |
| SignalR/MessagePack realtime player UI | No SignalR dependency or hub; HTTP polling only |
| Campaign-owned distributed Operational execution | Worker spawn/placement exists; simulation remains in ServiceHost |
| Multi-family tactical materialization | Portable owned-surface-unit family only |
| Complete portable tactical state | Damage, fuel, magazine, sensor, formation, and hosted-aircraft application remain explicitly unsupported/fenced |
| Hot Shadow convergence and failover | Cold checkpoint/Operational fallback only |
| Packaged player application | Developer-run .NET/TypeScript projects; no clean-machine installer/updater |
| Versioned campaign content/catalog pipeline | Installed-data readers and hard-coded Fulda seed; no authoring/package pipeline |
| Public/hostile multiplayer | Trusted private deployment with coalition-filtered projections |

See [Current code and product status](../planning/current-status.md) for the
feature gap order and [Supported claims](../planning/supported-claims.md) for the
narrow behavior the application currently advertises.
