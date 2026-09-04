# Current code and product status

## Summary

Reviewed against the repository and Sea Power `0.8.2-23444+s358` on 26 August
2026. Crimean Tom has a durable local campaign lifecycle, player map, and its
first campaign-owned surface, submarine, airborne-helicopter, and native hosted
helicopter tactical-entry paths. The canonical hosted SH-2F now also survives a
native flight-deck launch with identity, loadout, ammunition, and fuel intact.
The North Sea story now includes one coalition-known Soviet Krivak under
opposing tactical authority without exposing its canonical truth to the player.
A live run placed it on Sea Power's Enemy task force and returned the whole
five-member island cleanly. Hostile combat fate and complete expenditure return
remain unqualified, so this is not a complete campaign-to-battle loop.

The strongest parts are deterministic campaign state, SQLite persistence,
authority fencing, the Runtime Bridge, a broad Operational model, strategic AI,
and early map/proxy synchronization. A player can now create, inspect, run,
pause, save, and restore the full proving campaign without operator access. The
first durable player-order slice now works. The campaign application can also
check a selected friendly group against the connected Sea Power runtime and
activate the whole supported group. The remaining product gap is combined-arms
entry, usable tactical-map continuity, combat return, recovery, broader orders,
and objective progress.

The server combat model is not full fidelity. It can produce detections,
launches, weapon expenditure, point defense, aggregate damage, destruction, and
withdrawal. Weapon profiles now preserve native catalog type, range, guidance,
speed, seeker, reliability, hit-probability, and warhead fields while marking
incomplete profiles separately. Ammunition is no longer one fungible pool:
each platform carries rounds in named magazines whose capacities come from Sea
Power's own loadout rule, and weapon selection, point defense, rearming, and
withdrawal all work on those counts. Re-engagement is gated by each
mount's own native reload time, so a long-cycle box launcher no longer fires
every tick and a twin installation bursts before it goes quiet.
Magazine and reload state is carried on the simulation event, so expenditure
persists across ticks rather than resetting each one. Radar detection range
comes from the radars a hull actually carries rather than one constant per
domain, so a Knox and a Ticonderoga no longer see equally far, and it now
follows the game's own radar equation against the target's declared size,
bounded by the declared maximum range and the radar horizon. The model still does not reproduce Sea Power's
complete timing, sensors, weapon flight, damage, aviation, acoustics, collision,
or tactical AI. Its current memory and throughput numbers therefore describe an
incomplete simulator. They are not performance targets for the finished design.
The canonical backlog is
[full-fidelity server simulation](../architecture/full-fidelity-server-simulation.md).

The [product contract](playable-dynamic-campaign-product-contract.md) defines the
goal. The [implementation plan](implementation-plan.md) defines the order.

## What works now

