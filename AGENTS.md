# Repository guidance

Our eventual goal is to build a dynamic campaign mod.
Make architecture where needed.

Use the repository-local
[`$sea-power-runtime`](.agents/skills/sea-power-runtime/SKILL.md) skill when
building, deploying, launching, verifying, or debugging Sea Power Runtime Bridge inside Sea Power.
Try to test inside Seapower as it will be the most accurate enviroment.

During Sea Power UI work, prefer typed automation exposed by Sea Power Runtime
Bridge over desktop coordinate clicks. Use desktop input for discovery or as a fallback.


Prioritize getting real features working over expanding test infrastructure.
Avoid narrow unit tests. Keep a small number of broad integration tests that hit as much of the code as possible.


Follow the repository's
[command timeout guidance](docs/development/command-timeouts.md) for assembly
research and other commands that may hang.
