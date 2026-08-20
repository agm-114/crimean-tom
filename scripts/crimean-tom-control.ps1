[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'health', 'ping', 'capabilities', 'scene', 'world', 'catalog', 'catalog-diagnostics', 'ammunition', 'sensors', 'catalog-files', 'campaign', 'start-port-operations', 'open-task-force-builder', 'add-maintenance-unit', 'start-scenario', 'continue-briefing', 'exit-tactical', 'pause-experimental', 'unpause-experimental', 'run-controlled-ticks-experimental', 'launch-probe-weapon-experimental', 'save-probe-experimental', 'load-probe-experimental', 'arm-owned-mutations', 'dry-run-owned-surface', 'materialize-owned-surface', 'checkpoint-owned-surface', 'update-owned-surface', 'suspend-owned-surface', 'enable-owned-surface', 'restore-owned-surface', 'port-projection', 'materialize-port-projection', 'import-port-draft', 'test')]
    [string]$Action = 'status',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ControlUrl = $(
        if ($env:CRIMEAN_TOM_CONTROL_URL) { $env:CRIMEAN_TOM_CONTROL_URL }
        else { 'http://127.0.0.1:5137' }
    ),

    [string]$RunDirectory,

    [string]$GamePipeName,

    [string]$ScenarioId,

    [string]$ProjectionId = 'port-projection:sevastopol:blue-fleet:1042',

    [long]$ExpectedCampaignRevision = 1042,

    [string]$IdempotencyKey = 'manual-port-draft-import-1042',

    [string]$UnitType = 'usn_ffg_oliver_hazard_perry',

    [string]$VariantReference = 'Variant17',

    [ValidateSet('Missile', 'Torpedo', 'Bomb')]
    [string]$WeaponType = 'Missile',

    [string]$UnitId = '',

    [string]$TargetUnitId = '',

    [ValidateRange(1, 10000)]
    [int]$TickCount = 300,

    [ValidateRange(0.001, 0.1)]
    [double]$TickStepSeconds = (1.0 / 30.0),

    [int]$Seed = 1042,

    [string]$AssignmentId = 'assignment:phase2-surface-probe',

    [string]$EntityId = 'unit:crimean-tom:phase2-knox-1',

    [string]$CheckpointId = '',

    [string]$CheckpointSha256 = '',

    [long]$AuthorityEpoch = 1,

    [long]$ExpectedEntityRevision = 0,

    [string]$MutationIdempotencyKey = 'phase2-materialize-knox-1',

    [string]$TaskforceId = 'taskforce:Taskforce1',

    [double]$Latitude = 0,

    [double]$Longitude = 0,

    [double]$HeadingDegrees = 0,

    [ValidateRange(-1, 5)]
    [int]$Telegraph = 0,

    [ValidateRange(1, 600)]
    [int]$MutationArmSeconds = 300,

    [int]$Quantity = 1,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\CrimeanTom.Companion\CrimeanTom.Companion.csproj'
$companionDll = Join-Path $repoRoot "src\CrimeanTom.Companion\bin\$Configuration\net8.0\CrimeanTom.Companion.dll"
$runDir = if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
    Join-Path $repoRoot '.run'
}
else {
    [System.IO.Path]::GetFullPath($RunDirectory)
}
$pidFile = Join-Path $runDir 'companion.pid'
$stdoutLog = Join-Path $runDir 'companion.stdout.log'
$stderrLog = Join-Path $runDir 'companion.stderr.log'
$tokenFile = Join-Path $runDir 'control.token'

