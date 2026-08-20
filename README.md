# Crimean Tom

An [AnchorChain](https://github.com/SeaPower-Modders/AnchorChain) plugin for Sea Power.

## Local development

The default installation is `C:\Program Files (x86)\Steam\steamapps\common\Sea Power`.
Set `SEA_POWER_DIR` when the game is installed elsewhere.

Every successful build deploys only `CrimeanTom.dll` and its portable PDB to
`Sea Power_Data\StreamingAssets\CrimeanTom`. It never copies dependency DLLs.

```powershell
pnpm build       # build and deploy
pnpm launch      # launch the game and wait for the plugin health marker
pnpm dev         # restart as needed, then build, deploy, launch, and verify
pnpm verify      # compare deployed/build hashes and inspect the current Player.log
pnpm logs        # show recent Crimean Tom / AnchorChain / error log lines
pnpm status      # show paths, process state, hashes, and current load state
```

Launch commands use Steam app ID `1286220`. Starting `Sea Power.exe` directly
causes Steamworks to stop and restart the process, which breaks a deterministic
BepInEx/AnchorChain startup check.

`pnpm dev` succeeds only after a fresh game launch writes
`[Crimean Tom] loaded via AnchorChain` to
`%USERPROFILE%\AppData\LocalLow\Triassic Games\Sea Power\Player.log`.

Build, deploy, and launch commands stop an existing Sea Power process when a
fresh DLL must be loaded.