| Area | Current capability | Limit |
| --- | --- | --- |
| Campaign core | Immutable state, typed commands and events, fixed logical time, deterministic replay and hashing | Save schema and algorithm evolution are not qualified |
| Campaign clock | One quantum is one simulated second; the default target tempo is 10x and the player view declares the clock contract | Actual throughput can fall below target under load; player-selectable tempo and measured lag are not implemented |
| Persistence | SQLite WAL, writer fencing, periodic snapshots, compressed events, content-addressed blobs, integrity checks, online backup, side-directory restore | Long campaigns, compaction, disk pressure, and upgrade paths remain open |
| Scenario | Sourced 55-region, 81-base, 4,161-entity authored WW3++ order of battle with nationally based air, naval, and ground forces | Detailed historical OOB work, front geometry, force composition, and balance remain iterative |
| Server simulation | Movement, sustainment, sensors, intelligence, weapons, countermeasures, damage, weather effects, withdrawal, per-ammunition magazines counted with the game's own loadout rule, and native per-mount reload and salvo cadence | This is an aggregate Operational model, not full Sea Power parity; most mechanics in the full-fidelity gap inventory remain open. Magazine capacity, consumption, and cadence are catalog-derived but not yet matched-run qualified |
| Strategic AI | Two coalitions create intelligence-scoped objectives, enforce domain reach and objective capacity, penalize loaded route entrances, and form persistent two-to-six-group packages | No route reservations, regional throughput, deception, package success/abort state, or human-level command claim |
| Operational control | Maritime, fixed-wing, rotary-wing, ground, and logistics controllers stage packages, execute domain orders, return aircraft before fuel/time reserves expire, shuttle supply groups, and fence player/tactical authority | Air groups remain persistent abstractions rather than airbase inventory with launch, tanker, runway, and recovery schedules |
| Sea Power bridge | Authenticated local sessions, screenshots, UI inspection, controlled mutation, terrain probes, checkpoints, collider capture, and native hosted-helicopter registration and launch | Pinned game build, narrow portable-state allow-list, and no hosted-aircraft recovery contract |
| Tactical lease | Eligible heartbeats keep assignments live; loss starts a 12-second wall-clock grace while unrelated campaign entities advance; game-session loss blocks new authority work and drains the current result into a bounded durable outbox before fencing the old stream | Live reconnect during grace, abrupt process loss, and restart latency still need Sea Power qualification |
| Tactical readback | Changed ship or submarine pose, telegraph, stores fraction, damage, destruction, and campaign-tracked per-ammunition magazines can commit every two seconds; Sea Power exposes no native fuel capacity for current vessel profiles, so campaign fuel remains unchanged | Magazine readback is implemented but not live-qualified; sensors, formations, hosted assets, vessel fuel consumption, and broader families are incomplete |
| Tactical return | The campaign application can request a clean whole-island exit, receive final checkpoint references including campaign-tracked magazine counts, release the island atomically, show the durable member result, and idempotently reclaim matching bridge-owned objects from positive release receipts; a scene-lost checkpointed record can fence without tactical pause at its unchanged revision | Live expenditure return, sensors, formations, hosted-aircraft recovery, launched weapons, and objective effects remain unsupported; detached return is live-qualified for one submarine profile |
| Map projection | One full session/resync snapshot is followed by versioned upserts and removals on the Bulk lane; viewport work is frame-budgeted and uses 2-, 16-, or 24-pixel clustering cells according to visible density | A current global fullscreen pass processed 4,157 records, reduced 4,061 on-screen candidates to 183 markers, and refreshed in 4.056 ms with a 4.223 ms worst slice at 60 FPS; labels remain dense at world scale, and reconnect repair, delta telemetry, and concurrent control latency remain open |
| Symbology | Projected records use authored Sea Power unit symbols instead of generic X markers | Overlap, uncertainty glyphs, and catalog-wide behavior remain open |
| Visual proxies | Selection is an explicit state machine; it preserves native hit precedence, relocates once, polls terrain, then creates and focuses a non-simulating model or hostile estimate | Direct mouse selection, cancellation, failure display, and terrain behavior need live proof across more catalog families |
| Focused promotion | A native `IMapSelectable` transition requests one campaign entity without requiring its entire Operational group to fit; preparation requires the selected entity's exact runtime-advertised profile, native initialization, stores, airborne state where required, and friendly control before checkpoint and focus; public altitude/depth is corrected against native readback; pre-checkpoint failure rolls back and permits retry | A live CAS F-5E proved weapons, gear-up airborne state, native selection, status, and orders; a full-campaign USS Honolulu promotion proved exact Variant31 identity, initialized stores, native control, status, and camera focus; rollback and broader families still need live qualification |
| Ground placement | Several ground and installation profiles wait for terrain and reject invalid placement | Broad ground, air-defense, airbase, and ballistic families are not portable |
| Sessions | Player profile persists coalition, pause state, cursor, and save head; the process-lifetime browser session renews after service restart | Durable user identity is absent |
| Player orders | Coalition-scoped Transit and Hold use expected group revisions and durable idempotency; replacement completes the prior order, strategic AI respects the player order, and save/restart retains it | Patrol, escort, engage, withdraw, task-force editing, readiness, EMCON, and ROE are absent |
| Player application | Browser shell creates and names the authored scenario paused, shows the coalition-filtered map and force hierarchy, issues Transit/Hold, controls time and saves, and requests tactical entry for a selected friendly group | Combined-arms entry, complete objective progress, packaging, and detailed recovery UX are absent |
| Player tactical entry | Preflight derives coalition authority from the saved player profile, reports each surviving member's runtime-profile support and world-object capacity, rejects foreign or stale requests, and prepares the interaction island atomically; the known hostile remains a masked track in the player view | Live qualification covers the five-member North Sea island, USS Honolulu, and a separate airborne SH-2F/SH-3H group; combined surface/submarine entry, fixed-wing aircraft, and hosted recovery remain open |
| Authorization | Browser authority is limited to `/player`; operator and companion HTTP control remains bearer-only, and a spoofed gRPC content type does not bypass the HTTP boundary | This is a trusted local-session design, not durable user identity |
| Claim API | `GET /supported-claims` exposes a versioned machine-readable ledger aligned with the local runtime | Update it whenever a supported or unsupported boundary changes |

