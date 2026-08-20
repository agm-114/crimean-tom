[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'deploy', 'launch', 'verify', 'dev', 'logs', 'status')]
    [string]$Action = 'dev',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR }
        else { 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power' }
    ),

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot 'CrimeanTom.sln'
$gameExe = Join-Path $SeaPowerDir 'Sea Power.exe'
$steamAppId = 1286220
$steamLaunchUri = "steam://rungameid/$steamAppId"
$streamingAssets = Join-Path $SeaPowerDir 'Sea Power_Data\StreamingAssets'
$deployDir = Join-Path $streamingAssets 'CrimeanTom'
$deployedDll = Join-Path $deployDir 'CrimeanTom.dll'
$builtDir = Join-Path $repoRoot "src\CrimeanTom\bin\$Configuration"
$builtDll = Join-Path $builtDir 'CrimeanTom.dll'
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Triassic Games\Sea Power\Player.log'
$bepInExLog = Join-Path $SeaPowerDir 'BepInEx\LogOutput.log'
$anchorPreloader = Join-Path $SeaPowerDir 'BepInEx\plugins\AnchorChainPreloader\AnchorChain.Preloader.dll'
$doorstopProxy = Join-Path $SeaPowerDir 'winhttp.dll'
$healthMarker = '[Crimean Tom] loaded via AnchorChain'

function Assert-RuntimeInstalled {
    $requiredPaths = @($gameExe, $streamingAssets, $anchorPreloader, $doorstopProxy)
    $missingPaths = @($requiredPaths | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missingPaths.Count -gt 0) {
        throw "Sea Power's mod runtime is incomplete. Missing: $($missingPaths -join ', ')"
    }
}

function Get-SeaPowerProcess {
    @(Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue)
}

