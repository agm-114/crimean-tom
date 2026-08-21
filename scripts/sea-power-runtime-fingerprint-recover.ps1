[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedRuntimeFingerprintSha256,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ })]
    [string]$VerifiedPluginDll,

    [Parameter(Mandatory = $true)]
    [string[]]$WorkerIds,

    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ExpectedPluginAssemblySha256 = '',

    [string]$CapabilitiesBaselinePath = '',

    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$RequiredCatalogFingerprintSha256 = '01d4650c0f0ddeb6a106b3e2ded8c79d27ebbcad51319e56470f45d7fa3183ea',

    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR }
        else { 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power' }
    ),

    [switch]$Execute,

    [switch]$ConfirmLiveFingerprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$recomputeScript = Join-Path $repoRoot '.run\build-artifacts\fingerprint-analysis\recompute-runtime-fingerprint.ps1'
$streamingAssets = Join-Path $SeaPowerDir 'Sea Power_Data\StreamingAssets'
$deployDir = Join-Path $streamingAssets 'SeaPowerRuntimeBridge'
$deployedDll = Join-Path $deployDir 'SeaPower.RuntimeBridge.dll'
$processStartTimeToleranceSeconds = 2

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Assert-RestrictedWorkerId([string]$Id) {
    if ($Id -notmatch '^worker-[a-z0-9-]+$') {
        throw "Worker id '$Id' is not a restricted worker id."
    }
    return $Id
}

