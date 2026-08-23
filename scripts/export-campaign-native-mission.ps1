[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{32}$')]
    [string]$CampaignId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{32}$')]
    [string]$BlueCoalitionId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{32}$')]
    [string]$RedCoalitionId,

    [string]$CampaignUrl = 'http://127.0.0.1:5081',

    [string]$CampaignTokenPath = '.run/campaign-service/service.token',

    [string]$SeaPowerDir = $(
        if ($env:SEA_POWER_DIR) { $env:SEA_POWER_DIR }
        else { 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power' }
    ),

    [ValidateRange(1, 10000)]
    [int]$MaximumNativeEntities = 10000,

    [string]$MissionName = '',

    [string]$OutputPath = 'content/missions/Crimean Tom - Native Campaign Benchmark.generated.ini',

    [string]$ManifestPath = '.run/performance/native-campaign-benchmark-manifest.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Token([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Campaign token file not found at '$Path'."
    }
    (Get-Content -LiteralPath $Path -Raw).Trim()
}

function Resolve-UnitDefinition([string]$CatalogUnitId, [string]$OriginalContentRoot) {
    foreach ($folder in @('vessels', 'aircraft', 'land_units')) {
        $candidate = Join-Path (Join-Path $OriginalContentRoot $folder) "$CatalogUnitId.ini"
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }
        $match = [regex]::Match(
            (Get-Content -LiteralPath $candidate -Raw),
            '(?m)^\s*UnitType\s*=\s*([^\s/]+)')
        if (-not $match.Success) {
            throw "Unit definition '$candidate' has no UnitType."
        }
        return [pscustomobject]@{
            catalogUnitId = $CatalogUnitId
            path = $candidate
            unitType = $match.Groups[1].Value
        }
    }
    $null
}

function Resolve-MissionCategory([string]$UnitType) {
    switch ($UnitType) {
        'Vessel' { 'Vessel' }
        'Submarine' { 'Submarine' }
        'Aircraft' { 'Aircraft' }
        'VTOL' { 'Aircraft' }
        'Helicopter' { 'Helicopter' }
        'LandUnit' { 'LandUnit' }
        default { throw "Unsupported Sea Power UnitType '$UnitType'." }
    }
}

function Resolve-Altitude([string]$Category) {
    switch ($Category) {
        'Submarine' { 'deep' }
        'Aircraft' { '20000' }
        'Helicopter' { '500' }
        default { '0' }
    }
}

function Resolve-Telegraph([string]$Category) {
    if ($Category -in @('Aircraft', 'Helicopter')) { return 2 }
    0
}

function Escape-IniText([string]$Value) {
    ($Value -replace '[\r\n|]', ' ').Trim()
}

$streamingAssets = Join-Path $SeaPowerDir 'Sea Power_Data\StreamingAssets'
$originalContentRoot = Join-Path $streamingAssets 'original'
if (-not (Test-Path -LiteralPath $originalContentRoot -PathType Container)) {
    throw "Sea Power original content was not found at '$originalContentRoot'."
}

$token = Read-Token $CampaignTokenPath
$headers = @{ Authorization = "Bearer $token" }
$view = Invoke-RestMethod `
    -Method Get `
    -Uri "$CampaignUrl/campaigns/$CampaignId" `
    -Headers $headers `
    -TimeoutSec 60

$coalitions = @($view.entities | ForEach-Object coalitionId | Sort-Object -Unique)
foreach ($required in @($BlueCoalitionId, $RedCoalitionId)) {
    if ($required -notin $coalitions) {
        throw "Coalition '$required' is not present in campaign '$CampaignId'."
    }
}

$definitions = @{}
$missingCatalogIds = [Collections.Generic.List[string]]::new()
foreach ($catalogUnitId in @($view.entities | ForEach-Object catalogUnitId | Sort-Object -Unique)) {
    $definition = Resolve-UnitDefinition $catalogUnitId $originalContentRoot
    if ($null -eq $definition) {
        $missingCatalogIds.Add($catalogUnitId)
        continue
    }
    $definitions[$catalogUnitId] = $definition
}

$eligible = @($view.entities |
    Where-Object {
        $_.lifecycle -notin @('Destroyed', 'Retired') `
            -and $definitions.ContainsKey([string]$_.catalogUnitId) `
            -and $_.coalitionId -in @($BlueCoalitionId, $RedCoalitionId)
    } |
    Sort-Object entityId |
    Select-Object -First $MaximumNativeEntities)
if ($eligible.Count -eq 0) {
    throw 'No campaign entities resolved to native Sea Power definitions.'
}
$resolvedMissionName = if ([string]::IsNullOrWhiteSpace($MissionName)) {
    "Crimean Tom - Native Campaign Benchmark - $($eligible.Count) Units"
}
else {
    $MissionName.Trim()
}

