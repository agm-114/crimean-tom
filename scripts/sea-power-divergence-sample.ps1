[CmdletBinding()]
param(
    [string]$SourceWorkerId = 'worker-a',

    [string]$DestinationWorkerId = 'worker-b',

    [string]$WorkersRunRoot,

    [string]$SourceControlUrl,

    [string]$DestinationControlUrl,

    [ValidateRange(2, 120)]
    [int]$SampleCount = 8,

    [ValidateRange(1, 60)]
    [int]$SampleIntervalSeconds = 5,

    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($WorkersRunRoot)) {
    $WorkersRunRoot = Join-Path $repoRoot '.run\workers'
}

function Get-DefaultWorkerControlPort([string]$Id) {
    if ($Id -ieq 'default') { return 5137 }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Id))
    }
    finally {
        $sha256.Dispose()
    }
    return 5140 + ([BitConverter]::ToUInt32($hash, 0) % 900)
}

function Get-WorkerWorld([string]$WorkerId, [string]$ControlUrl) {
    $runDir = Join-Path $WorkersRunRoot $WorkerId
    $tokenFile = Join-Path $runDir 'control.token'
    if (-not (Test-Path -LiteralPath $tokenFile)) {
        throw "Companion control token not found at $tokenFile."
    }
    $token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
    $headers = @{ 'X-Crimean-Tom-Token' = $token }
    $world = Invoke-RestMethod -Method Get -Uri "$ControlUrl/game/world" -Headers $headers -TimeoutSec 15
    return [ordered]@{
        workerId = $WorkerId
        controlUrl = $ControlUrl
        capturedAtUnixMilliseconds = $world.capturedAtUnixMilliseconds
        schemaVersion = $world.schemaVersion
        sceneName = $world.sceneName
        simulationTimeSeconds = $world.simulationTimeSeconds
        timeCompression = $world.timeCompression
        isPaused = $world.isPaused
        engineTimeScale = $world.engineTimeScale
        engineFixedDeltaTime = $world.engineFixedDeltaTime
        engineUnscaledDeltaTime = $world.engineUnscaledDeltaTime
        measuredFramesPerSecond = $world.measuredFramesPerSecond
        observedObjectCount = $world.observedObjectCount
        kinematicsSha256 = $world.kinematicsSha256
        canonicalSha256 = $world.canonicalSha256
        identitySha256 = $world.identitySha256
        logisticsSha256 = $world.logisticsSha256
        damageSha256 = $world.damageSha256
        weaponsSha256 = $world.weaponsSha256
    }
}

if ([string]::IsNullOrWhiteSpace($SourceControlUrl)) {
    $SourceControlUrl = "http://127.0.0.1:$(Get-DefaultWorkerControlPort $SourceWorkerId)"
}
if ([string]::IsNullOrWhiteSpace($DestinationControlUrl)) {
    $DestinationControlUrl = "http://127.0.0.1:$(Get-DefaultWorkerControlPort $DestinationWorkerId)"
}

$samples = @()
for ($index = 0; $index -lt $SampleCount; $index++) {
    $left = Get-WorkerWorld $SourceWorkerId $SourceControlUrl
    $right = Get-WorkerWorld $DestinationWorkerId $DestinationControlUrl
    $simulationDelta = $null
    if ($null -ne $left.simulationTimeSeconds -and $null -ne $right.simulationTimeSeconds) {
        $simulationDelta = [double]$right.simulationTimeSeconds - [double]$left.simulationTimeSeconds
    }
    $samples += [ordered]@{
        index = $index
        recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        source = $left
        destination = $right
        simulationTimeDeltaSeconds = $simulationDelta
        canonicalSha256Match = $left.canonicalSha256 -eq $right.canonicalSha256
        kinematicsSha256Match = $left.kinematicsSha256 -eq $right.kinematicsSha256
        identitySha256Match = $left.identitySha256 -eq $right.identitySha256
        timeScaleDelta = [double]$right.engineTimeScale - [double]$left.engineTimeScale
    }
    if ($index -lt $SampleCount - 1) {
        Start-Sleep -Seconds $SampleIntervalSeconds
    }
}

$canonicalMismatches = @($samples | Where-Object { -not $_.canonicalSha256Match }).Count
$kinematicsMismatches = @($samples | Where-Object { -not $_.kinematicsSha256Match }).Count
$maxAbsSimulationDelta = (
    @($samples | Where-Object { $null -ne $_.simulationTimeDeltaSeconds } | ForEach-Object {
        [Math]::Abs([double]$_.simulationTimeDeltaSeconds)
    }) + 0
) | Measure-Object -Maximum | Select-Object -ExpandProperty Maximum

$evidence = [ordered]@{
    kind = 'e1a-two-process-divergence-sample'
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    sourceWorkerId = $SourceWorkerId
    destinationWorkerId = $DestinationWorkerId
    sampleCount = $SampleCount
    sampleIntervalSeconds = $SampleIntervalSeconds
    canonicalSha256MismatchCount = $canonicalMismatches
    kinematicsSha256MismatchCount = $kinematicsMismatches
    maximumAbsoluteSimulationTimeDeltaSeconds = $maxAbsSimulationDelta
    hotReplicaClaimed = $false
    samples = $samples
    notes = @(
        'Same-machine samples. Cross-machine floating-point and driver differences are strictly worse.',
        'CanonicalSha256 excludes engine FPS/timeScale so frame-rate noise does not masquerade as simulation disagreement.',
        'A mismatch here is enough to keep the hot-shadow claim unsupported. A match is not enough to claim D0-D5 adapter validation.'
    )
}

$json = $evidence | ConvertTo-Json -Depth 10
$json
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $directory = Split-Path -Parent $EvidencePath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $json | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}

$stale = @($samples | Where-Object {
    $_.source.schemaVersion -lt 11 -or $_.destination.schemaVersion -lt 11
}).Count
if ($stale -gt 0) {
    Write-Host 'DIVERGENCE_SAMPLE_STALE_RUNTIME: world snapshot schema is older than 11; rebuild and redeploy the Runtime Bridge.'
    exit 2
}

Write-Host "DIVERGENCE_SAMPLE_CAPTURED: canonical mismatches=$canonicalMismatches kinematics mismatches=$kinematicsMismatches max |dGameTime|=$maxAbsSimulationDelta. Hot replicas remain unclaimed."
exit 0
