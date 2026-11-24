# Quickload

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
widths follow measured time for completed phases and the previous run for
upcoming phases. Short phases have a small minimum width. After a
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

Close the game before deployment. The two DLLs and `_info.ini` go into
`Sea Power_Data/StreamingAssets/Quickload`. Override `SeaPowerDir` for another
installation. Enable **Quickload** in the game's Mod Manager and restart.

Quickload requests `LoadTiming=Early` in Keel and keeps `StartEarly=true` for
older Keel loaders. Its normal plugin entry point installs startup patches and
queues cache work after Keel checks the mod's enabled state and dependencies.
One reader feeds decompression and hashing workers, then checks for reusable
mod cache chunks. Startup keeps completed work when it takes over and validates
prefetched files against the live load order. The larger
loading panel keeps startup dialogs accessible and shrinks into the main menu.
Its timeline starts at process launch and includes pre-menu timings. The plugin ID is
`crimean-tom.quickload`.

With Quickload deployed and Sea Power closed, run
`pwsh -File scripts/test-quickload-menu.ps1` to check button enabled states at
the initial menu, core readiness, and encyclopedia readiness.
