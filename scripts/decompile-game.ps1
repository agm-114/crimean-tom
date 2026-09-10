param(
    [string]$GameDirectory = $(if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR } else { 'C:/Program Files (x86)/Steam/steamapps/common/Sea Power' }),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../decompiled/game'),
    [string[]]$Assemblies = @('Seapower-Scripts', 'Assets', 'Libraries', 'Plugins'),
    [switch]$AllManaged
)
$ErrorActionPreference = 'Stop'
$decompiler = Get-Command ilspycmd -ErrorAction SilentlyContinue
if (!$decompiler) { throw 'Install ILSpy first: dotnet tool install --global ilspycmd' }
$managed = Join-Path $GameDirectory 'Sea Power_Data/Managed'
if (!(Test-Path -LiteralPath $managed -PathType Container)) { throw "Managed assemblies not found: $managed" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$versionOutput = @(& $decompiler.Source --version)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read ILSpy version.' }
$toolVersion = $versionOutput[0]

$files = if ($AllManaged) {
    Get-ChildItem -LiteralPath $managed -Filter '*.dll' | Sort-Object Name
} else {
    foreach ($name in $Assemblies) {
        if ([IO.Path]::GetFileName($name) -ne $name) { throw 'Use assembly names, not paths.' }
        $filename = if ($name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { $name } else { "$name.dll" }
        Get-Item -LiteralPath (Join-Path $managed $filename)
    }
}

foreach ($file in $files) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $destination = Join-Path $output "$($file.BaseName)/$($hash.Substring(0, 12))"
    $manifest = Join-Path $destination 'decompilation.json'
    if (Test-Path -LiteralPath $manifest) {
        $saved = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($saved.sha256 -eq $hash -and $saved.ilspy -eq $toolVersion) {
            Write-Host "Already decompiled: $destination"
            continue
        }
    }
    # Preserve partial output and older tool results instead of overwriting them.
    if (Test-Path -LiteralPath $destination) {
        $destination += '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $manifest = Join-Path $destination 'decompilation.json'
    }
    Write-Host "Decompiling $($file.Name) to $destination"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $decompiler.Source $file.FullName --project --nested-directories --referencepath $managed --outputdir $destination --disable-updatecheck
    if ($LASTEXITCODE -ne 0) { throw "Decompilation failed for $($file.Name). Partial output: $destination" }
    @{
        assembly = $file.FullName
        sha256 = $hash
        ilspy = $toolVersion
        createdUtc = [DateTime]::UtcNow.ToString('O')
        seconds = $timer.Elapsed.TotalSeconds
    } | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
    Write-Host "Finished in $([Math]::Round($timer.Elapsed.TotalSeconds, 1)) seconds."
}