## What the player can and cannot do

The current prototype can launch services, create a named full campaign paused,
show its complete authorized order of battle as a coalition-filtered player picture, start and
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

The broad service story and a live Sea Power run extend that North Sea entry to five assignments:
Yorktown, Spruance, both hosted helicopters, and one confirmed Soviet Krivak
contact. The player preflight receives a stable track ID and generic surface
classification, while the trusted companion assignment retains the canonical
identity and exact runtime profile. The companion selects an authored opposing
task force, and normal exit fences and returns all five assignments together.
The live run reported five owned resources, three world objects, no duplicate
stable IDs, and no remaining generic hostile proxy. It held about 60 FPS with a
16.667 ms fixed step and internal dilation 1. A later bounded run started the
same five-member island, exited it, then started and exited the one-member USS
Honolulu island without restarting Sea Power. Both exits returned every
assignment and reduced the bridge-owned resource ledger to zero. The companion
explicitly reclaimed five committed resources after the first exit and one
after the second.

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
6. the service can return a campaign-owned hostile's supported state fields,
   but no live battle has qualified its fate and expenditure; and
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

### 1. Replace aggregate server combat with full-fidelity mechanics

Treat Sea Power as the reference implementation for the pinned build. Start
with one bounded combined naval and air engagement. Define lossless portable
state, capture native traces, implement compatible server movement, sensing,
weapons, fuel, damage, and tactical decisions, then compare the two from the
same initial state and random inputs. Add subsystem sleeping only after the
always-awake server path passes. A sleeping path must also match the awake path.

The full inventory covers timing, environment, navigation, collision, sensors,
tracks, electronic warfare, weapons, point defense, damage, fuel, naval and air
operations, ground systems, command links, AI, civilians, catalog mappings,
handoff state, cross-authority interactions, scheduling, validation,
performance, and versioning. It lives in
[full-fidelity server simulation](../architecture/full-fidelity-server-simulation.md).

### 2. Expand campaign-owned task-force entry

The selected-group transaction, unsupported-member report, one-member submarine
patrol, and two-member airborne helicopter group work. The canonical North Sea
group also enters as two native ships plus two helicopters in Yorktown's native
flight-deck inventory, and its SH-2F completes native launch. One confirmed
hostile Krivak now enters the authored Enemy task force and returns with that
island. Next qualify a combined surface/submarine group, implement hosted
recovery and restart reconciliation, and remove the authored mission as the
source of player membership.

### 3. Map and proxy parity

Map-driven promotion is entity-scoped. Explicit entry from the campaign
application remains a whole-group transaction. One fixed-wing path now has a
live proof: a campaign-selected RoCAF F-5E used the requested CAS loadout,
spawned airborne with gear retracted, exposed native orders and status, and
retained ammunition. The 3D follow camera centered on the unit. A later full-
campaign run applied all 4,096 projection records, focused USS Spruance, opened
Sea Power's full-screen tactical map, and found the viewport centered on that
unit. The current 4,161-entity campaign then entered the five-member North Sea
task force, kept seven native units in the tactical scene, and applied 4,157 map
records in 16.419 ms. A follow-up used the pinned vanilla fullscreen action and
Sea Power's clamped world zoom. It processed all 4,157 records, found 4,061
on-screen candidates, attached 183 clustered markers, and refreshed the viewport
in 4.056 ms. Sea Power held 60 FPS at a 16.667 ms fixed step and internal time
dilation 1. Labels are still dense at world scale. Direct mouse selection and
overlap behavior remain open.

