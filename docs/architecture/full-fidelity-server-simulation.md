# Full-fidelity server simulation

## Decision

The campaign service must eventually simulate every supported entity with the
same consequential mechanics that Sea Power applies in a native battle. The
server implementation does not need Unity, rendering, audio, animation, or the
same class structure. It does need compatible state transitions and combat
results.

"Operational" describes an authority location and update schedule. It does not
permit a lower-fidelity combat model. Sea Power remains the reference
implementation for the pinned game build. A server-resident engagement and a
matched native engagement should agree within a declared tolerance when they
start from the same state and receive the same orders and random inputs.

The current server does not meet this requirement. Its combat model proves that
the campaign can move forces and produce plausible detections, launches,
countermeasures, damage, destruction, and withdrawals. Those results are not
yet equivalent to Sea Power.

## What full fidelity means

Full fidelity requires parity in four areas.

1. **State parity.** Both simulators can represent the state that affects future
   outcomes. This includes pose, motion, fuel, stores, ammunition by magazine,
   sensor and emitter state, tracks, damage, component failures, formations,
   hosted assets, launched weapons, mission state, and relevant environment.
2. **Mechanical parity.** Detection, classification, tracking, guidance,
   interception, damage, repair, navigation, fuel use, aviation, and tactical AI
   follow compatible rules.
3. **Timing parity.** The order and timing of consequential events remain within
   a declared tolerance. A server weapon cannot hit before a native weapon could
   launch or remain guided after its support ended.
4. **Handoff parity.** An entity may move between server and Sea Power authority
   at any supported checkpoint without gaining or losing combat capability or
   changing its likely outcome merely because authority moved.

Exact floating-point identity is not required unless a mechanic depends on it.
Every accepted difference needs a measured tolerance, a versioned calibration
rule, or an explicit unsupported-state rejection. "Plausible" is not a parity
criterion.

## Work the server may skip

The server may sleep or replace expensive work when it can preserve the same
result. This is a scheduling optimization, not a second simulation model.

Examples include:

- skip collision broad-phase and narrow-phase work while reachable bounds cannot
  overlap;
- stop evaluating a disabled sensor or an emitter with no possible target in
  its horizon and propagation envelope;
- schedule the next fuel, repair, reload, or damage transition instead of
  updating an unchanged value every frame;
- sleep tactical AI until an order, track, threat, fuel threshold, formation
  change, or scheduled decision time can alter its choice;
- use an analytic trajectory between events when it reproduces the reference
  path and can be invalidated by guidance, target motion, terrain, weather, or
  damage changes; and
- omit presentation-only Unity work such as rendering, animation, audio,
  particles, camera behavior, and UI updates.

A sleeping subsystem must retain enough state to resume without a discontinuity.
The scheduler must wake it before the earliest event that could change its
result. At minimum, wake conditions include a new order, movement into a sensor,
weapon, support, or collision envelope, an emitter change, weapon launch,
damage, terrain or weather change, fuel or ammunition threshold, host operation,
authority handoff, and a player request to inspect or control the entity.

If the server cannot prove that a shortcut preserves the result, it must run the
full mechanic or reject that simulation state. It must not silently substitute a
statistical outcome.

## Authority model

The campaign service remains authoritative for all entities outside active Sea
Power interaction islands. Sea Power receives one renewable, epoch-fenced lease
for each loaded island and becomes authoritative for that island's simulation.
The server freezes authoritative advancement for those entities, though it may
run a non-publishing shadow comparison for diagnostics.

Periodic tactical updates are for recovery, monitoring, and bounded persistence.
They are not remote-control inputs. Native Sea Power objects should run on their
own between synchronization points. On return, the service accepts a complete
portable checkpoint and resumes the same mechanics from that state.

## Cross-authority interactions

Full fidelity does not stop at an entity boundary. A result can depend on the
shooter, target, weapon, supporting sensor, datalink, jammer, formation, host,
terrain, and environment. Crimean Tom must not let Sea Power and the server each
resolve one half without a shared mechanic.

Each consequential relationship needs one of these policies:

1. move the complete dependency set into the same interaction island;
2. transfer authority for an in-progress object through a lossless checkpoint;
3. use a boundary adapter that has passed the same matched parity tests as the
   underlying mechanic; or
4. reject or delay the interaction until one of those paths is safe.

A map track may cross the boundary as presentation data. A sensor detection,
fire-control solution, guidance update, weapon impact, collision, formation
constraint, or hosted launch is simulation data and needs an explicit policy.
For example, an in-flight missile and every source required for its guidance
must have one authority. A remote target can remain server-owned only if a
validated adapter gives the local Sea Power sensor and weapon chain the same
inputs and effects it would receive from a native target.

Promoting every sensor contact would make an island spread across the theater.
The interaction-island registry therefore needs versioned, mechanic-specific
rules that distinguish a presentation track from an unsafe simulation
dependency. Until a boundary adapter passes, the conservative rule keeps the
whole dependency in one authority.

