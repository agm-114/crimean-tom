# Current code and product status

## Summary

Reviewed against the repository and Sea Power `0.8.2-23444+s358` on 24 August
2026. Crimean Tom has a durable local campaign lifecycle, player map, and its
first campaign-owned surface, submarine, airborne-helicopter, and native hosted
helicopter tactical-entry paths. The canonical hosted SH-2F now also survives a
native flight-deck launch with identity, loadout, ammunition, and fuel intact.
It is not yet a complete campaign-to-battle loop.

The strongest parts are deterministic campaign state, SQLite persistence,
authority fencing, the Runtime Bridge, a broad Operational model, strategic AI,
and early map/proxy synchronization. A player can now create, inspect, run,
pause, save, and restore the full proving campaign without operator access. The
first durable player-order slice now works. The campaign application can also
check a selected friendly group against the connected Sea Power runtime and
activate the whole supported group. The remaining product gap is combined-arms
entry, usable tactical-map continuity, combat return, recovery, broader orders,
and objective progress.

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
| Sea Power bridge | Authenticated local sessions, screenshots, UI inspection, controlled mutation, terrain probes, checkpoints, collider capture, and native hosted-helicopter registration and launch | Pinned game build, narrow portable-state allow-list, and no hosted-aircraft recovery contract |
| Tactical lease | Eligible heartbeats keep assignments live; loss starts a 12-second wall-clock grace while unrelated campaign entities advance | Live reconnect during grace and restart latency still need Sea Power qualification |
| Tactical readback | Changed ship or submarine pose, telegraph, fuel or stores fraction, damage, and destruction can commit every two seconds | Magazines, sensors, formations, hosted assets, and broader families are incomplete |
| Tactical return | The campaign application can request a clean whole-island exit, receive final checkpoint references, release the island atomically, and show the durable member result | Final ammunition, sensors, formations, hosted-aircraft recovery, launched weapons, and objective effects remain unsupported; live Sea Power qualification is pending |
| Map projection | All non-destroyed friendlies and coalition-filtered hostile tracks can reach Sea Power map records | The campaign application map and direct interaction parity are not finished |
| Symbology | Projected records use authored Sea Power unit symbols instead of generic X markers | Overlap, uncertainty glyphs, and catalog-wide behavior remain open |
| Visual proxies | Typed friendly selection can load a non-simulating installed model after terrain relocation; hostile selection uses a generic estimate | Live proof covers only a few catalog entries; mouse selection needs requalification |
| Ground placement | Several ground and installation profiles wait for terrain and reject invalid placement | Broad ground, air-defense, airbase, and ballistic families are not portable |
| Sessions | Player profile persists coalition, pause state, cursor, and save head; the process-lifetime browser session renews after service restart | Durable user identity is absent |
| Player orders | Coalition-scoped Transit and Hold use expected group revisions and durable idempotency; replacement completes the prior order, strategic AI respects the player order, and save/restart retains it | Patrol, escort, engage, withdraw, task-force editing, readiness, EMCON, and ROE are absent |
| Player application | Browser shell creates and names the fixed 4,096-entity scenario paused, shows the coalition-filtered map and force hierarchy, issues Transit/Hold, controls time and saves, and requests tactical entry for a selected friendly group | Combined-arms entry, complete objective progress, packaging, and detailed recovery UX are absent |
| Player tactical entry | Preflight derives coalition authority from the saved player profile, reports each surviving member's runtime-profile support and world-object capacity, rejects foreign or stale requests, and prepares the selected group atomically | Live qualification covers the canonical North Sea group as two ships plus two native onboard helicopters, USS Honolulu, and a separate airborne SH-2F/SH-3H group; combined surface/submarine entry, fixed-wing aircraft, and hosted recovery remain open |
| Authorization | Browser authority is limited to `/player`; operator and companion HTTP control remains bearer-only, and a spoofed gRPC content type does not bypass the HTTP boundary | This is a trusted local-session design, not durable user identity |
| Claim API | `GET /supported-claims` exposes a versioned machine-readable ledger aligned with the local runtime | Update it whenever a supported or unsupported boundary changes |

## What the player can and cannot do

