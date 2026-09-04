# Sea Power mission-generation tooling

This is the working reference for generated Crimean Tom scenarios. It is based on the mission parser and editor in the currently installed game, checked against shipped standalone and campaign scenarios.

The generator should first produce small missions that reliably load and fight. More elaborate contracts should be assembled from the same tested pieces instead of introducing a separate simulation layer.

## First playable slice: fleet clash

The initial generator writes two versions of one engagement into a native `Crimean Tom Generated` scenario category:

- NATO command: the player controls the western surface action group.
- Soviet command: the player controls the same engagement from the other side.

Each side receives four combatants selected from role-based class pools. Both groups start in a box formation, steam toward one another, use the `AntiSurface` tactical mission, and have free weapon release. Three losses break a group's combat cohesion and end the mission. The generated pair shares one seed and identical forces, weather, time, spacing, and geometry.

This slice proves the parts every later contract needs:

- native scenario discovery and direct launch;
- repeatable random order-of-battle generation;
- player and opposing-force side swapping;
- formation membership and station roles;
- AI movement, detection, targeting, and weapon employment;
- objectives, victory, defeat, and delayed mission end;
- morale-aware withdrawal toward side-specific retreat points.

Do not add aircraft, submarines, reinforcements, or multi-stage triggers until this loop works in both directions.

## Scenario structure

### Category metadata

A folder below `StreamingAssets/user/missions` becomes a native scenario category when it contains `_info.ini`:

```ini
[General]
Type=Scenario

[Language_en]
Name=Crimean Tom Generated
Description=Generated contract scenarios for direct play and debugging.
```

The paired scenario files then appear in the normal Scenarios screen. No menu patch is required.

### Environment

Useful `[Environment]` fields include:

- `Date` and `Time`;
- `SeaState`, normally kept between 1 and 10;
- `WindDirection`;
- `Clouds` and precipitation or visibility settings;
- `MapCenterLatitude`, `MapCenterLongitude`, and `MapInitialZoom`;
- `LoadBackgroundData` and `LayerDisabled`.

Weather randomization should be bounded by mission type. Rough weather can materially change aircraft, small craft, sensors, and weapon employment, so it is a scenario rule rather than decoration.

### Mission and sides

The `[Mission]` section defines the player, enemy, and optional neutral taskforces, nations, unit counts, formations, triggers, routes, ROE, and simulation switches.

Important controls are:

- `PlayerTaskforce`, `EnemyTaskforce`, and `NeutralTaskforce`;
- `TaskforceX_Nation`;
- unit counts split across vessels, submarines, aircraft, helicopters, land units, biologics, life rafts, and weapons;
- `TaskforceX_NumberOfFormations` and formation records;
- `NumberOfTriggers`;
- `AllowMoraleToAffectAI`;
- per-side ROE and relative-position scales.

Debug switches exist for controlling enemy units, disabling enemy AI, changing enemy weapon state, revealing unidentified units, and weapon testing. Generated playable missions should leave them at normal gameplay defaults. A deliberately named diagnostic scenario may opt into them.

## Order-of-battle generation

### Unit selection

A unit `Type` may be one class or a comma-separated random choice. `VariantReference` can select a named variant, `Default`, or `Random`. The external generator should still resolve the broad force composition so paired scenarios use the same OOB and can record an exact seed.

Select units by role before selecting a class:

1. determine mission-required capabilities;
2. reserve core roles such as AAW, ASW, strike, escort, transport, or minesweeping;
3. choose a valid class from each role pool;
4. check the resulting force for required sensors, weapons, endurance, and launch facilities;
5. reject combinations that cannot accomplish the mission.

Raw ship count is not a useful balance measure. Later fleet-clash versions should compare anti-ship reach, magazine depth, area air defence, ASW coverage, aviation, sensor quality, speed, and survivability. The simple first slice instead uses curated peers.

### Unit state and behavior

Useful unit fields include:

- `Controllable`, `Undetectable`, `SpawnChance`, and `SetSelected`;
- `VisualSystemsOn`, `RadarsActive`, `ActiveSonarsEnabled`, and `TowedArrayDeployed`;
- `WeaponStatus`, `OverrideWeaponStatus`, and `MissionType`;
- `CrewSkill` and `Morale`;
- `RetreatAfterWeaponsExpended`;
- `Stores`, `Damage`, system availability, weapon loadouts, and magazine contents;
- `HomeBase`, `TargetObject`, and `CurrentState`;
- `AutomateRoute`, `Telegraph`, and `VelocityInKnots`;
- `IsValuableUnit`, `IdentifySelf`, and `UnlimitedFuel`.

