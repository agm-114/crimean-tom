# Live UI checks

Use this procedure only when the task needs player-visible proof inside Sea Power.

## Interaction rules

1. Work with one Sea Power window. Run `game-status` to check the process and
   `inspect` to read the visible UI tree through the enabled game bridge.
2. Select IDs and the snapshot token from that live read. Use `click`, `select`,
   `expand`, `open` or `set-text` for the corresponding control. Dropdown indexes
   are zero-based. Actions consume the snapshot, so inspect again after acting.
3. Use `update` for UI changes and `wait-for --text 'Flight Deck' --timeout 30`
   when waiting for a known label. Another inspection can invalidate your token.
4. Use screenshots to verify clipping, tooltip visibility, map geometry and
   other visual requirements. The tree does not prove how a control renders.
5. Use computer input only when the action lacks a CLI control, such as a map
   click or scrolling a virtualized list. Read the computer-use skill first and
   use a current screenshot, not coordinates from another run.

For example, read the current buttons before choosing an action:

```powershell
seapower-cli inspect --preset buttons
seapower-cli click --snapshot TOKEN --id ID --confirm
```

Replace TOKEN and ID with values from the read. Confirmation is required for
launch/load/save/exit-like buttons but does not authorize overwriting user saves.
Dismiss a blocking news popup if needed; do not change inspector configuration.
If the bridge or computer control needed for a check is unavailable, report
that portion as unverified rather than substituting a compile check.

## Tactical state and time

```powershell
seapower-cli status
seapower-cli pause
seapower-cli units --preset fleet-summary
seapower-cli units --where 'PlayerControlled' --select 'new(Id, Name, Type, Position, FuelKg, Loadout)'
seapower-cli resume
seapower-cli set-speed --speed 10
seapower-cli wait-sim --seconds 60 --timeout 30
seapower-cli units-update
```

Use only the actions needed by the task. `status` reads the mission clock, unlike
the installation-level `game-status`. `wait-sim` does not alter speed or pause
on completion. Restore the player's speed/pause state when leaving their session.

`units` includes undetected enemies, so it proves simulator state, not player
contacts. `units-update` compares with the last unit read. Queries preserve the
full baseline; delta queries filter added/updated rows but leave removed IDs
unfiltered. Use full reads when comparing membership in a filtered fleet.
For flight-deck work, combine task-row/tooltips inspection with aircraft reads;
unit snapshots do not include aircraft still in hangars.

## Stock-game smoke test

Use a small stock scenario that is already installed. Do not download content or
change Mod Manager state just to run the smoke test.

Verify these observable states:

- the main menu finishes loading;
- the Scenarios screen lists content;
- the selected scenario opens its briefing;
- Play reaches the tactical display without an error dialog;
- the tactical clock, map, and player unit information render;
- pause and resume change whether tactical time advances; and
- exiting reaches the after-action report or main menu without overwriting a save.

## Generated-scenario smoke test

Use a uniquely named disposable scenario. Before copying it into Sea Power,
record the source path, target path, and file hash. Do not overwrite a scenario
that the agent did not create.

Verify more than menu discovery:

- the generated title appears in the expected scenario group;
- the briefing opens with the intended side;
- Play reaches the tactical display;
- the expected player units exist and have plausible starting readiness;
- time advances after unpausing; and
- ending or exiting produces the state needed by the result-import workflow.

After the test, use `seapower-cli logs --tail 300` to inspect the fresh log for parser errors, missing database
keys, load failures, and unhandled exceptions. Remove the disposable installed
copy only if the agent created it during the task.

## Failure record

Capture the last successful screen and the first failed screen. Report the exact
scenario name, active side, visible error text, and nearby log lines. Do not call
the test successful because the scenario appeared in a list.
