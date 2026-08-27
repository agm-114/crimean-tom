[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run')]
    [ValidateSet('Combined')]
    [string]$Case = 'Combined',

    [Parameter(ParameterSetName = 'Run')]
    [ValidateSet('Native', 'Streamed')]
    [string]$Mode = 'Native',

    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(5, 600)]
    [int]$DurationSeconds = 60,

    [Parameter(ParameterSetName = 'Run')]
    [ValidateSet(1, 2, 3, 5, 10, 20, 30, 50, 100)]
    [double]$TimeCompression = 20,

    [Parameter(ParameterSetName = 'Run')]
    [string]$RunDirectory = '.run',

    [Parameter(ParameterSetName = 'Run')]
    [string]$OutputPath = '',

    [Parameter(ParameterSetName = 'Compare', Mandatory = $true)]
    [string]$NativePath,

    [Parameter(ParameterSetName = 'Compare', Mandatory = $true)]
    [string]$StreamedPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'tests\scenarios\combat-parity.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

function Resolve-AbsolutePath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Floor(($sorted.Count - 1) * $Percentile)
    return $sorted[$index]
}

if ($PSCmdlet.ParameterSetName -eq 'Compare') {
    $native = Get-Content -LiteralPath (Resolve-AbsolutePath $NativePath) -Raw | ConvertFrom-Json
    $streamed = Get-Content -LiteralPath (Resolve-AbsolutePath $StreamedPath) -Raw | ConvertFrom-Json
    if ($native.case -ne $streamed.case) {
        throw "Capture cases do not match: '$($native.case)' and '$($streamed.case)'."
    }

    $comparison = [ordered]@{
        schemaVersion = 1
        case = $native.case
        nativePath = Resolve-AbsolutePath $NativePath
        streamedPath = Resolve-AbsolutePath $StreamedPath
        native = [ordered]@{
            detectedHostile = $native.observations.detectedHostile
            launchedWeapon = $native.observations.launchedWeapon
            inflictedDamage = $native.observations.inflictedDamage
            destroyedUnit = $native.observations.destroyedUnit
            airToLandEngagement = $native.observations.airToLandEngagement
            gbadEngagement = $native.observations.gbadEngagement
            carrierPresent = $native.observations.carrierPresent
            carrierAirOperations = $native.observations.carrierAirOperations
            helicopterPresent = $native.observations.helicopterPresent
            subsurfacePresent = $native.observations.subsurfacePresent
            civilianTrafficPresent = $native.observations.civilianTrafficPresent
            firstDetectionSimulationSeconds = $native.observations.firstDetectionSimulationSeconds
            firstWeaponSimulationSeconds = $native.observations.firstWeaponSimulationSeconds
            maximumActiveWeapons = $native.observations.maximumActiveWeapons
            completedFocusSequence = @($native.focusPhases | Where-Object { $_.focus.outcome -ne 'completed' }).Count -eq 0
        }
        streamed = [ordered]@{
            detectedHostile = $streamed.observations.detectedHostile
            launchedWeapon = $streamed.observations.launchedWeapon
            inflictedDamage = $streamed.observations.inflictedDamage
            destroyedUnit = $streamed.observations.destroyedUnit
            airToLandEngagement = $streamed.observations.airToLandEngagement
            gbadEngagement = $streamed.observations.gbadEngagement
            carrierPresent = $streamed.observations.carrierPresent
            carrierAirOperations = $streamed.observations.carrierAirOperations
            hostedAircraftBecameActive = $streamed.observations.hostedAircraftBecameActive
            helicopterPresent = $streamed.observations.helicopterPresent
            subsurfacePresent = $streamed.observations.subsurfacePresent
            civilianTrafficPresent = $streamed.observations.civilianTrafficPresent
            firstDetectionSimulationSeconds = $streamed.observations.firstDetectionSimulationSeconds
            firstWeaponSimulationSeconds = $streamed.observations.firstWeaponSimulationSeconds
            maximumActiveWeapons = $streamed.observations.maximumActiveWeapons
            allNativeInitialized = $streamed.observations.allNativeInitialized
            allAcceptPlayerOrders = $streamed.observations.allAcceptPlayerOrders
            allHaveNativeControl = $streamed.observations.allHaveNativeControl
            completedFocusSequence = @($streamed.focusPhases | Where-Object { $_.focus.outcome -ne 'completed' }).Count -eq 0
        }
    }
    $comparison['parity'] = [ordered]@{
        detection = [bool]$native.observations.detectedHostile -eq [bool]$streamed.observations.detectedHostile
        weaponLaunch = [bool]$native.observations.launchedWeapon -eq [bool]$streamed.observations.launchedWeapon
        damage = [bool]$native.observations.inflictedDamage -eq [bool]$streamed.observations.inflictedDamage
        destruction = [bool]$native.observations.destroyedUnit -eq [bool]$streamed.observations.destroyedUnit
        airToLandEngagement = [bool]$native.observations.airToLandEngagement -eq [bool]$streamed.observations.airToLandEngagement
        gbadEngagement = [bool]$native.observations.gbadEngagement -eq [bool]$streamed.observations.gbadEngagement
        carrierPresence = $comparison.native.carrierPresent -and $comparison.streamed.carrierPresent
        carrierAirOperations = $comparison.native.carrierAirOperations -and $comparison.streamed.carrierAirOperations
        helicopterPresence = $comparison.native.helicopterPresent -and $comparison.streamed.helicopterPresent
        subsurfacePresence = $comparison.native.subsurfacePresent -and $comparison.streamed.subsurfacePresent
        civilianTraffic = $comparison.native.civilianTrafficPresent -and $comparison.streamed.civilianTrafficPresent
        focusSequence = $comparison.native.completedFocusSequence -and $comparison.streamed.completedFocusSequence
        streamedCombatReady = [bool]$streamed.observations.allNativeInitialized `
            -and [bool]$streamed.observations.allAcceptPlayerOrders `
            -and [bool]$streamed.observations.allHaveNativeControl
    }
    $comparison['passed'] = @($comparison.parity.Values) -notcontains $false
    $comparison | ConvertTo-Json -Depth 8
    exit $(if ($comparison.passed) { 0 } else { 1 })
}

