# Sea Power CLI

Development tools for operating Sea Power's menus and inspecting actual units.
A small AnchorChain mod reads Noesis controls and simulator objects and executes
native actions on Unity's main thread.
The standalone .NET 10 CLI owns argument parsing, transport, output, timeouts,
UI matching, snapshot differences, queries and wait coordination. It has no
game-assembly dependencies. Newtonsoft.Json provides JSONPath,
System.Linq.Dynamic.Core provides Dynamic LINQ, and YamlDotNet handles YAML.
These packages are not deployed to the game.

Only `launch --confirm` starts Sea Power.

## Build and enable

Build or publish the CLI independently of the game:

```powershell
dotnet build src/SeaPower.Cli/SeaPower.Cli.csproj
dotnet publish src/SeaPower.Cli/SeaPower.Cli.csproj -c Release -o artifacts/seapower-cli
```

The published command is `artifacts/seapower-cli/seapower-cli.exe` on Windows. It requires
the .NET 10 runtime. Add that directory to PATH or use the executable's full path
for the examples below. Rebuilding or restarting the CLI does not require a game
restart. You can also run it directly from the project:

```powershell
dotnet run --project src/SeaPower.Cli -- status
```

Build and deploy the game bridge:

```powershell
dotnet build src/SeaPower.GameBridge/SeaPower.GameBridge.csproj
```

Deployment is the default on Windows. Close the game first, or use
`-p:DeployToGame=false` to build without deploying.
Create `enabled.txt` beside the deployed DLL in
`Sea Power_Data/StreamingAssets/SeaPowerGameBridge`, then launch Sea Power.
Changes to the bridge DLL require a
game restart; changes to CLI logic do not.

When upgrading from UI Helper, close the game and remove its old
`SeaPowerUiHelper/SeaPower.UiHelper.dll` before deploying the new bridge.
Enable the new folder with `enabled.txt` and rebuild the CLI too. Its transport
path is now `%LOCALAPPDATA%/SeaPowerGameBridge`; the old bridge cannot serve it.

## Commands

```text
seapower-cli inspect
seapower-cli update
seapower-cli units
seapower-cli units-update
seapower-cli status
seapower-cli game-status
seapower-cli unity-explorer status
seapower-cli unity-explorer install --confirm
seapower-cli unity-explorer enable --confirm
seapower-cli unity-explorer disable --confirm
seapower-cli logs --tail 200
seapower-cli launch --confirm --require-pattern 'Sea Power Game Bridge'
seapower-cli load-scenario --scenario 'C:/Scenarios/carrier-training.ini' --confirm
seapower-cli cancel-scenario --confirm
seapower-cli menu
seapower-cli menu scenarios
seapower-cli menu main
seapower-cli encyclopedia --unit usn_cvn_nimitz --confirm
seapower-cli pause
seapower-cli resume
seapower-cli speed 10
seapower-cli click --snapshot TOKEN --id 12 --confirm
seapower-cli select --snapshot TOKEN --id 20 --index 1
seapower-cli expand --snapshot TOKEN --id 25
seapower-cli open --snapshot TOKEN --id 30
seapower-cli set-text --snapshot TOKEN --id 35 --text "Example"
seapower-cli wait-for --text "Flight Deck" --timeout 30
seapower-cli wait-sim --seconds 60 --timeout 120
```

With no arguments, the CLI runs `inspect`. `speed 10` is an alias for `set-speed --speed 10`.
Use `--help` for syntax. All command results default to YAML. Use `--format json`
for scripts and pipelines or `--format yaml` explicitly. `--json` is an alias for
`--format json`. Errors use the selected format too. Transport and saved baselines
remain JSON regardless of output format. All commands accept `--timeout` in
wall-clock seconds.

For PowerShell object output:

```powershell
$ui = seapower-cli inspect --format json | ConvertFrom-Json
$ui.controls | Format-Table id, parent, type, name, text, enabled
seapower-cli click --snapshot $ui.snapshot --id 12
```

