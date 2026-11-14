param(
    [string]$Cli = (Join-Path $PSScriptRoot '../src/SeaPower.Cli/bin/Debug/net10.0/seapower-cli.exe')
)

$ErrorActionPreference = 'Stop'
$boot = & $Cli launch --confirm --timeout 90 --require-pattern '\[Quickload\] Main menu visible' --format json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Quickload did not reach the main menu.' }
$game = Get-Process -Id $boot.processIds[0]

try {
    $deadline = (Get-Date).AddSeconds(90)
    do {
        $gates = @(Select-String -LiteralPath $boot.playerLog.path -Pattern '\[Quickload\] Menu gates: ' |
            ForEach-Object { $_.Line.Split(@('Menu gates: '), 2, [StringSplitOptions]::None)[1] })
        if ($gates.Count -ge 3) { break }
        Start-Sleep -Milliseconds 350
    } while ((Get-Date) -lt $deadline)

    if ($gates.Count -ne 3) { throw "Expected three readiness snapshots, got $($gates.Count)." }
    $core = 'Instant', 'Scenarios', 'Campaign', 'Tutorials'
    $encyclopedia = 'Editor', 'Encyclopedia', 'Options', 'ModManager'
    for ($phase = 0; $phase -lt $gates.Count; $phase++) {
        $states = ConvertFrom-StringData ($gates[$phase] -replace ', ', "`n")
        foreach ($name in $core + $encyclopedia) {
            # Instant Action can still be disabled by the native configuration.
            if ($name -eq 'Instant' -and $phase -gt 0) { continue }
            $enabled = if ($name -in $core) { $phase -ge 1 } else { $phase -eq 2 }
            if ($states[$name] -ne $enabled.ToString()) {
                throw "$name reported '$($states[$name])' at readiness snapshot $($phase + 1), expected $enabled."
            }
        }
        $gates[$phase]
    }
} finally {
    if (!$game.HasExited -and (!$game.CloseMainWindow() -or !$game.WaitForExit(20000))) {
        throw 'The test game did not close.'
    }
}
