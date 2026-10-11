# Quickload

The project and DLL are `Quickload`; the plugin ID is `crimean-tom.quickload`.
Deployment disables the old `SeaPower.StartupTweaks.dll` in the Quickload folder.

Experimental AnchorChain mod for Sea Power `0.8.5-261008`. Unsupported game
versions keep the native startup flow. Independent of Loading Profiler.

The October 2026 update moved encyclopedia cloning into `UnitTreeBrowserViewModel`.
Quickload supplies cached rows to that browser's native cloning and visibility
filters, and uses live entries when its completion event refreshes the browser.
The October 11 startup check passed in the installed game. Native metadata,
encyclopedia entries, voices and all seven music bundles completed, with 649 unit
records ready. The replenishment playground reached the tactical screen.
Cached encyclopedia browsing during loading has not been checked visually.

Sea Power `0.8.5-261008` loads unit metadata on parallel workers, then finalizes
the results on the main thread. Quickload builds cached unit labels only after
that native stage completes. Per-unit collection updates, metadata reprioritization
and per-file metadata progress hooks have been removed from the worker path.

## Build and deploy

```powershell
dotnet build src/Quickload/Quickload.csproj -p:DeployToGame=false
dotnet build src/Quickload/Quickload.csproj -p:DeployToGame=true
```

Close the game before deployment. Override `SeaPowerDir` for another installation.
The DLL and its `_info.ini` go into `Sea Power_Data/StreamingAssets/Quickload`,
along with the Workshop `thumbnail.jpg` once `scripts/create-mod-icons.ps1` has
generated it. Builds deploy by default on Windows; use `-p:DeployToGame=false`
for compile-only checks. Deployment also removes the
layouts this mod shipped under before, the `BepInEx/plugins` copy with its config
file and the `SeaPowerStartupTweaks` folder, either of which would otherwise load
the game a second time.

The mod appears in the in-game mod manager as "Quickload". Clear its checkbox
there and restart to disable it; AnchorChain only loads mods whose directory is
selected. No preloader changes.

## Mission loading

Mission scenes load at the native `LoadMission` step, after `PreloadInis` and
caller-supplied setup actions. Quickload does not hold an inactive scene in Unity's
async queue while the player browses menus. After deferred loading finishes, an
idle-menu worker reads `level2`, `sharedassets2.assets` and
`sharedassets2.assets.resS` into the OS file cache. It reads at most 16 MiB total
in 64 KiB chunks, with a short delay between reads. No Unity objects load or
activate during this work, and no file contents remain in a managed cache.

Warming runs at most once per mod instance. Mission requests, shutdown and mod
destruction stop further reads; an in-flight read may finish. Missing or
inaccessible files are skipped. The OS can evict warmed pages, and warming does
not reduce scene deserialization or initialization work. Speed gains are not yet
measured.

INI reads use the game's parsed-content cache and create their own handlers.
Handlers have mutable file paths and alias lists as well as shared content, so
Quickload does not reuse handler instances across callers or cache mod-path lookup.
It caches only `Path.GetRelativePath` results inside `IniHandler.open`, keyed by
the exact pair of fully qualified paths. Relative inputs use the native function
so working-directory changes retain their meaning. The cache holds at most 8192
entries and clears when full. File existence checks, uncached reads, aliases,
save destinations and the native parsed-content cache retain their usual behavior.

## Loading behavior

File validation, Workshop updates, compatibility checks and startup warnings
retain their native ordering and behavior.

Music startup creates a lightweight entry per bundle. Audio stays unloaded.
After priority data finishes, asynchronous bundle loading starts with a requested track first,
then remaining bundles. Loaded tracks replace the entry's temporary bundle name
with the real track name; bundles with multiple tracks add the remaining entries.
Playback waits for real audio instead of playing a silent dummy clip. Stop cancels
pending playback. Custom mission music retains its native loader. The game uses
`user/audio/music/original` instead of the original directory when present.
Explicit music reloads are ignored during initial warming, then use the native loader.

Loading runs in three phases after the menu appears:

1. Unit metadata loads first. Encyclopedia data follows
   metadata in that queue. Unknown third-party actions retain their native order
   there rather than assuming they are safe to run concurrently.
2. Voice clips load after the data queue finishes.
3. Once voice clips finish, music loads alongside Tacview export.
   These lower-priority jobs do not delay menu access.

These are overlapping native coroutines and asynchronous requests, not worker
threads running Unity code. The encyclopedia's per-unit coroutine runs in roughly 4 ms
main-thread batches instead of yielding a whole frame after every entry.
The data queue waits for that nested coroutine too.
Native data loaders can still produce frame hitches.
Unity objects are never accessed from a worker thread.

Menu controls unlock by dependency, while respecting native disabled states:

| Entry | Required startup work |
| --- | --- |
| Scenario browsing | Cached catalogue, refreshed from current files |
| Encyclopedia, campaigns, instant-action browsing | Unit metadata |
| Editor menus | Data queue, including encyclopedia entries and third-party actions |
| Continue and battle launches, including CLI requests | Data queue and voice clips |
| Options and Mod Manager | All loading, to avoid changing language or content mid-load |
| Exit, manual and credits | No deferred-loading restriction |

The full native encyclopedia mission can load after unit metadata without waiting
for the encyclopedia-detail batch or voices. Other mission requests before their
dependencies finish fail explicitly.
This makes the menu appear earlier; it does not make gameplay-ready time zero.
There is no persistent simulation-object cache or skipped integrity check.

## Catalogue caches

Before starting deferred loaders, the mod restores unit labels and the scenario
tree from `%LOCALAPPDATA%/SeaPowerQuickload/catalogues/`. Cache keys include
game version, language and ordered enabled mod directories. Cached rows are for
browsing only; they never replace live simulation metadata.

The native scenario menu opens without waiting for unit metadata. A refresh scans
one directory per frame through the native parser, preserving native folder rules.
Without a cache, folders populate progressively. Selection immediately rereads
the selected scenario and its briefing. Launch checks selection availability,
current mod-file precedence and readiness again. Cached Play controls begin disabled.

The full native encyclopedia keeps its own unit list and data pane. While the
detail batch is incomplete, the list uses cached class labels. A selection whose
details are missing clears the data pane; the real entry replaces it when ready.
The native 3D scene still needs unit metadata before it can open safely.
Native parallel metadata loading retains its own order. The encyclopedia batch
puts the selected class first when its unit enumeration
starts. It does not preempt a unit enumeration already in progress.

Metadata completion replaces cached unit labels and removes missing or hidden
classes. Successful scenario refresh replaces its cache. Both caches are bounded
JSON files written atomically; failed refreshes do not replace them.

The vegetation-distribution coroutine stops advancing during application shutdown.
Normal terrain-loading exceptions are not swallowed.

## Progress and timing history

The menu panel shows each active category and its last requested file, with one
overall percentage and ETA. File paths are relative to
StreamingAssets where possible. Voice requests can overlap, so this is the latest
request, not a list of every file currently being read.

Progress weights every data stage and music bundle by its duration on the last
successful load. An active job contributes elapsed time up to 99% of its weight;
only completion earns its full weight. Overall progress stays below 100% until
all jobs finish. ETA adds the longer remaining queue from each phase, accounting
for the serial data and voice queues, then the overlapping music and Tacview queues.
The first matching run uses an indeterminate bar and reports no ETA. A job that
exceeds its previous duration displays `ETA exceeded` rather than zero remaining.
The phased schedule uses fresh timing history rather than timings from the old
schedule where music competed with priority data.

Timing history lives in `%LOCALAPPDATA%/SeaPowerQuickload/`. Separate files
cover each game version, language, ordered selection of mod directories, and set
of loading stages and music bundles. Only a completed, successful load replaces
the history. Estimates are approximate: changed content within the same mod
directories, OS file caching and other system activity can change load times.

Use Loading Profiler to compare process-to-menu time separately from the
`Core ready` and `Music ready` log markers. Keep benchmark results under
Git-ignored `artifacts/loading-benchmark/`, not here.
