# Command timeout guidance

Use this runbook for assembly research and any command that can remain quiet
while doing expensive work. A lack of output is not evidence that a command is
making progress.

## Standard limits

| Operation | Timeout |
| --- | ---: |
| `ilspycmd --list` or `ilspycmd --type` | 60 seconds |
| Full assembly decompile | 600 seconds per assembly |

Start other exploratory commands with a 60-second bound. Increase the bound
only when the command is known to make useful progress and cannot be narrowed.

## Run bounded commands

Wrap the complete command, including its pipeline, with
`scripts/invoke-with-timeout.ps1`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File scripts/invoke-with-timeout.ps1 `
  -TimeoutSeconds 60 `
  -Command 'ilspycmd "C:\Program Files (x86)\Steam\steamapps\common\Sea Power\Sea Power_Data\Managed\Seapower-Scripts.dll" --list c --disable-updatecheck | Select-String -Pattern CameraManager'
```

Always pass `--disable-updatecheck` to `ilspycmd`. This prevents an update prompt
from consuming the command's time budget.

Set the calling shell or automation tool's own wait limit slightly longer than
the wrapper timeout. For tools that expose `block_until_ms`, allow the wrapper's
timeout plus a few seconds so it can kill the child process tree and report the
result.

The full decompile script already applies the 600-second per-assembly bound:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File scripts/decompile-sea-power.ps1 `
  -Target game
```

## Handle a timeout

The wrapper writes `COMMAND_TIMEOUT`, kills the child process tree, and exits
with code 124. Treat either signal as a failed command. Do not rerun the same
command with an open-ended wait.

Narrow the next attempt by reducing the assembly set, requesting one known
type, using a more specific search pattern, or decompiling once and searching
the resulting files. If a narrower command still times out, record the failed
command and stop that line of investigation until there is a bounded
alternative.
