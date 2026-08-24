# Current code and product status

## Summary

Reviewed against the repository and Sea Power `0.8.2-23444+s358` on 24 August
2026. Crimean Tom now has a durable local campaign lifecycle and player map,
but it is not yet a playable campaign-to-battle loop.

The strongest parts are deterministic campaign state, SQLite persistence,
authority fencing, the Runtime Bridge, a broad Operational model, strategic AI,
and early map/proxy synchronization. A player can now create, inspect, run,
pause, save, and restore the full proving campaign without operator access. The
remaining product gap starts with orders and continues through tactical entry,
combat return, and recovery.

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
| Sessions | Player profile persists coalition, pause state, cursor, and save head; the process-lifetime browser session renews after service restart | Durable user identity and canonical player orders are absent |
| Player application | Browser shell creates and names the fixed 4,096-entity scenario paused, shows the coalition-filtered map and friendly hierarchy, and provides start, pause, save, open, and restart recovery | Orders, objectives, tactical entry, packaging, and detailed degraded-state UX are absent |
| Authorization | Browser authority is limited to `/player`; operator and companion HTTP control remains bearer-only, and a spoofed gRPC content type does not bypass the HTTP boundary | This is a trusted local-session design, not durable user identity |
| Claim API | `GET /supported-claims` exposes a versioned machine-readable ledger aligned with the local runtime | Update it whenever a supported or unsupported boundary changes |

## What the player can and cannot do

The current prototype can launch services, create a named full campaign paused,
show 4,096 total entities as a coalition-filtered player picture, start and
pause time, save, restart the service, renew the local browser session, and
restore the same campaign head. It can also connect Sea Power, project many
campaign records, select some records through a typed bridge path, and keep one
owned surface unit synchronized under a lease.

It cannot yet provide a coherent war-to-battle play session because:

1. canonical coalition-scoped orders and a complete objective loop are absent;
2. the player application cannot request or follow tactical entry;
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
moved to [future work](../future/distributed-simulation.md). The standalone
worker and second-process tooling are preserved under the archive tag rather
than in the active tree.

## Immediate gaps

### 1. Player orders and objectives

Add coalition-scoped durable commands, visible acceptance and conflict results,
task-force membership, objective progress, and an understandable campaign end
state to the new player shell.

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

The latest full-application smoke used Sea Power
`0.8.2-23444+s358` and the native Red Sea Breakout scenario. At one tactical
capture it reported 18 units, 3 task forces, 90 observed objects, about 60 FPS,
a 16.667 ms engine and game fixed step, internal time dilation 1, no duplicate
stable IDs, and no units beyond loaded terrain. Pause, resume, tactical-map
toggle, screenshot, and native exit worked through the Runtime Bridge. This
proves runtime compatibility for that small native scenario. It does not prove
that the 4,096-entity campaign can enter or return from Sea Power.

## Next work

The next vertical slice should:

1. accept one durable player order and show its campaign effect;
2. expose a visible objective affected by that order;
3. enter Sea Power with a campaign-owned mixed naval task force;
4. make local units, non-local friendlies, and hostile tracks selectable with
   native interaction behavior;
5. fight one campaign-owned hostile while the rest of the war advances;
6. commit the result exactly once; and
7. return and recover the same timeline already proven by the local save path.

Security hardening, persistence work, and broader family support should be done
when this slice exposes the need. Infrastructure that cannot improve this story
should wait.
