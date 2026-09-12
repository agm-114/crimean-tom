param(
    [Parameter(Mandatory)][ValidateSet('DeckTweaks', 'Quickload', 'BoatOps')][string]$Mod,
    [Parameter(Mandatory)][string]$GameDirectory
)
$ErrorActionPreference = 'Stop'
$gamePath = [IO.Path]::GetFullPath($GameDirectory)
if (!(Test-Path -LiteralPath (Join-Path $gamePath 'Sea Power.exe'))) { throw 'Sea Power installation not found.' }
if (Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) { throw 'Close Sea Power before deploying.' }
$legacyName = @{ DeckTweaks = 'FlightDeckTweaks'; BoatOps = 'WetDeckControls'; Quickload = 'Quickload' }[$Mod]
$legacyDll = @{ DeckTweaks = 'FlightDeckTweaks.dll'; BoatOps = 'WetDeckControls.dll'; Quickload = 'SeaPower.StartupTweaks.dll' }[$Mod]
$assetsPath = Join-Path $gamePath 'Sea Power_Data/StreamingAssets'
$oldPath = [IO.Path]::GetFullPath((Join-Path $assetsPath $legacyName))
$newPath = [IO.Path]::GetFullPath((Join-Path $assetsPath $Mod))
$prefix = [IO.Path]::GetFullPath($assetsPath).TrimEnd('\') + '\'
if (!$oldPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
    !$newPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Deployment path is outside StreamingAssets.' }
if ($oldPath -ne $newPath -and (Test-Path -LiteralPath $oldPath)) {
    if (Test-Path -LiteralPath $newPath) { throw "Both $legacyName and $Mod exist. Merge their settings before deploying." }
    Move-Item -LiteralPath $oldPath -Destination $newPath
}
$oldDllPath = Join-Path $newPath $legacyDll
if (Test-Path -LiteralPath $oldDllPath) {
    # Retain the old binary without letting AnchorChain load it a second time.
    Move-Item -LiteralPath $oldDllPath -Destination ($oldDllPath + '.' + [Guid]::NewGuid().ToString('N') + '.disabled')
}
