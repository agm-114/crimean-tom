# OODA

See the [standalone scenario AI plan](scenario-ai-plan.md), [decision prompts](prompts/README.md), and [problem solving guide](decision-guide.md) for the player-side controller.

OODA starts in **AI control** when a scenario loads with a controllable player side. Open **OODA Decisions** from the bottom-right HUD toolbar. **Observe** asks the signed-in Codex CLI to plan and suggest orders. **Take control** cancels pending decisions and returns control to you. **Let AI manage time** gives OODA up to four seconds of pause for the opening plan. Cruise defaults to 20×, and OODA can raise it to 100× when Sea Power permits it and the mission is quiet. Nearby missiles and torpedoes keep the game at 1× while the immediate threat persists.

**Mission Command** shows the briefing, player observations, plan, and received mission popups and radio messages. In AI control, OODA records and acknowledges mission popups using the game's primary button. The Decision Log records the acknowledgement, and new messages trigger a replan using the running message history. Ship tabs and the native **Unit Status → OODA** tab show assignments, native orders, and decision results. **Preview urgent decision** on a ship tab sends a synthetic last-salvo choice to Codex and records its answer. The preview issues no weapons orders. The OpenAI Decisions client is available for a later provider change. This build uses the local Codex subscription for every decision.

OODA can adjust its cruise speed and time-compression rules during a mission. Rules can apply to every matching event or target a specific unit or track. Radar reacquisition is recorded without slowing cruise by default. **Time Control** shows the current pace and the rule, AI decision, or game condition behind it. Use `seapower-cli ooda-status --json` to inspect the cruise speed, rules, and recent events. OODA writes proposals for changing the built-in rules to `Player.log` under `[OODA] Time defaults suggestion`.

Each completed Codex decision with reported usage adds an `[OODA] Codex usage` line to `Player.log` with the decision role, model, input, cached input, output, reasoning output, and total tokens, plus the size of OODA's submitted text in bytes.

OODA can request new automation, tools, or analysis when it encounters a gap. Requests appear in `Player.log` under `[OODA] Capability request` with the mission, observed problem, and requested behavior.

The model input groups routine identified civilian traffic by category. Protected contacts, contacts named in mission communications, and contacts tied to weapons or orders retain their details. The decision window and bridge status retain the detailed player picture. Weapon selection can use catalog profiles, estimated direct-hit effects on ground and naval units, and Encyclopedia radar and sonar estimates. The planner can also order a short radar, ESM, or periscope sweep.

**AI control** can apply eligible surface waypoints and native Standing Orders at force, formation, and unit scope. Its map annotations show objectives, reference points, and search areas alongside the game's route display. Use **Chart waypoint** to supply a destination in decimal degrees. OODA checks the direct leg before applying it.

Surface defense takes priority when an attack is reported or gunfire is observed. OODA matches reports against the player picture and assigns fire orders per ship. It checks effective gun range and mount readiness, and reserves a surface target while a friendly missile is queued or airborne. `Player.log` records actual launches separately from queued orders. Possible rules-of-engagement violations appear in the Decision Log with the reason and go straight back to the planner for review.

An inbound raid warning prompts OODA to ready the escorts' air defenses. When matching hostile aircraft are tracked, OODA can order an intercept.

OODA plans CAP, reconnaissance, suppression, strike, transport, and recovery using the player-visible briefing, current flights, and deck readiness. It can authorize a held batch or queue a suitable fit, then give airborne flights native orders. The **Air Operations** tab shows deck preparation, airborne flights, and order results. Actual weapon launches appear in `Player.log`.

Transport planning includes the flight's estimated fuel margin for the delivery and recovery route. OODA can clear a pursuit, split an aircraft into a separate flight, or join compatible aircraft to an airborne leader.

