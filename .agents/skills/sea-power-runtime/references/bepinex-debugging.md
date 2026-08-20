# BepInEx startup debugging

Read this reference only when the current Sea Power logs contain no BepInEx
startup lines.

1. Run `pnpm launch`. A direct `Sea Power.exe` launch can trigger
   `RestartAppIfNecessary` before the mod runtime initializes.
2. Confirm `<Sea Power>\winhttp.dll` and `<Sea Power>\doorstop_config.ini` exist.
3. Confirm `doorstop_config.ini` has `enabled = true` and targets
   `BepInEx\core\BepInEx.Preloader.dll`.
4. Confirm that target assembly and the `BepInEx` directory exist, then inspect
   `<Sea Power>\BepInEx\LogOutput.log`.

If any required file is absent, restore the BepInEx/AnchorChain preloader
installation before debugging Crimean Tom.
