# Live UI testing

Read this reference only when the task requires player-visible Sea Power UI or
mission verification.

## Setup

1. Run `pnpm launch` and require a fresh Sea Power Runtime Bridge health marker.
2. Select exactly one Sea Power window from the desktop-control API.
3. Capture the current window before each action. Sea Power exposes little useful
   accessibility text, so use screenshots and current visual landmarks.
4. Close the news popup if present. Press F7 if UnityExplorer obscures the game.

Do not use fixed coordinates from an earlier resolution or run. One action must
be followed immediately by a fresh capture.

## Tactical smoke

1. From the main menu open **Scenarios**.
2. Select **Intro > 01 Red Sea Breakout - NATO (1988)**.
3. Press **Play**, wait for the full briefing, then press its **Play** button.
4. Verify the tactical date/time, Formation Manager, Tactical Display, and unit
   status strip.
5. Toggle pause, open Event Log, and verify live contact entries.
6. Open Flight Deck and verify capacity and flight-operation controls.
7. Press Escape and verify the pause menu. Restarting the mission is allowed when
   useful to the test.
8. Exit through the After Action Report and return to the main menu.

## Read-only menu checks

- Mission Editor: inspect Scenario, Groups, Triggers, Mission Briefing, and Map
  Elements, then leave without saving and accept the exit confirmation.
- Campaigns: inspect one standard campaign and one Task Force Mode campaign
  without starting or overwriting campaign state.
- Mod Manager: inspect discovery/order only. Do not toggle or accept changes
  unless the task specifically authorizes it. Checkbox state does not prove an
  AnchorChain DLL loaded.
- Options: inspect without changing or accepting settings.

For a fuller UI map and expected screens, read
[the UI workflow notes](../../../../docs/research/ui-workflows.md). Record the game
version, scenario, last successful screen, and relevant logs when reporting a
failure.
