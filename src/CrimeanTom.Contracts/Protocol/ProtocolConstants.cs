namespace CrimeanTom.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const string GamePipeName = "CrimeanTom.Game.v1";
    public const string DefaultControlUrl = "http://127.0.0.1:5137";
    public const string ControlTokenHeader = "X-Crimean-Tom-Token";

    public const int MaximumFrameBytes = 256 * 1024;
    public const int MaximumPendingCommands = 128;
    public const int MaximumGameCommandQueue = 128;
    public const int MaximumGameOutboundQueue = 256;
    public const int DefaultCommandTimeoutMilliseconds = 5_000;
    public const int HandshakeTimeoutMilliseconds = 5_000;
}

public static class MessageKinds
{
    public const string Hello = "hello";
    public const string HelloAccepted = "hello-accepted";
    public const string HelloRejected = "hello-rejected";
    public const string Command = "command";
    public const string CancelCommand = "cancel-command";
    public const string Response = "response";
}

public static class CommandPriorities
{
    public const int Bulk = -100;
    public const int Normal = 0;
    public const int Control = 100;

    public static readonly int[] Supported =
    {
        Bulk,
        Normal,
        Control,
    };

    public static bool IsDefined(int priority)
    {
        return priority == Bulk || priority == Normal || priority == Control;
    }

    public static int Normalize(int priority)
    {
        return IsDefined(priority) ? priority : Normal;
    }
}

public static class ProtocolErrorCodes
{
    public const string AutomationPreconditionFailed = "automation-precondition-failed";
    public const string AutomationPostconditionFailed = "automation-postcondition-failed";
    public const string CommandCancelled = "command-cancelled";
    public const string CommandExpired = "command-expired";
    public const string InternalError = "internal-error";
    public const string IdempotencyConflict = "idempotency-conflict";
    public const string InvalidMessage = "invalid-message";
    public const string InvalidMessageKind = "invalid-message-kind";
    public const string InvalidSession = "invalid-session";
    public const string ProtocolVersionMismatch = "protocol-version-mismatch";
    public const string QueueFull = "queue-full";
    public const string UnsupportedCommand = "unsupported-command";
    public const string MutationDisabled = "mutation-disabled";
    public const string MutationInjectedFailure = "mutation-injected-failure";
    public const string MutationPreconditionFailed = "mutation-precondition-failed";
    public const string StaleAuthorityEpoch = "stale-authority-epoch";
    public const string StaleRevision = "stale-revision";
    public const string WrongExecutionThread = "wrong-execution-thread";
}

public static class GameCommandTypes
{
    public const string Ping = "ping";
    public const string GetCapabilities = "get-capabilities";
    public const string GetSceneState = "get-scene-state";
    public const string GetWorldSnapshot = "get-world-snapshot";
    public const string GetTacticalCouplingState = "get-tactical-coupling-state";
    public const string GetUnitCatalogPage = "get-unit-catalog-page";
    public const string GetReferenceCatalogPage = "get-reference-catalog-page";
    public const string GetCatalogDiagnostics = "get-catalog-diagnostics";
    public const string GetCampaignManagementState = "get-campaign-management-state";
    public const string GetUiInspection = "get-ui-inspection";
    public const string CaptureGameScreenshot = "capture-game-screenshot";
    public const string CaptureColliderCatalog = "capture-collider-catalog";
    public const string GetColliderCatalog = "get-collider-catalog";
    public const string FocusCameraOnUnit = "focus-camera-on-unit";
    public const string SetTacticalHudPanel = "set-tactical-hud-panel";
    public const string DismissTopModal = "dismiss-top-modal";
    public const string OpenTaskForceBuilder = "open-task-force-builder";
    public const string StartPortOperations = "start-port-operations";
    public const string AddMaintenanceDraftUnit = "add-maintenance-draft-unit";
    public const string StartScenario = "start-scenario";
    public const string ContinueMissionBriefing = "continue-mission-briefing";
    public const string ExitTacticalSession = "exit-tactical-session";
    public const string SetExperimentalPauseState = "set-experimental-pause-state";
    public const string LaunchExperimentalProbeWeapon = "launch-experimental-probe-weapon";
    public const string SaveExperimentalProbe = "save-experimental-probe";
    public const string LoadExperimentalProbe = "load-experimental-probe";
    public const string RunControlledTickExperiment = "run-controlled-tick-experiment";
    public const string ArmOwnedMutationMode = "arm-owned-mutation-mode";
    public const string MaterializeOwnedSurfaceUnit = "materialize-owned-surface-unit";
    public const string CaptureOwnedSurfaceCheckpoint = "capture-owned-surface-checkpoint";
    public const string UpdateOwnedSurfaceUnit = "update-owned-surface-unit";
    public const string SetOwnedSurfaceSuspension = "set-owned-surface-suspension";
    public const string RestoreOwnedSurfaceCheckpoint = "restore-owned-surface-checkpoint";
    public const string StageOwnedSurfaceCheckpoint = "stage-owned-surface-checkpoint";
    public const string GetOwnedSurfaceResourceState = "get-owned-surface-resource-state";
    public const string GetSurfaceRenderProxyState = "get-surface-render-proxy-state";
    public const string ReconcileSurfaceRenderProxies = "reconcile-surface-render-proxies";
    public const string ReleaseDetachedOwnedSurfaceRecovery =
        "release-detached-owned-surface-recovery";

