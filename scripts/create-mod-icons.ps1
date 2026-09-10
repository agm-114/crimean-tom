#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('all', 'deck-tweaks', 'quickload')][string]$Mod = 'all',
    [ValidateRange(256, 2048)][int]$Size = 1024,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/mod-icons')
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This script uses Windows System.Drawing.' }
Add-Type -AssemblyName System.Drawing
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$sourceDirectory = Join-Path $OutputDirectory 'sources'
New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
$assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mod-icons.sources.json') -Raw | ConvertFrom-Json
$jpeg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'

foreach ($asset in $assets | Where-Object { $Mod -eq 'all' -or $_.id -eq $Mod }) {
    $source = Join-Path $sourceDirectory ($asset.id + '.jpg')
    if (!(Test-Path -LiteralPath $source)) {
        $download = $source + '.download'
        try {
            Invoke-WebRequest -Uri $asset.downloadUrl -OutFile $download -TimeoutSec 60 -UserAgent 'SeaPowerModIcons/1.0'
            if ((Get-FileHash -LiteralPath $download).Hash -ne $asset.sha256) { throw "Source changed: $($asset.id). Review the source before updating its hash." }
            Move-Item -LiteralPath $download -Destination $source
        } finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
        }
    }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $asset.sha256) { throw "Cached photo has an unexpected hash: $source" }

    $resources = [Collections.Generic.List[IDisposable]]::new()
    $temporary = Join-Path $OutputDirectory ($asset.id + '.tmp')
    try {
        $photo = [Drawing.Image]::FromFile($source)
        $resources.Add($photo)
        $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $resources.Add($bitmap)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $resources.Add($graphics)
        $graphics.Clear([Drawing.Color]::FromArgb(35, 38, 40))
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        # Centre-crop into the photo panel without stretching either source.
        $photoHeight = [int]($Size * 0.78)
        $scale = [Math]::Max($Size / $photo.Width, $photoHeight / $photo.Height)
        $cropWidth = [single]($Size / $scale)
        $cropHeight = [single]($photoHeight / $scale)
        $crop = [Drawing.RectangleF]::new(($photo.Width - $cropWidth) / 2, ($photo.Height - $cropHeight) / 2, $cropWidth, $cropHeight)
        $graphics.DrawImage($photo, [Drawing.RectangleF]::new(0, 0, $Size, $photoHeight), $crop, [Drawing.GraphicsUnit]::Pixel)
        $accent = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(190, 72, 58))
        $resources.Add($accent)
        $graphics.FillRectangle($accent, 0, $photoHeight, $Size, [Math]::Max(3, [int]($Size * 0.008)))
        $font = [Drawing.Font]::new('Bahnschrift', [single]($Size * 0.105), [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
        $resources.Add($font)
        $format = [Drawing.StringFormat]::new()
        $resources.Add($format)
        $format.Alignment = [Drawing.StringAlignment]::Center
        $format.LineAlignment = [Drawing.StringAlignment]::Center
        $format.FormatFlags = [Drawing.StringFormatFlags]::NoWrap
        $graphics.DrawString($asset.title, $font, [Drawing.Brushes]::White,
            [Drawing.RectangleF]::new($Size * 0.04, $photoHeight, $Size * 0.92, $Size - $photoHeight), $format)
        $encoding = [Drawing.Imaging.EncoderParameters]::new(1)
        $resources.Add($encoding)
        $encoding.Param[0] = [Drawing.Imaging.EncoderParameter]::new([Drawing.Imaging.Encoder]::Quality, [long]92)
        $bitmap.Save($temporary, $jpeg, $encoding)
        $destination = Join-Path $OutputDirectory ($asset.id + '.jpg')
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        [PSCustomObject]@{ Mod = $asset.title; Image = $destination; Width = $Size; Height = $Size; Bytes = (Get-Item -LiteralPath $destination).Length }
    } finally {
        for ($index = $resources.Count - 1; $index -ge 0; $index--) { $resources[$index].Dispose() }
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
