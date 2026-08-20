# Runtime debugging

## Evidence order

1. Run `pnpm status` to confirm the game, preloader, build, deployed DLL, and
   process state.
2. Run `pnpm logs` to inspect the end of both Unity's `Player.log` and BepInEx's
   `LogOutput.log`.
3. Compare the first failing layer below before changing code.

| Evidence | Interpretation | Next check |
| --- | --- | --- |
| No BepInEx lines | Doorstop/BepInEx did not start | Confirm `winhttp.dll`, `doorstop_config.ini`, and BepInEx are installed |
| `Could not find AnchorChain chainloader` | The preloader ran but could not locate Workshop AnchorChain | Confirm the Sea Power Workshop item is installed and visible to the game |
| AnchorChain loads, no Crimean Tom line | The plugin was not discovered or rejected | Confirm deploy path, hashes, and nearby AnchorChain exceptions |
| Crimean Tom health marker appears | The exact deployed assembly reached `TriggerEntryPoint` | Investigate feature-specific code after the entry point |
| Hash mismatch | The game folder contains a stale build | Stop the game and run `pnpm build` |
| `RestartAppIfNecessary returned true` | Sea Power was started outside Steam | Use `pnpm launch`; do not start the EXE directly |

## Logs

- Unity and AnchorChain: `%USERPROFILE%\AppData\LocalLow\Triassic Games\Sea Power\Player.log`
- BepInEx: `<Sea Power>\BepInEx\LogOutput.log`

Search by plugin name and GUID as well as generic exceptions:

```powershell
rg -n "Crimean Tom|CrimeanTom|crimean\.tom|AnchorChain|Exception|\[Error" `
  "$env:USERPROFILE\AppData\LocalLow\Triassic Games\Sea Power\Player.log" `
  "$env:SEA_POWER_DIR\BepInEx\LogOutput.log"
```

Do not accept an old marker when testing a new build. `pnpm launch` records the
launch time and waits for the current `Player.log` to be written after it.

## Runtime inspection

UnityExplorer may be present in this developer installation, but it is an
optional interactive inspector, not a health check. First prove that the plugin
entry point loaded through the log marker. Use UnityExplorer only for targeted
object/component inspection requested by the user, and leave game state and
files unchanged unless the task authorizes a mutation.
