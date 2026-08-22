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

    [ValidateRange(0, 60)]
    [double]$MaximumComparableSimulationTimeDeltaSeconds = 0.05,

    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($WorkersRunRoot)) {
    $WorkersRunRoot = Join-Path $repoRoot '.run\workers'
}

function Assert-WorkerId([string]$Value) {
    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw "Worker ID '$Value' must be a bounded filename-safe identifier."
    }
    return $Value
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

function New-WorkerContext([string]$WorkerId, [string]$ControlUrl) {
    $runDir = Join-Path $WorkersRunRoot $WorkerId
    $tokenFile = Join-Path $runDir 'control.token'
    if (-not (Test-Path -LiteralPath $tokenFile)) {
        throw "Companion control token not found at $tokenFile."
    }
    $token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
    $headers = @{ 'X-Crimean-Tom-Token' = $token }
    $health = Invoke-RestMethod -Method Get -Uri "$ControlUrl/health" -Headers $headers -TimeoutSec 15
    $capabilities = Invoke-RestMethod -Method Get -Uri "$ControlUrl/game/capabilities" -Headers $headers -TimeoutSec 15
    $identityDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while (($capabilities.runtimeFingerprint.fileIdentityState -ceq 'pending' `
            -or $capabilities.runtimeFingerprint.contentIdentityState -ceq 'pending') `
        -and [DateTime]::UtcNow -lt $identityDeadline) {
        Start-Sleep -Milliseconds 100
        $capabilities = Invoke-RestMethod -Method Get -Uri "$ControlUrl/game/capabilities" -Headers $headers -TimeoutSec 15
    }
    return [pscustomobject]@{
        WorkerId = $WorkerId
        ControlUrl = $ControlUrl
        Headers = $headers
        Health = $health
        Fingerprint = $capabilities.runtimeFingerprint
    }
}

function Get-WorkerWorld($Context) {
    $started = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $world = Invoke-RestMethod `
        -Method Get `
        -Uri "$($Context.ControlUrl)/game/world" `
        -Headers $Context.Headers `
        -TimeoutSec 15
    $completed = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    return [pscustomobject]@{
        Raw = $world
        RequestStartedAtUnixMilliseconds = $started
        RequestCompletedAtUnixMilliseconds = $completed
        RequestMidpointUnixMilliseconds = [long](($started + $completed) / 2)
        RequestDurationMilliseconds = $completed - $started
    }
}

function Select-WorkerWorld($Context, $Capture) {
    $world = $Capture.Raw
    return [ordered]@{
        workerId = $Context.WorkerId
        controlUrl = $Context.ControlUrl
        processId = [int]$Context.Fingerprint.processId
        gameSessionId = [string]$Context.Health.sessionId
        requestStartedAtUnixMilliseconds = $Capture.RequestStartedAtUnixMilliseconds
        requestCompletedAtUnixMilliseconds = $Capture.RequestCompletedAtUnixMilliseconds
        requestMidpointUnixMilliseconds = $Capture.RequestMidpointUnixMilliseconds
        requestDurationMilliseconds = $Capture.RequestDurationMilliseconds
        capturedAtUnixMilliseconds = $world.capturedAtUnixMilliseconds
        schemaVersion = $world.schemaVersion
        sceneName = $world.sceneName
        scenarioId = $world.scenarioId
        scenarioPath = $world.scenarioPath
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

function Test-Sha256([string]$Value) {
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[0-9a-f]{64}$'
}

function Assert-ComparableWorldPair($LeftCapture, $RightCapture) {
    $left = $LeftCapture.Raw
    $right = $RightCapture.Raw
    if ([int]$left.schemaVersion -lt 12 -or [int]$right.schemaVersion -lt 12) {
        Write-Host 'DIVERGENCE_SAMPLE_STALE_RUNTIME: world snapshot schema is older than 12; rebuild and redeploy the Runtime Bridge.'
        exit 2
    }
    if ([string]$left.sceneName -cne 'mission' -or [string]$right.sceneName -cne 'mission' `
        -or [int]$left.observedObjectCount -le 0 -or [int]$right.observedObjectCount -le 0) {
        Write-Host 'DIVERGENCE_SAMPLE_NOT_TACTICAL: both workers must have a loaded tactical mission.'
        exit 2
    }
    if ([string]::IsNullOrWhiteSpace([string]$left.scenarioId) `
        -or [string]$left.scenarioId -cne [string]$right.scenarioId `
        -or [string]$left.scenarioPath -cne [string]$right.scenarioPath) {
        Write-Host 'DIVERGENCE_SAMPLE_SCENARIO_MISMATCH: both workers must run the exact same authored scenario.'
        exit 2
    }
    if ([bool]$left.isPaused -ne [bool]$right.isPaused `
        -or [double]$left.timeCompression -ne [double]$right.timeCompression) {
        Write-Host 'DIVERGENCE_SAMPLE_CLOCK_MODE_MISMATCH: both workers must use the same pause and time-compression state.'
        exit 2
    }
    foreach ($hash in @(
        [string]$left.canonicalSha256,
        [string]$right.canonicalSha256,
        [string]$left.kinematicsSha256,
        [string]$right.kinematicsSha256,
        [string]$left.identitySha256,
        [string]$right.identitySha256)) {
        if (-not (Test-Sha256 $hash)) {
            Write-Host 'DIVERGENCE_SAMPLE_HASH_NOT_READY: both workers must publish non-empty canonical, kinematics, and identity hashes.'
            exit 2
        }
    }
}

$SourceWorkerId = Assert-WorkerId $SourceWorkerId
$DestinationWorkerId = Assert-WorkerId $DestinationWorkerId
if ([string]::IsNullOrWhiteSpace($SourceControlUrl)) {
    $SourceControlUrl = "http://127.0.0.1:$(Get-DefaultWorkerControlPort $SourceWorkerId)"
}
if ([string]::IsNullOrWhiteSpace($DestinationControlUrl)) {
    $DestinationControlUrl = "http://127.0.0.1:$(Get-DefaultWorkerControlPort $DestinationWorkerId)"
}
if ($SourceWorkerId -ieq $DestinationWorkerId -or $SourceControlUrl -ieq $DestinationControlUrl) {
    throw 'Source and destination must be different named worker endpoints.'
}

$sourceContext = New-WorkerContext $SourceWorkerId $SourceControlUrl
$destinationContext = New-WorkerContext $DestinationWorkerId $DestinationControlUrl
if (-not [bool]$sourceContext.Health.gameConnected `
    -or -not [bool]$destinationContext.Health.gameConnected `
    -or [string]$sourceContext.Health.connectionState -cne 'connected' `
    -or [string]$destinationContext.Health.connectionState -cne 'connected') {
    Write-Host 'DIVERGENCE_SAMPLE_WORKER_NOT_READY: both companion/game sessions must be connected.'
    exit 2
}
if ([int]$sourceContext.Fingerprint.processId -le 0 `
    -or [int]$sourceContext.Fingerprint.processId -eq [int]$destinationContext.Fingerprint.processId `
    -or [string]$sourceContext.Health.sessionId -ceq [string]$destinationContext.Health.sessionId) {
    Write-Host 'DIVERGENCE_SAMPLE_WORKERS_NOT_DISTINCT: two distinct Sea Power processes and game sessions are required.'
    exit 2
}
if ([string]$sourceContext.Fingerprint.fileIdentityState -cne 'ready' `
    -or [string]$destinationContext.Fingerprint.fileIdentityState -cne 'ready' `
    -or [string]$sourceContext.Fingerprint.contentIdentityState -cne 'ready' `
    -or [string]$destinationContext.Fingerprint.contentIdentityState -cne 'ready' `
    -or [string]$sourceContext.Fingerprint.fingerprintSha256 -cne [string]$destinationContext.Fingerprint.fingerprintSha256 `
    -or [string]$sourceContext.Fingerprint.catalogSha256 -cne [string]$destinationContext.Fingerprint.catalogSha256 `
    -or [string]$sourceContext.Fingerprint.modSetSha256 -cne [string]$destinationContext.Fingerprint.modSetSha256) {
    Write-Host 'DIVERGENCE_SAMPLE_FINGERPRINT_MISMATCH: both workers must have the same ready runtime/content fingerprint.'
    exit 2
}

$samples = @()
for ($index = 0; $index -lt $SampleCount; $index++) {
    $leftCapture = Get-WorkerWorld $sourceContext
    $rightCapture = Get-WorkerWorld $destinationContext
    Assert-ComparableWorldPair $leftCapture $rightCapture
    $left = Select-WorkerWorld $sourceContext $leftCapture
    $right = Select-WorkerWorld $destinationContext $rightCapture
    $simulationDelta = [double]$right.simulationTimeSeconds - [double]$left.simulationTimeSeconds
    $hashesComparable = [Math]::Abs($simulationDelta) -le $MaximumComparableSimulationTimeDeltaSeconds
    $rawCanonicalMatch = [string]$left.canonicalSha256 -ceq [string]$right.canonicalSha256
    $rawKinematicsMatch = [string]$left.kinematicsSha256 -ceq [string]$right.kinematicsSha256
    $samples += [ordered]@{
        index = $index
        recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        source = $left
        destination = $right
        requestMidpointDeltaMilliseconds = [long]$right.requestMidpointUnixMilliseconds - [long]$left.requestMidpointUnixMilliseconds
        gameCaptureDeltaMilliseconds = [long]$right.capturedAtUnixMilliseconds - [long]$left.capturedAtUnixMilliseconds
        simulationTimeDeltaSeconds = $simulationDelta
        hashesComparableAtLogicalTime = $hashesComparable
        rawCanonicalSha256Match = $rawCanonicalMatch
        rawKinematicsSha256Match = $rawKinematicsMatch
        canonicalSha256Match = if ($hashesComparable) { $rawCanonicalMatch } else { $null }
        kinematicsSha256Match = if ($hashesComparable) { $rawKinematicsMatch } else { $null }
        identitySha256Match = [string]$left.identitySha256 -ceq [string]$right.identitySha256
        timeScaleDelta = [double]$right.engineTimeScale - [double]$left.engineTimeScale
    }
    if ($index -lt $SampleCount - 1) {
        Start-Sleep -Seconds $SampleIntervalSeconds
    }
}

$comparableSamples = @($samples | Where-Object { $_.hashesComparableAtLogicalTime })
$canonicalMismatches = @($comparableSamples | Where-Object { -not $_.canonicalSha256Match }).Count
$kinematicsMismatches = @($comparableSamples | Where-Object { -not $_.kinematicsSha256Match }).Count
$rawCanonicalMismatches = @($samples | Where-Object { -not $_.rawCanonicalSha256Match }).Count
$maxAbsSimulationDelta = (
    @($samples | ForEach-Object { [Math]::Abs([double]$_.simulationTimeDeltaSeconds) }) + 0
) | Measure-Object -Maximum | Select-Object -ExpandProperty Maximum
$maxAbsCaptureDelta = (
    @($samples | ForEach-Object { [Math]::Abs([long]$_.gameCaptureDeltaMilliseconds) }) + 0
) | Measure-Object -Maximum | Select-Object -ExpandProperty Maximum

$evidence = [ordered]@{
    kind = 'e1a-two-process-divergence-sample'
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    sourceWorkerId = $SourceWorkerId
    destinationWorkerId = $DestinationWorkerId
    sourceProcessId = [int]$sourceContext.Fingerprint.processId
    destinationProcessId = [int]$destinationContext.Fingerprint.processId
    runtimeFingerprintSha256 = [string]$sourceContext.Fingerprint.fingerprintSha256
    catalogSha256 = [string]$sourceContext.Fingerprint.catalogSha256
    modSetSha256 = [string]$sourceContext.Fingerprint.modSetSha256
    scenarioId = [string]$samples[0].source.scenarioId
    sampleCount = $SampleCount
    sampleIntervalSeconds = $SampleIntervalSeconds
    maximumComparableSimulationTimeDeltaSeconds = $MaximumComparableSimulationTimeDeltaSeconds
    comparableHashSampleCount = $comparableSamples.Count
    comparableCanonicalSha256MismatchCount = $canonicalMismatches
    comparableKinematicsSha256MismatchCount = $kinematicsMismatches
    rawCanonicalSha256MismatchCount = $rawCanonicalMismatches
    maximumAbsoluteSimulationTimeDeltaSeconds = $maxAbsSimulationDelta
    maximumAbsoluteGameCaptureDeltaMilliseconds = $maxAbsCaptureDelta
    hotReplicaClaimed = $false
    samples = $samples
    notes = @(
        'Same-machine samples. Cross-machine floating-point and driver differences are strictly worse.',
        'Sequential HTTP capture skew is recorded. Hash matches and mismatches are classified only inside the configured logical-time delta.',
        'CanonicalSha256 excludes engine FPS/timeScale so frame-rate noise does not masquerade as simulation disagreement.',
        'A comparable mismatch keeps the hot-shadow claim unsupported. A match is not D0-D5 adapter validation.'
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

if ($comparableSamples.Count -eq 0) {
    Write-Host "DIVERGENCE_SAMPLE_NOT_LOGICALLY_ALIGNED: no sample was within $MaximumComparableSimulationTimeDeltaSeconds simulation seconds; clock drift was recorded but hashes were not classified."
    exit 2
}

Write-Host "DIVERGENCE_SAMPLE_CAPTURED: comparable samples=$($comparableSamples.Count) canonical mismatches=$canonicalMismatches kinematics mismatches=$kinematicsMismatches max |dGameTime|=$maxAbsSimulationDelta. Hot replicas remain unclaimed."
exit 0
