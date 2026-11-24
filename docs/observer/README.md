# Observer

## 0.1.3 unreleased

- Uses the renamed `Controls`, `Settings`, and `UI` projects and namespaces.
- Requires Controls & Settings 0.3.0 or newer.


## 0.1.2 unreleased

- Builds shared UI from the pinned `SEARATS/sp-controls` submodule.
- Initialize dependencies with `git submodule update --init --recursive`
  before building this checkout.
- Verification passed for the migrated Release solution build.
- Requires Controls & Settings 0.2.2 or newer at runtime.

Observer adds a spectator mode for debugging AI behavior in Sea Power. It requires
Anchor Chain or Keel and [Controls & Settings](../../external/sp-controls/src/Controls/README.md).

Version 0.1.3 (unreleased) uses the shared Controls & Settings runtime for its
Gameplay settings page. Existing saved values keep their original filename.

In **Options → Gameplay → Observer**, clear **Use mod defaults**, check **Watch scenarios**, and select **Accept**. Start a new standalone scenario. Sea Power runs both combatants, and the tactical map shows units from every side. You can use the usual map, camera, and time controls. Spectator sessions disable saving. Crimean Tom operations use their own scenario modes.

Scenarios can request spectator mode with an `[ObserverSpectator]` section. The [OODA reinforcement drill](../ooda/README.md) uses it.

Generate the Workshop thumbnail with `./scripts/create-mod-icons.ps1 -Mod observer`.