## Gap inventory

The table records the work between the current server and the target. It is the
canonical parity backlog. Current implementation details belong in the
[status ledger](../planning/current-status.md).

| Area | Current gap | Required result |
| --- | --- | --- |
| Time and numerics | The server uses one-second campaign quanta and coarse Operational updates. Sea Power uses native frame and fixed-step timing. | Define sub-quantum event ordering, integration rules, random streams, tolerances, and deterministic replay for consequential mechanics. |
| Geographic model | Server movement uses geographic campaign coordinates and broad regions. | Match Sea Power position, altitude, depth, heading, speed, turn, acceleration, terrain, water, and floating-origin conversions at handoff boundaries. |
| Environment | Weather affects some Operational calculations, but the server does not reproduce Sea Power's complete local environment. | Carry the environmental fields used by sensors, flight, sea state, acoustics, weapons, and damage. Version their sampling and interpolation. |
| Navigation and formation | Groups move and receive domain orders, but native route following, station keeping, avoidance, and formation behavior are not reproduced. | Match waypoint execution, speed and turn limits, formation offsets, terrain and depth constraints, avoidance, and order completion. |
| Collision | The server does not run Sea Power-equivalent collision and grounding behavior. | Add spatial bounds, swept collision tests, terrain and seabed checks, collision damage, and safe sleeping when no reachable bounds overlap. |
| Sensors | Radar detection range now comes from the unit's own radars in the game catalog, by declared range and role. Sensor modes, propagation, scan timing, altitude limits, line of sight, target-channel limits, radar cross-section, and classification are still approximate, and sonar and visual channels keep server-invented horizons. | Model each supported radar, sonar, visual, infrared, and electronic-support sensor with compatible modes, coverage, scan timing, environment, probability, and state. |
| Track management | Coalition intelligence exists, but native track quality, correlation, classification, uncertainty, aging, and loss are incomplete. | Preserve the evidence and update rules that determine what each side knows and what weapons may target. |
| Emissions and electronic warfare | EMCON, jamming, decoys, and countermeasures do not have broad native parity. | Model emitters, detection of emissions, jamming geometry, burn-through, deception, decoy selection, home-on-jam behavior, and countermeasure timing. |
| Weapons and fire control | Operational weapons now persist Sea Power-derived type, minimum and maximum range, guidance, speed, seeker range, launch reliability, hit probability, and warhead power. Complete and incomplete catalog profiles are distinguished. The resolver uses those fields where present, but it does not yet reproduce full launch gates, acceleration, trajectories, support, interception, or fuzes. Magazine selection now works on native per-ammunition round counts, so an exhausted magazine removes its weapon from selection, and re-engagement is gated by the mount's native reload time and salvo size. Each mount reloads independently, so mounts cycle out of phase. Simultaneous guided engagements are bounded by native fire-control channel counts. | Finish the incomplete catalog profiles, then implement launch doctrine, kinematics, guidance phases, datalinks, seeker behavior, support loss, interception, fuzes, misses, and impact. |
| Point defense | Point defense now picks a real interceptor, consumes its magazine, and is bounded by the defender's native fire-control channel counts. Reaction time, doctrine, and per-target channel occupancy are incomplete. | Reproduce compatible engagement scheduling, per-target channel occupancy, reaction delays, weapon choice, and saturation behavior. |
| Damage and survivability | Damage, disabling, destruction, and withdrawal exist at an aggregate level. | Match hit location where relevant, warhead effects, flooding, fire, component failures, mobility and sensor kills, magazine effects, repair, abandonment, and sinking or crash progression. |
| Fuel, stores, and readiness | The campaign tracks aggregate fuel and stores. Magazines are now per-ammunition round counts sourced from the game catalog, consumed per engagement, rearmed from base munitions, carried across the handoff boundary, and persisted across simulation ticks. Per-mount reload timing is tracked. Maintenance, crew readiness, and the remaining native resources are still absent. | Track platform-specific consumption, maintenance, crew readiness, damage-control resources, and support transfers. Qualify magazine and reload behavior against a matched native run. |
| Surface warfare | Ships can move, detect, fire, take damage, and withdraw in the server model. | Add native-compatible radar, sonar, fire-control, missile, gun, torpedo, countermeasure, formation, helicopter-support, and damage behavior per supported class. |
| Subsurface warfare | Submarines exist in campaign combat, but the acoustic and depth model is not Sea Power-equivalent. | Model depth, layers, cavitation, passive and active sonar, convergence and shadow effects where used, towed arrays, torpedoes, countermeasures, evasion, and battery or propulsion limits. |
| Fixed-wing aviation | Air groups are persistent Operational entities rather than aircraft inventories executing native-like sorties. | Add aircraft inventory, runway and deck launch, loadout, climb, cruise, formation, mission phases, tanker use, combat, diversion, recovery, turnaround, maintenance, and losses. |
| Helicopters | Rotary-wing groups have domain control, but launch, hover, dipping sonar, sonobuoys, host limits, fuel, and recovery lack full parity. | Implement host scheduling, flight profiles, sensors, weapons, fuel reserves, recovery, and native-compatible mission behavior. |
| Carriers and airbases | Hosted launch works for a narrow Sea Power path. The server has no complete deck, runway, parking, sortie, recovery, or turnaround cycle. | Represent inventories and facilities, capacity, launch and recovery queues, deck and runway state, damage, loadout preparation, tanker and AEW support, and diversion. |
| Ground units and installations | Ground, GBAD, radar, airbase, logistics, ballistic-missile, and installation entities exist, but most are targets or approximate combatants. | Add terrain-aware movement and placement, radar and fire-control networks, launcher and reload state, engagement zones, ballistic-missile preparation and flight, dispersal, camouflage, suppression, damage, and repair. |
| Command, control, and datalinks | Coalition AI and intelligence share campaign state more broadly than native units may be able to communicate. | Model communication reach, latency, loss, identification, track sharing, weapon support, command degradation, and doctrine. Prevent omniscient coordination. |
| Tactical AI and doctrine | Domain controllers create rational packages, but they do not reproduce Sea Power tactical decisions. | Define per-platform and per-mission doctrine for search, emissions, targeting, weapon choice, evasion, support, disengagement, and recovery. |
| Neutral and civilian behavior | Civilian traffic is present, but navigation, identification, avoidance, escalation, and collateral effects are incomplete. | Apply compatible movement and rules for neutral detection, classification, collision avoidance, engagement restrictions, damage, and campaign consequences. |
| Portable state | Pose, damage, per-ammunition magazines, aggregate resources, hosted relations, and selected loadouts work for a narrow platform set. | Define a versioned, lossless contract for every consequential field. Reject handoff before authority changes when a required field cannot cross. |
| Cross-authority interactions | The island registry can join dependencies, but there is no broad parity-qualified adapter for sensors, datalinks, jamming, fire control, in-flight weapons, collision, or support across the Sea Power boundary. | Give each mechanic a versioned boundary policy. Keep unsafe dependencies under one authority, support lossless in-progress handoff where possible, and qualify adapters against an all-native reference. |
| Catalog coverage | The server catalog and Runtime Bridge support only part of the installed game's units, variants, loadouts, sensors, and weapons. | Generate or maintain versioned mappings for every supported platform and dependency, with exact compatibility diagnostics after a game update. |
| Fidelity scheduler | Interaction islands and authority leases exist, but the server has no general mechanic-level sleep scheduler. | Track dependency envelopes, next-event times, invalidation causes, wake deadlines, and diagnostic reasons for every sleeping subsystem. |
| Validation | Current tests show that native and streamed Sea Power units fight, not that the server matches Sea Power. | Run matched server and native engagements from the same initial state. Compare event sequence, timing, tracks, ammunition, fuel, damage, and final state across fixed seeds. |
| Performance | The current server is cheap partly because it omits native mechanics. | Measure cost only after each mechanic passes parity. Report active and sleeping subsystems, events processed, wakeups, memory, and error against the native baseline. |
| Versioning | The campaign schema and game fingerprint are versioned, but calibration and semantic compatibility are incomplete. | Bind mechanics, catalog mappings, tolerances, and calibration data to the pinned Sea Power build and migrate saves without changing past results silently. |

