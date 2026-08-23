[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('capture', 'compare')]
    [string]$Action = 'capture',

    [string]$Label = 'sample',

    [string]$Workload = 'unspecified',

    [string]$PairingKey = '',

    [ValidateRange(6, 600)]
    [int]$DurationSeconds = 30,

    [ValidateRange(1, 30)]
    [int]$SampleIntervalSeconds = 2,

    [ValidateRange(0, 100000)]
    [int]$MinimumObservedObjectCount = 0,

    [string]$ControlUrl = 'http://127.0.0.1:5137',

    [string]$ControlTokenPath = '.run/control.token',

    [string]$CampaignUrl = 'http://127.0.0.1:5081',

    [string]$CampaignTokenPath = '.run/campaign-service/service.token',

    [string]$CampaignId = '',

    [string]$BaselinePath = '',

    [string]$CandidatePath = '',

    [string]$OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Token([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Token file not found at '$Path'."
    }
    (Get-Content -LiteralPath $Path -Raw).Trim()
}

function Invoke-JsonGet([string]$Uri, [hashtable]$Headers) {
    Invoke-RestMethod -Method Get -Uri $Uri -Headers $Headers -TimeoutSec 8
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return $null }
    $ordered = @($Values | Sort-Object)
    $index = [Math]::Min(
        $ordered.Count - 1,
        [Math]::Max(0, [Math]::Ceiling($Percentile * $ordered.Count) - 1))
    [double]$ordered[$index]
}

function Get-Average([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    [double](($Values | Measure-Object -Average).Average)
}

function Get-ProcessSnapshot([int]$ProcessId) {
    if ($ProcessId -le 0) { return $null }
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    $process.Refresh()
    [pscustomobject]@{
        processId = $process.Id
        processName = $process.ProcessName
        cpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
        workingSetBytes = $process.WorkingSet64
        privateMemoryBytes = $process.PrivateMemorySize64
    }
}

function Find-ProcessId([string]$ProcessName) {
    $matches = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) { return 0 }
    if ($matches.Count -ne 1) {
        throw "Expected exactly one '$ProcessName' process, found $($matches.Count)."
    }
    [int]$matches[0].Id
}

function Summarize-Process($Before, $After, [double]$WallSeconds) {
    if ($null -eq $Before -or $null -eq $After -or $WallSeconds -le 0) {
        return $null
    }
    [pscustomobject]@{
        processId = $After.processId
        processName = $After.processName
        normalizedCpuPercent = [Math]::Round(
            100d * ($After.cpuMilliseconds - $Before.cpuMilliseconds) /
                ($WallSeconds * 1000d * [Environment]::ProcessorCount),
            3)
        workingSetMiB = [Math]::Round($After.workingSetBytes / 1MB, 3)
        privateMemoryMiB = [Math]::Round($After.privateMemoryBytes / 1MB, 3)
    }
}

function Metric-Delta([string]$Name, $Baseline, $Candidate, [string]$Better) {
    $baselineValue = if ($null -eq $Baseline) { $null } else { [double]$Baseline }
    $candidateValue = if ($null -eq $Candidate) { $null } else { [double]$Candidate }
    $delta = if ($null -eq $baselineValue -or $null -eq $candidateValue) {
        $null
    }
    else {
        $candidateValue - $baselineValue
    }
    $deltaPercent = if ($null -eq $delta -or [Math]::Abs($baselineValue) -lt 0.0000001d) {
        $null
    }
    else {
        100d * $delta / $baselineValue
    }
    [pscustomobject]@{
        name = $Name
        better = $Better
        baseline = $baselineValue
        candidate = $candidateValue
        delta = if ($null -eq $delta) { $null } else { [Math]::Round($delta, 4) }
        deltaPercent = if ($null -eq $deltaPercent) { $null } else { [Math]::Round($deltaPercent, 3) }
    }
}

function Resolve-OutputPath([string]$Requested, [string]$FileName) {
    $resolved = if ([string]::IsNullOrWhiteSpace($Requested)) {
        Join-Path '.run/performance' $FileName
    }
    else {
        $Requested
    }
    $fullPath = [IO.Path]::GetFullPath($resolved)
    $parent = Split-Path -Parent $fullPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $fullPath
}

