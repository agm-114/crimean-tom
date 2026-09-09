# Scenario and campaign generation plan

Status: implementation complete; live-game acceptance checks remain open. Percentages and ground-unit point estimates are initial tuning values.

## Implemented

- Shared aircraft-deployment and player-order options in standalone setup and campaign sortie preparation. Campaign saves remember both choices.
- Six difficulty presets from Easy at 0.50x through Extreme at 2.00x. Contracts apply difficulty before displaying their terms.
- Surface, mixed, and submarine-heavy enemy profiles. Scaling keeps the selected nations and checks submarine point share. Unsupported explicit combinations fail with an explanation; random composition can fall back to surface forces.
- Eight bidirectional port routes, covering North Atlantic, Norwegian Sea, Baltic Sea, Eastern Mediterranean, Arabian Sea, South China Sea, North Sea and Western Mediterranean. Encounters support departure, coastal transit, mid-crossing and port approach. Configured chokepoints are available on the Norwegian, South China Sea and North Sea routes.
- Convoy routes continue through the local exit toward the destination approach. Convoys use a narrow line-astern formation at telegraph 3, with their slowest ship leading. Native formation code transfers orders when replacing a lost leader. Escort follow behavior after that loss still needs a live check.
- Surface attackers use their group's slowest speed at telegraph 4. Only small, fast attack craft pursue from astern; slower groups intercept ahead and other surface groups approach from a flank. Placement uses reachable interception lanes, not maximum missile range as an exclusion radius. Submarines receive separate depth-checked ambush patrols. Pursuit placement leaves at least 45% of the local transit time for combat; this is a generation constraint, not a mission timer.
- AI raider groups only receive an activation delay if a member starts within either side's naval weapon reach of an escort or protected ship. The check uses target-appropriate ASuW/ASW ranges, excludes countermeasures, and treats known unarmed vessels as having zero reach. Unknown weapon data conservatively counts as possible overlap. These are maximum class-level ranges across installed ammunition variants, not a loaded-ammunition or detection simulation. Non-overlapping groups keep their starting orders and normal AI sensor behavior.
- Delayed groups start stationary under native `OverrideWeaponStatus=Hold` and `ExcludeFromAIRadarRoutine=True`. Activation requires any one of three conditions: roughly 3 NM of forward convoy progress, a seeded 10–16 minute timer, or group damage to 99% integrity or less. Forward progress uses a hidden checkpoint 5 NM along the route with a 2 NM radius, not cumulative distance sailed. The timer prevents avoiding that checkpoint from keeping raiders inactive indefinitely. Deployed aircraft share their parent group's timer and the convoy checkpoint, with their own damage release. A zero-distance relative waypoint restores weapons-free and surface/air search radar before the attack route. Submarines keep passive sensors. FollowLeader formation control passes those orders to escorts. Player-controlled raiders are not held. These orders do not reveal targets or force attacks.
- Ahead/flank stations use 36–57% of the local route, with at least 26 NM surface or 20 NM submarine clearance from protected starting positions. Small pursuit craft retain their closer stern-chase stations. Placement includes the full 16-minute activation delay when checking interception feasibility. Long-range player attacks remain possible; the delay is not invulnerability.
- Chase compensation is capped at 20%, weighted by pursuit delay, weapon reach and the point share of pursuing groups. A bounded candidate pass must fit whole ships within that allowance and remain valid after placement. Unknown weapon references earn no credit. Campaign-owned attackers never get replaced: interdiction contracts instead reduce the generated escort budget. Purchase and salvage values do not change.
- Three uncertain threat sectors and a local-exit marker use native briefing/tactical map symbols. One coarse sector covers an actual attacking group.
- Independent friendly/enemy patrols and coastal sites have separate Off/On/Random settings and a shared per-side budget. Random presence is 33%; size is 10%, 25%, or 40%, with Random choosing those presets at 60%, 30%, and 10%. Standalone setup defaults friendly naval activity and both coastal-site selections to On; enemy naval activity remains Off. Unaffordable or unsafe groups are omitted; an unmet On selection is explained in the briefing.
- Coastal sites use installed national variants, nearby same-nation ports as an ownership check, land height and slope checks, and minimum spacing. Initial point estimates are 25 for radar, 100 for SAM sites and 120 for coastal anti-ship sites. Radar must be within 55 NM of the encounter; weapon sites within 20 NM. These are conservative planning estimates, not native purchase prices.
- Optional secondary activity puts opposing patrols on crossing routes away from the main encounter. It neither picks a winner nor adds a required objective. Nearby forces stay outside the main victory counts and the owned campaign roster.
- Campaign offers fix nearby activity before preparation. The campaign policy uses Random presence and Random size, with secondary encounters allowed. These are not preparation-time reroll controls. The board saves its ready-roster budget; offers show any minimum deployment needed to keep their fixed nearby forces under the 40% ceiling. Changing posture does not change the route, convoy classes, opponents, activity or payment.
- Campaign patrol aircraft are trimmed against the final native air roster. The native loader handles stock and ammunition deductions, including the first generated contract. Friendly nearby submarines survive native preparation's replacement of player-side submarine sections.
- Standalone setup accepts a replay seed. Generated files record composition, budgets, posture, activity, chase allowance and group interception diagnostics. Reproduction requires the same settings, installed data and generator version.
- Route legs, starting positions and authored movement legs use installed base-DEM checks. Placement searches are bounded. Surface clearance is 20 metres; submarine clearance is 80 metres.