## Validation rules

The existing Combined combat scenario compares authored native Sea Power units
with units streamed into Sea Power. It validates materialization and native
combat readiness. It does not validate server simulation parity.

Server parity needs a matched-run harness:

1. capture one initial state, orders, environment, doctrine, and random seed;
2. run the engagement under server authority without Sea Power publishing;
3. run the same engagement natively in Sea Power;
4. compare event order and timing, tracks, movement, fuel, ammunition, weapon
   state, damage, losses, and final checkpoints;
5. classify each difference as a defect, a measured tolerance, a versioned
   calibration rule, or an unsupported mechanic; and
6. repeat across seeds and adversarial geometries before claiming parity for a
   platform family.

Aggregate counts such as "both sides launched" are smoke checks. They do not
prove parity. A platform family is supported for server authority only when its
required mechanics pass the matched portfolio and its state can cross the Sea
Power handoff boundary without loss.

## Delivery order

1. Freeze the portable state and event vocabulary needed by the first combined
   naval and air engagement.
2. Capture native reference traces for movement, sensing, weapons, damage, fuel,
   and tactical decisions.
3. Replace aggregate server rules with compatible mechanics for that bounded
   platform set.
4. Add the sleep scheduler after the unslept implementation passes. Compare
   sleeping and always-awake server runs before comparing either with Sea Power.
5. Expand by dependency, not by unit count. A new platform is not supported
   until its sensors, weapons, hosted assets, damage, AI, and portable state are
   supported too.
6. Keep Sea Power as the authority for any interaction island that contains an
   unsupported consequential mechanic.
