# Mercenary naval contract and scenario catalog

## Structure

Use eight reusable mission families. Do not create a separate template for every subject, location, and side.

A generated mission has four layers:

1. **Family:** the basic objective and victory logic.
2. **Perspective:** which side of that objective the player takes.
3. **Variant:** the force or asset at the center of the mission.
4. **Complications:** threat mix, weather, intelligence, rules of engagement, timing, and force condition.

For example, "escort civilian ships," "escort a minesweeping group," and "escort a supply group" all use the same route-operation family. Flip the perspective and the same template becomes commerce raiding, minesweeper interdiction, or a strike on enemy logistics.

## Contract structure

A contract should tell the player what kind of work they are accepting without revealing the generated scenario in full.

| Field | Gameplay purpose |
|---|---|
| Employer and enemy | Sets reputation, political alignment, allies, and likely equipment. |
| Duty | Selects one or more mission families and weights their variants. |
| Operating area and duration | Controls travel, basing, weather, support distance, and scenario count. |
| Required capability | Surface, submarine, aviation, amphibious, logistics, or mixed group. |
| Threat estimate | Shows ship, air, submarine, and land threat bands with possible uncertainty. |
| Command rights | Controls force selection and how often employer units take part. |
| Support | Controls repair, rearm, intelligence, allied air, and emergency reinforcement. |
| Pay | Combines a retainer with scenario, objective, and risk bonuses. |
| Recovery rights | Covers recovered weapons, captured small craft, intelligence, bounties, and rescued assets. Intact warship capture should be rare. |
| ROE and penalties | Covers neutral losses, territorial violations, premature fire, and protected infrastructure. |
| Exit terms | Covers time expiry, completion, withdrawal, breach, and employer cancellation. |

Use four outcome bands:

- **Decisive success:** the primary objective and most optional goals are complete.
- **Success:** the primary objective is complete at an acceptable cost.
- **Partial success:** enough ships arrive, escape, survive, or are stopped to keep the contract moving.
- **Failure or breach:** the primary objective is lost or the player violates a contract restriction.

## Consolidated mission families

| Family | Perspective A | Perspective B | Shared victory logic |
|---|---|---|---|
| Route operation | Escort and deliver | Intercept and stop | Percentage of the protected group that reaches its destination |
| Passage operation | Run the barrier | Contain or block | Percentage of the running force that crosses a line or reaches an area |
| Search operation | Hunt and prosecute | Evade and escape | Detection, target condition, survival, and deadline |
| Area operation | Clear or seize | Hold or deny | Control of an area at a deadline or removal of a required enemy share |
| Fixed-target operation | Strike or disable | Defend or preserve | Condition of named installations or stationary assets |
| Littoral delivery operation | Land, insert, or extract | Intercept or repel | Delivered or recovered force percentage and transport survival |
| Intelligence operation | Observe and classify | Screen and deceive | Contacts classified, time tracked, emissions collected, and scout survival |
| Maritime security operation | Enforce and protect | Evade or disrupt | Identification, legal target handling, protected traffic, and ROE compliance |

Recovery, deception, surprise, rearguard duty, and bad intelligence work better as variants or complications. They do not need their own top-level mission logic.

## 1. Route operation

The subject is a separate protected group moving along a route. One side wants it delivered. The other wants it stopped.

**Escort perspective**

- Keep a required share of the protected group alive.
- Get that share into the destination area before the deadline.
- Destroying the attackers is optional unless the contract says otherwise.

**Interdiction perspective**

- Stop a required share of the protected group from reaching the destination.
- The player may destroy, disable, scatter, or force units to withdraw.
- Escorts matter only because they interfere with the objective.

**Variants**

| Variant | Escort version | Interdiction version | Campaign effect |
|---|---|---|---|
| Civilian shipping | Protect merchants or refugees | Commerce raid or blockade enforcement | Trade, civilian reputation, and escalation |
| Supply group | Deliver fuel, ammunition, or repair stores | Cut enemy supply | Rearm and repair capacity |
| Minesweeping group | Protect slow clearance ships | Stop channel clearance | Access through a port or strait |
| Amphibious group | Deliver landing ships to the release area | Break up the invasion convoy | Strength of the later landing |
| High-value ship | Escort a tanker, tender, spy ship, or command ship | Destroy or capture the named ship | Support, intelligence, or command ability |
| Damaged group | Cover a slow withdrawal | Finish off retreating ships | Which damaged ships return to the roster |
| Evacuation group | Escort civilian or diplomatic transports | Prevent evacuation or seize a wanted person | Reputation and political outcome |
| Replenishment group | Protect ships during a rendezvous | Strike during the replenishment window | Ammunition and readiness |

