[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KeelRepository,
    [Parameter(Mandatory)][string]$KeelRef,
    [Parameter(Mandatory)][string]$AnchorChainRepository,
    [Parameter(Mandatory)][string]$AnchorChainRef,
    [Parameter(Mandatory)][string]$AnchorChainPackage,
    [Parameter(Mandatory)][string]$GameDir
)

$ErrorActionPreference = 'Stop'
$target = 'https://steamcommunity.com/sharedfiles/filedetails/?id=3380210757'
$repo = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameDir).Path
if (!(Test-Path -LiteralPath (Join-Path $game 'Sea Power.exe') -PathType Leaf)) {
    throw 'GameDir must point to a Sea Power installation.'
}

$original = Get-Item -LiteralPath $AnchorChainPackage
if (!$original.PSIsContainer -or ($original.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Use the downloaded Anchor Chain package directory or a copy of it.'
}
$originalFiles = @(Get-ChildItem -LiteralPath $original.FullName -Force | Sort-Object Name)
$required = '_info.ini', 'AnchorChain.dll', 'thumbnail.png'
$runtimeFiles = 'AnchorChain.Runtime.dll', 'AnchorChainWatcher.exe', 'LICENSE-AnchorChain.md', 'LICENSE-Keel.txt'
if (@($required | Where-Object { $_ -cnotin $originalFiles.Name }).Count -gt 0 -or
    @($originalFiles | Where-Object { $_.Name -cnotin ($required + $runtimeFiles) }).Count -gt 0) {
    throw 'The Anchor Chain package layout changed. Review its files before staging.'
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

function Export-Source($repository, $ref, $name) {
    $path = (Resolve-Path -LiteralPath $repository).Path
    $commit = git -C $path rev-parse --verify --end-of-options "$ref^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "Could not resolve $name ref $ref." }
    $archive = Join-Path $stage "$name.zip"
    $source = Join-Path $stage $name
    git -C $path archive --format=zip --output=$archive $commit
    if ($LASTEXITCODE -ne 0) { throw "Could not archive $name commit $commit." }
    Expand-Archive -LiteralPath $archive -DestinationPath $source
    @{ Path = $source; Commit = $commit }
}

$stage = Join-Path $repo "artifacts/workshop/anchor-chain-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
$candidate = Join-Path $stage 'content'
$rollback = Join-Path $stage 'rollback'
New-Item -ItemType Directory -Path $stage | Out-Null
$keel = Export-Source $KeelRepository $KeelRef 'keel'
$anchor = Export-Source $AnchorChainRepository $AnchorChainRef 'anchor-chain'

foreach ($dll in 'Seapower-Scripts.dll', 'Noesis.NoesisGUI.dll', 'UnityEngine.ParticleSystemModule.dll') {
    Copy-Item -LiteralPath (Join-Path $game "Sea Power_Data/Managed/$dll") -Destination (Join-Path $anchor.Path 'Dll')
}
$builds = @(
    (Join-Path $anchor.Path 'AnchorChain/AnchorChain.csproj')
    (Join-Path $keel.Path 'src/SeaPowerKeel/SeaPowerKeel.csproj')
    (Join-Path $keel.Path 'src/SeaPowerKeel.Watcher/SeaPowerKeel.Watcher.csproj')
)
Push-Location -LiteralPath ([IO.Path]::GetTempPath())
try {
    foreach ($project in $builds) {
        & dotnet build $project -c Release "-p:GameDir=$game" -p:Deploy=false --nologo -v:q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $project." }
    }
} finally {
    Pop-Location
}

New-Item -ItemType Directory -Path $candidate, $rollback | Out-Null
foreach ($file in $originalFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination $rollback
}
Copy-Item -LiteralPath "$rollback/_info.ini", "$rollback/thumbnail.png" -Destination $candidate
Copy-Item -LiteralPath (Join-Path $anchor.Path 'AnchorChain/bin/Release/netstandard2.1/AnchorChain.dll'),
    (Join-Path $keel.Path 'src/SeaPowerKeel/bin/Release/netstandard2.1/AnchorChain.Runtime.dll'),
    (Join-Path $keel.Path 'src/SeaPowerKeel.Watcher/bin/Release/net48/AnchorChainWatcher.exe') -Destination $candidate
Copy-Item -LiteralPath (Join-Path $anchor.Path 'LICENSE.md') -Destination (Join-Path $candidate 'LICENSE-AnchorChain.md')
Copy-Item -LiteralPath (Join-Path $keel.Path 'LICENSE') -Destination (Join-Path $candidate 'LICENSE-Keel.txt')

$files = @(Get-ChildItem -LiteralPath $candidate, $rollback -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
$manifest = [ordered]@{
    workshopUrl = $target
    keelCommit = $keel.Commit
    anchorChainCommit = $anchor.Commit
    content = $candidate
    rollback = $rollback
    files = $files
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
[pscustomobject]$manifest | Select-Object workshopUrl, keelCommit, anchorChainCommit, content, rollback
