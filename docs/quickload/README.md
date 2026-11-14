# Quickload

Quickload opens the main menu while Sea Power `0.9.2-261113` runs its startup
checks. Startup warnings can bring the game's dialog screen forward. The progress
panel shows the current stage and elapsed time. Hover over
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
results. The native fingerprint and per-unit hash still apply.

Quickload stores the resolved sensor data by name and binds it to fresh game
entities on startup. For isolated radar range edits, it rebinds unit metadata,
recalculates scores for units that reference the radar, and refreshes the shared
radar entry used by the Encyclopedia's sensor and unit pages. It checks unit
variants, loadout and nation overrides, and references in the weapon and ammo
libraries. Changes with unresolved dependencies, or changes to other sensor
fields, use the native downstream loaders.

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