$runPath = Resolve-AbsolutePath $RunDirectory
$tokenPath = Join-Path $runPath 'control.token'
if (-not (Test-Path -LiteralPath $tokenPath)) {
    throw "No companion token exists at '$tokenPath'. Start the companion and Sea Power first."
}
$token = (Get-Content -LiteralPath $tokenPath -Raw).Trim()
$headers = @{ 'X-Crimean-Tom-Token' = $token }
$controlUrl = 'http://127.0.0.1:5137'

function Invoke-ControlGet([string]$Path) {
    return Invoke-RestMethod "$controlUrl$Path" -Headers $headers
}

function Invoke-ControlPost([string]$Path, [object]$Body) {
    try {
        return Invoke-RestMethod "$controlUrl$Path" `
            -Method Post `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body ($Body | ConvertTo-Json -Depth 12)
    } catch {
        $detail = if ($null -ne $_.ErrorDetails) { $_.ErrorDetails.Message } else { '' }
        if ([string]::IsNullOrWhiteSpace($detail) -and $null -ne $_.Exception.Response) {
            try {
                $responseStream = $_.Exception.Response.GetResponseStream()
                $reader = [System.IO.StreamReader]::new($responseStream)
                $detail = $reader.ReadToEnd()
                $reader.Dispose()
            } catch {
                $detail = ''
            }
        }
        if ([string]::IsNullOrWhiteSpace($detail)) { $detail = $_.Exception.Message }
        throw "POST $Path failed: $detail"
    }
}

