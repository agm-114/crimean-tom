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

The browser surface is now the local player campaign shell. Operator diagnostics
remain on bearer-authenticated service routes and are not sent through the
player projection.

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
| `apps/campaign-monitor` | Local player campaign lifecycle, coalition map, friendly hierarchy, objective assignment, Transit/Hold orders, time, save controls, and selected-group tactical entry | Combined-arms entry, broader orders, objective progress, packaging, and detailed recovery UX remain |
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

The bridge can materialize allow-listed owned ship and submarine profiles and several
terrain-gated ground or installation profiles. Terrain preparation relocates
the local window, waits for finalized land terrain, samples height, and rejects
water or unready placement. The two-second naval readback can commit changed
kinematics, telegraph, fuel or stores fractions, damage, and destruction.

This is not complete portable tactical state. Magazines, sensors, formations,
hosted aircraft, helicopters, broader aircraft, and broader subsurface ownership still
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

The browser polls a coalition-filtered player projection over loopback HTTP. It
can create and name the fixed 4,096-entity scenario paused, start, pause, save,
list, open, renew its process-lifetime session after service restart, and restore
the same campaign head. Its cookie is accepted only on `/player`; operator and
companion HTTP control remains bearer-authenticated. Enemy canonical entities
and operator diagnostics are not serialized into the player view.

The first canonical player-order slice accepts Transit to a visible region and
Hold for movable friendly groups. The service derives coalition authority from
the persisted player profile, checks the group revision, binds an idempotency key
to the request payload, and returns accepted, duplicate, conflict, or rejection
feedback. An open-ended player order remains active at its destination so the
strategic AI cannot immediately retask the group. Replacing an order completes
the prior order at the new order's start time.

The player view exposes current strategic objectives and destination-based
assignment counts. Patrol, escort, engage, withdraw, task-force editing, policy
orders, objective progress, victory, and defeat remain unimplemented.

The player tactical-entry route accepts only a friendly group from the saved
player coalition. Its read model reports the current group revision, connected
Sea Power session, usable capacity, and exact support for each surviving
member. The write requires that reviewed revision and an idempotency key. It
uses the existing fenced handoff path and activates multi-member groups only
after the complete batch is prepared. The full 4,096-entity service story and a
live Sea Power run now pass for the two-member USS Yorktown and USS Spruance
group. A separate run passes for USS Honolulu as a native Los Angeles-class
submarine while campaign time advances. Their campaign identities cross the
service, companion, and Runtime Bridge boundary into native Formation Manager
labels. Combined surface/submarine entry, hosted assets, broader submarine
families, combined-arms entry, and authored-force replacement remain open.

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
| Local campaign application | Lifecycle, filtered map, objective assignment, Transit/Hold, and selected-group entry work; broader orders, objective progress, packaging, and result UX remain |
| Campaign-owned task force | One two-ship surface group and one separate submarine patrol can enter; the authored mission still supplies the rest of the placeholder local force |
| Complete tactical round trip | No qualified campaign-owned hostile fate and expenditure commit |
| Representation parity | Mouse selection, overlap, uncertainty, and catalog-wide proxy behavior remain incomplete |
| Portable state | Aircraft, helicopters, broader submarine state, magazines, hosted assets, sensors, and formations are incomplete |
| Recovery | Protocol recovery exists, but live kill, relaunch, duplicate-object, and latency checks remain |
| Save longevity | Schema and algorithm migration plus long-running compaction remain open |
| Private multiplayer | Durable identity, authorization, realtime UI, and shared-control policy do not exist |

The [current status](../planning/current-status.md) owns the priority order. The
[supported claims](../planning/supported-claims.md) owns the proof boundary.
