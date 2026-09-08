# Fleet generation

> Design reference. See [development-guide.md](development-guide.md) for the implemented architecture, verification status, and current risks.

Crimean Tom fleet generation should produce task groups that look as if a naval staff assembled them for a real operation. It should not select a bag of individually strong ships, and it should not return the same optimal formation every time.

The generator therefore works in layers:

1. choose a theater and its plausible participants;
2. choose a lead navy for each force;
3. fill a mission-specific formation blueprint;
4. draw most ships from the lead navy and nearby regional partners;
5. occasionally attach a ship from a more distant ally;
6. generate many complete formations;
7. pair formations by point cost and relevant combat capabilities;
8. resolve exact national variants, split the force into tactical groups, and write the Sea Power scenario.

The scenario seed, selected theater, nations, classes, variants, and balance measurements must be logged. A surprising or broken result must be reproducible.

## Chaos mode

The skirmish setup has a Chaos mode checkbox, off by default, for fleet clash and route operations, including observers. Each nation, including normally unaligned nations, rolls Blue or Red independently. A nation cannot belong to both sides. Rolls use the scenario seed; Reroll creates a new split.

Local nations still receive the usual regional preference, but every nation assigned to that side can provide allied units regardless of its normal alignment. If a side has no usable local fleet, it draws from its full assigned roster. Invalid splits are rerolled up to 32 times to obtain usable forces on both sides. Fleet balancing, composition choices and difficulty remain in effect.

Nearby ships, submarines, coastal sites and independent aircraft use the same assignments. The generated INI records `ChaosMode`, `BlueNations` and `RedNations` in its `CrimeanTom` section. Normal scenario alignments and campaign diplomacy are unchanged.

## Design goals

### Believable composition

A task group should have a recognizable center. One navy normally supplies the command ship and a substantial part of the formation. Other coalition members appear mainly as escorts or specialists.

For example, a small Norwegian Sea force might contain:

- a Norwegian command frigate;
- a Norwegian strike-capable combatant;
- a Danish or Swedish escort.

It should usually not contain three unrelated ships drawn uniformly from every NATO member.

### Regional character

The theater determines the first pool of available nations. Small engagements are the most local. Medium and large engagements progressively admit alliance reinforcements.

Regional does not mean nationally pure. Nearby partners should be the main source of small-force variety, especially for the escort and specialist positions. A Nordic force should look Nordic before it looks generically NATO.

### Rare allied detachments

A ship from a distant ally is always possible when the alliance can supply one. This probability is deliberately small in a minor engagement and rises with force size.

This is how a Spanish destroyer, British frigate, or American cruiser can occasionally appear in a Nordic fight. Such a ship should occupy a role that it can plausibly perform. It is an attached detachment, not an unrestricted wildcard.

### Useful variety

Variety comes from several independent decisions:

- theater;
- lead navy;
- participating regional partners;
- distant allied attachment;
- class choice for each formation role;
- exact national variant;
- time, sea state, cloud, wind, starting separation, and lateral geometry.

The generator penalizes repeated classes but does not prohibit them. Two ships of one class are believable. A formation of nothing but the highest-scoring class usually is not.

### Fair opposition

Equal ship counts do not imply equal forces. The final pair is compared using an approximate point cost derived from Sea Power's authored AI unit value and the game's authored capability values.

The current fleet-clash comparison includes:

- approximate Task Force point cost;
- anti-surface capability;
- air-defence capability;
- ASW capability;
- the number of missiles required to saturate the ships' defences, used as a resilience proxy.

Surface strike and air defence remain the largest weights, while submarine selection now makes ASW capability operationally relevant.

## Theater model

A theater contains only its name and map center. Nation definitions supply the
participants. Each nation has a scenario alignment, local theaters, and reinforcement
theaters. The generator builds opposing force plans from those entries.

The initial theater catalog includes:

| Theater | First regional force | Second regional force | Typical escalation |
| --- | --- | --- | --- |
| North Atlantic | UK, Canada, France, Germany, Netherlands, and Norway | Soviet Navy | US, wider NATO, Commonwealth, and Warsaw Pact detachments |
| Norwegian Sea | Denmark, Norway, and Sweden | Soviet Northern Fleet | wider NATO and Warsaw Pact support |
| Baltic Sea | Germany and Scandinavia | East Germany and Poland | NATO and Soviet reinforcement |
| Eastern Mediterranean | Greece, Israel, and Turkey | Egypt, Libya, and Syria | NATO, Arab League, Soviet, or Warsaw Pact support |
| South China Sea | Philippines, South Korea, and South Vietnam | China and Vietnam | Pacific, Commonwealth, Soviet, or Warsaw Pact support |
| Arabian Sea | India and Pakistan | Iran, Iraq, Kuwait, Saudi Arabia, and South Yemen | wider aligned support |

The North Atlantic appears more than once in the weighted catalog. This preserves the expected NATO-versus-Soviet fight as a common result without making it the only result.

`Fleet/FactionCatalog.cs` holds nations, coalitions, and theater locations together.
`FactionDefinition` represents either a nation or a coalition. A nation's membership
contains just itself; a coalition lists its member nations. Commander screens still
distinguish the two, so national options keep native flags and coalition tabs are not
duplicated for individual nations.

Nations are named instances such as `FactionCatalog.Denmark`. Coalition declarations
reference those instances, for example `[Denmark, Norway, Sweden]`, and inherit
theaters directly from them. String nation IDs remain for native roster filters and
saved campaign data. Declare nations before coalitions so their references are ready
during static initialization; keep the `Nations` list order stable for seeded fleets.

Nations own their theater assignments. Coalitions inherit the union of their members'
local and reinforcement theaters. Optional coalition theater additions cover edge
cases; the current coalitions need none. Inherited coverage means some members are
active there, not that every member is local. Fleet generation always checks nations,
so adding a country to a coalition does not automatically deploy it in that coalition's
other regions. India and Pakistan remain local in the Arabian Sea, while Britain and
Australia can reinforce them.

The Western and Eastern scenario alignments preserve the existing opposing pools.
They describe scenario sides, not strict historical alliances. Coalition membership
does not override a nation's alignment. Nations without theater assignments remain
available through the campaign roster but do not enter generated fleets.

Both North Atlantic locations retain their separate reinforcement pools. The
nation-owned catalog preserves country eligibility and selection weights, but its
new enumeration order changes the fleets produced by older seeds. New runs remain
repeatable. Side labels come from a sole regional nation or the smallest coalition
covering the regional participants, with an alignment label as a fallback.

## Force sources

Every ship candidate belongs to one of three sources relative to its generated force.

### Lead navy

The lead navy supplies command and doctrinal cohesion. Command ships strongly prefer this source. Strike ships also favor it, but less absolutely.

The lead navy is selected from the theater's regional nations, not from the entire alliance. A small regional scenario therefore cannot accidentally become American merely because the American roster is large.

### Regional partner

A regional partner is another member of the theater's local force. It is different from a distant allied detachment.

Regional partners receive strong preference for escort slots:

| Size | Escort preference for regional partner |
| --- | ---: |
| Small | 70% |
| Medium | 60% |
| Large | 50% |

Non-command specialist slots also admit regional partners. Command changes nationality only rarely.

This mechanism matters because many smaller navies have only one or two usable classes. The generator can preserve a regional identity without requiring one nation to provide an implausibly complete fleet.

### Distant allied detachment

A distant allied pool contains same-alignment nations whose reinforcement list
includes the selected location, excluding nations already counted as regional.

The base per-slot attachment chance is:

| Size | Normal slot | Escort slot |
| --- | ---: | ---: |
| Small | 5% | 10% |
| Medium | 16% | 21% |
| Large | 30% | 35% |

An allied selection still passes through role scoring. A distant patrol craft should not replace a command cruiser merely because the allied roll succeeded.