The current prototype can launch services, create a named full campaign paused,
show 4,096 total entities as a coalition-filtered player picture, start and
pause time, save, restart the service, renew the local browser session, and
restore the same campaign head. It can redirect a movable friendly group to a
visible region, cancel it into Hold, and see objective assignment. It can also
connect Sea Power, inspect whether every surviving selected-group member can be
represented natively, and enter a fully supported group. The live proof loaded USS
Pharris as one real Knox object in the native player task force while unrelated
campaign time advanced and the lease remained live. A later full-campaign run
atomically loaded USS Yorktown and USS Spruance as two native player objects,
with campaign names and stable tags preserved. A third full-campaign run loaded
USS Honolulu as a native submarine and kept its campaign lease active while
Operational time advanced. A fourth run atomically loaded native SH-2F and
SH-3H campaign helicopters at roughly 500 feet and kept both leases active
while Operational time advanced. A separate bridge-level run registered an
SH-2F in a campaign-owned Ticonderoga's native flight-deck inventory, enforced
the two-aircraft capacity, scheduled Sea Power's real launch task, and preserved
the helicopter's campaign identity and host relation after it became airborne.
The canonical North Sea group now enters from the full campaign as USS Yorktown
and USS Spruance plus one SH-2F and one SH-3H in Yorktown's native flight-deck
inventory. A fresh full-campaign run launched the SH-2F through Sea Power's
native deck cycle and retained its campaign identity, host relation, ASW
loadout, ammunition, and full fuel state.

It cannot yet provide a coherent war-to-battle play session because:

1. only Transit and Hold player orders exist; broader commands, task-force
   editing, policy controls, objective progress, and campaign end state are absent;
2. the proven Sea Power mission still includes an authored placeholder task
   force instead of loading campaign membership by default;
3. entry has not been qualified for a combined surface/submarine, fixed-wing,
   ground, and installation island;
4. direct mouse interaction with non-local symbols and contacts is not fully
   qualified;
5. proxy and tactical materialization support does not cover the required
   fixed-wing, broader submarine, ground, and installation families;
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

### 1. Expand campaign-owned task-force entry

The selected-group transaction, unsupported-member report, one-member submarine
patrol, and two-member airborne helicopter group work. The canonical North Sea
group also enters as two native ships plus two helicopters in Yorktown's native
flight-deck inventory, and its SH-2F completes native launch. Qualify a combined
surface/submarine group, implement hosted recovery and restart reconciliation,
and then remove the authored mission as the source of player membership.

### 2. Map and proxy parity

Finish native-looking mouse selection, labels, overlap handling, contact
uncertainty, focus, bounded loading feedback, and terrain-safe visual proxies.
Local and non-local records must be indistinguishable at the map interaction
level.

### 3. Complete tactical return

Commit campaign-owned hostile fate, player damage, fuel, stores, ammunition,
hosted assets, and objective effects once. Show unsupported or rejected fields
instead of silently discarding them.

### 4. Broader orders and objective rules

Build on the accepted Transit/Hold path with patrol, escort, engage, withdraw,
task-force membership, readiness, EMCON, ROE, objective progress, and an
understandable campaign end state. The narrow order slice is enough to build and
test the campaign-to-battle loop before widening this surface.

### 5. Recovery and save longevity

Qualify clean exit, forced game termination, game relaunch, companion restart,
service restart, long-running storage, and one schema and algorithm upgrade.

### 6. Broader combined arms

Add portable fixed-wing aircraft, ship-hosted helicopter recovery and return,
broader submarine families, ground targets, airbases,
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

The baseline full-application smoke used Sea Power
`0.8.2-23444+s358` and the native Red Sea Breakout scenario. It reported 18
stable units, 3 task forces, 43 observed objects after weapons and effects had
spawned, about 60 FPS, a 16.667 ms engine and game fixed step, internal time
dilation 1, no invalid geodetic positions, and no units beyond loaded terrain.
Briefing continuation, pause, resume, contact and event capture, screenshot, and
native exit worked through the Runtime Bridge. This proves runtime compatibility
for that small native scenario.

A later full-application run kept the 4,096-entity campaign loaded, selected the
one-member NATO GIUK barrier group in the campaign application, and entered Sea
Power with USS Pharris. Sea Power created one campaign-owned Knox under
`taskforce:Taskforce1`, relocated the origin and camera to the GIUK position,
and reported no duplicate stable IDs or invalid positions. The running sample
reported about 60 FPS, a 16.667 ms engine and game fixed step, and internal time
dilation 1. Campaign time advanced from T+0 to T+40 while the selected group
remained Active and its lease remained live. This proves selected single-group
entry for the supported Knox profile. It does not prove combined-arms entry,
tactical-map parity, combat return, or restart recovery.

A subsequent full-application run entered the two-member NATO North Sea surface
action group from another 4,096-entity campaign. The service prepared USS
Yorktown and USS Spruance as one atomic batch. Sea Power created both native
objects under `taskforce:Taskforce1`, retained their campaign names and stable
tags, and placed them at their North Sea coordinates. Readback reported two
campaign identities, no duplicate stable IDs, about 60 FPS, a 16.667 ms game
fixed step, and internal time dilation 1. This proves mixed surface-class entry,
not a combined surface/submarine group, hosted aircraft, combat return, or
restart recovery.

