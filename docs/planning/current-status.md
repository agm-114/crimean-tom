# Current code and product status

## Summary

Reviewed against the repository and Sea Power `0.8.2-23444+s358` on 24 August
2026. Crimean Tom is an advanced campaign-runtime prototype, not a playable
dynamic campaign.

The strongest parts are deterministic campaign state, SQLite persistence,
authority fencing, the Runtime Bridge, a broad Operational model, strategic AI,
and early map/proxy synchronization. The missing part is the product loop: the
player still cannot reliably manage a campaign, enter with the campaign-owned
force, fight a campaign-owned enemy, commit the complete result, and return.

The [product contract](playable-dynamic-campaign-product-contract.md) defines the
goal. The [implementation plan](implementation-plan.md) defines the order.

## What works now

| Area | Current capability | Limit |
| --- | --- | --- |
| Campaign core | Immutable state, typed commands and events, fixed logical time, deterministic replay and hashing | Save schema and algorithm evolution are not qualified |
| Persistence | SQLite WAL, writer fencing, periodic snapshots, compressed events, content-addressed blobs, integrity checks, online backup, side-directory restore | Long campaigns, compaction, disk pressure, and upgrade paths remain open |
| Scenario | Sourced 55-region, 81-base, 4,096-entity WW3++ proving seed | It is an engineering load scenario, not a balanced release campaign |
| Operational model | Movement, sustainment, sensors, intelligence, weapons, countermeasures, damage, weather effects, and withdrawal | Parity with Sea Power remains incomplete and calibration is narrow |
| Strategic AI | Two coalitions create objectives, allocate capable groups, keep fixed defenses in place, form multi-role packages, and replan | No route or capacity deconfliction, deception, or human-level command claim |
| Sea Power bridge | Authenticated local sessions, screenshots, UI inspection, controlled mutation, terrain probes, checkpoints, and collider capture | Pinned game build and narrow portable-state allow-list |
| Tactical lease | Companion heartbeats keep one surface assignment authoritative while unrelated campaign entities advance | Live restart latency and duplicate-object behavior still need qualification |
| Tactical readback | Changed surface pose, telegraph, fuel or stores fraction, damage, and destruction can commit every two seconds | Magazines, sensors, formations, hosted assets, and broader families are incomplete |
| Map projection | All non-destroyed friendlies and coalition-filtered hostile tracks can reach Sea Power map records | The campaign application map and direct interaction parity are not finished |
| Symbology | Projected records use authored Sea Power unit symbols instead of generic X markers | Overlap, uncertainty glyphs, and catalog-wide behavior remain open |
| Visual proxies | Typed friendly selection can load a non-simulating installed model after terrain relocation; hostile selection uses a generic estimate | Live proof covers only a few catalog entries; mouse selection needs requalification |
| Ground placement | Several ground and installation profiles wait for terrain and reject invalid placement | Broad ground, air-defense, airbase, and ballistic families are not portable |
| Sessions | Coalition-filtered projections and reconnectable in-memory player sessions exist | Player identity is not durable and canonical player orders are absent |
| Operator UI | Browser monitor shows diagnostic map, hierarchy, telemetry, and controls | It exposes operator concepts and is not the product campaign application |
| Claim API | `GET /supported-claims` exposes a versioned machine-readable ledger | Its current strings still emphasize Knox proxies, worker placement, replica rules, and older AI limits; reconcile it when code work resumes |

## What the player can and cannot do

The current prototype can launch services, seed and advance a full campaign,
connect Sea Power, project many campaign records, select some records through a
typed bridge path, and keep one owned surface unit synchronized under a lease.

The current prototype cannot yet provide a coherent play session because:

1. the campaign UI is an operator monitor, not a player shell;
2. canonical coalition-scoped orders and a complete objective loop are absent;
3. the proven Sea Power mission still includes an authored placeholder task
   force instead of loading campaign membership by default;
4. direct mouse interaction with non-local symbols and contacts is not fully
   qualified;
5. proxy and tactical materialization support does not cover the required
   aircraft, helicopter, submarine, ground, and installation families;
6. no qualified flow commits the fate and expenditure of a campaign-owned
   hostile after a real battle; and
7. game and service restart recovery has not been proven as a complete live
   player story.

## Architecture decision

The active architecture is local-first:

- the campaign application owns strategy, orders, saves, and tactical entry;
- Sea Power owns the battle and tactical map;
- one campaign service runs Operational simulation and strategic AI;
- one local Sea Power process receives a renewable tactical lease; and
- private multiplayer extends this service after single-player works.

Distributed Operational workers, hot replicas, and live tactical migration have
moved to [future work](../future/distributed-simulation.md). Their experimental
code may remain, but they do not define the delivery order.

## Immediate gaps

### 1. Player campaign application

Turn the monitor into, or replace it with, a player surface for campaign
lifecycle, filtered map, force hierarchy, orders, objectives, time controls,
health, and tactical entry. Separate player authorization from operator access.

### 2. Campaign-owned task force

Remove the authored mission as the source of player membership. Load every
supported available member from the campaign save and report unsupported state
before activation.

### 3. Map and proxy parity

Finish native-looking mouse selection, labels, overlap handling, contact
uncertainty, focus, bounded loading feedback, and terrain-safe visual proxies.
Local and non-local records must be indistinguishable at the map interaction
level.

### 4. Complete tactical return

Commit campaign-owned hostile fate, player damage, fuel, stores, ammunition,
hosted assets, and objective effects once. Show unsupported or rejected fields
instead of silently discarding them.

### 5. Recovery and save longevity

Qualify clean exit, forced game termination, game relaunch, companion restart,
service restart, long-running storage, and one schema and algorithm upgrade.

### 6. Broader combined arms

Add portable aircraft, helicopters, submarines, ground targets, airbases,
radars, GBAD, strategic air defenses, ballistic launchers, logistics, and shore
sites through the same map, entry, return, and recovery contract.

### 7. Private multiplayer

After the local loop works, add durable identities, invitations, coalition
roles, realtime filtered projections, reconnect, shared-control policy, TLS, and
rate limits. This does not require distributed simulation.

## Performance position

The architecture targets 4,096 campaign entities, not 4,096 Sea Power objects.
The full campaign should keep most entities in the Operational model while Sea
Power holds a bounded interaction island, map records, and selected proxies.

Dynamic campaign subsets exist at 500, 1,000, 2,000, and 4,096 entities. Future
results must report native objects, map records, proxies, external entities, fixed
timestep, internal time dilation, frame time, memory, and campaign throughput
separately.

Earlier observations showed the map/proxy path could reconcile hundreds of
records at 60 FPS and a 16.667 ms fixed step in one selected-unit sample. That
is a narrow observation, not a general performance guarantee.

## Next work

The next vertical slice should:

1. create and load the 4,096-entity campaign paused in a player-facing app;
2. accept one durable player order and show its campaign effect;
3. enter Sea Power with a campaign-owned mixed naval task force;
4. make local units, non-local friendlies, and hostile tracks selectable with
   native interaction behavior;
5. fight one campaign-owned hostile while the rest of the war advances;
6. commit the result exactly once; and
7. return, save, restart, and recover the same timeline.

Security hardening, persistence work, and broader family support should be done
when this slice exposes the need. Infrastructure that cannot improve this story
should wait.
