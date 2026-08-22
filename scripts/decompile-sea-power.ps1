[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('game', 'anchorchain', 'all-managed')]
    [string]$Target = 'game',

    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR }
        else { 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power' }
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'decompiled'))
$managedDir = Join-Path $SeaPowerDir 'Sea Power_Data\Managed'
$bepInExDir = Join-Path $SeaPowerDir 'BepInEx'
$workshopRoot = Split-Path -Parent (Split-Path -Parent $SeaPowerDir)
$workshopRoot = Join-Path $workshopRoot 'workshop\content\1286220'

function Assert-Prerequisites {
    if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
        throw 'ilspycmd is not installed. Install the ILSpy CLI .NET global tool first.'
    }
    if (-not (Test-Path -LiteralPath $managedDir -PathType Container)) {
        throw "Sea Power managed assemblies were not found at $managedDir. Set SEA_POWER_DIR to the game directory."
    }
}

function Reset-OutputDirectory([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($outputRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace output outside $outputRoot`: $fullPath"
    }
    if ([System.IO.Directory]::Exists($fullPath)) {
        [System.IO.Directory]::Delete($fullPath, $true)
    }
    [System.IO.Directory]::CreateDirectory($fullPath) | Out-Null
}

function Invoke-Decompile([string]$AssemblyPath, [string]$DestinationRoot, [string]$ReferencePath) {
    if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
        throw "Assembly not found: $AssemblyPath"
    }

    $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($AssemblyPath)
    $destination = Join-Path $DestinationRoot $assemblyName
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null

    Write-Host "Decompiling $([System.IO.Path]::GetFileName($AssemblyPath)) -> $destination"
    $timeoutWrapper = Join-Path $repoRoot 'scripts\invoke-with-timeout.ps1'
    & $timeoutWrapper -TimeoutSeconds 600 -Command (
        "ilspycmd `"$AssemblyPath`" --project --outputdir `"$destination`" --referencepath `"$ReferencePath`" --disable-updatecheck"
    )
    if ($LASTEXITCODE -eq 124) {
        throw "ilspycmd timed out for $AssemblyPath after 600s."
    }
    if ($LASTEXITCODE -ne 0) {
        throw "ilspycmd failed for $AssemblyPath with exit code $LASTEXITCODE."
    }
}

Assert-Prerequisites
[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null

switch ($Target) {
    'game' {
        $destinationRoot = Join-Path $outputRoot 'game'
        Reset-OutputDirectory $destinationRoot
        foreach ($name in @('Seapower-Scripts.dll', 'Assets.dll', 'Libraries.dll', 'Plugins.dll')) {
            Invoke-Decompile (Join-Path $managedDir $name) $destinationRoot $managedDir
        }
    }
    'anchorchain' {
        $destinationRoot = Join-Path $outputRoot 'anchorchain'
        Reset-OutputDirectory $destinationRoot

        $anchorChain = Get-ChildItem -LiteralPath $workshopRoot -Filter 'AnchorChain.dll' -File -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $anchorChain) {
            throw "AnchorChain.dll was not found below $workshopRoot."
        }

        Invoke-Decompile $anchorChain.FullName $destinationRoot $managedDir
        Invoke-Decompile (Join-Path $bepInExDir 'plugins\AnchorChainPreloader\AnchorChain.Preloader.dll') $destinationRoot $managedDir
    }
    'all-managed' {
        $destinationRoot = Join-Path $outputRoot 'all-managed'
        Reset-OutputDirectory $destinationRoot
        foreach ($assembly in Get-ChildItem -LiteralPath $managedDir -Filter '*.dll' -File | Sort-Object Name) {
            Invoke-Decompile $assembly.FullName $destinationRoot $managedDir
        }
    }
}

Write-Host "Decompilation complete: $destinationRoot"