## 2. Passage operation

The subject is the player's own combat force or an enemy combat group. No separate convoy needs escorting.

**Runner perspective**

- Cross a patrol line, strait, or island chain with a required share of the force.
- Preserve designated ships or combat value.
- Fighting is a means to open the route, not the main goal.

**Blocker perspective**

- Prevent enough of the enemy force from crossing the line or reaching the destination.
- Destruction, damage, delay, and forced withdrawal can all count.
- A time limit prevents the mission from becoming a slow hunt for every survivor.

**Variants**

- Strait transit
- Blockade run
- Breakout from encirclement
- Surface picket line
- ASW barrier
- Carrier or raider breakout
- Reinforcement interception
- Rearguard action while another campaign group withdraws

The generator can mirror the same map and forces. Changing the player perspective swaps `ReachArea` with `PreventReachArea` and reverses the score.

## 3. Search operation

One side must find and prosecute a mobile target. The other must remain hidden, survive, or reach safety.

**Hunter perspective**

- Search a broad uncertainty area.
- Classify the correct target.
- Destroy, disable, or maintain contact with it before a deadline.

**Hunted perspective**

- Avoid classification or break contact.
- Reach a sanctuary, firing point, or exit area.
- Preserve the named unit or enough of its group.

**Variants**

- Attack submarine hunt
- SSBN search
- Surface raider hunt
- Carrier or cruiser hunt
- Tender hunt
- Counter-hunt after an attack
- Find a damaged or isolated group
- Hunt a group using false contacts and decoys

Intel quality should change the search area, suspected class, last-known position, and age of the report. It should not require a new scenario type.

## 4. Area operation

Both sides contest an operating area. This family covers sea control without requiring the destruction of every contact on the map.

**Clear or seize perspective**

- Remove or force away a required share of the defending force.
- Place a qualifying friendly group inside the area.
- Hold it until the deadline if the contract requires occupation.

**Hold or deny perspective**

- Keep enough combat power in the area.
- Prevent the attacker from meeting its control threshold.
- Survive until relief arrives or the timer expires.

**Variants**

- Surface sweep
- ASW sweep
- Sea-lane clearance
- Chokepoint control
- Anchorage defense
- Port approach defense
- Submarine bastion defense
- Relief of an isolated group
- Fleet-in-being patrol

Area control should use combat value as well as hull count. A single surviving patrol boat should not retain an area against a cruiser group.

## 5. Fixed-target operation

The mission centers on an installation, anchored vessel, or other target that does not need route logic.

**Strike perspective**

- Destroy or disable named targets.
- Escape with enough of the attacking force.
- Respect any protected-site or collateral restrictions.

**Defense perspective**

- Preserve a required share of the targets through the attack window.
- Force the attackers away or survive until reinforcement arrives.
- Protect the mission-critical system, not every structure.

**Variants**

- Port or anchorage raid
- Airfield strike
- Coastal battery suppression
- Radar or communications strike
- Fuel or ammunition depot attack
- Forward base defense
- Ships caught rearming or repairing
- Seabed sensor or cable site

The campaign effect should match the target. A destroyed radar reduces warning time. A damaged runway reduces air support. A lost depot reduces rearm capacity.

## 6. Littoral delivery operation

This family covers forces moving between sea and shore. It is route logic with a timed action at the destination and vulnerable transports.

**Delivery perspective**

- Get transports into the operating area.
- Keep them there until landing, insertion, loading, or recovery completes.
- Withdraw the required ships and embarked force when the task calls for it.

**Interception perspective**

- Stop the transports before they complete the timed action.
- Damage to escorts only matters when it exposes the transports.
- Surviving landed troops can still produce a partial attacker success.

