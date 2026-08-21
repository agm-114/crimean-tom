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
    & taskkill.exe /PID $ProcessId /T /F 2>$null | Out-Null
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
[System.IO.File]::WriteAllText(
    $tempScript,
    $Command,
    $utf8NoBom)

$process = $null
$exitCode = 0
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = (Get-Process -Id $PID).Path
    $startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$tempScript`""
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = (Get-Location).Path

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $null = $process.Start()

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        [Console]::Error.WriteLine("COMMAND_TIMEOUT after ${TimeoutSeconds}s")
        Stop-ProcessTree $process.Id
        if (-not $process.HasExited) {
            $process.Kill()
        }
        $exitCode = 124
    }
    else {
        [void]$stdoutTask.Wait()
        [void]$stderrTask.Wait()
        if ($stdoutTask.Result) {
            [Console]::Out.Write($stdoutTask.Result)
        }
        if ($stderrTask.Result) {
            [Console]::Error.Write($stderrTask.Result)
        }
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
