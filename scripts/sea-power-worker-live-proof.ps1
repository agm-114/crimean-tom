[CmdletBinding()]
param(
    [string]$CampaignHttpUrl = $(
        if ($env:CRIMEAN_TOM_CAMPAIGN_HTTP_URL) { $env:CRIMEAN_TOM_CAMPAIGN_HTTP_URL }
        else { 'http://127.0.0.1:5081' }
    ),

    [string]$CampaignId = $env:CRIMEAN_TOM_CAMPAIGN_ID,

    [string]$CampaignTokenFile,

    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR }
        else { 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power' }
    ),

    [string]$WorkersRunRoot,

    [string[]]$WorkerIds = @('worker-a', 'worker-b')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installedGameExecutable = [System.IO.Path]::GetFullPath((Join-Path $SeaPowerDir 'Sea Power.exe'))
$workersRunRoot = if ([string]::IsNullOrWhiteSpace($WorkersRunRoot)) {
    Join-Path $repoRoot '.run\workers'
}
else {
    [System.IO.Path]::GetFullPath($WorkersRunRoot)
}

. (Join-Path $PSScriptRoot 'sea-power-worker-proof-common.ps1')

if ([string]::IsNullOrWhiteSpace($CampaignId)) {
    throw 'CampaignId is required. Pass -CampaignId or set CRIMEAN_TOM_CAMPAIGN_ID.'
}

if ([string]::IsNullOrWhiteSpace($CampaignTokenFile)) {
    if ($env:CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE) {
        $CampaignTokenFile = $env:CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE
    }
    else {
        $CampaignTokenFile = Join-Path $repoRoot '.run\campaign-service\service.token'
    }
}

$coexistence = Prove-NamedWorkerCoexistence `
    $CampaignHttpUrl `
    $CampaignId `
    $CampaignTokenFile `
    $workersRunRoot `
    $WorkerIds `
    $installedGameExecutable

$matchedWorkers = @($coexistence.Workers)
$distinctGameSessions = @($matchedWorkers | Select-Object -ExpandProperty GameSessionId -Unique)
$distinctClientIds = @($matchedWorkers | Select-Object -ExpandProperty ClientInstanceId -Unique)
$distinctCampaignSessions = @($matchedWorkers | Select-Object -ExpandProperty CampaignSessionId -Unique)
$distinctStateDirectories = @(
    $matchedWorkers |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.CampaignStateDirectory) } |
        Select-Object -ExpandProperty CampaignStateDirectory -Unique
)

$result = [pscustomobject]@{
    CampaignId = $CampaignId
    CampaignHttpUrl = $CampaignHttpUrl
    ConnectedCampaignWorkerCount = $coexistence.ConnectedCampaignWorkerCount
    ProvenWorkerCount = $coexistence.ProvenWorkerCount
    DistinctGameSessionIds = $distinctGameSessions
    DistinctClientInstanceIds = $distinctClientIds
    DistinctCampaignSessionIds = $distinctCampaignSessions
    DistinctCampaignStateDirectories = $distinctStateDirectories
    Workers = $matchedWorkers | ForEach-Object {
        [pscustomobject]@{
            WorkerId = $_.WorkerId
            ControlUrl = $_.ControlUrl
            PipeName = $_.PipeName
            GameProcessId = $_.GameProcessId
            GameExecutable = $_.GameExecutable
            GameProcessStartTimeUtc = $_.GameProcessStartTimeUtc
            GameSessionId = $_.GameSessionId
            ClientInstanceId = $_.ClientInstanceId
            CampaignSessionId = $_.CampaignSessionId
            RuntimeFingerprintSha256 = $_.RuntimeFingerprintSha256
            CatalogFingerprintSha256 = $_.CatalogFingerprintSha256
            PromotionHeadroom = $_.PromotionHeadroom
            MigrationOverlapHeadroom = $_.MigrationOverlapHeadroom
            OccupiedTacticalEntityCount = $_.OccupiedTacticalEntityCount
            TacticalEntityCapacity = $_.TacticalEntityCapacity
            TacticalReserveCapacity = $_.TacticalReserveCapacity
            CampaignStateDirectory = $_.CampaignStateDirectory
        }
    }
    LiveProofSucceeded = $true
    TwoContainerMigrationClaim = $false
    Notes = @(
        'This proof admits coexistence of two real runtime+companion workers with distinct campaign sessions and validated live Sea Power processes.'
        'It does not claim safe two-container island migration or automatic rebalance.'
        'Local second-instance Sea Power remains experimental; Steam and shared native profile paths may still block or corrupt paired runs.'
    )
}

$result | ConvertTo-Json -Depth 6
Write-Host 'LIVE_PROOF_SUCCEEDED: two distinct real campaign workers coexist.'