The working implementation separates Bulk projection traffic from Control,
tracks game-session generation and revision, sends differential upserts and
removals after the initial snapshot, applies map records under a frame budget,
gives native Sea Power objects selection precedence, and refuses to replace an
in-progress apply. Viewport refreshes now discard records outside the current
geographic bounds before calling Sea Power's more expensive screen projection,
and report total, prefiltered, selectable, attached, total-time, and worst-slice
counts for live tuning. Views spanning a full globe bypass geographic culling
because Sea Power normalizes their corner longitudes. A typed map-selection event reaches one promotion
orchestrator; a two-second poll exists only for event loss and reconnect repair.
Same-locality promotion retains current player units. A distant-locality
preflight starts checkpoint and suspension before the floating-origin rebase.

Live inspection exposed a stronger floating-origin constraint: after focusing a
Mediterranean aircraft, authored GIUK objects remained active more than 3,000 km
from the new origin. The architecture now permits only one active native
locality. The implementation now requests checkpointed old-island suspension
before distant promotion. Live qualification must still cover authored-object
unload, selection, terrain polling, camera focus, loadout, orders, and reconnect
replay.

After that, finish overlap handling, contact uncertainty, failure feedback, and
terrain-safe visual proxies. The native name-override path is now applied to
promoted objects, and non-local friendlies no longer receive contact-number or
class prefixes, so both paths use the authored campaign unit name. That
correction still needs a live run. Local and non-local records must be
indistinguishable at the map interaction level.

For local map debugging, the companion may ask the authenticated worker
projection to reveal every live opposing entity as an exact hostile marker.
This temporary mode replaces approximate contact markers so contacts are not
drawn twice. Local service runs allow it by default and can disable it
with the literal value `false`; any other non-Boolean value fails startup. If
the service denies the debug view, the companion latches that denial and uses
ordinary coalition-filtered projections instead of dropping friendly map sync
or retrying a forbidden request every cycle.

### 4. Complete tactical return

Commit campaign-owned hostile fate, player damage, fuel, stores, ammunition,
hosted assets, and objective effects once. Show unsupported or rejected fields
instead of silently discarding them.

### 5. Broader orders and objective rules

Build on the accepted Transit/Hold path with patrol, escort, engage, withdraw,
task-force membership, readiness, EMCON, ROE, objective progress, and an
understandable campaign end state. The narrow order slice is enough to build and
test the campaign-to-battle loop before widening this surface.

### 6. Recovery and save longevity

Qualify clean exit, forced game termination, game relaunch, companion restart,
service restart, long-running storage, and one schema and algorithm upgrade.

### 7. Broader combined arms

Add portable fixed-wing aircraft, ship-hosted helicopter recovery and return,
broader submarine families, ground targets, airbases,
radars, GBAD, strategic air defenses, ballistic launchers, logistics, and shore
sites through the same map, entry, return, and recovery contract.

### 8. Private multiplayer

After the local loop works, add durable identities, invitations, coalition
roles, realtime filtered projections, reconnect, shared-control policy, TLS, and
rate limits. This does not require distributed simulation.

## Performance position

The authored order of battle determines campaign size. The full campaign should
keep most entities in the server simulator while Sea Power holds a bounded
interaction island, map records, and selected proxies. Future results must
report native objects, map records, proxies, external entities, fixed
timestep, internal time dilation, frame time, memory, and campaign throughput
separately.

The measurements below come from the current aggregate model. Repeat them after
each full-fidelity mechanic lands. A cheaper result is not an improvement if it
changes detection, weapon, damage, fuel, or AI behavior.

A local full-campaign profile advanced the current 4,161 entities and 1,244
groups through 600 quanta in 24.316 wall seconds. Both coalitions remained
active. They held 101 and 88 operational packages with 470 and 382 committed
groups, produced 23,707 and 18,360 recorded outcomes, damaged forces on both
sides, and lost five and three entities. The recorded outcomes included
detection, launch, weapon expenditure, point defense, damage, withdrawal, and
seeker loss. The largest coalition-region concentration changed from 108
groups to 107. This supports two-sided
combat and bounded convergence in the Operational model. It does not qualify
Sea Power parity, campaign balance, or long-duration save growth.