Use current tactical mission values. For surface groups, `AntiSurface` is the dependable choice. Other current values cover air, ships, carriers, transports, landing ships, ground targets, air defence, submarines, and patrol. Some old shipped files use strategic labels such as `ASuWSweep` in a unit's `MissionType`; do not copy those into new output without an in-game test.

Avoid `OverrideSingleUnitBehaviourTreePath` in generated missions. It couples content to internal AI assets and is harder to keep compatible than tasking units through supported mission fields, routes, formations, and triggers.

### Placement and routes

Units can use absolute `GeoPosition`, taskforce-scaled `RelativePositionInNM`, or a position relative to another unit. Random spawn centers, ranges, and directions are available. Heading can be fixed, inherited from a parent, or randomized.

Waypoint forms include:

- absolute world points;
- course or turn plus distance;
- bounded random turns;
- movement to another unit's spawn position;
- fixed or loose relative follow modes;
- looping routes.

Waypoint actions can change telegraph, sensor state, weapon status, identification behavior, formation membership, or launch a planned attack. Use those actions for doctrine changes at a geographical phase boundary, not to micromanage every engagement.

The documented `ToCurrentPosition` token does not match the spelling accepted by the current parser. Avoid it until a minimal live test confirms the actual behavior.

## Formations and station roles

Formation records contain a leader, member references, a name, a pattern, spacing, and an optional spawn-position override. Stable patterns include loose, circle, vic, echelon, line abreast, line astern, and box.

`Convoy` is marked unfinished in shipped documentation. `Battlegroup` has Instant Action-specific behavior. Generated scenarios should use simpler patterns until those two are proven independently.

Station roles include `Core`, `PlaneGuard`, `AAW`, `ASW`, `BattlegroupSSN`, `MPA`, `AEW`, `CAP`, `Intercept`, and `HeloASW`. Assign roles from unit capability. A formation label alone does not make the AI screen or coordinate well.

## Making the AI behave deliberately

The most dependable AI recipe is:

1. Give every independent AI group a route that creates contact or serves its objective.
2. Use a current tactical `MissionType` to limit target selection to the intended domain.
3. Put cooperating units in a formation and assign meaningful station roles.
4. Set an explicit weapon status. Combat opposition normally needs `Free`.
5. Let the normal AI radar routine manage emissions unless the scenario has a specific EMCON plan.
6. Use waypoint sensor actions when doctrine should change at a known phase or location.
7. Use explicit targets or `AttackAtWaypoint` for deliberate strikes, not routine self-defence.
8. Enable morale and provide retreat anchors when withdrawal is a valid outcome.
9. Use triggers to change mission phases and constraints; do not replace the AI's tactical loop with scripted target-by-target orders.

AI quality comes mainly from giving groups a coherent purpose, a reachable route, suitable sensors and weapons, and clear release authority. Random units dropped into an area without those pieces will often look passive or irrational.

## Triggers and conditions

Triggers can start enabled or disabled, repeat, complete, fail, and reactivate. Named conditions are combined with expressions using `AND`, `OR`, `NOT`, and parentheses.

Supported condition types include:

| Condition | Typical generator use |
| --- | --- |
| `OnMissionStart` | Initial orders, setup, or delayed subsystem activation |
| `Time` | Briefings, reinforcements, weather changes, and deadlines |
| `UnitDestroyed` | Attrition thresholds, escort loss, and high-value target kills |
| `UnitsInTheArea` | Passage, landing, interception, withdrawal, and patrol checks |
| `HasNoUnitsOfType` | Domain clearance and simple elimination outcomes |
| `UnitDetected` | Search-to-contact phase changes |
| `UnitClassified` | Identification-dependent ROE or reporting |
| `UnitsAreOutOfAmmo` | Mission-kill or withdrawal logic |
| `UnitHasNoGuidanceSystems` | Sensor or fire-control mission kills |
| `UnitDamageState` | Damage thresholds and disabled system checks |
| `NoIncomingWeapons` | Safe disengagement or post-attack transitions |
| `VariableCheck` | Stateful contracts and counters |
| `TriggerCompleted` / `TriggerFailed` | Chained phases and alternate outcomes |
| `JoinedCondition` | Reusable compound state |

Stock scenarios lean most heavily on time, destruction, unit-type elimination, and area checks. Detection, damage, ammunition, guidance, variables, and joined conditions are supported by the current parser but should enter generated content one feature at a time with live verification.

### Reusable trigger patterns

