# Implementation map

## Purpose

This document maps the active architecture to the current repository. It
describes code that exists, including experiments that are no longer on the
critical path. It does not turn an implemented endpoint or type into a supported
player feature.

## Runtime processes

```text
campaign-monitor browser
  -> Campaign ServiceHost
     -> SQLite journal and local blob store
     -> in-process CampaignSimulation
     -> gRPC companion session
        -> local companion
           -> Runtime Bridge named pipe
              -> Sea Power
```

The monitor is currently an operator and development surface. It is not yet the
campaign application defined by the product contract.

## Projects and roles

| Project or path | Current role | Important limit |
| --- | --- | --- |
| `CrimeanTom.Campaign.Contracts` | Campaign identities, commands, events, projections, AI and fidelity contracts | Contract presence does not prove a player flow |
| `CrimeanTom.Campaign.Engine` | Deterministic reduction, Operational simulation, strategic AI, canonical serialization | Does not call Sea Power or write storage |
| `CrimeanTom.Campaign.Service` | SQLite WAL journal, snapshots, blobs, integrity checks, backup and restore | Long-running save evolution and compaction are not qualified |
| `CrimeanTom.Campaign.Transport` | Generated gRPC/Protobuf companion protocol with reserved deferred-worker fields | Machine protocol only; not the player API |
| `CrimeanTom.Campaign.ServiceHost` | HTTP host, campaign coordinator, scheduler, sessions, projections, companion stream, in-process unattended simulation | Still uses broad shared trust in several paths |
| `CrimeanTom.SeaPower.Companion` | Local bridge adapter, tactical lease, map/proxy synchronization, checkpoint readback | Portable tactical state covers only a narrow profile set |
| `CrimeanTom.RuntimeBridge` and AnchorChain plugin | Typed Sea Power inspection, mutation, screenshots, and UI interaction | Pinned to the supported Sea Power build |
| `apps/campaign-monitor` | Browser map, hierarchy, telemetry, and operator controls | Not a finished player campaign app |
| Integration test projects | Broad campaign and protocol stories | Do not replace live Sea Power qualification |

## Implemented campaign path

The coordinator validates typed commands, reduces them through the campaign
kernel, and appends events under a writer epoch and expected head. SQLite WAL,
periodic snapshots, content-addressed blobs, online backup, and side-directory
restore exist.

`CampaignUnattendedSimulationHostedService` advances campaigns without Sea
Power. `CampaignSimulation` handles movement, sustainment, intelligence,
weather, doctrine, sensors, weapons, countermeasures, damage, and withdrawal.
Strategic AI generates coalition plans, objectives, commitments, packages, and
orders. All Operational ticks still execute in the service process.

## Implemented Sea Power path

The service assigns and fences interaction islands. Companion heartbeats renew
the local tactical lease while the service advances unrelated entities. When
the heartbeat expires, the system can release stale authority and fall back to
the accepted checkpoint or Operational state.

The bridge can materialize an allow-listed owned surface profile and several
terrain-gated ground or installation profiles. Terrain preparation relocates
the local window, waits for finalized land terrain, samples height, and rejects
water or unready placement. The two-second surface readback can commit changed
kinematics, telegraph, fuel or stores fractions, damage, and destruction.

This is not complete portable tactical state. Magazines, sensors, formations,
hosted aircraft, helicopters, broader aircraft, and subsurface ownership still
need explicit extraction, application, and return contracts.

## Implemented map and proxy path

An authenticated Sea Power session can receive every non-destroyed friendly
campaign record and coalition-filtered hostile tracks. Map presence is not tied
to the tactical materialization allow-list. Records use Sea Power authored unit
symbols rather than the generic drawing-marker X.

Typed selection can center the map and request a presentation-only model after
terrain relocation. Friendly proxies use campaign state. Hostile proxies use a
generic estimate and do not load the hostile catalog model or exact true state.
A proxy has no game identity, physics, sensors, weapons, velocity, campaign
authority, or checkpoint rights.

Current live qualification is narrow. Generic visual loading has been observed
for one submarine and one land installation. Direct mouse selection parity,
full catalog coverage, proper overlap handling, uncertainty glyphs, and a
campaign-bound default task force remain open.

## Implemented player and operator path

The monitor polls true diagnostic state over loopback HTTP. Coalition-filtered
projections and reconnectable in-memory player sessions exist. Player command
envelopes currently cover presentation and control-transfer actions, not the
canonical campaign orders required by the product contract.

There is no packaged application, durable player identity, SignalR client, or
private multiplayer flow yet.

## Deferred code archive

The standalone Operational worker, process supervisor, second-Sea-Power scripts,
and migration drivers were removed from the active tree. Git tag
`archive/distributed-workers-before-local-trim-2026-08-24` preserves them. Some
assignment, capacity, and protocol types remain because the local Sea Power
lease uses the same fencing model.

The design context is in [distributed simulation](../future/distributed-simulation.md).

## Current architecture gaps

| Product requirement | Current implementation gap |
| --- | --- |
| Local campaign application | Operator monitor lacks player lifecycle, orders, objectives, and result UX |
| Campaign-owned task force | Authored mission still supplies placeholder local units in the proven path |
| Complete tactical round trip | No qualified campaign-owned hostile fate and expenditure commit |
| Representation parity | Mouse selection, overlap, uncertainty, and catalog-wide proxy behavior remain incomplete |
| Portable state | Aircraft, helicopters, submarines, magazines, hosted assets, sensors, and formations are incomplete |
| Recovery | Protocol recovery exists, but live kill, relaunch, duplicate-object, and latency checks remain |
| Save longevity | Schema and algorithm migration plus long-running compaction remain open |
| Private multiplayer | Durable identity, authorization, realtime UI, and shared-control policy do not exist |

The [current status](../planning/current-status.md) owns the priority order. The
[supported claims](../planning/supported-claims.md) owns the proof boundary.