if ($Action -eq 'capture') {
    $controlToken = Read-Token $ControlTokenPath
    $controlHeaders = @{ 'X-Crimean-Tom-Token' = $controlToken }
    $health = Invoke-JsonGet "$ControlUrl/health" $controlHeaders
    if (-not $health.gameConnected) {
        throw 'Sea Power is not connected to the companion.'
    }

    $campaignHeaders = $null
    if (-not [string]::IsNullOrWhiteSpace($CampaignId)) {
        if ($CampaignId -notmatch '^[0-9a-fA-F]{32}$') {
            throw 'CampaignId must use canonical 32-character GUID form.'
        }
        $campaignToken = Read-Token $CampaignTokenPath
        $campaignHeaders = @{ Authorization = "Bearer $campaignToken" }
    }

    $seaPowerProcesses = @(Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue)
    if ($seaPowerProcesses.Count -ne 1) {
        throw "Expected exactly one Sea Power process, found $($seaPowerProcesses.Count)."
    }
    $seaPowerProcessId = [int]$seaPowerProcesses[0].Id
    $companionProcessId = if (Test-Path -LiteralPath '.run/companion.pid') {
        [int](Get-Content -LiteralPath '.run/companion.pid' -Raw).Trim()
    }
    else { 0 }
    $serviceProcessId = if ($null -eq $campaignHeaders) {
        0
    }
    else {
        Find-ProcessId 'CrimeanTom.Campaign.ServiceHost'
    }

    # Keep endpoint cold-start and metadata initialization outside the timed
    # process/sample window. Both sides still warm the same runtime endpoints;
    # the candidate additionally warms its campaign view.
    $warmWorld = Invoke-JsonGet "$ControlUrl/game/world" $controlHeaders
    $null = Invoke-JsonGet "$ControlUrl/game/ui" $controlHeaders
    $null = Invoke-JsonGet "$ControlUrl/game/render-proxies" $controlHeaders
    if ($null -ne $campaignHeaders) {
        $null = Invoke-JsonGet "$CampaignUrl/campaigns/$CampaignId" $campaignHeaders
    }

    $processBefore = [pscustomobject]@{
        seaPower = Get-ProcessSnapshot $seaPowerProcessId
        companion = Get-ProcessSnapshot $companionProcessId
        campaignService = Get-ProcessSnapshot $serviceProcessId
    }
    $samples = [Collections.Generic.List[object]]::new()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $scheduledSampleCount = [int][Math]::Ceiling($DurationSeconds / [double]$SampleIntervalSeconds)
    for ($sampleIndex = 0; $sampleIndex -lt $scheduledSampleCount; $sampleIndex++) {
        $scheduledSeconds = $sampleIndex * $SampleIntervalSeconds
        $delayMilliseconds = 1000d * ($scheduledSeconds - $watch.Elapsed.TotalSeconds)
        if ($delayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds ([int][Math]::Ceiling($delayMilliseconds))
        }
        $world = Invoke-JsonGet "$ControlUrl/game/world" $controlHeaders
        $ui = Invoke-JsonGet "$ControlUrl/game/ui" $controlHeaders
        $proxies = Invoke-JsonGet "$ControlUrl/game/render-proxies" $controlHeaders
        $campaign = if ($null -eq $campaignHeaders) {
            $null
        }
        else {
            Invoke-JsonGet "$CampaignUrl/campaigns/$CampaignId" $campaignHeaders
        }
        $samples.Add([pscustomobject]@{
            elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
            framesPerSecond = [double]$world.measuredFramesPerSecond
            mainThreadCaptureMicroseconds = [double]$world.mainThreadCaptureDurationMicroseconds
            observedObjectCount = [int]$world.observedObjectCount
            activeWeaponCount = [int]$world.spatialDiagnostics.activeWeaponCount
            tacticalSimulationSeconds = [double]$ui.simulationTimeSeconds
            tacticalPaused = [bool]$ui.isPaused
            tacticalCompression = [double]$ui.timeCompression
            proxyRecordCount = @($proxies.proxies).Count
            visibleProxyCount = @($proxies.proxies | Where-Object isVisible).Count
            campaignTimeQuanta = if ($null -eq $campaign) { $null } else { [long]$campaign.timeQuanta }
            campaignCommitPosition = if ($null -eq $campaign) { $null } else { [long]$campaign.commitPosition }
            campaignEntityCount = if ($null -eq $campaign) { $null } else { @($campaign.entities).Count }
            campaignAdvanceMicroseconds = if ($null -eq $campaign) { $null } else { [long]$campaign.performance.advance.lastDurationMicroseconds }
            campaignAdvanceWorkUnitsPerSecond = if ($null -eq $campaign) { $null } else { [double]$campaign.performance.advance.lastWorkUnitsPerSecondMilliunits / 1000d }
        })
    }
    $remainingDurationMilliseconds = 1000d * ($DurationSeconds - $watch.Elapsed.TotalSeconds)
    if ($remainingDurationMilliseconds -gt 0) {
        Start-Sleep -Milliseconds ([int][Math]::Ceiling($remainingDurationMilliseconds))
    }
    $watch.Stop()
    $processAfter = [pscustomobject]@{
        seaPower = Get-ProcessSnapshot $seaPowerProcessId
        companion = Get-ProcessSnapshot $companionProcessId
        campaignService = Get-ProcessSnapshot $serviceProcessId
    }

    $first = $samples[0]
    $last = $samples[$samples.Count - 1]
    $fpsValues = [double[]]@($samples | ForEach-Object framesPerSecond)
    $captureValues = [double[]]@($samples | ForEach-Object mainThreadCaptureMicroseconds)
    $objectValues = [double[]]@($samples | ForEach-Object observedObjectCount)
    $proxyValues = [double[]]@($samples | ForEach-Object visibleProxyCount)
    $sampleSpan = [Math]::Max(0.0001d, $last.elapsedSeconds - $first.elapsedSeconds)
    $result = [ordered]@{
        schemaVersion = 1
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        label = $Label
        workload = $Workload
        pairingKey = $PairingKey
        campaignId = $CampaignId
        scenarioId = [string]$warmWorld.scenarioId
        durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        sampleIntervalSeconds = $SampleIntervalSeconds
        sampleCount = $samples.Count
        gameSessionId = [string]$health.sessionId
        seaPowerBuild = [string]$health.game.gameVersion
        summary = [ordered]@{
            averageFramesPerSecond = [Math]::Round((Get-Average $fpsValues), 3)
            minimumFramesPerSecond = [Math]::Round((Get-Percentile $fpsValues 0.0), 3)
            p95MainThreadCaptureMicroseconds = [Math]::Round((Get-Percentile $captureValues 0.95), 3)
            averageObservedObjectCount = [Math]::Round((Get-Average $objectValues), 3)
            averageVisibleProxyCount = [Math]::Round((Get-Average $proxyValues), 3)
            tacticalSimulationSecondsPerWallSecond = [Math]::Round(
                ($last.tacticalSimulationSeconds - $first.tacticalSimulationSeconds) / $sampleSpan,
                4)
            campaignQuantaPerWallSecond = if ($null -eq $first.campaignTimeQuanta) { $null } else {
                [Math]::Round(($last.campaignTimeQuanta - $first.campaignTimeQuanta) / $sampleSpan, 4)
            }
            campaignCommitsPerWallSecond = if ($null -eq $first.campaignCommitPosition) { $null } else {
                [Math]::Round(($last.campaignCommitPosition - $first.campaignCommitPosition) / $sampleSpan, 4)
            }
            campaignEntityCount = $last.campaignEntityCount
            campaignAdvanceWorkUnitsPerSecond = $last.campaignAdvanceWorkUnitsPerSecond
            anyPausedSample = @($samples | Where-Object tacticalPaused).Count -gt 0
        }
        processes = [ordered]@{
            seaPower = Summarize-Process $processBefore.seaPower $processAfter.seaPower $watch.Elapsed.TotalSeconds
            companion = Summarize-Process $processBefore.companion $processAfter.companion $watch.Elapsed.TotalSeconds
            campaignService = Summarize-Process $processBefore.campaignService $processAfter.campaignService $watch.Elapsed.TotalSeconds
        }
        samples = $samples
    }
    if ($MinimumObservedObjectCount -gt 0 -and
        [double]$result.summary.averageObservedObjectCount -lt $MinimumObservedObjectCount) {
        throw "Observed object count $($result.summary.averageObservedObjectCount) is below required minimum $MinimumObservedObjectCount."
    }
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $safeLabel = $Label -replace '[^A-Za-z0-9_.-]', '-'
    $fullOutputPath = Resolve-OutputPath $OutputPath "$stamp-$safeLabel.json"
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $fullOutputPath -Encoding utf8
    [pscustomobject]@{ outputPath = $fullOutputPath; summary = $result.summary; processes = $result.processes } |
        ConvertTo-Json -Depth 8
    exit 0
}