A third full-application run entered the one-member NATO GIUK submarine patrol
from a 4,096-entity campaign. Sea Power created USS Honolulu as a native
`Submarine` using `usn_ssn_los_angeles / Variant31 / Default`, preserved its
campaign name and stable tag, and placed it at the campaign coordinate under
`taskforce:Taskforce1`. Formation Manager and the selected-unit strip showed
the campaign identity. Readback reported one campaign identity, no duplicate
stable IDs, about 60 FPS, a 16.667 ms fixed step, and internal time dilation 1.
Campaign time advanced from T+0 to T+30 while the tactical assignment remained
Active. This proves one submarine profile and parallel campaign advancement,
not combined entry, submerged-state portability, combat return, or restart
recovery.

A fourth full-application run entered the two-member NATO Atlantic maritime
helicopter group from the 4,096-entity campaign. Sea Power created a native
SH-2F and SH-3H using `usn_sh-2f / Squadron4 / ASW` and
`usn_sh-3h / Squadron1 / ASW`, preserved both campaign names and stable tags,
and placed both under `taskforce:Taskforce1` at about 498 feet. Formation
Manager, the tactical map, and the selected-unit strip exposed the native
objects. Readback reported two campaign identities, no duplicate stable IDs,
about 60 FPS, a 16.667 ms fixed step, and internal time dilation 1. Campaign
time advanced from T+0 to T+40 while both assignments remained Active. This
proves independent airborne helicopter entry, not ship-hosted inventory
transfer, launch and recovery, fixed-wing entry, combat return, or restart
recovery.

A fifth live runtime run exercised the hosted-aircraft primitive in
the native Red Sea Breakout mission. The bridge created a campaign-owned
Ticonderoga, registered an SH-2F as native onboard inventory, exposed Sea
Power's Flight Deck panel, rejected inventory beyond the ship's two-aircraft
capacity, and scheduled the real native launch cycle. Sea Power transferred the
campaign tag to the launched SH-2F. The bridge then adopted it as an active
campaign object, retained its campaign display name and `HostEntityId`, and
focused it by stable campaign ID. The airborne readback showed the requested
`Squadron4 / ASW` profile, full fuel, about 60 FPS, a 16.667 ms fixed step, and
internal time dilation 1. This proves the bridge primitive, not canonical
campaign host-child assignment, player-application entry, recovery, restart
reconciliation, or fixed-wing operations.

A sixth run exercised canonical hosted-aircraft entry from the full 4,096-
entity campaign. The service atomically prepared USS Yorktown, USS Spruance,
one SH-2F, and one SH-3H from their shared North Sea group. Sea Power created
only the two ships as world objects and registered both helicopters in
Yorktown's native flight-deck inventory. Formation Manager and Flight Deck
showed the campaign names and exact profiles. All four assignments remained
active while campaign time advanced past T+3500; the worker retained two active
world objects and four owned campaign resources without duplicate stable IDs.
The game reported about 60 FPS, a 16.667 ms fixed step, and internal time
dilation 1. This proves canonical host-child entry and correct capacity
accounting. It does not prove recovery, restart reconciliation, combat return,
or fixed-wing operations.

A seventh run launched the canonical SH-2F from a fresh full-campaign player-
entry batch. Sea Power completed the native deck cycle and adopted the airborne
object under the same campaign ID, display name, host relation, and
`Squadron4 / ASW` profile. Native readback reported two Mk 46 torpedoes, fifteen
sonobuoys, and 1,214 of 1,214 kg fuel; campaign readback reported 1,000,000 fuel
milliunits. The assignment stayed Active with a live lease, the world had no
duplicate stable IDs, and Sea Power returned from a bounded x10 wait to x1 at
about 60 FPS, a 16.667 ms fixed step, and internal time dilation 1. A concurrent
pre-activation readback was also made harmless so it cannot terminate the
companion stream. Recovery, restart reconciliation, and return to the host
remain unsupported.

## Next work

The next vertical slice should:

1. combine the surface, submarine, and helicopter paths, qualify native recovery
   and return to the host, then remove authored placeholder membership;
2. make local units, non-local friendlies, and hostile tracks selectable with
   native interaction behavior;
3. fight one campaign-owned hostile while the rest of the war advances;
4. commit the result exactly once; and
5. return and recover the same timeline already proven by the local save path.

Security hardening, persistence work, and broader family support should be done
when this slice exposes the need. Infrastructure that cannot improve this story
should wait.
