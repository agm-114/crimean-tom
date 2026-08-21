$ErrorActionPreference = 'Stop'

# Polling avoids Windows directory notification overflow. Full process restarts
# are intentionally used instead of Roslyn hot reload: Vite's generated chunks
# live beneath the service project and can otherwise crash the hot-reload
# workspace while a hosted monitor build is being replaced.
$env:DOTNET_USE_POLLING_FILE_WATCHER = '1'

& dotnet watch `
    --no-hot-reload `
    --project 'src/CrimeanTom.Campaign.ServiceHost/CrimeanTom.Campaign.ServiceHost.csproj' `
    run `
    --configuration Debug `
    --no-launch-profile
exit $LASTEXITCODE
