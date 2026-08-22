# Tactical simulation

## Tactical state model

The tactical mission combines several layers:

1. Ground-truth Unity objects (`ObjectBase` and type-specific subclasses).
2. Per-unit sensor systems and internal AI contact state.
3. Per-task-force plotting tables and shared tracks.
4. Orders, formations, state machines, and task-force coordinators.
5. Weapons, projectiles, blast/damage systems, and mission triggers.
6. Noesis view models that present the player-relative picture.

A test should specify which layer it is asserting. Seeing a map contact is not
the same as proving a ground-truth enemy exists, and seeing an enemy object via
UnityExplorer is not proof the player has detected it.

### Reproducibility

Exact reproduction is not guaranteed in this layer stack. Three process-global
RNG streams feed simulation decisions — `Globals._unityRandom` (fixed seed but a
single shared sequence), `Globals._rnd`, and `AI._aiRandom` (both unseeded) —
and per-object update order is reverse insertion order over a plain list. See
[Runtime architecture](runtime-architecture.md) for the details.

Treat tactical results statistically until the controlled runtime is validated.
Exact assertions may pass for a scenario that never reaches a divergent branch,
but they are not a sound general contract; repeated outcome classes and bounded
state envelopes are safer baseline tests.

## Off-camera units and land units

`GameMain` still ticks every registered `ObjectBase` that is not `Disabled`.
Units outside the player's map or camera are not abstracted and are not paused.
That is a capacity problem for Crimean Tom, not a native bubble. Distances,
relocation, and native campaign pull-in are compared in
[Tactical bubble](tactical-bubble.md).

`LandUnit : ObjectBase` is a full tactical object. Subtypes in
`LandUnitParameters.LandUnitSubType` are `AirBase`, `Installation`, `MobileUnit`,
`AAA`, `SAM`, `MissileSite`, `Radar`, `Port`, `Bridge`, and `OilRig`. Mobile
units apply `Rigidbody` motion from `OnFixedUpdate` when they have control.
Installations still run fire, destruction, terrain-chunk altitude correction,
and the shared weapon/sensor stack. `SceneCreator` also loads **background**
land units from mission `BackgroundCityFile`, `BackgroundAirportFile`,
`BackgroundPortFile`, and `BackgroundInstallationFile` sections.

Crimean Tom's Fulda seed uses `SimulationDomain.LandAbstract` with an Operational
ceiling for Central Front formations. Those must not be promoted into
`LandUnit`. Coastal SAM, radar, and ports that belong inside a Sea Power
session would materialize as `LandUnit`.

## Contacts and sensors

Sensor subclasses cover radar, sonar, visual, ESM, ECM, MAD, and laser
designation. A `SensorSystem` knows its view arcs, ranges, enabled/busy state,
search depth, target and weapon channels, associated fire-control radars,
tracked/identified objects, and guided weapons.

Detection feeds track entities rather than immediately revealing full identity.
`UnitInternalContact` records a progression including:

- detected, classified, and identified flags;
- solution quality and uncertainty ellipse;
- observed velocity, observation/signal values, and time since loss.

Each task force has a `PlottingTable`. It creates task-force-relative vehicle
tracks, assigns own/foreign track IDs, retains a ring of observations/states,
merges shared reports, steps/fades tracks, and publishes reactive state. Data
links update at a configured interval, so information arrival is intentionally
phased.

The cadence is set by `TaskforceManager.OnUpdate`. `_dataLinkUpdateRate` comes
from INI (`General/DataLinkUpdateRate`, default one second) and the elapsed
measure is `Time.time` — Unity's *scaled* clock, which the physics cap holds at
10×. Within a cycle every task force receives incremental updates carrying the
fraction of the interval elapsed; at the boundary the ECS
`PlottingTableSystemGroup` is updated explicitly and
`updateTaskforceContacts()` republishes the shared picture. The whole cycle is
skipped while paused.

The pipeline has both incremental and boundary work: sensor links and plotting
interpolation can change within an interval, while ECS synchronization,
own-plot publication, and the full plotting-table update happen at the boundary.
Sampling twice within an interval is therefore not guaranteed to be identical.
Because the interval is measured in capped scaled time, running above 10×
compression performs those boundaries progressively less often per unit of
simulated time, so the shared tactical picture can coarsen as compression rises.

The event log observed in play reflects this pipeline: visual, radar, ESM, and
passive reports appeared as distinct events and contacts received track
numbers.

### Campaign implication

