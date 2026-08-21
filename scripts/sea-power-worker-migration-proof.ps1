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

    [string]$SourceWorkerId = 'worker-a',

    [string]$DestinationWorkerId = 'worker-b',

    [string]$CanonicalEntityId = '',

    [switch]$Execute,

    [switch]$AllowGhostRelease,

    [switch]$AllowSourcePreparation,

    [switch]$AllowSchedulerPromotion,

    [switch]$AllowRepair,

    [ValidateRange(30, 1800)]
    [int]$TimeoutSeconds = 600,

    [string]$EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installedGameExecutable = [System.IO.Path]::GetFullPath((Join-Path $SeaPowerDir 'Sea Power.exe'))
if ([string]::IsNullOrWhiteSpace($WorkersRunRoot)) {
    $workersRunRoot = Join-Path $repoRoot '.run\workers'
}
else {
    $workersRunRoot = [System.IO.Path]::GetFullPath($WorkersRunRoot)
}

. (Join-Path $PSScriptRoot 'sea-power-worker-proof-common.ps1')

$sourceWorkerId = Assert-RestrictedWorkerId $SourceWorkerId
$destinationWorkerId = Assert-RestrictedWorkerId $DestinationWorkerId
if ($sourceWorkerId -ceq $destinationWorkerId) {
    throw 'SourceWorkerId and DestinationWorkerId must be distinct named workers.'
}

if ($AllowRepair) {
    $AllowGhostRelease = $true
    $AllowSourcePreparation = $true
}

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

$headers = Get-CampaignAuthHeaders $CampaignTokenFile
$evidence = [ordered]@{
    kind = 'phase-5-cross-worker-migration-proof'
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    campaignId = $CampaignId
    campaignHttpUrl = $CampaignHttpUrl
    sourceWorkerId = $sourceWorkerId
    destinationWorkerId = $destinationWorkerId
    executeRequested = [bool]$Execute
    allowGhostReleaseRequested = [bool]$AllowGhostRelease
    allowSourcePreparationRequested = [bool]$AllowSourcePreparation
    allowSchedulerPromotionRequested = [bool]$AllowSchedulerPromotion
    allowRepairRequested = [bool]$AllowRepair
    phases = @()
    blockers = @()
    succeeded = $false
}

function Add-PhaseEvidence([string]$Name, $Payload) {
    $script:evidence.phases += [ordered]@{
        name = $Name
        recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        payload = $Payload
    }
}

function Add-Blocker([string]$Code, [string]$Message, $Details = $null) {
    $entry = [ordered]@{
        code = $Code
        message = $Message
    }
    if ($null -ne $Details) {
        $entry.details = $Details
    }
    $script:evidence.blockers += $entry
}