function Wait-ForScenario([string]$ScenarioId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 250
        $scene = Invoke-ControlGet '/game/scene'
        if ($scene.sceneName -eq 'mission' -and $scene.gameMainAvailable) {
            $world = Invoke-ControlGet '/game/world'
            if ($world.scenarioId -ne $ScenarioId) {
                continue
            }
            $ui = Invoke-ControlGet '/game/ui'
            if ($ui.currentWindowType -eq 'MissionBriefView') {
                $null = Invoke-ControlPost '/game/automation/briefing/continue' @{}
            }
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Scenario '$ScenarioId' did not become ready within 60 seconds."
}

function Exit-CurrentScenario {
    $scene = Invoke-ControlGet '/game/scene'
    if ($scene.sceneName -ne 'mission') { return }

    $null = Invoke-ControlPost '/game/automation/tactical/exit' @{}
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $scene = Invoke-ControlGet '/game/scene'
        if ($scene.sceneName -ne 'mission') {
            $ui = Invoke-ControlGet '/game/ui'
            if ($ui.currentWindowType -in @('MainMenuView', 'ScenarioMenuView')) { return }
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Sea Power did not return to an actionable main menu within 30 seconds.'
}

function Start-Scenario([string]$ScenarioId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try {
            return Invoke-ControlPost '/game/automation/scenario/start' ([ordered]@{
                scenarioId = $ScenarioId
                idempotencyKey = "combat-parity-start-$([Guid]::NewGuid().ToString('N'))"
            })
        } catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)
}

function Materialize-StreamedUnits([object[]]$Units, [string]$RunInstanceId) {
    $health = Invoke-ControlGet '/health'
    $armDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        try {
            $capabilities = Invoke-ControlGet '/game/capabilities'
            if ($capabilities.runtimeFingerprint.fileIdentityState -ne 'ready') {
                throw "Runtime fingerprint is '$($capabilities.runtimeFingerprint.fileIdentityState)'."
            }
            $null = Invoke-ControlPost '/game/mutations/arm' ([ordered]@{
                sessionId = $health.sessionId
                runtimeFingerprintSha256 = $capabilities.runtimeFingerprint.fingerprintSha256
                confirmation = 'enable-owned-mutations'
                durationSeconds = 300
            })
            break
        } catch {
            if ([DateTime]::UtcNow -ge $armDeadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $armDeadline)

    $index = 0
    $materializedEntityIds = [System.Collections.Generic.List[string]]::new()
    $orderedUnits = @($Units | Sort-Object @{ Expression = {
        if ($_.platformKind -eq 'land') { 0 } else { 1 }
    } })
    foreach ($unit in $orderedUnits) {
        Set-TacticalPause $true
        $index++
        $entityId = "$($unit.entityId):run:$RunInstanceId"
        $missionType = if ($unit.PSObject.Properties.Name -contains 'missionType') {
            $unit.missionType
        } else { 'Patrol' }
        $request = [ordered]@{
            context = [ordered]@{
                sessionId = $health.sessionId
                assignmentId = "assignment:combat-parity-$($Case.ToLowerInvariant())-$RunInstanceId-$index"
                authorityEpoch = 1
                expectedRevision = 0
                idempotencyKey = "combat-parity-$($Case.ToLowerInvariant())-$index-$([Guid]::NewGuid().ToString('N'))"
                dryRun = $false
            }
            entityId = $entityId
            displayName = $unit.displayName
            unitTypeId = $unit.unitTypeId
            variantId = $unit.variantId
            loadoutId = $unit.loadoutId
            taskforceId = $unit.taskforceId
            relationship = $unit.relationship
            latitude = $unit.latitude
            longitude = $unit.longitude
            headingDegrees = $unit.headingDegrees
            telegraph = $unit.telegraph
            weaponStatus = 'Free'
            missionType = $missionType
            radarsActive = $unit.platformKind -in @('surface', 'air', 'helicopter', 'land')
        }
        $mutationPath = '/game/mutations/land/materialize'
        if ($unit.platformKind -ne 'land') {
            $mutationPath = '/game/mutations/surface/materialize'
            $request['altitudeFeet'] = $unit.altitudeFeet
            $request['activeSonarsEnabled'] = $unit.platformKind -eq 'helicopter' -or
                $unit.unitTypeId -eq 'wp_skr_krivak2'
            $request['towedArrayDeployed'] = $unit.platformKind -in @('surface', 'subsurface')
            if ($unit.platformKind -in @('air', 'helicopter')) {
                $request['fuelFractionMilliunits'] = 1000000
            }
            if ($unit.PSObject.Properties.Name -contains 'targetEntityId') {
                $request['initialTargetEntityId'] = "$($unit.targetEntityId):run:$RunInstanceId"
                $request['initialShotsToFire'] = 2
            }
        } else {
            $terrainDeadline = [DateTime]::UtcNow.AddSeconds(30)
            $terrain = $null
            $terrainProbe = $null
            do {
                Set-TacticalPause $true
                try {
                    $terrain = Invoke-ControlPost '/game/terrain/prepare' ([ordered]@{
                        sessionId = $health.sessionId
                        latitude = $unit.latitude
                        longitude = $unit.longitude
                    })
                    if (-not $terrain.terrain.placementReady) {
                        Start-Sleep -Milliseconds 250
                        $terrainProbe = Invoke-ControlPost '/game/terrain/probe' ([ordered]@{
                            latitude = $unit.latitude
                            longitude = $unit.longitude
                        })
                        if ($terrainProbe.placementReady) {
                            break
                        }
                    } else {
                        break
                    }
                } catch {
                    if ([DateTime]::UtcNow -ge $terrainDeadline) { throw }
                    Start-Sleep -Milliseconds 250
                }
            } while ([DateTime]::UtcNow -lt $terrainDeadline)
            if ($null -eq $terrain -or
                -not $terrain.terrain.placementReady -and -not $terrainProbe.placementReady) {
                throw "Terrain preparation for '$entityId' did not finish within 30 seconds."
            }
        }
        $materializeDeadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Set-TacticalPause $true
            try {
                $result = Invoke-ControlPost $mutationPath $request
                break
            } catch {
                if ($_.Exception.Message -notmatch 'fully loaded,\s+paused tactical mission' -or
                    [DateTime]::UtcNow -ge $materializeDeadline) {
                    throw
                }
                Start-Sleep -Milliseconds 100
            }
        } while ([DateTime]::UtcNow -lt $materializeDeadline)
        if ($result.outcome -notin @('materialized', 'materialized-from-portable-checkpoint')) {
            throw "Materialization of '$entityId' returned '$($result.outcome)'."
        }
        $materializedEntityIds.Add($entityId)
    }
    $firstMobile = @($orderedUnits | Where-Object platformKind -ne 'land')[0]
    $null = Invoke-ControlPost '/game/terrain/prepare' ([ordered]@{
        sessionId = $health.sessionId
        latitude = $firstMobile.latitude
        longitude = $firstMobile.longitude
    })
    return $materializedEntityIds.ToArray()
}

function Schedule-HostedAircraftLaunch([object]$Aircraft, [string]$RunInstanceId) {
    Set-TacticalPause $true
    $health = Invoke-ControlGet '/health'
    $entityId = "$($Aircraft.entityId):run:$RunInstanceId"
    $hostEntityId = "$($Aircraft.hostEntityId):run:$RunInstanceId"
    $assignmentId = "assignment:combat-parity-hosted-$RunInstanceId"
    $registration = Invoke-ControlPost '/game/mutations/hosted-aircraft/register' ([ordered]@{
        context = [ordered]@{
            sessionId = $health.sessionId
            assignmentId = $assignmentId
            authorityEpoch = 1
            expectedRevision = 0
            idempotencyKey = "combat-parity-hosted-register-$([Guid]::NewGuid().ToString('N'))"
            dryRun = $false
        }
        entityId = $entityId
        displayName = $Aircraft.displayName
        hostEntityId = $hostEntityId
        unitTypeId = $Aircraft.unitTypeId
        variantId = $Aircraft.variantId
        loadoutId = $Aircraft.loadoutId
        fuelFractionMilliunits = 1000000
    })
    if ($registration.outcome -ne 'registered-on-board') {
        throw "Hosted aircraft registration returned '$($registration.outcome)'."
    }
    $launch = Invoke-ControlPost '/game/mutations/hosted-aircraft/launch' ([ordered]@{
        context = [ordered]@{
            sessionId = $health.sessionId
            assignmentId = $assignmentId
            authorityEpoch = 1
            expectedRevision = $registration.revision
            idempotencyKey = "combat-parity-hosted-launch-$([Guid]::NewGuid().ToString('N'))"
            dryRun = $false
        }
        entityId = $entityId
    })
    return [ordered]@{
        entityId = $entityId
        unitTypeId = $Aircraft.unitTypeId
        registration = $registration
        launch = $launch
    }
}

function Set-TacticalPause([bool]$IsPaused) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        try {
            $null = Invoke-ControlPost '/game/experimental/pause' @{ isPaused = $IsPaused }
            $ui = Invoke-ControlGet '/game/ui'
            if ([bool]$ui.isPaused -eq $IsPaused) { return }
        } catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Sea Power did not reach tactical pause state '$IsPaused' within 10 seconds."
}

