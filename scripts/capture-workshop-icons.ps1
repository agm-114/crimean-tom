#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('all', 'controls', 'settings')][string]$Mod = 'all',
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/mod-icons'),
    [string]$Cli = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/SeaPower.Cli/bin/Debug/net10.0/seapower-cli.exe'),
    [string]$AnchorChainDll
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This capture uses the Windows Steam installation of Sea Power.' }
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$Cli = [IO.Path]::GetFullPath($Cli)

function Invoke-GameCli {
    $response = & $Cli @args --format json
    if ($LASTEXITCODE -ne 0) { throw "seapower-cli failed: $($args -join ' ')" }
    $response | ConvertFrom-Json
}

function Find-Controls([string]$Where) {
    Invoke-GameCli inspect --where $Where
}

function Wait-Control([string]$Where, [int]$TimeoutSeconds = 45) {
    $timeout = [Diagnostics.Stopwatch]::StartNew()
    do {
        $snapshot = Find-Controls $Where
        if (@($snapshot.results).Count -eq 1) { return $snapshot }
        Start-Sleep -Milliseconds 400
    } while ($timeout.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Expected one visible control matching: $Where"
}

function Select-Tab($Snapshot, [string]$Title) {
    $control = $Snapshot.results[0]
    $tab = $control.options | Where-Object text -eq $Title
    if (!$tab) { throw "Could not find the '$Title' tab." }
    $null = Invoke-GameCli select --snapshot $Snapshot.snapshot --id $control.id --index $tab.index
}

function Save-Icon([string]$Name, [Drawing.Rectangle]$Crop) {
    $shot = Invoke-GameCli screenshot
    if ($shot.width -ne 1920 -or $shot.height -ne 1200) {
        throw "This framing uses a 1920 × 1200 game view. The game captured $($shot.width) × $($shot.height)."
    }
    $path = Join-Path $OutputDirectory "$Name.jpg"
    $source = [Drawing.Bitmap]::FromFile($shot.path)
    $output = [Drawing.Bitmap]::new(512, 512, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [Drawing.Graphics]::FromImage($output)
    $encoding = [Drawing.Imaging.EncoderParameters]::new(1)
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, 512, 512), $Crop, [Drawing.GraphicsUnit]::Pixel)
        $encoding.Param[0] = [Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality, [long]96)
        $codec = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
        $output.Save($path, $codec, $encoding)
        Write-Output $path
    } finally {
        $encoding.Dispose()
        $graphics.Dispose()
        $output.Dispose()
        $source.Dispose()
    }
}

function Build-Gallery([string]$Stage, [string]$GameDirectory) {
    $bridgeSource = Join-Path $root 'src/SeaPower.GameBridge'
    Copy-Item -Path "$bridgeSource/*.cs" -Destination $Stage
    Copy-Item -LiteralPath "$PSScriptRoot/workshop-icon-gallery/UiGallery.cs" -Destination $Stage -Force
    $project = Join-Path $Stage 'SeaPower.GameBridge.csproj'
    [xml]$xml = Get-Content -LiteralPath "$bridgeSource/SeaPower.GameBridge.csproj" -Raw
    foreach ($item in $xml.SelectNodes('//*[@Include or @Project]')) {
        $attribute = if ($item.HasAttribute('Include')) { 'Include' } else { 'Project' }
        $path = $item.GetAttribute($attribute)
        if ($path.StartsWith('../')) {
            $item.SetAttribute($attribute, [IO.Path]::GetFullPath((Join-Path $bridgeSource $path)))
        }
    }
    $settings = $xml.CreateElement('Import')
    $settings.SetAttribute('Project', "$root/src/SeaPower.SharedSettings/SeaPower.SharedSettings.props")
    $null = $xml.DocumentElement.AppendChild($settings)
    $xml.Save($project)
    $buildArgs = @($project, "-p:DirectoryBuildPropsPath=$root/src/Directory.Build.props",
        "-p:SeaPowerDir=$GameDirectory", '-p:DeployToGame=false', '--nologo')
    if ($AnchorChainDll) { $buildArgs += "-p:AnchorChainDll=$AnchorChainDll" }
    Push-Location $Stage
    try {
        & dotnet build @buildArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not build the icon gallery.' }
    } finally { Pop-Location }
    Join-Path $Stage 'bin/Debug/SeaPower.GameBridge.dll'
}

