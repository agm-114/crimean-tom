# Campaign systems

Sea Power contains three campaign-related approaches. They share some tactical
infrastructure but should not be conflated.

## Authored linear campaigns

`LinearCampaign` sequences authored missions and events. It owns campaign
metadata, a mission list, persistent key/value data, commander name, unlocks,
localization, and campaign/temp save paths. Mission completion updates persistent
state and advances or branches the authored sequence.

This is best understood as a narrative orchestration layer around tactical
missions. It is useful for:

- event and mission gating;
- persistent variables and localized text;
- pre/post-mission events;
- campaign save lifecycle and compatibility checks.

It does not by itself continuously simulate a theater.

## Task Force Mode

Task Force Mode is built on the linear campaign path but adds a substantial
persistent force-management layer. The live campaign browser described it as a
mode where the player builds and customizes a task force, deploys it across
missions, carries losses and weapon expenditure forward, and repairs/resupplies
at campaign points.

The selected `Pacific Strike '85` campaign exposed:

- three difficulty presets plus unrestricted mode;
- a unit-date limit option;
- starting and cap points;
- rules for included air groups and purchasing ship loadouts;
- initial unlocked loadout families;
- initial crew skill;
- repair cost and refund percentages;
- a warning that the rules are locked after campaign start.

### Persistence model

`LinearCampaignTaskForceSaveManager` and related view models maintain a much
richer state than the base linear campaign:

- a canonical roster with stable instance IDs;
- selected variants and loadout variants;
- hosted aircraft assignments and remaining quantities;
- persistent unit damage and tactical losses;
- ammunition/weapon expenditure;
- deployment position, formation, and waypoints;
- points, purchases, refunds, repairs, and unlocks;
- mission launch snapshots, post-mission snapshots, and replay support;
- crew-skill survival credits and progression.

Repair rules distinguish repairable and heavy damage. The UI explicitly says a
repair does not replenish ammunition. Heavy damage may require decommissioning.

### Why this matters for Crimean Tom

Task Force Mode is the strongest existing example of a tactical continuity
contract. We should reuse its concepts and observe its file formats, but avoid
binding an external simulation directly to its large view models. A dedicated
Crimean Tom schema can import/export the stable subset and survive UI refactors.

### Port-operations shell experiment

A repository-owned development campaign now proves a narrower reuse boundary.
It enables Task Force Mode, exposes a single maintenance node, and supplies a
small local roster of surface ships, submarines, helicopters, and aircraft. A
typed Runtime Bridge command opens `OpenTaskForceBuilderCommand` on Unity's main
thread after checking that the linear campaign, selected event, and builder are
available. A companion read reports panel state, points, purchased/visible unit
counts, total available roster variants, and whether ship/submarine/air groups
are populated. It also projects each purchased draft item through a portable
payload containing the native instance ID, unit/category/variant, nation,
selected loadout, quantity, cost, and hosted-air assignments.

Live inspection established that `RosterFile` resolves relative to the authored
campaign file. Referencing another built-in campaign therefore produced an empty
catalog even though the builder loaded; copying a deliberately small roster into
the Crimean Tom campaign populated the native catalog. The shell currently uses
a built-in mission file only to supply working UI/map context. No purchase,
confirmation, save, or tactical result was required for this proof.

The experiment supports a controlled-restart projection design: generate the
local roster and session state from the authoritative external port revision,
restart/reload Sea Power, collect a maintenance draft from the native UI, and
validate/commit it externally. It does not justify using Task Force Mode saves
as campaign truth or sending battles into small authored missions for
resolution.

## Dynamic/map campaign framework

`CampaignMain` is a different system intended to simulate groups on a campaign
map. It loads INI data into:

- map labels and geographic points;
- air, sea, and land graph links;
- airports, ports, installations, and cities;
- blue/red unit pools and active groups;
- transit, patrol, escort, and background-traffic state;
- mission, event, and objective templates plus active instances.

