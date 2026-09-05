# Repository guidance

Sea Power is the tactical battle engine. Crimean Tom owns the operations campaign
between battles.

Keep the integration coarse grained:

1. Load persistent campaign and force state.
2. Generate a complete standalone Sea Power scenario.
3. Let the player fight it without runtime campaign synchronization.
4. Import the after-action result.
5. Apply damage, losses, expenditure, repair, rearm, and the next campaign turn.

Do not reintroduce background war simulation, live unit streaming, a companion
process, or a server-authoritative tactical simulation without an explicit new
decision.

Use [`$sea-power-testing`](.agents/skills/sea-power-testing/SKILL.md) whenever a
change needs verification in the installed Sea Power application.

Use `pnpm` instead of `npm` or `npx` if a JavaScript workspace is added later.