Search and engage:

1. begin under restrictive ROE or EMCON;
2. complete a detection or classification condition;
3. show new orders and change sensors or weapon status;
4. activate the engagement and outcome triggers.

Reinforcement:

1. author disabled units with a route and formation;
2. fire on time, area entry, damage, or a variable threshold;
3. enable or teleport the group, restore its systems, and reveal it as appropriate;
4. activate objectives involving that group.

Withdrawal:

1. detect damage, ammunition exhaustion, mission completion, or morale loss;
2. disband or reform the group if needed;
3. replace its route toward a same-side retreat anchor;
4. score survival when enough units reach the withdrawal area.

Changing conditions:

1. use a time or variable gate;
2. update sea state, cloud, wind, precipitation, fog, or time of day;
3. issue a forecast or message before the change when the player should be able to plan around it.

## Trigger actions

The current parser exposes actions in these useful groups:

- variables: set, add, multiply, divide, and invert Boolean;
- unit state: enable, disable, reveal, detect, classify, identify, transfer sides, despawn, or destroy;
- sustainment: repair, rearm, prepare flight deck, or enable flight-deck AI;
- systems: toggle sensors or whole propulsion, sensor, and weapon groups, including named sections;
- movement: teleport, move relative to another object, change heading or speed, replace waypoints, reform, or disband formation;
- environment: change time, sea state, clouds, wind, rain, snow, fog, or thunderstorm;
- information: messages, naval-message templates, voice messages, free-content panels, taskforce intelligence, and forecasts;
- objectives: complete, fail, hide, unhide, or cancel;
- outcome: declare victory and end the mission after a delay;
- air strikes: choose source, target or position, radius, force size, power, aircraft, loadouts, and partial-strike rules.

Actions should be used to express operational events. If a trigger continually teleports units or rewrites their behavior to force an expected result, the generated scenario is too scripted.

## Objectives and scoring

Each taskforce has an objective section. An objective supplies completion score, failure score, optional initial state, and main or hidden status. Localized text uses the objective identifier.

Generate objectives from the player's side of the contract rather than merely mirroring labels. The same route action might become:

- escort and preserve the convoy for the defender;
- locate and interdict the convoy for the attacker.

Hidden objectives are useful for optional intelligence, civilian protection, avoiding escalation, or preserving force. Main objectives should describe the decisive result plainly.

## Briefing and map tooling

Mission map symbols can create labels, unit icons, threat arrows, circles, ovals, rectangles, and polygons. They support side visibility, domain, color, border, size, position, and visibility in the campaign map, briefing map, or tactical mission.

Zones can mark deployment, patrol, or spawn areas using polygons, circles, ovals, rectangles, or squares. They can constrain allowed unit types and use fill, gradient, hatch, grid, or cross patterns.

Use generated map layers instead of custom artwork for briefing diagrams. Show only operational knowledge the player's side should possess: expected threat axes, patrol boxes, convoy routes, exclusion zones, and objectives. Do not disclose randomized enemy spawns unless the contract calls for exact intelligence.

Gameplay anchors are separate from decorative map symbols. They provide side-specific geographical anchors, including retreat points used by morale behavior.

## Other dynamic tools

Civilian routes can repeatedly spawn neutral or combatant traffic between endpoints. A route can restrict unit types, nations, classes, and loadouts; control group size and interval; set radar state; and enable weapons at a waypoint. This is useful for merchant density, fishing traffic, scheduled logistics, and some reinforcement patterns.

`SpawnChance`, random type lists, variant selection, random spawn areas, course variation, weather, time, and trigger branches provide enough variation for replayable contracts. Apply randomness in layers:

- preserve the mission's required capabilities;
- randomize equivalent classes and supporting units;
- randomize within tactically valid deployment areas;
- vary intelligence accuracy and optional events;
- keep the paired player-side versions on the same seed.

Every generated file should include its seed in the description or briefing. A failed mission must be reproducible.

## Development sequence

After the fleet clash works in both directions:

1. add aircraft to the same surface engagement and validate launch, tasking, recovery, and opposing air defence;
2. add one reinforcement trigger and one disabled reserve group;
3. add asymmetric intelligence and a detection-driven ROE change;
4. turn the same geometry into symmetric escort/interdiction variants;
5. add submarines and ASW only after surface and air behavior is dependable;
6. add randomized weather changes, damage states, rearm, and repair as independently testable modules;
7. connect generated scenario results to the campaign contract and post-battle systems.

This keeps scenario generation focused on playable contracts while steadily expanding the set of proven mission primitives.
