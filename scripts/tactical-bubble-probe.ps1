[CmdletBinding()]
param(
    [string]$ControlUrl = $(
        if ($env:CRIMEAN_TOM_CONTROL_URL) { $env:CRIMEAN_TOM_CONTROL_URL }
        else { 'http://127.0.0.1:5137' }
    ),

    [string]$RunDirectory,

    [ValidateRange(1, 120)]
    [int]$SampleIntervalSeconds = 5,

    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$runDir = if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
    Join-Path $repoRoot '.run'
}
else {
    [System.IO.Path]::GetFullPath($RunDirectory)
}
$tokenFile = Join-Path $runDir 'control.token'
if (-not (Test-Path -LiteralPath $tokenFile)) {
    throw "Companion control token not found at $tokenFile."
}
$token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
$headers = @{ 'X-Crimean-Tom-Token' = $token }

function Get-WorldSnapshot {
    return Invoke-RestMethod -Method Get -Uri "$ControlUrl/game/world" -Headers $headers -TimeoutSec 15
}

function Select-BubbleFields($Snapshot) {
    $spatial = $Snapshot.spatialDiagnostics
    return [ordered]@{
        capturedAtUnixMilliseconds = $Snapshot.capturedAtUnixMilliseconds
        sceneName = $Snapshot.sceneName
        simulationTimeSeconds = $Snapshot.simulationTimeSeconds
        timeCompression = $Snapshot.timeCompression
        isPaused = $Snapshot.isPaused
        kinematicsSha256 = $Snapshot.kinematicsSha256
        canonicalSha256 = $Snapshot.canonicalSha256
        observedObjectCount = $Snapshot.observedObjectCount
        spatialSchemaVersion = $spatial.schemaVersion
        rebaseThresholdMeters = $spatial.rebaseThresholdMeters
        cameraHorizontalDistanceMeters = $spatial.cameraHorizontalDistanceMeters
        objectCountBeyondRebaseThreshold = $spatial.objectCountBeyondRebaseThreshold
        maximumObjectHorizontalDistanceMeters = $spatial.maximumObjectHorizontalDistanceMeters
        maximumObjectGeodeticDistanceMeters = $spatial.maximumObjectGeodeticDistanceMeters
        maximumPairHorizontalSeparationMeters = $spatial.maximumPairHorizontalSeparationMeters
        maximumPairGeodeticSeparationMeters = $spatial.maximumPairGeodeticSeparationMeters
        loadedTerrainChunkCount = $spatial.loadedTerrainChunkCount
        maximumLoadedTerrainCenterDistanceMeters = $spatial.maximumLoadedTerrainCenterDistanceMeters
        objectCountBeyondLoadedTerrain = $spatial.objectCountBeyondLoadedTerrain
        unitCountBeyondLoadedTerrain = $spatial.unitCountBeyondLoadedTerrain
        activeWeaponCount = $spatial.activeWeaponCount
        activeWeaponCountBeyondLoadedTerrain = $spatial.activeWeaponCountBeyondLoadedTerrain
    }
}

$first = Get-WorldSnapshot
Start-Sleep -Seconds $SampleIntervalSeconds
$second = Get-WorldSnapshot
$firstBubble = Select-BubbleFields $first
$secondBubble = Select-BubbleFields $second

$kinematicsChanged = $firstBubble.kinematicsSha256 -ne $secondBubble.kinematicsSha256
$weaponsBeyondTerrain = [int]$secondBubble.activeWeaponCountBeyondLoadedTerrain -gt 0
$unitsBeyondTerrain = [int]$secondBubble.unitCountBeyondLoadedTerrain -gt 0
$objectsBeyondRebase = [int]$secondBubble.objectCountBeyondRebaseThreshold -gt 0

$evidence = [ordered]@{
    kind = 'tactical-bubble-probe'
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    controlUrl = $ControlUrl
    sampleIntervalSeconds = $SampleIntervalSeconds
    first = $firstBubble
    second = $secondBubble
    kinematicsChangedBetweenSamples = $kinematicsChanged
    objectsBeyondRebaseThreshold = $objectsBeyondRebase
    unitsOnUnloadedTerrain = $unitsBeyondTerrain
    activeWeaponsOnUnloadedTerrain = $weaponsBeyondTerrain
    qualifiedCombatRadiusClaimed = $false
    notes = @(
        'Unity rebase (~67 km) is camera-origin relocation, not a simulation cull.',
        'Geodetic maxima measure instantiated ObjectBase extent, not weapons/terrain continuity.',
        'activeWeaponsOnUnloadedTerrain is the first live hint of combat across unloaded chunks; a zero count is not a proof that such combat is impossible.'
    )
}

$json = $evidence | ConvertTo-Json -Depth 8
$json
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $directory = Split-Path -Parent $EvidencePath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $json | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}

if ($secondBubble.spatialSchemaVersion -lt 2) {
    Write-Host 'BUBBLE_PROBE_STALE_RUNTIME: spatial diagnostics schema is older than 2; rebuild and redeploy the Runtime Bridge.'
    exit 2
}

Write-Host 'BUBBLE_PROBE_CAPTURED: two world snapshots recorded. This does not qualify a combat/terrain radius.'
exit 0
