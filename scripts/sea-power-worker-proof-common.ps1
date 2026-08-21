Set-StrictMode -Version Latest

$script:WorkerProofSyntheticGameSessionIds = @(
    'container-worker-probe',
    'shadow-destination-worker',
    'shadow-migration-worker'
)
$script:WorkerProofSyntheticClientInstanceIds = @(
    '20000000000000000000000000000002'
)
$script:WorkerProofProcessStartTimeToleranceSeconds = 2

function Ensure-SingleProcess($Process) {
    if ($null -eq $Process) { return $null }
    if ($Process -is [System.Diagnostics.Process]) { return $Process }
    $processes = @($Process)
    if ($processes.Count -ne 1) { return $null }
    return $processes[0]
}

function Resolve-UtcTimestamp($Value) {
    if ($null -eq $Value) { return $null }

    if ($Value -is [DateTimeOffset]) {
        return $Value.ToUniversalTime()
    }

    if ($Value -is [DateTime]) {
        $utc = $Value
        if ($utc.Kind -eq [DateTimeKind]::Local) {
            $utc = $utc.ToUniversalTime()
        }
        elseif ($utc.Kind -eq [DateTimeKind]::Unspecified) {
            $utc = [DateTime]::SpecifyKind($utc, [DateTimeKind]::Utc)
        }
        return [DateTimeOffset]::new($utc, [TimeSpan]::Zero)
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }

    try {
        return [DateTimeOffset]::Parse(
            $text.Trim(),
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    }
    catch {
        return $null
    }
}

function Test-UtcTimestampPresent($Value) {
    return $null -ne (Resolve-UtcTimestamp $Value)
}

function Format-UtcTimestamp($Value) {
    $resolved = Resolve-UtcTimestamp $Value
    if ($null -eq $resolved) { return '' }
    return $resolved.ToString('o')
}

function Assert-RestrictedWorkerId([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw 'WorkerId is required.'
    }
    $trimmed = $Value.Trim()
    if ($trimmed.Length -gt 64) {
        throw "WorkerId '$trimmed' must contain at most 64 characters."
    }
    foreach ($character in $trimmed.ToCharArray()) {
        $isValid = (
            ($character -ge 'A' -and $character -le 'Z') -or
            ($character -ge 'a' -and $character -le 'z') -or
            ($character -ge '0' -and $character -le '9') -or
            ($character -eq '.' -or $character -eq '_' -or $character -eq '-')
        )
        if (-not $isValid) {
            throw "WorkerId '$trimmed' must contain only ASCII letters, digits, '.', '_', or '-'."
        }
    }
    return $trimmed
}

function Normalize-WorkerIds([string[]]$Ids) {
    $ordered = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($id in @($Ids)) {
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        foreach ($part in $id.Split(
            [char[]]@(',', ' ', "`t", "`r", "`n"),
            [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $trimmed = Assert-RestrictedWorkerId $part
            if (-not $seen.ContainsKey($trimmed)) {
                $seen[$trimmed] = $true
                $ordered.Add($trimmed)
            }
        }
    }
    return @($ordered.ToArray())
}

function Test-SyntheticFingerprint([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    if ($Value -notmatch '^[0-9a-f]{64}$') { return $true }
    return $Value -match '^(.)\1{63}$'
}

function Format-RedactedSha256([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    if ($Value.Length -le 16) { return $Value }
    return "$($Value.Substring(0, 8))...$($Value.Substring($Value.Length - 8))"
}

function Invoke-WorkerControl([string]$ControlUrl, [string]$TokenPath, [string]$Path) {
    $token = (Get-Content -LiteralPath $TokenPath -Raw).Trim()
    $headers = @{ 'X-Crimean-Tom-Token' = $token }
    Invoke-RestMethod -Method Get -Uri "$ControlUrl$Path" -Headers $headers -TimeoutSec 8
}

function Invoke-WorkerControlPost(
    [string]$ControlUrl,
    [string]$TokenPath,
    [string]$Path,
    $Body) {
    $token = (Get-Content -LiteralPath $TokenPath -Raw).Trim()
    $headers = @{ 'X-Crimean-Tom-Token' = $token }
    $json = if ($null -eq $Body) { '{}' } else { $Body | ConvertTo-Json -Depth 8 -Compress }
    Invoke-RestMethod `
        -Method Post `
        -Uri "$ControlUrl$Path" `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $json `
        -TimeoutSec 30
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
    $RecordedStartTime,
    [int]$ToleranceSeconds,
    [bool]$RequireRecordedStartTime = $true) {
    $recordedStart = Resolve-UtcTimestamp $RecordedStartTime
    if ($null -eq $recordedStart) {
        return -not $RequireRecordedStartTime
    }
    $process = Ensure-SingleProcess $Process
    if ($null -eq $process) { return $false }
    $actualStart = [DateTimeOffset]::new($process.StartTime.ToUniversalTime(), [TimeSpan]::Zero)
    $deltaForward = ($actualStart - $recordedStart).TotalSeconds
    $deltaBackward = ($recordedStart - $actualStart).TotalSeconds
    return $deltaForward -le $ToleranceSeconds -and $deltaBackward -le $ToleranceSeconds
}

function Test-LiveSeaPowerProcess(
    [int]$ProcessId,
    [string]$ExpectedExecutable,
    [string]$ExpectedUnityLog,
    $RecordedStartTimeUtc,
    [string]$InstalledGameExecutable) {
    if ($ProcessId -le 0) {
        throw 'Recorded game process ID is missing or invalid.'
    }
    if (-not (Test-UtcTimestampPresent $RecordedStartTimeUtc)) {
        throw 'Recorded game process start time is missing from worker metadata.'
    }

    $process = Ensure-SingleProcess (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
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
    if (-not $expectedResolved.Equals($InstalledGameExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Recorded metadata executable '$expectedResolved' does not match installed Sea Power at '$InstalledGameExecutable'."
    }

    if (-not (Test-ProcessStartTimeMatches `
        $process `
        $RecordedStartTimeUtc `
        $script:WorkerProofProcessStartTimeToleranceSeconds `
        $true)) {
        throw "Recorded game PID $ProcessId start time does not match metadata '$(Format-UtcTimestamp $RecordedStartTimeUtc)'."
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

function Get-WorkerProofLayout([string]$WorkerId, [string]$WorkersRunRoot) {
    $runDir = Join-Path $WorkersRunRoot $WorkerId
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
        GameProcessStartTimeUtc = $metadata.gameProcessStartTimeUtc
        GameExecutable = [string]$metadata.gameExecutable
        UnityLog = [string]$metadata.unityLog
        CampaignStateDirectory = [string]$metadata.campaignStateDirectory
        CampaignId = [string]$metadata.campaignId
        CampaignHttpUrl = [string]$metadata.campaignHttpUrl
        CampaignTokenFile = [string]$metadata.campaignTokenFile
    }
}

function Prove-WorkerRuntime($Layout, [string]$InstalledGameExecutable) {
    $liveGame = Test-LiveSeaPowerProcess `
        $Layout.GameProcessId `
        $Layout.GameExecutable `
        $Layout.UnityLog `
        $Layout.GameProcessStartTimeUtc `
        $InstalledGameExecutable

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
        GameProcessStartTimeUtc = Format-UtcTimestamp $Layout.GameProcessStartTimeUtc
        GameSessionId = [string]$health.sessionId
        SessionGeneration = [long]$health.sessionGeneration
        RuntimeFingerprintSha256 = $fingerprint
        CatalogFingerprintSha256 = $capabilities.runtimeFingerprint.catalogSha256
        CampaignStateDirectory = $Layout.CampaignStateDirectory
    }
}

function Get-CampaignAuthHeaders([string]$CampaignTokenFile) {
    if (-not (Test-Path -LiteralPath $CampaignTokenFile)) {
        throw "Campaign token file not found at $CampaignTokenFile."
    }
    $token = (Get-Content -LiteralPath $CampaignTokenFile -Raw).Trim()
    return @{ Authorization = "Bearer $token" }
}

function Invoke-CampaignApi(
    [string]$Method,
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$Path,
    $Body = $null,
    [int]$TimeoutSec = 30) {
    $uri = "$CampaignHttpUrl$Path"
    if ($null -eq $Body) {
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $Headers -TimeoutSec $TimeoutSec
    }
    $json = $Body | ConvertTo-Json -Depth 8 -Compress
    return Invoke-RestMethod `
        -Method $Method `
        -Uri $uri `
        -Headers $Headers `
        -ContentType 'application/json' `
        -Body $json `
        -TimeoutSec $TimeoutSec
}

function Invoke-CampaignApiRaw(
    [string]$Method,
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$Path,
    $Body = $null,
    [int]$TimeoutSec = 30) {
    $uri = "$CampaignHttpUrl$Path"
    $params = @{
        Method = $Method
        Uri = $uri
        Headers = $Headers
        TimeoutSec = $TimeoutSec
    }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = $Body | ConvertTo-Json -Depth 8 -Compress
    }
    return Invoke-WebRequest @params -UseBasicParsing
}

function Get-CampaignView(
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$CampaignId) {
    return Invoke-CampaignApi `
        Get `
        $CampaignHttpUrl `
        $Headers `
        "/campaigns/$CampaignId" `
        -TimeoutSec 15
}

function Wait-CampaignCondition(
    [string]$CampaignHttpUrl,
    [hashtable]$Headers,
    [string]$CampaignId,
    [scriptblock]$Predicate,
    [string]$Description,
    [int]$TimeoutSeconds,
    [int]$PollMilliseconds = 500) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = ''
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $view = Get-CampaignView $CampaignHttpUrl $Headers $CampaignId
            if (& $Predicate $view) {
                return $view
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    throw "Timed out after ${TimeoutSeconds}s waiting for $Description. $lastError"
}

function Get-CampaignWorkerByClientId($CampaignView, [string]$ClientInstanceId) {
    return @($CampaignView.workers | Where-Object {
        $_.clientInstanceId -eq $ClientInstanceId
    }) | Select-Object -First 1
}

function Format-ScopedAssignmentId([string]$AssignmentId) {
    if ([string]::IsNullOrWhiteSpace($AssignmentId)) { return '' }
    if ($AssignmentId.StartsWith('assignment:', [System.StringComparison]::Ordinal)) {
        return $AssignmentId
    }
    return "assignment:$AssignmentId"
}

function Format-GameEntityId([string]$CanonicalEntityId) {
    return "unit:crimean-tom:$CanonicalEntityId"
}

function Get-PlannedWorkerLoads($CampaignView) {
    $loads = @{}
    foreach ($worker in @($CampaignView.workers)) {
        $clientId = [string]$worker.clientInstanceId
        $assignmentCount = @($CampaignView.assignments | Where-Object {
            $_.lifecycle -ne 'Released' -and $_.workerClientInstanceId -eq $clientId
        } | Select-Object -ExpandProperty entityId -Unique).Count
        $materialized = [int]$worker.ownedMaterializedEntityCount
        $loads[$clientId] = [Math]::Max($materialized, $assignmentCount)
    }
    return $loads
}

function Resolve-ShadowDestinationWorker($CampaignView, $Context, [string]$SourceClientInstanceId) {
    if ($null -eq $Context) {
        return [pscustomobject]@{
            SelectedClientInstanceId = ''
            Candidates = @()
            Ambiguous = $true
            Reason = 'missing-context'
        }
    }

    $islandEntityCount = $Context.EntityIds.Count
    $plannedLoads = Get-PlannedWorkerLoads $CampaignView
    $destinationCapacity = 0
    foreach ($worker in @($CampaignView.workers)) {
        $safeCapacity = [Math]::Max(
            0,
            [int]$worker.tacticalEntityCapacity - [int]$worker.tacticalReserveCapacity)
        if ($safeCapacity -gt $destinationCapacity) {
            $destinationCapacity = $safeCapacity
        }
    }

    $candidates = @()
    foreach ($worker in @($CampaignView.workers)) {
        $clientId = [string]$worker.clientInstanceId
        if ($clientId -ceq $SourceClientInstanceId) { continue }
        if ([string]::IsNullOrWhiteSpace([string]$worker.playerTaskforceId)) { continue }
        if ([int]$worker.materializationProfileCount -lt 1) { continue }
        if ([int]$worker.tacticalEntityCapacity -le 0) { continue }
        if ([int]$worker.ownedRecoveryRequiredEntityCount -gt 0) { continue }
        if ([int]$worker.migrationOverlapHeadroom -lt $islandEntityCount) { continue }

        $candidates += [pscustomobject]@{
            ClientInstanceId = $clientId
            OccupiedTacticalEntityCount = [int]$worker.occupiedTacticalEntityCount
            OwnedRecoveryRequiredEntityCount = [int]$worker.ownedRecoveryRequiredEntityCount
            MigrationOverlapHeadroom = [int]$worker.migrationOverlapHeadroom
            PlannedLoad = [int]$plannedLoads[$clientId]
        }
    }

    if ($candidates.Count -eq 0) {
        return [pscustomobject]@{
            SelectedClientInstanceId = ''
            Candidates = @()
            Ambiguous = $true
            Reason = 'no-eligible-destination'
        }
    }

    $ordered = @($candidates | Sort-Object `
        OccupiedTacticalEntityCount, `
        OwnedRecoveryRequiredEntityCount, `
        ClientInstanceId)
    $best = $ordered[0]
    $ties = @($ordered | Where-Object {
        $_.OccupiedTacticalEntityCount -eq $best.OccupiedTacticalEntityCount `
            -and $_.OwnedRecoveryRequiredEntityCount -eq $best.OwnedRecoveryRequiredEntityCount
    })
    return [pscustomobject]@{
        SelectedClientInstanceId = [string]$best.ClientInstanceId
        Candidates = $ordered
        Ambiguous = ($ties.Count -gt 1)
        Reason = if ($ties.Count -gt 1) { 'tie-break-ambiguous' } else { 'selected' }
    }
}

function Get-WorkerSurfaceResources($Worker) {
    return Invoke-WorkerControl $Worker.ControlUrl $Worker.Layout.TokenFile '/game/mutations/surface/resources'
}

function Test-WorkerHasActiveMaterialization($Worker) {
    try {
        $resources = Get-WorkerSurfaceResources $Worker
    }
    catch {
        return [pscustomobject]@{
            Materialized = $false
            ActiveObjectCount = $null
            Error = $_.Exception.Message
        }
    }
    $activeCount = [int]$resources.activeObjectCount
    return [pscustomobject]@{
        Materialized = ($activeCount -gt 0)
        ActiveObjectCount = $activeCount
        SuspendedRetainedObjectCount = [int]$resources.suspendedRetainedObjectCount
        OwnedRecordCount = [int]$resources.ownedRecordCount
        Error = ''
    }
}

function Test-WorkerAssignmentMaterialization(
    $Worker,
    $Assignment,
    [string]$GameEntityId,
    [string]$ExpectedLifecycleState = 'active') {
    $scopedAssignmentId = Format-ScopedAssignmentId ([string]$Assignment.assignmentId)
    $expectedAuthorityEpoch = [long]$Assignment.authorityEpoch
    try {
        $resources = Get-WorkerSurfaceResources $Worker
    }
    catch {
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $false
            Error = $_.Exception.Message
        }
    }

    if (-not ($resources.PSObject.Properties.Name -contains 'entities')) {
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $false
            Error = 'surface-resources payload is missing entities[]'
        }
    }

    $entities = @($resources.entities)
    $match = @($entities | Where-Object {
        ($_.assignmentId -eq $scopedAssignmentId -or $_.entityId -eq $GameEntityId) -and
        ($_.lifecycleState -eq $ExpectedLifecycleState)
    })
    if ($match.Count -ne 1) {
        $observedEntities = @($entities | ForEach-Object {
            $authorityEpoch = $null
            if ($null -ne $_.authorityEpoch) { $authorityEpoch = [long]$_.authorityEpoch }
            $revision = $null
            if ($null -ne $_.revision) { $revision = [long]$_.revision }
            [ordered]@{
                entityId = [string]$_.entityId
                assignmentId = [string]$_.assignmentId
                lifecycleState = [string]$_.lifecycleState
                authorityEpoch = $authorityEpoch
                revision = $revision
            }
        })
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $true
            Error = "expected exactly one ${ExpectedLifecycleState} surface entity for assignment '$scopedAssignmentId' and game entity '$GameEntityId'"
            ObservedEntities = $observedEntities
        }
    }

    $entry = $match[0]
    $missingSchema = (-not ($entry.PSObject.Properties.Name -contains 'assignmentId')) -or
        (-not ($entry.PSObject.Properties.Name -contains 'entityId')) -or
        (-not ($entry.PSObject.Properties.Name -contains 'lifecycleState')) -or
        (-not ($entry.PSObject.Properties.Name -contains 'authorityEpoch')) -or
        (-not ($entry.PSObject.Properties.Name -contains 'revision'))
    if ($missingSchema) {
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $false
            Error = 'surface-resources entity entry is missing assignmentId, entityId, lifecycleState, authorityEpoch, or revision'
        }
    }

    if ([string]$entry.assignmentId -ne $scopedAssignmentId) {
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $true
            Error = "assignmentId mismatch: expected '$scopedAssignmentId', observed '$($entry.assignmentId)'"
        }
    }
    if ([string]$entry.entityId -ne $GameEntityId) {
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $true
            Error = "entityId mismatch: expected '$GameEntityId', observed '$($entry.entityId)'"
        }
    }
    if ([long]$entry.authorityEpoch -ne $expectedAuthorityEpoch) {
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $true
            Error = "authorityEpoch mismatch: expected $expectedAuthorityEpoch, observed $($entry.authorityEpoch)"
        }
    }
    if ([long]$entry.revision -le 0) {
        return [pscustomobject]@{
            Proven = $false
            SchemaValid = $true
            Error = "revision must be positive; observed $($entry.revision)"
        }
    }

    return [pscustomobject]@{
        Proven = $true
        SchemaValid = $true
        AssignmentId = [string]$entry.assignmentId
        EntityId = [string]$entry.entityId
        LifecycleState = [string]$entry.lifecycleState
        AuthorityEpoch = [long]$entry.authorityEpoch
        Revision = [long]$entry.revision
        ActiveObjectCount = [int]$resources.activeObjectCount
        SuspendedRetainedObjectCount = [int]$resources.suspendedRetainedObjectCount
        OwnedRecordCount = [int]$resources.ownedRecordCount
        Error = ''
    }
}

