[CmdletBinding()]
param(
    [string]$CampaignHttpUrl = $(
        if ($env:CRIMEAN_TOM_CAMPAIGN_HTTP_URL) { $env:CRIMEAN_TOM_CAMPAIGN_HTTP_URL }
        else { 'http://127.0.0.1:5080' }
    ),

    [string]$CampaignId = $env:CRIMEAN_TOM_CAMPAIGN_ID,

    [string]$CampaignTokenFile,

    [string[]]$WorkerIds = @('worker-a', 'worker-b')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CampaignTokenFile)) {
    if ($env:CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE) {
        $CampaignTokenFile = $env:CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE
    }
    else {
        $CampaignTokenFile = Join-Path $repoRoot '.run\campaign-service\service.token'
    }
}

$syntheticGameSessionIds = @(
    'container-worker-probe',
    'shadow-destination-worker',
    'shadow-migration-worker'
)
$syntheticClientInstanceIds = @(
    '20000000000000000000000000000002'
)

function Test-SyntheticFingerprint([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    if ($Value -notmatch '^[0-9a-f]{64}$') { return $true }
    return $Value -match '^(.)\1{63}$'
}

function Invoke-WorkerControl([string]$ControlUrl, [string]$TokenPath, [string]$Path) {
    $token = (Get-Content -LiteralPath $TokenPath -Raw).Trim()
    $headers = @{ 'X-Crimean-Tom-Token' = $token }
    Invoke-RestMethod -Method Get -Uri "$ControlUrl$Path" -Headers $headers -TimeoutSec 8
}

function Get-WorkerControlLayout([string]$WorkerId) {
    $runDir = Join-Path $repoRoot ".run\workers\$WorkerId"
    $metadataPath = Join-Path $runDir 'worker.metadata.json'
    if (-not (Test-Path -LiteralPath $metadataPath)) {
        throw "Worker metadata not found for '$WorkerId' at $metadataPath. Start the worker first."
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    return [pscustomobject]@{
        WorkerId = $WorkerId
        ControlUrl = [string]$metadata.controlUrl
        TokenFile = Join-Path $runDir 'control.token'
        PipeName = [string]$metadata.pipeName
        InstanceId = [string]$metadata.instanceId
        GameProcessId = [int]$metadata.gameProcessId
        UnityLog = [string]$metadata.unityLog
    }
}

function Prove-WorkerRuntime($Layout) {
    if (-not (Test-Path -LiteralPath $Layout.TokenFile)) {
        throw "Control token missing for worker '$($Layout.WorkerId)' at $($Layout.TokenFile)."
    }

    $health = Invoke-WorkerControl $Layout.ControlUrl $Layout.TokenFile '/health'
    if (-not $health.gameConnected) {
        throw "Worker '$($Layout.WorkerId)' companion is healthy but Sea Power is not connected on $($Layout.PipeName)."
    }
    if ($health.connectionState -cne 'connected' -or [string]::IsNullOrWhiteSpace([string]$health.sessionId)) {
        throw "Worker '$($Layout.WorkerId)' did not publish a valid connected session."
    }

    $capabilities = Invoke-WorkerControl $Layout.ControlUrl $Layout.TokenFile '/game/capabilities'
    $fingerprint = $capabilities.runtimeFingerprint.fingerprintSha256
    if (Test-SyntheticFingerprint $fingerprint) {
        throw "Worker '$($Layout.WorkerId)' reports a synthetic or missing runtime fingerprint '$fingerprint'."
    }

    return [pscustomobject]@{
        WorkerId = $Layout.WorkerId
        ControlUrl = $Layout.ControlUrl
        PipeName = $Layout.PipeName
        InstanceId = $Layout.InstanceId
        GameProcessId = $Layout.GameProcessId
        GameSessionId = [string]$health.sessionId
        SessionGeneration = [long]$health.sessionGeneration
        RuntimeFingerprintSha256 = $fingerprint
        CatalogFingerprintSha256 = $capabilities.runtimeFingerprint.catalogSha256
    }
}

if ([string]::IsNullOrWhiteSpace($CampaignId)) {
    throw 'CampaignId is required. Pass -CampaignId or set CRIMEAN_TOM_CAMPAIGN_ID.'
}
if (-not (Test-Path -LiteralPath $CampaignTokenFile)) {
    throw "Campaign token file not found at $CampaignTokenFile."
}

$workerProofs = @()
foreach ($workerId in $WorkerIds) {
    $layout = Get-WorkerControlLayout $workerId.Trim()
    $workerProofs += Prove-WorkerRuntime $layout
}

$token = (Get-Content -LiteralPath $CampaignTokenFile -Raw).Trim()
$headers = @{ Authorization = "Bearer $token" }
$campaign = Invoke-RestMethod `
    -Method Get `
    -Uri "$CampaignHttpUrl/campaigns/$CampaignId" `
    -Headers $headers `
    -TimeoutSec 15

$campaignWorkers = @($campaign.workers)
$matchedWorkers = @()
foreach ($proof in $workerProofs) {
    $match = @($campaignWorkers | Where-Object {
        $_.gameSessionId -eq $proof.GameSessionId
    })
    if ($match.Count -ne 1) {
        throw "Campaign worker registry did not contain exactly one session for game session '$($proof.GameSessionId)' from worker '$($proof.WorkerId)'."
    }
    $campaignWorker = $match[0]
    if ($campaignWorker.clientInstanceId -in $syntheticClientInstanceIds) {
        throw "Worker '$($proof.WorkerId)' matched a synthetic gRPC client instance '$($campaignWorker.clientInstanceId)'."
    }
    if ($campaignWorker.gameSessionId -in $syntheticGameSessionIds) {
        throw "Worker '$($proof.WorkerId)' matched synthetic game session '$($campaignWorker.gameSessionId)'."
    }
    if (Test-SyntheticFingerprint $campaignWorker.runtimeFingerprintSha256) {
        throw "Campaign worker for '$($proof.WorkerId)' reports a synthetic runtime fingerprint."
    }
    $matchedWorkers += [pscustomobject]@{
        WorkerId = $proof.WorkerId
        ControlUrl = $proof.ControlUrl
        PipeName = $proof.PipeName
        GameProcessId = $proof.GameProcessId
        GameSessionId = $proof.GameSessionId
        ClientInstanceId = [string]$campaignWorker.clientInstanceId
        CampaignSessionId = [string]$campaignWorker.sessionId
        RuntimeFingerprintSha256 = [string]$campaignWorker.runtimeFingerprintSha256
        CatalogFingerprintSha256 = [string]$campaignWorker.catalogFingerprintSha256
        PromotionHeadroom = [int]$campaignWorker.promotionHeadroom
        MigrationOverlapHeadroom = [int]$campaignWorker.migrationOverlapHeadroom
        OccupiedTacticalEntityCount = [int]$campaignWorker.occupiedTacticalEntityCount
        TacticalEntityCapacity = [int]$campaignWorker.tacticalEntityCapacity
        TacticalReserveCapacity = [int]$campaignWorker.tacticalReserveCapacity
    }
}

if ($matchedWorkers.Count -lt 2) {
    throw 'Live proof requires at least two worker proofs.'
}

$distinctGameSessions = @($matchedWorkers | Select-Object -ExpandProperty GameSessionId -Unique)
$distinctClientIds = @($matchedWorkers | Select-Object -ExpandProperty ClientInstanceId -Unique)
$distinctCampaignSessions = @($matchedWorkers | Select-Object -ExpandProperty CampaignSessionId -Unique)
if ($distinctGameSessions.Count -lt 2) {
    throw 'Live proof failed: game session IDs are not distinct across workers.'
}
if ($distinctClientIds.Count -lt 2) {
    throw 'Live proof failed: campaign client instance IDs are not distinct across workers.'
}
if ($distinctCampaignSessions.Count -lt 2) {
    throw 'Live proof failed: campaign session IDs are not distinct across workers.'
}

$result = [pscustomobject]@{
    CampaignId = $CampaignId
    CampaignHttpUrl = $CampaignHttpUrl
    ConnectedCampaignWorkerCount = $campaignWorkers.Count
    ProvenWorkerCount = $matchedWorkers.Count
    DistinctGameSessionIds = $distinctGameSessions
    DistinctClientInstanceIds = $distinctClientIds
    DistinctCampaignSessionIds = $distinctCampaignSessions
    Workers = $matchedWorkers
    LiveProofSucceeded = $true
    TwoContainerMigrationClaim = $false
    Notes = @(
        'This proof admits coexistence of two real runtime+companion workers with distinct campaign sessions.'
        'It does not claim safe two-container island migration or automatic rebalance.'
        'Local second-instance Sea Power remains experimental; Steam and shared native profile paths may still block or corrupt paired runs.'
    )
}

$result | ConvertTo-Json -Depth 6
Write-Host 'LIVE_PROOF_SUCCEEDED: two distinct real campaign workers coexist.'