The planner receives ranked engagement options for nearby hostile tracks. Surface intercepts lead contacts using visual motion or successive plotted fixes when available. The planner can preview routes, compare shooters, and estimate a sortie cycle from deck preparation through outbound and return transit. It can order Deck Tweaks to refit a held batch, set native movement and sensor commands, and arm one-shot orders to resume a held transport route, launch CAP relief, or turn back a screen when a track drops. **Order progress** in the Decision Log and Unit Status shows native acceptance, movement, launch or weapon release, and blockers. The same records appear in `seapower-cli ooda-status --json`. See the [order and tool prompts](prompts/orders.md) for the supported arguments.

When an airborne flight's route crosses the estimated range of a live, identified hostile ship or ground air defense, OODA warns in **Air Operations** and the **Decision Log** and requests a fresh plan. The warning gives the distance to the crossing. When OODA manages time, the default interrupt briefly caps speed at 2×. Native attacks use the direct line to the plotted target as an approach estimate.

OODA compares radar emissions before and after a strike. A tracked site that goes quiet after friendly fire is marked **Likely suppressed** in the Decision Log. OODA can use a conventional strike to finish the site when the route is safe. Carrier and airbase fighters can share CAP stations and launch in time to cover an exposed route. Straight CAP routes appear as lines. OODA removes its own stations once they have no aircraft or pending launches.

The tactical map shows active strike and intercept assignments as lines from the flight lead to the player’s current target fix, with labels at the target. CAP labels sit at patrol centers and staging labels sit at their waypoints. OODA removes the assignment line when the attack ends or the live fix is lost.

OODA can stage submarines just inside nominal torpedo range of plotted surface contacts while scouts work on identification. It shows in-range hostile targets and prefers a torpedo shot over a ship-launched missile when the submarine can take the shot. An immediate threat can justify a missile alongside the torpedo.

OODA can task escort helicopters and anti-ship aircraft against the sea threat. Ships can engage plotted vessels or bombard plotted shore targets. When the approach is clear, an independently navigable ship can take a depth-checked route into shore gun range.

OODA hails eligible unidentified surface contacts once when an owned ship can reach them by radio. The planner can arm a one-shot attack that waits for a live hostile track. The Decision Log records hails and conditional orders, and `ooda-status` shows orders still waiting.

With the Game Bridge enabled, run `seapower-cli player-view --json` for the player-scoped snapshot and map annotations, and `seapower-cli ooda-status --json` for the plan, message history, and trace. The CLI and OODA share the native waypoint and Standing Orders actions. See the [CLI documentation](../seapower-cli/README.md).

Enable AnchorChain, OODA, and Observer. Open **OODA Reinforcement Drill** in the Scenarios menu, or load it from the repository root.

```powershell
seapower-cli load-scenario --scenario src/OODA/missions/reinforcement-drill.ini --confirm
```

The drill enters spectator mode automatically. Blue and Red each start with a random carrier and escort in the Strait of Gibraltar. The carriers carry their normal air wings and queue a patrol. Every two simulation minutes, each side draws independently from a shuffled set of surface ships, submarines, fixed-wing aircraft, helicopters, and ground units. Both sides draw from the active military catalog. Each platform family has an equal chance within its category before the drill chooses a specific model or variant.

There are six regular reinforcement waves. The side with more confirmed losses receives a second unit that wave. Lost carriers are replaced at the next two-minute check, including after the regular waves end. Reinforcements stage outside nearby hostile weapon envelopes. Moving units switch to weapons free at their entry waypoint. Ground units use the game terrain height, and ports spawn on the shoreline facing the water. OODA writes cumulative losses and deployments for both sides to `Player.log`.

Merchants and fishing boats populate the strait and the outer Atlantic and Mediterranean approaches. Airliners cross the approaches and the airspace north and south of the battle. Repeatable routes replace ships and aircraft after losses or completed trips. Fishing boats loop through their fishing grounds. Whale contacts add underwater clutter. Both combatants withhold self-identification. The observer map still shows the actual contacts.

Generate the Workshop thumbnail with `./scripts/create-mod-icons.ps1 -Mod ooda`.
