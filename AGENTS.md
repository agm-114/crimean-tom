# Repository guidance

Use `pnpm` instead of `npm` or `npx` for package management and repository commands.

Use the repository-local
[`$sea-power-runtime`](.agents/skills/sea-power-runtime/SKILL.md) skill when
building, deploying, launching, verifying, or debugging Sea Power Runtime Bridge inside Sea Power.

Minimize applications launched by agents. Launch only what the current task
needs, and safely close agent-launched applications when they are no longer in
use unless the user asked to keep them running.

Sea Power may be launched for runtime or UI work. Minimize its window whenever it is running but is not being used interactively.

During Sea Power UI work, prefer typed automation exposed by Sea Power Runtime
Bridge over desktop coordinate clicks. Use desktop input for discovery or as a fallback.
Our eventual goal is to build a dynamic campaign mod.

Prioritize getting real features working over expanding test infrastructure.
Avoid narrow unit tests. Keep a small number of broad integration tests that hit as much of the code as possible.
