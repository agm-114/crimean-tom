# Scenario generation

## 12. Centralize launcher identity

`Scenarios/ScenarioDebugGenerator.cs` repeats three launcher identities across
filename constants, path getters, path classifiers, and `DeleteObsoleteEntries`.

Start with one filename list and a path comparison helper. Add a kind-to-file
mapping only if it simplifies dispatch as well. Keep generation and setup behavior
specific to fleet clash, spectator, and route operation.

Preserve the exact filenames, full-path case-insensitive comparisons, blank-input
handling, and distinction between command and spectator launches.
`_info.ini` is category metadata, not a launcher, and must survive cleanup.
Deletion must remain limited to the generated scenario directory and its current
top-level INI scope.

Compare classification for all three launchers, unrelated paths, and blank inputs.
Inspect the retained/deleted filename set without deleting user scenarios.
Do not introduce a general scenario-plugin registry for three entries.

Back to the [index](README.md).