**Variants**

- Opposed amphibious landing
- Amphibious raid
- Amphibious withdrawal
- Special-operations insertion
- Special-operations extraction
- Noncombatant evacuation
- Downed-aircrew recovery
- Amphibious demonstration with no actual landing

Sea Power can initially represent shore actions with time in an area and transport survival. Do not build a separate ground simulation for the first version.

## 7. Intelligence operation

The player collects information or prevents the enemy from collecting it. Weapons may never be released.

**Collection perspective**

- Find and classify required contacts or installations.
- Maintain contact or collect emissions for a set time.
- Preserve the collection platform and leave the area.

**Screening perspective**

- Prevent classification or sustained tracking.
- Drive the scout away without violating ROE when possible.
- Use emissions control, decoys, terrain, and movement to protect the main group.

**Variants**

- Identify and shadow a task group
- Submarine trail
- Electronic reconnaissance
- Beach reconnaissance
- Route reconnaissance before a convoy
- Locate shore defenses before a strike
- Inspect a suspected seabed sensor site
- Counter-reconnaissance screen

Successful collection should reduce uncertainty in a later mission. It can reveal force composition, narrow deployment zones, identify loadouts, or warn of reinforcement.

## 8. Maritime security operation

This family revolves around identity, intent, and rules of engagement. It works for both state forces and mercenary work against piracy or smuggling.

**Enforcement perspective**

- Patrol an area and classify traffic.
- Protect legal shipping.
- Stop, divert, or engage only contacts that meet the contract's conditions.

**Evasion or disruption perspective**

- Pass through the patrol area without interception.
- Conceal a protected vessel among neutral traffic.
- Distract the patrol or create an incident without losing the main asset.

**Variants**

- Anti-piracy patrol
- Contraband interception
- Sanctions or blockade enforcement
- Sovereignty patrol
- Exclusion-zone enforcement
- Escort through a piracy zone
- Captured merchant recovery
- Show of force in disputed waters
- False-flag or disguised shipping operation

## Variants that should not become families

These change how a mission plays but do not change its core objective:

| Modifier | Effect |
|---|---|
| Surprise | One side starts dispersed, unready, or at poor alert status. |
| Bad intelligence | The threat estimate, target class, or reported position is wrong. |
| Delayed reinforcement | A campaign group arrives after a trigger or timer. |
| Decoy | A secondary group tries to draw sensors or weapons away from the objective. |
| Restrictive ROE | Classification, territorial entry, or hostile action gates weapon release. |
| Neutral traffic | Civilian contacts complicate identification and targeting. |
| Weather front | Visibility, flight operations, and sensor conditions change during the mission. |
| Low ammunition | The player enters with expenditure carried over from the campaign. |
| Existing damage | A ship begins impaired and may be the mission's pacing constraint. |
| No support | Air, AEW, tanker, or reinforcement access is unavailable. |
| Time pressure | The target escapes, completes an action, or receives help at a deadline. |
| Preservation order | The employer limits damage to a target, site, or neutral faction. |

## A compact scenario record

One record should describe all perspectives and variants without naming a bespoke scenario class:

```yaml
family: route_operation
perspective: escort
variant: supply_group
subject_group: convoy_17
destination_area: narvik_approach
required_arrival_ratio: 0.60
deadline_minutes: 150
secondary_objectives:
  - preserve_replenishment_ship
complications:
  - stale_submarine_intel
  - worsening_weather
campaign_effects:
  decisive_success: supply_plus_3
  success: supply_plus_2
  partial_success: supply_plus_1
  failure: no_supply
```

To generate the opposing version, change `perspective` to `interdict`. Keep the route, subject, timing, and forces. The scoring code then evaluates the same arrival ratio from the other side.

## Group-unit handling

A group unit is a persistent campaign formation. The campaign expands it into Sea Power units only when a tactical scenario starts.

1. Select a group or detach a subgroup.
2. Expand it into surviving ships, assigned aircraft, ammunition, damage, readiness, and commander state.
3. Add enemy campaign groups and mission-specific auxiliaries.
4. Fight one normal Sea Power scenario.
5. Return survivors, losses, damage, ammunition, experience, and location to their campaign groups.

