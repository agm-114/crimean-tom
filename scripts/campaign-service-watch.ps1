$ErrorActionPreference = 'Stop'

# Watch only the MSBuild-discovered source set. Windows directory notifications
# can overflow when Vite atomically replaces its generated production chunks
# beneath the service project, even though those files are excluded by MSBuild.
$env:DOTNET_USE_POLLING_FILE_WATCHER = '1'

& dotnet watch `
    --project 'src/CrimeanTom.Campaign.ServiceHost/CrimeanTom.Campaign.ServiceHost.csproj' `
    run `
    --configuration Debug `
    --no-launch-profile
exit $LASTEXITCODE
