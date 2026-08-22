[CmdletBinding()]
param(
    [string]$CampaignHttpUrl = $(
        if ($env:CRIMEAN_TOM_CAMPAIGN_HTTP_URL) { $env:CRIMEAN_TOM_CAMPAIGN_HTTP_URL }
        else { 'http://127.0.0.1:5081' }
    ),

    [string]$CampaignId = $env:CRIMEAN_TOM_CAMPAIGN_ID,

    [string]$CampaignTokenFile,

    [string]$WorkersRunRoot,

    [string]$SourceWorkerId = 'worker-a',

    [string]$DestinationWorkerId = 'worker-b',

    [string]$CanonicalEntityId = '',

    [string]$CampaignName = 'Live two-container migrate',

    [long]$Seed = 1042,

    [string]$CatalogFingerprint = 'live-two-container-migrate',

    [switch]$CreateCampaign,

    [switch]$Execute,

    [ValidateRange(30, 1800)]
    [int]$TimeoutSeconds = 600,

    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'sea-power-worker-proof-common.ps1')

if ([string]::IsNullOrWhiteSpace($CampaignTokenFile)) {
    if ($env:CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE) {
        $CampaignTokenFile = $env:CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE
    }
    else {
        $CampaignTokenFile = Join-Path $repoRoot '.run\campaign-service\service.token'
    }
}

$headers = Get-CampaignAuthHeaders $CampaignTokenFile

if ($CreateCampaign) {
    if ($Execute) {
        throw '-CreateCampaign and -Execute are separate phases: create/bootstrap, reconnect both workers, preflight, then execute.'
    }
    if ([string]::IsNullOrWhiteSpace($CampaignId)) {
        $CampaignId = [Guid]::NewGuid().ToString('N')
    }
    $createResponse = Invoke-CampaignApiRaw Post $CampaignHttpUrl $headers '/control/campaigns' @{
        campaignId = $CampaignId
        name = $CampaignName
        seed = $Seed
        catalogFingerprint = $CatalogFingerprint
    }
    if ($createResponse.StatusCode -ge 400) {
        throw "Create campaign failed ($($createResponse.StatusCode)): $($createResponse.Content)"
    }
    $bootstrap = Invoke-CampaignApi Post $CampaignHttpUrl $headers "/control/campaigns/$CampaignId/bootstrap"
    if ([string]::IsNullOrWhiteSpace($CanonicalEntityId)) {
        $CanonicalEntityId = [string]$bootstrap.primaryEntityId
    }

    [pscustomobject]@{
        CampaignId = $CampaignId
        CanonicalEntityId = $CanonicalEntityId
        Created = $true
        MigrationExecuted = $false
        NextStep = "Reconnect worker-a and worker-b with -CampaignId $CampaignId, then run this driver without -CreateCampaign for preflight."
    } | ConvertTo-Json -Depth 4
    Write-Host "LIVE_MIGRATE_CAMPAIGN_CREATED: campaign=$CampaignId entity=$CanonicalEntityId; no migration was attempted."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($CampaignId)) {
    throw 'CampaignId is required unless -CreateCampaign is supplied.'
}

if ([string]::IsNullOrWhiteSpace($CanonicalEntityId)) {
    $view = Get-CampaignView $CampaignHttpUrl $headers $CampaignId
    $singletonIslands = @($view.islands | Where-Object {
        @($_.entityIds).Count -eq 1
    })
    if ($singletonIslands.Count -eq 1) {
        $CanonicalEntityId = [string]@($singletonIslands[0].entityIds)[0]
    }
}

$driverArgs = @{
    CampaignHttpUrl = $CampaignHttpUrl
    CampaignId = $CampaignId
    CampaignTokenFile = $CampaignTokenFile
    SourceWorkerId = $SourceWorkerId
    DestinationWorkerId = $DestinationWorkerId
    TimeoutSeconds = $TimeoutSeconds
}
if (-not [string]::IsNullOrWhiteSpace($WorkersRunRoot)) {
    $driverArgs.WorkersRunRoot = $WorkersRunRoot
}
if (-not [string]::IsNullOrWhiteSpace($CanonicalEntityId)) {
    $driverArgs.CanonicalEntityId = $CanonicalEntityId
}
if ($Execute) {
    $driverArgs.Execute = $true
    $driverArgs.AllowSourcePreparation = $true
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $driverArgs.EvidencePath = $EvidencePath
}

Write-Host @"
LIVE_MIGRATE_DRIVER: campaign=$CampaignId entity=$CanonicalEntityId execute=$Execute
Named workers must already be connected to this campaign with a loaded player task force.
Read-only preflight does not repair campaign state. -Execute authorizes pinned source preparation and migration.
This driver does not claim success until worker:migration-proof prints MIGRATION_PROOF_SUCCEEDED.
"@

& (Join-Path $PSScriptRoot 'sea-power-worker-migration-proof.ps1') @driverArgs
exit $LASTEXITCODE