These are starting values. They should be tuned from generated-fleet samples rather than from a handful of memorable outcomes.

## Formation blueprints

A blueprint describes jobs, not exact ship classes.

### Small fleet clash: three combatants

1. command;
2. surface strike;
3. escort.

This creates a compact national or regional action group. The escort slot is the strongest entry point for a neighboring navy or rare distant attachment.

### Medium fleet clash: five combatants

1. command;
2. area or local air defence;
3. surface strike;
4. ASW;
5. escort.

This is the first formation expected to contain a meaningful coalition mix. It still keeps a clear lead navy.

### Large fleet clash: eight combatants

1. command;
2. air defence;
3. air defence;
4. surface strike;
5. surface strike;
6. ASW;
7. ASW;
8. escort.

The duplicated jobs represent layers and redundancy, not permission to clone the best ship. Class repetition receives a strong selection penalty after the first copy.

After surface selection, suitable non-command slots can become attack-submarine slots. The current chance is 25% for a small force, 50% for a medium force, and 80% for a large force; large forces have a 20% chance of receiving two submarines. Ballistic-missile submarines are excluded from fleet clashes.

## Candidate construction

Surface candidates come from the campaign's generated roster. Submarine candidates come from the same installed vessel database and national variants. A candidate retains:

- class;
- nation;
- all valid national variant references;
- regional or allied source;
- approximate point cost derived from `UnitCostValue`;
- Sea Power's `ASuW_Capability`, `AAW_Capability`, and `ASW_Capability` values;
- Sea Power's `MissilesToSaturate` value.

Civilian vessels and classes without any authored combat capability are excluded from fleet clashes. Exact variants are not left as `Random`: the generator resolves a variant belonging to the selected nation and writes it into the scenario. This prevents a multinational class from silently spawning under the wrong flag.

The current capability values describe the class more reliably than filename prefixes. Filename prefixes decide roster eligibility; they should not decide tactical role by themselves.

## Role-weighted selection

After choosing a source pool, the generator weights every candidate by fitness for the requested role.

The broad priorities are:

- command: combined warfare capability and resilience;
- air defence: AAW capability and resilience;
- surface strike: ASuW capability and resilience;
- ASW: ASW capability and resilience;
- escort: balanced capability across all three warfare areas.

Selection is weighted rather than greedy. The strongest candidate is more likely, but every capable candidate retains a nonzero chance. This allows substitutions and prevents the same theoretical optimum from appearing in every scenario.

Repeated classes are penalized approximately as follows:

- first copy: full weight;
- second copy: 18% of normal selection weight;
- further copies: 2.5% of normal selection weight.

This is a soft rule. It permits a believable two-ship division while making accidental monocultures rare.

## Pair generation and balancing

The generator produces many complete candidates for each opposing side. It compares every first-side candidate with every second-side candidate, sorts the pairs by error, and randomly chooses among the ten best.

Choosing among several top pairs is important. Always taking the mathematical best would make a fixed theater and size converge toward the same OOB.

The fleet-clash balance error currently uses:

| Component | Weight |
| --- | ---: |
| Task Force point cost | 45% |
| anti-surface capability | 25% |
| air-defence capability | 15% |
| resilience | 10% |
| ASW capability | 5% |

The comparison uses relative difference, so the same method works across small and large point totals.

Additional penalties apply beyond these tolerances:

- point-cost difference above 12%;
- anti-surface difference above 20%;
- air-defence difference above 25%.

The tolerance is a target rather than a guarantee. Some regional matchups have shallow rosters and cannot produce a close mirror without destroying their identity. The generator chooses the best available pair and logs its measurements.

## Size and regionality

Size changes more than the number of ships.

### Small

- three ships;
- strongest lead-navy identity;
- escorts strongly prefer local partners;
- distant attachments are rare;
- starting separation: approximately 36–48 nautical miles.

A small Norwegian Sea force will often be entirely Nordic. A distant NATO ship remains possible but noteworthy.

### Medium

