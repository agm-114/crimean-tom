# Repository guidance
Seapower is a game we are modding.

Our eventual goal is to build a dynamic campaign mod.
The game can handle small scenarios but cannot handle large scale global scale conflicts.
To a a user it should look like the game can suddenly handle huge amounts of units.

In reality our solution is as follows. 
Seapower serves mostly as thin client where it lools like all units are present and simulation is happening locally.
However in reality we are spawning units in after they are selected. 
Some sim happens locally, so for units the player is watching results are immediate. 
We should be able to simulate everything on the server though and it should be indistinguishable from game native play.

Make architecture changes where needed.

Use the [`$sea-power-runtime`](.agents/skills/sea-power-runtime/SKILL.md) skill when working with the actual seapower application.
Try to test inside Seapower as it will be the most accurate enviroment.

Instead of using computer use clicks, prefer using or creating typed automation exposed by Sea Power Runtime
Bridge, Use desktop input for discovery or as a fallback.


Prioritize getting real features working over expanding test infrastructure.
Avoid narrow unit tests. Keep a small number of broad integration tests that hit as much of the code as possible.


Follow the repository's
[command timeout guidance](docs/development/command-timeouts.md) for assembly
research and other commands that may hang.