function Set-ObservationFocus([string]$PhaseName, [string]$UnitTypeId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $world = Invoke-ControlGet '/game/world'
        $matches = @($world.units | Where-Object { $_.unitTypeId -eq $UnitTypeId })
        if ($matches.Count -gt 0) {
            $target = $matches[0]
            try {
                $result = Invoke-ControlPost '/game/automation/camera/focus' @{ unitId = $target.id }
                return [ordered]@{
                    name = $PhaseName
                    unitId = $target.id
                    unitTypeId = $target.unitTypeId
                    simulationTimeSeconds = $world.simulationTimeSeconds
                    outcome = $result.outcome
                    postcondition = $result.postcondition
                }
            } catch {
                if ([DateTime]::UtcNow -ge $deadline) { throw }
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Could not find '$UnitTypeId' for the '$PhaseName' observation phase within 60 seconds."
}

$caseDefinition = $manifest.cases.$Case
$scenarioId = if ($Mode -eq 'Native') {
    $caseDefinition.nativeScenarioId
} else {
    $caseDefinition.streamedScenarioId
}

$health = Invoke-ControlGet '/health'
if (-not $health.gameConnected) {
    throw 'Sea Power is not connected to the companion.'
}

Exit-CurrentScenario
$null = Start-Scenario $scenarioId
Wait-ForScenario $scenarioId

$runInstanceId = [Guid]::NewGuid().ToString('N')
$materializedEntityIds = @()
$hostedLaunches = @()
if ($Mode -eq 'Streamed') {
    Set-TacticalPause $true
    $materializedEntityIds = @(Materialize-StreamedUnits @($caseDefinition.units) $runInstanceId)
    $hostedLaunches = @($caseDefinition.hostedAircraft | ForEach-Object {
        Schedule-HostedAircraftLaunch $_ $runInstanceId
    })
}

$focusDefinitions = @($caseDefinition.focusSequence)
$firstFocus = Set-ObservationFocus $focusDefinitions[0].name $focusDefinitions[0].unitTypeId
$baselineSimulationTime = [double]$firstFocus.simulationTimeSeconds
$focusPhases = [System.Collections.Generic.List[object]]::new()
$focusPhases.Add([ordered]@{
    name = $focusDefinitions[0].name
    focus = $firstFocus
    elapsedSimulationStartSeconds = 0
})
$nextFocusIndex = 1
$initialOwned = if ($Mode -eq 'Streamed') {
    Invoke-ControlGet '/game/mutations/surface/resources'
} else { $null }
$null = Invoke-ControlPost '/game/experimental/time-compression' @{ timeCompression = $TimeCompression }
if ($Mode -eq 'Streamed') {
    Set-TacticalPause $false
}
$startedAt = [DateTimeOffset]::UtcNow
$deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
$focusIntervalSeconds = [Math]::Min(1.5, $DurationSeconds / [Math]::Max(1, $focusDefinitions.Count))
$focusSwitchAt = [DateTime]::UtcNow.AddSeconds($focusIntervalSeconds)
$samples = [System.Collections.Generic.List[object]]::new()
$firstSimulationTime = $null
$firstDetection = $null
$firstWeapon = $null
$maximumActiveWeapons = 0
$inflictedDamage = $false
$destroyedUnit = $false
$airToLandEngagement = $false
$gbadEngagement = $false
$carrierPresent = $false
$helicopterPresent = $false
$subsurfacePresent = $false
$civilianTrafficPresent = $false
$hostedAircraftBecameActive = $false

try {
    do {
        $world = Invoke-ControlGet '/game/world'
        if ($nextFocusIndex -lt $focusDefinitions.Count -and [DateTime]::UtcNow -ge $focusSwitchAt) {
            $definition = $focusDefinitions[$nextFocusIndex]
            $focus = Set-ObservationFocus $definition.name $definition.unitTypeId
            $focusPhases.Add([ordered]@{
                name = $definition.name
                focus = $focus
                elapsedSimulationStartSeconds = [double]$focus.simulationTimeSeconds - $baselineSimulationTime
            })
            $nextFocusIndex++
            $focusSwitchAt = [DateTime]::UtcNow.AddSeconds($focusIntervalSeconds)
        }
        if ($null -eq $firstSimulationTime) {
            $firstSimulationTime = [double]$world.simulationTimeSeconds
        }
        $hostileContacts = @($world.contacts | Where-Object {
            -not $_.isOwnForce -and $_.relationship -eq 'Hostile'
        })
        $weapons = @($world.weapons | Where-Object {
            $_.weaponType -notin @('Noisemaker', 'Decoy') -and
            -not [string]::IsNullOrWhiteSpace($_.intendedTargetId)
        })
        $units = @($world.units | Where-Object {
            $_.unitTypeId -in @($caseDefinition.units.unitTypeId)
        })
        $worldUnitsById = @{}
        foreach ($worldUnit in @($world.units)) {
            $worldUnitsById[$worldUnit.id] = $worldUnit
        }
        if ($hostileContacts.Count -gt 0 -and $null -eq $firstDetection) {
            $firstDetection = [double]$world.simulationTimeSeconds - $baselineSimulationTime
        }
        if ($weapons.Count -gt 0 -and $null -eq $firstWeapon) {
            $firstWeapon = [double]$world.simulationTimeSeconds - $baselineSimulationTime
        }
        $maximumActiveWeapons = [Math]::Max($maximumActiveWeapons, $weapons.Count)
        $inflictedDamage = $inflictedDamage -or @($units | Where-Object {
            $_.damageFraction -gt 0 -or $_.overallDamagePoints -gt 0
        }).Count -gt 0
        $destroyedUnit = $destroyedUnit -or @($units | Where-Object isDestroyed).Count -gt 0
        foreach ($weapon in $weapons) {
            $launcher = $worldUnitsById[$weapon.launchPlatformId]
            $target = $worldUnitsById[$weapon.intendedTargetId]
            if ($null -ne $launcher -and $null -ne $target) {
                $launcherDefinition = @($caseDefinition.units | Where-Object unitTypeId -eq $launcher.unitTypeId)[0]
                $targetDefinition = @($caseDefinition.units | Where-Object unitTypeId -eq $target.unitTypeId)[0]
                $airToLandEngagement = $airToLandEngagement -or (
                    $launcherDefinition.platformKind -in @('air', 'helicopter') -and
                    $targetDefinition.platformKind -eq 'land')
                $gbadEngagement = $gbadEngagement -or (
                    $launcherDefinition.platformKind -eq 'land' -and
                    $targetDefinition.platformKind -in @('air', 'helicopter'))
            }
        }
        $carrierPresent = $carrierPresent -or @($units | Where-Object unitTypeId -eq 'usn_cv_forrestal_75').Count -gt 0
        $helicopterPresent = $helicopterPresent -or @($units | Where-Object unitTypeId -eq 'usn_sh-2f').Count -gt 0
        $subsurfacePresent = $subsurfacePresent -or @($units | Where-Object {
            $_.unitTypeId -in @('usn_ssn_los_angeles', 'wp_ssn_victor3')
        }).Count -ge 2
        $civilianTrafficPresent = $civilianTrafficPresent -or @($world.units | Where-Object {
            $_.unitTypeId -in @('civ_ms_bulk', 'civ_fv_dhow')
        }).Count -ge 2
        $hostedAircraftBecameActive = $hostedAircraftBecameActive -or @($world.units | Where-Object {
            $_.unitTypeId -in @($caseDefinition.hostedAircraft.unitTypeId)
        }).Count -gt 0
        $samples.Add([ordered]@{
            capturedAtUnixMilliseconds = $world.capturedAtUnixMilliseconds
            simulationTimeSeconds = $world.simulationTimeSeconds
            measuredFramesPerSecond = $world.measuredFramesPerSecond
            fixedDeltaTime = $world.engineFixedDeltaTime
            internalTimeDilation = $world.internalTimeDilation
            hostileContactCount = $hostileContacts.Count
            activeWeaponCount = $weapons.Count
            activeWeapons = @($weapons | ForEach-Object {
                [ordered]@{
                    id = $_.id
                    weaponType = $_.weaponType
                    ammunitionId = $_.ammunitionId
                    launchPlatformId = $_.launchPlatformId
                    intendedTargetId = $_.intendedTargetId
                }
            })
            units = @($units | ForEach-Object {
                $ammunitionMeasure = @($_.ammunition | Measure-Object quantity -Sum)
                [ordered]@{
                    id = $_.id
                    taskforceId = $_.taskforceId
                    unitTypeId = $_.unitTypeId
                    latitude = $_.latitude
                    longitude = $_.longitude
                    altitudeFeet = $_.altitudeFeet
                    headingDegrees = $_.headingDegrees
                    speedKnots = $_.speedKnots
                    damageFraction = $_.damageFraction
                    isDestroyed = $_.isDestroyed
                    ammunitionQuantity = if ($ammunitionMeasure.Count -eq 0) {
                        0
                    } else {
                        $ammunitionMeasure[0].Sum
                    }
                }
            })
        })
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
}
finally {
    try {
        $null = Invoke-ControlPost '/game/experimental/time-compression' @{ timeCompression = 1 }
    } catch {
        Write-Warning "Could not restore x1 time compression: $($_.Exception.Message)"
    }
}

$owned = if ($Mode -eq 'Streamed') {
    Invoke-ControlGet '/game/mutations/surface/resources'
} else { $null }
$ownedRunEntities = if ($null -eq $owned) { @() } else {
    @($owned.entities | Where-Object { $_.entityId -in $materializedEntityIds })
}
$initialOwnedRunEntities = if ($null -eq $initialOwned) { @() } else {
    @($initialOwned.entities | Where-Object { $_.entityId -in $materializedEntityIds })
}
$friendlyEntityIds = @($caseDefinition.units | Where-Object relationship -eq 'Friendly' | ForEach-Object {
    "$($_.entityId):run:$runInstanceId"
})
$initialOwnedFriendlyRunEntities = @($initialOwnedRunEntities | Where-Object {
    $_.entityId -in $friendlyEntityIds
})
$fpsValues = @($samples | ForEach-Object { [double]$_.measuredFramesPerSecond })
$capture = [ordered]@{
    schemaVersion = 1
    case = $Case
    mode = $Mode
    runInstanceId = $runInstanceId
    scenarioId = $scenarioId
    gameVersion = $health.game.gameVersion
    startedAtUnixMilliseconds = $startedAt.ToUnixTimeMilliseconds()
    wallDurationSeconds = ([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds
    requestedTimeCompression = $TimeCompression
    focusPhases = $focusPhases
    initialOwnedEntities = $initialOwnedRunEntities
    ownedEntities = $ownedRunEntities
    hostedLaunches = $hostedLaunches
    observations = [ordered]@{
        detectedHostile = $null -ne $firstDetection
        launchedWeapon = $null -ne $firstWeapon
        inflictedDamage = $inflictedDamage
        destroyedUnit = $destroyedUnit
        airToLandEngagement = $airToLandEngagement
        gbadEngagement = $gbadEngagement
        carrierPresent = $carrierPresent
        helicopterPresent = $helicopterPresent
        subsurfacePresent = $subsurfacePresent
        civilianTrafficPresent = $civilianTrafficPresent
        carrierAirOperations = $carrierPresent -and (
            $Mode -eq 'Native' -or
            @($hostedLaunches | Where-Object { $_.launch.outcome -eq 'launch-scheduled' }).Count -gt 0)
        hostedAircraftBecameActive = if ($Mode -eq 'Native') { $null } else {
            $hostedAircraftBecameActive
        }
        firstDetectionSimulationSeconds = $firstDetection
        firstWeaponSimulationSeconds = $firstWeapon
        maximumActiveWeapons = $maximumActiveWeapons
        allNativeInitialized = if ($null -eq $owned) { $null } else {
            $initialOwnedRunEntities.Count -eq $materializedEntityIds.Count -and
                @($initialOwnedRunEntities | Where-Object { -not $_.nativeInitialized }).Count -eq 0
        }
        allAcceptPlayerOrders = if ($null -eq $owned) { $null } else {
            $initialOwnedFriendlyRunEntities.Count -eq $friendlyEntityIds.Count -and
                @($initialOwnedFriendlyRunEntities | Where-Object { -not $_.acceptsPlayerOrders }).Count -eq 0
        }
        allHaveNativeControl = if ($null -eq $owned) { $null } else {
            $initialOwnedRunEntities.Count -eq $materializedEntityIds.Count -and
                @($initialOwnedRunEntities | Where-Object { -not $_.hasNativeControl }).Count -eq 0
        }
        meanFramesPerSecond = ($fpsValues | Measure-Object -Average).Average
        p05FramesPerSecond = Get-Percentile $fpsValues 0.05
    }
    samples = $samples
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $captureDirectory = Join-Path $repoRoot '.run\combat-parity'
    New-Item -ItemType Directory -Path $captureDirectory -Force | Out-Null
    $OutputPath = Join-Path $captureDirectory "$($Case.ToLowerInvariant())-$($Mode.ToLowerInvariant()).json"
}
$resolvedOutput = Resolve-AbsolutePath $OutputPath
$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$capture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
[ordered]@{
    outputPath = $resolvedOutput
    case = $Case
    mode = $Mode
    observations = $capture.observations
} | ConvertTo-Json -Depth 8
