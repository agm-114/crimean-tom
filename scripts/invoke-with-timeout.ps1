[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds,

    [string]$Command = '',

    [string]$CommandBase64 = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-ProcessTree([int]$ProcessId) {
    try {
        & taskkill.exe /PID $ProcessId /T /F 1>$null 2>$null
    }
    catch {
    }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

if (-not [string]::IsNullOrWhiteSpace($CommandBase64)) {
    $Command = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($CommandBase64))
}

if ([string]::IsNullOrWhiteSpace($Command)) {
    throw 'A -Command or -CommandBase64 value is required.'
}

$tempScript = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "crimean-tom-timeout-$([Guid]::NewGuid().ToString('N')).ps1")
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$scriptBody = @(
    $Command.TrimEnd(),
    'if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }'
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText($tempScript, $scriptBody, $utf8NoBom)

$process = $null
$exitCode = 0
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = (Get-Process -Id $PID).Path
    $startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$tempScript`""
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $startInfo.WorkingDirectory = (Get-Location).Path

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $null = $process.Start()

    $budgetMs = $TimeoutSeconds * 1000
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.HasExited -and $elapsed.ElapsedMilliseconds -lt $budgetMs) {
        Start-Sleep -Milliseconds 50
    }

    if (-not $process.HasExited) {
        Stop-ProcessTree $process.Id
        if (-not $process.HasExited) {
            try { $process.Kill() } catch { }
        }
        [Console]::Error.WriteLine("COMMAND_TIMEOUT after ${TimeoutSeconds}s")
        $exitCode = 124
    }
    else {
        $exitCode = $process.ExitCode
    }
}
finally {
    if ($null -ne $process) {
        $process.Dispose()
    }
    Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
}

exit $exitCode