Strategic intelligence should not use tactical omniscience. Store estimated
location, classification confidence, last-seen time, and source provenance. A
client projection should materialize only contacts and uncertainty its player
may know, never exact hidden enemy state merely to support local viewing.

## Orders, formations, and unit state machines

`Order`/`OrderBase` represent player or AI intent such as course changes and
return-to-base behavior. Units convert intent into type-specific state machines.
Transitions are evaluated during fixed update; any-state transitions are tested
before current-state transitions.

Observed or decompiled states include:

- vessels: default movement, formation movement, sprint/drift, clear baffles,
  contact solution/classification/identification, collision avoidance, air ops,
  counter-launch, and torpedo evasion;
- aircraft: formation/loitering, CAP, MPA, AEW patterns, intercept/identify,
  surface/subsurface/air engagement, collision and missile evasion, bingo fuel,
  winchester, and return to base;
- submarines: contact acquisition/reacquisition, classification, engagement,
  evasion, depth/motion states, and surfacing constraints.

Higher-priority emergency states can preempt routine states. Player override is
also represented as a state, rather than bypassing all unit logic.

## AI layers

There are at least three overlapping AI layers:

- Per-unit `AI`, which processes contacts, threats, weapon status, sensors,
  evasion, target selection, air-station behavior, and engagement execution.
- `Taskforce`, which coordinates alerts, contact reports, patrol/air support,
  VID, ASuW/ASW assignment, air strikes, missile groups, and shared weapons.
- A newer `SeaPowerAI` namespace containing pathfinding, objectives, sensors,
  classification, and debug-map types. Its `AIController.OnUpdate` is empty in
  this build, so it should not be assumed to replace the established unit and
  task-force AI.

Per-unit AI performs periodic work rather than every expensive decision every
frame. It maintains internal contacts, candidates to investigate/identify or
destroy, reacquisition cooldowns, defensive and cruise-missile target lists,
and preset attacks. It also handles AEW/CAP/MPA/strike launch decisions, weapon
preparation, auto-engagement, submarine missile volleys, and ECM positioning.

The task-force `WeaponAllocator` collects manual and automatic priorities for
tracks. Shooters bid on each unengaged target; the lowest-priced valid bid wins
and receives a releasable engagement handle. This is a local auction-style
coordination mechanism, not a global optimal planner.

## Doctrine and hostility

Units/task forces expose relations, alert state, EMCON/sensor state, rules of
engagement, and weapon status. In the live mission the bottom UI showed `Radio
Routine` and `Weapons Tight`. Gameplay options can also change behavior, such as
whether player units auto-attack surface targets under Weapons Free and whether
a search sensor is required for engagement.

Automated tests must record difficulty and gameplay options. Otherwise identical
mission files can produce different engagement behavior.

## Weapons

Launch is a multi-stage system, not a single spawn call. A launcher can:

- select compatible ammunition and a container/rail;
- request preparation and silently ready while waiting;
- align a mount or fixed-angle container and open hatches;
- reserve fire-control/guidance channels;
- launch against an object, datum, bearing, or offset;
- hand over guidance, reload/unload, and close containers;
- serialize loaded ammo, system state, and associated targets.

Per-unit AI accounts for range, target type, priority, existing committed
weapons, point-defense suitability, guidance capacity, and whether launch would
reveal a submarine. Task-force state tracks missile and torpedo target timing so
multiple shooters do not blindly overcommit.

Launched ordnance splits across two unrelated systems, and the naming invites
confusion:

- **Missiles, torpedoes, bombs, and decoys** derive from
  `WeaponBase : ObjectBase`. They are full tactical entities in
  `ObjectsManager._listOfAllObjects` and `_listOfAllWeapons`, updated by the same
  `GameMain` per-object loops as ships and aircraft, and moved by relocation
  events like any other object.
- **Gun projectiles** are `Projectile`, a plain class that is neither a
  `MonoBehaviour` nor an `ObjectBase`. They live only in
  `ProjectileManager._activeProjectiles` and are updated in
  `ProjectileManager.OnFixedUpdate`. `ProjectileManager` pools them by
  ammunition file name, reusing a destroyed instance where possible and stamping
  `_birthTime = GameTime.time`.

So an in-flight missile is visible to any enumeration of the object registry; an
in-flight gun round is not.

### Transient weapon state

A missile in flight carries a large amount of state that exists only in memory
and only for that flight. Representative fields on `Missile` and its seeker:

