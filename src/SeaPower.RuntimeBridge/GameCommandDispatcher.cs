using System.Diagnostics;
using System.Text.Json;

using CrimeanTom.Protocol;

using SeaPower;

using SeapowerUI;
using SeapowerUI.ViewModels;

using UnityEngine;
using UnityEngine.SceneManagement;

namespace SeaPowerRuntimeBridge;

internal static class GameCommandDispatcher
{
    private const string ExperimentalProbeSaveFileName = "_CrimeanTomPhase1Probe.sav";
    private const string ScreenshotFileName = "_CrimeanTomScreenshot.png";
    private static Action? _deferredAction;
    private static Action? _endOfFrameAction;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static WireEnvelope Execute(
        WireEnvelope command,
        int initializedMainThreadId,
        RuntimeFileFingerprintProvider fileFingerprintProvider,
        string executionThreadGuardSelfTest)
    {
        CommandResponsePayload result;
        var executingThreadId = System.Environment.CurrentManagedThreadId;

        try
        {
            if (command.ProtocolVersion != ProtocolConstants.Version)
            {
                result = Failure(
                    ProtocolErrorCodes.ProtocolVersionMismatch,
                    $"Game supports protocol {ProtocolConstants.Version}, received {command.ProtocolVersion}.");
            }
            else if (!string.Equals(command.Kind, MessageKinds.Command, StringComparison.Ordinal))
            {
                result = Failure(
                    ProtocolErrorCodes.InvalidMessageKind,
                    $"Expected '{MessageKinds.Command}'.");
            }
            else if (string.IsNullOrWhiteSpace(command.SessionId)
                || string.IsNullOrWhiteSpace(command.MessageId)
                || string.IsNullOrWhiteSpace(command.MessageType))
            {
                result = Failure(
                    ProtocolErrorCodes.InvalidMessage,
                    "Command session, message ID, and message type are required.");
            }
            else if (command.DeadlineUnixMilliseconds is null
                || command.DeadlineUnixMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                result = Failure(
                    ProtocolErrorCodes.CommandExpired,
                    "The command deadline elapsed before Unity could execute it.");
            }
            else if (executingThreadId != initializedMainThreadId)
            {
                result = Failure(
                    ProtocolErrorCodes.WrongExecutionThread,
                    "The command was not executed on Unity's initialized main thread.");
            }
            else
            {
                result = command.MessageType switch
                {
                    GameCommandTypes.Ping => Success(new PongPayload
                    {
                        PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
                        ProcessId = Process.GetCurrentProcess().Id,
                        InitializedMainThreadId = initializedMainThreadId,
                        ExecutingThreadId = executingThreadId,
                        IsMainThread = executingThreadId == initializedMainThreadId,
                        RespondedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }),
                    GameCommandTypes.GetCapabilities => Success(RuntimeCapabilityCollector.Capture(
                        fileFingerprintProvider,
                        executionThreadGuardSelfTest)),
                    GameCommandTypes.GetSceneState => Success(GetSceneState(
                        initializedMainThreadId,
                        executingThreadId)),
                    GameCommandTypes.GetWorldSnapshot => Success(GetWorldSnapshot()),
                    GameCommandTypes.GetTacticalCouplingState => Success(
                        GetTacticalCouplingState()),
                    GameCommandTypes.GetOwnedSurfaceResourceState =>
                        Success(RuntimeOwnedMutationAdapter.CaptureResourceState()),
                    GameCommandTypes.GetUnitCatalogPage => Success(GetUnitCatalogPage(
                        command.PayloadJson,
                        fileFingerprintProvider)),
                    GameCommandTypes.GetReferenceCatalogPage => Success(
                        GetReferenceCatalogPage(command.PayloadJson, fileFingerprintProvider)),
                    GameCommandTypes.GetCatalogDiagnostics => Success(
                        GetCatalogDiagnostics(fileFingerprintProvider)),
                    GameCommandTypes.GetCampaignManagementState => Success(
                        GetCampaignManagementState()),
                    GameCommandTypes.GetUiInspection => Success(GetUiInspection()),
                    GameCommandTypes.CaptureGameScreenshot => CaptureGameScreenshot(
                        command.PayloadJson),
                    GameCommandTypes.FocusCameraOnUnit => FocusCameraOnUnit(command.PayloadJson),
                    GameCommandTypes.SetTacticalHudPanel => SetTacticalHudPanel(
                        command.PayloadJson),
                    GameCommandTypes.OpenTaskForceBuilder => OpenTaskForceBuilder(),
                    GameCommandTypes.StartPortOperations => StartPortOperations(),
                    GameCommandTypes.AddMaintenanceDraftUnit => AddMaintenanceDraftUnit(
                        command.PayloadJson),
                    GameCommandTypes.StartScenario => StartScenario(command.PayloadJson),
                    GameCommandTypes.ContinueMissionBriefing => ContinueMissionBriefing(),
                    GameCommandTypes.ExitTacticalSession => ExitTacticalSession(),
                    GameCommandTypes.SetExperimentalPauseState => SetExperimentalPauseState(
                        command.PayloadJson),
                    GameCommandTypes.LaunchExperimentalProbeWeapon =>
                        LaunchExperimentalProbeWeapon(command.PayloadJson),
                    GameCommandTypes.SaveExperimentalProbe => SaveExperimentalProbe(),
                    GameCommandTypes.LoadExperimentalProbe => LoadExperimentalProbe(),
                    GameCommandTypes.RunControlledTickExperiment =>
                        RunControlledTickExperiment(command.PayloadJson),
                    GameCommandTypes.ArmOwnedMutationMode => ArmOwnedMutationMode(
                        command.PayloadJson,
                        command.SessionId,
                        fileFingerprintProvider,
                        executionThreadGuardSelfTest),
                    GameCommandTypes.MaterializeOwnedSurfaceUnit =>
                        MaterializeOwnedSurfaceUnit(command.PayloadJson, command.SessionId),
                    GameCommandTypes.CaptureOwnedSurfaceCheckpoint =>
                        CaptureOwnedSurfaceCheckpoint(command.PayloadJson, command.SessionId),
                    GameCommandTypes.UpdateOwnedSurfaceUnit =>
                        UpdateOwnedSurfaceUnit(command.PayloadJson, command.SessionId),
                    GameCommandTypes.SetOwnedSurfaceSuspension =>
                        SetOwnedSurfaceSuspension(command.PayloadJson, command.SessionId),
                    GameCommandTypes.RestoreOwnedSurfaceCheckpoint =>
                        RestoreOwnedSurfaceCheckpoint(command.PayloadJson, command.SessionId),
                    GameCommandTypes.StageOwnedSurfaceCheckpoint =>
                        StageOwnedSurfaceCheckpoint(command.PayloadJson, command.SessionId),
                    GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery =>
                        ReleaseDetachedOwnedSurfaceRecovery(
                            command.PayloadJson,
                            command.SessionId),
                    _ => Failure(
                        ProtocolErrorCodes.UnsupportedCommand,
                        $"Command '{command.MessageType}' is not supported."),
                };
            }
        }
        catch (Exception exception)
        {
            var errorId = Guid.NewGuid().ToString("N");
            if (executingThreadId == initializedMainThreadId)
            {
                UnityEngine.Debug.LogError(
                    $"[Sea Power Runtime Bridge] command failed ({errorId}): {exception}");
            }

            result = Failure(
                ProtocolErrorCodes.InternalError,
                $"The game command failed. See the local game log with error ID {errorId}.",
                errorId);
        }

        return CreateResponse(command, result);
    }

    public static WireEnvelope Reject(WireEnvelope command, string code, string message)
    {
        return CreateResponse(command, Failure(code, message));
    }

    public static Action? TakeDeferredAction()
    {
        var action = _deferredAction;
        _deferredAction = null;
        return action;
    }

    public static Action? TakeEndOfFrameAction()
    {
        var action = _endOfFrameAction;
        _endOfFrameAction = null;
        return action;
    }

    private static WireEnvelope CreateResponse(
        WireEnvelope command,
        CommandResponsePayload result)
    {
        return new WireEnvelope
        {
            Kind = MessageKinds.Response,
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = command.MessageId,
            SessionId = command.SessionId,
            MessageType = command.MessageType,
            PayloadJson = JsonSerializer.Serialize(result, JsonOptions),
            Priority = CommandPriorities.Normalize(command.Priority),
        };
    }

    private static SceneStatePayload GetSceneState(
        int initializedMainThreadId,
        int executingThreadId)
    {
        var scene = SceneManager.GetActiveScene();
        var objectsManagerAvailable = Singleton<ObjectsManager>.InstanceExists();
        var taskforceManagerAvailable = Singleton<TaskforceManager>.InstanceExists();

        return new SceneStatePayload
        {
            SceneName = scene.name,
            SceneLoaded = scene.isLoaded,
            GameMainAvailable = Singleton<GameMain>.InstanceExists(),
            ObjectsManagerAvailable = objectsManagerAvailable,
            TaskforceManagerAvailable = taskforceManagerAvailable,
            ObjectCount = objectsManagerAvailable
                ? Singleton<ObjectsManager>.Instance._listOfAllObjects.Count
                : 0,
            TaskforceCount = taskforceManagerAvailable
                ? Singleton<TaskforceManager>.Instance._taskForces.Count
                : 0,
            InitializedMainThreadId = initializedMainThreadId,
            ExecutingThreadId = executingThreadId,
            IsMainThread = executingThreadId == initializedMainThreadId,
        };
    }