- five ships;
- lead navy remains dominant;
- several regional partner roles;
- occasional wider-alliance detachment;
- starting separation: approximately 46–60 nautical miles.

### Large

- eight ships;
- operational coalition task group rather than a national flotilla;
- more alliance-level reinforcement;
- redundant warfare roles;
- starting separation: approximately 58–74 nautical miles.

A large Nordic fight may therefore contain a Nordic core plus a major American, British, French, German, or Spanish combatant.

## Scenario entry and setup menu

The Scenarios menu contains two entries under `Crimean Tom Generated`. `Generated Fleet Clash` opens a setup panel for command play. The player can choose a fixed or random force size, region, sea state, command side, and an enemy force multiplier from 50% to 200%. The player also chooses whether 25%, 50%, 75%, or 100% of the friendly ships are under direct control. The remaining ships move to a friendly AI taskforce after loading. A strongest-first or weakest-first option decides which ships the player receives, using approximate point cost and combat capability.

`Generated Fleet Clash - Spectator` uses the same battle and environment choices without player-control settings. It moves the first fleet to a friendly AI taskforce and leaves the Blue observer taskforce empty. The observer receives exact tracks for every unit, but those tracks are not added to the allied AI taskforce. Blue and Red map symbols therefore remain readable while both combatant taskforces still rely on their own sensors. Truth vision does not enable damage immunity.

Civilian naval and air traffic default to enabled in all generated scenario types, including observers and campaign missions. Skirmish setup has separate toggles to disable either. Small battles request 12–18 ships and 6–9 airliners, medium battles 18–26 and 8–12, and large battles 24–34 and 10–15. Actual counts can be lower where safe routes or separated starting positions are unavailable.

About four-fifths of ships are merchants on independent port-to-port voyages using the shipping graph. They start partway along a nearby lane and continue through its remaining turns toward port. The remainder follow local fishing routes. Airliners follow airport-to-airport cruise routes passing near the encounter, with longer routes restricted to long-range types. Background traffic remains neutral and separate from the protected convoy and its objectives.

Airliner routes are clipped to complete overflights of the local traffic area, with entry and exit points 30 nm beyond the initial placement radius. Each starts with one preplaced aircraft registered to a native `CivilianRoute`. The game removes that aircraft without scoring a kill after its final waypoint, then spawns a replacement at the route entrance. Each route allows one active aircraft and has a seeded 6–15 minute minimum departure interval, so busy routes cannot accumulate aircraft. Different route lengths and departure intervals stagger replacements. Civilian air traffic off disables both initial airliners and these routes. Combat aircraft, merchants and whales are unaffected.

Background merchants use individual lateral offsets of roughly 0.3 to 3 nm and slightly different courses near their starting positions. Each gradually rejoins its port-bound route at a different distance, rather than appearing on one exact line. Narrow-water fallbacks reduce the spread, and both displaced legs must pass the terrain/depth check. Fishing routes and airliner corridors are unchanged.

Before saving any generated clash, escort, or passage mission, `ScenarioTerrain.ValidateNavalSpawns` checks every serialized naval start, including protected ships and nearby or neutral traffic. It samples the installed base terrain around each hull within 0.15 nm and requires at least 20 m of water for surface ships and 80 m for submarines. Submarine starts use an explicit 100 ft depth instead of the variable native `shallow` preset. Numeric depths also require 20 m of seabed clearance. Unsafe layouts fail generation with a reroll message. This checks starting positions, not later native AI maneuvers or terrain changes from other mods.

`ScenarioWildlife` attempts to add two neutral whales to every generated mission, independent of civilian traffic settings. It uses the installed fin, humpback and blue whale units, starts them in at least 80 m of water, and enables native automated movement. Whale placement has its own seeded random stream and may produce fewer whales if no safe positions are found.

Delegated ships use the separate `CrimeanTomAlly` taskforce. Native submarine contact processing otherwise skips this taskforce because it expects only the designated player or enemy taskforce. `GeneratedSubmarineContactsPatch` replaces that identity check with a hostility check only for submarines when either participant belongs to our delegated taskforce. Local detection, identification, weapon availability and ROE still control attacks; observer truth is not shared with the AI. Other missions retain the native check.

