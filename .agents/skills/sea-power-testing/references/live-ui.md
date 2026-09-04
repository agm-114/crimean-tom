# Live UI checks

Use this procedure only when the task needs player-visible proof inside Sea Power.

## Interaction rules

1. Work with one Sea Power window.
2. Capture the current screen before acting. Sea Power exposes little useful
   accessibility text, so identify the visible control from the current image.
3. Perform one action, wait for the screen to settle, then capture it again.
4. Do not reuse coordinates from another resolution or earlier run.
5. Dismiss a news popup if it blocks the main menu. If an inspector overlay is
   already open, hide it without changing inspector state.

Use desktop input only for the visible steps that lack a typed test hook. If
screen control is unavailable, stop and report that the live portion remains
untested.

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

After the test, inspect the fresh `Player.log` for parser errors, missing database
keys, load failures, and unhandled exceptions. Remove the disposable installed
copy only if the agent created it during the task.

## Failure record

Capture the last successful screen and the first failed screen. Report the exact
scenario name, active side, visible error text, and nearby log lines. Do not call
the test successful because the scenario appeared in a list.
