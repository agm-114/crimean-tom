param(
    [ValidateRange(1, 100)]
    [int]$ForceMultiplier = 20,
    [ValidateRange(10, 10000)]
    [long]$TargetQuanta = 30,
    [string]$ServiceUrl = 'http://127.0.0.1:5081',
    [string]$TokenPath = '.run/campaign-service/service.token'
)

$ErrorActionPreference = 'Stop'
$token = (Get-Content -LiteralPath $TokenPath -Raw).Trim()
$headers = @{ Authorization = "Bearer $token" }
$campaignId = [Guid]::NewGuid().ToString('N')
$createBody = @{
    campaignId = $campaignId
    name = "Fulda Gap Stress x$ForceMultiplier"
    seed = 19890911
    catalogFingerprint = "fulda-gap-1989-stress-x$ForceMultiplier"
} | ConvertTo-Json -Compress

$bootstrapWatch = [Diagnostics.Stopwatch]::StartNew()
Invoke-RestMethod `
    -Method Post `
    -Uri "$ServiceUrl/control/campaigns" `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body $createBody | Out-Null
$seed = Invoke-RestMethod `
    -Method Post `
    -Uri "$ServiceUrl/control/campaigns/$campaignId/bootstrap/fulda-gap/$ForceMultiplier" `
    -Headers $headers
$bootstrapWatch.Stop()

$advanceWatch = [Diagnostics.Stopwatch]::StartNew()
Invoke-RestMethod `
    -Method Post `
    -Uri "$ServiceUrl/control/campaigns/$campaignId/advance/$TargetQuanta" `
    -Headers $headers | Out-Null
$advanceWatch.Stop()

$view = Invoke-RestMethod `
    -Method Get `
    -Uri "$ServiceUrl/campaigns/$campaignId" `
    -Headers $headers

[ordered]@{
    campaignId = $campaignId
    forceMultiplier = $seed.forceMultiplier
    regions = $view.regions.Count
    entities = $view.entities.Count
    groups = $view.groups.Count
    outcomes = $view.recentOperationalOutcomes.Count
    targetQuanta = $TargetQuanta
    bootstrapMilliseconds = [math]::Round($bootstrapWatch.Elapsed.TotalMilliseconds, 1)
    advanceMilliseconds = [math]::Round($advanceWatch.Elapsed.TotalMilliseconds, 1)
    serviceAdvanceMicroseconds = $view.performance.advance.lastDurationMicroseconds
    measuredQuantaPerSecond = [math]::Round(
        $view.performance.advance.lastWorkUnitsPerSecondMilliunits / 1000,
        1)
    recoveryMicroseconds = $view.performance.recovery.lastDurationMicroseconds
    managedMemoryMiB = [math]::Round($view.performance.managedMemoryBytes / 1MB, 1)
    workingSetMiB = [math]::Round($view.performance.workingSetBytes / 1MB, 1)
    canonicalHash = $view.canonicalHash
} | ConvertTo-Json
