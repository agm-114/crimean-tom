param(
    [string]$OutputPath = 'artifacts/helicopter-monitor.jsonl',
    [int]$DurationSeconds = 180,
    [int]$IntervalSeconds = 2
)
$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot '../src/SeaPower.Cli/bin/Debug/net10.0/seapower-cli.exe'
$directory = Split-Path ([IO.Path]::GetFullPath($OutputPath))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
do {
    $raw = & $cli units --format json
    if ($LASTEXITCODE -ne 0) { throw "Unit snapshot failed: $raw" }
    $sample = $raw | ConvertFrom-Json
    if (!$sample.time.available) { break }
    $sample | ConvertTo-Json -Depth 30 -Compress | Add-Content -LiteralPath $OutputPath
    $helicopters = $sample.units | Where-Object type -eq Helicopter
    foreach ($unit in $helicopters) {
        '{0:N1}s {1} {2:N1}kn {3} landing={4}' -f $sample.time.elapsedSeconds, $unit.id, $unit.speedKnots, $unit.flightDeckState, $unit.landing
    }
    Start-Sleep -Seconds ([Math]::Max(1, [Math]::Min($IntervalSeconds, 60)))
} while ([DateTime]::UtcNow -lt $deadline)