## Verification status

The build passes with zero warnings and errors. In-memory checks, with no permanent test files, covered:

| Check | Result |
|---|---|
| Complete mission writing across scenario families, both sides and three sizes | 326 passed |
| Eight regions, supported encounter kinds, three sizes and two route seeds; surface fleets | 210 passed |
| The same route matrix with submarine support | 169 passed; 41 unsafe layouts rejected |
| Nearby forces: budgets, terrain, counts, ownership, determinism and settings | 480 passed |
| Starting posture combinations, final campaign order removal and aircraft-stock fixture | Passed |
| Native-style player submarine replacement with a nearby AI patrol | Passed |
| Speed thresholds, nine interception groups and deterministic reruns | Passed |
| Difficulty/composition on a limited installed US roster | 158 valid; four unsupported small mixed-force budgets rejected |
| Chase reach/cap checks and whole-hull allocation | 36 layouts checked; 40 of 60 gun-destroyer allocations fit the allowance |

The terrain rejections concern shallow North Sea waters and some South China Sea approaches. Explicit unsupported selections fail without writing an unsafe mission. Campaign offer creation retries a bounded set of deterministic seeds before showing terms.

These checks substitute fleet fixtures or engine context. They are not battle acceptance tests. The following remain open:

- [ ] Inspect both setup windows and launch all four posture combinations, including the first campaign sortie.
- [ ] Cross a campaign save/load boundary and confirm aircraft stock, difficulty and offer stability.
- [ ] Sail route turns and local exit triggers from both sides; destroy the convoy leader and inspect convoy and escort behavior.
- [ ] Inspect nearby naval/ground units, allied AI control and secondary patrol encounters in battle.
- [ ] Check terrain stamps and native formation navigation, which the base-DEM checks cannot prove.

Sea Power was running during the final build. The build used `DeployToGame=false`; no installed files were updated. Native screen control was unavailable, so live verification stopped at that boundary under the Sea Power testing skill. Do not mark these acceptance checks complete from generated INI output alone.

## Design

Generate a local skirmish along a larger shipping route. The destination port explains where the convoy is going, but the battle can happen anywhere along that journey. Use Sea Power's scenario tools for complete standalone battles, not a background trade or war simulation.

## Routes and encounter locations

Build a curated graph of ports present in the game, connected by navigable shipping routes. Expand regional coverage around these routes and use the existing nation and coalition data to select plausible regional forces.

Each generated mission selects:

- An origin and destination port.
- A route between them, with intermediate waypoints.
- An encounter segment: departure, coastal transit, chokepoint, mid-crossing, or port approach.
- A convoy starting position and a safe-passage objective beyond the likely interception area.

Convoys receive an onward waypoint sequence all the way to the destination's harbor approach. Escort groups follow and screen the convoy through its route changes. For mid-crossing battles, victory means clearing the local danger area, not spending hours finishing the crossing. Port-approach missions end near the harbor entrance.

Place the mission objective farther beyond the interception area than the current corridor does. Allow room to maneuver and escape without adding a long, uneventful transit. There is no mission timer.

Port coordinates alone do not establish a safe route. Check offshore approaches, coastal turns, water depth, and spawn positions. End near the harbor entrance rather than attempting docking unless native harbor navigation proves reliable.