Sea state defaults to state 1. If the player chooses random sea state, state 1 has about a two-thirds chance, state 2 about a one-fifth chance, and each rougher state becomes progressively less likely. Random cloud cover also favors clear conditions.

The entry is a launcher, not an authored fixed battle. Confirming the setup rewrites that file with a fresh seed before Sea Power loads it. The generated file remains an ordinary Sea Power mission, so the normal scene loader, AI, trigger, objective, save, and debrief systems handle it.

## Formation and AI output

The planner assigns formation stations after it builds each tactical group:

- every surface group leader becomes `Core`, including the carrier in a carrier group;
- air-defence escorts become `AAW`;
- ASW escorts become `ASW`;
- strike and general escort use the default station behavior.

The force is no longer emitted as one fleet-wide box. It is partitioned into:

- carrier task groups with the carrier as leader and up to three complementary escorts;
- two-to-four-ship surface action groups;
- escort divisions when the remaining hulls are ASW or escort specialists;
- individual submarine patrols operating away from the surface screen.

Where the roster permits it, one or two lighter ships form a forward screen instead of joining the main surface group. Carrier groups retain close escorts. Placement uses installed displacement rather than point cost: light groups start forward, groups with a 6,000-ton or larger combatant start deeper, and carriers stay farthest back. Both sides use this layout. Close escorts also start ahead of their leader. Route attackers favor earlier interception stations for light groups and later stations for heavier groups, subject to safe water and reachability.

Search circles can describe several separate groups. Up to three coarse group reports are mixed with empty alternatives. They are intelligence estimates, not spawn boundaries; unreported units can start outside them.

Player-controlled and friendly-AI ships are partitioned separately, so handing a group to the allied taskforce does not break a mixed formation. Every group leader receives its own offset route. Surface action groups close the enemy from different bearings and submarines patrol ahead on wider flanks. Carrier task groups instead begin on the reciprocal heading and withdraw from the surface threat, buying time for their air group to work.

Each carrier anchors a separate carrier task group. The escort selector cannot absorb another carrier into the first group's screen, so every embarked air wing remains independently usable.

Each carrier can start with up to four fighters airborne as two native two-aircraft CAP formations. Aircraft type, squadron, available count, and loadout come from that carrier's installed `AirGroup` and aircraft data. A carrier without compatible fixed-wing or VTOL fighters does not receive an invalid generated CAP.

If the embarked wing includes an AEW aircraft, one starts airborne on a radar-active orbit behind its carrier. CAP stays close enough to cover the fleet and its radar aircraft. The generator does not substitute a generic AWACS for carriers whose installed air group lacks one.

CAP and AEW leaders repeat their patrol routes with the native `Loop` token. Fighters in the same pair follow their leader. Fuel and recovery remain under normal game behavior.

Up to two patrol helicopters deploy from each surface group's available embarked stock, preferring the carrier and otherwise using a helicopter-equipped escort. ASW loadouts prefer combined fits, then long-range ASW, torpedo-only killer fits, depth bombs, and finally buoy-only hunter fits. The selected fit must be listed by the aircraft and contain payload stations. Ka-27 ASW patrols carry a torpedo and buoys; Ka-25 ASW patrols use their torpedo-carrying killer fit. Each eligible helicopter has a 10% chance to take a supported CAS or AntiArmor fit instead, chosen with equal probability. These use `AntiSurface` orders and a local surface patrol without sonobuoy drops or the `HeloASW` station role. Rolls use the scenario seed with separate streams for each side, including campaign missions. AEW loadouts remain unchanged.

ASW patrols fly independent barrier patterns and receive native `AttackAtWaypoint` orders to drop six sonobuoys when their loadout supports them. The generator resolves buoy ammunition, including date-based substitutions, and assigns the supplying ship as home base. These routes do not loop, so helicopters do not endlessly repeat buoy drops. Submarine groups do not receive aircraft.

