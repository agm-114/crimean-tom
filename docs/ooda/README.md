# OODA

See the [standalone scenario AI plan](scenario-ai-plan.md), [decision prompts](prompts/README.md), and [problem solving guide](decision-guide.md) for the player-side controller.

OODA starts in **AI control** when a scenario loads with a controllable player side. Open **OODA Decisions** from the bottom-right HUD toolbar. **Observe** asks the signed-in Codex CLI to plan and suggest orders. **Take control** cancels pending decisions and returns control to you. **Let AI manage time** gives OODA up to four seconds of pause for the opening plan. Once it has a plan, OODA cruises at up to 20× and slows temporarily for new developments. Nearby missiles and torpedoes keep the game at 1× while the immediate threat persists.

**Mission Command** shows the briefing, player observations, plan, and received mission popups and radio messages. In AI control, OODA records and acknowledges mission popups using the game's primary button. The Decision Log records the acknowledgement, and new messages trigger a replan using the running message history. Ship tabs and the native **Unit Status → OODA** tab show assignments, native orders, and decision results. **Preview urgent decision** on a ship tab sends a synthetic last-salvo choice to Codex and records its answer. The preview issues no weapons orders. The OpenAI Decisions client is available for a later provider change. This build uses the local Codex subscription for every decision.

OODA can adjust its cruise speed and time-compression rules during a mission. Rules can apply to every matching event or target a specific unit or track. Radar reacquisition is recorded without slowing cruise by default. **Time Control** shows the current pace and the rule, AI decision, or game condition behind it. Use `seapower-cli ooda-status --json` to inspect the cruise speed, rules, and recent events. OODA writes proposals for changing the built-in rules to `Player.log` under `[OODA] Time defaults suggestion`.

**AI control** can apply eligible surface waypoints and native Standing Orders at force, formation, and unit scope. Its map annotations show objectives, reference points, and search areas alongside the game's route display. Use **Chart waypoint** to supply a destination in decimal degrees. OODA checks the direct leg before applying it.

Surface defense takes priority when an attack is reported or gunfire is observed. OODA matches reports against the player picture and gives engagement orders to each eligible ship in the chosen formation. Possible rules-of-engagement violations appear in the Decision Log with the reason and go straight back to the planner for review.

An inbound raid warning prompts OODA to ready the escorts' air defenses. When matching hostile aircraft are tracked, OODA can order an intercept.

With the Game Bridge enabled, run `seapower-cli player-view --json` for the player-scoped snapshot and map annotations, and `seapower-cli ooda-status --json` for the plan, message history, and trace. The CLI and OODA share the native waypoint and Standing Orders actions. See the [CLI documentation](../seapower-cli/README.md).

Enable AnchorChain, OODA, and Observer. Open **OODA Reinforcement Drill** in the Scenarios menu, or load it from the repository root.

```powershell
seapower-cli load-scenario --scenario src/OODA/missions/reinforcement-drill.ini --confirm
```

The drill enters spectator mode automatically. Blue and Red each start with a random carrier and escort in the Strait of Gibraltar. The carriers carry their normal air wings and queue a patrol. Every two simulation minutes, each side draws independently from a shuffled set of surface ships, submarines, fixed-wing aircraft, helicopters, and ground units. Both sides draw from the active military catalog. Each platform family has an equal chance within its category before the drill chooses a specific model or variant.

There are six regular reinforcement waves. The side with more confirmed losses receives a second unit that wave. Lost carriers are replaced at the next two-minute check, including after the regular waves end. Reinforcements stage outside nearby hostile weapon envelopes. Moving units switch to weapons free at their entry waypoint. Ground units use the game terrain height, and ports spawn on the shoreline facing the water. OODA writes cumulative losses and deployments for both sides to `Player.log`.

Merchants and fishing boats populate the strait and the outer Atlantic and Mediterranean approaches. Airliners cross the approaches and the airspace north and south of the battle. Repeatable routes replace ships and aircraft after losses or completed trips. Fishing boats loop through their fishing grounds. Whale contacts add underwater clutter. Both combatants withhold self-identification. The observer map still shows the actual contacts.

Generate the Workshop thumbnail with `./scripts/create-mod-icons.ps1 -Mod ooda`.