function Get-CompanionProcess {
    if (-not (Test-Path -LiteralPath $pidFile)) { return $null }

    $rawPid = Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue
    $processId = 0
    if (-not [int]::TryParse($rawPid.Trim(), [ref]$processId)) { return $null }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }

    $searcher = New-Object System.Management.ManagementObjectSearcher(
        "SELECT CommandLine FROM Win32_Process WHERE ProcessId = $processId")
    try {
        $processInfo = @($searcher.Get()) | Select-Object -First 1
        $commandLine = if ($null -eq $processInfo) { '' } else { [string]$processInfo.CommandLine }
    }
    finally {
        $searcher.Dispose()
    }

    if ($commandLine.IndexOf(
        $companionDll,
        [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $null
    }

    $process
}

function Clear-StaleCompanionArtifacts {
    foreach ($path in @($pidFile, $tokenFile, $stdoutLog, $stderrLog)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

function Stop-Companion {
    $process = Get-CompanionProcess
    if ($null -ne $process) {
        Write-Host "Stopping Crimean Tom companion (PID $($process.Id))..."
        Stop-Process -Id $process.Id
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $pidFile) {
        Remove-Item -LiteralPath $pidFile -Force
    }
    if (Test-Path -LiteralPath $tokenFile) {
        Remove-Item -LiteralPath $tokenFile -Force
    }
}

function Invoke-ControlRequest([string]$Path, [string]$Method = 'Get', $Body = $null) {
    if (-not (Test-Path -LiteralPath $tokenFile)) {
        throw "Companion control token not found at $tokenFile."
    }

    $token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
    $headers = @{ 'X-Crimean-Tom-Token' = $token }
    if ($null -eq $Body) {
        Invoke-RestMethod -Method $Method -Uri "$ControlUrl$Path" -Headers $headers -TimeoutSec 8
    }
    else {
        Invoke-RestMethod `
            -Method $Method `
            -Uri "$ControlUrl$Path" `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body ($Body | ConvertTo-Json -Compress -Depth 12) `
            -TimeoutSec 8
    }
}

function Wait-ForCompanion {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $null = Invoke-ControlRequest '/health'
            return
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    $errorTail = if (Test-Path -LiteralPath $stderrLog) {
        (Get-Content -LiteralPath $stderrLog -Tail 30) -join [Environment]::NewLine
    }
    else { '' }
    throw "Crimean Tom companion did not become healthy at $ControlUrl. $errorTail"
}

function Start-Companion {
    $running = Get-CompanionProcess
    if ($null -ne $running) {
        Write-Host "Crimean Tom companion is already running (PID $($running.Id))."
        Wait-ForCompanion
        return
    }

    Clear-StaleCompanionArtifacts

    if (-not $SkipBuild) {
        & dotnet build $project --configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Companion build failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $companionDll)) {
        throw "Companion DLL not found at $companionDll. Build it before using -SkipBuild."
    }

    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    $process = $null
    try {
        $companionArguments = @(
            $companionDll,
            '--ControlUrl', $ControlUrl,
            '--ControlTokenFile', $tokenFile
        )
        if (-not [string]::IsNullOrWhiteSpace($GamePipeName)) {
            $companionArguments += @('--GamePipeName', $GamePipeName)
        }

        $process = Start-Process `
            -FilePath 'dotnet' `
            -ArgumentList $companionArguments `
            -RedirectStandardOutput $stdoutLog `
            -RedirectStandardError $stderrLog `
            -WindowStyle Hidden `
            -PassThru

        Set-Content -LiteralPath $pidFile -Value $process.Id -NoNewline
        Wait-ForCompanion
    }
    catch {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
        foreach ($path in @($pidFile, $tokenFile)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force
            }
        }
        throw
    }
    Write-Host "Crimean Tom companion started (PID $($process.Id)) at $ControlUrl."
}

function Show-Json($Value) {
    $Value | ConvertTo-Json -Depth 12
}

function Test-ControlBridge {
    $health = Invoke-ControlRequest '/health'
    if (-not $health.gameConnected) {
        throw 'The companion is healthy, but Sea Power is not connected to its game pipe.'
    }
    if ($health.connectionState -cne 'connected' -or
        [string]::IsNullOrWhiteSpace([string]$health.sessionId) -or
        [long]$health.sessionGeneration -lt 1 -or
        [long]$health.lastConnectionTransitionUnixMilliseconds -le 0) {
        throw 'The companion did not publish a valid connected session state and generation.'
    }

    $ping = Invoke-ControlRequest '/game/ping'
    $capabilities = Invoke-ControlRequest '/game/capabilities'
    $contentDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($capabilities.runtimeFingerprint.contentIdentityState -ceq 'pending' -and
        [DateTime]::UtcNow -lt $contentDeadline) {
        Start-Sleep -Milliseconds 100
        $capabilities = Invoke-ControlRequest '/game/capabilities'
    }
    $scene = Invoke-ControlRequest '/game/scene'
    $world = Invoke-ControlRequest '/game/world'
    $catalog = Invoke-ControlRequest '/game/catalog/units?offset=0&limit=5'
    $catalogDiagnostics = Invoke-ControlRequest '/game/catalog/diagnostics'
    $ammunitionCatalog = Invoke-ControlRequest '/game/catalog/references?kind=ammunition&offset=0&limit=5'
    $sensorCatalog = Invoke-ControlRequest '/game/catalog/references?kind=sensors&offset=0&limit=5'
    $metadataDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while (($catalog.state -cne 'ready' -or
            $ammunitionCatalog.state -cne 'ready' -or
            $sensorCatalog.state -cne 'ready') -and
        [DateTime]::UtcNow -lt $metadataDeadline) {
        Start-Sleep -Milliseconds 100
        $catalog = Invoke-ControlRequest '/game/catalog/units?offset=0&limit=5'
        $ammunitionCatalog = Invoke-ControlRequest '/game/catalog/references?kind=ammunition&offset=0&limit=5'
        $sensorCatalog = Invoke-ControlRequest '/game/catalog/references?kind=sensors&offset=0&limit=5'
    }
    $campaign = Invoke-ControlRequest '/game/campaign'
    $capabilitiesRepeat = Invoke-ControlRequest '/game/capabilities'

    if ($capabilities.mutatingCommandsEnabled) {
        throw 'The initial read-only bridge unexpectedly reports mutating commands enabled.'
    }
    if ($capabilities.supportedCommands.Count -lt 8) {
        throw 'The game did not report the expected read and UI-automation command set.'
    }
    $requiredUiCommands = @(
        'open-task-force-builder',
        'start-port-operations',
        'add-maintenance-draft-unit',
        'start-scenario',
        'continue-mission-briefing',
        'exit-tactical-session'
    )
    if (-not $capabilities.uiAutomationEnabled -or
        @(Compare-Object $requiredUiCommands @($capabilities.supportedUiAutomationCommands)).Count -ne 0) {
        throw 'The game did not report the complete fingerprint-verified typed UI action set.'
    }
    if (-not $capabilities.commandCancellationSupported) {
        throw 'The game did not negotiate transmitted command cancellation.'
    }
    if ($capabilities.experimentalRuntimeMode -cne 'ControlledExperimental' -or
        @($capabilities.supportedExperimentalCommands) -cnotcontains
            'set-experimental-pause-state') {
        throw 'The pinned logical-time control is not exposed in ControlledExperimental mode.'
    }
    if ($capabilities.executionThreadGuardSelfTest -cne 'passed') {
        throw "The execution-thread guard self-test is '$($capabilities.executionThreadGuardSelfTest)', not 'passed'."
    }
    $expectedPriorities = @(-100, 0, 100)
    $reportedPriorities = @($capabilities.supportedPriorities)
    if ($reportedPriorities.Count -ne $expectedPriorities.Count -or
        @(Compare-Object $expectedPriorities $reportedPriorities).Count -ne 0) {
        throw 'The game did not negotiate the expected bulk, normal, and control priorities.'
    }

    $fingerprint = $capabilities.runtimeFingerprint
    if ($null -eq $fingerprint) {
        throw 'The game did not report a runtime capability fingerprint.'
    }
    if ($fingerprint.fileIdentityState -ne 'ready') {
        throw "Runtime file identity is '$($fingerprint.fileIdentityState)', not 'ready'."
    }
    if ($fingerprint.contentIdentityState -ne 'ready') {
        throw "Runtime content identity is '$($fingerprint.contentIdentityState)', not 'ready': $($fingerprint.contentIdentityError)"
    }
    if ($fingerprint.fingerprintSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'The runtime capability fingerprint is not a lowercase SHA-256 value.'
    }
    if ($fingerprint.gameExecutableSha256 -notmatch '^[0-9a-f]{64}$' -or
        $fingerprint.pluginAssemblySha256 -notmatch '^[0-9a-f]{64}$' -or
        $fingerprint.catalogSha256 -notmatch '^[0-9a-f]{64}$' -or
        $fingerprint.modSetSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'An executable, plugin, catalog, or mod-set identity is not a lowercase SHA-256 value.'
    }
    if ([int]$fingerprint.catalogFileCount -le 0 -or
        @($fingerprint.activeContentSources).Count -lt 2) {
        throw 'The winning catalog manifest or active content-source report is empty.'
    }
    $loadAcceleration = $fingerprint.loadAcceleration
    if ($null -eq $loadAcceleration -or
        $loadAcceleration.fileHashCacheState -cne 'ready' -or
        [int]$loadAcceleration.fileHashCacheEntryCount -le 0 -or
        ([int]$loadAcceleration.fileHashCacheHitCount +
            [int]$loadAcceleration.fileHashCacheMissCount) -le 0 -or
        -not $loadAcceleration.failOpen) {
        throw 'The fail-open load-acceleration cache is unavailable or has no measured work.'
    }

    $requiredHookNames = @(
        'logical-time',
        'random-sources',
        'stable-update-order',
        'native-save-load',
        'weapon-probe-launch'
    )
    $reportedHooks = @($fingerprint.deterministicHooks)
    if ($reportedHooks.Count -ne $requiredHookNames.Count) {
        throw "Expected $($requiredHookNames.Count) deterministic hook inventories, got $($reportedHooks.Count)."
    }
    foreach ($requiredHookName in $requiredHookNames) {
        $reportedHook = @($reportedHooks | Where-Object name -CEQ $requiredHookName)
        $expectedHookStatus = if ($requiredHookName -cin @(
                'logical-time',
                'stable-update-order',
                'native-save-load',
                'weapon-probe-launch')) {
            'verified'
        }
        else {
            'inventory-observed'
        }
        if ($reportedHook.Count -ne 1 -or
            $reportedHook[0].status -cne $expectedHookStatus -or
            $reportedHook[0].methodFingerprintSha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Deterministic hook '$requiredHookName' is missing, duplicated, not inventory-observed, or has an invalid hash."
        }
    }
    $uiAutomationSeams = @($fingerprint.uiAutomationSeams)
    if ($uiAutomationSeams.Count -ne 6) {
        throw "Expected 6 UI automation seam fingerprints, got $($uiAutomationSeams.Count)."
    }
    foreach ($uiAutomationSeam in $uiAutomationSeams) {
        if ($uiAutomationSeam.status -cne 'verified' -or
            $uiAutomationSeam.methodFingerprintSha256 -notmatch '^[0-9a-f]{64}$' -or
            $uiAutomationSeam.methodFingerprintSha256 -cne
                $uiAutomationSeam.expectedFingerprintSha256) {
            throw "UI automation seam '$($uiAutomationSeam.name)' is not pinned and verified."
        }
    }

    $requiredManagedAssemblies = @(
        'Seapower-Scripts.dll',
        'Unity.Burst.dll',
        'Unity.Collections.dll',
        'Unity.Entities.dll',
        'Unity.Mathematics.dll',
        'UnityEngine.CoreModule.dll',
        'UnityEngine.PhysicsModule.dll',
        'UnityEngine.TerrainPhysicsModule.dll'
    )
    $reportedManagedAssemblies = @($fingerprint.managedAssemblies)
    if ($reportedManagedAssemblies.Count -ne $requiredManagedAssemblies.Count) {
        throw "Expected $($requiredManagedAssemblies.Count) managed assembly hashes, got $($reportedManagedAssemblies.Count)."
    }
    foreach ($requiredAssembly in $requiredManagedAssemblies) {
        $reportedAssembly = @($reportedManagedAssemblies | Where-Object name -CEQ $requiredAssembly)
        if ($reportedAssembly.Count -ne 1 -or $reportedAssembly[0].sha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Managed assembly '$requiredAssembly' is missing, duplicated, or has an invalid hash."
        }
    }

    if ($fingerprint.fingerprintSha256 -cne
        $capabilitiesRepeat.runtimeFingerprint.fingerprintSha256) {
        throw 'Two consecutive capability captures produced different compatibility fingerprints.'
    }
    if ($fingerprint.authorityEligible) {
        throw 'The read-only P0 runtime unexpectedly reports simulation-authority eligibility.'
    }
    if (@($fingerprint.authorityBlockers) -cnotcontains 'mutating-commands-disabled') {
        throw 'The authority blockers do not explain that mutating commands are disabled.'
    }
    if ($catalog.state -cne 'ready' -or
        $catalog.catalogSha256 -cne $fingerprint.catalogSha256 -or
        [int]$catalog.totalRecords -le 0 -or
        @($catalog.records).Count -ne 5) {
        throw "The normalized runtime unit catalog page failed validation (state=$($catalog.state), total=$($catalog.totalRecords), records=$(@($catalog.records).Count), limit=$($catalog.limit), hashMatch=$($catalog.catalogSha256 -ceq $fingerprint.catalogSha256))."
    }
    foreach ($catalogRecord in @($catalog.records)) {
        if ($catalogRecord.id -notmatch '^unit:[^/]+/.+$' -or
            [string]::IsNullOrWhiteSpace([string]$catalogRecord.sourceId) -or
            $catalogRecord.sourceFileSha256 -notmatch '^[0-9a-f]{64}$') {
            throw 'A unit catalog record lacks a normalized ID or winning-file provenance.'
        }
    }
    foreach ($referenceCatalog in @($ammunitionCatalog, $sensorCatalog)) {
        if ($referenceCatalog.state -cne 'ready' -or
            $referenceCatalog.catalogSha256 -cne $fingerprint.catalogSha256 -or
            [int]$referenceCatalog.totalRecords -le 0 -or
            @($referenceCatalog.records).Count -ne 5) {
            throw "The '$($referenceCatalog.kind)' runtime reference catalog is unavailable, empty, or not tied to the active catalog fingerprint."
        }
        foreach ($referenceRecord in @($referenceCatalog.records)) {
            if ([string]::IsNullOrWhiteSpace([string]$referenceRecord.id) -or
                [string]::IsNullOrWhiteSpace([string]$referenceRecord.sourceId) -or
                $referenceRecord.sourceFileSha256 -notmatch '^[0-9a-f]{64}$') {
                throw "A '$($referenceCatalog.kind)' catalog record lacks normalized identity or winning-file provenance."
            }
        }
    }
    if ($catalogDiagnostics.state -cne 'ready' -or
        $catalogDiagnostics.catalogSha256 -cne $fingerprint.catalogSha256 -or
        [int]$catalogDiagnostics.winningFileCount -ne [int]$fingerprint.catalogFileCount -or
        [int]$catalogDiagnostics.overrideCount -ne
            ([int]$catalogDiagnostics.identicalOverrideCount +
                [int]$catalogDiagnostics.conflictingOverrideCount)) {
        throw 'The catalog override/conflict diagnostics are unavailable or internally inconsistent.'
    }
    foreach ($catalogOverride in @($catalogDiagnostics.overrides)) {
        if ([string]::IsNullOrWhiteSpace([string]$catalogOverride.virtualPath) -or
            $catalogOverride.winningSha256 -notmatch '^[0-9a-f]{64}$' -or
            $catalogOverride.shadowedSha256 -notmatch '^[0-9a-f]{64}$' -or
            $catalogOverride.classification -cnotin @('identical-override', 'conflicting-override')) {
            throw 'A catalog override diagnostic is incomplete or unclassified.'
        }
    }
    if ([int]$world.schemaVersion -lt 3 -or
        [long]$world.mainThreadCaptureDurationMicroseconds -lt 0) {
        throw 'The canonical world read lacks its extraction-cost instrumentation.'
    }
    if (-not $ping.isMainThread -or -not $scene.isMainThread) {
        throw 'A command did not execute on Unity''s initialized main thread.'
    }
    if ($ping.executingThreadId -ne $ping.initializedMainThreadId -or
        $scene.executingThreadId -ne $scene.initializedMainThreadId -or
        $ping.executingThreadId -ne $scene.executingThreadId) {
        throw 'The reported execution thread does not match the initialized Unity main thread.'
    }

    Show-Json ([pscustomobject]@{
        Health = $health
        Ping = $ping
        Capabilities = $capabilities
        Scene = $scene
        World = $world
        Catalog = $catalog
        CatalogDiagnostics = $catalogDiagnostics
        AmmunitionCatalog = $ammunitionCatalog
        SensorCatalog = $sensorCatalog
        Campaign = $campaign
    })
}

switch ($Action) {
    'start' { Start-Companion }
    'stop' { Stop-Companion }
    'status' {
        $process = Get-CompanionProcess
        if ($null -eq $process) {
            [pscustomobject]@{ Running = $false; ControlUrl = $ControlUrl } | Format-List
        }
        else {
            $health = try { Invoke-ControlRequest '/health' } catch { $null }
            [pscustomobject]@{
                Running = $true
                ProcessId = $process.Id
                ControlUrl = $ControlUrl
                Healthy = $null -ne $health
                GameConnected = $null -ne $health -and $health.gameConnected
            } | Format-List
        }
    }
    'health' { Show-Json (Invoke-ControlRequest '/health') }
    'ping' { Show-Json (Invoke-ControlRequest '/game/ping') }
    'capabilities' { Show-Json (Invoke-ControlRequest '/game/capabilities') }
    'scene' { Show-Json (Invoke-ControlRequest '/game/scene') }
    'world' { Show-Json (Invoke-ControlRequest '/game/world') }
    'catalog' { Show-Json (Invoke-ControlRequest '/game/catalog/units?offset=0&limit=25') }
    'catalog-diagnostics' { Show-Json (Invoke-ControlRequest '/game/catalog/diagnostics') }
    'ammunition' { Show-Json (Invoke-ControlRequest '/game/catalog/references?kind=ammunition&offset=0&limit=25') }
    'sensors' { Show-Json (Invoke-ControlRequest '/game/catalog/references?kind=sensors&offset=0&limit=25') }
    'catalog-files' { Show-Json (Invoke-ControlRequest '/game/catalog/references?kind=files&offset=0&limit=25') }
    'campaign' { Show-Json (Invoke-ControlRequest '/game/campaign') }
    'start-port-operations' {
        $result = $null
        $lastError = $null
        for ($attempt = 1; $attempt -le 100 -and $null -eq $result; $attempt++) {
            try {
                $result = Invoke-ControlRequest '/game/automation/port-operations/start' 'Post'
            }
            catch {
                $lastError = $_
                Start-Sleep -Milliseconds 100
            }
        }
        if ($null -eq $result) {
            throw $lastError
        }
        Show-Json $result
    }
    'open-task-force-builder' {
        Show-Json (Invoke-ControlRequest '/game/automation/task-force-builder' 'Post')
    }
    'add-maintenance-unit' {
        Show-Json (Invoke-ControlRequest `
            '/game/automation/port-operations/draft/units' `
            'Post' `
            ([pscustomobject]@{
                unitType = $UnitType
                variantReference = $VariantReference
                quantity = $Quantity
            }))
    }
    'start-scenario' {
        if ([string]::IsNullOrWhiteSpace($ScenarioId)) {
            throw '-ScenarioId is required for start-scenario.'
        }
        $result = $null
        $lastError = $null
        for ($attempt = 1; $attempt -le 100 -and $null -eq $result; $attempt++) {
            try {
                $result = Invoke-ControlRequest `
                    '/game/automation/scenario/start' `
                    'Post' `
                    ([pscustomobject]@{ scenarioId = $ScenarioId })
            }
            catch {
                $lastError = $_
                Start-Sleep -Milliseconds 100
            }
        }
        if ($null -eq $result) {
            throw $lastError
        }
        Show-Json $result
    }
    'continue-briefing' {
        Show-Json (Invoke-ControlRequest '/game/automation/briefing/continue' 'Post')
    }
    'exit-tactical' {
        Show-Json (Invoke-ControlRequest '/game/automation/tactical/exit' 'Post')
    }
    'pause-experimental' {
        Show-Json (Invoke-ControlRequest `
            '/game/experimental/pause' `
            'Post' `
            ([pscustomobject]@{ isPaused = $true }))
    }
    'unpause-experimental' {
        Show-Json (Invoke-ControlRequest `
            '/game/experimental/pause' `
            'Post' `
            ([pscustomobject]@{ isPaused = $false }))
    }
    'run-controlled-ticks-experimental' {
        Show-Json (Invoke-ControlRequest `
            '/game/experimental/ticks/run' `
            'Post' `
            ([pscustomobject]@{
                tickCount = $TickCount
                stepSeconds = $TickStepSeconds
                seed = $Seed
            }))
    }
    'save-probe-experimental' {
        Show-Json (Invoke-ControlRequest '/game/experimental/save-load/save' 'Post')
    }
    'launch-probe-weapon-experimental' {
        Show-Json (Invoke-ControlRequest `
            '/game/experimental/save-load/launch' `
            'Post' `
            ([pscustomobject]@{
                weaponType = $WeaponType
                unitId = $UnitId
                targetUnitId = $TargetUnitId
            }))
    }
    'load-probe-experimental' {
        Show-Json (Invoke-ControlRequest '/game/experimental/save-load/load' 'Post')
    }
    'arm-owned-mutations' {
        $health = Invoke-ControlRequest '/health'
        $capabilities = Invoke-ControlRequest '/game/capabilities'
        Show-Json (Invoke-ControlRequest `
            '/game/mutations/arm' `
            'Post' `
            ([pscustomobject]@{
                sessionId = $health.sessionId
                runtimeFingerprintSha256 = $capabilities.runtimeFingerprint.fingerprintSha256
                confirmation = 'enable-owned-mutations'
                durationSeconds = $MutationArmSeconds
            }))
    }
    { $_ -cin @('dry-run-owned-surface', 'materialize-owned-surface') } {
        $health = Invoke-ControlRequest '/health'
        Show-Json (Invoke-ControlRequest `
            '/game/mutations/surface/materialize' `
            'Post' `
            ([pscustomobject]@{
                context = [pscustomobject]@{
                    sessionId = $health.sessionId
                    assignmentId = $AssignmentId
                    authorityEpoch = $AuthorityEpoch
                    expectedRevision = $ExpectedEntityRevision
                    idempotencyKey = $MutationIdempotencyKey
                    dryRun = ($Action -ceq 'dry-run-owned-surface')
                }
                entityId = $EntityId
                unitTypeId = 'usn_ff_knox_84'
                variantId = 'Variant13'
                loadoutId = 'Default'
                taskforceId = $TaskforceId
                latitude = $Latitude
                longitude = $Longitude
                headingDegrees = $HeadingDegrees
                telegraph = $Telegraph
            }))
    }
    'checkpoint-owned-surface' {
        $health = Invoke-ControlRequest '/health'
        Show-Json (Invoke-ControlRequest `
            '/game/mutations/surface/checkpoint' `
            'Post' `
            ([pscustomobject]@{
                context = [pscustomobject]@{
                    sessionId = $health.sessionId
                    assignmentId = $AssignmentId
                    authorityEpoch = $AuthorityEpoch
                    expectedRevision = $ExpectedEntityRevision
                    idempotencyKey = $MutationIdempotencyKey
                    dryRun = $false
                }
                entityId = $EntityId
            }))
    }
    'update-owned-surface' {
        $health = Invoke-ControlRequest '/health'
        Show-Json (Invoke-ControlRequest `
            '/game/mutations/surface/update' `
            'Post' `
            ([pscustomobject]@{
                context = [pscustomobject]@{
                    sessionId = $health.sessionId
                    assignmentId = $AssignmentId
                    authorityEpoch = $AuthorityEpoch
                    expectedRevision = $ExpectedEntityRevision
                    idempotencyKey = $MutationIdempotencyKey
                    dryRun = $false
                }
                entityId = $EntityId
                latitude = $Latitude
                longitude = $Longitude
                headingDegrees = $HeadingDegrees
                telegraph = $Telegraph
            }))
    }
    { $_ -cin @('suspend-owned-surface', 'enable-owned-surface') } {
        $health = Invoke-ControlRequest '/health'
        Show-Json (Invoke-ControlRequest `
            '/game/mutations/surface/suspension' `
            'Post' `
            ([pscustomobject]@{
                context = [pscustomobject]@{
                    sessionId = $health.sessionId
                    assignmentId = $AssignmentId
                    authorityEpoch = $AuthorityEpoch
                    expectedRevision = $ExpectedEntityRevision
                    idempotencyKey = $MutationIdempotencyKey
                    dryRun = $false
                }
                entityId = $EntityId
                isSuspended = ($Action -ceq 'suspend-owned-surface')
            }))
    }
    'restore-owned-surface' {
        $health = Invoke-ControlRequest '/health'
        Show-Json (Invoke-ControlRequest `
            '/game/mutations/surface/restore' `
            'Post' `
            ([pscustomobject]@{
                context = [pscustomobject]@{
                    sessionId = $health.sessionId
                    assignmentId = $AssignmentId
                    authorityEpoch = $AuthorityEpoch
                    expectedRevision = $ExpectedEntityRevision
                    idempotencyKey = $MutationIdempotencyKey
                    dryRun = $false
                }
                entityId = $EntityId
                checkpointId = $CheckpointId
                checkpointSha256 = $CheckpointSha256
            }))
    }
    'port-projection' {
        Show-Json (Invoke-ControlRequest '/port-operations/projection')
    }
    'materialize-port-projection' {
        Show-Json (Invoke-ControlRequest '/port-operations/projection/materialize' 'Post')
    }
    'import-port-draft' {
        Show-Json (Invoke-ControlRequest `
            '/port-operations/drafts/import' `
            'Post' `
            ([pscustomobject]@{
                projectionId = $ProjectionId
                expectedCampaignRevision = $ExpectedCampaignRevision
                idempotencyKey = $IdempotencyKey
            }))
    }
    'test' { Test-ControlBridge }
}
