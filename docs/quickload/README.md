# Quickload

Quickload opens the main menu after Sea Power `0.9.2-261113` runs its startup
checks. The progress panel shows the current stage and elapsed time. After a
complete run, it estimates progress and time remaining from the last complete
run with the same game build, language, and enabled mods. Scenarios, Tutorials,
Campaigns and Continue unlock when the game's startup data is ready. Editor,
Encyclopedia, Options and Mods unlock after the encyclopedia finishes. Manual,
Credits and Exit are available from the start.

Sea Power still builds its own INI cache and unit metadata. Quickload runs the
native startup routines behind the menu. Music loads in the background after
Scenarios unlocks. Voice definitions are ready before a mission starts, and the
game loads voice clips as needed. The Encyclopedia shows the reference
browser while its scene loads. The same browser moves into the in-scene window
with the current search and selection. Unit testing becomes available while
terrain continues streaming. Regular missions still wait for terrain.
The encyclopedia pauses when you leave the main menu so it does not read from
a mounted mission. While it runs, Quickload batches its per-unit frame waits
within a 4 ms budget. An individual unit can take longer than that.

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