The baseline full-application smoke used Sea Power
`0.8.2-23444+s358` and the native Red Sea Breakout scenario. It reported 18
stable units, 3 task forces, 43 observed objects after weapons and effects had
spawned, about 60 FPS, a 16.667 ms engine and game fixed step, internal time
dilation 1, no invalid geodetic positions, and no units beyond loaded terrain.
Briefing continuation, pause, resume, contact and event capture, screenshot, and
native exit worked through the Runtime Bridge. This proves runtime compatibility
for that small native scenario.

A later full-application run kept the then-current full campaign loaded, selected the
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
action group from another full campaign. The service prepared USS
Yorktown and USS Spruance as one atomic batch. Sea Power created both native
objects under `taskforce:Taskforce1`, retained their campaign names and stable
tags, and placed them at their North Sea coordinates. Readback reported two
campaign identities, no duplicate stable IDs, about 60 FPS, a 16.667 ms game
fixed step, and internal time dilation 1. This proves mixed surface-class entry,
not a combined surface/submarine group, hosted aircraft, combat return, or
restart recovery.

A third full-application run entered the one-member NATO GIUK submarine patrol
from a full campaign. Sea Power created USS Honolulu as a native
`Submarine` using `usn_ssn_los_angeles / Variant31 / Default`, preserved its
campaign name and stable tag, and placed it at the campaign coordinate under
`taskforce:Taskforce1`. Formation Manager and the selected-unit strip showed
the campaign identity. Readback reported one campaign identity, no duplicate
stable IDs, about 60 FPS, a 16.667 ms fixed step, and internal time dilation 1.
Campaign time advanced from T+0 to T+30 while the tactical assignment remained
Active. This proves one submarine profile and parallel campaign advancement,
not combined entry, submerged-state portability, combat return, or restart
recovery.

A later map-driven run repeated USS Honolulu from the full campaign rather than
the explicit group-entry path. The first inert representation was replaced by
a native `usn_ssn_los_angeles / Variant31 / Default` object. Runtime readback
reported native initialization, player orders and control, initialized stores,
52 rounds of ammunition, no recovery flag, and one active owned record. Sea
Power's map, Formation Manager, and selected-unit HUD all showed USS Honolulu.
This run also exposed and corrected two exact-state defects: profile sorting had
selected a fallback ahead of canonical `Variant31`, and Sea Power's submarine
center-to-keel offset had shifted public altitude by roughly 15 feet. It proves
the map-selection promotion boundary for this submarine profile, not the full
order UI.

A final recovery run selected and materialized the same USS Honolulu from a
fresh full campaign, then left Sea Power before requesting campaign
return. Unity had destroyed the native object but retained its managed wrapper.
The bridge classified the record as `detached-after-scene-loss`, reported native
readiness and control as false, reused the current revision-1 checkpoint, and
completed the fence without a tactical-pause command. The campaign committed the
result and released the assignment. On companion reconnect, the matching
positive receipt reclaimed the detached ledger record without loading another
tactical scene; the bridge then reported zero owned or detached records. This
qualifies detached return and post-commit reclamation for one submarine profile,
not arbitrary process termination or every portable family.

A follow-up code audit applied the same Unity destroyed-wrapper rule to world
snapshots, camera focus, tactical-location scans, collider and memory
diagnostics, hosted-aircraft lookup, and both visual-proxy implementations. It
also releases a deselected inert model and its materials while retaining the map
record. The broad integration suite and runtime build cover these changes; the
expanded diagnostic and repeated-selection paths have not had another live Sea
Power pass yet.

A fourth full-application run entered the two-member NATO Atlantic maritime
helicopter group from the full campaign. Sea Power created a native
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

A sixth run exercised canonical hosted-aircraft entry from the then-current
full campaign. The service atomically prepared USS Yorktown, USS Spruance,
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
