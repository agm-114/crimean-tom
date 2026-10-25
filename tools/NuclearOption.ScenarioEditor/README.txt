Nuclear Option scenario editor

Windows and the .NET 10 Desktop Runtime are required.
Run NuclearOption.ScenarioEditor.exe. Keep all published files together.

1. Select a source mission and click Open selected source.
2. Check the OUTPUT path. The default creates a separate " - Edited" mission.
3. Select a unit by its in-game name, choose a replacement, and Apply globally.
4. Use Factory selection to change products or production times for selected rows.
5. Review Changes, then Save output. Ctrl+Z undoes an operation; Ctrl+S saves.

The editor scans the current Windows user's standard Nuclear Option Missions
folder at startup and whenever the window regains focus. Browse accepts other
scenario JSON files. The source mission folder is read-only.

Opening a source with an existing output resumes that output. Reload reads the
original source again. Existing outputs receive timestamped .bak backups.

Global substitution covers the open mission's placed vehicles, aircraft, ships,
factory products and faction starting reserves. Mission references stay intact.
Unknown JSON properties survive saving. Global does not mean every mission file.

The source catalogue data.json contains game names and IDs, embedded at build time.
Unknown IDs remain editable. Unit compatibility and the exported mission's behavior
must be checked in Nuclear Option's mission editor. Extra map assets and thumbnails
are not copied. No scenario data is sent to online services.
