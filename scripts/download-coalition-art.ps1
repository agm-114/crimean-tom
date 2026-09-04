[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $PSScriptRoot 'coalition-art.sources.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$targetDirectory = Join-Path $repositoryRoot $manifest.targetDirectory

New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
Add-Type -AssemblyName System.Drawing

$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.DefaultRequestHeaders.UserAgent.ParseAdd('CrimeanTom-coalition-art-downloader/1.0')

try {
    foreach ($asset in $manifest.assets) {
        $destinationPath = Join-Path $targetDirectory $asset.fileName

        if (Test-Path -LiteralPath $destinationPath) {
            Write-Host "Present: $($asset.fileName)"
            continue
        }

        Write-Host "Downloading: $($asset.fileName)"
        $sourceBytes = $httpClient.GetByteArrayAsync([string]$asset.downloadUrl).GetAwaiter().GetResult()
        $sourceHash = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($sourceBytes))

        if ($sourceHash -ne $asset.sourceSha256) {
            throw "Hash mismatch for $($asset.fileName). The upstream file may have changed."
        }

        $temporaryPath = "$destinationPath.download"

        try {
            if ($asset.outputWidth -and $asset.outputHeight) {
                $sourceStream = [System.IO.MemoryStream]::new($sourceBytes)
                try {
                    $sourceImage = [System.Drawing.Image]::FromStream($sourceStream)
                    try {
                        $outputImage = [System.Drawing.Bitmap]::new(
                            [int]$asset.outputWidth,
                            [int]$asset.outputHeight,
                            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                        try {
                            $graphics = [System.Drawing.Graphics]::FromImage($outputImage)
                            try {
                                $graphics.Clear([System.Drawing.Color]::Transparent)
                                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
                                $graphics.DrawImage(
                                    $sourceImage,
                                    0,
                                    0,
                                    [int]$asset.outputWidth,
                                    [int]$asset.outputHeight)
                            }
                            finally {
                                $graphics.Dispose()
                            }

                            $outputImage.Save(
                                $temporaryPath,
                                [System.Drawing.Imaging.ImageFormat]::Png)
                        }
                        finally {
                            $outputImage.Dispose()
                        }
                    }
                    finally {
                        $sourceImage.Dispose()
                    }
                }
                finally {
                    $sourceStream.Dispose()
                }
            }
            else {
                [System.IO.File]::WriteAllBytes($temporaryPath, $sourceBytes)
            }

            Move-Item -LiteralPath $temporaryPath -Destination $destinationPath
        }
        catch {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
            throw
        }
    }
}
finally {
    $httpClient.Dispose()
}