- `_flightStage` (launch, drop-from-aircraft, to-bearing, cruise, terminal, …);
- `_seeker._guidanceType`, `_seeker._activationTime`, `_terminalStartTime`,
  `_lostGuidanceTime`;
- `CurrentIntendedTargetObject`, `CurrentTarget`, `CurrentStrongestTargetEcho` —
  live references to other objects and to shared echo entries;
- `ConnectionLost` / `ConnectionLostForever` reactive properties, and
  `_requiresExternalGuidance` for semi-active and command-guided rounds;
- `_insDriftValue` and `_motorPerformance`, both drawn once at spawn from
  `Globals._unityRandom`, so they are per-round samples from a shared global
  stream;
- `_targetLost`, `_wasInMissileGroup`, and preset attack bookkeeping.

Two properties make this state hard to treat as portable. It is full of
references to other live objects rather than identifiers, and a lot of the
guidance logic compares against **absolute** `GameTime.time` — for example
`GameTime.time - _seeker._activationTime > 180f` and
`GameTime.time - _lostGuidanceTime > 10f`. Reproducing it elsewhere requires
reproducing the clock origin as well as the fields.

The game's native save format does attempt to preserve `WeaponBase` ordnance.
`SaveLoadManager.WriteWeaponData` enumerates each task force's weapon list and
calls `SaveStateToFile`; `SceneCreator.loadWeapons`/`initWeapons` reconstructs
weapon objects and launches them after their references are parsed. `WeaponBase`,
`Missile`, `Torpedo`, and `Bomb` provide save/load state paths. Chaff and
noisemaker objects are excluded from the task-force weapon collection used by
this writer.

That code evidence is not proof that every transient field and live relationship
above survives a mid-flight round trip with the same outcome. Gun `Projectile`
instances are a separate pooled system and are not covered by the task-force
weapon path. Crimean Tom therefore needs live missile, torpedo, bomb, guidance,
target, and gun-projectile experiments before declaring a handoff portable or
making weapons a permanent migration fence.

## Damage and survivability

Damage is spatial and system-oriented:

- ammunition defines penetration class and impact size;
- blast/penetration processing raycasts through colliders and armor;
- hit zones and `BaseSystem` instances lose integrity;
- ship compartments track port/starboard integrity and flooding;
- fires, secondary explosions, flooding rate, damage-control teams, and delayed
  sinking can continue after the initial hit;
- propulsion, sensors, weapons, magazines, flight-deck systems, and visual
  damaged models can be affected independently.

The compartment model derives integrity partly from displacement and ship type,
then divides it among compartments. Armor can block or reduce penetration;
surviving penetration distributes damage and can start progressive casualties.

Per-system integrity is stepped from `ObjectBase.OnLazyUpdate`, which walks
`_obp._systems` calling `BaseSystem.OnUpdate()` when not paused and recomputes
`_overallDamage` as the summed integrity deficit. Damage is therefore not a
single event applied at impact: it is a state that keeps evolving for as long as
the object is simulated. A snapshot taken shortly after a hit and one taken a
minute later describe genuinely different states, and the path between them
depends on elapsed simulated time.

Campaign persistence should therefore distinguish at least:

- destroyed/lost;
- heavy damage that cannot be repaired in theater;
- repairable structural/system damage;
- ammunition expenditure, which task-force-mode repair explicitly does not
  replenish;
- hosted aircraft losses and remaining air-group quantities;
- crew experience/survival progression.

## Flight operations

Ships/land facilities with `FlightDeck` track supported air vehicles,
squadrons, assignments, loadouts, deck and crew utilization, readiness,
launch/recovery tasks, and active flight operations. Aircraft can return due to
bingo fuel or winchester state.

The live Flight Deck panel exposed capacity, utilization, assigned aircraft,
loadout/callsign, ready/launch state, queued operations, recover-all, abort, and
launch actions. A dynamic campaign needs a separate strategic readiness model;
it should instantiate aircraft tactically only while their assignment or
interaction requires it.

## Mission outcomes

Objectives and triggers turn tactical state into player-facing results. The
After Action Report observed outcome, objective statuses, enemy losses, allied
units/damage, neutral penalties, rescued survivors, score, and efficiency.

Those fields are useful outcome vocabulary, but the continuous campaign needs
stable per-instance IDs, ordered tactical events, and periodic exact supported
stores/damage checkpoints. The plugin should capture them during tactical play
and at quiesce boundaries rather than wait for mission teardown or scrape the
rendered report.