### Battle geometry

Initial separation depends on the forces rather than size alone. A fleet clash without carriers keeps the compact size-based ranges, with extra room for submarines and missile-heavy fleets. One carrier expands the engagement to 85-110 nautical miles in confined waters or 115-145 nautical miles at sea. Two-carrier battles use 110-135 or 145-180 nautical miles respectively.

CAP patrol legs stay within 18 nm ahead of their group's station. AEW orbits stay behind it. Surface groups use different approach bearings, with every third group trailing as a reserve. Route attackers sample seven positions along the local route and several lateral offsets, with separate group centers and water/path checks. Charts show up to six uncertain search areas, not exact enemy locations.

Every tactical group receives:

- explicit non-overlapping positions;
- an intercepting route;
- immediate movement along its opening route;
- defensive weapons release during overlapping opening engagements;
- working radar and missile defence from the start, except submerged submarines;
- equal training and morale;
- withdrawal after primary weapons are expended;
- a side-specific retreat anchor.

Surface action groups use `AntiSurface` tasking. Carrier groups and independent submarine patrols use the shipped `Patrol` tasking, with routes that keep carriers clear of the surface advance and move submarines into flank patrol areas.

`ScenarioOpeningOrders` stages AI groups whose starting weapon envelopes overlap. Search and offensive commitment have different per-group delays. Route progress or damage can release a group earlier. Ships keep moving throughout. Player-controlled groups are not overridden. Native `Tight` ROE allows defensive interception; `Hold` is not used. Radar stays available because suppressing native alert activation while also starting blind left attackers unable to defend themselves. Search triggers must not downgrade a group that has already committed after taking damage.

Aircraft and coastal radar coverage use conservative planning ranges, not injected contacts. Supporting sensors add an opening caution margin, and airborne search widens route attackers' lateral placement. These approximations do not guarantee an undetected opening or an even exchange under missile saturation.

### Nearby land and air support

Naval/coastal support gets 10%, 25%, or 40% of its own side's fleet points, with minimum allowances of 8, 16, or 24 points, capped at that fleet's value. When both are enabled, coastal sites get first use of 70% of the allowance. Radar sites cost 3 generation points, SAM sites 6, and anti-ship batteries 8. These are generation-scale estimates, not native campaign purchase prices.

Air support has a separate 25% allowance, minimum 10 points and capped at fleet value. Airfields default on where an owned military airport or suitable land near an owned port exists. Each side independently has a configurable 35% chance of a fighter, anti-ship strike, or reconnaissance package. Role selection precedes class selection. Aircraft begin 150 to 220 nm behind their actual fleet, including rotated route encounters. Only purchased aircraft populate a generated airfield's custom air group. Preplaced aircraft are not also left aboard as a duplicate wing.

Campaign contracts keep these support forces fixed. Their minimum deployment accounts for the friendly support allowance separately from the enemy's strength. Friendly aircraft can remain aboard through the existing starting option.

Standalone fleet clashes end when roughly 75% of an opposing force's surface and submarine combatants are destroyed, capped below total annihilation for fleets with more than one ship. A three-ship side therefore breaks after two losses. Objectives state both the enemy loss target and the friendly loss limit. Campaign clashes instead count 75% of undiscounted ship value, using native per-hull destruction triggers and loss counters; their preservation text also uses points. Aircraft are not counted, so a returning CAP cannot prevent the battle from ending after its fleet has broken. Campaign enemies are fixed in the turn's offer before player deployment; the close-match logic below applies to standalone skirmishes.

## Route operations

`Generated Route Operation` opens a second setup panel in the same scenario category. It supports three protected groups:

- civilian shipping;
- supply ships;
- minesweepers.

The player can escort the group or attack it. Both perspectives use the same generated route, forces, weather, and victory thresholds. The protected ships move under AI control when the player escorts them. The player controls only the assigned warships, subject to the existing control-percentage setting.