## Speed-aware interception

Compare each attacking group's practical operating speed with the convoy's speed, not with the fastest escort or the fastest ship in that group.

- Meaningfully slower surface attackers always start ahead, positioned to intercept.
- Faster surface attackers start behind, with enough closing speed and route remaining to catch up.
- Similar-speed groups approach from ahead or the flank.
- Submarines receive separate interception positions and patrol routes rather than inheriting surface chase behavior.

Pursuing attackers receive a bounded generation-cost discount for their positional disadvantage. This permits more attacking capability without changing campaign purchase prices or salvage values. Consider closing time and weapon reach: an attacker already within effective missile range should not receive a large chase bonus.

## Uncertain intelligence

Place several briefing and tactical-map indicators labeled "Possible interdiction force." These describe plausible approach sectors, not exact enemy positions. Some sectors remain empty.

Markers should give the player useful scouting choices without revealing the actual fleet. Briefings identify the shipping route, destination, local passage objective, and reported threat sectors.

## Enemy fleet composition

Add an enemy composition setting to skirmish generation:

| Profile | Intended composition | Weight when Random |
|---|---|---:|
| Surface-focused | Surface combatants, no submarines | 45% |
| Mixed | Surface core with submarine support | 40% |
| Submarine-heavy | Most combat budget spent on submarines | 15% |

These are budget and role preferences, not rigid hull-count ratios. Available regional units, water depth, and mission requirements constrain the result. Random chooses among viable profiles and retains the stronger preference for surface-focused and mixed forces.

Unsupported explicit selections produce a clear explanation rather than silently generating an unrelated fleet. Campaign contracts record their chosen profile when the offer is generated.

## Optional nearby forces

Add separate friendly and enemy settings for:

- Coastal installations.
- Independent naval groups, including submarines.

Each side and category gets Off / On / Random. Random has a proposed 33% presence chance when the location supports the category. On still requires valid placement; explain when a requested category cannot be placed.

These forces are separate from the existing player-control percentage. They do not become player-controlled or campaign-owned merely because they appear.

### Coastal installations

Generate small, believable groups near the transit route:

- Surveillance radar.
- Air-defense sites.
- Coastal anti-ship batteries.
- Combinations appropriate to the country and location.

Open-ocean encounters have no coastal installations. Placement requires valid land positions, plausible ownership, and useful coverage. Account for their actual combat contribution, even when their purpose is to make the surrounding region feel occupied.

### Independent naval groups

Examples include a local patrol, an ASW group, a submarine on station, or ships moving between bases.

Give them their own routes and tasks. They may assist, encounter opposition, or remain outside the main battle. Do not make every nearby group converge on the player automatically.

### Nearby-force size

Use the player's deployed combat budget as the reference, rather than hull count.

| Size | Share of player force budget |
|---|---:|
| Small | 10% |
| Medium | 25% |
| Large | 40% |
| Random | Small 60%, Medium 30%, Large 10% |

Treat this as a combined per-side allowance when coastal and naval forces both appear. Enabling several categories must not overwhelm the main mission. Estimate ground-unit strength conservatively where ship points are not comparable.

If no suitable unit fits, skip the group rather than inserting something disproportionately powerful.

## Optional secondary encounters

Some nearby groups can have opposing patrols positioned on intersecting routes.

Use initial orders and supported area or detection triggers to create an encounter opportunity. Do not script the winner, force ships to ignore the player, or require the player to finish these side battles.

Opponents generated for secondary encounters count toward their side's nearby-force allowance. Briefings can mention local activity without exposing exact enemy positions.

## Starting options for all scenarios

Both options apply to every generated scenario family, not just route operations. Make them available in the standalone scenario setup menu and in campaign preparation before each sortie:

| Setting | Enabled | Disabled | Default |
|---|---|---|---|
| Friendly aircraft already deployed | Begin with available CAP, AEW, and ASW cover | Keep those aircraft aboard their assigned hosts | Enabled |
| Preload player-unit orders | Supply initial movement, patrol, and screen orders | Player-controlled units await player orders | Enabled |

The settings are independent. Enemy AI, friendly AI, and protected shipping retain essential orders regardless of the player-order setting.

Campaign preparation remembers the player's choices with the campaign save and allows changing them before subsequent sorties, including the first sortie. These choices change starting posture, not the contract's opponents, route, payment, or difficulty.