function Get-LivePrimaryAssignmentsForIsland($CampaignView, [string[]]$EntityIds) {
    $allowed = @(
        'Active',
        'Quiescing',
        'Quiesced'
    )
    return @($CampaignView.assignments | Where-Object {
        $EntityIds -contains $_.entityId `
            -and $_.role -eq 'Primary' `
            -and $allowed -contains $_.lifecycle
    })
}

function Get-LiveShadowAssignmentsForIsland($CampaignView, [string[]]$EntityIds) {
    return @($CampaignView.assignments | Where-Object {
        $EntityIds -contains $_.entityId `
            -and $_.role -eq 'Shadow' `
            -and $_.lifecycle -ne 'Released'
    })
}

function Find-TacticalMigrationIsland($CampaignView, [string]$CanonicalEntityId) {
    $islands = @($CampaignView.scheduler.islands)
    if (-not [string]::IsNullOrWhiteSpace($CanonicalEntityId)) {
        $match = @($islands | Where-Object {
            $_.entityIds -contains $CanonicalEntityId -and $_.currentFidelity -eq 'Tactical'
        })
        if ($match.Count -ne 1) {
            throw "Canonical entity '$CanonicalEntityId' is not the sole member of exactly one Tactical island."
        }
        return [pscustomobject]@{
            Island = $match[0]
            EntityIds = @($match[0].entityIds)
            AnchorEntityId = $CanonicalEntityId
        }
    }

    $candidates = @($islands | Where-Object {
        $_.currentFidelity -eq 'Tactical' `
            -and $_.entityIds.Count -eq 1 `
            -and -not $_.unsafeToSplit `
            -and -not $_.engaged
    })
    if ($candidates.Count -eq 0) {
        return $null
    }
    if ($candidates.Count -gt 1) {
        throw 'Multiple eligible single-entity Tactical islands found. Pass -CanonicalEntityId explicitly.'
    }
    $island = $candidates[0]
    return [pscustomobject]@{
        Island = $island
        EntityIds = @($island.entityIds)
        AnchorEntityId = [string]$island.entityIds[0]
    }
}

function Get-WorkerGameRevision($WorkerLayout, [string]$AssignmentId, [string]$GameEntityId) {
    $resources = Invoke-WorkerControl $WorkerLayout.ControlUrl $WorkerLayout.TokenFile '/game/mutations/surface/resources'
    $entities = @($resources.entities)
    $scopedAssignmentId = Format-ScopedAssignmentId $AssignmentId
    $match = @($entities | Where-Object {
        $_.assignmentId -eq $scopedAssignmentId -or $_.entityId -eq $GameEntityId
    })
    if ($match.Count -ge 1) {
        return [long]$match[0].revision
    }

    $safeKey = "assignment_$AssignmentId"
    $revisionPath = Join-Path $WorkerLayout.CampaignStateDirectory "assignments\$safeKey.revision"
    if (Test-Path -LiteralPath $revisionPath) {
        $text = (Get-Content -LiteralPath $revisionPath -Raw).Trim()
        $parsed = [long]0
        if ([long]::TryParse($text, [ref]$parsed)) {
            return $parsed
        }
    }
    return 0
}

function Prove-NamedWorkerCoexistence(
    [string]$CampaignHttpUrl,
    [string]$CampaignId,
    [string]$CampaignTokenFile,
    [string]$WorkersRunRoot,
    [string[]]$WorkerIds,
    [string]$InstalledGameExecutable) {
    $normalizedWorkerIds = @(Normalize-WorkerIds $WorkerIds)
    if ($normalizedWorkerIds.Count -lt 2) {
        throw 'Proof requires at least two distinct worker IDs.'
    }

    $headers = Get-CampaignAuthHeaders $CampaignTokenFile
    $workerLayouts = @()
    foreach ($workerId in $normalizedWorkerIds) {
        $workerLayouts += Get-WorkerProofLayout $workerId $WorkersRunRoot
    }

    $workerProofs = @()
    foreach ($layout in $workerLayouts) {
        $workerProofs += Prove-WorkerRuntime $layout $InstalledGameExecutable
    }

    $campaign = Get-CampaignView $CampaignHttpUrl $Headers $CampaignId
    if ($campaign.campaignId -ne $CampaignId) {
        throw "Campaign binding mismatch: requested '$CampaignId', service returned '$($campaign.campaignId)'."
    }

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
        if ($campaignWorker.clientInstanceId -in $script:WorkerProofSyntheticClientInstanceIds) {
            throw "Worker '$($proof.WorkerId)' matched a synthetic gRPC client instance."
        }
        if ($campaignWorker.gameSessionId -in $script:WorkerProofSyntheticGameSessionIds) {
            throw "Worker '$($proof.WorkerId)' matched a synthetic game session."
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
            Layout = ($workerLayouts | Where-Object { $_.WorkerId -eq $proof.WorkerId } | Select-Object -First 1)
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
        throw 'Coexistence proof failed: game session IDs are not distinct across workers.'
    }
    if ($distinctClientIds.Count -lt 2) {
        throw 'Coexistence proof failed: campaign client instance IDs are not distinct across workers.'
    }
    if ($distinctCampaignSessions.Count -lt 2) {
        throw 'Coexistence proof failed: campaign session IDs are not distinct across workers.'
    }
    if ($distinctStateDirectories.Count -lt 2) {
        throw 'Coexistence proof failed: campaign companion state directories are not distinct across workers.'
    }

    return [pscustomobject]@{
        CampaignId = $CampaignId
        CampaignHttpUrl = $CampaignHttpUrl
        ConnectedCampaignWorkerCount = $campaignWorkers.Count
        ProvenWorkerCount = $matchedWorkers.Count
        Workers = $matchedWorkers
    }
}