function Resolve-UtcTimestamp($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) {
        $utc = $Value
        if ($utc.Kind -eq [DateTimeKind]::Local) { $utc = $utc.ToUniversalTime() }
        elseif ($utc.Kind -eq [DateTimeKind]::Unspecified) {
            $utc = [DateTime]::SpecifyKind($utc, [DateTimeKind]::Utc)
        }
        return [DateTimeOffset]::new($utc, [TimeSpan]::Zero)
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return [DateTimeOffset]::Parse(
        $text.Trim(),
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

function Get-ProcessExecutablePath([int]$ProcessId) {
    $searcher = New-Object System.Management.ManagementObjectSearcher(
        "SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = $ProcessId")
    try {
        $processInfo = @($searcher.Get()) | Select-Object -First 1
        if ($null -eq $processInfo) { return '' }
        return [string]$processInfo.ExecutablePath
    }
    finally {
        $searcher.Dispose()
    }
}

function Get-ProcessCommandLine([int]$ProcessId) {
    $searcher = New-Object System.Management.ManagementObjectSearcher(
        "SELECT CommandLine FROM Win32_Process WHERE ProcessId = $ProcessId")
    try {
        $processInfo = @($searcher.Get()) | Select-Object -First 1
        if ($null -eq $processInfo) { return '' }
        return [string]$processInfo.CommandLine
    }
    finally {
        $searcher.Dispose()
    }
}

function Test-ProcessStartTimeMatches($Process, $RecordedStartTime) {
    $recordedStart = Resolve-UtcTimestamp $RecordedStartTime
    if ($null -eq $recordedStart) { return $false }
    $actualStart = [DateTimeOffset]::new($Process.StartTime.ToUniversalTime(), [TimeSpan]::Zero)
    $deltaForward = ($actualStart - $recordedStart).TotalSeconds
    $deltaBackward = ($recordedStart - $actualStart).TotalSeconds
    return $deltaForward -le $processStartTimeToleranceSeconds -and $deltaBackward -le $processStartTimeToleranceSeconds
}

function Test-ValidatedWorkerGameProcess([string]$WorkerId) {
    $runDir = Join-Path $repoRoot ".run\workers\$WorkerId"
    $metadataPath = Join-Path $runDir 'worker.metadata.json'
    if (-not (Test-Path -LiteralPath $metadataPath)) {
        throw "Worker '$WorkerId' metadata is missing at $metadataPath."
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $processId = [int]$metadata.gameProcessId
    if ($processId -le 0) {
        throw "Worker '$WorkerId' metadata does not record a game process id."
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        throw "Worker '$WorkerId' recorded game PID $processId is not running."
    }
    if ($process.ProcessName -cne 'Sea Power') {
        throw "Worker '$WorkerId' recorded PID $processId is not a Sea Power process."
    }

    $executablePath = Get-ProcessExecutablePath $processId
    $expectedExecutable = [System.IO.Path]::GetFullPath([string]$metadata.gameExecutable)
    $resolvedExecutable = [System.IO.Path]::GetFullPath($executablePath)
    if (-not $resolvedExecutable.Equals($expectedExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to stop worker '$WorkerId' PID $processId because executable '$resolvedExecutable' does not match '$expectedExecutable'."
    }

    if (-not (Test-ProcessStartTimeMatches $process $metadata.gameProcessStartTimeUtc)) {
        throw "Refusing to stop worker '$WorkerId' PID $processId because the recorded UTC start time does not match."
    }

    $unityLog = [System.IO.Path]::GetFullPath((Join-Path $runDir 'unity\Player.log'))
    $commandLine = Get-ProcessCommandLine $processId
    $hasUnityLog = $commandLine.IndexOf('-logFile', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 `
        -and $commandLine.IndexOf($unityLog, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $hasUnityLog) {
        throw "Refusing to stop worker '$WorkerId' PID $processId because its command line does not match the worker Unity log path."
    }

    return [pscustomobject]@{
        WorkerId = $WorkerId
        Process = $process
        ProcessId = $processId
        Executable = $expectedExecutable
    }
}

function Stop-ValidatedWorkerGameProcess($Target) {
    Write-Host "Stopping validated game process for worker '$($Target.WorkerId)' (PID $($Target.ProcessId))..."
    $process = $Target.Process
    $null = $process.CloseMainWindow()
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $Target.ProcessId -Force
        Wait-Process -Id $Target.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Get-WorkerRuntimeFingerprint([string]$WorkerId) {
    $runDir = Join-Path $repoRoot ".run\workers\$WorkerId"
    $tokenPath = Join-Path $runDir 'control.token'
    if (-not (Test-Path -LiteralPath $tokenPath)) {
        throw "Worker '$WorkerId' control token is missing at $tokenPath."
    }

    $port = switch -Regex ($WorkerId) {
        '^worker-a$' { 6018 }
        '^worker-b$' { 5464 }
        default { throw "Unknown control port for worker '$WorkerId'." }
    }

    $token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
    $capabilities = Invoke-RestMethod `
        -Method Get `
        -Uri "http://127.0.0.1:$port/game/capabilities" `
        -Headers @{ 'X-Crimean-Tom-Token' = $token } `
        -TimeoutSec 20

    return [pscustomobject]@{
        WorkerId = $WorkerId
        RuntimeFingerprintSha256 = [string]$capabilities.runtimeFingerprint.fingerprintSha256
        PluginAssemblySha256 = [string]$capabilities.runtimeFingerprint.pluginAssemblySha256
        CatalogFingerprintSha256 = [string]$capabilities.runtimeFingerprint.catalogSha256
    }
}

$validatedWorkers = @($WorkerIds | ForEach-Object { Assert-RestrictedWorkerId $_ })
$artifactSha256 = Get-Sha256 $VerifiedPluginDll
$artifactLength = (Get-Item -LiteralPath $VerifiedPluginDll).Length

Write-Host 'Runtime fingerprint recovery preflight'
Write-Host "  Expected runtime fingerprint: $ExpectedRuntimeFingerprintSha256"
Write-Host "  Verified plugin artifact:     $VerifiedPluginDll"
Write-Host "  Plugin assembly SHA-256:      $artifactSha256"
Write-Host "  Plugin artifact length:       $artifactLength"
Write-Host "  Workers:                      $($validatedWorkers -join ', ')"

if (-not [string]::IsNullOrWhiteSpace($ExpectedPluginAssemblySha256) -and $artifactSha256 -cne $ExpectedPluginAssemblySha256) {
    throw "The supplied plugin artifact SHA-256 '$artifactSha256' does not match ExpectedPluginAssemblySha256 '$ExpectedPluginAssemblySha256'."
}

if (Test-Path -LiteralPath $recomputeScript) {
    if ([string]::IsNullOrWhiteSpace($CapabilitiesBaselinePath)) {
        $CapabilitiesBaselinePath = Join-Path $repoRoot '.run\build-artifacts\fingerprint-analysis\worker-a-capabilities-e063.json'
    }

    if (Test-Path -LiteralPath $CapabilitiesBaselinePath) {
        $recomputeOutput = & $recomputeScript `
            -CapabilitiesPath $CapabilitiesBaselinePath `
            -PluginAssemblyCandidates @($artifactSha256)
        $offlineFingerprint = @($recomputeOutput | Select-String -Pattern '=> fingerprint=([0-9a-f]{64})' -AllMatches |
            ForEach-Object { $_.Matches } |
            ForEach-Object { $_.Groups[1].Value } |
            Select-Object -First 1)

        if ($offlineFingerprint) {
            Write-Host "  Offline recomputed fingerprint: $offlineFingerprint"
            if ($offlineFingerprint -cne $ExpectedRuntimeFingerprintSha256) {
                throw "Refusing recovery: verified plugin artifact recomputes to '$offlineFingerprint', not '$ExpectedRuntimeFingerprintSha256'."
            }
        }
    }
    else {
        Write-Warning "Capabilities baseline not found at '$CapabilitiesBaselinePath'; skipping offline fingerprint proof."
    }
}
else {
    Write-Warning "Offline recompute helper not found at '$recomputeScript'; artifact hash proof only."
}

if ($ConfirmLiveFingerprint) {
    foreach ($workerId in $validatedWorkers) {
        $live = Get-WorkerRuntimeFingerprint $workerId
        Write-Host "  Live worker '$workerId' fingerprint: $($live.RuntimeFingerprintSha256) (plugin $($live.PluginAssemblySha256))"
        if ($live.RuntimeFingerprintSha256 -cne $ExpectedRuntimeFingerprintSha256) {
            throw "Worker '$workerId' reports runtime fingerprint '$($live.RuntimeFingerprintSha256)', not '$ExpectedRuntimeFingerprintSha256'."
        }
        if ($live.CatalogFingerprintSha256 -cne $RequiredCatalogFingerprintSha256) {
            throw "Worker '$workerId' catalog fingerprint '$($live.CatalogFingerprintSha256)' does not match the required catalog profile."
        }
    }

    Write-Host 'Live runtime fingerprint confirmation succeeded.'
    return
}

if (-not $Execute) {
    Write-Host ''
    Write-Host 'Preflight succeeded. Re-run with -Execute to stop validated worker game PIDs, deploy the verified plugin, and preserve companions/state.'
    Write-Host 'After relaunching the named workers, run again with -ConfirmLiveFingerprint to prove the live runtime fingerprint.'
    return
}

if (-not $PSCmdlet.ShouldProcess($deployedDll, 'Deploy verified RuntimeBridge plugin')) {
    return
}

$gameTargets = @($validatedWorkers | ForEach-Object { Test-ValidatedWorkerGameProcess $_ })
foreach ($target in $gameTargets) {
    Stop-ValidatedWorkerGameProcess $target
}

New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
Copy-Item -LiteralPath $VerifiedPluginDll -Destination $deployedDll -Force
$deployedSha256 = Get-Sha256 $deployedDll
if ($deployedSha256 -cne $artifactSha256) {
    throw "Deployed DLL hash '$deployedSha256' does not match verified artifact hash '$artifactSha256'."
}

Write-Host ''
Write-Host "Deployed verified RuntimeBridge plugin to $deployedDll"
Write-Host 'Companions were left running. Relaunch only the named worker games, then verify:'
Write-Host "  scripts/sea-power-runtime-fingerprint-recover.ps1 -ExpectedRuntimeFingerprintSha256 $ExpectedRuntimeFingerprintSha256 -VerifiedPluginDll '$VerifiedPluginDll' -WorkerIds $($validatedWorkers -join ',') -ConfirmLiveFingerprint"
