Nuclear Option scenario editor

Windows and the .NET 10 Desktop Runtime are required.
Run NuclearOption.ScenarioEditor.exe. Keep all published files together.
Keep overrides.json writable. Inside this repository the app uses the tracked
tools/NuclearOption.ScenarioEditor/overrides.json file instead of its published copy.

1. Select a source mission and click Open selected source.
2. Check the OUTPUT path. The default creates a separate " - Edited" mission.
3. Select a unit by its in-game name, choose a replacement, and Apply globally.
4. Use Factory production to preview what factories will produce after substitution.
5. Review Changes, then Save output. Ctrl+Z undoes an operation; Ctrl+S saves.

The Production tab compares starting and resulting seconds per unit and units
per hour. Units with zero final production are hidden, and Hide unchanged production
is enabled by default. Times combine parallel factories; hover to see individual
factory intervals. Filter by faction.

Choose Ground, Air or Ships to filter both selectors and the unit table.
Your desired changes are recorded in the Overrides tab. Every rule edit saves
overrides.json and rebuilds the preview from source automatically. Use Git commits
to keep versions. External JSON edits reload automatically. Select a rule to edit,
disable or remove it. Save output exports the current preview as a mission.
One global substitution list covers placed units, factory products and reserves.
Factory buildings and production times stay intact.

The editor scans the current Windows user's standard Nuclear Option Missions
folder at startup and whenever the window regains focus. Browse accepts other
scenario JSON files. The source mission folder is read-only.

Opening a source reapplies its overrides. Reload rereads the original source and
reapplies the rules. Existing outputs receive timestamped .bak backups.

For automatic development rebuilds, use scripts/watch-nuclear-option-editor.ps1
from the source repository. Code, XAML and catalogue changes restart the app;
override JSON changes reload in the existing window.

Global substitution covers the open mission's placed vehicles, aircraft, ships,
factory products and faction starting reserves. Mission references stay intact.
Unknown JSON properties survive saving. Global does not mean every mission file.

The source catalogue data.json contains game names and IDs, embedded at build time.
Unknown IDs remain editable. Unit compatibility and the exported mission's behavior
must be checked in Nuclear Option's mission editor. Extra map assets and thumbnails
are not copied. No scenario data is sent to online services.