IDs above are examples. Use IDs from the current snapshot, inspect after each
action, and select the next control from that result. Actions consume their
snapshot. The bridge rejects stale IDs, rebound data, and hidden, disabled or
detached controls. Simultaneous inspections share one snapshot; another client
or a pending `wait-for` can invalidate previously returned IDs.

Dropdown indexes are zero-based. `select` without an index selects a tree item.
`expand` opens a tree item; `open` opens a dropdown. Buttons execute their native
command or click event, while selections and text changes use control bindings.

Buttons whose name or label includes save, load, play, launch, quit, exit, start
or delete require `--confirm`. This bridge-side check is a precaution, not a
substitute for reviewing the action. Do not automate overwriting user saves.

## Installation, logs and startup

These commands run entirely in the CLI without the game bridge:

```powershell
seapower-cli game-status
seapower-cli game-status --game-dir 'D:/SteamLibrary/steamapps/common/Sea Power'
seapower-cli logs --tail 300 --format json
seapower-cli launch --confirm --timeout 90 --require-pattern 'loaded native Task Force Mode shim'
```

`game-status` reports the executable path and existence, current process IDs,
and Player.log/BepInEx log paths, existence and last-write times. It is distinct
from `status`, which reads the running mission's clock through the bridge.
Installation lookup uses `--game-dir`, then `SEA_POWER_DIR`, then the default
Steam folder under `C:/Program Files (x86)`.

`logs` returns the last 200 lines of Player.log and, when present,
`BepInEx/LogOutput.log`. `--tail` accepts 1 through 5000. A missing Player.log is
an error; a missing BepInEx log is normal. Log reads allow the game to keep writing.
All three commands accept `--log-file PATH` to override the default
`%USERPROFILE%/AppData/LocalLow/Triassic Games/Sea Power/Player.log`, including
for inspecting saved logs. These commands do not change snapshot baselines.

`launch --confirm` opens `steam://run/1286220`, not the game executable directly.
It refuses an existing Sea Power process and waits up to 60 seconds for a running
process and fresh Player.log content. `--require-pattern` additionally requires
a regex match in that fresh content. An appended log excludes the pre-launch
text, so an old load marker alone cannot satisfy the check. Patterns have a
4096-character limit and a one-second match timeout.

Startup success does not prove menu readiness or scenario loading. Use `inspect`
or `wait-for` next. Launch timeout or cancellation leaves the game alone; check
`game-status` and `logs` before retrying. The CLI never resolves Steam cloud-save
conflicts or changes Mod Manager selections. Only the explicit
`menu exit --confirm` command requests game exit.

### UnityExplorer

`unity-explorer status` reports installed plugin paths and whether the DLL is
enabled for the next launch. This is a disk check, not proof of runtime loading.
All UnityExplorer commands run without the bridge and accept `--game-dir` or
`SEA_POWER_DIR`. Install, enable and disable require `--confirm` and refuse a
running Sea Power process. They never launch or stop the game.

