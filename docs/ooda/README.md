# OODA

Enable AnchorChain, OODA, and Observer. Open **OODA Reinforcement Drill** in the Scenarios menu, or load it from the repository root.

```powershell
seapower-cli load-scenario --scenario src/OODA/missions/reinforcement-drill.ini --confirm
```

The drill enters spectator mode automatically. Blue and Red each start with a random carrier and escort in the Strait of Gibraltar. The carriers carry their normal air wings and queue a patrol. Every two simulation minutes, each side draws independently from a shuffled set of surface ships, submarines, fixed-wing aircraft, helicopters, and ground units. Both sides draw from the active military catalog. Each platform family has an equal chance within its category before the drill chooses a specific model or variant.

There are six regular reinforcement waves. The side with more confirmed losses receives a second unit that wave. Lost carriers are replaced at the next two-minute check, including after the regular waves end. Reinforcements stage outside nearby hostile weapon envelopes. Moving units switch to weapons free at their entry waypoint. Ground units use the game terrain height, and ports spawn on the shoreline facing the water. OODA writes cumulative losses and deployments for both sides to `Player.log`.

Merchants and fishing boats populate the strait and the outer Atlantic and Mediterranean approaches. Airliners cross the approaches and the airspace north and south of the battle. Repeatable routes replace ships and aircraft after losses or completed trips. Fishing boats loop through their fishing grounds. Whale contacts add underwater clutter. Both combatants withhold self-identification. The observer map still shows the actual contacts.

Generate the Workshop thumbnail with `./scripts/create-mod-icons.ps1 -Mod ooda`.