    private static WorldSnapshotPayload GetWorldSnapshot()
    {
        var captureTimer = Stopwatch.StartNew();
        var scene = SceneManager.GetActiveScene();
        if (!Singleton<ObjectsManager>.InstanceExists()
            || !Singleton<TaskforceManager>.InstanceExists())
        {
            var emptySnapshot = new WorldSnapshotPayload
            {
                SceneName = scene.name ?? string.Empty,
                CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SimulationTimeSeconds = Finite(GameTime.time),
                TimeCompression = Finite(GameTime.TimeCompression),
                IsPaused = GameTime.IsPaused(),
                StableIdentityState = "ready",
                SpatialDiagnostics = CaptureSpatialDiagnostics([]),
                ControlledTickExperiment = RuntimeControlledTickAdapter.Capture(),
            };
            PopulateWorldSubsystemHashes(emptySnapshot);
            captureTimer.Stop();
            emptySnapshot.MainThreadCaptureDurationMicroseconds =
                captureTimer.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            return emptySnapshot;
        }

        var allObjects = Singleton<ObjectsManager>.Instance._listOfAllObjects;

        var taskforces = Singleton<TaskforceManager>.Instance._taskForces
            .Where(taskforce => taskforce is not null)
            .ToArray();
        var taskforceIds = BuildTaskforceIds(taskforces);

        var units = allObjects
            .Where(unit => unit is not null && unit.isUnit())
            .Select(unit => CaptureUnit(unit, taskforceIds))
            .OrderBy(unit => unit.GameUniqueId)
            .ToArray();
        var weapons = allObjects
            .OfType<WeaponBase>()
            .Select(CaptureWeapon)
            .OrderBy(weapon => weapon.GameUniqueId)
            .ToArray();
        var duplicateStableIds = units
            .GroupBy(unit => unit.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var contacts = Globals._playerTaskforce?.PlottingTable?.Vehicles
            .Where(vehicle => vehicle is not null && vehicle.IsVisible())
            .Select(vehicle => CaptureContact(vehicle, taskforceIds))
            .OrderBy(contact => contact.TrackNumber)
            .ToArray() ?? [];
        var events = CapturePlayerEventLog();

        var snapshot = new WorldSnapshotPayload
        {
            SceneName = scene.name ?? string.Empty,
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SimulationTimeSeconds = Finite(GameTime.time),
            TimeCompression = Finite(GameTime.TimeCompression),
            IsPaused = GameTime.IsPaused(),
            ObservedObjectCount = allObjects.Count,
            StableIdentityState = duplicateStableIds.Length == 0
                ? "ready"
                : "duplicate-stable-ids",
            CampaignIdentityCount = units.Count(unit =>
                string.Equals(unit.IdentityProvenance, "campaign-tag", StringComparison.Ordinal)),
            SessionSurrogateIdentityCount = units.Count(unit =>
                string.Equals(unit.IdentityProvenance, "game-unique-id", StringComparison.Ordinal)),
            DuplicateStableIds = duplicateStableIds,
            Taskforces = taskforces
                .Select(taskforce => new TaskforceSnapshotPayload
                {
                    Id = taskforceIds[taskforce],
                    MissionName = taskforce._nameInMissionFile ?? string.Empty,
                    Side = taskforce.Side.ToString(),
                    Nation = taskforce.Nation ?? string.Empty,
                    IsAiControlled = taskforce.IsAIControlled,
                    IsPlayerControlled = ReferenceEquals(taskforce, Globals._playerTaskforce),
                    UnitIds = taskforce._taskforceUnits
                        .Where(unit => unit is not null && unit.isUnit())
                        .Select(StableUnitId)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray(),
                })
                .OrderBy(taskforce => taskforce.Id, StringComparer.Ordinal)
                .ToArray(),
            Units = units,
            Weapons = weapons,
            Contacts = contacts,
            Events = events,
            SpatialDiagnostics = CaptureSpatialDiagnostics(allObjects),
            ControlledTickExperiment = RuntimeControlledTickAdapter.Capture(),
        };
        PopulateWorldSubsystemHashes(snapshot);
        captureTimer.Stop();
        snapshot.MainThreadCaptureDurationMicroseconds =
            captureTimer.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        return snapshot;
    }

    private static TacticalCouplingStatePayload GetTacticalCouplingState()
    {
        var captureTimer = Stopwatch.StartNew();
        var allObjects = Singleton<ObjectsManager>.InstanceExists()
            ? Singleton<ObjectsManager>.Instance._listOfAllObjects
                .Where(item => item is not null)
                .ToArray()
            : [];
        var activeWeapons = allObjects
            .OfType<WeaponBase>()
            .Where(weapon => weapon.isLaunched() && !weapon.IsDestroyed)
            .Select(CaptureWeapon)
            .OrderBy(weapon => weapon.GameUniqueId)
            .ToArray();
        var relationships = CaptureTacticalCouplingRelationships(allObjects);
        captureTimer.Stop();
        return new TacticalCouplingStatePayload
        {
            SceneName = SceneManager.GetActiveScene().name ?? string.Empty,
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MainThreadCaptureDurationMicroseconds =
                captureTimer.ElapsedTicks * 1_000_000L / Stopwatch.Frequency,
            ActiveWeapons = activeWeapons,
            Relationships = relationships,
        };
    }

    private static TacticalCouplingObservationPayload[] CaptureTacticalCouplingRelationships(
        IReadOnlyList<ObjectBase> allObjects)
    {
        var observations = new List<TacticalCouplingObservationPayload>();
        void Add(
            string kind,
            ObjectBase? source,
            ObjectBase? target,
            string evidenceId,
            string detail,
            bool unsafeToSplit = true)
        {
            var sourceId = ObjectSnapshotId(source);
            var targetId = ObjectSnapshotId(target);
            if (string.IsNullOrWhiteSpace(sourceId)
                || string.IsNullOrWhiteSpace(targetId))
            {
                return;
            }
            observations.Add(new TacticalCouplingObservationPayload
            {
                Kind = kind,
                SourceObjectId = sourceId,
                TargetObjectId = targetId,
                EvidenceId = evidenceId,
                Detail = detail,
                UnsafeToSplit = unsafeToSplit,
            });
        }

        var units = allObjects
            .Where(item => item.isUnit() && !item.IsDestroyed)
            .OrderBy(item => item.UniqueID)
            .ToArray();
        foreach (var unit in units)
        {
            if (unit._homeBase is { } homeBase && !ReferenceEquals(unit, homeBase))
            {
                Add("host-dependency", unit, homeBase, $"host:{StableUnitId(unit)}", "home-base");
            }

            var leader = unit.Formation?.LeaderStation?.UnitObject;
            if (unit.FollowingFormation && leader is not null && !ReferenceEquals(unit, leader))
            {
                Add("runtime-formation", unit, leader, $"formation:{StableUnitId(unit)}", "follow-leader");
            }

            if (unit is Aircraft aircraft
                && aircraft.HasTemporaryFormationReference
                && aircraft.TemporaryFormationLeader is { } temporaryLeader
                && !ReferenceEquals(unit, temporaryLeader))
            {
                var detail = AerialRefuelingEquipment.TryGetCompatibleSystem(
                    temporaryLeader,
                    aircraft,
                    out var refuelingSystem)
                    ? $"aerial-refueling:{refuelingSystem}"
                    : "temporary-formation";
                Add(
                    "physical-dependency",
                    unit,
                    temporaryLeader,
                    $"physical:temporary-formation:{StableUnitId(unit)}",
                    detail);
            }

            if (unit.CollisionAvoidance?.UnitToAvoid is { } collisionThreat
                && !ReferenceEquals(unit, collisionThreat))
            {
                Add(
                    "physical-dependency",
                    unit,
                    collisionThreat,
                    $"physical:collision-avoidance:{StableUnitId(unit)}",
                    "collision-avoidance");
            }

            var sensors = unit._obp?._sensorSystems;
            if (sensors is null) continue;
            for (var index = 0; index < sensors.Count; index++)
            {
                var sensor = sensors[index];
                if (sensor is null) continue;
                var evidenceId = $"sensor:{StableUnitId(unit)}:{index}:{sensor._systemName}";
                var detail = sensor.GetType().Name;
                foreach (var target in sensor._associatedTargets
                    .Concat([sensor._targetObject, sensor._preLaunchTargetObject])
                    .Where(item => item is not null)
                    .Distinct())
                {
                    Add("sensor-track", unit, target, evidenceId, detail);
                }
                foreach (var weapon in sensor._associatedWeapons
                    .Where(item => item is not null)
                    .Distinct())
                {
                    Add("sensor-guidance", unit, weapon, evidenceId, detail);
                }
            }
        }

        if (Singleton<TaskforceManager>.InstanceExists()
            && Unity.Entities.World.DefaultGameObjectInjectionWorld is { IsCreated: true } world)
        {
            var entityManager = world.EntityManager;
            var objectsByEntity = units
                .Where(item => item.Entity != Unity.Entities.Entity.Null)
                .GroupBy(item => item.Entity)
                .ToDictionary(group => group.Key, group => group.First());
            var vehicles = Singleton<TaskforceManager>.Instance._taskForces
                .Where(taskforce => taskforce?.PlottingTable is not null)
                .SelectMany(taskforce => taskforce.PlottingTable.LocalVehicles.Values)
                .Where(vehicle => vehicle is not null)
                .Distinct()
                .OrderBy(vehicle => vehicle.Id)
                .ToArray();
            foreach (var vehicle in vehicles)
            {
                foreach (var trackEntity in vehicle.CurrentTracks()
                    .Where(entityManager.HasComponent<TrackComponent>))
                {
                    var track = entityManager.GetComponentData<TrackComponent>(trackEntity);
                    if (!objectsByEntity.TryGetValue(track.Source, out var source)
                        || !objectsByEntity.TryGetValue(track.DetectedObject, out var target)
                        || ReferenceEquals(source, target))
                    {
                        continue;
                    }
                    Add(
                        "plotting-track",
                        source,
                        target,
                        $"track:{StableUnitId(source)}:{StableUnitId(target)}:{track.SensorType}:{track.Kind}",
                        $"{track.SensorType}:{track.Kind}");
                }
            }
        }

        return observations
            .GroupBy(item => string.Join('\n',
                item.Kind,
                item.SourceObjectId,
                item.TargetObjectId,
                item.Detail,
                item.UnsafeToSplit))
            .Select(group => group
                .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.SourceObjectId, StringComparer.Ordinal)
            .ThenBy(item => item.TargetObjectId, StringComparer.Ordinal)
            .ThenBy(item => item.Detail, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .Take(4_096)
            .ToArray();
    }

    private static SpatialDiagnosticsPayload CaptureSpatialDiagnostics(
        IEnumerable<ObjectBase> objects)
    {
        const double metersPerUnityUnit = 67.2;
        const double terrainChunkSizeUnity = 330.71396;
        var candidates = objects
            .Where(candidate => candidate is not null)
            .Select(candidate => new
            {
                Object = candidate,
                Position = candidate.transform.position,
                Id = candidate is WeaponBase
                    ? $"weapon:{candidate.UniqueID}"
                    : candidate.isUnit()
                        ? StableUnitId(candidate)
                        : $"object:{candidate.UniqueID}",
            })
            .ToArray();
        var rebaseThreshold = Math.Max(0d, Finite(Globals._rebaseToOrigin) ?? 0d);
        var maximumDistance = 0d;
        var maximumDistanceObjectId = string.Empty;
        var beyondThreshold = 0;
        foreach (var candidate in candidates)
        {
            var distance = Math.Sqrt(
                candidate.Position.x * candidate.Position.x
                + candidate.Position.z * candidate.Position.z);
            if (distance > maximumDistance)
            {
                maximumDistance = distance;
                maximumDistanceObjectId = candidate.Id;
            }
            if (rebaseThreshold > 0d && distance > rebaseThreshold)
            {
                beyondThreshold++;
            }
        }

        var maximumPairDistance = 0d;
        var maximumPairFirstObjectId = string.Empty;
        var maximumPairSecondObjectId = string.Empty;
        for (var firstIndex = 0; firstIndex < candidates.Length; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < candidates.Length; secondIndex++)
            {
                var deltaX = candidates[firstIndex].Position.x - candidates[secondIndex].Position.x;
                var deltaZ = candidates[firstIndex].Position.z - candidates[secondIndex].Position.z;
                var distance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
                if (distance <= maximumPairDistance)
                {
                    continue;
                }
                maximumPairDistance = distance;
                maximumPairFirstObjectId = candidates[firstIndex].Id;
                maximumPairSecondObjectId = candidates[secondIndex].Id;
            }
        }

        var cameraPosition = Singleton<CameraManager>.InstanceExists()
            ? Singleton<CameraManager>.Instance.getCameraUnityPosition()
            : Vector3.zero;
        var cameraDistance = Math.Sqrt(
            cameraPosition.x * cameraPosition.x
            + cameraPosition.z * cameraPosition.z);
        var terrainManager = Singleton<TerrainManager>.InstanceExists()
            ? Singleton<TerrainManager>.Instance
            : null;
        var maximumLoadedTerrainTileOffset = terrainManager?.TerrainChunkDictionary.Keys
            .Select(tile => Math.Sqrt(
                Math.Pow(tile.x - Globals._currentCenterTile.x, 2d)
                + Math.Pow(tile.y - Globals._currentCenterTile.y, 2d)))
            .DefaultIfEmpty(0d)
            .Max() ?? 0d;
        var relocationHandler = Singleton<RelocationHandler>.InstanceExists()
            ? Singleton<RelocationHandler>.Instance
            : null;
        return new SpatialDiagnosticsPayload
        {
            MetersPerUnityUnit = metersPerUnityUnit,
            CenterTileX = Globals._currentCenterTile.x,
            CenterTileY = Globals._currentCenterTile.y,
            RebaseThresholdUnity = rebaseThreshold,
            RebaseThresholdMeters = rebaseThreshold * metersPerUnityUnit,
            CameraHorizontalDistanceUnity = cameraDistance,
            CameraHorizontalDistanceMeters = cameraDistance * metersPerUnityUnit,
            ObjectCountBeyondRebaseThreshold = beyondThreshold,
            MaximumObjectHorizontalDistanceUnity = maximumDistance,
            MaximumObjectHorizontalDistanceMeters = maximumDistance * metersPerUnityUnit,
            MaximumDistanceObjectId = maximumDistanceObjectId,
            MaximumPairHorizontalSeparationUnity = maximumPairDistance,
            MaximumPairHorizontalSeparationMeters = maximumPairDistance * metersPerUnityUnit,
            MaximumPairFirstObjectId = maximumPairFirstObjectId,
            MaximumPairSecondObjectId = maximumPairSecondObjectId,
            TerrainChunkSizeUnity = terrainChunkSizeUnity,
            TerrainChunkSizeMeters = terrainChunkSizeUnity * metersPerUnityUnit,
            ConfiguredTerrainChunkViewDistance = Globals._terrainChunksViewDistance,
            LoadedTerrainChunkCount = terrainManager?.TerrainChunkDictionary.Count ?? 0,
            FinalizedTerrainChunkCount = terrainManager?.TerrainChunkDictionary.Values.Count(
                chunk => chunk is not null && chunk._isFinalized) ?? 0,
            MaximumLoadedTerrainTileOffset = maximumLoadedTerrainTileOffset,
            MaximumLoadedTerrainCenterDistanceMeters =
                maximumLoadedTerrainTileOffset * terrainChunkSizeUnity * metersPerUnityUnit,
            InitialTerrainLoadingDone = terrainManager?.IsInitialLoadingDone ?? false,
            RelocationHandlerAvailable = relocationHandler is not null,
            LastRelocationEventTimeMilliseconds = relocationHandler?.LastRelocationEventTime ?? 0L,
        };
    }

    private static void PopulateWorldSubsystemHashes(WorldSnapshotPayload snapshot)
    {
        snapshot.IdentitySha256 = CapabilityFingerprint.ComputeSha256(
            snapshot.Taskforces.Select(taskforce => FingerprintField(
                $"taskforce.{taskforce.Id}",
                string.Join(",", taskforce.UnitIds)))
            .Concat(snapshot.Units.Select(unit => FingerprintField(
                $"unit.{unit.Id}",
                unit.IdentitySha256))));
        snapshot.KinematicsSha256 = CapabilityFingerprint.ComputeSha256(
            snapshot.Units.Select(unit => FingerprintField(unit.Id, unit.KinematicsSha256)));
        snapshot.LogisticsSha256 = CapabilityFingerprint.ComputeSha256(
            snapshot.Units.Select(unit => FingerprintField(unit.Id, unit.LogisticsSha256)));
        snapshot.DamageSha256 = CapabilityFingerprint.ComputeSha256(
            snapshot.Units.Select(unit => FingerprintField(unit.Id, unit.DamageSha256)));
        snapshot.WeaponsSha256 = CapabilityFingerprint.ComputeSha256(
            snapshot.Weapons.Select(weapon => FingerprintField(
                weapon.Id,
                $"{weapon.IdentitySha256}:{weapon.KinematicsSha256}:{weapon.EngagementSha256}")));
        snapshot.OrdersSha256 = CapabilityFingerprint.ComputeSha256(
            snapshot.Units.Select(unit => FingerprintField(unit.Id, unit.OrdersSha256)));
        snapshot.ContactsSha256 = CapabilityFingerprint.ComputeSha256(
            snapshot.Contacts.Select(contact => FingerprintField(
                contact.Id,
                contact.ObservationSha256)));
        snapshot.EventsSha256 = snapshot.Events.ObservationSha256;
        snapshot.CanonicalSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("schema-version", snapshot.SchemaVersion),
            FingerprintField("scene-name", snapshot.SceneName),
            FingerprintField("identity-scope", snapshot.IdentityScope),
            FingerprintField("simulation-time-seconds", snapshot.SimulationTimeSeconds),
            FingerprintField("time-compression", snapshot.TimeCompression),
            FingerprintField("is-paused", snapshot.IsPaused),
            FingerprintField("observed-object-count", snapshot.ObservedObjectCount),
            FingerprintField("stable-identity-state", snapshot.StableIdentityState),
            FingerprintField("campaign-identity-count", snapshot.CampaignIdentityCount),
            FingerprintField(
                "session-surrogate-identity-count",
                snapshot.SessionSurrogateIdentityCount),
            FingerprintField(
                "duplicate-stable-ids",
                string.Join(",", snapshot.DuplicateStableIds.OrderBy(id => id, StringComparer.Ordinal))),
            FingerprintField("identity-sha256", snapshot.IdentitySha256),
            FingerprintField("kinematics-sha256", snapshot.KinematicsSha256),
            FingerprintField("logistics-sha256", snapshot.LogisticsSha256),
            FingerprintField("damage-sha256", snapshot.DamageSha256),
            FingerprintField("weapons-sha256", snapshot.WeaponsSha256),
            FingerprintField("orders-sha256", snapshot.OrdersSha256),
            FingerprintField("contacts-sha256", snapshot.ContactsSha256),
            FingerprintField("events-sha256", snapshot.EventsSha256),
        });
    }

    private static CampaignManagementStatePayload GetCampaignManagementState()
    {
        var scene = SceneManager.GetActiveScene();
        var campaignView = LinearCampaignView.Instance;
        var campaignMap = LinearCampaignMapViewModel.Instance;
        var builder = campaignMap?.TaskForceBuilderWindow;
        var selectedEvent = campaignMap?.SelectedMission;
        var targetMissionIndex = builder?.TargetMissionIndex ?? -1;
        var availableRosterVariantCount = builder is null
            ? 0
            : CountRosterVariants(builder.ShipRosterGroups)
                + CountRosterVariants(builder.SubmarineRosterGroups)
                + CountRosterVariants(builder.HelicopterRosterGroups)
                + CountRosterVariants(builder.AircraftRosterGroups);
        var nativeMaintenance = CaptureNativeMaintenanceState(
            campaignMap?.TaskForceManagerUnits);

        return new CampaignManagementStatePayload
        {
            SceneName = scene.name ?? string.Empty,
            LinearCampaignViewAvailable = campaignView is not null,
            LinearCampaignMapAvailable = campaignMap is not null,
            IsTaskForceModeCampaign = campaignMap?.IsTaskForceModeCampaign ?? false,
            HasSelectedCampaignEvent = selectedEvent is not null,
            SelectedCampaignEventName = selectedEvent?.EventName ?? string.Empty,
            TaskForceBuilderVisible = campaignMap?.IsTaskForceBuilderCommandVisible ?? false,
            TaskForceBuilderEnabled = campaignMap?.IsTaskForceBuilderCommandEnabled ?? false,
            TaskForceBuilderTargetMissionIndex = targetMissionIndex,
            TaskForceBuilderLoaded = targetMissionIndex >= 1,
            HasConfirmedTaskForce = builder?.HasConfirmedTaskForce ?? false,
            TaskForceName = builder?.TaskForceName ?? string.Empty,
            AvailablePoints = builder?.AvailablePoints ?? 0,
            PointCap = builder?.PointCap ?? 0,
            PurchasedUnitCount = builder?.PurchasedItems.Count ?? 0,
            PendingUnitCount = builder?.PendingPurchaseItems.Count ?? 0,
            PendingPurchaseCost = builder?.PendingPurchaseCost ?? 0,
            HasPendingTransactions = builder?.HasPendingTransactions ?? false,
            VisibleTaskForceUnitCount = campaignMap?.TaskForceManagerVisibleUnits.Count ?? 0,
            AvailableRosterVariantCount = availableRosterVariantCount,
            HasShipRosterUnits = builder?.HasShipRosterUnits ?? false,
            HasSubmarineRosterUnits = builder?.HasSubmarineRosterUnits ?? false,
            HasAirRosterUnits = builder?.HasAirRosterUnits ?? false,
            PurchasedUnits = CaptureMaintenanceUnits(builder?.PurchasedItems),
            PendingUnits = CaptureMaintenanceUnits(builder?.PendingPurchaseItems),
            NativeMaintenanceState = campaignMap is null
                ? "unavailable"
                : nativeMaintenance.Length == 0
                    ? "empty"
                    : "ready",
            NativeRearmAvailable = nativeMaintenance.Any(unit => unit.HasRearmedBadge),
            NativeDamagedUnitCount = nativeMaintenance.Count(unit =>
                !string.Equals(unit.DamageSummary, "No Damage", StringComparison.OrdinalIgnoreCase)),
            NativeRepairCandidateCount = nativeMaintenance.Count(unit => unit.CanRepair),
            NativeMaintenanceUnits = nativeMaintenance,
        };
    }

    private static CampaignNativeMaintenanceStatePayload[] CaptureNativeMaintenanceState(
        IEnumerable<LinearCampaignTaskForceStatusUnitRow>? rows)
    {
        return rows?
            .Where(row => row?.Item is not null)
            .Select(row => new CampaignNativeMaintenanceStatePayload
            {
                InstanceId = row.Item.InstanceId ?? string.Empty,
                UnitType = row.Item.UnitType ?? string.Empty,
                DisplayName = row.Item.DisplayName ?? string.Empty,
                DamageSummary = row.DamageSummary ?? string.Empty,
                CanRepair = row.CanSendForRepair,
                RepairCostPoints = row.RepairCostPoints,
                RepairStatus = row.CanSendForRepair
                    ? "available"
                    : string.Equals(
                        row.DamageSummary,
                        "No Damage",
                        StringComparison.OrdinalIgnoreCase)
                            ? "not-needed"
                            : "unavailable",
                HasRearmedBadge = row.HasRearmedBadge,
                HasRepairedBadge = row.HasRepairedBadge,
                Ammunition = row.AmmoLines
                    .Select(line => new CampaignNativeAmmunitionStatePayload
                    {
                        Reference = line.Reference ?? string.Empty,
                        NativeSummary = line.Text ?? string.Empty,
                    })
                    .OrderBy(line => line.Reference, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(line => line.NativeSummary, StringComparer.Ordinal)
                    .ToArray(),
            })
            .OrderBy(unit => unit.InstanceId, StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static CampaignMaintenanceUnitPayload[] CaptureMaintenanceUnits(
        IEnumerable<LinearCampaignTaskForceBuilderRosterItem>? items)
    {
        return items?
                .Where(item => item is not null)
                .Select(item => new CampaignMaintenanceUnitPayload
                {
                    InstanceId = item.InstanceId ?? string.Empty,
                    UnitFolder = item.UnitFolder ?? string.Empty,
                    UnitType = item.UnitType ?? string.Empty,
                    Category = item.Category ?? string.Empty,
                    VariantReference = item.VariantReference ?? string.Empty,
                    DisplayName = item.DisplayName ?? string.Empty,
                    Nation = item.Nation ?? string.Empty,
                    SelectedLoadoutVariant = item.SelectedLoadoutVariant ?? string.Empty,
                    Quantity = item.QuantityPurchased,
                    Cost = item.Cost,
                    AircraftAssignments = item.AircraftAssignments
                        .OrderBy(assignment => assignment.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            assignment => assignment.Key,
                            assignment => assignment.Value,
                            StringComparer.OrdinalIgnoreCase),
                })
                .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
                .ToArray() ?? [];
    }

    private static UnitCatalogPagePayload GetUnitCatalogPage(
        string payloadJson,
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        var request = JsonSerializer.Deserialize<UnitCatalogPageRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new UnitCatalogPageRequestPayload();
        var offset = Math.Max(0, request.Offset);
        var limit = Math.Clamp(request.Limit, 1, 50);
        var folder = string.IsNullOrWhiteSpace(request.Folder)
            ? null
            : request.Folder.Trim().ToLowerInvariant();
        var content = fileFingerprintProvider.GetContentSnapshot();
        var provider = Singleton<UnitMetadataProvider>.Instance;
        var unitData = provider?.UnitData;
        if (content.State != RuntimeContentFingerprintSnapshot.ReadyState || unitData is null)
        {
            return new UnitCatalogPagePayload
            {
                State = unitData is null ? "metadata-unavailable" : content.State,
                CatalogSha256 = content.CatalogSha256 ?? string.Empty,
                Offset = offset,
                Limit = limit,
            };
        }

        var records = unitData
            .Where(item => folder is null || string.Equals(
                item.Key.Item1,
                folder,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key.Item1, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Item2, StringComparer.Ordinal)
            .ToArray();

        return new UnitCatalogPagePayload
        {
            State = "ready",
            CatalogSha256 = content.CatalogSha256 ?? string.Empty,
            TotalRecords = records.Length,
            Offset = offset,
            Limit = limit,
            Records = records
                .Skip(offset)
                .Take(limit)
                .Select(item => CaptureUnitCatalogRecord(item, content))
                .ToArray(),
        };
    }

    private static UnitCatalogRecordPayload CaptureUnitCatalogRecord(
        KeyValuePair<(string, string), (int, SimpleObjectParameters)> item,
        RuntimeContentFingerprintSnapshot content)
    {
        var folder = item.Key.Item1 ?? string.Empty;
        var typeId = item.Key.Item2 ?? string.Empty;
        var metadata = item.Value.Item2;
        var source = content.FindWinningFile($"{folder}/{typeId}.ini");
        return new UnitCatalogRecordPayload
        {
            Id = $"unit:{folder.ToLowerInvariant()}/{typeId.ToLowerInvariant()}",
            RuntimeMetadataId = item.Value.Item1,
            UnitFolder = folder,
            UnitTypeId = typeId,
            ObjectType = metadata?._objectType.ToString() ?? string.Empty,
            DefaultName = metadata?.DefaultName ?? string.Empty,
            ClassName = metadata?.ClassName ?? string.Empty,
            TypeAbbreviation = metadata?._typeAbbr ?? string.Empty,
            TypeName = metadata?._typeFullName ?? string.Empty,
            DefaultNation = metadata?._defaultNation ?? string.Empty,
            SourceId = source?.SourceId ?? string.Empty,
            SourceFileSha256 = source?.Sha256 ?? string.Empty,
            LoadoutIds = metadata?._loadouts?.Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray() ?? [],
            Roles = metadata?._unitRoles
                .Select(value => value.ToString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray() ?? [],
            Variants = metadata?._variants
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new UnitCatalogVariantPayload
                {
                    Id = value.Key,
                    Index = value.Value.Index,
                    Name = value.Value.DefaultName ?? string.Empty,
                    ClassName = value.Value.ClassName ?? string.Empty,
                    Nation = value.Value.Nation ?? string.Empty,
                    LoadoutId = value.Value.LoadoutVariant ?? string.Empty,
                    ServiceEntryYear = value.Value.ServiceEntryYear,
                    ServiceExitYear = value.Value.ServiceExitYear,
                })
                .ToArray() ?? [],
        };
    }

    private static ReferenceCatalogPagePayload GetReferenceCatalogPage(
        string payloadJson,
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        var request = JsonSerializer.Deserialize<ReferenceCatalogPageRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new ReferenceCatalogPageRequestPayload();
        var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var offset = Math.Max(0, request.Offset);
        var limit = Math.Clamp(request.Limit, 1, 100);
        var content = fileFingerprintProvider.GetContentSnapshot();
        var result = new ReferenceCatalogPagePayload
        {
            State = content.State,
            CatalogSha256 = content.CatalogSha256 ?? string.Empty,
            Kind = kind,
            Offset = offset,
            Limit = limit,
        };
        if (content.State != RuntimeContentFingerprintSnapshot.ReadyState)
        {
            return result;
        }

        ReferenceCatalogRecordPayload[] records = kind switch
        {
            "ammunition" => CaptureAmmunitionCatalog(content),
            "sensors" => CaptureSensorCatalog(content),
            "signatures" => CaptureSignatureCatalog(content),
            "files" => CaptureFileCatalog(content),
            _ => [],
        };
        result.State = records.Length == 0 && kind is not "files"
            ? "metadata-unavailable-or-kind-unsupported"
            : "ready";
        result.TotalRecords = records.Length;
        result.Records = records.Skip(offset).Take(limit).ToArray();
        return result;
    }

    private static CatalogDiagnosticsPayload GetCatalogDiagnostics(
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        var content = fileFingerprintProvider.GetContentSnapshot();
        return new CatalogDiagnosticsPayload
        {
            State = content.State,
            CatalogSha256 = content.CatalogSha256 ?? string.Empty,
            WinningFileCount = content.CatalogFileCount,
            OverrideCount = content.Overrides.Length,
            IdenticalOverrideCount = content.Overrides.Count(item =>
                string.Equals(item.Classification, "identical-override", StringComparison.Ordinal)),
            ConflictingOverrideCount = content.Overrides.Count(item =>
                string.Equals(item.Classification, "conflicting-override", StringComparison.Ordinal)),
            Overrides = content.Overrides.Select(item => new CatalogOverrideDiagnosticPayload
            {
                VirtualPath = item.VirtualPath,
                WinningSourceId = item.WinningSourceId,
                WinningSha256 = item.WinningSha256,
                ShadowedSourceId = item.ShadowedSourceId,
                ShadowedSha256 = item.ShadowedSha256,
                Classification = item.Classification,
            }).ToArray(),
        };
    }

    private static ReferenceCatalogRecordPayload[] CaptureAmmunitionCatalog(
        RuntimeContentFingerprintSnapshot content)
    {
        var ammunitionData = Singleton<UnitMetadataProvider>.Instance?.AmmunitionData;
        if (ammunitionData is null)
        {
            return [];
        }

        return ammunitionData
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item =>
            {
                var source = content.FindWinningFile($"ammunition/{item.Key}.ini");
                return new ReferenceCatalogRecordPayload
                {
                    Id = $"ammunition:{item.Key.ToLowerInvariant()}",
                    Kind = "ammunition",
                    Name = item.Key,
                    DisplayName = item.Value?.DisplayedName ?? string.Empty,
                    Type = item.Value?._type.ToString() ?? string.Empty,
                    Subtype = item.Value?._weaponSystemType.ToString() ?? string.Empty,
                    GuidanceType = item.Value?._guidanceType.ToString() ?? string.Empty,
                    PrimaryTargetType = item.Value?._targetType.ToString() ?? string.Empty,
                    SecondaryTargetType = item.Value?._secondonaryTargetType.ToString() ?? string.Empty,
                    SourceId = source?.SourceId ?? string.Empty,
                    VirtualPath = source?.VirtualPath ?? string.Empty,
                    SourceFileSha256 = source?.Sha256 ?? string.Empty,
                };
            })
            .ToArray();
    }

    private static ReferenceCatalogRecordPayload[] CaptureSensorCatalog(
        RuntimeContentFingerprintSnapshot content)
    {
        var sensorProvider = Singleton<SensorMetadataProvider>.Instance;
        var source = content.FindWinningFile("systems/sensors.ini");
        return sensorProvider?.Sensors.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => new ReferenceCatalogRecordPayload
            {
                Id = $"sensor:{value.ToLowerInvariant()}",
                Kind = "sensor",
                Name = value,
                SourceId = source?.SourceId ?? string.Empty,
                VirtualPath = source?.VirtualPath ?? string.Empty,
                SourceFileSha256 = source?.Sha256 ?? string.Empty,
            })
            .ToArray() ?? [];
    }

    private static ReferenceCatalogRecordPayload[] CaptureSignatureCatalog(
        RuntimeContentFingerprintSnapshot content)
    {
        var sensorProvider = Singleton<SensorMetadataProvider>.Instance;
        var source = content.FindWinningFile("systems/signatures.ini");
        return sensorProvider?.SignatureMap.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => new ReferenceCatalogRecordPayload
            {
                Id = $"signature:{value.ToLowerInvariant()}",
                Kind = "signature",
                Name = value,
                Family = sensorProvider.FamilyMap.TryGetValue(value, out var family)
                    ? family ?? string.Empty
                    : string.Empty,
                Category = sensorProvider.CategoryMap.TryGetValue(value, out var category)
                    ? category ?? string.Empty
                    : string.Empty,
                SourceId = source?.SourceId ?? string.Empty,
                VirtualPath = source?.VirtualPath ?? string.Empty,
                SourceFileSha256 = source?.Sha256 ?? string.Empty,
            })
            .ToArray() ?? [];
    }

    private static ReferenceCatalogRecordPayload[] CaptureFileCatalog(
        RuntimeContentFingerprintSnapshot content) => content.Files
        .OrderBy(file => file.VirtualPath, StringComparer.Ordinal)
        .Select(file => new ReferenceCatalogRecordPayload
        {
            Id = $"asset:{file.VirtualPath}",
            Kind = "file",
            Name = Path.GetFileNameWithoutExtension(file.VirtualPath),
            Category = file.VirtualPath.Split('/')[0],
            SourceId = file.SourceId,
            VirtualPath = file.VirtualPath,
            SourceFileSha256 = file.Sha256,
        })
        .ToArray();

    private static int CountRosterVariants(
        IEnumerable<LinearCampaignTaskForceBuilderRosterTypeGroup> typeGroups)
    {
        return typeGroups.Sum(typeGroup => typeGroup.AllClassGroups.Sum(
            classGroup => classGroup.Variants.Count));
    }

    private static CommandResponsePayload OpenTaskForceBuilder()
    {
        if (!RuntimeUiAutomationSeamFingerprintProvider.IsCommandVerified(
            GameCommandTypes.OpenTaskForceBuilder))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned Task Force Builder UI seam is not verified for this game build.");
        }

        var before = GetCampaignManagementState();
        var campaignMap = LinearCampaignMapViewModel.Instance;
        var command = campaignMap?.OpenTaskForceBuilderCommand;

        if (!before.LinearCampaignViewAvailable || campaignMap is null)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The linear campaign map is not currently open.");
        }

        if (!before.IsTaskForceModeCampaign)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The open campaign is not a Task Force Mode campaign.");
        }

        if (!before.HasSelectedCampaignEvent)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "No campaign mission is selected.");
        }

        if (command is null || !before.TaskForceBuilderEnabled || !command.CanExecute(null))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The Task Force Builder is not available for the selected campaign mission.");
        }

        command.Execute(null);

        var after = GetCampaignManagementState();
        if (!after.TaskForceBuilderLoaded)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The Task Force Builder command ran, but its selected-mission state did not load.");
        }

        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.OpenTaskForceBuilder,
            Outcome = "completed",
            Postcondition = "task-force-builder-loaded-for-selected-mission",
            Campaign = after,
        });
    }

    private static CommandResponsePayload StartScenario(string payloadJson)
    {
        if (!RuntimeUiAutomationSeamFingerprintProvider.IsCommandVerified(
            GameCommandTypes.StartScenario))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned scenario-navigation UI seam is not verified for this game build.");
        }

        var request = JsonSerializer.Deserialize<StartScenarioRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new StartScenarioRequestPayload();
        var requestedId = NormalizeScenarioId(request.ScenarioId);
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            return Failure(
                ProtocolErrorCodes.InvalidMessage,
                "A normalized scenario ID is required.");
        }
        if (!Singleton<MenuSystemViewModel>.InstanceExists())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The main-menu navigation view model is unavailable.");
        }
        if (Singleton<GameMain>.InstanceExists()
            || !string.Equals(
                SceneManager.GetActiveScene().name,
                "background",
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "Scenario navigation requires the native main-menu context.");
        }

        var scenarioView = new ScenarioMenuView();
        Singleton<MenuSystemViewModel>.Instance.CurrentWindow = scenarioView;
        if (scenarioView.DataContext is not ScenarioMenuViewModel viewModel)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The scenario menu did not expose its typed view model.");
        }

        var matches = EnumerateMissions(viewModel.Missions)
            .Where(mission => string.Equals(
                ScenarioId(mission.FileName),
                requestedId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                $"Scenario ID '{requestedId}' resolved to {matches.Length} missions.");
        }

        var selected = matches[0];
        viewModel.SelectedMission = selected;
        if (!selected.PlayButtonEnabled || !viewModel.PlayCommand.CanExecute(null))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                $"Scenario '{requestedId}' is not currently playable.");
        }

        viewModel.PlayCommand.Execute(null);
        var after = GetScenarioAutomationState();
        if (!string.Equals(after.ScenarioId, requestedId, StringComparison.Ordinal))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The scenario load command ran without selecting the requested mission path.");
        }

        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.StartScenario,
            Outcome = "completed",
            Postcondition = "scenario-load-requested",
            Scenario = after,
        });
    }

    private static CommandResponsePayload StartPortOperations()
    {
        if (!RuntimeUiAutomationSeamFingerprintProvider.IsCommandVerified(
            GameCommandTypes.StartPortOperations))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned campaign-navigation UI seam is not verified for this game build.");
        }
        if (!Singleton<MenuSystemViewModel>.InstanceExists())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The main-menu navigation view model is unavailable.");
        }

        const string requestedPath =
            "campaigns/crimean-tom-port-operations/campaign.ini";
        var campaignView = new CampaignMenuView();
        Singleton<MenuSystemViewModel>.Instance.CurrentWindow = campaignView;
        if (campaignView.DataContext is not CampaignMenuViewModel viewModel)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The campaign menu did not expose its typed view model.");
        }

        var matches = viewModel.Missions
            .OfType<Mission>()
            .Where(mission => string.Equals(
                NormalizeVirtualPath(Singleton<FileManager>.Instance.GetRelativePath(
                    mission.FileName)),
                requestedPath,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                $"Port-operations campaign resolved to {matches.Length} entries.");
        }

        viewModel.SelectedMission = matches[0];
        if (viewModel.PlaySelectedCampaignCommand is null
            || !viewModel.PlaySelectedCampaignCommand.CanExecute(null))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The port-operations campaign is not currently playable.");
        }

        viewModel.PlaySelectedCampaignCommand.Execute(null);
        var selectedPath = NormalizeVirtualPath(
            Singleton<FileManager>.Instance.GetRelativePath(
                SaveLoadManager.GetGlobalCampaignSaveFilePath()));
        if (!string.Equals(selectedPath, requestedPath, StringComparison.Ordinal))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The campaign command ran without staging the port-operations campaign path.");
        }

        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.StartPortOperations,
            Outcome = "completed",
            Postcondition = "port-operations-campaign-load-requested",
            CampaignPath = selectedPath,
        });
    }

    private static CommandResponsePayload AddMaintenanceDraftUnit(string payloadJson)
    {
        if (!RuntimeUiAutomationSeamFingerprintProvider.IsCommandVerified(
            GameCommandTypes.AddMaintenanceDraftUnit))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned maintenance-draft edit seam is not verified for this game build.");
        }
        var request = JsonSerializer.Deserialize<AddMaintenanceDraftUnitRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new AddMaintenanceDraftUnitRequestPayload();
        if (string.IsNullOrWhiteSpace(request.UnitType)
            || string.IsNullOrWhiteSpace(request.VariantReference)
            || request.Quantity < 1)
        {
            return Failure(
                ProtocolErrorCodes.InvalidMessage,
                "Unit type, variant reference, and positive quantity are required.");
        }

        var builder = LinearCampaignMapViewModel.Instance?.TaskForceBuilderWindow;
        if (builder is null || builder.TargetMissionIndex < 1)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The Task Force Builder maintenance draft is not loaded.");
        }
        var matches = builder.ShipRosterGroups
            .Concat(builder.SubmarineRosterGroups)
            .Concat(builder.HelicopterRosterGroups)
            .Concat(builder.AircraftRosterGroups)
            .SelectMany(group => group.AllClassGroups)
            .SelectMany(group => group.Variants)
            .Where(item => string.Equals(item.UnitType, request.UnitType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    item.VariantReference,
                    request.VariantReference,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                $"Maintenance unit '{request.UnitType}/{request.VariantReference}' resolved to {matches.Length} roster items.");
        }

        var beforeCount = builder.PendingPurchaseItems.Count;
        builder.SelectedRosterItem = matches[0];
        builder.SelectedPurchaseQuantity = request.Quantity;
        if (!builder.AddSelectedItemCommand.CanExecute(null))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The exact maintenance unit is not currently affordable or addable.");
        }
        builder.AddSelectedItemCommand.Execute(null);
        var after = GetCampaignManagementState();
        if (after.PendingUnitCount <= beforeCount
            || !after.PendingUnits.Any(item =>
                string.Equals(item.UnitType, request.UnitType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    item.VariantReference,
                    request.VariantReference,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The add command ran without staging the requested maintenance unit.");
        }

        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.AddMaintenanceDraftUnit,
            Outcome = "completed",
            Postcondition = "maintenance-unit-staged-uncommitted",
            Campaign = after,
        });
    }

    private static CommandResponsePayload ContinueMissionBriefing()
    {
        if (!RuntimeUiAutomationSeamFingerprintProvider.IsCommandVerified(
            GameCommandTypes.ContinueMissionBriefing))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned mission-briefing UI seam is not verified for this game build.");
        }

        var currentWindow = Singleton<MenuSystemViewModel>.InstanceExists()
            ? Singleton<MenuSystemViewModel>.Instance.CurrentWindow
            : null;
        if (currentWindow is not MissionBriefView briefingView
            || briefingView.DataContext is not MissionBriefViewModel viewModel
            || !viewModel.hasContent
            || viewModel.PlayCommand is null
            || !viewModel.PlayCommand.CanExecute(null))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A playable native mission briefing is not currently open.");
        }

        viewModel.PlayCommand.Execute(null);
        var after = GetScenarioAutomationState();
        if (after.BriefingPaused)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The briefing continue command ran, but the mission remained briefing-paused.");
        }

        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.ContinueMissionBriefing,
            Outcome = "completed",
            Postcondition = "mission-running-unpaused",
            Scenario = after,
        });
    }

    private static CommandResponsePayload ExitTacticalSession()
    {
        if (!RuntimeUiAutomationSeamFingerprintProvider.IsCommandVerified(
            GameCommandTypes.ExitTacticalSession))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned tactical-exit UI seam is not verified for this game build.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<MissionManager>.InstanceExists())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A loaded tactical session is required for the exit action.");
        }

        Singleton<MissionManager>.Instance.ExitToMainMenu();
        var currentWindow = Singleton<MenuSystemViewModel>.InstanceExists()
            ? Singleton<MenuSystemViewModel>.Instance.CurrentWindow
            : null;
        if (currentWindow is not MissionDebriefView debriefView
            || debriefView.DataContext is not MissionDebriefViewModel viewModel
            || viewModel.ExitCommand is null
            || viewModel.ConfirmExit is null)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPostconditionFailed,
                "Sea Power did not open its typed mission-exit workflow.");
        }

        _deferredAction = () =>
        {
            viewModel.ExitCommand.Execute(null);
            viewModel.ConfirmExit.Execute(null);
        };
        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.ExitTacticalSession,
            Outcome = "scheduled",
            Postcondition = "native-tactical-exit-scheduled",
            Scenario = GetScenarioAutomationState(),
        });
    }

    private static CommandResponsePayload SetExperimentalPauseState(string payloadJson)
    {
        if (!RuntimeDeterministicHookFingerprintProvider.IsVerified("logical-time"))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned logical-time seam is not verified for this game build.");
        }
        if (!Singleton<GameMain>.InstanceExists())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A tactical mission is required for experimental pause control.");
        }

        var request = JsonSerializer.Deserialize<ExperimentalPauseRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new ExperimentalPauseRequestPayload();
        if (request.IsPaused)
        {
            GameTime.Pause();
        }
        else
        {
            GameTime.UnPause();
        }

        var actual = GameTime.IsPaused();
        if (actual != request.IsPaused)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The logical-time pause command did not reach its requested postcondition.");
        }
        return Success(new ExperimentalPauseResultPayload
        {
            RequestedPaused = request.IsPaused,
            IsPaused = actual,
            SimulationTimeSeconds = Finite(GameTime.time),
            TimeCompression = Finite(GameTime.TimeCompression),
            Postcondition = actual ? "tactical-time-paused" : "tactical-time-running",
        });
    }

    private static CommandResponsePayload RunControlledTickExperiment(string payloadJson)
    {
        if (!RuntimeDeterministicHookFingerprintProvider.IsVerified("logical-time")
            || !RuntimeDeterministicHookFingerprintProvider.IsVerified("stable-update-order"))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned logical-time and stable-update-order seams are not verified.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone
            || !GameTime.IsPaused())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A fully loaded, paused tactical mission is required for controlled ticks.");
        }

        var request = JsonSerializer.Deserialize<ControlledTickExperimentRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new ControlledTickExperimentRequestPayload();
        var started = RuntimeControlledTickAdapter.Start(request);
        if (!started.Success)
        {
            return Failure(ProtocolErrorCodes.AutomationPreconditionFailed, started.Error);
        }
        return Success(RuntimeControlledTickAdapter.Capture());
    }

    private static CommandResponsePayload ArmOwnedMutationMode(
        string payloadJson,
        string commandSessionId,
        RuntimeFileFingerprintProvider fileFingerprintProvider,
        string executionThreadGuardSelfTest)
    {
        var request = JsonSerializer.Deserialize<ArmOwnedMutationModeRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new ArmOwnedMutationModeRequestPayload();
        var currentFingerprint = RuntimeCapabilityCollector.Capture(
            fileFingerprintProvider,
            executionThreadGuardSelfTest).RuntimeFingerprint.FingerprintSha256;
        var result = RuntimeOwnedMutationAdapter.Arm(
            request,
            commandSessionId,
            currentFingerprint);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload MaterializeOwnedSurfaceUnit(
        string payloadJson,
        string commandSessionId)
    {
        var request = JsonSerializer.Deserialize<MaterializeOwnedSurfaceUnitRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new MaterializeOwnedSurfaceUnitRequestPayload();
        var result = RuntimeOwnedMutationAdapter.Materialize(request, commandSessionId);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload CaptureOwnedSurfaceCheckpoint(
        string payloadJson,
        string commandSessionId)
    {
        var request = JsonSerializer.Deserialize<OwnedSurfaceEntityRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new OwnedSurfaceEntityRequestPayload();
        var result = RuntimeOwnedMutationAdapter.CaptureCheckpoint(request, commandSessionId);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload UpdateOwnedSurfaceUnit(
        string payloadJson,
        string commandSessionId)
    {
        var request = JsonSerializer.Deserialize<UpdateOwnedSurfaceUnitRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new UpdateOwnedSurfaceUnitRequestPayload();
        var result = RuntimeOwnedMutationAdapter.Update(request, commandSessionId);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload SetOwnedSurfaceSuspension(
        string payloadJson,
        string commandSessionId)
    {
        var request = JsonSerializer.Deserialize<SetOwnedSurfaceSuspensionRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new SetOwnedSurfaceSuspensionRequestPayload();
        var result = RuntimeOwnedMutationAdapter.SetSuspension(request, commandSessionId);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload RestoreOwnedSurfaceCheckpoint(
        string payloadJson,
        string commandSessionId)
    {
        var request = JsonSerializer.Deserialize<RestoreOwnedSurfaceCheckpointRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new RestoreOwnedSurfaceCheckpointRequestPayload();
        var result = RuntimeOwnedMutationAdapter.RestoreCheckpoint(request, commandSessionId);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload StageOwnedSurfaceCheckpoint(
        string payloadJson,
        string commandSessionId)
    {
        var request = JsonSerializer.Deserialize<StageOwnedSurfaceCheckpointRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new StageOwnedSurfaceCheckpointRequestPayload();
        var result = RuntimeOwnedMutationAdapter.StageCheckpoint(request, commandSessionId);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload ReleaseDetachedOwnedSurfaceRecovery(
        string payloadJson,
        string commandSessionId)
    {
        var request = JsonSerializer.Deserialize<OwnedSurfaceEntityRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new OwnedSurfaceEntityRequestPayload();
        var result = RuntimeOwnedMutationAdapter.ReleaseDetachedRecovery(
            request,
            commandSessionId);
        return result.Succeeded
            ? Success(result.Payload)
            : Failure(result.ErrorCode, result.ErrorMessage, result.ErrorId);
    }

    private static CommandResponsePayload SaveExperimentalProbe()
    {
        if (!RuntimeDeterministicHookFingerprintProvider.IsVerified("native-save-load"))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned native save/load seam is not verified for this game build.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A fully loaded tactical mission is required for the save/load probe.");
        }

        var serializableWeapons = GetSerializableActiveWeapons();
        if (serializableWeapons.Length == 0)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The native save probe requires at least one taskforce-registered missile, torpedo, or bomb; mounted systems and deployed noisemakers do not qualify.");
        }
        var before = GetWorldSnapshot();
        var expectedSavePath = RuntimeInstanceConfiguration.Current
            .GetExperimentalSavePath(ExperimentalProbeSaveFileName);
        if (File.Exists(expectedSavePath))
        {
            File.Delete(expectedSavePath);
        }
        var savePath = SaveLoadManager.SaveGame(
            ExperimentalProbeSaveFileName,
            filePath: expectedSavePath);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "Sea Power refused to schedule the experimental save write.");
        }
        Globals._lastAutoSave = savePath;
        return Success(new ExperimentalSaveLoadProbeResultPayload
        {
            Action = GameCommandTypes.SaveExperimentalProbe,
            Outcome = "scheduled",
            Postcondition = "native-save-write-scheduled",
            SaveFileName = ExperimentalProbeSaveFileName,
            SaveFilePath = savePath,
            SaveFileSha256 = string.Empty,
            PreTransitionWeaponCount = serializableWeapons.Length,
            PreTransitionWeaponsSha256 = before.WeaponsSha256,
            PreTransitionCanonicalSha256 = before.CanonicalSha256,
        });
    }

    private static CommandResponsePayload LaunchExperimentalProbeWeapon(string payloadJson)
    {
        if (!RuntimeDeterministicHookFingerprintProvider.IsVerified("weapon-probe-launch"))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned weapon-probe launch seam is not verified for this game build.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<ObjectsManager>.InstanceExists()
            || Globals._playerTaskforce is null
            || !GameTime.IsPaused())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A fully loaded, paused tactical mission with a player taskforce is required.");
        }

        var request = JsonSerializer.Deserialize<ExperimentalWeaponLaunchRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new ExperimentalWeaponLaunchRequestPayload();
        if (!Enum.TryParse<Ammunition.Type>(request.WeaponType, true, out var desiredType)
            || desiredType is not (Ammunition.Type.Missile
                or Ammunition.Type.Torpedo
                or Ammunition.Type.Bomb
                or Ammunition.Type.ASROC))
        {
            return Failure(
                ProtocolErrorCodes.InvalidMessage,
                "weaponType must be Missile, Torpedo, Bomb, or ASROC.");
        }
        var desiredAmmunitionId = request.AmmunitionId?.Trim() ?? string.Empty;
        static bool WeaponMatchesRequestedAmmunition(
            WeaponBase candidate,
            string requestedAmmunitionId) =>
            string.IsNullOrEmpty(requestedAmmunitionId)
            || string.Equals(
                candidate._ap?._ammunitionFileName,
                requestedAmmunitionId,
                StringComparison.OrdinalIgnoreCase);
        static bool AmmunitionMatchesRequestedAmmunition(
            Ammunition candidate,
            string requestedAmmunitionId) =>
            string.IsNullOrEmpty(requestedAmmunitionId)
            || string.Equals(
                candidate._ap?._ammunitionFileName,
                requestedAmmunitionId,
                StringComparison.OrdinalIgnoreCase);

        ObjectBase? intendedTarget = null;
        if (!string.IsNullOrWhiteSpace(request.TargetUnitId))
        {
            var targetMatches = Singleton<ObjectsManager>.Instance._listOfAllObjects
                .Where(candidate => candidate is not null
                    && candidate.isUnit()
                    && !candidate.IsDestroyed
                    && string.Equals(
                        StableUnitId(candidate),
                        request.TargetUnitId,
                        StringComparison.Ordinal))
                .ToArray();
            if (targetMatches.Length != 1)
            {
                return Failure(
                    ProtocolErrorCodes.AutomationPreconditionFailed,
                    $"Target unit '{request.TargetUnitId}' resolved to {targetMatches.Length} live units.");
            }
            intendedTarget = targetMatches[0];
            if (Globals._playerTaskforce._taskforceUnits.Contains(intendedTarget))
            {
                return Failure(
                    ProtocolErrorCodes.AutomationPreconditionFailed,
                    "The experimental probe refuses a player-taskforce target.");
            }
        }

        var units = Globals._playerTaskforce._taskforceUnits
            .Where(unit => unit is not null && unit.isUnit())
            .Where(unit => string.IsNullOrWhiteSpace(request.UnitId)
                || string.Equals(StableUnitId(unit), request.UnitId, StringComparison.Ordinal))
            .OrderBy(unit => unit.UniqueID)
            .ToArray();
        foreach (var unit in units)
        {
            var weaponSystems = unit._obp?._weaponSystems ?? [];
            for (var weaponSystemIndex = 0; weaponSystemIndex < weaponSystems.Count; weaponSystemIndex++)
            {
                if (weaponSystems[weaponSystemIndex] is WeaponSystemHardpoint hardpoint)
                {
                    var weapon = hardpoint._weapons.FirstOrDefault(candidate =>
                        candidate is not null
                        && !candidate.isLaunched()
                        && candidate._ap?._type == desiredType
                        && WeaponMatchesRequestedAmmunition(candidate, desiredAmmunitionId));
                    if (weapon is null)
                    {
                        continue;
                    }

                    var forward = unit.transform.forward;
                    forward.y = 0f;
                    forward.Normalize();
                    var launchPosition = unit.transform.position;
                    launchPosition.y = 0f;
                    var previousAmmunition = hardpoint._ammoForEngage;
                    try
                    {
                        hardpoint._ammoForEngage = new Ammunition(weapon._ap);
                        hardpoint.launch(
                            intendedTarget,
                            intendedTarget is null
                                ? launchPosition + forward * 44.64281f
                                : intendedTarget.transform.position,
                            Vector3.zero,
                            unit.UnityVelocityVector);
                    }
                    finally
                    {
                        hardpoint._ammoForEngage = previousAmmunition;
                    }
                    return CompleteExperimentalWeaponLaunch(
                        unit,
                        weaponSystemIndex,
                        -1,
                        desiredType,
                        weapon,
                        intendedTarget,
                        desiredAmmunitionId,
                        "The pinned hardpoint launch seam returned without launching the selected weapon.");
                }

                if (weaponSystems[weaponSystemIndex] is not WeaponSystemLauncher launcher)
                {
                    continue;
                }
                for (var containerIndex = 0; containerIndex < launcher._containers.Count; containerIndex++)
                {
                    var container = launcher._containers[containerIndex];
                    var weapon = container._weapons.FirstOrDefault(candidate =>
                        candidate is not null
                        && !candidate.isLaunched()
                        && candidate._ap?._type == desiredType
                        && WeaponMatchesRequestedAmmunition(candidate, desiredAmmunitionId));
                    if (weapon is not null)
                    {
                        if (intendedTarget is null)
                        {
                            launcher.testLaunchWeapon(containerIndex);
                        }
                        else
                        {
                            container.launch(
                                intendedTarget,
                                intendedTarget.transform.position,
                                Vector3.zero,
                                0f,
                                weapon._ap._ammunitionFileName);
                        }
                        return CompleteExperimentalWeaponLaunch(
                            unit,
                            weaponSystemIndex,
                            containerIndex,
                            desiredType,
                            weapon,
                            intendedTarget,
                            desiredAmmunitionId,
                            "The pinned container launch seam returned without launching the selected weapon.");
                    }

                    var weaponParameters = launcher._vwp;
                    if (weaponParameters is null)
                    {
                        continue;
                    }

                    var magazine = weaponParameters._associatedMagazine;
                    var ammunition = magazine?._ammunitionList.FirstOrDefault(candidate =>
                        candidate?._ap?._type == desiredType
                        && AmmunitionMatchesRequestedAmmunition(candidate, desiredAmmunitionId)
                        && magazine.getAmmunitionCount(candidate._ap._ammunitionFileName) > 0);
                    if (ammunition is null || !container.HasReadyCell())
                    {
                        continue;
                    }

                    var beforeIds = GetSerializableActiveWeapons()
                        .Select(candidate => candidate.UniqueID)
                        .ToHashSet();
                    var forward = unit.transform.forward;
                    forward.y = 0f;
                    forward.Normalize();
                    var launchPosition = unit.transform.position;
                    launchPosition.y = 0f;
                    var targetPosition = launchPosition + forward * 44.64281f;
                    if (!container.LaunchFromMagazine(
                        ammunition._ap._ammunitionFileName,
                        intendedTarget,
                        intendedTarget is null
                            ? targetPosition
                            : intendedTarget.transform.position,
                        Vector3.zero,
                        0f,
                        weaponParameters._magazineReloadTime))
                    {
                        return Failure(
                            ProtocolErrorCodes.AutomationPostconditionFailed,
                            "The pinned magazine launch seam refused the selected weapon.");
                    }
                    weapon = GetSerializableActiveWeapons()
                        .Where(candidate => !beforeIds.Contains(candidate.UniqueID))
                        .Where(candidate => string.Equals(
                            candidate._ap?._ammunitionFileName,
                            ammunition._ap._ammunitionFileName,
                            StringComparison.Ordinal))
                        .OrderByDescending(candidate => candidate.UniqueID)
                        .FirstOrDefault();
                    if (weapon is null)
                    {
                        return Failure(
                            ProtocolErrorCodes.AutomationPostconditionFailed,
                            "The magazine launch completed without a new taskforce-registered weapon.");
                    }
                    return CompleteExperimentalWeaponLaunch(
                        unit,
                        weaponSystemIndex,
                        containerIndex,
                        desiredType,
                        weapon,
                        intendedTarget,
                        desiredAmmunitionId,
                        "The magazine launch returned a weapon that was not marked launched.");
                }
            }
        }

        var requestedDescription = string.IsNullOrEmpty(desiredAmmunitionId)
            ? desiredType.ToString()
            : $"{desiredType} ammunition '{desiredAmmunitionId}'";
        return Failure(
            ProtocolErrorCodes.AutomationPreconditionFailed,
            $"No loaded or magazine-ready {requestedDescription} was found on the requested player unit scope.");
    }

    private static CommandResponsePayload CompleteExperimentalWeaponLaunch(
        ObjectBase unit,
        int weaponSystemIndex,
        int containerIndex,
        Ammunition.Type desiredType,
        WeaponBase weapon,
        ObjectBase? intendedTarget,
        string requestedAmmunitionId,
        string notLaunchedMessage)
    {
        if (!weapon.isLaunched())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPostconditionFailed,
                notLaunchedMessage);
        }
        var activeWeapons = GetActiveWeapons();
        if (!activeWeapons.Contains(weapon))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPostconditionFailed,
                "The launched weapon was not registered in Sea Power's active object world.");
        }
        var serializableWeapons = GetSerializableActiveWeapons();
        var nativeSaveRegistered = serializableWeapons.Contains(weapon);
        var launchedAmmunitionId = weapon._ap?._ammunitionFileName ?? string.Empty;
        if (!string.IsNullOrEmpty(requestedAmmunitionId)
            && !string.Equals(
                launchedAmmunitionId,
                requestedAmmunitionId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPostconditionFailed,
                $"The launch seam returned ammunition '{launchedAmmunitionId}' instead of requested ammunition '{requestedAmmunitionId}'.");
        }
        return Success(new ExperimentalWeaponLaunchResultPayload
        {
            Action = GameCommandTypes.LaunchExperimentalProbeWeapon,
            Outcome = "completed",
            Postcondition = nativeSaveRegistered
                ? "serializable-active-weapon-launched"
                : "active-weapon-launched",
            UnitId = StableUnitId(unit),
            WeaponSystemIndex = weaponSystemIndex,
            ContainerIndex = containerIndex,
            WeaponType = desiredType.ToString(),
            AmmunitionId = launchedAmmunitionId,
            ActiveWeaponId = $"weapon:{weapon.UniqueID}",
            IntendedTargetUnitId = intendedTarget is null
                ? string.Empty
                : StableUnitId(intendedTarget),
            ActiveWeaponCount = activeWeapons.Length,
            NativeSaveRegistered = nativeSaveRegistered,
            SerializableActiveWeaponCount = serializableWeapons.Length,
        });
    }

    private static WeaponBase[] GetActiveWeapons()
    {
        return Singleton<ObjectsManager>.InstanceExists()
            ? Singleton<ObjectsManager>.Instance._listOfAllObjects
                .OfType<WeaponBase>()
                .Where(weapon => weapon.isLaunched() && !weapon.IsDestroyed)
                .Distinct()
                .ToArray()
            : [];
    }

    private static WeaponBase[] GetSerializableActiveWeapons()
    {
        return new[]
            {
                Globals._playerTaskforce,
                Globals._enemyTaskforce,
                Globals._neutralTaskforce,
            }
            .Where(taskforce => taskforce is not null)
            .SelectMany(taskforce => taskforce._taskforceWeapons)
            .OfType<WeaponBase>()
            .Where(weapon => weapon._ap?._type is Ammunition.Type.Missile
                or Ammunition.Type.Torpedo
                or Ammunition.Type.Bomb)
            .Where(weapon => weapon.isLaunched() && !weapon.IsDestroyed)
            .Distinct()
            .ToArray();
    }

    private static CommandResponsePayload LoadExperimentalProbe()
    {
        if (!RuntimeDeterministicHookFingerprintProvider.IsVerified("native-save-load"))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The pinned native save/load seam is not verified for this game build.");
        }
        if (!Singleton<MissionManager>.InstanceExists())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A tactical mission is required for the save/load probe reload.");
        }
        var savePath = RuntimeInstanceConfiguration.Current
            .GetExperimentalSavePath(ExperimentalProbeSaveFileName);
        if (!File.Exists(savePath))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "The experimental save file does not exist.");
        }
        var before = GetWorldSnapshot();
        Globals._lastAutoSave = savePath;
        _deferredAction = () =>
        {
            Globals.currentMissionFilePath = savePath;
            Singleton<MissionManager>.Instance.UnloadMissionAndReload();
        };
        return Success(new ExperimentalSaveLoadProbeResultPayload
        {
            Action = GameCommandTypes.LoadExperimentalProbe,
            Outcome = "scheduled",
            Postcondition = "native-mission-reload-scheduled",
            SaveFileName = ExperimentalProbeSaveFileName,
            SaveFilePath = savePath,
            SaveFileSha256 = ComputeFileSha256(savePath),
            PreTransitionWeaponCount = before.Weapons.Length,
            PreTransitionWeaponsSha256 = before.WeaponsSha256,
            PreTransitionCanonicalSha256 = before.CanonicalSha256,
        });
    }

    private static ScenarioAutomationStatePayload GetScenarioAutomationState()
    {
        var currentWindow = Singleton<MenuSystemViewModel>.InstanceExists()
            ? Singleton<MenuSystemViewModel>.Instance.CurrentWindow
            : null;
        var briefingViewModel = (currentWindow as MissionBriefView)?.DataContext
            as MissionBriefViewModel;
        var scenarioPath = string.IsNullOrWhiteSpace(Globals.currentMissionFilePath)
            ? string.Empty
            : NormalizeVirtualPath(Singleton<FileManager>.Instance.GetRelativePath(
                Globals.currentMissionFilePath));
        return new ScenarioAutomationStatePayload
        {
            SceneName = SceneManager.GetActiveScene().name ?? string.Empty,
            CurrentWindowType = currentWindow?.GetType().Name ?? string.Empty,
            ScenarioId = ScenarioId(scenarioPath),
            ScenarioPath = scenarioPath,
            BriefingVisible = briefingViewModel?.hasContent ?? false,
            BriefingPaused = Singleton<SceneCreator>.InstanceExists()
                && Singleton<SceneCreator>.Instance._pauseForBriefing,
            BriefingCanContinue = briefingViewModel?.PlayCommand?.CanExecute(null) ?? false,
            GameMainAvailable = Singleton<GameMain>.InstanceExists(),
        };
    }

    private static UiInspectionPayload GetUiInspection()
    {
        var currentWindow = Singleton<MenuSystemViewModel>.InstanceExists()
            ? Singleton<MenuSystemViewModel>.Instance.CurrentWindow
            : null;
        var main = Globals._mainGameViewModel;
        var selected = Singleton<RenderPosition>.InstanceExists()
            ? Singleton<RenderPosition>.Instance.getSelectedObject()
            : null;
        return new UiInspectionPayload
        {
            SceneName = SceneManager.GetActiveScene().name ?? string.Empty,
            CurrentWindowType = currentWindow?.GetType().Name ?? string.Empty,
            GameMainAvailable = Singleton<GameMain>.InstanceExists(),
            MainGameViewAvailable = main is not null,
            IsPaused = GameTime.IsPaused(),
            SimulationTimeSeconds = Finite(GameTime.time),
            TimeCompression = Finite(GameTime.TimeCompression),
            MapVisible = main?.MapVisible ?? false,
            HudVisible = MainGameViewModel.GetHudVisible(),
            FormationManagerVisible = main?.ShowFormationManager ?? false,
            EventLogAvailable = main?.EventLog is not null,
            SelectedUnitId = selected is not null && selected.isUnit()
                ? StableUnitId(selected)
                : string.Empty,
            ScreenWidth = Screen.width,
            ScreenHeight = Screen.height,
            PlayerTaskforceId = RuntimeOwnedMutationAdapter.GetPlayerTaskforceId(),
        };
    }

    private static CommandResponsePayload CaptureGameScreenshot(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<CaptureGameScreenshotRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new CaptureGameScreenshotRequestPayload();
        var maxWidth = request.MaxWidth < 320 ? 1920 : Math.Min(request.MaxWidth, 3840);
        var path = RuntimeInstanceConfiguration.Current.GetExperimentalSavePath(ScreenshotFileName);
        var sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
        _endOfFrameAction = () => WriteScreenshotPng(path, maxWidth);
        return Success(new CaptureGameScreenshotResultPayload
        {
            Outcome = "scheduled",
            Postcondition = "screenshot-capture-scheduled",
            FileName = ScreenshotFileName,
            FilePath = path,
            SceneName = sceneName,
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private static void WriteScreenshotPng(string path, int maxWidth)
    {
        Texture2D? source = null;
        Texture2D? scaled = null;
        try
        {
            source = ScreenCapture.CaptureScreenshotAsTexture();
            if (source is null)
            {
                UnityEngine.Debug.LogError(
                    "[Sea Power Runtime Bridge] screenshot capture returned no texture.");
                return;
            }

            var texture = source;
            if (source.width > maxWidth && source.width > 0)
            {
                var height = Math.Max(1, source.height * maxWidth / source.width);
                scaled = ScaleTexture(source, maxWidth, height);
                texture = scaled;
            }

            var png = texture.EncodeToPNG();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllBytes(path, png);
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogError(
                $"[Sea Power Runtime Bridge] screenshot capture failed: {exception}");
        }
        finally
        {
            if (scaled is not null)
            {
                UnityEngine.Object.Destroy(scaled);
            }
            if (source is not null)
            {
                UnityEngine.Object.Destroy(source);
            }
        }
    }

    private static Texture2D ScaleTexture(Texture2D source, int width, int height)
    {
        var render = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(source, render);
            RenderTexture.active = render;
            var scaled = new Texture2D(width, height, TextureFormat.RGB24, false);
            scaled.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            scaled.Apply();
            return scaled;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(render);
        }
    }

    private static CommandResponsePayload FocusCameraOnUnit(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<FocusCameraOnUnitRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new FocusCameraOnUnitRequestPayload();
        var requestedId = request.UnitId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestedId))
        {
            return Failure(
                ProtocolErrorCodes.InvalidMessage,
                "A unit ID is required to focus the camera.");
        }
        if (!Singleton<RenderPosition>.InstanceExists()
            || !Singleton<ObjectsManager>.InstanceExists())
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "Camera focus requires a loaded tactical scene with RenderPosition.");
        }

        var matches = Singleton<ObjectsManager>.Instance._listOfAllObjects
            .Where(item => item is not null && item.isUnit())
            .Where(item => string.Equals(StableUnitId(item), requestedId, StringComparison.Ordinal)
                || string.Equals(UnitId(item.UniqueID), requestedId, StringComparison.Ordinal)
                || string.Equals($"object:{item.UniqueID}", requestedId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                $"Unit ID '{requestedId}' resolved to {matches.Length} live objects.");
        }

        Singleton<RenderPosition>.Instance.switchToObject(matches[0], performSmoothMove: false);
        var selected = Singleton<RenderPosition>.Instance.getSelectedObject();
        var selectedId = selected is not null && selected.isUnit()
            ? StableUnitId(selected)
            : string.Empty;
        if (!string.Equals(selectedId, StableUnitId(matches[0]), StringComparison.Ordinal))
        {
            return Failure(
                ProtocolErrorCodes.AutomationPostconditionFailed,
                "Camera switch ran without selecting the requested unit.");
        }

        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.FocusCameraOnUnit,
            Outcome = "completed",
            Postcondition = "camera-focused-on-unit",
            Scenario = GetScenarioAutomationState(),
        });
    }

    private static CommandResponsePayload SetTacticalHudPanel(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<SetTacticalHudPanelRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new SetTacticalHudPanelRequestPayload();
        var panel = request.Panel?.Trim().ToLowerInvariant() ?? string.Empty;
        var main = Globals._mainGameViewModel;
        if (main is null && panel is not "pause-resume")
        {
            return Failure(
                ProtocolErrorCodes.AutomationPreconditionFailed,
                "A loaded tactical HUD is required.");
        }

        string postcondition;
        switch (panel)
        {
            case "map":
                main!.ToggleMapOpen();
                postcondition = main.MapVisible ? "tactical-map-visible" : "tactical-map-hidden";
                break;
            case "formation":
                main!.ShowFormationManager = !main.ShowFormationManager;
                postcondition = main.ShowFormationManager
                    ? "formation-manager-visible"
                    : "formation-manager-hidden";
                break;
            case "hud":
                MainGameViewModel.SetHudVisible(!MainGameViewModel.GetHudVisible());
                postcondition = MainGameViewModel.GetHudVisible() ? "hud-visible" : "hud-hidden";
                break;
            case "pause-resume":
                var currentWindow = Singleton<MenuSystemViewModel>.InstanceExists()
                    ? Singleton<MenuSystemViewModel>.Instance.CurrentWindow
                    : null;
                if (currentWindow is not PauseMenuView pauseView
                    || pauseView.DataContext is not PauseMenuViewModel pauseModel
                    || pauseModel.ResumeCommand is null
                    || !pauseModel.ResumeCommand.CanExecute(null))
                {
                    return Failure(
                        ProtocolErrorCodes.AutomationPreconditionFailed,
                        "The pause menu is not open with a resumable command.");
                }
                pauseModel.ResumeCommand.Execute(null);
                postcondition = "pause-menu-resume-requested";
                break;
            default:
                return Failure(
                    ProtocolErrorCodes.InvalidMessage,
                    "Panel must be map, formation, hud, or pause-resume.");
        }

        return Success(new UiAutomationActionResultPayload
        {
            Action = GameCommandTypes.SetTacticalHudPanel,
            Outcome = "completed",
            Postcondition = postcondition,
            Scenario = GetScenarioAutomationState(),
        });
    }

    private static IEnumerable<Mission> EnumerateMissions(
        IEnumerable<IMissionTreeViewItem> items)
    {
        foreach (var item in items)
        {
            if (item is Mission mission)
            {
                yield return mission;
            }
            else if (item is MissionFolder folder)
            {
                foreach (var child in EnumerateMissions(folder.Missions))
                {
                    yield return child;
                }
            }
        }
    }

    private static string ScenarioId(string? path)
    {
        var effectivePath = path ?? string.Empty;
        if (Path.IsPathRooted(effectivePath) && Singleton<FileManager>.InstanceExists())
        {
            effectivePath = Singleton<FileManager>.Instance.GetRelativePath(effectivePath);
        }
        var normalized = NormalizeVirtualPath(effectivePath);
        if (normalized.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : $"scenario:{normalized}";
    }

    private static string NormalizeScenarioId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();
        return normalized.StartsWith("scenario:", StringComparison.Ordinal)
            ? normalized
            : ScenarioId(normalized);
    }

    private static string NormalizeVirtualPath(string? path) => (path ?? string.Empty)
        .Replace('\\', '/')
        .TrimStart('/')
        .ToLowerInvariant();

    private static Dictionary<Taskforce, string> BuildTaskforceIds(Taskforce[] taskforces)
    {
        var ids = new Dictionary<Taskforce, string>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < taskforces.Length; index++)
        {
            var taskforce = taskforces[index];
            var identityName = string.IsNullOrWhiteSpace(taskforce._nameInMissionFile)
                ? $"{taskforce.Side.ToString().ToLowerInvariant()}-{index + 1}"
                : taskforce._nameInMissionFile.Trim();
            var baseId = $"taskforce:{identityName}";
            occurrences.TryGetValue(baseId, out var occurrence);
            occurrence++;
            occurrences[baseId] = occurrence;
            ids[taskforce] = occurrence == 1 ? baseId : $"{baseId}#{occurrence}";
        }

        return ids;
    }

    private static UnitSnapshotPayload CaptureUnit(
        ObjectBase unit,
        IReadOnlyDictionary<Taskforce, string> taskforceIds)
    {
        var position = unit._geoPosition;
        var taskforceId = unit._taskforce is not null
            && taskforceIds.TryGetValue(unit._taskforce, out var foundTaskforceId)
                ? foundTaskforceId
                : null;
        var parameters = unit._obp;
        var campaignTag = unit._campaignTag ?? string.Empty;
        var currentFuelMass = parameters is null ? null : Finite(parameters._currentFuelMass);
        var maximumFuelMass = parameters is null ? null : Finite(parameters._maxFuel);
        var fuelFraction = currentFuelMass.HasValue
            && maximumFuelMass.HasValue
            && maximumFuelMass.Value > 0
                ? Finite(currentFuelMass.Value / maximumFuelMass.Value)
                : null;
        var overallDamage = Finite(unit._overallDamage);
        var totalDamage = Finite(unit._totalDamagePoints);
        var damageFraction = overallDamage.HasValue
            && totalDamage.HasValue
            && totalDamage.Value > 0
                ? Finite(overallDamage.Value / totalDamage.Value)
                : null;
        var flightDeck = parameters?._flightDeck;

        var snapshot = new UnitSnapshotPayload
        {
            Id = StableUnitId(unit),
            GameUniqueId = unit.UniqueID,
            IdentityProvenance = string.IsNullOrWhiteSpace(campaignTag)
                ? "game-unique-id"
                : "campaign-tag",
            CampaignTag = campaignTag,
            TaskforceId = taskforceId,
            Name = unit.getName() ?? string.Empty,
            ClassName = unit.ClassName ?? string.Empty,
            Type = unit.GetType().Name,
            Nation = unit.getNation() ?? string.Empty,
            UnitFolder = parameters?._objectIniFolder ?? string.Empty,
            UnitTypeId = parameters?._objectIniName ?? string.Empty,
            VariantId = parameters?._objectVariantReference ?? string.Empty,
            LoadoutId = parameters?._loadoutVariant ?? string.Empty,
            Latitude = Finite(position._latitude),
            Longitude = Finite(position._longitude),
            AltitudeFeet = Finite(unit.getHeightInFeet()),
            HeadingDegrees = Finite(unit.getHeading()),
            SpeedKnots = Finite(unit.getVelocityInKnots()),
            CurrentOrder = unit.CurrentOrder.Value?.OrderType.ToString() ?? string.Empty,
            CurrentOrderTargetId = unit.CurrentOrder.Value?.TargetUnit is { } orderTarget
                ? StableUnitId(orderTarget)
                : null,
            Waypoints = unit.ExportWaypoints()
                .Select((waypoint, index) => CaptureWaypoint(waypoint, index))
                .ToArray(),
            CurrentState = unit.StateMachine?.CurrentStateName ?? string.Empty,
            OverallDamagePoints = overallDamage,
            TotalDamagePoints = totalDamage,
            DamageFraction = damageFraction,
            CurrentFuelMass = currentFuelMass,
            MaximumFuelMass = maximumFuelMass,
            FuelFraction = fuelFraction,
            Ammunition = unit.AmmunitionAmountDictionary
                .Where(store => !string.IsNullOrWhiteSpace(store.Key))
                .Select(store => new AmmunitionStoreSnapshotPayload
                {
                    AmmunitionId = store.Key,
                    Quantity = store.Value,
                })
                .OrderBy(store => store.AmmunitionId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            HostedAircraft = flightDeck?._vehiclesOnBoard
                .Where(vehicle => vehicle is not null
                    && !string.IsNullOrWhiteSpace(vehicle._fileName)
                    && vehicle.Numbers > 0)
                .Select(vehicle => new HostedAircraftSnapshotPayload
                {
                    UnitTypeId = vehicle._fileName,
                    Type = vehicle._objectType.ToString(),
                    Quantity = vehicle.Numbers,
                })
                .OrderBy(vehicle => vehicle.UnitTypeId, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [],
            HostedAircraftCapacity = flightDeck?.MaxAircraftOnBoard,
            IsDestroyed = unit.IsDestroyed,
        };
        PopulateSubsystemHashes(snapshot);
        return snapshot;
    }

    private static ContactSnapshotPayload CaptureContact(
        Vehicle vehicle,
        IReadOnlyDictionary<Taskforce, string> taskforceIds)
    {
        var position = vehicle.PositionEstimate;
        var positionState = "unavailable";
        if (position.HasValue)
        {
            positionState = vehicle.DetectedPosition.HasValue
                && vehicle.DetectedPosition.Value.Item3 == position.Value.Item3
                    ? "detected"
                    : "estimated";
        }

        var detectedTaskforce = vehicle.Side?.Value;
        var detectedTaskforceId = detectedTaskforce is not null
            && taskforceIds.TryGetValue(detectedTaskforce, out var foundTaskforceId)
                ? foundTaskforceId
                : null;
        var altitude = vehicle.Altitude;
        var entityManager = Unity.Entities.World.DefaultGameObjectInjectionWorld.EntityManager;
        var sources = vehicle.CurrentTracks()
            .Where(entityManager.HasComponent<TrackComponent>)
            .Select(entity => entityManager.GetComponentData<TrackComponent>(entity))
            .Select(track => new ContactTrackSourcePayload
            {
                SensorType = track.SensorType.ToString(),
                SensorDisplayName = track.SensorDisplayName.ToString(),
                DetectionKind = track.Kind.ToString(),
                ObservationTimeSeconds = Finite(track.Epoch.ToLive()),
                MaximumAgeSeconds = Finite(track.MaxAge),
                Confidence = Finite(track.Confidence),
                ObservationCount = 1,
            })
            .GroupBy(source => new
            {
                source.SensorType,
                source.SensorDisplayName,
                source.DetectionKind,
            })
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(source => source.ObservationTimeSeconds)
                    .First();
                latest.ObservationCount = group.Count();
                return latest;
            })
            .OrderByDescending(source => source.ObservationTimeSeconds)
            .ThenBy(source => source.SensorType, StringComparer.Ordinal)
            .ThenBy(source => source.SensorDisplayName, StringComparer.Ordinal)
            .ToArray();

        var snapshot = new ContactSnapshotPayload
        {
            Id = $"player-track:{vehicle.Id}",
            TrackNumber = vehicle.Id,
            IsOwnForce = vehicle.Object?._taskforce == Globals._playerTaskforce,
            IsDormant = vehicle.IsDormant.Value,
            IsIdentified = vehicle.Identified.Value,
            IsClassified = vehicle.IsClassified,
            DetectedTaskforceId = detectedTaskforceId,
            DetectedClassName = vehicle.Class?.Value ?? string.Empty,
            Relationship = vehicle.CurrentRelationship().ToString(),
            Latitude = position.HasValue ? Finite(position.Value.Item1._latitude) : null,
            Longitude = position.HasValue ? Finite(position.Value.Item1._longitude) : null,
            AltitudeMeters = position.HasValue ? Finite(position.Value.Item1._height) : null,
            PositionState = positionState,
            PositionMajorStdDevMeters = position.HasValue
                ? Finite(position.Value.Item2.MajorStdDev)
                : null,
            PositionMinorStdDevMeters = position.HasValue
                ? Finite(position.Value.Item2.MinorStdDev)
                : null,
            AltitudeErrorMeters = altitude.HasValue
                ? Finite(altitude.Value.Value.Error)
                : null,
            InitialDetectionTimeSeconds = Finite(vehicle.InitialDetectionEpoch),
            LastUpdateTimeSeconds = Finite(vehicle.UpdateEpoch),
            IsObservedVisually = vehicle.IsObservedByVisual,
            DetectingSensors = vehicle.DetectingSensors.ToString(),
            TrackSources = sources,
        };
        snapshot.ObservationSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("id", snapshot.Id),
            FingerprintField("scope", snapshot.ObservationScope),
            FingerprintField("own-force", snapshot.IsOwnForce),
            FingerprintField("dormant", snapshot.IsDormant),
            FingerprintField("identified", snapshot.IsIdentified),
            FingerprintField("classified", snapshot.IsClassified),
            FingerprintField("detected-taskforce", snapshot.DetectedTaskforceId),
            FingerprintField("detected-class", snapshot.DetectedClassName),
            FingerprintField("relationship", snapshot.Relationship),
            FingerprintField("latitude", snapshot.Latitude),
            FingerprintField("longitude", snapshot.Longitude),
            FingerprintField("altitude-meters", snapshot.AltitudeMeters),
            FingerprintField("position-state", snapshot.PositionState),
            FingerprintField("major-stddev-meters", snapshot.PositionMajorStdDevMeters),
            FingerprintField("minor-stddev-meters", snapshot.PositionMinorStdDevMeters),
            FingerprintField("altitude-error-meters", snapshot.AltitudeErrorMeters),
            FingerprintField("initial-detection", snapshot.InitialDetectionTimeSeconds),
            FingerprintField("last-update", snapshot.LastUpdateTimeSeconds),
            FingerprintField("visual", snapshot.IsObservedVisually),
            FingerprintField("detecting-sensors", snapshot.DetectingSensors),
        }.Concat(snapshot.TrackSources.Select((source, index) => FingerprintField(
            $"source.{index}",
            string.Join(":", new object?[]
            {
                source.SensorType,
                source.SensorDisplayName,
                source.DetectionKind,
                source.ObservationTimeSeconds,
                source.MaximumAgeSeconds,
                source.Confidence,
                source.ObservationCount,
            }.Select(value => value is IFormattable formattable
                ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                : value?.ToString() ?? string.Empty))))));
        return snapshot;
    }

    private static PlayerEventLogSnapshotPayload CapturePlayerEventLog()
    {
        const int maximumRecordsPerCollection = 100;
        var viewModel = Globals._mainGameViewModel?.EventLog;
        var nativeEventLog = Singleton<EventLog>.InstanceExists()
            ? Singleton<EventLog>.Instance
            : null;
        if (viewModel is null || nativeEventLog is null)
        {
            var unavailable = new PlayerEventLogSnapshotPayload
            {
                State = "unavailable",
            };
            unavailable.ObservationSha256 = CapabilityFingerprint.ComputeSha256(new[]
            {
                FingerprintField("scope", unavailable.ObservationScope),
                FingerprintField("state", unavailable.State),
            });
            return unavailable;
        }

        var alerts = viewModel.Alerts
            .Take(maximumRecordsPerCollection)
            .Select((alert, index) => new PlayerAlertSnapshotPayload
            {
                Index = index,
                TimeUtc = alert.Time.ToUniversalTime().ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture),
                Priority = alert.Priority,
                Text = alert.GetAlertText() ?? string.Empty,
                RaisingObjectId = ObjectSnapshotId(alert.RaisingObject),
                AboutObjectId = ObjectSnapshotId(alert.AboutObject),
            })
            .ToArray();
        var intelligence = nativeEventLog.Intelligence
            .Take(maximumRecordsPerCollection)
            .Select((item, index) => new PlayerIntelligenceSnapshotPayload
            {
                Index = index,
                TimeUtc = item.Time.ToUniversalTime().ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture),
                Text = item.IntelligenceText ?? string.Empty,
            })
            .ToArray();
        var snapshot = new PlayerEventLogSnapshotPayload
        {
            State = "ready",
            AlertCount = viewModel.Alerts.Count,
            IntelligenceCount = nativeEventLog.Intelligence.Count,
            IsTruncated = viewModel.Alerts.Count > alerts.Length
                || nativeEventLog.Intelligence.Count > intelligence.Length,
            Alerts = alerts,
            Intelligence = intelligence,
        };
        snapshot.ObservationSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("scope", snapshot.ObservationScope),
            FingerprintField("state", snapshot.State),
            FingerprintField("alert-count", snapshot.AlertCount),
            FingerprintField("intelligence-count", snapshot.IntelligenceCount),
            FingerprintField("truncated", snapshot.IsTruncated),
        }.Concat(snapshot.Alerts.Select(alert => FingerprintField(
            $"alert.{alert.Index}",
            string.Join(":", alert.TimeUtc, alert.Priority, alert.Text,
                alert.RaisingObjectId, alert.AboutObjectId))))
        .Concat(snapshot.Intelligence.Select(item => FingerprintField(
            $"intelligence.{item.Index}",
            string.Join(":", item.TimeUtc, item.Text)))));
        return snapshot;
    }

    private static WaypointSnapshotPayload CaptureWaypoint(WaypointData waypoint, int index)
    {
        return new WaypointSnapshotPayload
        {
            Index = index,
            Name = waypoint.wpName ?? string.Empty,
            Latitude = Finite(waypoint._geoposition._latitude),
            Longitude = Finite(waypoint._geoposition._longitude),
            HeightMeters = Finite(waypoint._geoposition._height),
            HeightState = waypoint._heightState.ToString(),
            IsRelative = waypoint._isRelative,
            IsReachable = waypoint._reachable,
            ParentUnitId = waypoint._parentObject is null
                ? null
                : StableUnitId(waypoint._parentObject),
            SetCourse = waypoint._setCourse,
            CourseDegrees = Finite(waypoint._course),
            SetTurn = waypoint._setTurn,
            Distance = Finite(waypoint._distance),
            TimeToFollowSeconds = Finite(waypoint._timeToFollow),
            IsAutogenerated = waypoint._isAutogenerated,
            EngagementAmmunitionId = waypoint._ammunitionForEngage ?? string.Empty,
            EngagementTargetId = waypoint._targetObject is null
                ? null
                : StableUnitId(waypoint._targetObject),
            Salvo = waypoint._salvo,
            SalvoType = waypoint._salvoType.ToString(),
            AttackOnlyDetected = waypoint._attackOnlyDetected,
            FormationAttack = waypoint._formationAttack,
        };
    }

    private static WeaponSnapshotPayload CaptureWeapon(WeaponBase weapon)
    {
        var position = weapon._geoPosition;
        var ammunition = weapon._ap;
        var snapshot = new WeaponSnapshotPayload
        {
            Id = $"weapon:{weapon.UniqueID}",
            GameUniqueId = weapon.UniqueID,
            Type = weapon.GetType().Name,
            AmmunitionId = ammunition?._ammunitionFileName ?? string.Empty,
            DisplayName = ammunition?._displayedName ?? string.Empty,
            WeaponType = ammunition?._type.ToString() ?? string.Empty,
            WeaponSubtype = ammunition?._subType.ToString() ?? string.Empty,
            GuidanceType = ammunition?._guidanceType.ToString() ?? string.Empty,
            PrimaryTargetType = ammunition?._targetType.ToString() ?? string.Empty,
            SecondaryTargetType = ammunition?._secondaryTargetType.ToString() ?? string.Empty,
            LaunchPlatformId = ObjectSnapshotId(weapon._launchPlatform),
            IntendedTargetId = ObjectSnapshotId(weapon.CurrentIntendedTargetObject),
            Latitude = Finite(position._latitude),
            Longitude = Finite(position._longitude),
            AltitudeFeet = Finite(weapon.getHeightInFeet()),
            HeadingDegrees = Finite(weapon.getHeading()),
            SpeedKnots = Finite(weapon.getVelocityInKnots()),
            FlightStage = weapon.getFlightStage().ToString(),
            LaunchTimeSeconds = Finite(weapon._launchTime),
            TraveledDistance = Finite(weapon._traveledDistance),
            RemainingRangeFraction = Finite(weapon._remainingRangePercent),
            IsLaunched = weapon.isLaunched(),
            IsDestroyed = weapon.IsDestroyed,
        };
        snapshot.IdentitySha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("id", snapshot.Id),
            FingerprintField("ammunition", snapshot.AmmunitionId),
            FingerprintField("type", snapshot.Type),
            FingerprintField("launch-platform", snapshot.LaunchPlatformId),
        });
        snapshot.KinematicsSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("latitude", snapshot.Latitude),
            FingerprintField("longitude", snapshot.Longitude),
            FingerprintField("altitude-feet", snapshot.AltitudeFeet),
            FingerprintField("heading-degrees", snapshot.HeadingDegrees),
            FingerprintField("speed-knots", snapshot.SpeedKnots),
        });
        snapshot.EngagementSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("target", snapshot.IntendedTargetId),
            FingerprintField("flight-stage", snapshot.FlightStage),
            FingerprintField("traveled-distance", snapshot.TraveledDistance),
            FingerprintField("remaining-range-fraction", snapshot.RemainingRangeFraction),
            FingerprintField("destroyed", snapshot.IsDestroyed),
        });
        return snapshot;
    }

    private static string? ObjectSnapshotId(ObjectBase? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.isUnit()
            ? StableUnitId(value)
            : value.isWeapon()
                ? $"weapon:{value.UniqueID}"
                : $"object:{value.UniqueID}";
    }

    private static string UnitId(int uniqueId) => $"unit:{uniqueId}";

    private static string StableUnitId(ObjectBase unit)
    {
        return string.IsNullOrWhiteSpace(unit._campaignTag)
            ? UnitId(unit.UniqueID)
            : $"campaign-unit:{unit._campaignTag.Trim()}";
    }

    private static void PopulateSubsystemHashes(UnitSnapshotPayload unit)
    {
        unit.IdentitySha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("id", unit.Id),
            FingerprintField("game-unique-id", unit.GameUniqueId),
            FingerprintField("identity-provenance", unit.IdentityProvenance),
            FingerprintField("campaign-tag", unit.CampaignTag),
            FingerprintField("taskforce-id", unit.TaskforceId),
            FingerprintField("unit-folder", unit.UnitFolder),
            FingerprintField("unit-type-id", unit.UnitTypeId),
            FingerprintField("variant-id", unit.VariantId),
            FingerprintField("loadout-id", unit.LoadoutId),
            FingerprintField("nation", unit.Nation),
        });

        unit.KinematicsSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("latitude", unit.Latitude),
            FingerprintField("longitude", unit.Longitude),
            FingerprintField("altitude-feet", unit.AltitudeFeet),
            FingerprintField("heading-degrees", unit.HeadingDegrees),
            FingerprintField("speed-knots", unit.SpeedKnots),
            FingerprintField("current-order", unit.CurrentOrder),
            FingerprintField("current-state", unit.CurrentState),
        });
        unit.OrdersSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("current-order", unit.CurrentOrder),
            FingerprintField("current-order-target", unit.CurrentOrderTargetId),
        }.Concat(unit.Waypoints.Select(waypoint => FingerprintField(
            $"waypoint.{waypoint.Index}",
            string.Join(":", new object?[]
            {
                waypoint.Name,
                waypoint.Latitude,
                waypoint.Longitude,
                waypoint.HeightMeters,
                waypoint.HeightState,
                waypoint.IsRelative,
                waypoint.IsReachable,
                waypoint.ParentUnitId,
                waypoint.SetCourse,
                waypoint.CourseDegrees,
                waypoint.SetTurn,
                waypoint.Distance,
                waypoint.TimeToFollowSeconds,
                waypoint.IsAutogenerated,
                waypoint.EngagementAmmunitionId,
                waypoint.EngagementTargetId,
                waypoint.Salvo,
                waypoint.SalvoType,
                waypoint.AttackOnlyDetected,
                waypoint.FormationAttack,
            }.Select(value => value is IFormattable formattable
                ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                : value?.ToString() ?? string.Empty))))));

        var logisticsFields = new List<KeyValuePair<string, string?>>
        {
            FingerprintField("current-fuel-mass", unit.CurrentFuelMass),
            FingerprintField("maximum-fuel-mass", unit.MaximumFuelMass),
            FingerprintField("fuel-fraction", unit.FuelFraction),
            FingerprintField("hosted-aircraft-capacity", unit.HostedAircraftCapacity),
        };
        for (var index = 0; index < unit.Ammunition.Length; index++)
        {
            logisticsFields.Add(FingerprintField(
                $"ammunition.{index}.id",
                unit.Ammunition[index].AmmunitionId));
            logisticsFields.Add(FingerprintField(
                $"ammunition.{index}.quantity",
                unit.Ammunition[index].Quantity));
        }
        for (var index = 0; index < unit.HostedAircraft.Length; index++)
        {
            logisticsFields.Add(FingerprintField(
                $"hosted-aircraft.{index}.unit-type-id",
                unit.HostedAircraft[index].UnitTypeId));
            logisticsFields.Add(FingerprintField(
                $"hosted-aircraft.{index}.type",
                unit.HostedAircraft[index].Type));
            logisticsFields.Add(FingerprintField(
                $"hosted-aircraft.{index}.quantity",
                unit.HostedAircraft[index].Quantity));
        }
        unit.LogisticsSha256 = CapabilityFingerprint.ComputeSha256(logisticsFields);

        unit.DamageSha256 = CapabilityFingerprint.ComputeSha256(new[]
        {
            FingerprintField("overall-damage-points", unit.OverallDamagePoints),
            FingerprintField("total-damage-points", unit.TotalDamagePoints),
            FingerprintField("damage-fraction", unit.DamageFraction),
            FingerprintField("is-destroyed", unit.IsDestroyed),
        });
    }

    private static KeyValuePair<string, string?> FingerprintField(
        string name,
        object? value)
    {
        return new KeyValuePair<string, string?>(name, value switch
        {
            null => null,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(
                null,
                System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        });
    }

    private static double? Finite(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = sha256.ComputeHash(stream);
        var result = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            result.Append(value.ToString(
                "x2",
                System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private static CommandResponsePayload Success<T>(T payload)
    {
        return new CommandResponsePayload
        {
            Success = true,
            ResultJson = JsonSerializer.Serialize(payload, JsonOptions),
        };
    }

    private static CommandResponsePayload Failure(
        string code,
        string message,
        string? errorId = null)
    {
        return new CommandResponsePayload
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message,
            ErrorId = errorId,
        };
    }
}
