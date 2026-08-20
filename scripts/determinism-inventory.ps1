[CmdletBinding()]
param(
    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR }
        else { 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power' }
    ),

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $SeaPowerDir 'Sea Power_Data\Managed\Seapower-Scripts.dll'
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw "Sea Power managed assembly not found at $assemblyPath."
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'artifacts\determinism\callsite-manifest.json'
}

& dotnet run `
    --project (Join-Path $repoRoot 'src\CrimeanTom.CallsiteInventory\CrimeanTom.CallsiteInventory.csproj') `
    --configuration Debug `
    -- `
    --assembly $assemblyPath `
    --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Call-site inventory failed with exit code $LASTEXITCODE."
}
