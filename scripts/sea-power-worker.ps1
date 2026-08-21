[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status')]
    [string]$Action = 'status',

    [Parameter(Position = 1, Mandatory = $true)]
    [string]$WorkerId,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR }
        else { 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power' }
    ),

    [string]$RunDirectory,

    [string]$ControlUrl,

    [int]$ControlPort = 0,

    [string]$CampaignGrpcUrl,

    [string]$CampaignHttpUrl,

    [string]$CampaignId,

    [string]$CampaignRunDirectory,

    [string]$CampaignTokenFile,

    [string]$CampaignStateDirectory,

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 120,

    [ValidateRange(640, 3840)]
    [int]$UnityScreenWidth = 1280,

    [ValidateRange(480, 2160)]
    [int]$UnityScreenHeight = 720,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$gameExe = Join-Path $SeaPowerDir 'Sea Power.exe'
$companionControlScript = Join-Path $PSScriptRoot 'crimean-tom-control.ps1'
$companionDll = Join-Path $repoRoot "src\CrimeanTom.Companion\bin\$Configuration\net8.0\CrimeanTom.Companion.dll"
$healthMarker = '[Sea Power Runtime Bridge] loaded via AnchorChain'
$defaultPipeName = 'CrimeanTom.Game.v1'
$processStartTimeToleranceSeconds = 2

