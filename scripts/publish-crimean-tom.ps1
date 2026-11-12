[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AmphibsPackage,
    [Parameter(Mandatory)][string]$DeckTweaksPackage,
    [ValidateSet('Bundle', 'Amphibs', 'DeckTweaks', 'Both')][string]$Update = 'Bundle',
    [string]$AmphibsPreviews,
    [string]$DeckTweaksPreviews,
    [string]$ChangeLog,
    [switch]$Preview,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$listing = Join-Path $repo 'content/workshop/crimean-tom'
$cli = Join-Path $repo 'src/SeaPower.Cli/bin/Debug/net10.0/seapower-cli.exe'
$components = @(
    @{ Name = 'Amphibs'; Path = $AmphibsPackage; Files = @('Amphibs.dll', 'amphibious-variants.ini'); ContentFolders = @('unit_extensions'); Previews = $AmphibsPreviews }
    @{ Name = 'DeckTweaks'; Path = $DeckTweaksPackage; Files = @('DeckTweaks.dll'); OptionalFiles = @('rearm-times.ini'); Previews = $DeckTweaksPreviews }
)

function Get-GameVersion($text) {
    $version = [regex]::Match($text, '(?m)^ExactVersion[ \t]*=[ \t]*([^\r\n]+)')
    if (!$version.Success) { throw 'Each component package must declare ExactVersion in _info.ini.' }
    $version.Groups[1].Value.Trim()
}

$versions = foreach ($component in $components) {
    $component.Path = (Resolve-Path -LiteralPath $component.Path).Path
    foreach ($file in $component.Files) {
        if (!(Test-Path -LiteralPath (Join-Path $component.Path $file) -PathType Leaf)) {
            throw "$($component.Name) package is missing $file."
        }
    }
    Get-GameVersion ([IO.File]::ReadAllText((Join-Path $component.Path '_info.ini')))
}
if ($versions[0] -ne $versions[1]) { throw "Component packages target different game versions: $($versions -join ', ')." }

$metadata = [IO.File]::ReadAllText((Join-Path $listing '_info.ini'))
$metadata = $metadata -replace '(?m)^ExactVersion[ \t]*=[^\r\n]+', "ExactVersion=$($versions[0])"
if (($Publish -or $Preview) -and $metadata -notmatch '(?m)^Url=https://steamcommunity\.com/sharedfiles/filedetails/\?id=\d+\s*$') {
    throw 'Create the Crimean Tom Workshop item first and record its URL in the bundle metadata.'
}

$bundle = Join-Path $repo "artifacts/workshop/$([guid]::NewGuid().ToString('N'))/CrimeanTom"
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $bundle '_info.ini'), $metadata)
Copy-Item -LiteralPath (Join-Path $listing 'thumbnail.jpg') -Destination $bundle
foreach ($component in $components) {
    foreach ($file in $component.Files) {
        Copy-Item -LiteralPath (Join-Path $component.Path $file) -Destination $bundle
    }
    foreach ($file in $component.OptionalFiles) {
        $source = Join-Path $component.Path $file
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $bundle
        }
    }
    foreach ($folder in $component.ContentFolders) {
        $source = Join-Path $component.Path $folder
        if (Test-Path -LiteralPath $source -PathType Container) {
            Copy-Item -LiteralPath $source -Destination $bundle -Recurse
        }
    }
}
Write-Output $bundle
if (!$Publish -and !$Preview) { return }

$targets = @($components | Where-Object { $Update -eq 'Both' -or $_.Name -eq $Update })
$targets += @{ Name = 'Crimean Tom'; Path = $bundle }
foreach ($target in $targets) {
    $arguments = @('workshop-update', '--mod-dir', $target.Path, '--json')
    if ($target.Previews) { $arguments += @('--preview-dir', $target.Previews) }
    if ($ChangeLog) { $arguments += @('--changelog', $ChangeLog) }
    $target.Arguments = $arguments
    & $cli @arguments
    if ($LASTEXITCODE -ne 0) { throw "Workshop preview failed for $($target.Name)." }
}
if (!$Publish) { return }
foreach ($target in $targets) {
    $arguments = $target.Arguments
    & $cli @arguments --confirm
    if ($LASTEXITCODE -ne 0) { throw "Workshop update failed for $($target.Name). Check workshop-status before retrying." }
}
