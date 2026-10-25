# Nuclear Option scenario editor

A standalone WPF editor for Nuclear Option mission JSON. This tool has no Sea Power or mod dependencies.

## Use

Run `NuclearOption.ScenarioEditor.exe` from the published folder. Windows and the .NET 10 Desktop Runtime are required. Keep the executable, DLLs and runtime configuration together.

The mission list scans the current Windows user's `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Missions` directory at startup, when the window regains focus, and when you click **Refresh**. Search the list, select a source, then click **Open selected source**. **Browse** also accepts mission JSON elsewhere. `meta.json` is not a scenario.

The source is read-only. The default output is a sibling mission folder named `<source name> - Edited`, containing a JSON file with the same name. Choose another output folder or edit the output path. Output paths inside the source mission folder are rejected, including directory junctions and symlinks resolving into that folder.

When an output already exists, opening its source resumes that output. **Reload** discards the working document and reads the original source again, after checking for unsaved changes. Source/output associations are remembered after saving in `%LOCALAPPDATA%\NuclearOptionScenarioEditor\settings.json`.

## Editing

- **Global substitution** replaces an exact, case-sensitive unit type ID throughout the open mission, across every faction. It covers placed vehicles, aircraft, ships, factory products, and faction starting reserves. Select a usage row, choose a replacement, check the counts, then click **Apply globally**. This applies to the open scenario, not every file in the missions directory.
- Mission unit identifiers, positions, rotations, waypoints, objective references, reserve quantities, and all unrelated fields stay intact. A reserve count in the grid means matching supply entries, not the number of units in inventory.
- The UI displays game names and retains raw IDs for reference. Select a unit by name, type its full game name, or enter an exact JSON ID. The saved JSON always uses IDs.
- `Disabled` and `EventOnly` default to false in the catalogue model. `data.json` only writes these flags when they are true.
- [data.json](../../tools/NuclearOption.ScenarioEditor.Core/data.json) contains 138 ID/name mappings, including all 137 unit definitions extracted from the installed game's assets, plus the `Nuclear Warhead` factory product. The data includes category and availability flags. Names such as `6x6_1_AA` → `AFV6 AA` come from the game's `jsonKey` and `unitName` fields. This is a snapshot of installed build `cb745d6c44f1`, extracted on 2026-10-25. The data is embedded in the core assembly when built, so the app needs no game installation to display names.
- Default replacement suggestions include vehicles, aircraft, ships and the warhead resource. Disabled prototypes and event-only units are omitted unless encountered in a mission. Buildings and scenery have name mappings for display. Unknown IDs fall back to their raw ID and remain editable. Each opened source and output adds its IDs for the session. Choosing a different unit category does not convert category-specific properties.
- **Factory selection** filters production buildings by ID, faction, building type, or product. Select rows with Ctrl or Shift, or click **Select visible**. Set the product, production time, or both, then apply. Blank fields preserve existing settings. The clear-product checkbox stops production. Enrichment plants appear because they also have production settings.
- **Changes** lists edits made during this session. **Undo** reverses one complete operation, including all fields changed by a global replacement. Ctrl+Z and Ctrl+S also work.

**Save output** writes the current working document, leaving the source untouched. Existing outputs receive timestamped `.bak` copies. If an output changed externally, the editor asks before replacing it. First exports copy `meta.json` when present and update its `FileName`. Existing output metadata stays intact. Extra source assets such as thumbnails or custom map files are not copied.

JSON formatting and escaping may change when saving. Unedited values and unknown properties survive. The editor does not run Nuclear Option or validate whether a replacement unit can navigate a particular location. Check exported missions in the game's mission editor before playing.

## Build and verify

From the repository root with the .NET 10 SDK installed:

```powershell
dotnet build tools/NuclearOption.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet run --project tools/NuclearOption.ScenarioEditor.Tests -c Release -p:UseSharedCompilation=false
dotnet publish tools/NuclearOption.ScenarioEditor -c Release --no-self-contained -p:UseSharedCompilation=false -o artifacts/nuclear-option-editor
```

The tests also accept a mission path as a positional argument after `--`. That optional integration check expects the supplied Escalation Co-op mission, with 574 vehicles and 78 production buildings, including enrichment plants. Test exports go into a temporary directory inside the test build folder and are removed afterward. The original mission is never written.

You can pass a source JSON path as the executable's first argument. The app still checks the current user's standard missions directory.

## Format references

The JSON field names come from the supplied mission. The wiki explains why both placed units and production need editing:

- [How units spawn in game](https://nuclearoption.wiki.gg/wiki/How_units_spawn_in_game), factory output feeds faction inventory, which depots and airbases deploy.
- [Factory](https://nuclearoption.wiki.gg/wiki/Factory), product and production interval are mission settings.
- [Ground vehicles](https://nuclearoption.wiki.gg/wiki/Ground_Vehicles), vehicle roles and names.

The editor works offline and does not send scenario data to the wiki or any service.
