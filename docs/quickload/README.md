# Quickload

## 0.2.7 unreleased

Quickload resolves its patch targets directly in the game's assembly. All
patches still install at the first scene. When a full INI snapshot is available,
the splash worker also computes Sea Power's unit price hashes. At pricing time,
Quickload checks the live section names, keys and values before reusing a hash.
Changed records and virtual INIs use the game's hash function.

Five warm launches per build on Sea Power `0.9.3-261116` with Quickload as
the only DLL mod gave these medians, measured from process start.

| Ready | 0.2.6 | 0.2.7 |
| --- | ---: | ---: |
| Main menu visible | 9.94 s | 9.49 s |
| Scenarios | 11.72 s | 11.35 s |
| Editor | 12.72 s | 12.09 s |

The two builds ran in pairs with alternating order. Every run reused all 718
unit prices. The displayed prices for 718 units and 872 ammunition entries
matched between builds.

## 0.2.6 unreleased

On Windows, Quickload shows elapsed time on the game splash and hands the bar
over to Noesis when the in-game panel is visible. After a complete launch,
both bars use that profile's previous stage durations for segment widths and
the ETA to full menu readiness. The first launch shows elapsed time while
Quickload learns the profile.

Quickload can now decode the sensor, INI, unit and encyclopedia snapshots
before the game asks for them. It validates the inputs before applying cached
data. The startup log breaks pricing time down into unit work, ammunition,
publication, setup and waits between frames.

On Sea Power `0.9.3-261116` with Quickload as the only DLL mod, three warm
launches reached scenarios at a median of 11.66 s and the editor at 12.72 s.
Three more launches with the pricing breakdown enabled reached those gates
at 11.93 s and 12.93 s. All six reused 718 cached prices.

## 0.2.5 unreleased

On Keel builds with splash-screen preparation, Quickload starts hashing files
and reading cached archives before its plugin starts. Harmony patches wait for
the first loaded scene so Noesis can initialize its native UI types.

On one warm launch of Sea Power `0.9.3-261116` with Quickload as the only
active plugin, preparation started 2.08 s after launch. All 3,627 input hashes
and 12 archives were ready when patch setup began at 7.72 s. The menu appeared
at 9.82 s, scenarios unlocked at 12.67 s, and the editor unlocked at 14.13 s.
All 718 point values came from cache.

## 0.2.4 unreleased

Quickload filters cache inputs before sorting paths and skips directive searches
in INIs without directives. Existing snapshots keep their fingerprints. Pricing
uses an 8 ms frame budget while the encyclopedia keeps its 4 ms budget.

On Sea Power `0.9.3-261116` with 29 plugins, scenarios opened at a median of
12.72 s and the editor at 16.89 s from launch. Version 0.2.3 took 13.58 s and
18.18 s on the same Keel build. Each median covers three launches with a warm
cache. All 749 unit prices came from cache and the menu gates opened in order.

## 0.2.3 (2026-11-24)

- Verified startup under Keel on Sea Power `0.9.3-261116` with both Euromod packs enabled.

## 0.2.2 unreleased

- Uses the renamed `Controls`, `Settings`, and `UI` projects and namespaces.


## 0.2.1 unreleased

- Builds shared UI from the pinned `SEARATS/sp-controls` submodule.
- Initialize dependencies with `git submodule update --init --recursive`
  before building this checkout.
- Verification passed for the migrated Release solution build.

Quickload opens the main menu while Sea Power `0.9.3-261116` runs its startup
checks. Startup warnings can bring the game's dialog screen forward. The progress
panel shows the current stage and elapsed time. Hover over
a segment for that phase's name, elapsed time, and last-run duration. Segment
widths follow the previous complete launch and stay fixed as phases finish.
Short phases have a small minimum width. After a
complete run, it estimates progress and time remaining from the last complete
run with the same game build, language, and enabled mods. The Encyclopedia
unlocks when its catalog is ready and opens a reference window on the menu
while pricing is in progress. Scenarios, Tutorials, and ordinary campaigns
open as soon as core data is ready. Point-based campaigns and their saves,
Instant Action, and the Editor wait for current point values. Pricing pauses when a
mission mounts its own data and resumes when you return to the menu. Manual, Credits and Exit are
available from the start.

Sea Power still warms its own INI cache behind the menu. Music can load in the
background as soon as the core data is ready. Voice definitions are ready before
a mission starts, and the game loads voice clips as needed. The Encyclopedia shows the reference
browser while its scene loads. The same browser moves into the in-scene window
with the current search and selection. Unit testing becomes available while
terrain continues streaming. Regular missions still wait for terrain.
The encyclopedia can also load in the campaign picker. It pauses while a mission
has its own data mounted. Quickload batches weapon entries and per-unit frame waits
within a 4 ms budget. An individual weapon or unit can take longer than that.