function Assert-RestrictedWorkerId([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw 'WorkerId is required.'
    }
    $trimmed = $Value.Trim()
    if ($trimmed.Length -gt 64) {
        throw 'WorkerId must contain at most 64 characters.'
    }
    foreach ($character in $trimmed.ToCharArray()) {
        $isValid = (
            ($character -ge 'A' -and $character -le 'Z') -or
            ($character -ge 'a' -and $character -le 'z') -or
            ($character -ge '0' -and $character -le '9') -or
            ($character -eq '.' -or $character -eq '_' -or $character -eq '-')
        )
        if (-not $isValid) {
            throw 'WorkerId must contain only ASCII letters, digits, ".", "_", or "-".'
        }
    }
    return $trimmed
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

function Get-WorkerGamePipeName([string]$Id) {
    if ($Id -ieq 'default') { return $defaultPipeName }
    return "$defaultPipeName.$Id"
}

function Set-ProcessStartInfoEnvironment(
    [System.Diagnostics.ProcessStartInfo]$StartInfo,
    [hashtable]$Overrides) {
    foreach ($entry in [System.Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
        $StartInfo.EnvironmentVariables[$entry.Key] = [string]$entry.Value
    }
    foreach ($entry in $Overrides.GetEnumerator()) {
        $StartInfo.EnvironmentVariables[$entry.Key] = [string]$entry.Value
    }
}

function Resolve-WorkerCampaignSettings([string]$RunDir) {
    $hasGrpc = -not [string]::IsNullOrWhiteSpace($CampaignGrpcUrl)
    $hasHttp = -not [string]::IsNullOrWhiteSpace($CampaignHttpUrl)
    $hasId = -not [string]::IsNullOrWhiteSpace($CampaignId)
    $hasRunDirectory = -not [string]::IsNullOrWhiteSpace($CampaignRunDirectory)
    $hasTokenFile = -not [string]::IsNullOrWhiteSpace($CampaignTokenFile)
    $hasStateDirectory = -not [string]::IsNullOrWhiteSpace($CampaignStateDirectory)
    $anySupplied = $hasGrpc -or $hasHttp -or $hasId -or $hasRunDirectory -or $hasTokenFile -or $hasStateDirectory

    if (-not $anySupplied) {
        return [pscustomobject]@{
            IsCampaignBound = $false
            CampaignGrpcUrl = ''
            CampaignHttpUrl = ''
            CampaignId = ''
            CampaignRunDirectory = ''
            CampaignTokenFile = ''
            CampaignStateDirectory = ''
        }
    }

    $missing = @()
    if (-not $hasGrpc) { $missing += 'CampaignGrpcUrl' }
    if (-not $hasHttp) { $missing += 'CampaignHttpUrl' }
    if (-not $hasId) { $missing += 'CampaignId' }

    $resolvedRunDirectory = if ($hasRunDirectory) {
        [System.IO.Path]::GetFullPath($CampaignRunDirectory)
    }
    else {
        ''
    }

    $resolvedTokenFile = if ($hasTokenFile) {
        [System.IO.Path]::GetFullPath($CampaignTokenFile)
    }
    elseif ($hasRunDirectory) {
        Join-Path ([System.IO.Path]::GetFullPath($CampaignRunDirectory)) 'service.token'
    }
    else {
        ''
    }

    if ([string]::IsNullOrWhiteSpace($resolvedTokenFile)) {
        $missing += 'CampaignTokenFile or CampaignRunDirectory'
    }
    elseif (-not (Test-Path -LiteralPath $resolvedTokenFile)) {
        $missing += "resolved CampaignTokenFile '$resolvedTokenFile'"
    }

    if ($hasStateDirectory) {
        $resolvedStateDirectory = [System.IO.Path]::GetFullPath($CampaignStateDirectory)
    }
    elseif ($hasId) {
        $resolvedStateDirectory = Join-Path $RunDir "campaign-companion-state-$CampaignId"
    }
    else {
        $missing += 'CampaignStateDirectory or CampaignId'
        $resolvedStateDirectory = ''
    }

    if ($missing.Count -gt 0) {
        throw "Incomplete campaign worker configuration. Resolve or supply: $($missing -join ', ')."
    }

    return [pscustomobject]@{
        IsCampaignBound = $true
        CampaignGrpcUrl = $CampaignGrpcUrl.Trim()
        CampaignHttpUrl = $CampaignHttpUrl.Trim()
        CampaignId = $CampaignId.Trim()
        CampaignRunDirectory = $resolvedRunDirectory
        CampaignTokenFile = $resolvedTokenFile
        CampaignStateDirectory = $resolvedStateDirectory
    }
}

function Get-WorkerLayout([string]$Id) {
    $runDir = if ([string]::IsNullOrWhiteSpace($RunDirectory)) {
        Join-Path $repoRoot ".run\workers\$Id"
    }
    else {
        [System.IO.Path]::GetFullPath($RunDirectory)
    }
    $port = if ($ControlPort -gt 0) { $ControlPort } else { Get-DefaultWorkerControlPort $Id }
    $control = if ([string]::IsNullOrWhiteSpace($ControlUrl)) {
        "http://127.0.0.1:$port"
    }
    else {
        $ControlUrl.TrimEnd('/')
    }
    $pipeName = Get-WorkerGamePipeName $Id
    $experimentalSaveDirectory = Join-Path $runDir 'experimental-saves'
    $unityLog = Join-Path $runDir 'unity\Player.log'
    $metadataPath = Join-Path $runDir 'worker.metadata.json'
    $gamePidFile = Join-Path $runDir 'game.pid'
    $companionPidFile = Join-Path $runDir 'companion.pid'
    $tokenFile = Join-Path $runDir 'control.token'
    $campaignSettings = Resolve-WorkerCampaignSettings $runDir
    return [pscustomobject]@{
        WorkerId = $Id
        RunDirectory = $runDir
        ControlUrl = $control
        ControlPort = $port
        PipeName = $pipeName
        InstanceId = $Id
        ExperimentalSaveDirectory = $experimentalSaveDirectory
        UnityLog = $unityLog
        MetadataPath = $metadataPath
        GamePidFile = $gamePidFile
        CompanionPidFile = $companionPidFile
        TokenFile = $tokenFile
        GameExecutable = [System.IO.Path]::GetFullPath($gameExe)
        CompanionDll = [System.IO.Path]::GetFullPath($companionDll)
        CampaignSettings = $campaignSettings
        UnityScreenWidth = $UnityScreenWidth
        UnityScreenHeight = $UnityScreenHeight
    }
}

function Assert-GameInstalled([string]$ExecutablePath) {
    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        throw "Sea Power executable not found at $ExecutablePath."
    }
}

function Read-WorkerMetadata([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $raw = Get-Content -LiteralPath $Path -Raw
    return $raw | ConvertFrom-Json
}

function Write-WorkerMetadata($Layout, [int]$CompanionProcessId, $GameProcess) {
    $campaign = $Layout.CampaignSettings
    $metadata = [ordered]@{
        workerId = $Layout.WorkerId
        instanceId = $Layout.InstanceId
        controlUrl = $Layout.ControlUrl
        pipeName = $Layout.PipeName
        runDirectory = $Layout.RunDirectory
        gameExecutable = $Layout.GameExecutable
        unityLog = $Layout.UnityLog
        experimentalSaveDirectory = $Layout.ExperimentalSaveDirectory
        companionProcessId = $CompanionProcessId
        gameProcessId = $GameProcess.Id
        gameProcessStartTimeUtc = $GameProcess.StartTime.ToUniversalTime().ToString('o')
        campaignId = if ($campaign.IsCampaignBound) { $campaign.CampaignId } else { '' }
        campaignGrpcUrl = if ($campaign.IsCampaignBound) { $campaign.CampaignGrpcUrl } else { '' }
        campaignHttpUrl = if ($campaign.IsCampaignBound) { $campaign.CampaignHttpUrl } else { '' }
        campaignRunDirectory = if ($campaign.IsCampaignBound) { $campaign.CampaignRunDirectory } else { '' }
        campaignStateDirectory = if ($campaign.IsCampaignBound) { $campaign.CampaignStateDirectory } else { '' }
        campaignTokenFile = if ($campaign.IsCampaignBound) { $campaign.CampaignTokenFile } else { '' }
        recordedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Layout.MetadataPath -Encoding UTF8
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

function Test-GameProcessValidation(
    $Process,
    [string]$ExpectedExecutable,
    [string]$ExpectedUnityLog,
    [int]$ExpectedProcessId,
    [string]$RecordedStartTimeUtc,
    [bool]$RequireRecordedStartTime = $true) {
    if ($null -eq $Process) { return $false }
    if ($Process.ProcessName -cne 'Sea Power') { return $false }
    if ($ExpectedProcessId -gt 0 -and $Process.Id -ne $ExpectedProcessId) { return $false }

    $executablePath = Get-ProcessExecutablePath $Process.Id
    if ([string]::IsNullOrWhiteSpace($executablePath)) { return $false }
    $resolvedExecutable = [System.IO.Path]::GetFullPath($executablePath)
    $expectedResolved = [System.IO.Path]::GetFullPath($ExpectedExecutable)
    if (-not $resolvedExecutable.Equals($expectedResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if (-not (Test-ProcessStartTimeMatches `
        $Process `
        $RecordedStartTimeUtc `
        $processStartTimeToleranceSeconds `
        $RequireRecordedStartTime)) {
        return $false
    }

    $commandLine = Get-ProcessCommandLine $Process.Id
    if ($commandLine.IndexOf('-logFile', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $false
    }
    $normalizedLog = [System.IO.Path]::GetFullPath($ExpectedUnityLog)
    if ($commandLine.IndexOf($normalizedLog, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $false
    }

    return $true
}

function Get-LiveProcessFromPidFile([string]$PidFilePath) {
    if (-not (Test-Path -LiteralPath $PidFilePath)) { return $null }
    $rawPid = (Get-Content -LiteralPath $PidFilePath -Raw).Trim()
    $processId = 0
    if (-not [int]::TryParse($rawPid, [ref]$processId)) { return $null }
    return Get-Process -Id $processId -ErrorAction SilentlyContinue
}

function Remove-StalePidFile([string]$PidFilePath) {
    if (-not (Test-Path -LiteralPath $PidFilePath)) { return }
    $liveProcess = Get-LiveProcessFromPidFile $PidFilePath
    if ($null -eq $liveProcess) {
        Remove-Item -LiteralPath $PidFilePath -Force
    }
}

function Test-ValidatedGameProcess($Layout, $Metadata) {
    $liveProcess = Get-LiveProcessFromPidFile $Layout.GamePidFile
    if ($null -eq $liveProcess) { return $null }
    if ($null -eq $Metadata) { return $null }

    $recordedStart = [string]$Metadata.gameProcessStartTimeUtc
    if ([string]::IsNullOrWhiteSpace($recordedStart)) { return $null }
    try {
        [DateTimeOffset]::Parse($recordedStart)
    }
    catch {
        return $null
    }

    $expectedProcessId = [int]$Metadata.gameProcessId
    if (-not (Test-GameProcessValidation `
        $liveProcess `
        $Layout.GameExecutable `
        $Layout.UnityLog `
        $expectedProcessId `
        $recordedStart `
        $true)) {
        return $null
    }

    return $liveProcess
}

function Test-ValidatedCompanionProcess($Layout) {
    $liveProcess = Get-LiveProcessFromPidFile $Layout.CompanionPidFile
    if ($null -eq $liveProcess) { return $null }

    $commandLine = Get-ProcessCommandLine $liveProcess.Id
    if ($commandLine.IndexOf($Layout.CompanionDll, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $null
    }
    if ($commandLine.IndexOf($Layout.ControlUrl, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        return $null
    }

    return $liveProcess
}

function Assert-NoUnvalidatedBlockingProcesses($Layout) {
    Remove-StalePidFile $Layout.GamePidFile
    Remove-StalePidFile $Layout.CompanionPidFile

    $liveGame = Get-LiveProcessFromPidFile $Layout.GamePidFile
    if ($null -ne $liveGame) {
        $metadata = Read-WorkerMetadata $Layout.MetadataPath
        if ($null -eq $metadata) {
            throw "Refusing to start worker '$($Layout.WorkerId)': game PID file points to live PID $($liveGame.Id), but worker metadata is missing at $($Layout.MetadataPath)."
        }
        if ([string]::IsNullOrWhiteSpace([string]$metadata.gameProcessStartTimeUtc)) {
            throw "Refusing to start worker '$($Layout.WorkerId)': game PID file points to live PID $($liveGame.Id), but metadata has no recorded UTC start time."
        }
        if ($null -eq (Test-ValidatedGameProcess $Layout $metadata)) {
            throw "Refusing to start worker '$($Layout.WorkerId)': game PID file points to live PID $($liveGame.Id), but the process failed worker validation. Stop or diagnose the stale worker before retrying."
        }
    }

    $liveCompanion = Get-LiveProcessFromPidFile $Layout.CompanionPidFile
    if ($null -ne $liveCompanion) {
        if ($null -eq (Test-ValidatedCompanionProcess $Layout)) {
            throw "Refusing to start worker '$($Layout.WorkerId)': companion PID file points to live PID $($liveCompanion.Id), but the process failed worker validation. Stop or diagnose the stale worker before retrying."
        }
    }
}

function Invoke-ControlRequest([string]$Url, [string]$TokenPath, [string]$Path) {
    if (-not (Test-Path -LiteralPath $TokenPath)) {
        throw "Companion control token not found at $TokenPath."
    }
    $token = (Get-Content -LiteralPath $TokenPath -Raw).Trim()
    $headers = @{ 'X-Crimean-Tom-Token' = $token }
    Invoke-RestMethod -Method Get -Uri "$Url$Path" -Headers $headers -TimeoutSec 8
}

function Wait-ForGameConnection($Layout) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastHealth = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $metadata = Read-WorkerMetadata $Layout.MetadataPath
        $gameProcess = Test-ValidatedGameProcess $Layout $metadata
        if ($null -eq $gameProcess) {
            throw "Sea Power exited or failed validation before connecting to $($Layout.PipeName). Check $($Layout.UnityLog)."
        }

        try {
            $lastHealth = Invoke-ControlRequest $Layout.ControlUrl $Layout.TokenFile '/health'
            if ($lastHealth.gameConnected -and $lastHealth.connectionState -ceq 'connected') {
                return $lastHealth
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    }

    $diagnostics = ''
    if (Test-Path -LiteralPath $Layout.UnityLog) {
        $diagnostics = (Get-Content -LiteralPath $Layout.UnityLog -Tail 20) -join [Environment]::NewLine
    }
    throw "Worker '$($Layout.WorkerId)' did not connect to $($Layout.PipeName) within $TimeoutSeconds seconds. Sea Power may enforce single-instance, or Steam/BepInEx may have blocked a second process. $diagnostics"
}

function Wait-ForPluginMarker($Layout, [datetime]$NotBeforeUtc) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Layout.UnityLog) {
            $logInfo = Get-Item -LiteralPath $Layout.UnityLog
            if ($logInfo.LastWriteTimeUtc -ge $NotBeforeUtc.AddSeconds(-2)) {
                $text = Get-Content -LiteralPath $Layout.UnityLog -Raw -ErrorAction SilentlyContinue
                if ($null -ne $text -and $text.Contains($healthMarker)) {
                    return
                }
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Worker '$($Layout.WorkerId)' did not write the runtime bridge health marker to $($Layout.UnityLog)."
}

function Get-UnityLaunchArguments($Layout) {
    $logPath = [System.IO.Path]::GetFullPath($Layout.UnityLog)
    return @(
        "-logFile `"$logPath`""
        "-screen-fullscreen 0"
        "-screen-width $($Layout.UnityScreenWidth)"
        "-screen-height $($Layout.UnityScreenHeight)"
    ) -join ' '
}

function Start-WorkerGameProcess($Layout) {
    Assert-GameInstalled $Layout.GameExecutable
    New-Item -ItemType Directory -Path (Split-Path -Parent $Layout.UnityLog) -Force | Out-Null
    New-Item -ItemType Directory -Path $Layout.ExperimentalSaveDirectory -Force | Out-Null

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Layout.GameExecutable
    $startInfo.WorkingDirectory = Split-Path -Parent $Layout.GameExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Minimized
    $startInfo.Arguments = Get-UnityLaunchArguments $Layout

    Set-ProcessStartInfoEnvironment $startInfo @{
        CRIMEAN_TOM_INSTANCE_ID = $Layout.InstanceId
        CRIMEAN_TOM_GAME_PIPE_NAME = $Layout.PipeName
        CRIMEAN_TOM_EXPERIMENTAL_SAVE_DIRECTORY = $Layout.ExperimentalSaveDirectory
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start Sea Power for worker '$($Layout.WorkerId)'."
    }
    Set-Content -LiteralPath $Layout.GamePidFile -Value $process.Id -NoNewline
    return $process
}

function Start-WorkerCompanion($Layout) {
    $campaign = $Layout.CampaignSettings
    $companionParameters = @{
        Action = 'start'
        Configuration = $Configuration
        SkipBuild = $SkipBuild
        ControlUrl = $Layout.ControlUrl
        RunDirectory = $Layout.RunDirectory
        GamePipeName = $Layout.PipeName
    }
    if ($campaign.IsCampaignBound) {
        New-Item -ItemType Directory -Path $campaign.CampaignStateDirectory -Force | Out-Null
        $companionParameters.CampaignGrpcUrl = $campaign.CampaignGrpcUrl
        $companionParameters.CampaignHttpUrl = $campaign.CampaignHttpUrl
        $companionParameters.CampaignId = $campaign.CampaignId
        $companionParameters.CampaignStateDirectory = $campaign.CampaignStateDirectory
        $companionParameters.CampaignTokenFile = $campaign.CampaignTokenFile
        if (-not [string]::IsNullOrWhiteSpace($campaign.CampaignRunDirectory)) {
            $companionParameters.CampaignRunDirectory = $campaign.CampaignRunDirectory
        }
    }
    & $companionControlScript @companionParameters

    if (-not (Test-Path -LiteralPath $Layout.CompanionPidFile)) {
        throw "Companion did not record a PID at $($Layout.CompanionPidFile)."
    }
    $rawPid = (Get-Content -LiteralPath $Layout.CompanionPidFile -Raw).Trim()
    $processId = 0
    if (-not [int]::TryParse($rawPid, [ref]$processId)) {
        throw "Companion PID file at $($Layout.CompanionPidFile) is invalid."
    }
    return $processId
}

function Stop-WorkerCompanion($Layout) {
    & $companionControlScript stop `
        -ControlUrl $layout.ControlUrl `
        -RunDirectory $layout.RunDirectory
}

function Cleanup-FailedWorkerStart($Layout, $GameProcess, [int]$CompanionProcessId, [string]$Reason) {
    if ($null -ne $GameProcess -and -not $GameProcess.HasExited) {
        if (Test-GameProcessValidation `
            $GameProcess `
            $Layout.GameExecutable `
            $Layout.UnityLog `
            $GameProcess.Id `
            $GameProcess.StartTime.ToUniversalTime().ToString('o') `
            $false) {
            Stop-ValidatedProcess $GameProcess.Id $Layout.GameExecutable "worker '$($Layout.WorkerId)' Sea Power"
        }
    }

    if ($CompanionProcessId -gt 0) {
        $liveCompanion = Get-Process -Id $CompanionProcessId -ErrorAction SilentlyContinue
        if ($null -ne $liveCompanion) {
            Stop-WorkerCompanion $Layout
        }
    }

    if (Test-Path -LiteralPath $Layout.GamePidFile) {
        Remove-Item -LiteralPath $Layout.GamePidFile -Force
    }
    if (Test-Path -LiteralPath $Layout.MetadataPath) {
        Remove-Item -LiteralPath $Layout.MetadataPath -Force
    }

    throw $Reason
}

function Start-Worker {
    $id = Assert-RestrictedWorkerId $WorkerId
    $layout = Get-WorkerLayout $id
    New-Item -ItemType Directory -Path $layout.RunDirectory -Force | Out-Null

    Assert-NoUnvalidatedBlockingProcesses $layout

    $metadata = Read-WorkerMetadata $layout.MetadataPath
    $existingGame = Test-ValidatedGameProcess $layout $metadata
    if ($null -ne $existingGame) {
        Write-Host "Worker '$id' already has a validated Sea Power process (PID $($existingGame.Id))."
        $health = try {
            Invoke-ControlRequest $layout.ControlUrl $layout.TokenFile '/health'
        }
        catch {
            $null
        }
        if ($null -ne $health -and $health.gameConnected) {
            Write-Host "Companion at $($layout.ControlUrl) is healthy and connected."
            return
        }
    }

    $companionPid = 0
    $gameProcess = $null
    try {
        Write-Host "Starting companion for worker '$id' at $($layout.ControlUrl) on pipe $($layout.PipeName)..."
        if ($layout.CampaignSettings.IsCampaignBound) {
            Write-Host "Campaign companion state: $($layout.CampaignSettings.CampaignStateDirectory)"
        }
        else {
            Write-Host 'Campaign companion: unbound runtime worker (no campaign transport).'
        }
        $companionPid = Start-WorkerCompanion $layout

        $startedAtUtc = [DateTime]::UtcNow
        Write-Host "Launching Sea Power directly for worker '$id' (experimental second-instance path)..."
        $gameProcess = Start-WorkerGameProcess $layout
        Write-WorkerMetadata $layout $companionPid $gameProcess

        Wait-ForPluginMarker $layout $startedAtUtc
        $health = Wait-ForGameConnection $layout
        Write-Host "Worker '$id' is live: companion PID $companionPid, game PID $($gameProcess.Id), session $($health.sessionId)."
    }
    catch {
        $reason = $_.Exception.Message
        if ([string]::IsNullOrWhiteSpace($reason)) {
            $reason = "Worker '$id' start failed."
        }
        Cleanup-FailedWorkerStart $layout $gameProcess $companionPid $reason
    }
}

function Stop-ValidatedProcess([int]$ProcessId, [string]$ExpectedExecutable, [string]$Label) {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }

    $executablePath = Get-ProcessExecutablePath $ProcessId
    if ([string]::IsNullOrWhiteSpace($executablePath)) {
        throw "Refusing to stop $Label PID $ProcessId because its executable path could not be verified."
    }
    $resolvedExecutable = [System.IO.Path]::GetFullPath($executablePath)
    $expectedResolved = [System.IO.Path]::GetFullPath($ExpectedExecutable)
    if (-not $resolvedExecutable.Equals($expectedResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to stop $Label PID $ProcessId because executable '$resolvedExecutable' does not match '$expectedResolved'."
    }

    Write-Host "Stopping $Label (PID $ProcessId)..."
    $null = $process.CloseMainWindow()
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $ProcessId -Force
        Wait-Process -Id $ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Stop-Worker {
    $id = Assert-RestrictedWorkerId $WorkerId
    $layout = Get-WorkerLayout $id
    Remove-StalePidFile $layout.GamePidFile
    $metadata = Read-WorkerMetadata $layout.MetadataPath
    $expectedExecutable = $layout.GameExecutable

    $candidateProcessId = 0
    if ($null -ne $metadata -and [int]$metadata.gameProcessId -gt 0) {
        $candidateProcessId = [int]$metadata.gameProcessId
    }

    if ($candidateProcessId -gt 0) {
        $liveProcess = Get-Process -Id $candidateProcessId -ErrorAction SilentlyContinue
        if ($null -ne $liveProcess) {
            if ($null -eq $metadata) {
                throw "Refusing to stop worker '$id': live game PID $candidateProcessId requires worker metadata at $($layout.MetadataPath)."
            }
            $recordedStart = [string]$metadata.gameProcessStartTimeUtc
            if ([string]::IsNullOrWhiteSpace($recordedStart)) {
                throw "Refusing to stop worker '$id': live game PID $candidateProcessId requires a recorded UTC start time in metadata. Metadata preserved at $($layout.MetadataPath)."
            }
            if (-not (Test-GameProcessValidation `
                $liveProcess `
                $expectedExecutable `
                $layout.UnityLog `
                $candidateProcessId `
                $recordedStart `
                $true)) {
                throw "Refusing to stop worker '$id': recorded game PID $candidateProcessId is live but failed validation against $expectedExecutable. Metadata preserved at $($layout.MetadataPath)."
            }
            Stop-ValidatedProcess $candidateProcessId $expectedExecutable "worker '$id' Sea Power"
        }
    }

    if (Test-Path -LiteralPath $layout.GamePidFile) {
        Remove-Item -LiteralPath $layout.GamePidFile -Force
    }

    Stop-WorkerCompanion $layout

    if (Test-Path -LiteralPath $layout.MetadataPath) {
        Remove-Item -LiteralPath $layout.MetadataPath -Force
    }
    Write-Host "Worker '$id' stopped."
}

function Show-WorkerStatus {
    $id = Assert-RestrictedWorkerId $WorkerId
    $layout = Get-WorkerLayout $id
    $metadata = Read-WorkerMetadata $layout.MetadataPath
    $gameProcess = Test-ValidatedGameProcess $layout $metadata
    $companionStatus = & $companionControlScript status `
        -ControlUrl $layout.ControlUrl `
        -RunDirectory $layout.RunDirectory

    $health = $null
    $capabilities = $null
    $campaignSession = $null
    $healthError = ''
    if (Test-Path -LiteralPath $layout.TokenFile) {
        try {
            $health = Invoke-ControlRequest $layout.ControlUrl $layout.TokenFile '/health'
            if ($health.gameConnected) {
                $capabilities = Invoke-ControlRequest $layout.ControlUrl $layout.TokenFile '/game/capabilities'
            }
        }
        catch {
            $healthError = $_.Exception.Message
        }
    }

    $campaign = $layout.CampaignSettings
    $campaignHttp = if (-not [string]::IsNullOrWhiteSpace($CampaignHttpUrl)) {
        $CampaignHttpUrl
    }
    elseif ($campaign.IsCampaignBound) {
        $campaign.CampaignHttpUrl
    }
    elseif ($null -ne $metadata -and -not [string]::IsNullOrWhiteSpace([string]$metadata.campaignHttpUrl)) {
        [string]$metadata.campaignHttpUrl
    }
    else {
        ''
    }
    $campaignId = if (-not [string]::IsNullOrWhiteSpace($CampaignId)) {
        $CampaignId
    }
    elseif ($campaign.IsCampaignBound) {
        $campaign.CampaignId
    }
    elseif ($null -ne $metadata -and -not [string]::IsNullOrWhiteSpace([string]$metadata.campaignId)) {
        [string]$metadata.campaignId
    }
    else {
        ''
    }
    $campaignToken = if (-not [string]::IsNullOrWhiteSpace($CampaignTokenFile)) {
        $CampaignTokenFile
    }
    elseif ($campaign.IsCampaignBound) {
        $campaign.CampaignTokenFile
    }
    elseif ($null -ne $metadata -and -not [string]::IsNullOrWhiteSpace([string]$metadata.campaignTokenFile)) {
        [string]$metadata.campaignTokenFile
    }
    else {
        ''
    }
    $campaignStateDirectory = if ($campaign.IsCampaignBound) {
        $campaign.CampaignStateDirectory
    }
    elseif ($null -ne $metadata) {
        [string]$metadata.campaignStateDirectory
    }
    else {
        ''
    }

    if ((-not [string]::IsNullOrWhiteSpace($campaignHttp)) -and
        (-not [string]::IsNullOrWhiteSpace($campaignId)) -and
        (-not [string]::IsNullOrWhiteSpace($campaignToken)) -and
        (Test-Path -LiteralPath $campaignToken)) {
        try {
            $token = (Get-Content -LiteralPath $campaignToken -Raw).Trim()
            $headers = @{ Authorization = "Bearer $token" }
            $campaignView = Invoke-RestMethod `
                -Method Get `
                -Uri "$campaignHttp/campaigns/$campaignId" `
                -Headers $headers `
                -TimeoutSec 8
            $campaignSession = @($campaignView.workers | Where-Object {
                $_.gameSessionId -and $health -and $_.gameSessionId -eq $health.sessionId
            }) | Select-Object -First 1
        }
        catch {
            if ([string]::IsNullOrWhiteSpace($healthError)) {
                $healthError = $_.Exception.Message
            }
        }
    }

    [pscustomobject]@{
        WorkerId = $id
        ControlUrl = $layout.ControlUrl
        PipeName = $layout.PipeName
        RunDirectory = $layout.RunDirectory
        CampaignStateDirectory = $campaignStateDirectory
        UnityLog = $layout.UnityLog
        GameExecutable = $layout.GameExecutable
        GameProcessId = if ($null -ne $gameProcess) { $gameProcess.Id } else { $null }
        GameConnected = $null -ne $health -and $health.gameConnected
        CompanionHealthy = $null -ne $health
        ConnectionState = if ($null -ne $health) { $health.connectionState } else { '' }
        GameSessionId = if ($null -ne $health) { $health.sessionId } else { '' }
        RuntimeFingerprint = if ($null -ne $capabilities) {
            $capabilities.runtimeFingerprint.fingerprintSha256
        } else { '' }
        CampaignWorkerClientId = if ($null -ne $campaignSession) {
            $campaignSession.clientInstanceId
        } else { '' }
        CampaignWorkerSessionId = if ($null -ne $campaignSession) {
            $campaignSession.sessionId
        } else { '' }
        PromotionHeadroom = if ($null -ne $campaignSession) {
            $campaignSession.promotionHeadroom
        } else { $null }
        MigrationOverlapHeadroom = if ($null -ne $campaignSession) {
            $campaignSession.migrationOverlapHeadroom
        } else { $null }
        HealthMarkerInLog = if (Test-Path -LiteralPath $layout.UnityLog) {
            (Get-Content -LiteralPath $layout.UnityLog -Raw -ErrorAction SilentlyContinue).Contains($healthMarker)
        } else { $false }
        StatusError = $healthError
        CompanionStatus = ($companionStatus | Out-String).Trim()
    } | Format-List
}

$validatedWorkerId = Assert-RestrictedWorkerId $WorkerId
$WorkerId = $validatedWorkerId

try {
    switch ($Action) {
        'start' { Start-Worker }
        'stop' { Stop-Worker }
        'status' { Show-WorkerStatus }
    }
}
catch {
    Write-Error $_
    exit 1
}
