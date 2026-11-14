# Quickload

Quickload opens the main menu after Sea Power `0.9.2-261113` runs its startup
checks. The progress panel shows the current stage and elapsed time. Hover over
a segment for that phase's name, elapsed time, and last-run duration. Segment
widths follow measured time for completed phases and the previous run for
upcoming phases. Short phases have a small minimum width. After a
complete run, it estimates progress and time remaining from the last complete
run with the same game build, language, and enabled mods. Scenarios, Tutorials,
Campaigns and Continue unlock when the game's startup data is ready. Editor,
Encyclopedia, Options and Mods unlock after the encyclopedia finishes. Manual,
Credits and Exit are available from the start.

Sea Power still warms its own INI cache behind the menu. Music loads in the background after
Scenarios unlocks. Voice definitions are ready before a mission starts, and the
game loads voice clips as needed. The Encyclopedia shows the reference
browser while its scene loads. The same browser moves into the in-scene window
with the current search and selection. Unit testing becomes available while
terrain continues streaming. Regular missions still wait for terrain.
The encyclopedia pauses when you leave the main menu so it does not read from
a mounted mission. Quickload batches weapon entries and per-unit frame waits
within a 4 ms budget. An individual weapon or unit can take longer than that.

To try the experimental startup snapshots, create an empty
`%LOCALAPPDATA%/SeaPowerQuickload/cache-unit-loadouts.enabled` file. The first
startup writes the cache for the current game, language, year, and enabled mod
order. Quickload compresses the resolved unit metadata, parsed INIs, and
Encyclopedia records. Subsequent runs hash enabled INI, JSON, and DLL inputs
while workers read and decompress the snapshots. The game still attaches live
sensors, assigns unit IDs, applies point values, and publishes its catalog.
The INI warmup uses Sea Power's path resolution and cache registration. The
Encyclopedia reuses the cached ranges, hit chances, and cross-references, then
resolves live missile parameters on demand. Changing the Encyclopedia's naming
options rebuilds its catalog. Delete the flag to use the native loading passes
on the next startup.

## Build and deploy

```powershell
dotnet build src/Quickload/Quickload.csproj -p:DeployToGame=false
dotnet build src/Quickload/Quickload.csproj -p:DeployToGame=true
```

Close the game before deployment. The DLL and `_info.ini` go into
`Sea Power_Data/StreamingAssets/Quickload`. Override `SeaPowerDir` for another
installation. Enable **Quickload** in the game's Mod Manager and restart.

Keel uses the manifest's `StartEarly` flag to install the patches before the
native startup stages. The plugin ID is `crimean-tom.quickload`.

With Quickload deployed and Sea Power closed, run
`pwsh -File scripts/test-quickload-menu.ps1` to check button enabled states at
the initial menu, core readiness, and encyclopedia readiness.
