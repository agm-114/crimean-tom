# Runtime debugging

## Evidence order

1. Run `pnpm status` to confirm the game, preloader, build, deployed DLL, and
   process state.
2. Run `pnpm logs` to inspect the end of both Unity's `Player.log` and BepInEx's
   `LogOutput.log`.
3. Compare the first failing layer below before changing code.

| Evidence | Meaning and action |
| --- | --- |
| No BepInEx lines | Read [BepInEx startup debugging](bepinex-debugging.md). |
| `Could not find AnchorChain chainloader` | Read [AnchorChain startup debugging](anchorchain-debugging.md). |
| AnchorChain loads, but Sea Power Runtime Bridge does not | Run `pnpm status`; rebuild on a hash mismatch, otherwise inspect nearby AnchorChain exceptions. |
| Sea Power Runtime Bridge health marker appears | The deployed assembly reached `TriggerEntryPoint`; continue with feature-specific debugging. |

## Logs

- Unity and AnchorChain: `%USERPROFILE%\AppData\LocalLow\Triassic Games\Sea Power\Player.log`
- BepInEx: `<Sea Power>\BepInEx\LogOutput.log`

Search by plugin name and GUID as well as generic exceptions:

```powershell
rg -n "Sea Power Runtime Bridge|SeaPower\.RuntimeBridge|seapower\.runtime-bridge|Crimean Tom|AnchorChain|Exception|\[Error" `
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
