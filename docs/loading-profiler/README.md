# Loading Profiler

Development mod for measuring Sea Power startup and scenario loading. It records
timings without changing load order, skipping work, dismissing dialogs or
altering scenario behavior. No network service or game-control commands.

## Build and install

```powershell
dotnet build src/SeaPower.LoadingProfiler/SeaPower.LoadingProfiler.csproj -p:DeployToGame=false
dotnet build src/SeaPower.LoadingProfiler/SeaPower.LoadingProfiler.csproj -p:DeployToGame=true
```

Close the game before deployment and restart through Steam afterward. Override
`-p:SeaPowerDir=...` for another installation. Builds deploy by default on Windows;
use `-p:DeployToGame=false` for compile-only checks.
Requires the installed BepInEx 5 runtime and Sea Power managed assemblies.
Currently verified against Sea Power `0.8.2-23444+s358`.

One module builds two DLLs:

| Project | Installed under BepInEx | Purpose |
| --- | --- | --- |
| `SeaPower.LoadingProfiler` | `plugins/SeaPower.LoadingProfiler/` | Native game stages, scene transitions and menu visibility |
| `SeaPower.LoadingProfiler.Bootstrap` | `patchers/SeaPower.LoadingProfiler/` | Timestamp BepInEx/plugin logs before the game profiler loads |

The project reference builds and deploys the bootstrap with the main mod. The
bootstrap must not reference Unity, Noesis, game assemblies or Newtonsoft.Json.
Loading those during BepInEx patching would change or break the startup being
measured. It has an empty patch target list and does not edit assemblies.
Neither DLL changes AnchorChain or its preloader; both run directly under BepInEx.
The main plugin loads after AnchorChain's preloader when it is present, using a
soft dependency. The early listener still records that loader's timings.
They are not controlled by Sea Power's Mod Manager.

Remove the two deployed DLLs while the game is closed to disable this module.
Remove older scratch copies `BepInEx/plugins/SeaPower.LoadingProfiler.dll` and
`BepInEx/patchers/SeaPower.EarlyTiming.dll` before installing the tracked version.
Keep unrelated plugins and preloaders untouched.

## Capture

Launch normally, or use `seapower-cli launch --confirm`. Look for
`[Sea Power Loading Profiler] Timing hooks ready` in the BepInEx log. CLI launch
markers search Player.log, so this BepInEx-only message is not a suitable
`--require-pattern` value.

Each process writes to:

```text
%LOCALAPPDATA%/SeaPowerLoadingProfiler/<process-start-UTC>-<pid>/
  bootstrap.tsv
  loading.jsonl
```

- `bootstrap.tsv` records UTC, monotonic process-relative seconds, accumulated
  process CPU seconds, managed thread ID, source and message. It includes assembly
  load notifications and stops 60 seconds after listener initialization.
- `loading.jsonl` records process-relative seconds, UTC, event kind and payload.
  It continues through scenario loading and unloading until the game exits.
  `profiler-start` includes the game version and process start time.

The two files share the process start reference. They do not use simulation time.
CPU seconds include all process threads and can exceed wall-clock seconds.
BepInEx replays earlier preloader messages when it creates its final log sources.
Those replayed timestamps are delivery times, not original event times. Use the
first patch event and the bootstrap's own `patchers-finished` boundary instead.

Files remain until manually removed. Logs can include local paths and plugin
messages; review them before sharing. A write failure stops that log stream
rather than interrupting the game's loading routine.

## Reading timings

```powershell
$events = Get-Content "$env:LOCALAPPDATA/SeaPowerLoadingProfiler/SESSION/loading.jsonl" | ConvertFrom-Json
$events | Where-Object kind -eq end | Select-Object -ExpandProperty data
$events | Where-Object kind -eq call-end | Select-Object -ExpandProperty data
Import-Csv "$env:LOCALAPPDATA/SeaPowerLoadingProfiler/SESSION/bootstrap.tsv" -Delimiter "`t"
```

Replace `SESSION` with a captured directory name. Important events:

| Event | Meaning |
| --- | --- |
| `begin` / `end` | Coroutine wall duration, including nested work and waits |
| `call-begin` / `call-end` | Synchronous startup method entry and exit |
| `mission-request` | Native mission load requested, including the INI path |
| `scene-loaded` | Unity scene-loaded notification, not necessarily usable UI |
| `menu` | Window type and visibility sampled every 100 ms |
| `first-update` | First profiler Update and the splash's completion state then |
| `failed` / `call-failed` | Original game routine threw; exception behavior is preserved |
| `disposed` | Coroutine disposed before a normal completion was recorded |
| `missing-hook` / `missing-type` | A requested hook is unavailable on this build |

`screen:N` encloses `stage:N:*`; `routine:N:*` is nested inside mission loading.
Do not sum parent and child durations. Unload screens also get their own number.
A blocking dialog contributes wall time to its enclosing coroutine. Exclude
such samples or report that wait separately. A loading screen ending does not
prove tactical readiness; check the actual UI and simulation state too.

## Limits and safe hooks

The early listener begins partway through BepInEx startup, so use timestamped
Player.log engine markers for the preceding interval. This is not a native CPU
stack sampler. The game build exposes only a few Unity timing counters.

Optional `[Capture] StartupCounters = true` in
`BepInEx/config/crimean-tom.loading-profiler.cfg` captures available nanosecond
counters from plugin initialization to the first Update. Default is false.
Counters can overlap and are not an exclusive breakdown of startup. They do
not identify native functions within a scene-loading gap.

Keep the hook list explicit. Patching deeper Noesis methods before native Noesis
initialization can run dependent static constructors too early and crash the
game. The current list times `NoesisUnity.Init` and `OnBeforeSplashScreen`, not
`SetApplicationResources`, `Noesis.GUI` or arbitrary UI methods. Missing methods
are logged; managed installation failures remove this mod's hooks.

Timing hooks and synchronous log writes add overhead. Compare repeated runs with
the same mod configuration. Do not describe repeat-process startup as cold-disk
loading, and do not assume background jobs' remaining waits equal their total
CPU cost. Native Windows CPU tracing can require an elevated capture session.

Local measurements and unresolved intervals are recorded in the Git-ignored
[loading benchmark](../../artifacts/loading-benchmark/loading-benchmark.md).