function Write-EvidenceArtifact {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    ($evidence | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Test-ConnectedNamedWorkers($Coexistence, $SourceId, $DestinationId) {
    $source = @($Coexistence.Workers | Where-Object { $_.WorkerId -ceq $SourceId })
    $destination = @($Coexistence.Workers | Where-Object { $_.WorkerId -ceq $DestinationId })
    if ($source.Count -ne 1 -or $destination.Count -ne 1) {
        throw 'Coexistence proof did not return exactly one record for each named worker.'
    }
    return [pscustomobject]@{
        Source = $source[0]
        Destination = $destination[0]
        NamedClientInstanceIds = @(
            $source[0].ClientInstanceId
            $destination[0].ClientInstanceId
        )
    }
}

function Get-IslandAssignmentContext($IslandInfo, $CampaignView) {
    if ($null -eq $IslandInfo) {
        return $null
    }
    $primaryAssignments = @(Get-LivePrimaryAssignmentsForIsland $CampaignView $IslandInfo.EntityIds)
    $shadowAssignments = @(Get-LiveShadowAssignmentsForIsland $CampaignView $IslandInfo.EntityIds)
    return [pscustomobject]@{
        Island = $IslandInfo.Island
        EntityIds = $IslandInfo.EntityIds
        AnchorEntityId = $IslandInfo.AnchorEntityId
        PrimaryAssignments = $primaryAssignments
        ShadowAssignments = $shadowAssignments
    }
}

function Get-MigrationContext($CampaignView, $Workers, [string]$EntityId) {
    $islandInfo = Find-TacticalMigrationIsland $CampaignView $EntityId
    return Get-IslandAssignmentContext $islandInfo $CampaignView
}

function Get-RepairContext($CampaignView, $Workers, [string]$EntityId) {
    $islandInfo = Find-InteractionIsland $CampaignView $EntityId
    return Get-IslandAssignmentContext $islandInfo $CampaignView
}

function Test-MigrationPreflight($CampaignView, $Workers, $Context, $InteractionIsland = $null) {
    $blockers = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Context) {
        if ($null -ne $InteractionIsland) {
            if ($InteractionIsland.EntityIds.Count -ne 1) {
                $blockers.Add([ordered]@{
                    code = 'multi-entity-island-unsupported'
                    message = 'Cross-worker migration proof supports singleton interaction islands only.'
                    details = [ordered]@{
                        islandEntityCount = $InteractionIsland.EntityIds.Count
                        entityIds = @($InteractionIsland.EntityIds)
                    }
                })
                return $blockers
            }
            if ($InteractionIsland.Island.currentFidelity -ne 'Tactical') {
                $blockers.Add([ordered]@{
                    code = 'source-preparation-required'
                    message = 'The interaction island is not Tactical. Run -AllowSourcePreparation to pin Primary materialization on the source worker before migration preflight can pass.'
                    details = [ordered]@{
                        anchorEntityId = $InteractionIsland.AnchorEntityId
                        islandId = [string]$InteractionIsland.Island.islandId
                        currentFidelity = [string]$InteractionIsland.Island.currentFidelity
                        livePrimaryAssignmentCount = @(
                            Get-LivePrimaryAssignmentsForIsland $CampaignView $InteractionIsland.EntityIds
                        ).Count
                    }
                })
                return $blockers
            }
        }
        $blockers.Add([ordered]@{
            code = 'migration-island-missing'
            message = 'No eligible single-entity Tactical island was found for migration.'
        })
        return $blockers
    }

    if ($Context.EntityIds.Count -ne 1) {
        $blockers.Add([ordered]@{
            code = 'multi-entity-island-unsupported'
            message = 'Cross-worker migration proof supports singleton Tactical islands only.'
            details = [ordered]@{
                islandEntityCount = $Context.EntityIds.Count
                entityIds = @($Context.EntityIds)
            }
        })
        return $blockers
    }

    if ($Context.EntityIds.Count -ne $Context.PrimaryAssignments.Count) {
        $blockers.Add([ordered]@{
            code = 'shadow-island-incomplete'
            message = 'The tactical island does not have a complete Primary assignment set.'
            details = [ordered]@{
                islandEntityCount = $Context.EntityIds.Count
                primaryAssignmentCount = $Context.PrimaryAssignments.Count
            }
        })
    }

    $sourceClientId = $Workers.Source.ClientInstanceId
    $destinationClientId = $Workers.Destination.ClientInstanceId
    $primaryOwners = @($Context.PrimaryAssignments | Select-Object -ExpandProperty workerClientInstanceId -Unique)
    if ($primaryOwners.Count -ne 1) {
        $blockers.Add([ordered]@{
            code = 'island-worker-affinity-conflict'
            message = 'Primary assignments for the island are split across multiple workers.'
            details = @{ owners = $primaryOwners }
        })
    }
    elseif ($primaryOwners[0] -ne $sourceClientId) {
        $connectedIds = @($CampaignView.workers | Select-Object -ExpandProperty clientInstanceId)
        $ownerConnected = $connectedIds -contains $primaryOwners[0]
        $blockers.Add([ordered]@{
            code = 'source-worker-mismatch'
            message = 'The live Primary assignment is not owned by the requested source worker.'
            details = [ordered]@{
                requestedSourceWorkerId = $Workers.Source.WorkerId
                requestedSourceClientInstanceId = $sourceClientId
                actualPrimaryOwnerClientInstanceId = $primaryOwners[0]
                actualPrimaryOwnerConnected = $ownerConnected
            }
        })
    }

    foreach ($primary in $Context.PrimaryAssignments) {
        if ($primary.lifecycle -notin @('Active', 'Quiescing', 'Quiesced')) {
            $blockers.Add([ordered]@{
                code = 'migration-source-not-ready'
                message = "Primary assignment '$($primary.assignmentId)' lifecycle '$($primary.lifecycle)' is not migratable."
            })
        }
    }

    $destinationWorker = Get-CampaignWorkerByClientId $CampaignView $destinationClientId
    if ($null -eq $destinationWorker) {
        $blockers.Add([ordered]@{
            code = 'shadow-destination-unavailable'
            message = 'Destination worker is not connected to the campaign.'
        })
    }
    elseif ([int]$destinationWorker.migrationOverlapHeadroom -lt $Context.EntityIds.Count) {
        $blockers.Add([ordered]@{
            code = 'shadow-destination-capacity'
            message = 'Destination worker does not have migration-overlap headroom for the island.'
            details = [ordered]@{
                required = $Context.EntityIds.Count
                migrationOverlapHeadroom = [int]$destinationWorker.migrationOverlapHeadroom
            }
        })
    }

    $shadowPrediction = Resolve-ShadowDestinationWorker $CampaignView $Context $sourceClientId
    if ($shadowPrediction.Reason -eq 'no-eligible-destination') {
        $blockers.Add([ordered]@{
            code = 'shadow-destination-unavailable'
            message = 'No connected worker can admit a Prepared Shadow for this island.'
        })
    }
    elseif ($shadowPrediction.Ambiguous) {
        $blockers.Add([ordered]@{
            code = 'shadow-destination-ambiguous'
            message = 'prepare-shadow destination selection is not deterministic for the current worker set.'
            details = [ordered]@{
                reason = $shadowPrediction.Reason
                candidates = @($shadowPrediction.Candidates | ForEach-Object {
                    [ordered]@{
                        clientInstanceId = $_.ClientInstanceId
                        occupiedTacticalEntityCount = $_.OccupiedTacticalEntityCount
                        ownedRecoveryRequiredEntityCount = $_.OwnedRecoveryRequiredEntityCount
                    }
                })
            }
        })
    }
    elseif ($shadowPrediction.SelectedClientInstanceId -ne $destinationClientId) {
        $blockers.Add([ordered]@{
            code = 'shadow-destination-mismatch'
            message = 'prepare-shadow would select a destination other than the requested destination worker.'
            details = [ordered]@{
                requestedDestinationWorkerId = $Workers.Destination.WorkerId
                requestedDestinationClientInstanceId = $destinationClientId
                predictedDestinationClientInstanceId = $shadowPrediction.SelectedClientInstanceId
            }
        })
    }

    $liveShadows = @($Context.ShadowAssignments)
    if ($liveShadows.Count -gt 0) {
        $shadowOwners = @($liveShadows | Select-Object -ExpandProperty workerClientInstanceId -Unique)
        $shadowLifecycles = @($liveShadows | Select-Object -ExpandProperty lifecycle -Unique)
        $acceptable = ($shadowOwners.Count -eq 1 -and $shadowOwners[0] -eq $destinationClientId)
        if (-not $acceptable) {
            $blockers.Add([ordered]@{
                code = 'shadow-already-assigned'
                message = 'Existing Shadow assignments are incomplete or assigned to the wrong worker.'
                details = @{ owners = $shadowOwners; lifecycles = $shadowLifecycles }
            })
        }
    }

    $connectedClientIds = @($CampaignView.workers | Select-Object -ExpandProperty clientInstanceId)
    foreach ($primary in $Context.PrimaryAssignments) {
        $ownerWorker = if ($primary.workerClientInstanceId -eq $sourceClientId) {
            $Workers.Source
        }
        elseif ($primary.workerClientInstanceId -eq $destinationClientId) {
            $Workers.Destination
        }
        else {
            $null
        }
        if ($primary.lifecycle -eq 'Active' -and $primary.workerClientInstanceId -eq $sourceClientId) {
            $gameEntityId = Format-GameEntityId $primary.entityId
            $materialization = Test-WorkerAssignmentMaterialization $Workers.Source $primary $gameEntityId
            if (-not $materialization.Proven) {
                $blockers.Add([ordered]@{
                    code = 'source-not-materialized'
                    message = 'Source worker does not report an active owned campaign object for the Primary assignment.'
                    details = [ordered]@{
                        assignmentId = $primary.assignmentId
                        entityId = $primary.entityId
                        authorityEpoch = [long]$primary.authorityEpoch
                        schemaValid = [bool]$materialization.SchemaValid
                        activeObjectCount = $materialization.ActiveObjectCount
                        revision = if ($null -ne $materialization.Revision) { [long]$materialization.Revision } else { $null }
                        error = $materialization.Error
                        observedEntities = if ($null -ne $materialization.ObservedEntities) { $materialization.ObservedEntities } else { @() }
                    }
                })
            }
        }
        if ($primary.workerClientInstanceId -notin $connectedClientIds) {
            $blockers.Add([ordered]@{
                code = 'source-worker-offline'
                message = 'Primary assignment owner is not a currently connected campaign worker.'
                details = @{ ownerClientInstanceId = $primary.workerClientInstanceId }
            })
        }
        if ($null -eq $ownerWorker -and $primary.workerClientInstanceId -notin $Workers.NamedClientInstanceIds) {
            $blockers.Add([ordered]@{
                code = 'primary-owner-not-named-worker'
                message = 'Primary assignment owner is not one of the named proof workers.'
                details = @{ ownerClientInstanceId = $primary.workerClientInstanceId }
            })
        }
    }

    return $blockers
}

function Invoke-GhostPrimaryRelease(
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$CampaignId,
    $Context,
    [int]$TimeoutSeconds) {
    $view = Get-CampaignView $CampaignHttpUrl $Headers $CampaignId
    $repair = [ordered]@{ steps = @() }
    $primaryOwners = @($Context.PrimaryAssignments | Select-Object -ExpandProperty workerClientInstanceId -Unique)
    $connectedIds = @($view.workers | Select-Object -ExpandProperty clientInstanceId)
    $needsRelease = $false
    foreach ($owner in $primaryOwners) {
        if ($connectedIds -notcontains $owner) {
            $needsRelease = $true
        }
    }
    if (-not $needsRelease) {
        $repair.steps += [ordered]@{ action = 'ghost-release-skipped'; reason = 'no-offline-primary-owner' }
        return $repair
    }

    $targetQuanta = 150
    foreach ($assignment in $Context.PrimaryAssignments) {
        if ($assignment.resolveByQuanta -gt $targetQuanta) {
            $targetQuanta = [int]$assignment.resolveByQuanta
        }
    }
    if ([long]$view.timeQuanta -lt $targetQuanta) {
        $advance = Invoke-CampaignApi Post $CampaignHttpUrl $Headers "/control/campaigns/$CampaignId/advance/$targetQuanta"
        $repair.steps += [ordered]@{
            action = 'advance-campaign-time'
            targetQuanta = $targetQuanta
            commitPosition = [long]$advance.commitPosition
        }
    }
    $resolved = Invoke-CampaignApi Post $CampaignHttpUrl $Headers "/control/campaigns/$CampaignId/resolve-expired/$targetQuanta"
    $repair.steps += [ordered]@{
        action = 'resolve-expired-assignments'
        targetQuanta = $targetQuanta
        releasedCount = @($resolved.assignments).Count
        commitPosition = [long]$resolved.commitPosition
    }
    return $repair
}

function Invoke-SourcePreparation(
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$CampaignId,
    $Workers,
    [string]$AnchorEntityId,
    [int]$TimeoutSeconds) {
    $proofStamp = [Guid]::NewGuid().ToString('N')
    $prepareBody = @{
        campaignId = $CampaignId
        canonicalEntityId = $AnchorEntityId
        preferredWorkerClientInstanceId = [string]$Workers.Source.ClientInstanceId
        idempotencyKey = "live-source-prep-$proofStamp"
    }
    $prepareResponse = Invoke-CampaignApiRaw Post $CampaignHttpUrl $Headers "/control/campaigns/$CampaignId/prepare-source" $prepareBody
    if ($prepareResponse.StatusCode -ge 400) {
        throw "prepare-source failed ($($prepareResponse.StatusCode)): $($prepareResponse.Content)"
    }
    $prepare = $prepareResponse.Content | ConvertFrom-Json
    $phase = [string]$prepare.phase
    $gameEntityId = Format-GameEntityId $AnchorEntityId
    $readyView = Wait-CampaignCondition `
        $CampaignHttpUrl `
        $Headers `
        $CampaignId `
        {
            param($candidate)
            $primary = @($candidate.assignments | Where-Object {
                $_.entityId -eq $AnchorEntityId `
                    -and $_.role -eq 'Primary' `
                    -and $_.lifecycle -eq 'Active' `
                    -and $_.workerClientInstanceId -eq $Workers.Source.ClientInstanceId
            })
            if ($primary.Count -ne 1) { return $false }
            $materialization = Test-WorkerAssignmentMaterialization $Workers.Source $primary[0] $gameEntityId
            return $materialization.Proven
        } `
        'active Primary promotion and exact native materialization on the requested source worker' `
        $TimeoutSeconds
    $primary = @($readyView.assignments | Where-Object {
        $_.entityId -eq $AnchorEntityId `
            -and $_.role -eq 'Primary' `
            -and $_.lifecycle -eq 'Active'
    } | Select-Object -First 1)
    $materialization = Test-WorkerAssignmentMaterialization $Workers.Source $primary $gameEntityId
    if (-not $materialization.Proven) {
        throw "Source preparation completed in campaign state but exact surface materialization proof failed: $($materialization.Error)"
    }
    return [ordered]@{
        action = 'prepare-source'
        phase = $phase
        observedEntityId = $AnchorEntityId
        preferredWorkerClientInstanceId = [string]$Workers.Source.ClientInstanceId
        primaryOwnerClientInstanceId = [string]$primary.workerClientInstanceId
        primaryAssignmentId = [string]$primary.assignmentId
        primaryAuthorityEpoch = [long]$primary.authorityEpoch
        promotionMatchedRequestedSource = ($primary.workerClientInstanceId -eq $Workers.Source.ClientInstanceId)
        surfaceAssignmentId = [string]$materialization.AssignmentId
        surfaceEntityId = [string]$materialization.EntityId
        surfaceLifecycleState = [string]$materialization.LifecycleState
        surfaceRevision = [long]$materialization.Revision
        activeObjectCount = $materialization.ActiveObjectCount
        commitPosition = [long]$readyView.commitPosition
    }
}

function Invoke-SchedulerPromotionAttempt(
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$CampaignId,
    $Workers,
    [string]$AnchorEntityId,
    [int]$TimeoutSeconds) {
    $scheduleBody = @{
        observedEntityIds = @($AnchorEntityId)
        controlledEntityIds = @()
        health = 'Healthy'
        eligibility = 'Eligible'
        tacticalEntityCapacity = [int]$Workers.Source.TacticalEntityCapacity
        tacticalReserveCapacity = [int]$Workers.Source.TacticalReserveCapacity
        backlogDepth = 0
    }
    $scheduled = Invoke-CampaignApi Post $CampaignHttpUrl $Headers "/control/campaigns/$CampaignId/schedule" $scheduleBody
  $readyView = Wait-CampaignCondition `
        $CampaignHttpUrl `
        $Headers `
        $CampaignId `
        {
            param($candidate)
            $primary = @($candidate.assignments | Where-Object {
                $_.entityId -eq $AnchorEntityId `
                    -and $_.role -eq 'Primary' `
                    -and $_.lifecycle -eq 'Active'
            })
            return $primary.Count -eq 1
        } `
        'active Primary promotion on a connected worker' `
        $TimeoutSeconds

    $primary = @($readyView.assignments | Where-Object {
        $_.entityId -eq $AnchorEntityId -and $_.role -eq 'Primary' -and $_.lifecycle -eq 'Active'
    } | Select-Object -First 1)
    return [ordered]@{
        action = 'schedule-promotion'
        observedEntityId = $AnchorEntityId
        transitionCount = @($scheduled.transitions).Count
        commitPosition = [long]$readyView.commitPosition
        primaryOwnerClientInstanceId = [string]$primary.workerClientInstanceId
        requestedSourceClientInstanceId = [string]$Workers.Source.ClientInstanceId
        promotionMatchedRequestedSource = ($primary.workerClientInstanceId -eq $Workers.Source.ClientInstanceId)
    }
}

function Get-MigrationRecoveryContext($CampaignView, $Workers, [string]$CanonicalEntityId = '') {
    $destinationClientId = $Workers.Destination.ClientInstanceId
    $destinationPrimaries = @($CampaignView.assignments | Where-Object {
        $_.role -eq 'Primary' `
            -and $_.lifecycle -eq 'Active' `
            -and $_.workerClientInstanceId -eq $destinationClientId
    })
    if ($destinationPrimaries.Count -ne 1) {
        return $null
    }

    $destinationPrimary = $destinationPrimaries[0]
    if (-not [string]::IsNullOrWhiteSpace($CanonicalEntityId) `
        -and $destinationPrimary.entityId -ne $CanonicalEntityId) {
        return $null
    }

    $entity = @($CampaignView.entities | Where-Object {
        $_.entityId -eq $destinationPrimary.entityId
    } | Select-Object -First 1)
    if ($null -eq $entity -or $entity.fidelity -ne 'Tactical') {
        return $null
    }

    $sourceClientId = $Workers.Source.ClientInstanceId
    $destinationAuthorityEpoch = [long]$destinationPrimary.authorityEpoch
    $releasedCandidates = @($CampaignView.assignments | Where-Object {
        $_.entityId -eq $destinationPrimary.entityId `
            -and $_.role -eq 'Primary' `
            -and $_.lifecycle -eq 'Released' `
            -and $_.workerClientInstanceId -eq $sourceClientId `
            -and [long]$_.authorityEpoch -lt $destinationAuthorityEpoch
    })
    if ($releasedCandidates.Count -eq 0) {
        return $null
    }

    $latestReleasedEpoch = ($releasedCandidates | Measure-Object -Property authorityEpoch -Maximum).Maximum
    $releasedSources = @($releasedCandidates | Where-Object {
        [long]$_.authorityEpoch -eq $latestReleasedEpoch
    })
    if ($releasedSources.Count -ne 1) {
        return $null
    }

    $releasedSource = $releasedSources[0]
    $sourceGameEntityId = Format-GameEntityId $releasedSource.entityId
    $sourceMaterialization = Test-WorkerAssignmentMaterialization `
        $Workers.Source `
        $releasedSource `
        $sourceGameEntityId `
        'active'
    if ($sourceMaterialization.Proven) {
        return $null
    }

    $gameEntityId = Format-GameEntityId $destinationPrimary.entityId
    $destinationMaterialization = Test-WorkerAssignmentMaterialization `
        $Workers.Destination `
        $destinationPrimary `
        $gameEntityId `
        'active'
    if ($destinationMaterialization.Proven) {
        return $null
    }

    return [ordered]@{
        anchorEntityId = [string]$destinationPrimary.entityId
        migrationCommitPosition = [long]$CampaignView.commitPosition
        destinationPrimaryAssignmentId = [string]$destinationPrimary.assignmentId
        destinationPrimaryAuthorityEpoch = [long]$destinationPrimary.authorityEpoch
        destinationGameEntityId = $gameEntityId
        destinationLifecycleState = [string]$destinationMaterialization.LifecycleState
        destinationRevision = [long]$destinationMaterialization.Revision
        releasedSourceAssignmentId = [string]$releasedSource.assignmentId
        releasedSourceAuthorityEpoch = [long]$releasedSource.authorityEpoch
        releasedSourceLifecycleState = [string]$sourceMaterialization.LifecycleState
    }
}

function Invoke-MigrationRecoveryVerification(
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$CampaignId,
    $Workers,
    $RecoveryContext,
    [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastObservation = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $view = Get-CampaignView $CampaignHttpUrl $Headers $CampaignId
        $destinationPrimary = @($view.assignments | Where-Object {
            $_.assignmentId -eq $RecoveryContext.destinationPrimaryAssignmentId `
                -and $_.role -eq 'Primary' `
                -and $_.lifecycle -eq 'Active' `
                -and $_.workerClientInstanceId -eq $Workers.Destination.ClientInstanceId
        } | Select-Object -First 1)
        if ($destinationPrimary.Count -ne 1) {
            throw 'Recovery verification lost the destination Primary assignment.'
        }

        $materialization = Test-WorkerAssignmentMaterialization `
            $Workers.Destination `
            $destinationPrimary[0] `
            $RecoveryContext.destinationGameEntityId `
            'active'
        $lastObservation = [ordered]@{
            commitPosition = [long]$view.commitPosition
            lifecycleState = [string]$materialization.LifecycleState
            revision = [long]$materialization.Revision
            proven = [bool]$materialization.Proven
            error = [string]$materialization.Error
        }
        if ($materialization.Proven) {
            return [ordered]@{
                mode = 'activation-recovery'
                migrationCommitPosition = [long]$RecoveryContext.migrationCommitPosition
                destinationPrimaryAssignmentId = [string]$RecoveryContext.destinationPrimaryAssignmentId
                destinationPrimaryAuthorityEpoch = [long]$RecoveryContext.destinationPrimaryAuthorityEpoch
                destinationSurfaceAssignmentId = [string]$materialization.AssignmentId
                destinationSurfaceEntityId = [string]$materialization.EntityId
                destinationSurfaceLifecycleState = [string]$materialization.LifecycleState
                destinationSurfaceRevision = [long]$materialization.Revision
                commitPosition = [long]$view.commitPosition
                observations = @($lastObservation)
            }
        }

        Start-Sleep -Seconds 2
    }

    throw "Recovery verification timed out waiting for destination activation materialization. Last observation: $($lastObservation | ConvertTo-Json -Compress)"
}

function Invoke-MigrationFlow(
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$CampaignId,
    $Workers,
    $Context,
    [int]$TimeoutSeconds) {
    if ($Context.EntityIds.Count -ne 1) {
        throw 'Invoke-MigrationFlow requires a singleton tactical island.'
    }

    $anchorEntityId = $Context.AnchorEntityId
    $entityId = $Context.EntityIds[0]
    $sourcePrimaries = @($Context.PrimaryAssignments | Sort-Object authorityEpoch -Descending)
    if ($sourcePrimaries.Count -ne 1) {
        throw 'Migration requires exactly one live Primary assignment for the singleton island.'
    }
    $sourcePrimary = $sourcePrimaries[0]
    $assignmentScopedId = Format-ScopedAssignmentId $sourcePrimary.assignmentId
    $gameEntityId = Format-GameEntityId $anchorEntityId
    $proofStamp = [Guid]::NewGuid().ToString('N')
    $idempotencyPrefix = "live-migration-$proofStamp"

    $prepareBody = @{
        campaignId = $CampaignId
        canonicalEntityId = $anchorEntityId
        preferredDestinationClientInstanceId = [string]$Workers.Destination.ClientInstanceId
        idempotencyKey = "$idempotencyPrefix-prepare-shadow"
    }
    $prepareResponse = Invoke-CampaignApiRaw Post $CampaignHttpUrl $Headers "/control/campaigns/$CampaignId/prepare-shadow" $prepareBody
    if ($prepareResponse.StatusCode -ge 400) {
        throw "prepare-shadow failed ($($prepareResponse.StatusCode)): $($prepareResponse.Content)"
    }
    $prepare = $prepareResponse.Content | ConvertFrom-Json
    $actualDestinationClientId = [string]$prepare.destinationClientInstanceId
    $namedClientIds = @($Workers.NamedClientInstanceIds)
    if ($actualDestinationClientId -notin $namedClientIds) {
        throw "prepare-shadow selected non-named destination '$actualDestinationClientId'. Aborting before migration to avoid an unintended Shadow on an uncontrolled worker."
    }
    if ($actualDestinationClientId -ne $Workers.Destination.ClientInstanceId) {
        throw "prepare-shadow selected named destination '$actualDestinationClientId' instead of requested '$($Workers.Destination.ClientInstanceId)'. Aborting to avoid unintended Shadow state."
    }

    $preparedView = Wait-CampaignCondition `
        $CampaignHttpUrl `
        $Headers `
        $CampaignId `
        {
            param($candidate)
            $shadows = @(Get-LiveShadowAssignmentsForIsland $candidate $Context.EntityIds)
            return ($shadows.Count -eq $Context.EntityIds.Count) `
                -and (@($shadows | Where-Object { $_.lifecycle -eq 'Prepared' }).Count -eq $Context.EntityIds.Count) `
                -and (@($shadows | Where-Object { $_.workerClientInstanceId -eq $actualDestinationClientId }).Count -eq $Context.EntityIds.Count)
        } `
        'Shadow assignments reaching Prepared on the destination worker' `
        $TimeoutSeconds
    $shadowAssignments = @(Get-LiveShadowAssignmentsForIsland $preparedView $Context.EntityIds)

    $expectedRevision = Get-WorkerGameRevision `
        $Workers.Source.Layout `
        $sourcePrimary.assignmentId `
        $gameEntityId
    if ($expectedRevision -le 0) {
        throw 'Could not resolve the source worker game revision for quiesce.'
    }

    $quiesceBody = @{
        campaignId = $CampaignId
        assignmentId = $assignmentScopedId
        gameEntityId = $gameEntityId
        expectedGameRevision = $expectedRevision
        idempotencyKey = "$idempotencyPrefix-source-quiesce"
    }
    $quiesceResponse = Invoke-CampaignApiRaw Post $CampaignHttpUrl $Headers '/control/quiesce' $quiesceBody
    if ($quiesceResponse.StatusCode -ge 400) {
        throw "quiesce failed ($($quiesceResponse.StatusCode)): $($quiesceResponse.Content)"
    }

    $quiescedView = Wait-CampaignCondition `
        $CampaignHttpUrl `
        $Headers `
        $CampaignId `
        {
            param($candidate)
            $primary = @($candidate.assignments | Where-Object {
                $_.assignmentId -eq $sourcePrimary.assignmentId -and $_.role -eq 'Primary'
            })
            return ($primary.Count -eq 1 -and $primary[0].lifecycle -eq 'Quiesced')
        } `
        'source Primary reaching Quiesced' `
        $TimeoutSeconds

    $migrateBody = @{
        campaignId = $CampaignId
        canonicalEntityId = $anchorEntityId
        idempotencyKey = "$idempotencyPrefix-migrate"
    }
    $migrateResponse = Invoke-CampaignApi Post $CampaignHttpUrl $Headers "/control/campaigns/$CampaignId/migrate-tactical-island" $migrateBody
    $migrationPhase = [string]$migrateResponse.phase
    $migrationCommitPosition = if ($null -ne $migrateResponse.migrationCommitPosition) {
        [long]$migrateResponse.migrationCommitPosition
    }
    else {
        $null
    }

    $committedView = $null
    if ($migrationPhase -eq 'staging-queued') {
        $committedView = Wait-CampaignCondition `
            $CampaignHttpUrl `
            $Headers `
            $CampaignId `
            {
                param($candidate)
                $destinationPrimaries = @($candidate.assignments | Where-Object {
                    $Context.EntityIds -contains $_.entityId `
                        -and $_.role -eq 'Primary' `
                        -and $_.lifecycle -eq 'Active' `
                        -and $_.workerClientInstanceId -eq $actualDestinationClientId
                })
                $releasedSource = @($candidate.assignments | Where-Object {
                    $_.assignmentId -eq $sourcePrimary.assignmentId `
                        -and $_.lifecycle -eq 'Released'
                })
                return ($destinationPrimaries.Count -eq $Context.EntityIds.Count) -and ($releasedSource.Count -eq 1)
            } `
            'migration commit with destination Primary Active and source Primary Released' `
            $TimeoutSeconds
        if ($null -ne $committedView) {
            $migrationCommitPosition = [long]$committedView.commitPosition
            $migrationPhase = 'migration-committed'
        }
    }

    $finalView = if ($null -ne $committedView) { $committedView } else { Get-CampaignView $CampaignHttpUrl $Headers $CampaignId }
    $destinationPrimaries = @($finalView.assignments | Where-Object {
        $Context.EntityIds -contains $_.entityId `
            -and $_.role -eq 'Primary' `
            -and $_.lifecycle -eq 'Active' `
            -and $_.workerClientInstanceId -eq $actualDestinationClientId
    })
    $released = @($finalView.assignments | Where-Object {
        $_.assignmentId -eq $sourcePrimary.assignmentId -and $_.lifecycle -eq 'Released'
    })
    if ($destinationPrimaries.Count -ne $Context.EntityIds.Count -or $released.Count -ne 1) {
        throw 'Post-migration campaign view did not show destination Primary Active and source Primary Released for every island member.'
    }

    $destinationPrimary = $destinationPrimaries[0]
    $destinationGameEntityId = Format-GameEntityId $anchorEntityId
    $destinationMaterialization = Test-WorkerAssignmentMaterialization `
        $Workers.Destination `
        $destinationPrimary `
        $destinationGameEntityId
    if (-not $destinationMaterialization.Proven) {
        throw "Post-migration destination exact surface materialization proof failed: $($destinationMaterialization.Error)"
    }

    $sourceMaterialization = Test-WorkerAssignmentMaterialization `
        $Workers.Source `
        $sourcePrimary `
        $gameEntityId `
        'active'
    if ($sourceMaterialization.Proven) {
        throw 'Post-migration source worker still reports active authority for the released Primary assignment.'
    }
    $sourceResources = Get-WorkerSurfaceResources $Workers.Source
    $scopedSourceAssignmentId = Format-ScopedAssignmentId $sourcePrimary.assignmentId
    $sourceRetainedAuthority = @($sourceResources.entities | Where-Object {
        $_.assignmentId -eq $scopedSourceAssignmentId `
            -and $_.lifecycleState -eq 'active'
    })
    if ($sourceRetainedAuthority.Count -gt 0) {
        throw 'Post-migration source worker still retains an active owned surface record for the released Primary assignment.'
    }

    return [ordered]@{
        idempotencyPrefix = $idempotencyPrefix
        prepareShadowDestinationClientInstanceId = $actualDestinationClientId
        shadowAssignmentIds = @($shadowAssignments | Select-Object -ExpandProperty assignmentId)
        sourceAssignmentId = $sourcePrimary.assignmentId
        sourceAuthorityEpoch = [long]$sourcePrimary.authorityEpoch
        destinationPrimaryAssignmentId = [string]$destinationPrimary.assignmentId
        destinationPrimaryAuthorityEpoch = [long]$destinationPrimary.authorityEpoch
        destinationSurfaceAssignmentId = [string]$destinationMaterialization.AssignmentId
        destinationSurfaceEntityId = [string]$destinationMaterialization.EntityId
        destinationSurfaceLifecycleState = [string]$destinationMaterialization.LifecycleState
        destinationSurfaceRevision = [long]$destinationMaterialization.Revision
        migrationPhase = $migrationPhase
        migrateApiPhase = [string]$migrateResponse.phase
        migrationCommitPosition = $migrationCommitPosition
        commitPosition = [long]$finalView.commitPosition
        sourceResources = [ordered]@{
            activeObjectCount = [int]$sourceResources.activeObjectCount
            suspendedRetainedObjectCount = [int]$sourceResources.suspendedRetainedObjectCount
            ownedRecordCount = [int]$sourceResources.ownedRecordCount
            retainedActiveAuthorityForReleasedAssignment = ($sourceRetainedAuthority.Count -gt 0)
        }
        destinationResources = [ordered]@{
            activeObjectCount = [int]$destinationMaterialization.ActiveObjectCount
            suspendedRetainedObjectCount = [int]$destinationMaterialization.SuspendedRetainedObjectCount
            ownedRecordCount = [int]$destinationMaterialization.OwnedRecordCount
            assignmentId = [string]$destinationMaterialization.AssignmentId
            entityId = [string]$destinationMaterialization.EntityId
            lifecycleState = [string]$destinationMaterialization.LifecycleState
            revision = [long]$destinationMaterialization.Revision
        }
    }
}

try {
    $coexistence = Prove-NamedWorkerCoexistence `
        $CampaignHttpUrl `
        $CampaignId `
        $CampaignTokenFile `
        $workersRunRoot `
        @($sourceWorkerId, $destinationWorkerId) `
        $installedGameExecutable
    Add-PhaseEvidence 'coexistence' @{
        provenWorkerCount = $coexistence.ProvenWorkerCount
        workers = @($coexistence.Workers | ForEach-Object {
            [ordered]@{
                workerId = $_.WorkerId
                clientInstanceId = $_.ClientInstanceId
                gameSessionId = $_.GameSessionId
                gameProcessId = $_.GameProcessId
                runtimeFingerprintSha256 = (Format-RedactedSha256 $_.RuntimeFingerprintSha256)
                migrationOverlapHeadroom = $_.MigrationOverlapHeadroom
            }
        })
    }

    $workers = Test-ConnectedNamedWorkers $coexistence $sourceWorkerId $destinationWorkerId
    $campaignView = Get-CampaignView $CampaignHttpUrl $headers $CampaignId
    if ($campaignView.campaignId -ne $CampaignId) {
        throw "Campaign binding mismatch: requested '$CampaignId', service returned '$($campaignView.campaignId)'."
    }

    $interactionIsland = if (-not [string]::IsNullOrWhiteSpace($CanonicalEntityId)) {
        Find-InteractionIsland $campaignView $CanonicalEntityId
    }
    else {
        $null
    }
    $migrationContext = Get-MigrationContext $campaignView $workers $CanonicalEntityId
    if ($null -eq $interactionIsland -and $null -ne $migrationContext) {
        $interactionIsland = [pscustomobject]@{
            Island = $migrationContext.Island
            EntityIds = $migrationContext.EntityIds
            AnchorEntityId = $migrationContext.AnchorEntityId
        }
    }
    $shadowPrediction = if ($null -ne $migrationContext) {
        Resolve-ShadowDestinationWorker $campaignView $migrationContext $workers.Source.ClientInstanceId
    }
    else {
        $null
    }
    Add-PhaseEvidence 'preflight-initial' @{
        commitPosition = [long]$campaignView.commitPosition
        timeQuanta = [long]$campaignView.timeQuanta
        anchorEntityId = if ($null -eq $migrationContext) {
            if ($null -ne $interactionIsland) { $interactionIsland.AnchorEntityId } else { '' }
        }
        else { $migrationContext.AnchorEntityId }
        islandId = if ($null -eq $migrationContext) {
            if ($null -ne $interactionIsland) { [string]$interactionIsland.Island.islandId } else { '' }
        }
        else { $migrationContext.Island.islandId }
        islandCurrentFidelity = if ($null -eq $interactionIsland) { '' } else { [string]$interactionIsland.Island.currentFidelity }
        islandEntityCount = if ($null -eq $migrationContext) { 0 } else { $migrationContext.EntityIds.Count }
        predictedShadowDestinationClientInstanceId = if ($null -eq $shadowPrediction) { '' } else { $shadowPrediction.SelectedClientInstanceId }
        shadowDestinationAmbiguous = if ($null -eq $shadowPrediction) { $false } else { [bool]$shadowPrediction.Ambiguous }
        primaryAssignments = if ($null -eq $migrationContext) { @() } else {
            @(@($migrationContext.PrimaryAssignments | ForEach-Object {
                [ordered]@{
                    assignmentId = $_.assignmentId
                    entityId = $_.entityId
                    lifecycle = $_.lifecycle
                    authorityEpoch = [long]$_.authorityEpoch
                    workerClientInstanceId = $_.workerClientInstanceId
                }
            }))
        }
        shadowAssignments = if ($null -eq $migrationContext) { @() } else {
            @(@($migrationContext.ShadowAssignments | ForEach-Object {
                [ordered]@{
                    assignmentId = $_.assignmentId
                    entityId = $_.entityId
                    lifecycle = $_.lifecycle
                    workerClientInstanceId = $_.workerClientInstanceId
                }
            }))
        }
    }

    $recoveryContext = Get-MigrationRecoveryContext $campaignView $workers $CanonicalEntityId
    if ($null -ne $recoveryContext) {
        Add-PhaseEvidence 'recovery-detected' $recoveryContext
        if (-not $Execute) {
            $evidence.succeeded = $false
            $evidence.preflightOnly = $true
            $evidence.recoveryPending = $true
            $evidence | ConvertTo-Json -Depth 12
            Write-Host 'MIGRATION_PROOF_RECOVERY_PENDING: campaign migration is committed but destination activation is not materialized. Rebuild/restart the destination companion, then rerun with -Execute for recovery verification.'
            Write-EvidenceArtifact $EvidencePath
            exit 0
        }

        $migrationResult = Invoke-MigrationRecoveryVerification `
            $CampaignHttpUrl `
            $headers `
            $CampaignId `
            $workers `
            $recoveryContext `
            $TimeoutSeconds
        Add-PhaseEvidence 'recovery' $migrationResult
        $evidence.succeeded = $true
        $evidence.recoveryMode = $true
        $evidence | ConvertTo-Json -Depth 12
        Write-Host 'MIGRATION_PROOF_RECOVERY_SUCCEEDED: committed migration converged after destination activation materialized.'
        Write-EvidenceArtifact $EvidencePath
        exit 0
    }

    $preflightBlockers = @(Test-MigrationPreflight $campaignView $workers $migrationContext $interactionIsland)
    foreach ($blocker in $preflightBlockers) {
        Add-Blocker $blocker.code $blocker.message $blocker.details
    }

    $repairRequested = [bool]($AllowGhostRelease -or $AllowSourcePreparation -or $AllowSchedulerPromotion)
    $ghostReleaseOnlyRequested = [bool]($AllowGhostRelease -and -not $AllowSourcePreparation -and -not $AllowSchedulerPromotion)
    $repairCompleted = $false

    if ($repairRequested -and $preflightBlockers.Count -gt 0) {
        if ([string]::IsNullOrWhiteSpace($CanonicalEntityId)) {
            if ($null -ne $migrationContext) {
                $CanonicalEntityId = $migrationContext.AnchorEntityId
            }
            elseif ($null -ne $interactionIsland) {
                $CanonicalEntityId = $interactionIsland.AnchorEntityId
            }
            else {
                $tacticalEntity = @($campaignView.entities | Where-Object {
                    $_.fidelity -eq 'Tactical'
                } | Select-Object -First 1)
                if ($null -ne $tacticalEntity) {
                    $CanonicalEntityId = [string]$tacticalEntity.entityId
                }
            }
        }
        if ([string]::IsNullOrWhiteSpace($CanonicalEntityId)) {
            throw 'Repair requires -CanonicalEntityId or a resolvable interaction island in campaign state.'
        }
        $interactionIsland = Find-InteractionIsland $campaignView $CanonicalEntityId
        if ($null -eq $interactionIsland) {
            throw "Repair could not locate a singleton interaction island for canonical entity '$CanonicalEntityId'."
        }
        $repairContext = Get-RepairContext $campaignView $workers $CanonicalEntityId
        if ($null -eq $repairContext) {
            throw "Repair could not build assignment context for canonical entity '$CanonicalEntityId'."
        }

        $repairSteps = @()
        if ($AllowGhostRelease) {
            $ghostRelease = Invoke-GhostPrimaryRelease `
                $CampaignHttpUrl `
                $headers `
                $CampaignId `
                $repairContext `
                $TimeoutSeconds
            $repairSteps += $ghostRelease.steps
        }
        if ($AllowSourcePreparation) {
            $sourcePreparation = Invoke-SourcePreparation `
                $CampaignHttpUrl `
                $headers `
                $CampaignId `
                $workers `
                $repairContext.AnchorEntityId `
                $TimeoutSeconds
            $repairSteps += $sourcePreparation
        }
        if ($AllowSchedulerPromotion) {
            $promotion = Invoke-SchedulerPromotionAttempt `
                $CampaignHttpUrl `
                $headers `
                $CampaignId `
                $workers `
                $repairContext.AnchorEntityId `
                $TimeoutSeconds
            $repairSteps += $promotion
        }
        Add-PhaseEvidence 'repair' @{ steps = $repairSteps }
        $repairCompleted = $true

        $campaignView = Get-CampaignView $CampaignHttpUrl $headers $CampaignId
        $interactionIsland = Find-InteractionIsland $campaignView $CanonicalEntityId
        $migrationContext = Get-MigrationContext $campaignView $workers $CanonicalEntityId
        if ($null -eq $interactionIsland -and $null -ne $migrationContext) {
            $interactionIsland = [pscustomobject]@{
                Island = $migrationContext.Island
                EntityIds = $migrationContext.EntityIds
                AnchorEntityId = $migrationContext.AnchorEntityId
            }
        }
        $preflightBlockers = @(Test-MigrationPreflight $campaignView $workers $migrationContext $interactionIsland)
        $evidence.blockers = @()
        foreach ($blocker in $preflightBlockers) {
            Add-Blocker $blocker.code $blocker.message $blocker.details
        }
        Add-PhaseEvidence 'preflight-after-repair' @{
            anchorEntityId = if ($null -eq $migrationContext) {
                if ($null -ne $interactionIsland) { $interactionIsland.AnchorEntityId } else { '' }
            }
            else { $migrationContext.AnchorEntityId }
            islandCurrentFidelity = if ($null -eq $interactionIsland) { '' } else { [string]$interactionIsland.Island.currentFidelity }
            primaryAssignments = if ($null -eq $migrationContext) { @() } else {
                @(@($migrationContext.PrimaryAssignments | ForEach-Object {
                    [ordered]@{
                        assignmentId = $_.assignmentId
                        lifecycle = $_.lifecycle
                        workerClientInstanceId = $_.workerClientInstanceId
                    }
                }))
            }
        }
    }

    if ($evidence.blockers.Count -gt 0) {
        $evidence.succeeded = $false
        if ($repairCompleted -and $ghostReleaseOnlyRequested) {
            $evidence.resumable = $true
            $evidence.repairStepCompleted = 'ghost-release'
            $evidence | ConvertTo-Json -Depth 12
            Write-Host 'MIGRATION_PROOF_REPAIR_STEP_COMPLETED: ghost Primary release finished; continue with -AllowSourcePreparation when ready.'
            Write-EvidenceArtifact $EvidencePath
            exit 0
        }
        $evidence | ConvertTo-Json -Depth 12
        Write-Host 'MIGRATION_PROOF_PREFLIGHT_FAILED: live cross-worker migration is not safe to execute.'
        Write-EvidenceArtifact $EvidencePath
        exit 2
    }

    if (-not $Execute) {
        $evidence.succeeded = $false
        $evidence.preflightOnly = $true
        $evidence | ConvertTo-Json -Depth 12
        Write-Host 'MIGRATION_PROOF_PREFLIGHT_PASSED: state is ready, but -Execute was not supplied.'
        Write-EvidenceArtifact $EvidencePath
        exit 0
    }

    $migrationResult = Invoke-MigrationFlow `
        $CampaignHttpUrl `
        $headers `
        $CampaignId `
        $workers `
        $migrationContext `
        $TimeoutSeconds
    Add-PhaseEvidence 'migration' $migrationResult
    $evidence.succeeded = $true
    $evidence | ConvertTo-Json -Depth 12
    Write-Host 'MIGRATION_PROOF_SUCCEEDED: source->destination final-checkpoint migration completed.'
    Write-EvidenceArtifact $EvidencePath
    exit 0
}
catch {
    Add-PhaseEvidence 'failure' @{
        message = $_.Exception.Message
    }
    $evidence.succeeded = $false
    $evidence | ConvertTo-Json -Depth 12
    Write-EvidenceArtifact $EvidencePath
    Write-Error $_
    exit 1
}