    public static readonly string[] ReadOnlyCommands =
    {
        Ping,
        GetCapabilities,
        GetSceneState,
        GetWorldSnapshot,
        GetTacticalCouplingState,
        GetOwnedSurfaceResourceState,
        GetSurfaceRenderProxyState,
        GetUnitCatalogPage,
        GetReferenceCatalogPage,
        GetCatalogDiagnostics,
        GetCampaignManagementState,
        GetUiInspection,
        CaptureGameScreenshot,
        CaptureColliderCatalog,
        GetColliderCatalog,
    };

    public static readonly string[] UiAutomationCommands =
    {
        OpenTaskForceBuilder,
        StartPortOperations,
        AddMaintenanceDraftUnit,
        StartScenario,
        ContinueMissionBriefing,
        ExitTacticalSession,
        FocusCameraOnUnit,
        SetTacticalHudPanel,
    };

    public static readonly string[] ExperimentalCommands =
    {
        SetExperimentalPauseState,
        LaunchExperimentalProbeWeapon,
        SaveExperimentalProbe,
        LoadExperimentalProbe,
        RunControlledTickExperiment,
    };

    public static readonly string[] MutationControlCommands =
    {
        ArmOwnedMutationMode,
    };

    public static readonly string[] MutationCommands =
    {
        MaterializeOwnedSurfaceUnit,
        CaptureOwnedSurfaceCheckpoint,
        UpdateOwnedSurfaceUnit,
        SetOwnedSurfaceSuspension,
        RestoreOwnedSurfaceCheckpoint,
        StageOwnedSurfaceCheckpoint,
        ReconcileSurfaceRenderProxies,
        ReleaseDetachedOwnedSurfaceRecovery,
    };

    public static readonly string[] AllCommands =
    {
        Ping,
        GetCapabilities,
        GetSceneState,
        GetWorldSnapshot,
        GetTacticalCouplingState,
        GetUnitCatalogPage,
        GetReferenceCatalogPage,
        GetCatalogDiagnostics,
        GetCampaignManagementState,
        GetUiInspection,
        CaptureGameScreenshot,
        CaptureColliderCatalog,
        GetColliderCatalog,
        FocusCameraOnUnit,
        SetTacticalHudPanel,
        DismissTopModal,
        OpenTaskForceBuilder,
        StartPortOperations,
        AddMaintenanceDraftUnit,
        StartScenario,
        ContinueMissionBriefing,
        ExitTacticalSession,
        SetExperimentalPauseState,
        LaunchExperimentalProbeWeapon,
        SaveExperimentalProbe,
        LoadExperimentalProbe,
        RunControlledTickExperiment,
        ArmOwnedMutationMode,
        MaterializeOwnedSurfaceUnit,
        CaptureOwnedSurfaceCheckpoint,
        UpdateOwnedSurfaceUnit,
        SetOwnedSurfaceSuspension,
        RestoreOwnedSurfaceCheckpoint,
        StageOwnedSurfaceCheckpoint,
        GetOwnedSurfaceResourceState,
        GetSurfaceRenderProxyState,
        ReconcileSurfaceRenderProxies,
        ReleaseDetachedOwnedSurfaceRecovery,
    };
}
