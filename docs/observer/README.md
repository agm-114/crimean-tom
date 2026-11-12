# Observer

Observer adds a spectator mode for debugging AI behavior in Sea Power. It requires AnchorChain.

In **Options → Gameplay → Observer**, clear **Use mod defaults**, check **Watch scenarios**, and select **Accept**. Start a new standalone scenario. Sea Power runs both combatants, and the tactical map shows units from every side. You can use the usual map, camera, and time controls. Spectator sessions disable saving. Crimean Tom operations use their own scenario modes.

Scenarios can request spectator mode with an `[ObserverSpectator]` section. The [OODA reinforcement drill](../ooda/README.md) uses it.

Generate the Workshop thumbnail with `./scripts/create-mod-icons.ps1 -Mod observer`.
