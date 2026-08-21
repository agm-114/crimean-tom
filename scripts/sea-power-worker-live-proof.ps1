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

    [string[]]$WorkerIds = @('worker-a', 'worker-b')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installedGameExecutable = [System.IO.Path]::GetFullPath((Join-Path $SeaPowerDir 'Sea Power.exe'))
$processStartTimeToleranceSeconds = 2

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

function Normalize-WorkerIds([string[]]$Ids) {
    $normalized = @()
    foreach ($id in $Ids) {
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        foreach ($part in $id.Split(',')) {
            $trimmed = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                $normalized += $trimmed
            }
        }
    }
    return @($normalized | Select-Object -Unique)
}

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

function Test-ProcessStartTimeMatches(
    $Process,
    [string]$RecordedStartTimeUtc,
    [int]$ToleranceSeconds,
    [bool]$RequireRecordedStartTime = $true) {
    if ([string]::IsNullOrWhiteSpace($RecordedStartTimeUtc)) {
        return -not $RequireRecordedStartTime
    }
    try {
        $recordedStart = [DateTimeOffset]::Parse($RecordedStartTimeUtc)
    }
    catch {
        return $false
    }
    $actualStart = [DateTimeOffset]$Process.StartTime.ToUniversalTime()
    $deltaForward = ($actualStart - $recordedStart).TotalSeconds
    $deltaBackward = ($recordedStart - $actualStart).TotalSeconds
    return $deltaForward -le $ToleranceSeconds -and $deltaBackward -le $ToleranceSeconds
}

