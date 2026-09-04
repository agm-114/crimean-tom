[CmdletBinding()]
param(
    [ValidateSet('Status', 'Launch', 'Logs')]
    [string]$Action = 'Status',

    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) {
            $env:SEA_POWER_DIR
        }
        else {
            'C:\Program Files (x86)\Steam\steamapps\common\Sea Power'
        }
    ),

    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 60,

    [string]$RequirePattern,

    [ValidateRange(1, 5000)]
    [int]$TailLines = 200
)

$ErrorActionPreference = 'Stop'
$gameExecutable = Join-Path $SeaPowerDir 'Sea Power.exe'
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Triassic Games\Sea Power\Player.log'
$bepInExLog = Join-Path $SeaPowerDir 'BepInEx\LogOutput.log'

function Get-SeaPowerProcess {
    Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

function Show-Status {
    $process = Get-SeaPowerProcess
    [pscustomobject]@{
        GameDirectory = $SeaPowerDir
        GameExecutableExists = Test-Path -LiteralPath $gameExecutable
        ProcessId = if ($process) { $process.Id } else { $null }
        PlayerLog = $playerLog
        PlayerLogExists = Test-Path -LiteralPath $playerLog
        PlayerLogLastWriteUtc = if (Test-Path -LiteralPath $playerLog) {
            (Get-Item -LiteralPath $playerLog).LastWriteTimeUtc
        }
        else {
            $null
        }
        BepInExLogExists = Test-Path -LiteralPath $bepInExLog
    } | Format-List
}

function Show-Logs {
    if (-not (Test-Path -LiteralPath $playerLog)) {
        throw "Player.log was not found at '$playerLog'."
    }

    Write-Output "Player.log: $playerLog"
    Get-Content -LiteralPath $playerLog -Tail $TailLines

    if (Test-Path -LiteralPath $bepInExLog) {
        Write-Output ""
        Write-Output "BepInEx log: $bepInExLog"
        Get-Content -LiteralPath $bepInExLog -Tail $TailLines
    }
}

function Start-And-WaitForFreshLog {
    if (-not (Test-Path -LiteralPath $gameExecutable)) {
        throw "Sea Power was not found at '$SeaPowerDir'. Set SEA_POWER_DIR or pass -SeaPowerDir."
    }

    $existingProcess = Get-SeaPowerProcess
    if ($existingProcess) {
        throw "Sea Power is already running as process $($existingProcess.Id). The helper will not stop it."
    }

    $launchUtc = [DateTime]::UtcNow
    Start-Process 'steam://run/1286220'

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $playerLog) {
            $log = Get-Item -LiteralPath $playerLog
            if ($log.LastWriteTimeUtc -ge $launchUtc.AddSeconds(-2)) {
                if (-not $RequirePattern) {
                    Write-Output "Sea Power wrote a fresh Player.log at $($log.LastWriteTimeUtc.ToString('O'))."
                    return
                }

                if (Select-String -LiteralPath $playerLog -Pattern $RequirePattern -Quiet) {
                    Write-Output "Sea Power wrote a fresh Player.log containing '$RequirePattern'."
                    return
                }
            }
        }

        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($RequirePattern) {
        throw "Sea Power did not write a fresh Player.log containing '$RequirePattern' within $TimeoutSeconds seconds."
    }

    throw "Sea Power did not write a fresh Player.log within $TimeoutSeconds seconds."
}

switch ($Action) {
    'Status' { Show-Status }
    'Launch' { Start-And-WaitForFreshLog }
    'Logs' { Show-Logs }
}