if ([string]::IsNullOrWhiteSpace($BaselinePath) -or
    [string]::IsNullOrWhiteSpace($CandidatePath)) {
    throw 'Compare requires both BaselinePath and CandidatePath.'
}
$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
$candidate = Get-Content -LiteralPath $CandidatePath -Raw | ConvertFrom-Json
$sameScenario = -not [string]::IsNullOrWhiteSpace([string]$baseline.scenarioId) -and
    [string]$baseline.scenarioId -ceq [string]$candidate.scenarioId
$sameExplicitPair = -not [string]::IsNullOrWhiteSpace([string]$baseline.pairingKey) -and
    [string]$baseline.pairingKey -ceq [string]$candidate.pairingKey
if (-not $sameScenario -and -not $sameExplicitPair) {
    throw 'Performance comparison requires the same scenario or an explicit matching pairing key.'
}
if ([string]::IsNullOrWhiteSpace([string]$baseline.seaPowerBuild) -or
    [string]$baseline.seaPowerBuild -cne [string]$candidate.seaPowerBuild) {
    throw 'Performance comparison requires the same Sea Power build on both sides.'
}
if ([int]$baseline.sampleCount -ne [int]$candidate.sampleCount -or
    [int]$baseline.sampleIntervalSeconds -ne [int]$candidate.sampleIntervalSeconds) {
    throw 'Performance comparison requires matching sample counts and intervals.'
}
if ([bool]$baseline.summary.anyPausedSample -or
    [bool]$candidate.summary.anyPausedSample -or
    [double]$baseline.summary.tacticalSimulationSecondsPerWallSecond -le 0 -or
    [double]$candidate.summary.tacticalSimulationSecondsPerWallSecond -le 0) {
    throw 'Performance comparison requires unpaused, advancing tactical time on both sides.'
}
if (-not [string]::IsNullOrWhiteSpace([string]$candidate.campaignId) -and
    ($null -eq $candidate.summary.campaignEntityCount -or
        [int]$candidate.summary.campaignEntityCount -le 0 -or
        $null -eq $candidate.summary.campaignQuantaPerWallSecond -or
        [double]$candidate.summary.campaignQuantaPerWallSecond -le 0)) {
    throw 'A campaign-bound candidate must report entities and advancing campaign time.'
}
$comparison = [ordered]@{
    schemaVersion = 1
    comparedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    interpretation = 'System-level comparison; workload and object/entity counts are reported and are not unit-count-normalized.'
    pairingKey = if ($sameExplicitPair) { [string]$baseline.pairingKey } else { '' }
    baseline = [ordered]@{
        label = $baseline.label
        workload = $baseline.workload
        scenarioId = $baseline.scenarioId
        sampleCount = $baseline.sampleCount
        averageObservedObjectCount = $baseline.summary.averageObservedObjectCount
        campaignEntityCount = $baseline.summary.campaignEntityCount
    }
    candidate = [ordered]@{
        label = $candidate.label
        workload = $candidate.workload
        scenarioId = $candidate.scenarioId
        sampleCount = $candidate.sampleCount
        averageObservedObjectCount = $candidate.summary.averageObservedObjectCount
        averageVisibleProxyCount = $candidate.summary.averageVisibleProxyCount
        campaignEntityCount = $candidate.summary.campaignEntityCount
    }
    deltas = @(
        Metric-Delta 'averageFramesPerSecond' $baseline.summary.averageFramesPerSecond $candidate.summary.averageFramesPerSecond 'higher'
        Metric-Delta 'minimumFramesPerSecond' $baseline.summary.minimumFramesPerSecond $candidate.summary.minimumFramesPerSecond 'higher'
        Metric-Delta 'p95MainThreadCaptureMicroseconds' $baseline.summary.p95MainThreadCaptureMicroseconds $candidate.summary.p95MainThreadCaptureMicroseconds 'lower'
        Metric-Delta 'tacticalSimulationSecondsPerWallSecond' $baseline.summary.tacticalSimulationSecondsPerWallSecond $candidate.summary.tacticalSimulationSecondsPerWallSecond 'higher'
        Metric-Delta 'seaPowerNormalizedCpuPercent' $baseline.processes.seaPower.normalizedCpuPercent $candidate.processes.seaPower.normalizedCpuPercent 'lower'
        Metric-Delta 'seaPowerWorkingSetMiB' $baseline.processes.seaPower.workingSetMiB $candidate.processes.seaPower.workingSetMiB 'lower'
        Metric-Delta 'companionNormalizedCpuPercent' $baseline.processes.companion.normalizedCpuPercent $candidate.processes.companion.normalizedCpuPercent 'lower'
        Metric-Delta 'companionWorkingSetMiB' $baseline.processes.companion.workingSetMiB $candidate.processes.companion.workingSetMiB 'lower'
    )
    dynamicCampaign = [ordered]@{
        quantaPerWallSecond = $candidate.summary.campaignQuantaPerWallSecond
        commitsPerWallSecond = $candidate.summary.campaignCommitsPerWallSecond
        advanceWorkUnitsPerSecond = $candidate.summary.campaignAdvanceWorkUnitsPerSecond
        serviceProcess = $candidate.processes.campaignService
    }
}
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$fullOutputPath = Resolve-OutputPath $OutputPath "$stamp-comparison.json"
$comparison | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $fullOutputPath -Encoding utf8
[pscustomobject]@{ outputPath = $fullOutputPath; comparison = $comparison } | ConvertTo-Json -Depth 12