`CampaignUpdater` advances the campaign clock and environment, evaluates
objective triggers and active missions/objectives, updates positions in a
Burst `IJobParallelFor` (`UpdateLocationParallelForJob`), then updates patrols,
bases, ports, and other updateable groups. The job steps `float2` graph
locations at `GameTime.deltaTime * speedKnots * 0.514444` m/s (knots to metres
per second). Adds/removes are deferred so collections are not mutated during
iteration. `CampaignObjective.Update` and the win/lose paths remain empty in
this build.

`CampaignFixedUpdater` and `CampaignLateUpdater` are empty in this build. The
strategic simulation is therefore driven primarily by the ordinary update and
logical campaign time.

### Strategic graph and groups

Campaign groups move through typed graphs rather than full tactical physics.
They can be spawned from pools, assigned paths/patrols/escorts, placed on
station, or sent between cities and bases. The position job is already a
proof-of-concept for parallel abstract movement.

`CampaignMission` includes these mission types:

- Amphibious Assault
- Land Assault
- Air Assault
- Patrol
- Standoff Strike
- Transit

Mission setup selects suitable locations and units by side, role, range,
reachability, and logistics. It consumes city logistics, spawns strategic
groups, tracks movement/on-station time, launches standoff missile groups, and
can change a city's side after capture.

### Tactical materialization evidence

`CampaignMain.StartMission(group)` does not stream entities into an already
running tactical scene. `SetupGroupsForTacticalMission` clears
`MissionEditorModel.Groups`, adds the central group, then copies every other
`GroupOfUnits` whose `Location.GetDistance(...) / 1000.0` is under
`925.9999990463257`, or **about 926 km and 500 nmi** with 1852 m/nmi. It writes
`GeoPosition` from each group's station offsets, and calls
`MissionEditorModel.SetupMission`. That is a generated-mission snapshot, not
Crimean Tom live streaming, and it is not a measured weapons/terrain bubble.

`CampaignMain.SceneManager_sceneLoaded` is empty. Pause-menu campaign save and
exit on this path throw `NotImplementedException`. Treat the seam as proof that
abstract groups *can* be copied into a mission file, not as a handoff API.

Crimean Tom's current design still generalizes the idea (selected strategic
entities become detailed tactical units) into live streaming and
dematerialization within a continuous campaign. It does not use a fresh mission
as the normal boundary. See [Tactical bubble](tactical-bubble.md).

### Incomplete areas

`CampaignObjective` defines Take, Hold, and Interdict objectives and can trigger
random mission templates, but several lifecycle methods decompile as empty in
this installed build. The newer `SeaPowerAI.AIController.OnUpdate` is also empty.

Treat the dynamic campaign as an unfinished architectural prototype, not a
stable public API. Useful ideas include its graph, abstract group, location,
mission-template, and generated-mission materialization model. Depending on its
exact runtime classes would create high update risk.

## Comparison

| Concern | Linear | Task Force Mode | Dynamic/map |
| --- | --- | --- | --- |
| Primary unit | Authored mission/event | Persistent player roster plus authored mission | Abstract map group/location |
| Theater clock | Mostly progression/event based | Mostly progression plus logistics between missions | Continuous logical campaign time |
| Force persistence | Variables/unlocks | Detailed loss, ammo, damage, crew, loadout state | Pools and active strategic groups |
| Tactical entry | Authored mission | Generated/customized authored mission | Generate a mission from groups within ~500 nmi |
| Current completeness | Shipping campaign flow | Rich shipping feature | Partly implemented framework |

## Recommended reuse boundary

Reuse or emulate concepts from all three:

- linear campaign: events, localization, versioned persistence, mission gates;
- task-force mode: stable unit instances, losses, ammunition, damage, repair,
  crew progression, and tactical snapshot lifecycle;
- dynamic/map campaign: strategic graph, abstract movement, logistics, and the
  strategic-to-tactical materialization seam.

Do not make the campaign core a subclass of any of them. Keep the core pure and
use a version-specific in-game adapter.