To try the experimental startup snapshots, create an empty
`%LOCALAPPDATA%/SeaPowerQuickload/cache-unit-loadouts.enabled` file. The first
startup writes the cache for the current game, language, year, and enabled mod
order. Quickload keeps compressed startup snapshots per profile alongside
reusable INI and unit records grouped by the vanilla or mod folder that supplied
them. After a profile change, it checks the resolved paths, file contents, and
shared inputs before reusing those records. Files with aliases, copy directives,
or applied modifiers use the native parser on this path. Changes to shared
libraries can require more unit records to be rebuilt. The Encyclopedia keeps
a full snapshot for each profile.
Start once with the gameplay mods disabled to create a full vanilla snapshot.
Quickload keeps it when you switch back to a modded profile.

Workers hash enabled INI, JSON, and DLL inputs while they read and decompress
the snapshots. The game still attaches live
sensors, assigns unit IDs, applies point values, and publishes its catalog.
The unit metadata stage and INI warmup use Sea Power's path resolution and
cache registration. A complete unit snapshot replaces the native unit worker pass.
The Encyclopedia reuses the cached ranges, hit chances, and cross-references, then
resolves live missile parameters on demand. Changing the Encyclopedia's naming
options rebuilds its catalog. Delete the flag to use the native loading passes
on the next startup.

Sea Power writes its own point-value cache into a Quickload profile directory.
Returning to a matching load order and shared inputs can reuse its unit scores
and ammunition prices. A new profile can start from a compatible profile's
results. The native fingerprint and per-unit hash still apply. Quickload runs
the native scorer in short main-thread batches because it reads live Unity
sensor data. The Encyclopedia shows the profile's last unit and ammo prices
with an "Updating…" label and refreshes them when the new prices are ready.

Quickload stores the resolved sensor data by name and binds it to fresh game
entities on startup. For isolated radar range edits, it rebinds unit metadata,
recalculates scores for units that reference the radar, and refreshes the shared
radar entry used by the Encyclopedia's sensor and unit pages. It checks unit
variants, loadout and nation overrides, and references in the weapon and ammo
libraries. Changes with unresolved dependencies, or changes to other sensor
fields, use the native downstream loaders.

## Starter cache bundles

The local pool keeps parsed INIs, unit records, Encyclopedia weapon records,
and unit reference calculations for each source layer, including disabled
mods. Weapon records use the winning ammo file's hash and a fingerprint of
the shared ammo, sensor, and localization inputs. Unit reference calculations
use the resolved ammo names, dispersion and range values, and the displayed units. Sea Power still builds
the unit pages and binds their shared references.

To build a starter bundle, create
`%LOCALAPPDATA%/SeaPowerQuickload/export-bundles.enabled` alongside the cache
flag, then let startup finish. With gameplay mods disabled this exports a
full vanilla profile and reusable chunks. With EuroMod enabled it exports
separate chunks for each enabled Workshop item to
`%LOCALAPPDATA%/SeaPowerQuickload/CacheBundles`.

Build with `-p:QuickloadCacheBundlesDir=<directory>` to include the exported
bundles in a local deployment. Bundle paths use the source layer and relative
file paths. Quickload checks the game build and content fingerprints before
restoring a full snapshot. Workshop chunks require the matching enabled item,
matching file contents, and a compatible record dependency fingerprint.
Quickload uses the game's resolved file when mods override the same asset.

## Build and deploy

```powershell
dotnet build src/Quickload/Quickload.csproj -p:DeployToGame=false
dotnet build src/Quickload/Quickload.csproj -p:DeployToGame=true
```

Close the game before deployment. `Quickload.dll`, `Quickload.Preloader.dll`,
and `_info.ini` go into
`Sea Power_Data/StreamingAssets/Quickload`. Override `SeaPowerDir` for another
installation. Enable **Quickload** in the game's Mod Manager and restart.

Quickload requests `LoadTiming=Early` in Keel and keeps `StartEarly=true` for
older Keel loaders. If Keel skips splash preparation or lacks that entry point,
the regular plugin entry starts the same cache job. Both paths honor the
startup snapshot setting.
One reader feeds decompression and hashing workers, then checks for reusable
mod cache chunks. Startup keeps completed work when it takes over and validates
prefetched files against the live load order. The larger
loading panel keeps startup dialogs accessible and shrinks into the main menu.
Its timeline starts at process launch and includes pre-menu timings. The plugin ID is
`crimean-tom.quickload`.

With Quickload deployed and Sea Power closed, run
`pwsh -File scripts/test-quickload-menu.ps1` to check button enabled states at
the initial menu, core readiness, and encyclopedia readiness.