$placed = [Collections.Generic.List[object]]::new()
$categoryCounters = @{}
foreach ($entity in $eligible) {
    $definition = $definitions[[string]$entity.catalogUnitId]
    $category = Resolve-MissionCategory $definition.unitType
    $side = if ($entity.coalitionId -eq $BlueCoalitionId) { 'Taskforce1' } else { 'Taskforce2' }
    $counterKey = "$side|$category"
    $priorCount = if ($categoryCounters.ContainsKey($counterKey)) {
        [int]$categoryCounters[$counterKey]
    }
    else {
        0
    }
    $categoryCounters[$counterKey] = 1 + $priorCount
    $index = $categoryCounters[$counterKey]

    # Retain campaign geography in one global Sea Power scene. Longitude is
    # compressed by cos(latitude) so high-latitude fleets are not spread wider
    # than their campaign coordinates imply.
    $latitude = [double]$entity.latitudeMicrodegrees / 1000000d
    $longitude = [double]$entity.longitudeMicrodegrees / 1000000d
    $eastNm = $longitude * 60d * [Math]::Cos($latitude * [Math]::PI / 180d)
    $northNm = $latitude * 60d
    $placed.Add([pscustomobject]@{
        entity = $entity
        side = $side
        category = $category
        index = $index
        eastNm = $eastNm
        northNm = $northNm
    })
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('[Debug]')
$lines.Add('DisableEnemyAIPlayer=True')
$lines.Add('')
$lines.Add('[Language_en]')
$lines.Add("Name=$(Escape-IniText $resolvedMissionName)")
$lines.Add("Description=Generated all-native comparison for campaign $CampaignId. Every resolvable, non-terminal campaign entity is a native Sea Power simulation object.")
$lines.Add('Taskforce1StartMessage=CRIMEAN TOM // NATIVE CAMPAIGN BENCHMARK|This generated mission intentionally instantiates the campaign force as native Sea Power objects for performance comparison. AI is disabled and weapons are held to measure steady-state simulation cost.|Begin benchmark')
$lines.Add('')
$lines.Add('[Environment]')
$lines.Add('Date=1989,9,11')
$lines.Add('Time=4,30')
$lines.Add('ConvertTimeToLocal=False')
$lines.Add('SeaState=2')
$lines.Add('Clouds=Scattered')
$lines.Add('WindDirection=SW')
$lines.Add('Weather=Clear')
$lines.Add('MapCenterLatitude=0')
$lines.Add('MapCenterLongitude=0')
$lines.Add('LoadBackgroundData=False')
$lines.Add('')
$lines.Add('[Mission]')
$lines.Add('Difficulty=3')
$lines.Add('PlayerTaskforce=Taskforce1')
$lines.Add('EnemyTaskforce=Taskforce2')
$lines.Add('Taskforce1_RulesOfEngagement=Restricted')
$lines.Add('Taskforce2_RulesOfEngagement=Restricted')
foreach ($side in @('Taskforce1', 'Taskforce2')) {
    foreach ($category in @('LandUnit', 'Vessel', 'Submarine', 'Aircraft', 'Helicopter')) {
        $plural = switch ($category) {
            'LandUnit' { 'LandUnits' }
            'Aircraft' { 'Aircraft' }
            default { "${category}s" }
        }
        $counterKey = "$side|$category"
        $count = if ($categoryCounters.ContainsKey($counterKey)) {
            [int]$categoryCounters[$counterKey]
        }
        else {
            0
        }
        $lines.Add("NumberOf$side$plural=$count")
    }
}
$lines.Add('NumberOfTriggers=1')
$lines.Add('')

foreach ($item in $placed) {
    $entity = $item.entity
    $category = $item.category
    $lines.Add("[$($item.side)$category$($item.index)]")
    $lines.Add("Type=$($entity.catalogUnitId)")
    if ($category -in @('Aircraft', 'Helicopter')) {
        $lines.Add('SquadronReference=Default')
        $lines.Add('MissionType=NoMission')
    }
    else {
        $lines.Add('VariantReference=Default')
    }
    if ($category -ne 'LandUnit' -and
        -not [string]::IsNullOrWhiteSpace([string]$entity.catalogLoadoutId)) {
        $lines.Add("LoadoutVariant=$($entity.catalogLoadoutId)")
    }
    $lines.Add("Name=$(Escape-IniText ([string]$entity.name))")
    $lines.Add('WeaponStatus=Hold')
    $lines.Add('RadarsActive=False')
    $lines.Add('ActiveSonarsEnabled=False')
    $lines.Add('CrewSkill=Trained')
    $lines.Add(('RelativePositionInNM={0:F4},{1},{2:F4}' -f
        $item.eastNm,
        (Resolve-Altitude $category),
        $item.northNm))
    $lines.Add("Telegraph=$(Resolve-Telegraph $category)")
    $lines.Add(('Heading={0:F3}' -f ([double]$entity.headingMilliDegrees / 1000d)))
    $lines.Add('')
}

$lines.Add('[Trigger1]')
$lines.Add('Name=Native campaign benchmark notice')
$lines.Add('Action_Taskforce1_Message=Taskforce1StartMessage')
$lines.Add('Condition_Condition1_Type=Time')
$lines.Add('Condition_Condition1_Time=2')
$lines.Add('ConditionsCompleted=<Condition1>')

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputFullPath) | Out-Null
$lines | Set-Content -LiteralPath $outputFullPath -Encoding utf8

$manifest = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    campaignId = $CampaignId
    campaignCommitPosition = [long]$view.commitPosition
    campaignTimeQuanta = [long]$view.timeQuanta
    campaignEntityCount = @($view.entities).Count
    maximumNativeEntities = $MaximumNativeEntities
    missionName = $resolvedMissionName
    exportedNativeEntityCount = $placed.Count
    excludedTerminalEntityCount = @($view.entities | Where-Object lifecycle -in @('Destroyed', 'Retired')).Count
    excludedMissingDefinitionEntityCount = @($view.entities | Where-Object catalogUnitId -in $missingCatalogIds).Count
    missingCatalogUnitIds = @($missingCatalogIds | Sort-Object)
    counts = [ordered]@{}
    outputPath = $outputFullPath
}
foreach ($group in @($placed | Group-Object side, category | Sort-Object Name)) {
    $manifest.counts[$group.Name] = $group.Count
}
$manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifestFullPath) | Out-Null
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestFullPath -Encoding utf8
$manifest | ConvertTo-Json -Depth 8