if (!(Test-Path -LiteralPath $Cli -PathType Leaf)) { throw "Build SeaPower.Cli first or provide -Cli. Missing: $Cli" }
if ($AnchorChainDll) { $AnchorChainDll = (Resolve-Path -LiteralPath $AnchorChainDll).Path }
$game = Invoke-GameCli game-status
if (!$game.executableExists -or $game.processIds) { throw 'Install Sea Power and close it before running the capture.' }
$bridge = Join-Path $game.gameDirectory 'Sea Power_Data/StreamingAssets/SeaPowerGameBridge/SeaPower.GameBridge.dll'
if (!(Test-Path -LiteralPath $bridge -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path (Split-Path $bridge) 'enabled.txt') -PathType Leaf)) {
    throw 'Install and enable the Sea Power Game Bridge first.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = (New-Item -ItemType Directory -Path ([IO.Path]::Combine([IO.Path]::GetTempPath(), "seapower-icons-$([guid]::NewGuid().ToString('N'))"))).FullName
$backup = Join-Path $stage 'original-bridge.dll'
$restoreBridge = $false
$launchTime = $null
try {
    if ($Mod -ne 'settings') {
        $gallery = Build-Gallery $stage $game.gameDirectory
        Copy-Item -LiteralPath $bridge -Destination $backup
        $restoreBridge = $true
        Copy-Item -LiteralPath $gallery -Destination $bridge -Force
    }

    $launchTime = Get-Date
    $null = Invoke-GameCli launch --confirm --timeout 80 --require-pattern '\[Sea Power Game Bridge\] Enabled local command queue'
    $news = Find-Controls 'Enabled && Name == "CloseButton" && Text == "X"'
    if (@($news.results).Count -eq 1) {
        $null = Invoke-GameCli click --snapshot $news.snapshot --id $news.results[0].id
    }

    if ($Mod -ne 'controls') {
        $null = Invoke-GameCli menu options
        Select-Tab (Wait-Control 'Type == "TabControl" && Name == ""') 'Gameplay'
        Select-Tab (Wait-Control 'Name == "ModGameplayTabs"') 'Deck Tweaks'
        $options = Wait-Control 'Name == "WeaponMountSecondsOverrides"'
        $null = Invoke-GameCli expand --snapshot $options.snapshot --id $options.results[0].id
        Save-Icon 'settings' ([Drawing.Rectangle]::new(98, 58, 940, 940))
        if ($Mod -eq 'all') { $null = Invoke-GameCli menu main }
    }

    if ($Mod -ne 'settings') {
        $null = Invoke-GameCli menu encyclopedia --confirm
        $titles = Wait-Control 'Text == "Unit Reference" && Type == "TextBlock"'
        $reference = $titles.results[0]
        $ui = Invoke-GameCli inspect
        $parents = @{}
        foreach ($control in $ui.controls) { $parents[[int]$control.id] = $control }
        $buttons = @($ui.controls | Where-Object { $_.name -eq 'CloseButton' -and $_.enabled })
        $ancestors = @{}
        for ($node = $parents[[int]$reference.parent]; $node; $node = $parents[[int]$node.parent]) {
            $ancestors[[int]$node.id] = $ancestors.Count
        }
        # The nearest shared ancestor is in the reference panel's title bar.
        $candidates = foreach ($button in $buttons) {
            for ($node = $parents[[int]$button.parent]; $node; $node = $parents[[int]$node.parent]) {
                if (!$ancestors.ContainsKey([int]$node.id)) { continue }
                [pscustomobject]@{ Id = $button.id; Distance = $ancestors[[int]$node.id] }
                break
            }
        }
        $ranked = @($candidates | Sort-Object Distance)
        if (!$ranked -or ($ranked.Count -gt 1 -and $ranked[0].Distance -eq $ranked[1].Distance)) {
            throw 'Could not identify the Unit Reference close button.'
        }
        $null = Invoke-GameCli click --snapshot $ui.snapshot --id $ranked[0].Id
        $null = Invoke-GameCli ui-gallery --confirm
        $null = Wait-Control 'Name == "SharedIconSettings"'
        Save-Icon 'controls' ([Drawing.Rectangle]::new(654, 248, 640, 640))
    }
} finally {
    if ($launchTime) {
        $process = Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $game.executable -and $_.StartTime -ge $launchTime }
        if ($process -and (!$process.CloseMainWindow() -or !$process.WaitForExit(20000))) {
            throw "Close Sea Power to restore the bridge from $backup."
        }
    }
    if ($restoreBridge) {
        Copy-Item -LiteralPath $backup -Destination $bridge -Force
        if ((Get-FileHash -LiteralPath $backup).Hash -ne (Get-FileHash -LiteralPath $bridge).Hash) {
            throw "Could not restore the bridge. Original DLL: $backup"
        }
    }
    if ((Resolve-Path -LiteralPath $stage).Path -eq $stage -and
        (Split-Path $stage -Parent) -eq [IO.Path]::GetTempPath().TrimEnd('\')) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
