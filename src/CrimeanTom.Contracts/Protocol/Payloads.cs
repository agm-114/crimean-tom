namespace CrimeanTom.Protocol;

public sealed class GameHelloPayload
{
    public string ClientInstanceId { get; set; } = string.Empty;

    public string RuntimeInstanceId { get; set; } = "default";

    public string GamePipeName { get; set; } = ProtocolConstants.GamePipeName;

    public string PersistentDataPath { get; set; } = string.Empty;

    public string ExperimentalSaveDirectory { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string GameVersion { get; set; } = string.Empty;

    public string InitialScene { get; set; } = string.Empty;

    public string ColliderCacheDirectory { get; set; } = string.Empty;
}

public sealed class HandshakeResponsePayload
{
    public bool Accepted { get; set; }

    public string? SessionId { get; set; }

    public int ProtocolVersion { get; set; }

    public int MaximumFrameBytes { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

public sealed class CommandResponsePayload
{
    public bool Success { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ErrorId { get; set; }

    public string ResultJson { get; set; } = "{}";
}

public sealed class CancelCommandPayload
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class PongPayload
{
    public string PluginVersion { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public int InitializedMainThreadId { get; set; }

    public int ExecutingThreadId { get; set; }

    public bool IsMainThread { get; set; }

    public long RespondedAtUnixMilliseconds { get; set; }
}

public sealed class CapabilitiesPayload
{
    public int ProtocolVersion { get; set; }

    public string[] SupportedCommands { get; set; } = Array.Empty<string>();

    public string[] SupportedUiAutomationCommands { get; set; } = Array.Empty<string>();

    public bool UiAutomationEnabled { get; set; }

    public string ExperimentalRuntimeMode { get; set; } = "Disabled";

    public string[] SupportedExperimentalCommands { get; set; }
        = Array.Empty<string>();

    public bool MutatingCommandsEnabled { get; set; }

    public string MutationMode { get; set; } = "disabled";

    public string[] SupportedMutationCommands { get; set; } = Array.Empty<string>();

    public OwnedSurfaceMaterializationProfilePayload[] OwnedSurfaceMaterializationProfiles { get; set; }
        = Array.Empty<OwnedSurfaceMaterializationProfilePayload>();

    public string PlayerTaskforceId { get; set; } = string.Empty;

    public long? MutationModeExpiresAtUnixMilliseconds { get; set; }

    public bool CommandCancellationSupported { get; set; }

    public int[] SupportedPriorities { get; set; } = Array.Empty<int>();

    public string ExecutionThreadGuardSelfTest { get; set; } = "pending";

    public RuntimeCapabilityFingerprintPayload RuntimeFingerprint { get; set; } = new();
}

public sealed class OwnedSurfaceMaterializationProfilePayload
{
    public string UnitTypeId { get; set; } = string.Empty;

    public string VariantId { get; set; } = string.Empty;

    public string LoadoutId { get; set; } = string.Empty;
}

public sealed class ArmOwnedMutationModeRequestPayload
{
    public string SessionId { get; set; } = string.Empty;
    public string RuntimeFingerprintSha256 { get; set; } = string.Empty;
    public string Confirmation { get; set; } = string.Empty;
    public int DurationSeconds { get; set; } = 300;
}

public sealed class ArmOwnedMutationModeResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Mode { get; set; } = "disabled";
    public string SessionId { get; set; } = string.Empty;
    public long? ExpiresAtUnixMilliseconds { get; set; }
    public string Postcondition { get; set; } = string.Empty;
}

public sealed class OwnedMutationContextPayload
{
    public string SessionId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public long AuthorityEpoch { get; set; }
    public long ExpectedRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool DryRun { get; set; }
}

public sealed class TerrainPlacementProbeRequestPayload
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed class TerrainPlacementProbeResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string TileId { get; set; } = string.Empty;
    public bool TerrainManagerAvailable { get; set; }
    public bool InitialTerrainLoadingDone { get; set; }
    public bool TileLoaded { get; set; }
    public bool TileFinalized { get; set; }
    public bool FlatteningFinishedForLandUnits { get; set; }
    public bool PlacementReady { get; set; }
    public bool IsLand { get; set; }
    public double? TerrainHeightUnity { get; set; }
    public double? TerrainHeightMeters { get; set; }
    public double? LocalX { get; set; }
    public double? LocalY { get; set; }
    public double? LocalZ { get; set; }
    public string Postcondition { get; set; } = string.Empty;
}

public sealed class MaterializeOwnedSurfaceUnitRequestPayload
{
    public OwnedMutationContextPayload Context { get; set; } = new();
    public string EntityId { get; set; } = string.Empty;
    public string UnitTypeId { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
    public string LoadoutId { get; set; } = "Default";
    public string TaskforceId { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double HeadingDegrees { get; set; }
    public int Telegraph { get; set; }
    public OwnedSurfaceCheckpointPayload? SourceCheckpoint { get; set; }
    public string FaultInjectionStep { get; set; } = string.Empty;
    public string FaultInjectionConfirmation { get; set; } = string.Empty;
}

public sealed class OwnedSurfaceUnitStatePayload
{
    public string StableUnitId { get; set; } = string.Empty;
    public int GameUniqueId { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public string UnitTypeId { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
    public string LoadoutId { get; set; } = string.Empty;
    public string TaskforceId { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? HeadingDegrees { get; set; }
    public double? SpeedKnots { get; set; }
    public int Telegraph { get; set; }
    public long? FuelFractionMilliunits { get; set; }
    public long? StoresFractionMilliunits { get; set; }
    public long? DamageFractionMilliunits { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsDestroyed { get; set; }
}

public sealed class MaterializeOwnedSurfaceUnitResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Outcome { get; set; } = string.Empty;
    public string Postcondition { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public long AuthorityEpoch { get; set; }
    public long Revision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public bool IdempotentReplay { get; set; }
    public bool RecoverableOwnedObject { get; set; }
    public long SynchronousDurationMicroseconds { get; set; }
    public OwnedSurfaceUnitStatePayload? RealizedUnit { get; set; }
    public string[] UnsupportedFields { get; set; } = Array.Empty<string>();
}

public sealed class OwnedSurfaceEntityRequestPayload
{
    public OwnedMutationContextPayload Context { get; set; } = new();
    public string EntityId { get; set; } = string.Empty;
}

public sealed class UpdateOwnedSurfaceUnitRequestPayload
{
    public OwnedMutationContextPayload Context { get; set; } = new();
    public string EntityId { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double HeadingDegrees { get; set; }
    public int Telegraph { get; set; }
    public string FaultInjectionStep { get; set; } = string.Empty;
    public string FaultInjectionConfirmation { get; set; } = string.Empty;
}

public sealed class SetOwnedSurfaceSuspensionRequestPayload
{
    public OwnedMutationContextPayload Context { get; set; } = new();
    public string EntityId { get; set; } = string.Empty;
    public bool IsSuspended { get; set; }
    public string FaultInjectionStep { get; set; } = string.Empty;
    public string FaultInjectionConfirmation { get; set; } = string.Empty;
}

public sealed class RestoreOwnedSurfaceCheckpointRequestPayload
{
    public OwnedMutationContextPayload Context { get; set; } = new();
    public string EntityId { get; set; } = string.Empty;
    public string CheckpointId { get; set; } = string.Empty;
    public string CheckpointSha256 { get; set; } = string.Empty;
    public string FaultInjectionStep { get; set; } = string.Empty;
    public string FaultInjectionConfirmation { get; set; } = string.Empty;
}

public sealed class StageOwnedSurfaceCheckpointRequestPayload
{
    public OwnedMutationContextPayload Context { get; set; } = new();
    public string EntityId { get; set; } = string.Empty;
    public OwnedSurfaceCheckpointPayload SourceCheckpoint { get; set; } = new();
    public string FaultInjectionStep { get; set; } = string.Empty;
    public string FaultInjectionConfirmation { get; set; } = string.Empty;
}

public sealed class OwnedSurfaceResourceStatePayload
{
    public int SchemaVersion { get; set; } = 1;
    public int OwnedRecordCount { get; set; }
    public int ActiveObjectCount { get; set; }
    public int SuspendedRetainedObjectCount { get; set; }
    public int DetachedObjectCount { get; set; }
    public int DetachedCheckpointCount { get; set; }
    public int RecoveryRequiredCount { get; set; }
    public int RetainedConstructedObjectCount { get; set; }
    public bool MemoryReclamationSupported { get; set; }
    public string SchedulerPolicy { get; set; } = string.Empty;
    public OwnedSurfaceResourceEntryPayload[] Entities { get; set; } =
        Array.Empty<OwnedSurfaceResourceEntryPayload>();
}

public sealed class OwnedSurfaceResourceEntryPayload
{
    public string EntityId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public long AuthorityEpoch { get; set; }
    public long Revision { get; set; }
    public string LifecycleState { get; set; } = string.Empty;
    public bool HasCheckpoint { get; set; }
    public bool RecoveryRequired { get; set; }
    public int? GameUniqueId { get; set; }
}

public sealed class ReconcileSurfaceRenderProxiesRequestPayload
{
    public string CampaignId { get; set; } = string.Empty;
    public string TaskforceId { get; set; } = string.Empty;
    public SurfaceRenderProxySpecPayload[] Proxies { get; set; } =
        Array.Empty<SurfaceRenderProxySpecPayload>();
}

public sealed class SurfaceRenderProxySpecPayload
{
    public string EntityId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string UnitTypeId { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
    public string LoadoutId { get; set; } = "Default";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double HeadingDegrees { get; set; }
    public int CampaignTelegraph { get; set; }
}

public sealed class ReconcileSurfaceRenderProxiesResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Outcome { get; set; } = string.Empty;
    public string Postcondition { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public int RequestedCount { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SuspendedCount { get; set; }
    public int ExcludedOwnedEntityCount { get; set; }
    public SurfaceRenderProxyStatePayload[] Proxies { get; set; } =
        Array.Empty<SurfaceRenderProxyStatePayload>();
}

public sealed class SurfaceRenderProxyStatePayload
{
    public string EntityId { get; set; } = string.Empty;
    public string StableUnitId { get; set; } = string.Empty;
    public int? GameUniqueId { get; set; }
    public bool IsVisible { get; set; }
    public bool IsSimulated { get; set; }
    public string RepresentationKind { get; set; } = string.Empty;
    public bool HasNativeSimulationObject { get; set; }
    public bool WorldModelVisible { get; set; }
    public bool MapMarkerVisible { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double HeadingDegrees { get; set; }
    public int CampaignTelegraph { get; set; }
}

public sealed class SurfaceRenderProxySnapshotPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string RepresentationProvider { get; set; } = string.Empty;
    public long CapturedAtUnixMilliseconds { get; set; }
    public SurfaceRenderProxyStatePayload[] Proxies { get; set; } =
        Array.Empty<SurfaceRenderProxyStatePayload>();
}

public sealed class OwnedSurfaceRecoveryReleaseResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Outcome { get; set; } = string.Empty;
    public string Postcondition { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public long AuthorityEpoch { get; set; }
    public long Revision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool IdempotentReplay { get; set; }
}

public sealed class OwnedSurfaceCheckpointPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string CheckpointId { get; set; } = string.Empty;
    public string CheckpointSha256 { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public long AuthorityEpoch { get; set; }
    public long Revision { get; set; }
    public long CapturedAtUnixMilliseconds { get; set; }
    public OwnedSurfaceUnitStatePayload Unit { get; set; } = new();
    public string[] UnsupportedFields { get; set; } = Array.Empty<string>();
}

public sealed class OwnedSurfaceLifecycleResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Outcome { get; set; } = string.Empty;
    public string Postcondition { get; set; } = string.Empty;
    public string AssignmentId { get; set; } = string.Empty;
    public long AuthorityEpoch { get; set; }
    public long Revision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool IdempotentReplay { get; set; }
    public long SynchronousDurationMicroseconds { get; set; }
    public string SourceCheckpointId { get; set; } = string.Empty;
    public string SourceCheckpointSha256 { get; set; } = string.Empty;
    public bool ReconstructedAfterSceneLoss { get; set; }
    public OwnedSurfaceUnitStatePayload Unit { get; set; } = new();
}

public sealed class RuntimeCapabilityFingerprintPayload
{
    public int SchemaVersion { get; set; }

    public string FingerprintSha256 { get; set; } = string.Empty;

    public string FileIdentityState { get; set; } = string.Empty;

    public string? FileIdentityError { get; set; }

    public string GameVersion { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string ActiveScene { get; set; } = string.Empty;

    public bool SceneLoaded { get; set; }

    public string? GameExecutableSha256 { get; set; }

    public RuntimeAssemblyFingerprintPayload[] ManagedAssemblies { get; set; }
        = Array.Empty<RuntimeAssemblyFingerprintPayload>();

    public string? PluginAssemblySha256 { get; set; }

    public string ContentIdentityState { get; set; } = string.Empty;

    public string? ContentIdentityError { get; set; }

    public string? CatalogSha256 { get; set; }

    public string? ModSetSha256 { get; set; }

    public int CatalogFileCount { get; set; }

    public RuntimeContentSourcePayload[] ActiveContentSources { get; set; }
        = Array.Empty<RuntimeContentSourcePayload>();

    public RuntimeSimulationSettingsPayload SimulationSettings { get; set; } = new();

    public RuntimeTimingPayload Timing { get; set; } = new();

    public DeterministicHookCapabilityPayload[] DeterministicHooks { get; set; }
        = Array.Empty<DeterministicHookCapabilityPayload>();

    public UiAutomationSeamCapabilityPayload[] UiAutomationSeams { get; set; }
        = Array.Empty<UiAutomationSeamCapabilityPayload>();

    public RuntimeLoadAccelerationPayload LoadAcceleration { get; set; } = new();

    public bool AuthorityEligible { get; set; }

    public string[] AuthorityBlockers { get; set; } = Array.Empty<string>();
}

public sealed class RuntimeLoadAccelerationPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string FileHashCacheState { get; set; } = string.Empty;

    public int FileHashCacheEntryCount { get; set; }

    public int FileHashCacheHitCount { get; set; }

    public int FileHashCacheMissCount { get; set; }

    public bool FailOpen { get; set; } = true;
}

public sealed class RuntimeContentSourcePayload
{
    public int LoadOrder { get; set; }

    public string SourceId { get; set; } = string.Empty;

    public string DirectoryName { get; set; } = string.Empty;

    public bool IsUserData { get; set; }

    public bool IsBaseGame { get; set; }

    public bool IsSteamWorkshop { get; set; }

    public int WinningCatalogFileCount { get; set; }
}

public sealed class RuntimeSimulationSettingsPayload
{
    public bool OptionsManagerAvailable { get; set; }

    public bool UseAssetBundles { get; set; }

    public bool UseDatalinks { get; set; }

    public bool SaveLoadSensorData { get; set; }

    public bool SaveLoadFlightDeck { get; set; }

    public int? Difficulty { get; set; }

    public bool? PlayerAutoAttackSurface { get; set; }

    public bool? RestrictAirOpsBySeaState { get; set; }

    public bool? AllowAssociatedSearchSensors { get; set; }

    public bool? UseImprovedSonarModel { get; set; }

    public string? AswSimulation { get; set; }

    public string? FlightDeckTimingsMode { get; set; }

    public float FloodingCheckSeconds { get; set; }

    public float FireCheckSeconds { get; set; }
}

public sealed class RuntimeTimingPayload
{
    public float EngineFixedDeltaTime { get; set; }

    public float GameFixedDeltaTime { get; set; }

    public float TimeScale { get; set; }

    public float TimeCompression { get; set; }

    public float InternalTimeDilation { get; set; }
}

public sealed class DeterministicHookCapabilityPayload
{
    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? MethodFingerprintSha256 { get; set; }
}

public sealed class UiAutomationSeamCapabilityPayload
{
    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string MethodFingerprintSha256 { get; set; } = string.Empty;

    public string ExpectedFingerprintSha256 { get; set; } = string.Empty;

    public string[] Commands { get; set; } = Array.Empty<string>();
}

public sealed class RuntimeAssemblyFingerprintPayload
{
    public string Name { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}

public sealed class SceneStatePayload
{
    public string SceneName { get; set; } = string.Empty;

    public bool SceneLoaded { get; set; }

    public bool GameMainAvailable { get; set; }

    public bool ObjectsManagerAvailable { get; set; }

    public bool TaskforceManagerAvailable { get; set; }

    public int ObjectCount { get; set; }

    public int TaskforceCount { get; set; }

    public int InitializedMainThreadId { get; set; }

    public int ExecutingThreadId { get; set; }

    public bool IsMainThread { get; set; }
}

public sealed class WorldSnapshotPayload
{
    public int SchemaVersion { get; set; } = 13;

    public string SceneName { get; set; } = string.Empty;

    public string ScenarioId { get; set; } = string.Empty;

    public string ScenarioPath { get; set; } = string.Empty;

    public string IdentityScope { get; set; } = "loaded-scene";

    public long CapturedAtUnixMilliseconds { get; set; }

    public double? SimulationTimeSeconds { get; set; }

    public double? TimeCompression { get; set; }

    public bool IsPaused { get; set; }

    public float EngineTimeScale { get; set; }

    public float EngineFixedDeltaTime { get; set; }

    public float GameFixedDeltaTime { get; set; }

    public float InternalTimeDilation { get; set; }

    public float EngineUnscaledDeltaTime { get; set; }

    public float? MeasuredFramesPerSecond { get; set; }

    public int ObservedObjectCount { get; set; }

    public long MainThreadCaptureDurationMicroseconds { get; set; }

    public string StableIdentityState { get; set; } = string.Empty;

    public int CampaignIdentityCount { get; set; }

    public int SessionSurrogateIdentityCount { get; set; }

    public string[] DuplicateStableIds { get; set; } = Array.Empty<string>();

    public string IdentitySha256 { get; set; } = string.Empty;

    public string KinematicsSha256 { get; set; } = string.Empty;

    public string LogisticsSha256 { get; set; } = string.Empty;

    public string DamageSha256 { get; set; } = string.Empty;

    public string WeaponsSha256 { get; set; } = string.Empty;

    public string OrdersSha256 { get; set; } = string.Empty;

    public string ContactsSha256 { get; set; } = string.Empty;

    public string EventsSha256 { get; set; } = string.Empty;

    public string CanonicalSha256 { get; set; } = string.Empty;

    public TaskforceSnapshotPayload[] Taskforces { get; set; }
        = Array.Empty<TaskforceSnapshotPayload>();

    public UnitSnapshotPayload[] Units { get; set; }
        = Array.Empty<UnitSnapshotPayload>();

    public WeaponSnapshotPayload[] Weapons { get; set; }
        = Array.Empty<WeaponSnapshotPayload>();

    public ContactSnapshotPayload[] Contacts { get; set; }
        = Array.Empty<ContactSnapshotPayload>();

    public PlayerEventLogSnapshotPayload Events { get; set; } = new();

    public SpatialDiagnosticsPayload SpatialDiagnostics { get; set; } = new();

    public ControlledTickExperimentPayload ControlledTickExperiment { get; set; } = new();
}

public sealed class TacticalCouplingStatePayload
{
    public int SchemaVersion { get; set; } = 2;

    public string SceneName { get; set; } = string.Empty;

    public long CapturedAtUnixMilliseconds { get; set; }

    public long MainThreadCaptureDurationMicroseconds { get; set; }

    public WeaponSnapshotPayload[] ActiveWeapons { get; set; }
        = Array.Empty<WeaponSnapshotPayload>();

    public TacticalCouplingObservationPayload[] Relationships { get; set; }
        = Array.Empty<TacticalCouplingObservationPayload>();
}

public sealed class TacticalCouplingObservationPayload
{
    public string Kind { get; set; } = string.Empty;

    public string SourceObjectId { get; set; } = string.Empty;

    public string TargetObjectId { get; set; } = string.Empty;

    public string EvidenceId { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public bool UnsafeToSplit { get; set; } = true;
}

public sealed class ControlledTickExperimentRequestPayload
{
    public int TickCount { get; set; } = 300;
    public double StepSeconds { get; set; } = 1d / 30d;
    public int? Seed { get; set; }
}

public sealed class ControlledTickExperimentPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Mode { get; set; } = "ControlledExperimental";
    public string State { get; set; } = "idle";
    public string Postcondition { get; set; } = string.Empty;
    public bool HooksInstalled { get; set; }
    public string InstallationError { get; set; } = string.Empty;
    public int RequestedTicks { get; set; }
    public int CompletedTicks { get; set; }
    public int GameUpdateCount { get; set; }
    public int PhysicsFixedUpdateCount { get; set; }
    public double StepSeconds { get; set; }
    public double? StartSimulationTimeSeconds { get; set; }
    public double? TargetSimulationTimeSeconds { get; set; }
    public double? CurrentSimulationTimeSeconds { get; set; }
    public int? Seed { get; set; }
    public string[] SeededSources { get; set; } = Array.Empty<string>();
    public string[] UncontrolledSources { get; set; } = Array.Empty<string>();
}

public sealed class SpatialDiagnosticsPayload
{
    public int SchemaVersion { get; set; } = 2;
    public double MetersPerUnityUnit { get; set; } = 67.2;
    public double CenterTileX { get; set; }
    public double CenterTileY { get; set; }
    public double OriginLatitude { get; set; }
    public double OriginLongitude { get; set; }
    public double RebaseThresholdUnity { get; set; }
    public double RebaseThresholdMeters { get; set; }
    public double CameraHorizontalDistanceUnity { get; set; }
    public double CameraHorizontalDistanceMeters { get; set; }
    public double CameraLatitude { get; set; }
    public double CameraLongitude { get; set; }
    public int ObjectCountBeyondRebaseThreshold { get; set; }
    public double MaximumObjectHorizontalDistanceUnity { get; set; }
    public double MaximumObjectHorizontalDistanceMeters { get; set; }
    public double MaximumObjectGeodeticDistanceMeters { get; set; }
    public string MaximumDistanceObjectId { get; set; } = string.Empty;
    public string MaximumGeodeticDistanceObjectId { get; set; } = string.Empty;
    public double MaximumPairHorizontalSeparationUnity { get; set; }
    public double MaximumPairHorizontalSeparationMeters { get; set; }
    public double MaximumPairGeodeticSeparationMeters { get; set; }
    public string MaximumPairFirstObjectId { get; set; } = string.Empty;
    public string MaximumPairSecondObjectId { get; set; } = string.Empty;
    public string MaximumPairGeodeticFirstObjectId { get; set; } = string.Empty;
    public string MaximumPairGeodeticSecondObjectId { get; set; } = string.Empty;
    public double TerrainChunkSizeUnity { get; set; } = 330.71396;
    public double TerrainChunkSizeMeters { get; set; }
    public int ConfiguredTerrainChunkViewDistance { get; set; }
    public int LoadedTerrainChunkCount { get; set; }
    public int FinalizedTerrainChunkCount { get; set; }
    public double MaximumLoadedTerrainTileOffset { get; set; }
    public double MaximumLoadedTerrainCenterDistanceMeters { get; set; }
    public int ObjectCountBeyondLoadedTerrain { get; set; }
    public int UnitCountBeyondLoadedTerrain { get; set; }
    public int ObjectCountWithInvalidGeodeticPosition { get; set; }
    public int ActiveWeaponCount { get; set; }
    public int ActiveWeaponCountBeyondLoadedTerrain { get; set; }
    public bool InitialTerrainLoadingDone { get; set; }
    public bool RelocationHandlerAvailable { get; set; }
    public long LastRelocationEventTimeMilliseconds { get; set; }
}

public sealed class PlayerEventLogSnapshotPayload
{
    public string ObservationScope { get; set; } = "player-event-log";
    public string State { get; set; } = string.Empty;
    public int AlertCount { get; set; }
    public int IntelligenceCount { get; set; }
    public bool IsTruncated { get; set; }
    public PlayerAlertSnapshotPayload[] Alerts { get; set; } = [];
    public PlayerIntelligenceSnapshotPayload[] Intelligence { get; set; } = [];
    public string ObservationSha256 { get; set; } = string.Empty;
}

public sealed class PlayerAlertSnapshotPayload
{
    public int Index { get; set; }
    public string TimeUtc { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? RaisingObjectId { get; set; }
    public string? AboutObjectId { get; set; }
}

public sealed class PlayerIntelligenceSnapshotPayload
{
    public int Index { get; set; }
    public string TimeUtc { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class ExperimentalSaveLoadProbeResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Postcondition { get; set; } = string.Empty;
    public string SaveFileName { get; set; } = string.Empty;
    public string SaveFilePath { get; set; } = string.Empty;
    public string SaveFileSha256 { get; set; } = string.Empty;
    public int PreTransitionWeaponCount { get; set; }
    public string PreTransitionWeaponsSha256 { get; set; } = string.Empty;
    public string PreTransitionCanonicalSha256 { get; set; } = string.Empty;
}

public sealed class ExperimentalWeaponLaunchRequestPayload
{
    public string WeaponType { get; set; } = "Missile";
    public string AmmunitionId { get; set; } = string.Empty;
    public string UnitId { get; set; } = string.Empty;
    public string TargetUnitId { get; set; } = string.Empty;
}

public sealed class ExperimentalWeaponLaunchResultPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string Postcondition { get; set; } = string.Empty;
    public string UnitId { get; set; } = string.Empty;
    public int WeaponSystemIndex { get; set; }
    public int ContainerIndex { get; set; }
    public string WeaponType { get; set; } = string.Empty;
    public string AmmunitionId { get; set; } = string.Empty;
    public string ActiveWeaponId { get; set; } = string.Empty;
    public string IntendedTargetUnitId { get; set; } = string.Empty;
    public int ActiveWeaponCount { get; set; }
    public bool NativeSaveRegistered { get; set; }
    public int SerializableActiveWeaponCount { get; set; }
}

public sealed class ContactSnapshotPayload
{
    public string Id { get; set; } = string.Empty;

    public int TrackNumber { get; set; }

    public string ObservationScope { get; set; } = "player-plotting-table";

    public bool IsOwnForce { get; set; }

    public bool IsDormant { get; set; }

    public bool IsIdentified { get; set; }

    public bool IsClassified { get; set; }

    public string? DetectedTaskforceId { get; set; }

    public string DetectedClassName { get; set; } = string.Empty;

    public string Relationship { get; set; } = string.Empty;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? AltitudeMeters { get; set; }

    public string PositionState { get; set; } = string.Empty;

    public double? PositionMajorStdDevMeters { get; set; }

    public double? PositionMinorStdDevMeters { get; set; }

    public double? AltitudeErrorMeters { get; set; }

    public double? InitialDetectionTimeSeconds { get; set; }

    public double? LastUpdateTimeSeconds { get; set; }

    public bool IsObservedVisually { get; set; }

    public string DetectingSensors { get; set; } = string.Empty;

    public ContactTrackSourcePayload[] TrackSources { get; set; }
        = Array.Empty<ContactTrackSourcePayload>();

    public string ObservationSha256 { get; set; } = string.Empty;
}

public sealed class ContactTrackSourcePayload
{
    public string SensorType { get; set; } = string.Empty;

    public string SensorDisplayName { get; set; } = string.Empty;

    public string DetectionKind { get; set; } = string.Empty;

    public double? ObservationTimeSeconds { get; set; }

    public double? MaximumAgeSeconds { get; set; }

    public double? Confidence { get; set; }

    public int ObservationCount { get; set; }
}

public sealed class TaskforceSnapshotPayload
{
    public string Id { get; set; } = string.Empty;

    public string MissionName { get; set; } = string.Empty;

    public string Side { get; set; } = string.Empty;

    public string Nation { get; set; } = string.Empty;

    public bool IsAiControlled { get; set; }

    public bool IsPlayerControlled { get; set; }

    public string[] UnitIds { get; set; } = Array.Empty<string>();
}

public sealed class UnitSnapshotPayload
{
    public string Id { get; set; } = string.Empty;

    public int GameUniqueId { get; set; }

    public string IdentityProvenance { get; set; } = string.Empty;

    public string CampaignTag { get; set; } = string.Empty;

    public string? TaskforceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Nation { get; set; } = string.Empty;

    public string UnitFolder { get; set; } = string.Empty;

    public string UnitTypeId { get; set; } = string.Empty;

    public string VariantId { get; set; } = string.Empty;

    public string LoadoutId { get; set; } = string.Empty;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? AltitudeFeet { get; set; }

    public double? HeadingDegrees { get; set; }

    public double? SpeedKnots { get; set; }

    public string CurrentOrder { get; set; } = string.Empty;

    public string? CurrentOrderTargetId { get; set; }

    public WaypointSnapshotPayload[] Waypoints { get; set; }
        = Array.Empty<WaypointSnapshotPayload>();

    public string CurrentState { get; set; } = string.Empty;

    public double? OverallDamagePoints { get; set; }

    public double? TotalDamagePoints { get; set; }

    public double? DamageFraction { get; set; }

    public double? CurrentFuelMass { get; set; }

    public double? MaximumFuelMass { get; set; }

    public double? FuelFraction { get; set; }

    public AmmunitionStoreSnapshotPayload[] Ammunition { get; set; }
        = Array.Empty<AmmunitionStoreSnapshotPayload>();

    public HostedAircraftSnapshotPayload[] HostedAircraft { get; set; }
        = Array.Empty<HostedAircraftSnapshotPayload>();

    public int? HostedAircraftCapacity { get; set; }

    public string IdentitySha256 { get; set; } = string.Empty;

    public string KinematicsSha256 { get; set; } = string.Empty;

    public string LogisticsSha256 { get; set; } = string.Empty;

    public string DamageSha256 { get; set; } = string.Empty;

    public string OrdersSha256 { get; set; } = string.Empty;

    public bool IsDestroyed { get; set; }
}

public sealed class WaypointSnapshotPayload
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? HeightMeters { get; set; }
    public string HeightState { get; set; } = string.Empty;
    public bool IsRelative { get; set; }
    public bool IsReachable { get; set; }
    public string? ParentUnitId { get; set; }
    public bool SetCourse { get; set; }
    public double? CourseDegrees { get; set; }
    public bool SetTurn { get; set; }
    public double? Distance { get; set; }
    public double? TimeToFollowSeconds { get; set; }
    public bool IsAutogenerated { get; set; }
    public string EngagementAmmunitionId { get; set; } = string.Empty;
    public string? EngagementTargetId { get; set; }
    public int Salvo { get; set; }
    public string SalvoType { get; set; } = string.Empty;
    public bool AttackOnlyDetected { get; set; }
    public bool FormationAttack { get; set; }
}

public sealed class AmmunitionStoreSnapshotPayload
{
    public string AmmunitionId { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

public sealed class HostedAircraftSnapshotPayload
{
    public string UnitTypeId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public int Quantity { get; set; }
}

public sealed class WeaponSnapshotPayload
{
    public string Id { get; set; } = string.Empty;

    public int GameUniqueId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string AmmunitionId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string WeaponType { get; set; } = string.Empty;

    public string WeaponSubtype { get; set; } = string.Empty;

    public string GuidanceType { get; set; } = string.Empty;

    public string PrimaryTargetType { get; set; } = string.Empty;

    public string SecondaryTargetType { get; set; } = string.Empty;

    public string? LaunchPlatformId { get; set; }

    public string? IntendedTargetId { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? AltitudeFeet { get; set; }

    public double? HeadingDegrees { get; set; }

    public double? SpeedKnots { get; set; }

    public string FlightStage { get; set; } = string.Empty;

    public double? LaunchTimeSeconds { get; set; }

    public double? TraveledDistance { get; set; }

    public double? RemainingRangeFraction { get; set; }

    public bool IsLaunched { get; set; }

    public bool IsDestroyed { get; set; }

    public string IdentitySha256 { get; set; } = string.Empty;

    public string KinematicsSha256 { get; set; } = string.Empty;

    public string EngagementSha256 { get; set; } = string.Empty;
}

public sealed class UnitCatalogPageRequestPayload
{
    public int Offset { get; set; }

    public int Limit { get; set; } = 50;

    public string? Folder { get; set; }
}

public sealed class UnitCatalogPagePayload
{
    public int SchemaVersion { get; set; } = 1;

    public string CatalogSha256 { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public int TotalRecords { get; set; }

    public int Offset { get; set; }

    public int Limit { get; set; }

    public UnitCatalogRecordPayload[] Records { get; set; }
        = Array.Empty<UnitCatalogRecordPayload>();
}

public sealed class UnitCatalogRecordPayload
{
    public string Id { get; set; } = string.Empty;

    public int RuntimeMetadataId { get; set; }

    public string UnitFolder { get; set; } = string.Empty;

    public string UnitTypeId { get; set; } = string.Empty;

    public string ObjectType { get; set; } = string.Empty;

    public string DefaultName { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string TypeAbbreviation { get; set; } = string.Empty;

    public string TypeName { get; set; } = string.Empty;

    public string DefaultNation { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string SourceFileSha256 { get; set; } = string.Empty;

    public string[] LoadoutIds { get; set; } = Array.Empty<string>();

    public string[] Roles { get; set; } = Array.Empty<string>();

    public UnitCatalogVariantPayload[] Variants { get; set; }
        = Array.Empty<UnitCatalogVariantPayload>();
}

public sealed class UnitCatalogVariantPayload
{
    public string Id { get; set; } = string.Empty;

    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string Nation { get; set; } = string.Empty;

    public string LoadoutId { get; set; } = string.Empty;

    public int ServiceEntryYear { get; set; }

    public int ServiceExitYear { get; set; }
}

public sealed class ReferenceCatalogPageRequestPayload
{
    public string Kind { get; set; } = string.Empty;

    public int Offset { get; set; }

    public int Limit { get; set; } = 50;
}

public sealed class ReferenceCatalogPagePayload
{
    public int SchemaVersion { get; set; } = 1;

    public string State { get; set; } = string.Empty;

    public string CatalogSha256 { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int TotalRecords { get; set; }

    public int Offset { get; set; }

    public int Limit { get; set; }

    public ReferenceCatalogRecordPayload[] Records { get; set; }
        = Array.Empty<ReferenceCatalogRecordPayload>();
}

public sealed class ReferenceCatalogRecordPayload
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Subtype { get; set; } = string.Empty;

    public string Family { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string GuidanceType { get; set; } = string.Empty;

    public string PrimaryTargetType { get; set; } = string.Empty;

    public string SecondaryTargetType { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string VirtualPath { get; set; } = string.Empty;

    public string SourceFileSha256 { get; set; } = string.Empty;
}

public sealed class CatalogDiagnosticsPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string State { get; set; } = string.Empty;

    public string CatalogSha256 { get; set; } = string.Empty;

    public int WinningFileCount { get; set; }

    public int OverrideCount { get; set; }

    public int IdenticalOverrideCount { get; set; }

    public int ConflictingOverrideCount { get; set; }

    public CatalogOverrideDiagnosticPayload[] Overrides { get; set; }
        = Array.Empty<CatalogOverrideDiagnosticPayload>();
}

public sealed class CatalogOverrideDiagnosticPayload
{
    public string VirtualPath { get; set; } = string.Empty;

    public string WinningSourceId { get; set; } = string.Empty;

    public string WinningSha256 { get; set; } = string.Empty;

    public string ShadowedSourceId { get; set; } = string.Empty;

    public string ShadowedSha256 { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;
}

public sealed class CampaignManagementStatePayload
{
    public int SchemaVersion { get; set; } = 3;

    public string SceneName { get; set; } = string.Empty;

    public bool LinearCampaignViewAvailable { get; set; }

    public bool LinearCampaignMapAvailable { get; set; }

    public bool IsTaskForceModeCampaign { get; set; }

    public bool HasSelectedCampaignEvent { get; set; }

    public string SelectedCampaignEventName { get; set; } = string.Empty;

    public bool TaskForceBuilderVisible { get; set; }

    public bool TaskForceBuilderEnabled { get; set; }

    public int TaskForceBuilderTargetMissionIndex { get; set; } = -1;

    public bool TaskForceBuilderLoaded { get; set; }

    public bool HasConfirmedTaskForce { get; set; }

    public string TaskForceName { get; set; } = string.Empty;

    public int AvailablePoints { get; set; }

    public int PointCap { get; set; }

    public int PurchasedUnitCount { get; set; }

    public int PendingUnitCount { get; set; }

    public int PendingPurchaseCost { get; set; }

    public bool HasPendingTransactions { get; set; }

    public int VisibleTaskForceUnitCount { get; set; }

    public int AvailableRosterVariantCount { get; set; }

    public bool HasShipRosterUnits { get; set; }

    public bool HasSubmarineRosterUnits { get; set; }

    public bool HasAirRosterUnits { get; set; }

    public CampaignMaintenanceUnitPayload[] PurchasedUnits { get; set; } = [];

    public CampaignMaintenanceUnitPayload[] PendingUnits { get; set; } = [];

    public string NativeMaintenanceState { get; set; } = string.Empty;

    public bool NativeRearmAvailable { get; set; }

    public int NativeDamagedUnitCount { get; set; }

    public int NativeRepairCandidateCount { get; set; }

    public CampaignNativeMaintenanceStatePayload[] NativeMaintenanceUnits { get; set; } = [];
}

public sealed class CampaignNativeMaintenanceStatePayload
{
    public string InstanceId { get; set; } = string.Empty;

    public string UnitType { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string DamageSummary { get; set; } = string.Empty;

    public bool CanRepair { get; set; }

    public int RepairCostPoints { get; set; }

    public string RepairStatus { get; set; } = string.Empty;

    public bool HasRearmedBadge { get; set; }

    public bool HasRepairedBadge { get; set; }

    public CampaignNativeAmmunitionStatePayload[] Ammunition { get; set; } = [];
}

public sealed class CampaignNativeAmmunitionStatePayload
{
    public string Reference { get; set; } = string.Empty;

    public string NativeSummary { get; set; } = string.Empty;
}

public sealed class CampaignMaintenanceUnitPayload
{
    public string InstanceId { get; set; } = string.Empty;

    public string UnitFolder { get; set; } = string.Empty;

    public string UnitType { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string VariantReference { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Nation { get; set; } = string.Empty;

    public string SelectedLoadoutVariant { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int Cost { get; set; }

    public Dictionary<string, int> AircraftAssignments { get; set; } = new();
}

public sealed class UiAutomationActionResultPayload
{
    public int SchemaVersion { get; set; } = 3;

    public string Action { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string Postcondition { get; set; } = string.Empty;

    public string CampaignPath { get; set; } = string.Empty;

    public CampaignManagementStatePayload Campaign { get; set; } = new();

    public ScenarioAutomationStatePayload? Scenario { get; set; }
}

public sealed class StartScenarioRequestPayload
{
    public string ScenarioId { get; set; } = string.Empty;
}

public sealed class AddMaintenanceDraftUnitRequestPayload
{
    public string UnitType { get; set; } = string.Empty;

    public string VariantReference { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;
}

public sealed class ScenarioAutomationStatePayload
{
    public int SchemaVersion { get; set; } = 1;

    public string SceneName { get; set; } = string.Empty;

    public string CurrentWindowType { get; set; } = string.Empty;

    public string ScenarioId { get; set; } = string.Empty;

    public string ScenarioPath { get; set; } = string.Empty;

    public bool BriefingVisible { get; set; }

    public bool BriefingPaused { get; set; }

    public bool BriefingCanContinue { get; set; }

    public bool GameMainAvailable { get; set; }
}

public sealed class UiInspectionPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string SceneName { get; set; } = string.Empty;

    public string CurrentWindowType { get; set; } = string.Empty;

    public bool GameMainAvailable { get; set; }

    public bool MainGameViewAvailable { get; set; }

    public bool IsPaused { get; set; }

    public double? SimulationTimeSeconds { get; set; }

    public double? TimeCompression { get; set; }

    public bool MapVisible { get; set; }

    public bool HudVisible { get; set; }

    public bool FormationManagerVisible { get; set; }

    public bool EventLogAvailable { get; set; }

    public string SelectedUnitId { get; set; } = string.Empty;

    public int ScreenWidth { get; set; }

    public int ScreenHeight { get; set; }

    public string PlayerTaskforceId { get; set; } = string.Empty;
}

public sealed class CaptureGameScreenshotRequestPayload
{
    public int MaxWidth { get; set; } = 1920;
}

public sealed class CaptureGameScreenshotResultPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string Action { get; set; } = GameCommandTypes.CaptureGameScreenshot;

    public string Outcome { get; set; } = string.Empty;

    public string Postcondition { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public string SceneName { get; set; } = string.Empty;

    public long CapturedAtUnixMilliseconds { get; set; }
}

public sealed class FocusCameraOnUnitRequestPayload
{
    public string UnitId { get; set; } = string.Empty;
}

public sealed class SetTacticalHudPanelRequestPayload
{
    public string Panel { get; set; } = string.Empty;

    public bool? Visible { get; set; }
}

public sealed class ExperimentalPauseRequestPayload
{
    public bool IsPaused { get; set; }
}

public sealed class ExperimentalPauseResultPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string Mode { get; set; } = "ControlledExperimental";

    public bool RequestedPaused { get; set; }

    public bool IsPaused { get; set; }

    public double? SimulationTimeSeconds { get; set; }

    public double? TimeCompression { get; set; }

    public string Postcondition { get; set; } = string.Empty;
}

public sealed class CaptureColliderCatalogRequestPayload
{
    public bool ForceRefresh { get; set; }

    public string? UnitTypeId { get; set; }
}

public sealed class ColliderPrimitivePayload
{
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsTrigger { get; set; }

    public bool Enabled { get; set; }

    public float[] Center { get; set; } = Array.Empty<float>();

    public float[] Size { get; set; } = Array.Empty<float>();

    public float[] Rotation { get; set; } = Array.Empty<float>();

    public float Radius { get; set; }

    public float Height { get; set; }

    public int Direction { get; set; }
}

public sealed class ColliderUnitGeometryPayload
{
    public int SchemaVersion { get; set; } = 2;

    public string GameVersion { get; set; } = string.Empty;

    public string UnitTypeId { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string Source { get; set; } = "live-objectbase";

    public long CapturedAtUnixMilliseconds { get; set; }

    public int ColliderCount { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public ColliderPrimitivePayload[] Colliders { get; set; } = Array.Empty<ColliderPrimitivePayload>();
}

public sealed class ColliderCatalogEntryPayload
{
    public string UnitTypeId { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string GameVersion { get; set; } = string.Empty;

    public int ColliderCount { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public bool InMemory { get; set; }

    public string FilePath { get; set; } = string.Empty;
}

public sealed class CaptureColliderCatalogResultPayload
{
    public int SchemaVersion { get; set; } = 2;

    public string CacheDirectory { get; set; } = string.Empty;

    public string GameVersion { get; set; } = string.Empty;

    public int ScannedObjectCount { get; set; }

    public int CapturedUnitCount { get; set; }

    public int MemoryCount { get; set; }

    public int FileCount { get; set; }

    public string Postcondition { get; set; } = string.Empty;

    public ColliderCatalogEntryPayload[] Entries { get; set; } = Array.Empty<ColliderCatalogEntryPayload>();
}

public sealed class GetColliderCatalogResultPayload
{
    public int SchemaVersion { get; set; } = 2;

    public string CacheDirectory { get; set; } = string.Empty;

    public string GameVersion { get; set; } = string.Empty;

    public int MemoryCount { get; set; }

    public int FileCount { get; set; }

    public ColliderCatalogEntryPayload[] Entries { get; set; } = Array.Empty<ColliderCatalogEntryPayload>();

    public ColliderUnitGeometryPayload? Geometry { get; set; }

    public string Source { get; set; } = "file-cache";
}
