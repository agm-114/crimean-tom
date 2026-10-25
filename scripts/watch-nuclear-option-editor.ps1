param([string]$Source)

$project = Join-Path $PSScriptRoot '../tools/NuclearOption.ScenarioEditor/NuclearOption.ScenarioEditor.csproj'
$env:UseSharedCompilation = 'false'
$watchArguments = @('watch', '--no-hot-reload', '--project', $project, 'run', '--no-restore')
if ($Source) { $watchArguments += @('--', $Source) }

# WPF XAML changes need a new window; watch rebuilds and restarts the app.
& dotnet @watchArguments
exit $LASTEXITCODE