Random route attackers roll 5% surface-focused, 20% surface with submarine support, 25% even mix, and 50% submarine-heavy. The bias follows the attacking side even when the player commands it. Explicit enemy-composition choices still take priority. Fleets without suitable submarines fall back to surface forces, so these are profile-roll chances rather than guaranteed final roster frequencies. Fleet clashes roll 45%, 25%, 15%, and 15% respectively.

Even mix means equal surface and submarine hull counts, within one ship when the total is odd. It does not mean equal point value. Explicit even-mix selection remains in force when applying difficulty and pursuit budget adjustments.

Small, medium, and large missions contain progressively longer routes and larger protected groups. About two thirds of the protected ships must reach the destination. The escort wins when that threshold enters the marked destination area. The attacker wins once losses make the threshold impossible. Neither outcome uses a timer.

Protected ships start in a staggered native `Vic` formation around the route, rather than a single line astern. The slowest ship leads. Open-water spacing starts at 0.8 nm and coastal spacing at 0.4 nm, with seeded position variation and narrower fallbacks if the starting formation would touch land or shallow water. The leader retains the full port-bound route.

Surface escort leaders start ahead of the convoy and use native loose-follow orders around its lead ship. Their formation followers keep their stations, while submarines retain independent search routes. If the protected leader sinks, the screen may need new follow orders; native succession still needs a live check. Raider groups approach an interception point from a flank. Passage routes finish at the exit center rather than leaving separate groups parked outside the scoring area. Route generation prefers carrier-free forces. Minesweeper groups prefer classes and national variants already present in the escort coalition. All six combinations reroll their theater and order of battle whenever the player launches the scenario.

## Known limitations

The current balance model is class-level. A variant can change loadout, sensors, or system availability without changing the class's authored capability values.

Point cost and three broad capability ratings cannot capture every interaction. Important omissions include:

- missile range, speed, sea-skimming profile, and salvo timing;
- fire-control channels and magazine composition;
- electronic warfare quality;
- the combat value of generated carrier aircraft;
- helicopter availability and loadout;
- datalink compatibility;
- date-specific doctrine and readiness;
- player skill and AI behavior under saturation.

The generator also does not yet enforce historical service dates when choosing a national variant.

These limitations should be addressed with additional components, not by turning the fleet-clash evaluator into one universal formula.

## Planned extensions

### Variant-aware evaluation

Resolve the exact variant before final balancing, then include loadout and system overrides in its score.

### Salvo model

Add separate measurements for offensive missile count, practical range, speed, guidance, and time needed to complete a salvo.

### Defensive channel model

Measure SAM range bands, guidance channels, ready rounds, reload behavior, CIWS, decoys, and electronic warfare. Compare an opponent's likely salvo against the formation's defensive capacity.

### Doctrine packages

Give navies and coalitions preferences beyond raw capability. Examples include Soviet concentrated missile attack, NATO layered air defence, Nordic fast coastal groups, or regional formations centered on one scarce high-value combatant.

### Mission-specific evaluators

Each mission family should select its own required roles and balance weights:

- convoy escort values area defence, ASW, endurance, and protected capacity;
- interdiction values reconnaissance, speed, and anti-surface reach;
- amphibious operations value transport capacity, shore attack, mine warfare, and air cover;
- ASW missions value sonar combinations, helicopters, quietness, and torpedo endurance;
- air-defence missions value search coverage, channels, magazines, and defended assets.

The theater, source, role, variety, and pairing components remain reusable. Only the blueprint and evaluator should change.

## Tuning rules

Before changing probabilities or weights, generate a large sample and inspect:

- percentage of nationally pure, regional-mixed, and distant-allied forces by size;
- appearance rate of every eligible class and nation;
- repeated-class frequency;
- point and capability differences between opponents;
- frequency of each theater;
- formations that lack credible strike or air defence;
- classes that dominate a role despite intended variety.

Tune distributions from those results. Do not correct one strange screenshot with a hard ban unless the combination is genuinely impossible.
