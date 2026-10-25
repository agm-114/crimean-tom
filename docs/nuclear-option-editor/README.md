# Nuclear Option scenario editor

A standalone WPF editor for Nuclear Option mission JSON. This tool has no Sea Power or mod dependencies.

## Use

Run `NuclearOption.ScenarioEditor.exe` from the published folder. Windows and the .NET 10 Desktop Runtime are required. Keep the executable, DLLs and runtime configuration together.

The mission list scans the current Windows user's `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Missions` directory at startup, when the window regains focus, and when you click **Refresh**. Search the list, select a source, then click **Open selected source**. **Browse** also accepts mission JSON elsewhere. `meta.json` is not a scenario.

The source is read-only. The default output is a sibling mission folder named `<source name> - Edited`, containing a JSON file with the same name. Choose another output folder or edit the output path. Output paths inside the source mission folder are rejected, including directory junctions and symlinks resolving into that folder.

Opening a source builds the preview from that source and the current overrides. An existing output is used to determine whether the preview still needs exporting. **Reload** reads the source again and reapplies the overrides. Source/output associations and the selected unit mode are remembered in `%LOCALAPPDATA%\NuclearOptionScenarioEditor\settings.json`.

## Editing

- **Global substitution** replaces an exact, case-sensitive unit type ID throughout the open mission, across every faction. It covers placed vehicles, aircraft, ships, factory products, and faction starting reserves. Select a usage row, choose a replacement, check the counts, then click **Apply globally**. This applies to the open scenario, not every file in the missions directory.
- The **Ground / Air / Ships** mode at the top filters the source selector, target selector and unit table together. Factory-only and reserve-only unit types appear in their catalogue category. Selectors display game names; IDs remain in the JSON.
- The source selector only offers units remaining in the preview that do not already have a substitution rule. After replacing a type, it disappears from that selector. Adjust its existing rule through **Overrides → Edit**, or remove the rule to make it available again. Placed aircraft remain part of global substitutions.
- **Units** counts placed units of the row's type. **Unit cost** shows the game's base price for one unit, independent of the quantity placed. Costs come from the installed build's unit definitions and are stored in `data.json` in dollars. Unknown units display `Unknown` rather than a zero cost. Click the column header to sort numerically.
- Mission unit identifiers, positions, rotations, waypoints, objective references, reserve quantities, and all unrelated fields stay intact. A reserve count in the grid means matching supply entries, not the number of units in inventory.
- Select a unit by name or type its full game name. The saved JSON always uses IDs. Unmapped units fall back to their ID so they remain editable.
- `Disabled` and `EventOnly` default to false in the catalogue model. `data.json` only writes these flags when they are true.
- [data.json](../../tools/NuclearOption.ScenarioEditor.Core/data.json) contains 138 ID/name mappings, including all 137 unit definitions extracted from the installed game's assets, plus the `Nuclear Warhead` factory product. The data includes category and availability flags. Names such as `6x6_1_AA` → `AFV6 AA` come from the game's `jsonKey` and `unitName` fields. This is a snapshot of installed build `cb745d6c44f1`, extracted on 2026-10-25. The data is embedded in the core assembly when built, so the app needs no game installation to display names.
- Default replacement suggestions include vehicles, aircraft, ships and the warhead resource. Disabled prototypes and event-only units are omitted unless encountered in a mission. Buildings and scenery have name mappings for display. Unknown IDs fall back to their raw ID and remain editable. Each opened source and output adds its IDs for the session. Choosing a different unit category does not convert category-specific properties.
- **Factory production** previews the factories' current products after applying global substitutions. Filter by name, faction, building type or product. Double-click a factory to select its product in the global substitution controls. Rules change what factories produce; their building types, production intervals and mission references stay intact.
- **Changes** lists the fields changed by the current overrides. **Undo** reverses your last override edit, saves the previous rules, and rebuilds the preview. Ctrl+Z and Ctrl+S also work.

## Desired overrides

[overrides.json](../../tools/NuclearOption.ScenarioEditor/overrides.json) records one list of global substitutions to keep in source control. Each rule covers placed units, factory products and starting reserves. It starts empty. Applying a substitution adds or updates its source/target rule. It stores no scenario-specific factory list or absolute machine paths.

The **Overrides** tab lists the rules with game names. Select a rule and click **Edit** to adjust it using the unit controls. You can also enable, disable, or remove a selected rule. Every override edit writes the JSON and rebuilds the preview automatically. Use Git commits, diffs and reverts to manage versions. The editor does not create commits.

Rebuilding always reads the original source and runs enabled global substitutions in list order. Revising or removing a rule therefore updates the preview immediately without leaving its earlier replacement behind. The source remains untouched.

The app checks for external override changes every 750 ms and when it regains focus. Git checkouts, reverts, and text edits reload and rebuild automatically. Malformed or temporarily incomplete JSON leaves the last valid preview in place while the app retries. External changes clear local undo history so Undo cannot silently restore an older Git version. `Disabled` defaults to false and is omitted unless true.

When running anywhere inside this repository, the app uses `tools/NuclearOption.ScenarioEditor/overrides.json`. The displayed path confirms this. A standalone published copy outside the repository uses `overrides.json` beside its executable. That file must remain with the app and be writable.

**Save output** writes the current working document, leaving the source untouched. Existing outputs receive timestamped `.bak` copies. If an output changed externally, the editor asks before replacing it. First exports copy `meta.json` when present and update its `FileName`. Existing output metadata stays intact. Extra source assets such as thumbnails or custom map files are not copied.

JSON formatting and escaping may change when saving. Unedited values and unknown properties survive. The editor does not run Nuclear Option or validate whether a replacement unit can navigate a particular location. Check exported missions in the game's mission editor before playing.

## Build

From the repository root with the .NET 10 SDK installed:

```powershell
dotnet build tools/NuclearOption.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet publish tools/NuclearOption.ScenarioEditor -c Release --no-self-contained -p:UseSharedCompilation=false -o artifacts/nuclear-option-editor
```

You can pass a source JSON path as the executable's first argument. The app still checks the current user's standard missions directory.

For development, keep the app running under the watcher:

```powershell
./scripts/watch-nuclear-option-editor.ps1
# Or choose an initial source explicitly:
./scripts/watch-nuclear-option-editor.ps1 -Source 'C:\path\Mission\Mission.json'
```

C#, XAML and catalogue data changes rebuild and restart the app automatically. WPF layout changes use a fresh window. The last source and unit mode are restored. Override JSON changes reload in place and do not restart the app. Closing the app leaves the watcher waiting for the next source change; stop the watch command when development is finished.

## Format references

The JSON field names come from the supplied mission. The wiki explains why both placed units and production need editing:

- [How units spawn in game](https://nuclearoption.wiki.gg/wiki/How_units_spawn_in_game), factory output feeds faction inventory, which depots and airbases deploy.
- [Factory](https://nuclearoption.wiki.gg/wiki/Factory), product and production interval are mission settings.
- [Ground vehicles](https://nuclearoption.wiki.gg/wiki/Ground_Vehicles), vehicle roles and names.

The editor works offline and does not send scenario data to the wiki or any service.