`unity-explorer install --confirm` downloads the pinned
[upstream 4.9.0 BepInEx 5 Mono release](https://github.com/sinai-dev/UnityExplorer/releases/tag/4.9.0)
and checks its SHA-256 before extracting the two DLLs. It installs under
`BepInEx/plugins/sinai-dev-UnityExplorer`, initially disabled. The default timeout
is 120 seconds. An existing installation is left unchanged; this is not an updater
or repair command. BepInEx must already be installed.

`unity-explorer disable --confirm` renames `UnityExplorer.BIE5.Mono.dll` to
`UnityExplorer.BIE5.Mono.dll.disabled`. `enable --confirm` reverses that rename
and checks for `UniverseLib.Mono.dll` beside it. Settings, logs, UniverseLib and
other plugins stay untouched. Repeating enable or disable is harmless.
Multiple copies, unsupported plugin builds and linked mutation paths are rejected.
Keep UnityExplorer disabled for normal play and enable it for runtime inspection.

### Launch into a scenario

```powershell
seapower-cli launch --confirm --scenario 'C:/Scenarios/carrier-training.ini' --timeout 120
```

Supply an existing mission `.ini` file, using an absolute path or a path relative
to the current directory. The command does not copy or rewrite the file. Select
the side through the mission's own settings; it does not override player taskforces.

This option requires the updated `SeaPower.GameBridge.dll` and `enabled.txt` in
`Sea Power_Data/StreamingAssets/SeaPowerGameBridge`. The CLI checks those files
before starting Steam, then waits for a fresh bridge session and a ready main
menu. It calls the native Scenarios Play entry point on the game thread. The
bridge rejects a load during an active mission, scene transition or open dialog.
Startup popups need dismissal before the main menu becomes ready.

The shared launch timeout defaults to 120 seconds with `--scenario`. A successful
reply includes `scenario.state: load-requested` and the absolute path. This means
the request was accepted, not that tactical loading has finished. Check `status`,
`inspect` and logs afterward. Do not retry an uncertain load against the same run.
Existing mod hooks still run, so a generated Crimean Tom launcher opens its setup
menu rather than skipping its settings. Ordinary mission files load directly.

This flow uses Steam and the bridge, not undocumented game startup arguments.
The CLI does not rewrite mission or save files to arrange startup.

### Running-game navigation

These commands require an enabled, updated bridge in a game that is already running.
They do not start or restart Sea Power:

```powershell
seapower-cli load-scenario --scenario 'C:/Scenarios/carrier-training.ini' --confirm
seapower-cli cancel-scenario --confirm
seapower-cli menu
seapower-cli menu scenarios
seapower-cli menu main
seapower-cli menu campaigns
seapower-cli encyclopedia --unit usn_cvn_nimitz --variant 0 --confirm --timeout 120
```

`load-scenario` submits one native Play request from the main or Scenarios menu.
From the main menu it opens Scenarios first, so generated setup dialogs have
their native window host ready.
It validates the file and its `[Mission]` section. It returns `load-requested`,
not tactical readiness. Generated launcher files open their normal setup dialog.
You can load another scenario after returning from a mission or closing setup.

`cancel-scenario --confirm` abandons a fully loaded standalone mission or the
encyclopedia through the native pause-menu exit. It does not save the mission
or record a debrief result. The reply is `unload-requested`; wait until `menu`
reports `idle: true` before loading again. Cancellation refuses scene transitions,
campaign missions and editor sessions. Use their native exit controls instead.
For an unlaunched scenario setup dialog, use `inspect` and click its Cancel button.
Cancelling the CLI process with Ctrl+C does not cancel the scenario.

`menu` without a page reports the current window, loading state, idle state and
supported pages. This read leaves the UI snapshot intact. Navigation uses native
handlers and consumes the previous UI snapshot. Supported pages are:

- `main`, `scenarios`, `campaigns`, `instant-action`, `editor`
- `encyclopedia`, `options`, `mods`, `manual`, `continue`, `exit`

Open pages from the main menu. `menu main` runs the current page's native Back
action. It does not apply pending options.
Leaving the editor requires its own Back/Cancel controls to avoid discarding work.
An active mission, campaign, open dialog or scene transition blocks navigation.
Disabled native entries, including Instant Action in the current build, stay disabled.
Opening Mod Manager does not alter its selections.

`menu encyclopedia`, `menu continue`, `menu manual` and `menu exit` require
`--confirm`. Continue uses the game's last-save selection. Manual opens the native
PDF viewer. Exit requests normal game shutdown from the main menu; it is not a
process kill. Check `game-status` afterward if the exit reply times out.

`encyclopedia --unit` opens the native encyclopedia mission, waits for its loading
screen to finish, then uses the museum's display command to show the unit in 3D
and open its information panel. If the encyclopedia is already ready, it switches
units without reloading the mission. Use the exact INI class name, not a display
name. `--variant` is zero-based and defaults to 0. Invalid names or variants fail
without replacing the displayed unit. Selection is restricted to museum mode,
so it cannot replace a unit in a tactical mission. The default timeout is 120 seconds.
If loading times out, inspect before retrying; the encyclopedia may still be loading.

## Full dumps and updates

`inspect` returns the full visible UI tree in `controls`. `update` returns only
the changes since the last returned `inspect`, `update` or successful `wait-for`.
Control IDs stay stable while their elements remain in the inspected tree.
Each row has a `parent` ID and zero-based sibling `order` to reconstruct the tree.
Reopening a window or recycling a virtualized control can produce new IDs.

`units` returns all spawned simulator units in `units`, including undetected
enemies and neutrals. It is not a contact report and does not grant either side
new detections. Rows contain Unity instance ID, name, class, variant, nation,
taskforce, side, player control, destruction state, geographic position, heading,
speed, loadout and aircraft fuel. Hangar inventory and weapon projectiles are
not included. Latitude and longitude use six decimal places; altitude in meters,
speed in knots, heading in degrees and fuel in kilograms use one decimal place.
Non-finite numeric values are null.

`units-update` compares against the last `units` or `units-update`. Unit IDs
remain stable for the lifetime of the spawned object. A removed ID means the
unit left the simulator's object list, not necessarily that it was destroyed.
Returning to the main menu produces an empty full unit dump.

Both update commands return `mode: "delta"` with `added` and `updated` containing
complete rows, and `removed` containing IDs. Unchanged rows are absent. An idle
snapshot can return three empty arrays. Moving units will usually change on
every read. The reply includes `baseSnapshot`, the new `snapshot` and current
simulation time. Use the new UI snapshot token for actions, even when acting on
an unchanged control. Unit reads do not invalidate UI action tokens.

The CLI keeps independent UI and unit baselines in
`%LOCALAPPDATA%/SeaPowerCli/controls.json` and `units.json`. Separate invocations
share them. Concurrent dumps of the same kind serialize through a file lock.
Missing or invalid baselines, a different game session, a changed mission start
time, or a backwards simulation clock return `mode: "full", reset: true` with
the complete collection. Replace your cached collection on a full reply.
Ordinary `status` reads and unsuccessful wait polls do not advance either
baseline. A truncated UI scan fails without replacing the baseline.

## Query snapshots

`inspect`, `update`, `units`, `units-update` and `wait-for` accept either JSONPath
or Dynamic LINQ options. Queries run in the CLI after reading the game state.
They do not change the DLL or require a game restart.

JSONPath selects values anywhere in the returned snapshot:

```powershell
seapower-cli units --query '$.units[?(@.speedKnots > 10)].name'
seapower-cli inspect --query '$.controls[?(@.type == "Button" && @.enabled == true)]'
seapower-cli units-update --query '$.updated[*].position'
```

Dynamic LINQ operates on typed unit or control rows. `--where` filters,
`--order-by` sorts, then `--select` projects. Each option can be used alone:

```powershell
seapower-cli units --where 'PlayerControlled && SpeedKnots > 10' --order-by 'SpeedKnots descending' --select 'new(Id, Name, SpeedKnots)'
seapower-cli units --where 'Position != null && Position.Latitude > 30' --select 'Name'
seapower-cli inspect --where 'Enabled && Type == "Button"' --select 'new(Id, Text)'
seapower-cli units --where 'FuelKg != null && FuelKg < 500'
seapower-cli units --where 'SpeedKnots > @0' --params '[10]' --order-by 'SpeedKnots descending' --take 10
seapower-cli units --select 'Nation' --distinct
seapower-cli units --where 'PlayerControlled' --count
```

Property names follow the snapshot fields and are case-insensitive in LINQ.
Unit `Position` exposes `Latitude`, `Longitude` and `AltitudeMeters`; control
`Options` contains `Index` and `Text`. Check nullable values before comparisons
or member access. Without `--select`, matching rows retain all their original
fields. Unknown LINQ properties are errors, even when the collection is empty.

Use `--group-by` with `--select` for summaries. Group projections can use `Key`,
`Count()`, `Sum(...)`, `Average(...)`, `Min(...)` and `Max(...)`. `--having` filters
groups before projection. Grouped queries sort after projection, so `--order-by`
can refer to summary columns:

```powershell
seapower-cli units --group-by 'Type' --having 'Count() > 1' --select 'new(Key as Type, Count() as Units, Average(SpeedKnots) as AverageSpeed)' --order-by 'Units descending'
```

Without `--select`, grouped queries return `Key` and `Count`. After projection
and sorting, `--distinct` removes duplicates, then `--skip` and `--take` page the
results. `--count` returns the final count after those operations, including
paging. On a grouped query it counts groups, not units. Skip and take accept
0 through 1,000,000. `--params` binds JSON scalar values or null as `@0`, `@1`,
and so on, avoiding string interpolation inside expressions.

JSONPath and LINQ options cannot be mixed in one invocation. Each expression is
limited to 4096 characters. LINQ uses the library's restricted method access,
property-only sorting and disabled arbitrary-type construction/casts. No game
objects or custom callable methods are exposed. This is a local debugging tool,
not a sandbox for executing queries supplied by untrusted callers.

Query output includes `mode: "query"`, `sourceMode`, the query expressions,
`results`, and snapshot metadata such as `snapshot`, `session` and `time`.
JSONPath results are always an array, including zero or one match. LINQ full-dump
results are also an array unless `--count` returns a number. Selecting a single property returns scalar values;
`new(...)` returns objects. The original bulk collections are omitted.

For a delta, LINQ returns `results.added` and `results.updated`, filtered and
sorted separately, plus **unfiltered** `results.removed` IDs. These are queries
over changes, not a maintained filtered view. A unit falling out of a predicate
does not become a removal. Use an unqueried delta to maintain a complete local
copy, or query a fresh full dump. A reset produces full-dump query results.

Successful live queries advance the same baseline as an ordinary dump/update,
but always save the **complete, unfiltered snapshot**. Failed queries leave it
untouched. `wait-for` matches the original UI before applying the query.

Query an existing dump without contacting the game or advancing any baseline:

```powershell
seapower-cli query --input units.json --where 'SpeedKnots > 10' --select 'new(Name, SpeedKnots)'
seapower-cli query --input units.json --query '$.units[*].name'
seapower-cli query --input units.yaml --preset moving-units --take 5 --format json
seapower-cli query --input "$env:LOCALAPPDATA/SeaPowerCli/controls.json" --where 'Enabled' --select 'new(Id, Type, Text)'
```

Inputs with `.yaml` or `.yml` extensions use YAML; other extensions use JSON.
Offline UI snapshot tokens may be stale. Read the live UI before acting.
Syntax references: [JSONPath](https://www.newtonsoft.com/json/help/html/SelectToken.htm),
[Dynamic LINQ](https://dynamic-linq.net/),
[LINQ restrictions](https://github.com/zzzprojects/System.Linq.Dynamic.Core#breaking-changes).

### Query files and built-in presets

Use query files to keep longer expressions out of shell commands:

```powershell
seapower-cli units --query-file docs/seapower-cli/queries/fleet-summary.yaml
seapower-cli query --input units.json --query-file docs/seapower-cli/queries/fleet-summary.yaml --params '[10]' --take 5
seapower-cli units --query-file docs/seapower-cli/queries/moving-units.jsonpath
```

YAML and JSON query files use the same fields:

```yaml
where: '!Destroyed && SpeedKnots >= @0'
parameters: [10]
groupBy: new(Nation, Type)
having: Count() >= 2
select: new(Key.Nation as Nation, Key.Type as Type, Count() as Units)
orderBy: Units descending, Nation, Type
take: 10
```

Other fields are `jsonPath`, `distinct`, `skip` and `count`. A `.jsonpath` file
contains just the JSONPath expression. Files must contain a valid query, are
limited to 64 KiB, and reject unknown fields. Inline values replace corresponding
file values. `--distinct` and `--count` enable their options; omit them to keep
the file's setting.

Frequently used queries can become built-in presets:

```powershell
seapower-cli presets
seapower-cli units --preset player-units
seapower-cli units --preset moving-units --take 10
seapower-cli units --preset fleet-summary
seapower-cli inspect --preset buttons
```

`presets` lists each name and its query. To promote a useful query file, add its
definition to `src/SeaPower.Cli/QueryPresets.cs`. Presets use the same validation
and execution as inline queries, with no duplicate filter implementation. They
accept inline overrides too. Use either `--preset` or `--query-file`, not both.
A preset's row fields must match the command, such as `buttons` with `inspect`.

## Simulation time and waits

`status` returns availability, pause state, speed, elapsed mission seconds and
the mission's start time on the game clock. It does not scan the UI or replace
the current snapshot. At the main menu it reports no active mission.

Pause and resume are idempotent. Resume restores the previous acceleration;
`speed` also unpauses. Supported speeds are 0.1, 0.2, 0.5, 1, 2, 3, 5, 10, 20,
30, 50 and 100, subject to the game's configured maximum. The bridge rejects time
changes outside a running mission or while a menu or blocking message is open.

The CLI implements both waits by sending ordinary reads every half second:

- `wait-for` polls `inspect`, matching a literal text substring or exact control
  name, ignoring case. It returns the matching UI snapshot.
- `wait-sim` polls `status` until at least the requested number of additional
  simulation seconds pass. It preserves speed and pause state and does not pause
  on completion. Its reply includes requested and actual elapsed duration
  plus the final time state. Polling can overshoot, especially at high speed.

`wait-sim` defaults to 60 simulation seconds and twice that duration as its
wall-clock timeout, bounded to 30 seconds through 24 hours. `wait-for` defaults
to ten wall-clock seconds. Launch defaults to sixty, or 120 with `--scenario`;
other commands default to twelve. Override these with `--timeout`, especially at speeds below 1x.
The encyclopedia command defaults to 120 seconds. Paused games consume the timeout.
Mission changes, a backwards clock, game restarts or connection errors stop the
wait rather than continuing against a different mission.

Ctrl+C cancels the CLI and further polling. It does not undo an accepted game
action. Waits do not run inside the DLL, so the game remains free to process
other commands while a client waits.

Successful commands exit with code 0; errors use 1 and cancellation uses 130.
Results go to stdout, errors to stderr, both in the selected format.

## Transport and limits

Requests and replies use JSON files under `%LOCALAPPDATA%/SeaPowerGameBridge`.
The directory inherits the user's permissions. There is no network listener,
arbitrary reflection command or code-execution endpoint.

Each request carries a session ID, unique request ID and ten-second expiry.
The CLI pins the session across a wait and publishes requests atomically. It
waits for a busy request slot but never resends an action once published.
Each round trip has a twelve-second limit within the overall command timeout.

The CLI removes its temporary and reply files. It does not delete the shared
request slot during cancellation because another client may already own it.
An already-published request may execute before its expiry, and a late reply
can remain if the CLI exits first. Inspect after an uncertain result rather
than retrying a potentially completed action.

Inspection covers realized, visible controls and floating windows opened after
the first inspection. It does not detect occlusion by another window. Virtualized
entries can require scrolling, and the map or 3D scene can require computer use.
Screenshots remain necessary for layout, clipping and actual tooltip visibility.

## Code boundaries

`SeaPower.Cli` contains the CLI parser, async file transport, wait conditions,
snapshot comparison and storage, local querying, cancellation and output formatting.
`GameDiagnostics.cs` owns installation/process inspection, log reads and explicit
Steam launch verification. None of these require game assembly references.
`UnityExplorerInstall.cs` owns the pinned download and on-disk plugin toggles.
`Protocol.cs` decodes only the reply fields
needed for those tasks; unqueried output preserves the entire bridge reply.

`SeaPower.GameBridge` contains only the file request dispatcher, Noesis inspection,
unit reads, native UI/time actions and safety checks that require current game state. Its
polling runs on Unity's main thread. It does not track pending waits or know
about CLI presentation and timeout preferences. Stable UI IDs and unit dumps
require the current bridge build.