function Test-LiveSeaPowerProcess(
    [int]$ProcessId,
    [string]$ExpectedExecutable,
    [string]$ExpectedUnityLog,
    [string]$RecordedStartTimeUtc) {
    if ($ProcessId -le 0) {
        throw 'Recorded game process ID is missing or invalid.'
    }
    if ([string]::IsNullOrWhiteSpace($RecordedStartTimeUtc)) {
        throw 'Recorded game process start time is missing from worker metadata.'
    }
    try {
        [DateTimeOffset]::Parse($RecordedStartTimeUtc)
    }
    catch {
        throw "Recorded game process start time '$RecordedStartTimeUtc' is not a parseable UTC timestamp."
    }

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        throw "Recorded game PID $ProcessId is not running."
    }
    if ($process.ProcessName -cne 'Sea Power') {
        throw "Recorded game PID $ProcessId is not a Sea Power process (name='$($process.ProcessName)')."
    }

    $executablePath = Get-ProcessExecutablePath $ProcessId
    if ([string]::IsNullOrWhiteSpace($executablePath)) {
        throw "Recorded game PID $ProcessId has no verifiable executable path."
    }
    $resolvedExecutable = [System.IO.Path]::GetFullPath($executablePath)
    $expectedResolved = [System.IO.Path]::GetFullPath($ExpectedExecutable)
    if (-not $resolvedExecutable.Equals($expectedResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Recorded game PID $ProcessId executable '$resolvedExecutable' does not match expected '$expectedResolved'."
    }
    if (-not $expectedResolved.Equals($installedGameExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Recorded metadata executable '$expectedResolved' does not match installed Sea Power at '$installedGameExecutable'."
    }

    if (-not (Test-ProcessStartTimeMatches `
        $process `
        $RecordedStartTimeUtc `
        $processStartTimeToleranceSeconds `
        $true)) {
        throw "Recorded game PID $ProcessId start time does not match metadata '$RecordedStartTimeUtc'."
    }

    $commandLine = Get-ProcessCommandLine $ProcessId
    if ($commandLine.IndexOf('-logFile', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Recorded game PID $ProcessId command line does not include -logFile."
    }
    $normalizedLog = [System.IO.Path]::GetFullPath($ExpectedUnityLog)
    if ($commandLine.IndexOf($normalizedLog, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Recorded game PID $ProcessId command line does not reference worker log '$normalizedLog'."
    }

    return $process
}

function Get-WorkerProofLayout([string]$WorkerId) {
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
        GameProcessStartTimeUtc = [string]$metadata.gameProcessStartTimeUtc
        GameExecutable = [string]$metadata.gameExecutable
        UnityLog = [string]$metadata.unityLog
        CampaignStateDirectory = [string]$metadata.campaignStateDirectory
    }
}

function Prove-WorkerRuntime($Layout) {
    $liveGame = Test-LiveSeaPowerProcess `
        $Layout.GameProcessId `
        $Layout.GameExecutable `
        $Layout.UnityLog `
        $Layout.GameProcessStartTimeUtc

    if (-not (Test-Path -LiteralPath $Layout.TokenFile)) {
        throw "Control token missing for worker '$($Layout.WorkerId)' at $($Layout.TokenFile)."
    }

    $health = Invoke-WorkerControl $Layout.ControlUrl $Layout.TokenFile '/health'
    if (-not $health.gameConnected) {
        throw "Worker '$($Layout.WorkerId)' has a validated Sea Power PID $($liveGame.Id), but the companion is not connected on $($Layout.PipeName)."
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
        GameProcessId = $liveGame.Id
        GameExecutable = $Layout.GameExecutable
        GameProcessStartTimeUtc = $Layout.GameProcessStartTimeUtc
        GameSessionId = [string]$health.sessionId
        SessionGeneration = [long]$health.sessionGeneration
        RuntimeFingerprintSha256 = $fingerprint
        CatalogFingerprintSha256 = $capabilities.runtimeFingerprint.catalogSha256
        CampaignStateDirectory = $Layout.CampaignStateDirectory
    }
}

if ([string]::IsNullOrWhiteSpace($CampaignId)) {
    throw 'CampaignId is required. Pass -CampaignId or set CRIMEAN_TOM_CAMPAIGN_ID.'
}
if (-not (Test-Path -LiteralPath $CampaignTokenFile)) {
    throw "Campaign token file not found at $CampaignTokenFile."
}

$normalizedWorkerIds = Normalize-WorkerIds $WorkerIds
if ($normalizedWorkerIds.Count -lt 2) {
    throw 'Live proof requires at least two distinct worker IDs. Pass -WorkerIds worker-a,worker-b or separate values.'
}

$workerProofs = @()
foreach ($workerId in $normalizedWorkerIds) {
    $layout = Get-WorkerProofLayout $workerId
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
        GameExecutable = $proof.GameExecutable
        GameProcessStartTimeUtc = $proof.GameProcessStartTimeUtc
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
        CampaignStateDirectory = $proof.CampaignStateDirectory
    }
}

$distinctGameSessions = @($matchedWorkers | Select-Object -ExpandProperty GameSessionId -Unique)
$distinctClientIds = @($matchedWorkers | Select-Object -ExpandProperty ClientInstanceId -Unique)
$distinctCampaignSessions = @($matchedWorkers | Select-Object -ExpandProperty CampaignSessionId -Unique)
$distinctStateDirectories = @(
    $matchedWorkers |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.CampaignStateDirectory) } |
        Select-Object -ExpandProperty CampaignStateDirectory -Unique
)
if ($distinctGameSessions.Count -lt 2) {
    throw 'Live proof failed: game session IDs are not distinct across workers.'
}
if ($distinctClientIds.Count -lt 2) {
    throw 'Live proof failed: campaign client instance IDs are not distinct across workers.'
}
if ($distinctCampaignSessions.Count -lt 2) {
    throw 'Live proof failed: campaign session IDs are not distinct across workers.'
}
if ($distinctStateDirectories.Count -lt 2) {
    throw 'Live proof failed: campaign companion state directories are not distinct across workers.'
}

$result = [pscustomobject]@{
    CampaignId = $CampaignId
    CampaignHttpUrl = $CampaignHttpUrl
    ConnectedCampaignWorkerCount = $campaignWorkers.Count
    ProvenWorkerCount = $matchedWorkers.Count
    DistinctGameSessionIds = $distinctGameSessions
    DistinctClientInstanceIds = $distinctClientIds
    DistinctCampaignSessionIds = $distinctCampaignSessions
    DistinctCampaignStateDirectories = $distinctStateDirectories
    Workers = $matchedWorkers
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
