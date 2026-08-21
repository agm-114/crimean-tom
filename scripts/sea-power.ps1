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
    [int]$TimeoutSeconds = 90,

    [string]$CompanionRunDirectory,

    [string]$ControlUrl,

    [string]$CampaignGrpcUrl,

    [string]$CampaignHttpUrl,

    [string]$CampaignId,

    [string]$CampaignRunDirectory,

    [string]$CampaignTokenFile,

    [string]$CampaignStateDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot 'CrimeanTom.sln'
$gameExe = Join-Path $SeaPowerDir 'Sea Power.exe'
$steamAppId = 1286220
$steamLaunchUri = "steam://rungameid/$steamAppId"
$streamingAssets = Join-Path $SeaPowerDir 'Sea Power_Data\StreamingAssets'
$deployDir = Join-Path $streamingAssets 'SeaPowerRuntimeBridge'
$legacyDeployDir = Join-Path $streamingAssets 'CrimeanTom'
$deployedDll = Join-Path $deployDir 'SeaPower.RuntimeBridge.dll'
$portOperationsSourceDir = Join-Path $repoRoot 'content\campaigns\crimean-tom-port-operations'
$portOperationsDeployDir = Join-Path $streamingAssets 'user\campaigns\crimean-tom-port-operations'
$builtDir = Join-Path $repoRoot "src\SeaPower.RuntimeBridge\bin\$Configuration"
$builtDll = Join-Path $builtDir 'SeaPower.RuntimeBridge.dll'
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Triassic Games\Sea Power\Player.log'
$bepInExLog = Join-Path $SeaPowerDir 'BepInEx\LogOutput.log'
$anchorPreloader = Join-Path $SeaPowerDir 'BepInEx\plugins\AnchorChainPreloader\AnchorChain.Preloader.dll'
$doorstopProxy = Join-Path $SeaPowerDir 'winhttp.dll'
$healthMarker = '[Sea Power Runtime Bridge] loaded via AnchorChain'
$companionControlScript = Join-Path $PSScriptRoot 'crimean-tom-control.ps1'

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

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Remove-LegacyPluginDeployment {
    if (-not (Test-Path -LiteralPath $legacyDeployDir)) { return }

    $resolvedStreamingAssets = [System.IO.Path]::GetFullPath($streamingAssets).TrimEnd('\')
    $resolvedLegacyDeployDir = [System.IO.Path]::GetFullPath($legacyDeployDir).TrimEnd('\')
    $resolvedLegacyParent = [System.IO.Path]::GetDirectoryName($resolvedLegacyDeployDir).TrimEnd('\')
    if (-not $resolvedLegacyParent.Equals(
        $resolvedStreamingAssets,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove legacy plugin directory outside StreamingAssets: $resolvedLegacyDeployDir"
    }

    Write-Host "Removing legacy in-game plugin directory $resolvedLegacyDeployDir..."
    Remove-Item -LiteralPath $resolvedLegacyDeployDir -Recurse -Force
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
        throw "The deployed SeaPower.RuntimeBridge.dll does not match the $Configuration build. Run pnpm build again."
    }
}

function Deploy-PortOperationsContent {
    if (-not (Test-Path -LiteralPath $portOperationsSourceDir -PathType Container)) {
        throw "Crimean Tom port-operations campaign source not found at $portOperationsSourceDir."
    }

    New-Item -ItemType Directory -Path $portOperationsDeployDir -Force | Out-Null
    $sourceFiles = @(Get-ChildItem -LiteralPath $portOperationsSourceDir -File)
    if ($sourceFiles.Count -eq 0) {
        throw "Crimean Tom port-operations campaign source is empty at $portOperationsSourceDir."
    }

    foreach ($sourceFile in $sourceFiles) {
        $deployedFile = Join-Path $portOperationsDeployDir $sourceFile.Name
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $deployedFile -Force

        $sourceHash = Get-HashValue $sourceFile.FullName
        $deployedHash = Get-HashValue $deployedFile
        if ($sourceHash -ne $deployedHash) {
            throw "The deployed port-operations asset $($sourceFile.Name) does not match its repository source."
        }
    }

    Write-Host "Deployed Crimean Tom port-operations campaign to $portOperationsDeployDir"
}

function Invoke-Build {
    Assert-RuntimeInstalled
    & $companionControlScript stop
    Stop-SeaPower
    Remove-LegacyPluginDeployment
    Write-Host "Building and deploying Sea Power Runtime Bridge ($Configuration)..."
    & dotnet build $solutionPath --configuration $Configuration "-p:SeaPowerDir=$SeaPowerDir" '-p:CopyPluginToGame=true'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
    Deploy-PortOperationsContent
    Assert-DeployedDllMatchesBuild
    Write-Host "Deployed DLL matches the build: $deployedDll"
}

function Invoke-Deploy {
    Assert-RuntimeInstalled
    Remove-LegacyPluginDeployment
    if (-not (Test-Path -LiteralPath $builtDll)) {
        throw "Built DLL not found at $builtDll. Run pnpm build first."
    }

    New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
    Copy-Item -LiteralPath $builtDll -Destination $deployedDll -Force

    $builtPdb = Join-Path $builtDir 'SeaPower.RuntimeBridge.pdb'
    if (Test-Path -LiteralPath $builtPdb) {
        Copy-Item -LiteralPath $builtPdb -Destination (Join-Path $deployDir 'SeaPower.RuntimeBridge.pdb') -Force
    }

    Deploy-PortOperationsContent

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
                Where-Object { $_ -match 'Sea Power Runtime Bridge|SeaPower\.RuntimeBridge|Crimean Tom|CrimeanTom|AnchorChain|Exception|\[Error' }
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
    throw "Sea Power did not write the Sea Power Runtime Bridge health marker within $TimeoutSeconds seconds. See $playerLog and $bepInExLog."
}

function Start-SeaPowerAndVerify {
    Assert-RuntimeInstalled
    Assert-DeployedDllMatchesBuild
    Stop-SeaPower

    $companionParameters = @{
        Configuration = $Configuration
        SkipBuild = $true
    }
    foreach ($setting in @{
        RunDirectory = $CompanionRunDirectory
        ControlUrl = $ControlUrl
        CampaignGrpcUrl = $CampaignGrpcUrl
        CampaignHttpUrl = $CampaignHttpUrl
        CampaignId = $CampaignId
        CampaignRunDirectory = $CampaignRunDirectory
        CampaignTokenFile = $CampaignTokenFile
        CampaignStateDirectory = $CampaignStateDirectory
    }.GetEnumerator()) {
        if (-not [string]::IsNullOrWhiteSpace($setting.Value)) {
            $companionParameters[$setting.Key] = $setting.Value
        }
    }
    & $companionControlScript start @companionParameters

    $startedAtUtc = [DateTime]::UtcNow
    $processWaitSeconds = [Math]::Max(30, [Math]::Min(60, $TimeoutSeconds))
    $processDeadline = [DateTime]::UtcNow.AddSeconds($processWaitSeconds)
    $nextLaunchAttemptUtc = [DateTime]::MinValue
    $launchAttempts = 0
    $processes = @()
    while ($processes.Count -eq 0 -and [DateTime]::UtcNow -lt $processDeadline) {
        if ([DateTime]::UtcNow -ge $nextLaunchAttemptUtc) {
            $launchAttempts++
            Write-Host "Requesting Sea Power launch through Steam (attempt $launchAttempts)..."
            Start-Process $steamLaunchUri
            $nextLaunchAttemptUtc = [DateTime]::UtcNow.AddSeconds(10)
        }

        Start-Sleep -Milliseconds 500
        $processes = @(Get-SeaPowerProcess)
    }
    if ($processes.Count -eq 0) {
        throw "Steam did not start Sea Power after $launchAttempts attempts within $processWaitSeconds seconds. Launch URI: $steamLaunchUri"
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
        Write-Host "No Sea Power Runtime Bridge, Crimean Tom, AnchorChain, or error lines found in $playerLog or $bepInExLog."
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