Aircraft deployment must respect purchased and assigned aircraft, available quantities, and native campaign state. Turning deployment off must neither delete owned aircraft nor create replacements. Turning it on must not duplicate aircraft already carried or deployed through native preparation.

Apply order settings after native campaign generation and route finalization so those steps cannot silently reintroduce disabled orders. Preserve mission objectives and protected convoy routing.

## Difficulty for all scenarios

Use the same difficulty presets for every generated scenario family. Offer them in standalone scenario setup and at campaign creation. Persist campaign difficulty with the save and apply it to every generated contract:

| Difficulty | Enemy force budget multiplier |
|---|---:|
| Easy | 0.50x |
| Relaxed | 0.75x |
| Standard | 1.00x |
| Hard | 1.25x |
| Very hard | 1.50x |
| Extreme | 2.00x |

Apply difficulty to approximate point cost and capabilities, not simply hull count. Keep chase compensation separate from difficulty.

Include nearby forces in the strength assessment. Do not automatically give the enemy an exact counter to every friendly support unit, but do not present substantially different threats as equivalent contracts either.

Campaign offers freeze the generated route, encounter, forces, and difficulty before preparation. Reopening an offer or adjusting starting posture must not reroll its terms. Show difficulty in the contract description.

## Configuration and debugging

Use shared scenario configuration for the standalone menu and campaign generator.

Include a reproducible seed and a generation summary showing route, encounter segment, composition profile, budgets, starting options, and optional forces. Add small inspection tools, scenario configs, or debug map overlays where they help verify placement and orders. Do not build a separate runtime control service.

## Implementation order

1. Port-route data and one route supporting both mid-crossing and arrival encounters.
2. Speed-aware interception, uncertain intelligence markers, and enemy composition profiles.
3. Aircraft and preloaded-order options in both standalone setup and campaign preparation.
4. Independent naval activity, then coastal installations.
5. Optional secondary encounters.
6. Wider regional coverage and shared difficulty integration across all generated scenarios and campaigns.

Keep the first milestone a playable escort/interdiction scenario. Add surrounding activity after the core route and encounter work.

## Verification

- Check route and spawn navigability, convoy turns, escort following, and behavior after losing a convoy leader.
- Verify local passage victory on a mid-crossing encounter and port-approach victory on an arrival encounter, from both sides.
- Check that speed-based placement allows a plausible interception without requiring a timer.
- Exercise fleet profiles against regional availability and water-depth constraints.
- Check all four starting-option combinations in standalone and campaign missions, including the first sortie and a save/load boundary.
- Verify aircraft ownership and stock are preserved and disabled player orders stay disabled after native generation.
- Check nearby-force allowances, side ownership, AI orders, and secondary encounters without forcing player involvement.
- Verify every difficulty tier across scenario families, including Easy at 0.50x. Campaign difficulty must match the advertised and generated threat, with stable offers across preparation and reloads.

Use the Sea Power testing skill for live verification. Build and generated-file checks do not prove that routes, triggers, or UI settings work in battle. Do not add permanent tests or commit changes without a new request.

### Interception update verification (2026-09-07)

- Build and deployment passed with no warnings. In-memory checks passed for 326 generated missions and eight focused ambush/ownership cases; no permanent tests were added.
- The earlier checkpoint-based build loaded observer seed `33068953` in Sea Power with groups approximately 21, 28, and 35 NM away. Player feedback found that opening too close. The replacement uses timers and later route stations. A large Norwegian Sea fleet fixture using that seed now places groups at approximately 40, 48, and 55 NM. This is a fixture check, not a replay of the original fleet or live verification.
- The timer update passes the 326-mission writing check and 12 focused timer, ownership, and deterministic-output checks. Native timer syntax matches installed missions. Live radar/ROE activation with the new timers remains unconfirmed.
- The combined progress/time/damage update passes 326 mission-writing checks and eight focused generation cases. Additional in-memory assertions cover one-sided firing reach, separate ASW range, zero-range merchants, non-overlapping groups retaining their orders, and all three OR conditions. Live activation remains unconfirmed.
- A Baltic mixed-force fixture (seed `11`) still fails the safe-depth placement check for a submarine group. It reports that failure rather than moving the group far offshore. Shallow-water submarine placement needs a separate pass.