The mission families should read and write group state in consistent ways:

- convoy cargo delivered changes theater supply;
- escaped enemies remain available for later missions;
- detected groups become easier to intercept;
- detached groups cannot appear in two missions at once;
- surviving support ships improve repair and rearm;
- loss of a flagship hurts command efficiency and reputation;
- withdrawal saves ships but concedes the local objective;
- loss of a base reduces air, radar, repair, or reinforcement support.

## Generator dimensions

| Dimension | Examples |
|---|---|
| Family | Route, passage, search, area, fixed target, littoral delivery, intelligence, security |
| Perspective | Protect or attack, run or block, hunt or evade, seize or hold |
| Variant | Civilian convoy, tender, submarine, airfield, landing group, radar site |
| Geometry | Open ocean, island chain, strait, coast, port approach, patrol barrier |
| Threat mix | Surface, submarine, air, land, mines, or uncertain |
| Intel | Exact, stale, incomplete, deceptive, or absent |
| ROE | Free fire, positive identification, territorial trigger, civilian protection |
| Timing | Day, night, deadline, delayed reinforcement, defend until, weather change |
| Support | Land-based air, tanker, AEW, satellite cue, allied group, none |
| Force condition | Fresh, damaged, low ammunition, dispersed, surprised, detected |
| Campaign result | Supply, reputation, access, threat reduction, reinforcement, intelligence |

Each generated mission should have one primary objective, no more than two secondary objectives, and one major complication. More conditions make the briefing harder to read and the result harder to judge.

## Contract families shown to the player

The market-facing contract names do not need to match the internal mission families.

| Contract duty | Weighted mission families | Typical work |
|---|---|---|
| Convoy protection | Route, passage, security | Escort merchants, support groups, and evacuation shipping |
| Maritime security | Security, intelligence, route | Patrol, anti-piracy, interdiction, and protected transit |
| Sea control | Area, passage, search | Clear sea lanes, hold chokepoints, and hunt major groups |
| Commerce warfare | Route, search, fixed target | Attack shipping, logistics, tenders, and ports |
| Undersea operations | Search, passage, intelligence | ASW barriers, submarine hunts, trails, and infiltration |
| Amphibious support | Littoral delivery, fixed target, area | Reconnaissance, suppression, landing, and withdrawal |
| Raider operations | Search, route, fixed target, intelligence | Hit isolated targets and escape before concentration |
| Retainer duty | Any, weighted by employer need | A longer contract with less predictable tasking |

## Example linked contracts

### Protect the lifeline

1. An intelligence operation surveys the route.
2. A route operation escorts the supply group.
3. A failed escort may generate a littoral recovery operation for survivors.
4. Delivered cargo sets local repair and rearm capacity.

### Break the island blockade

1. An intelligence operation locates coastal defenses.
2. A fixed-target operation suppresses them.
3. A littoral delivery operation lands the force.
4. An area operation holds the approaches.
5. A failed landing flips the next littoral operation to withdrawal.

### Hunt the raider

1. A route operation establishes the raider's effect on shipping.
2. An intelligence operation narrows its location.
3. A search operation hunts the group.
4. If the target escapes, the next search operation flips the player to the hunted perspective.

### Open the strait

1. A fixed-target operation suppresses coastal sensors or missiles.
2. An area operation clears nearby patrol forces.
3. A passage operation takes the task force through.
4. A route operation brings the support group after the route is open.

## Implementation order

### Sprint 1

Implement route operations with civilian, supply, and minesweeper variants. Support both escort and interdiction perspectives. This proves symmetric objectives, percentage outcomes, and group persistence.

### Sprint 2

Add passage and search operations. These cover strait runs, blockades, ASW barriers, surface hunts, submarine hunts, breakout, and escape.

### Sprint 3

Add area and fixed-target operations. Connect target survival to air, radar, repair, rearm, and reinforcement support.

### Sprint 4

Add intelligence and maritime-security operations with classification and ROE scoring.

### Sprint 5

Add littoral delivery using area timers and transport survival. Keep shore activity abstract until the core campaign loop is fun.

## Implementation constraint

Boarding, towing, mine clearance, and shore-party actions may need area timers or campaign-side resolution.