function Stop-SeaPower {
    $processes = @(Get-SeaPowerProcess)
    if ($processes.Count -eq 0) { return }

    Write-Host "Stopping Sea Power (PID $($processes.Id -join ', ')) before deploying the new build..."
    foreach ($process in $processes) {
        $null = $process.CloseMainWindow()
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (@(Get-SeaPowerProcess).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(Get-SeaPowerProcess)
    if ($remaining.Count -gt 0) {
        $remaining | Stop-Process -Force
        $remaining | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Get-HashValue([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-DeployedDllMatchesBuild {
    if (-not (Test-Path -LiteralPath $builtDll)) {
        throw "Built DLL not found at $builtDll. Run pnpm build first."
    }
    if (-not (Test-Path -LiteralPath $deployedDll)) {
        throw "Deployed DLL not found at $deployedDll. Run pnpm build or pnpm deploy first."
    }

    $builtHash = Get-HashValue $builtDll
    $deployedHash = Get-HashValue $deployedDll
    if ($builtHash -ne $deployedHash) {
        throw "The deployed CrimeanTom.dll does not match the $Configuration build. Run pnpm build again."
    }
}

function Invoke-Build {
    Assert-RuntimeInstalled
    Stop-SeaPower
    Write-Host "Building and deploying Crimean Tom ($Configuration)..."
    & dotnet build $solutionPath --configuration $Configuration "-p:SeaPowerDir=$SeaPowerDir" '-p:CopyPluginToGame=true'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
    Assert-DeployedDllMatchesBuild
    Write-Host "Deployed DLL matches the build: $deployedDll"
}

function Invoke-Deploy {
    Assert-RuntimeInstalled
    if (-not (Test-Path -LiteralPath $builtDll)) {
        throw "Built DLL not found at $builtDll. Run pnpm build first."
    }

    New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
    Copy-Item -LiteralPath $builtDll -Destination $deployedDll -Force

    $builtPdb = Join-Path $builtDir 'CrimeanTom.pdb'
    if (Test-Path -LiteralPath $builtPdb) {
        Copy-Item -LiteralPath $builtPdb -Destination (Join-Path $deployDir 'CrimeanTom.pdb') -Force
    }

    Assert-DeployedDllMatchesBuild
    Write-Host "Deployed $builtDll to $deployDir"
}

function Get-LogText([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    try { Get-Content -LiteralPath $Path -Raw -ErrorAction Stop }
    catch { '' }
}

function Get-DiagnosticLines {
    $lines = @()
    foreach ($path in @($playerLog, $bepInExLog)) {
        if (Test-Path -LiteralPath $path) {
            $lines += Get-Content -LiteralPath $path -Tail 300 -ErrorAction SilentlyContinue |
                Where-Object { $_ -match 'Crimean Tom|CrimeanTom|AnchorChain|Exception|\[Error' }
        }
    }
    @($lines | Select-Object -Last 40)
}

function Wait-ForPlugin([datetime]$NotBeforeUtc) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $playerLog) {
            $logInfo = Get-Item -LiteralPath $playerLog
            if ($logInfo.LastWriteTimeUtc -ge $NotBeforeUtc.AddSeconds(-2)) {
                $text = Get-LogText $playerLog
                if ($text.Contains($healthMarker)) {
                    $matchingLine = Get-Content -LiteralPath $playerLog -ErrorAction SilentlyContinue |
                        Where-Object { $_.Contains($healthMarker) } |
                        Select-Object -Last 1
                    Write-Host "Plugin verified in Player.log: $matchingLine"
                    return
                }
            }
        }

        $processes = @(Get-SeaPowerProcess)
        if ($processes.Count -eq 0 -and [DateTime]::UtcNow -gt $NotBeforeUtc.AddSeconds(5)) {
            break
        }
        Start-Sleep -Milliseconds 500
    }

    $diagnostics = @(Get-DiagnosticLines)
    if ($diagnostics.Count -gt 0) {
        Write-Host 'Recent runtime diagnostics:'
        $diagnostics | ForEach-Object { Write-Host "  $_" }
    }
    throw "Sea Power did not write the Crimean Tom health marker within $TimeoutSeconds seconds. See $playerLog and $bepInExLog."
}

function Start-SeaPowerAndVerify {
    Assert-RuntimeInstalled
    Assert-DeployedDllMatchesBuild
    Stop-SeaPower

    $startedAtUtc = [DateTime]::UtcNow
    Start-Process $steamLaunchUri

    $processDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $processes = @()
    while ($processes.Count -eq 0 -and [DateTime]::UtcNow -lt $processDeadline) {
        Start-Sleep -Milliseconds 500
        $processes = @(Get-SeaPowerProcess)
    }
    if ($processes.Count -eq 0) {
        throw "Steam did not start Sea Power within 30 seconds. Launch URI: $steamLaunchUri"
    }

    Write-Host "Launched Sea Power through Steam (PID $($processes.Id -join ', ')); waiting for the plugin health marker..."
    Wait-ForPlugin $startedAtUtc
}

function Test-CurrentPluginState {
    Assert-RuntimeInstalled
    Assert-DeployedDllMatchesBuild
    $text = Get-LogText $playerLog
    if (-not $text.Contains($healthMarker)) {
        throw "The current Player.log does not contain '$healthMarker'. Run pnpm launch for a fresh runtime check."
    }
    $matchingLine = Get-Content -LiteralPath $playerLog -ErrorAction SilentlyContinue |
        Where-Object { $_.Contains($healthMarker) } |
        Select-Object -Last 1
    Write-Host "Plugin is deployed and present in Player.log: $matchingLine"
}

function Show-Logs {
    $lines = @(Get-DiagnosticLines)
    if ($lines.Count -eq 0) {
        Write-Host "No Crimean Tom, AnchorChain, or error lines found in $playerLog or $bepInExLog."
        return
    }
    $lines | ForEach-Object { Write-Host $_ }
}

function Show-Status {
    $processes = @(Get-SeaPowerProcess)
    $builtHash = Get-HashValue $builtDll
    $deployedHash = Get-HashValue $deployedDll
    $logText = Get-LogText $playerLog

    [pscustomobject]@{
        GameDirectory = $SeaPowerDir
        GameInstalled = Test-Path -LiteralPath $gameExe
        AnchorChainPreloaderInstalled = Test-Path -LiteralPath $anchorPreloader
        RunningProcessIds = if ($processes.Count -gt 0) { $processes.Id -join ', ' } else { '' }
        BuiltDll = $builtDll
        DeployedDll = $deployedDll
        HashesMatch = $null -ne $builtHash -and $builtHash -eq $deployedHash
        HealthMarkerInCurrentLog = $logText.Contains($healthMarker)
        PlayerLog = $playerLog
    } | Format-List
}

try {
    switch ($Action) {
        'build' { Invoke-Build }
        'deploy' { Stop-SeaPower; Invoke-Deploy }
        'launch' { Start-SeaPowerAndVerify }
        'verify' { Test-CurrentPluginState }
        'dev' { Invoke-Build; Start-SeaPowerAndVerify }
        'logs' { Show-Logs }
        'status' { Show-Status }
    }
}
catch {
    Write-Error $_
    exit 1
}
