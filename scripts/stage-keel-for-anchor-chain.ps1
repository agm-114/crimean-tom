[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KeelRepository,
    [Parameter(Mandatory)][string]$KeelRef,
    [Parameter(Mandatory)][string]$AnchorChainPackage,
    [Parameter(Mandatory)][string]$GameDir
)

$ErrorActionPreference = 'Stop'
$target = 'https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757'
$repo = Split-Path -Parent $PSScriptRoot
$keel = (Resolve-Path -LiteralPath $KeelRepository).Path
$game = (Resolve-Path -LiteralPath $GameDir).Path
if (!(Test-Path -LiteralPath (Join-Path $game 'Sea Power.exe') -PathType Leaf)) {
    throw 'GameDir must point to a Sea Power installation.'
}

$original = Get-Item -LiteralPath $AnchorChainPackage
if (!$original.PSIsContainer -or ($original.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Use the downloaded Anchor Chain package directory or a copy of it.'
}
$originalFiles = @(Get-ChildItem -LiteralPath $original.FullName -Force | Sort-Object Name)
$expected = '_info.ini', 'AnchorChain.dll', 'thumbnail.png' | Sort-Object
if (($originalFiles.Name -join ',') -cne ($expected -join ',')) {
    throw 'The stock Anchor Chain package layout changed. Review its files before staging.'
}
foreach ($file in $originalFiles) {
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected a regular file: $($file.FullName)"
    }
}
$ini = Get-Content -LiteralPath (Join-Path $original.FullName '_info.ini') -Raw
$workshop = [regex]::Match($ini, '(?m)^\[Workshop\]\s*\r?\n(?<values>[^\[]*)').Groups['values'].Value
if ($workshop -notmatch ('(?m)^Url\s*=\s*' + [regex]::Escape($target) + '\s*$') -or
    $workshop -notmatch '(?m)^Title\s*=\s*Anchor Chain\s*$') {
    throw 'The stock package must pin the Anchor Chain Workshop URL and title.'
}

$commit = git -C $keel rev-parse --verify --end-of-options "$KeelRef^{commit}"
if ($LASTEXITCODE -ne 0) { throw "Could not resolve Keel ref $KeelRef." }
$stage = Join-Path $repo "artifacts/workshop/anchor-chain-$($commit.Substring(0, 12))-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$archive = Join-Path $stage 'source.zip'
$source = Join-Path $stage 'source'
$candidate = Join-Path $stage 'candidate'
$rollback = Join-Path $stage 'rollback'
$bootstrap = Join-Path $stage 'bootstrap'
New-Item -ItemType Directory -Path $stage | Out-Null
git -C $keel archive --format=zip --output=$archive $commit
if ($LASTEXITCODE -ne 0) { throw "Could not archive Keel commit $commit." }
Expand-Archive -LiteralPath $archive -DestinationPath $source

$builds = [ordered]@{
    'SeaPowerKeel' = 'netstandard2.1/SeaPowerKeel.dll'
    'SeaPowerKeel.Patcher' = 'netstandard2.1/SeaPowerKeel.Patcher.dll'
    'SeaPowerKeel.Watcher' = 'net48/KeelWatcher.exe'
    'SeaPowerKeel.Stub' = 'netstandard2.1/SeaPowerKeel.Stub.dll'
}
Push-Location -LiteralPath ([IO.Path]::GetTempPath())
try {
    foreach ($project in $builds.Keys) {
        & dotnet build (Join-Path $source "src/$project/$project.csproj") -c Release "-p:GameDir=$game" -p:Deploy=false --nologo -v:q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Keel build failed for $project." }
    }
} finally {
    Pop-Location
}

New-Item -ItemType Directory -Path $candidate, $rollback, $bootstrap, (Join-Path $candidate 'lib') | Out-Null
foreach ($file in $originalFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination $rollback
    Copy-Item -LiteralPath (Join-Path $rollback $file.Name) -Destination $candidate
}
foreach ($project in $builds.Keys) {
    $destination = if ($project -eq 'SeaPowerKeel.Stub') { $bootstrap } else { $candidate }
    Copy-Item -LiteralPath (Join-Path $source "src/$project/bin/Release/$($builds[$project])") -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $candidate 'Keel-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $source 'lib/AnchorChain.keel.bin'), (Join-Path $source 'lib/THIRD-PARTY.txt') -Destination (Join-Path $candidate 'lib')

$files = @(Get-ChildItem -LiteralPath $stage -Directory |
    Where-Object Name -In 'candidate', 'rollback', 'bootstrap' |
    Get-ChildItem -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
$manifest = [ordered]@{
    workshopUrl = $target
    keelCommit = $commit
    candidate = $candidate
    rollback = $rollback
    bootstrap = $bootstrap
    files = $files
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
Write-Warning 'Local test package. The installed Anchor Chain preloader still starts the original AnchorChain.dll. Keel needs its bootstrap.'
[pscustomobject]$manifest | Select-Object workshopUrl, keelCommit, candidate, rollback, bootstrap
