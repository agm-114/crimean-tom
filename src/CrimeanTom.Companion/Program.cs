using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using CrimeanTom.Companion;
using CrimeanTom.Protocol;

var builder = WebApplication.CreateBuilder(args);
var wireJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var controlUrl = RequireLoopbackControlUrl(
    builder.Configuration["ControlUrl"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CONTROL_URL")
    ?? ProtocolConstants.DefaultControlUrl);
builder.WebHost.UseUrls(controlUrl);

var controlTokenPath = builder.Configuration["ControlTokenFile"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CONTROL_TOKEN_FILE")
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".run", "control.token");
var controlToken = ControlTokenStore.LoadOrCreate(controlTokenPath);
var gamePipeName = builder.Configuration["GamePipeName"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_GAME_PIPE_NAME")
    ?? ProtocolConstants.GamePipeName;
if (string.IsNullOrWhiteSpace(gamePipeName))
{
    throw new InvalidOperationException("GamePipeName must not be empty.");
}

builder.Services.AddSingleton(controlToken);
builder.Services.AddSingleton(serviceProvider => new GamePipeBroker(
    serviceProvider.GetRequiredService<ILogger<GamePipeBroker>>(),
    gamePipeName));
builder.Services.AddSingleton<CompanionColliderCatalog>();
builder.Services.AddSingleton<PortOperationsProjectionStore>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<GamePipeBroker>());
var campaignServiceGrpcUrl = builder.Configuration["CampaignServiceGrpcUrl"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_GRPC_URL");
var campaignServiceHttpUrl = string.Empty;
var campaignId = string.Empty;
var playerCoalitionId = string.Empty;
var debugRevealAllEnemies = false;
if (!string.IsNullOrWhiteSpace(campaignServiceGrpcUrl))
{
    campaignServiceHttpUrl = builder.Configuration["CampaignServiceHttpUrl"]
        ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_HTTP_URL")
        ?? throw new InvalidOperationException("CampaignServiceHttpUrl is required when campaign transport is enabled.");
    campaignId = builder.Configuration["CampaignId"]
        ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_ID")
        ?? throw new InvalidOperationException("CampaignId is required when campaign transport is enabled.");
    if (!Guid.TryParseExact(campaignId, "N", out _))
    {
        throw new InvalidOperationException("CampaignId must use canonical 32-character GUID form.");
    }
    var campaignStateDirectory = Path.GetFullPath(
        builder.Configuration["CampaignStateDirectory"]
        ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_STATE_DIRECTORY")
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".run", "campaign-companion"));
    Directory.CreateDirectory(campaignStateDirectory);
    var clientInstanceId = LoadOrCreateClientInstanceId(Path.Combine(campaignStateDirectory, "client-instance-id"));
    playerCoalitionId = builder.Configuration["PlayerCoalitionId"]
        ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_PLAYER_COALITION_ID")
        ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(playerCoalitionId)
        && !Guid.TryParseExact(playerCoalitionId, "N", out _))
    {
        throw new InvalidOperationException(
            "PlayerCoalitionId must be empty or use canonical 32-character GUID form.");
    }
    var tacticalEntityCapacity = ReadBoundedInteger(
        builder.Configuration["TacticalEntityCapacity"]
            ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_TACTICAL_ENTITY_CAPACITY"),
        defaultValue: 4,
        minimum: 1,
        maximum: 10_000,
        "TacticalEntityCapacity");
    var tacticalReserveCapacity = ReadBoundedInteger(
        builder.Configuration["TacticalReserveCapacity"]
            ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_TACTICAL_RESERVE_CAPACITY"),
        defaultValue: 1,
        minimum: 0,
        maximum: tacticalEntityCapacity,
        "TacticalReserveCapacity");
    debugRevealAllEnemies = bool.TryParse(
        builder.Configuration["DebugRevealAllEnemies"]
            ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_DEBUG_REVEAL_ALL_ENEMIES"),
        out var revealAllEnemies)
        && revealAllEnemies;
    var options = new CampaignCompanionClientOptions(
        RequireCampaignServiceUri(campaignServiceGrpcUrl),
        RequireCampaignServiceUri(campaignServiceHttpUrl),
        campaignId,
        clientInstanceId,
        Path.GetFullPath(builder.Configuration["CampaignServiceTokenFile"]
            ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE")
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".run", "campaign-service", "service.token")),
        campaignStateDirectory,
        playerCoalitionId,
        tacticalEntityCapacity,
        tacticalReserveCapacity,
        debugRevealAllEnemies);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(new CampaignCompanionStateStore(campaignStateDirectory));
    builder.Services.AddHostedService<CampaignCompanionClient>();
}

var app = builder.Build();

app.Use(async (context, next) =>
    await ControlTokenAuthentication.InvokeAsync(context, controlToken, next));

app.MapGet("/health", (GamePipeBroker broker) => Results.Ok(new
{
    status = "ok",
    protocolVersion = ProtocolConstants.Version,
    maximumFrameBytes = ProtocolConstants.MaximumFrameBytes,
    gameConnected = broker.IsGameConnected,
    connectionState = broker.ConnectionState,
    sessionGeneration = broker.SessionGeneration,
    lastConnectionTransitionUnixMilliseconds = broker.LastConnectionTransitionUnixMilliseconds,
    sessionId = broker.SessionId,
    campaignTransport = new
    {
        enabled = !string.IsNullOrWhiteSpace(campaignServiceGrpcUrl),
        campaignId,
        playerCoalitionId,
        debugRevealAllEnemies,
        grpcUrl = campaignServiceGrpcUrl ?? string.Empty,
        httpUrl = campaignServiceHttpUrl,
    },
    game = broker.GameHello,
}));

app.MapGet("/game/ping", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.Ping,
        CommandPriorities.Control,
        cancellationToken));

app.MapGet("/game/capabilities", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetCapabilities,
        CommandPriorities.Control,
        cancellationToken));

app.MapGet("/game/scene", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetSceneState,
        CommandPriorities.Normal,
        cancellationToken));

app.MapGet("/game/world", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetWorldSnapshot,
        CommandPriorities.Bulk,
        cancellationToken));

app.MapGet("/game/memory", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetRuntimeMemoryState,
        CommandPriorities.Bulk,
        cancellationToken));

app.MapGet("/game/position-watch", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetPositionWatch,
        CommandPriorities.Bulk,
        cancellationToken));

app.MapPost("/game/position-watch", (
    PositionWatchRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.SetPositionWatch,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/terrain/probe", (
    TerrainPlacementProbeRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.ProbeTerrainPlacement,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/terrain/prepare", (
    PrepareTacticalLocationRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var sessionError = ValidateMutationSession(request.SessionId, broker);
    if (sessionError is not null)
    {
        return Task.FromResult(sessionError);
    }
    if (!double.IsFinite(request.Latitude)
        || !double.IsFinite(request.Longitude)
        || request.Latitude is < -88d or > 88d
        || request.Longitude is < -180d or >= 180d)
    {
        return Task.FromResult<IResult>(Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The tactical-location coordinates are invalid.",
        }, statusCode: StatusCodes.Status400BadRequest));
    }
    return SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.PrepareTacticalLocation,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken);
});

app.MapGet("/game/coupling", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetTacticalCouplingState,
        CommandPriorities.Control,
        cancellationToken));

app.MapGet("/game/mutations/surface/resources", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetOwnedSurfaceResourceState,
        CommandPriorities.Normal,
        cancellationToken));

app.MapGet("/game/render-proxies", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetSurfaceRenderProxyState,
        CommandPriorities.Normal,
        cancellationToken));

app.MapGet("/game/catalog/units", (
    int? offset,
    int? limit,
    string? folder,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.GetUnitCatalogPage,
        JsonSerializer.Serialize(new UnitCatalogPageRequestPayload
        {
            Offset = offset ?? 0,
            Limit = limit ?? 25,
            Folder = folder,
        }, wireJsonOptions),
        CommandPriorities.Bulk,
        cancellationToken));

app.MapGet("/game/catalog/references", (
    string kind,
    int? offset,
    int? limit,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.GetReferenceCatalogPage,
        JsonSerializer.Serialize(new ReferenceCatalogPageRequestPayload
        {
            Kind = kind,
            Offset = offset ?? 0,
            Limit = limit ?? 50,
        }, wireJsonOptions),
        CommandPriorities.Bulk,
        cancellationToken));

app.MapGet("/game/catalog/diagnostics", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetCatalogDiagnostics,
        CommandPriorities.Bulk,
        cancellationToken));

app.MapGet("/game/campaign", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetCampaignManagementState,
        CommandPriorities.Normal,
        cancellationToken));

app.MapGet("/game/ui", (GamePipeBroker broker, CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.GetUiInspection,
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/colliders/capture", (
    CaptureColliderCatalogRequestPayload? request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.CaptureColliderCatalog,
        JsonSerializer.Serialize(request ?? new CaptureColliderCatalogRequestPayload(), wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapGet("/game/colliders", (
    string? unitTypeId,
    CompanionColliderCatalog catalog) =>
    Results.Json(catalog.Get(unitTypeId), wireJsonOptions));

app.MapPost("/game/screenshot", async (
    CaptureGameScreenshotRequestPayload? request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var hello = broker.GameHello;
    if (hello is null || string.IsNullOrWhiteSpace(hello.ExperimentalSaveDirectory))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.AutomationPreconditionFailed,
            message = "No connected game has advertised an experimental save directory.",
        }, statusCode: StatusCodes.Status409Conflict);
    }

    var screenshotPath = Path.GetFullPath(Path.Combine(
        hello.ExperimentalSaveDirectory,
        "_CrimeanTomScreenshot.png"));
    var previousHash = await TryHashFileAsync(screenshotPath, cancellationToken);
    var previousLastWriteTimeUtc = File.Exists(screenshotPath)
        ? File.GetLastWriteTimeUtc(screenshotPath)
        : (DateTime?)null;

    var payload = request ?? new CaptureGameScreenshotRequestPayload();
    var result = await broker.SendCommandAsync(
        GameCommandTypes.CaptureGameScreenshot,
        JsonSerializer.Serialize(payload, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken);
    if (!result.Success)
    {
        return CommandFailure(result);
    }

    var scheduled = JsonSerializer.Deserialize<CaptureGameScreenshotResultPayload>(
        result.ResultJson,
        wireJsonOptions) ?? new CaptureGameScreenshotResultPayload();
    if (string.IsNullOrWhiteSpace(scheduled.FilePath)
        || !string.Equals(
            Path.GetFullPath(scheduled.FilePath),
            screenshotPath,
            StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The game returned an unexpected screenshot path.",
        }, statusCode: StatusCodes.Status502BadGateway);
    }

    var stableHash = await WaitForStableFileHashAsync(
        screenshotPath,
        TimeSpan.FromSeconds(15),
        cancellationToken,
        excludeHash: previousHash,
        excludeLastWriteTimeUtc: previousLastWriteTimeUtc);
    if (string.IsNullOrEmpty(stableHash))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.AutomationPostconditionFailed,
            message = "The game scheduled a screenshot but no stable PNG materialized before the deadline.",
        }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    scheduled.Outcome = "completed";
    scheduled.Postcondition = "screenshot-png-materialized";
    scheduled.Sha256 = stableHash;
    return Results.Ok(scheduled);
});

app.MapGet("/game/screenshot/image", (GamePipeBroker broker) =>
{
    var hello = broker.GameHello;
    if (hello is null || string.IsNullOrWhiteSpace(hello.ExperimentalSaveDirectory))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.AutomationPreconditionFailed,
            message = "No connected game has advertised an experimental save directory.",
        }, statusCode: StatusCodes.Status409Conflict);
    }

    var fileName = "_CrimeanTomScreenshot.png";
    var fullPath = Path.GetFullPath(Path.Combine(hello.ExperimentalSaveDirectory, fileName));
    var root = Path.GetFullPath(hello.ExperimentalSaveDirectory);
    if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(fullPath, Path.Combine(root, fileName), StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "Screenshot path escaped the experimental save directory.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    if (!File.Exists(fullPath))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.AutomationPreconditionFailed,
            message = "No screenshot PNG is available yet. POST /game/screenshot first.",
        }, statusCode: StatusCodes.Status404NotFound);
    }

    return Results.File(fullPath, "image/png", fileName);
});

app.MapPost("/game/automation/camera/focus", (
    FocusCameraOnUnitRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.FocusCameraOnUnit,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/hud", (
    SetTacticalHudPanelRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.SetTacticalHudPanel,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/modal/dismiss", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.DismissTopModal,
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/task-force-builder", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.OpenTaskForceBuilder,
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/port-operations/start", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.StartPortOperations,
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/port-operations/draft/units", (
    AddMaintenanceDraftUnitRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.AddMaintenanceDraftUnit,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/scenario/start", (
    StartScenarioRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.StartScenario,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/briefing/continue", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.ContinueMissionBriefing,
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/automation/tactical/exit", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.ExitTacticalSession,
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/experimental/pause", (
    ExperimentalPauseRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.SetExperimentalPauseState,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/experimental/time-compression", (
    ExperimentalTimeCompressionRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.SetExperimentalTimeCompression,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/mutations/arm", (
    ArmOwnedMutationModeRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var validationError = ValidateMutationArmRequest(request, broker);
    return validationError is not null
        ? Task.FromResult(validationError)
        : SendCommandWithPayloadAsync(
            broker,
            GameCommandTypes.ArmOwnedMutationMode,
            JsonSerializer.Serialize(request, wireJsonOptions),
            CommandPriorities.Control,
            cancellationToken);
});

app.MapPost("/game/mutations/surface/materialize", (
    MaterializeOwnedSurfaceUnitRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var validationError = ValidateSurfaceMutationRequest(request, broker);
    return validationError is not null
        ? Task.FromResult(validationError)
        : SendCommandWithPayloadAsync(
            broker,
            GameCommandTypes.MaterializeOwnedSurfaceUnit,
            JsonSerializer.Serialize(request, wireJsonOptions),
            CommandPriorities.Control,
            cancellationToken);
});

app.MapPost("/game/mutations/hosted-aircraft/register", (
    RegisterOwnedHostedAircraftRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var validationError = ValidateHostedAircraftRequest(request, broker);
    return validationError is not null
        ? Task.FromResult(validationError)
        : SendCommandWithPayloadAsync(
            broker,
            GameCommandTypes.RegisterOwnedHostedAircraft,
            JsonSerializer.Serialize(request, wireJsonOptions),
            CommandPriorities.Control,
            cancellationToken);
});

app.MapPost("/game/mutations/hosted-aircraft/launch", (
    LaunchOwnedHostedAircraftRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    ForwardOwnedSurfaceCommand(
        request,
        request.Context,
        request.EntityId,
        GameCommandTypes.LaunchOwnedHostedAircraft,
        broker,
        wireJsonOptions,
        cancellationToken));

app.MapPost("/game/mutations/land/materialize", (
    MaterializeOwnedLandUnitRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var validationError = ValidateLandMutationRequest(request, broker);
    return validationError is not null
        ? Task.FromResult(validationError)
        : SendCommandWithPayloadAsync(
            broker,
            GameCommandTypes.MaterializeOwnedLandUnit,
            JsonSerializer.Serialize(request, wireJsonOptions),
            CommandPriorities.Control,
            cancellationToken);
});

app.MapPost("/game/mutations/surface/checkpoint", (
    OwnedSurfaceEntityRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    ForwardOwnedSurfaceCommand(
        request,
        request.Context,
        request.EntityId,
        GameCommandTypes.CaptureOwnedSurfaceCheckpoint,
        broker,
        wireJsonOptions,
        cancellationToken));

app.MapPost("/game/mutations/surface/update", (
    UpdateOwnedSurfaceUnitRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var faultError = ValidateFaultInjectionRequest(
        request.FaultInjectionStep,
        request.FaultInjectionConfirmation,
        "after-position-apply",
        "after-telegraph-apply",
        "after-readback");
    return faultError is not null
        ? Task.FromResult(faultError)
        : ForwardOwnedSurfaceCommand(
            request,
            request.Context,
            request.EntityId,
            GameCommandTypes.UpdateOwnedSurfaceUnit,
            broker,
            wireJsonOptions,
            cancellationToken);
});

app.MapPost("/game/mutations/surface/suspension", (
    SetOwnedSurfaceSuspensionRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var faultError = ValidateFaultInjectionRequest(
        request.FaultInjectionStep,
        request.FaultInjectionConfirmation,
        "after-native-toggle",
        "after-readback");
    return faultError is not null
        ? Task.FromResult(faultError)
        : ForwardOwnedSurfaceCommand(
            request,
            request.Context,
            request.EntityId,
            GameCommandTypes.SetOwnedSurfaceSuspension,
            broker,
            wireJsonOptions,
            cancellationToken);
});

app.MapPost("/game/mutations/surface/restore", (
    RestoreOwnedSurfaceCheckpointRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var validationError = ValidateOwnedLifecycleRequest(
        request.Context,
        request.EntityId,
        broker);
    if (validationError is not null)
    {
        return Task.FromResult(validationError);
    }
    if (string.IsNullOrWhiteSpace(request.CheckpointId)
        || request.CheckpointId.Length > 256
        || request.CheckpointSha256.Length != 64
        || request.CheckpointSha256.Any(character =>
            character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
    {
        return Task.FromResult<IResult>(Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The portable checkpoint identity or SHA-256 is invalid.",
        }, statusCode: StatusCodes.Status400BadRequest));
    }
    var faultError = ValidateFaultInjectionRequest(
        request.FaultInjectionStep,
        request.FaultInjectionConfirmation,
        "after-state-apply",
        "after-enable",
        "after-readback");
    if (faultError is not null)
    {
        return Task.FromResult(faultError);
    }
    return SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.RestoreOwnedSurfaceCheckpoint,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken);
});

app.MapPost("/game/mutations/surface/recovery/release", (
    OwnedSurfaceEntityRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var validationError = ValidateOwnedEntityFenceRequest(request, broker);
    return validationError is not null
        ? Task.FromResult(validationError)
        : SendCommandWithPayloadAsync(
            broker,
            GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery,
            JsonSerializer.Serialize(request, wireJsonOptions),
            CommandPriorities.Control,
            cancellationToken);
});

app.MapPost("/game/mutations/surface/materialization/rollback", (
    OwnedSurfaceEntityRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var validationError = ValidateOwnedEntityFenceRequest(request, broker);
    return validationError is not null
        ? Task.FromResult(validationError)
        : SendCommandWithPayloadAsync(
            broker,
            GameCommandTypes.RollbackIncompleteOwnedMaterialization,
            JsonSerializer.Serialize(request, wireJsonOptions),
            CommandPriorities.Control,
            cancellationToken);
});

app.MapGet("/port-operations/projection", (PortOperationsProjectionStore store) =>
    Results.Ok(store.Load()));

app.MapPost("/port-operations/projection/materialize", async (
    PortOperationsProjectionStore store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.MaterializeAsync(cancellationToken)));

app.MapPost("/port-operations/drafts/import", async (
    PortMaintenanceDraftImportRequest request,
    PortOperationsProjectionStore store,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var result = await broker.SendCommandAsync(
        GameCommandTypes.GetCampaignManagementState,
        "{}",
        CommandPriorities.Normal,
        cancellationToken);
    if (!result.Success)
    {
        return Results.Json(new
        {
            error = result.ErrorCode,
            message = result.ErrorMessage,
            errorId = result.ErrorId,
        }, statusCode: result.HttpStatusCode);
    }

    var nativeDraft = JsonSerializer.Deserialize<CampaignManagementStatePayload>(
        result.ResultJson,
        wireJsonOptions) ?? new CampaignManagementStatePayload();
    return Results.Ok(store.ValidateDraft(request, nativeDraft));
});

app.Run();

static Uri RequireCampaignServiceUri(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException($"Campaign service URL '{value}' is invalid.");
    }
    if (uri.Scheme == Uri.UriSchemeHttp
        && (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)))
    {
        throw new InvalidOperationException("Unencrypted campaign transport is permitted only on explicit loopback addresses.");
    }
    return uri;
}

static string LoadOrCreateClientInstanceId(string path)
{
    if (File.Exists(path))
    {
        var existing = File.ReadAllText(path).Trim();
        if (Guid.TryParseExact(existing, "N", out _))
        {
            return existing;
        }
        throw new InvalidDataException("Campaign companion client identity is invalid.");
    }
    var value = Guid.NewGuid().ToString("N");
    File.WriteAllText(path, value);
    return value;
}

static string RequireLoopbackControlUrl(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException($"ControlUrl '{value}' is not a valid HTTP(S) URL.");
    }

    var isLoopback = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
    if (!isLoopback)
    {
        throw new InvalidOperationException(
            $"ControlUrl '{value}' is not loopback-only. Refusing to expose the local control API.");
    }

    return value;
}

static async Task<IResult> SendCommandAsync(
    GamePipeBroker broker,
    string commandType,
    int priority,
    CancellationToken cancellationToken)
    => await SendCommandWithPayloadAsync(
        broker,
        commandType,
        "{}",
        priority,
        cancellationToken);

static async Task<IResult> SendCommandWithPayloadAsync(
    GamePipeBroker broker,
    string commandType,
    string payloadJson,
    int priority,
    CancellationToken cancellationToken)
{
    var result = await broker.SendCommandAsync(commandType, payloadJson, priority, cancellationToken);
    if (!result.Success)
    {
        return Results.Json(new
        {
            error = result.ErrorCode,
            message = result.ErrorMessage,
            errorId = result.ErrorId,
        }, statusCode: result.HttpStatusCode);
    }

    return Results.Content(result.ResultJson, "application/json");
}

static IResult CommandFailure(GameCommandResult result) => Results.Json(new
{
    error = result.ErrorCode,
    message = result.ErrorMessage,
    errorId = result.ErrorId,
}, statusCode: result.HttpStatusCode);

static IResult? ValidateMutationSession(string requestedSessionId, GamePipeBroker broker)
{
    if (string.IsNullOrWhiteSpace(requestedSessionId)
        || !string.Equals(requestedSessionId, broker.SessionId, StringComparison.Ordinal))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidSession,
            message = "The mutation request session does not match the connected game session.",
        }, statusCode: StatusCodes.Status409Conflict);
    }
    return null;
}

static IResult? ValidateMutationArmRequest(
    ArmOwnedMutationModeRequestPayload request,
    GamePipeBroker broker)
{
    var sessionError = ValidateMutationSession(request.SessionId, broker);
    if (sessionError is not null)
    {
        return sessionError;
    }
    if (!string.Equals(
            request.Confirmation,
            "enable-owned-mutations",
            StringComparison.Ordinal)
        || request.DurationSeconds is < 1 or > 600
        || request.RuntimeFingerprintSha256.Length != 64
        || request.RuntimeFingerprintSha256.Any(character =>
            character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "Mutation arming confirmation, duration, or runtime fingerprint is invalid.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return null;
}

static IResult? ValidateSurfaceMutationRequest(
    MaterializeOwnedSurfaceUnitRequestPayload request,
    GamePipeBroker broker)
{
    if (request.Context is null)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned surface mutation context is required.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    var sessionError = ValidateMutationSession(request.Context.SessionId, broker);
    if (sessionError is not null)
    {
        return sessionError;
    }
    if (string.IsNullOrWhiteSpace(request.Context.AssignmentId)
        || request.Context.AssignmentId.Length > 128
        || !request.Context.AssignmentId.StartsWith("assignment:", StringComparison.Ordinal)
        || request.Context.AuthorityEpoch < 1
        || request.Context.ExpectedRevision != 0
        || string.IsNullOrWhiteSpace(request.Context.IdempotencyKey)
        || request.Context.IdempotencyKey.Length > 128
        || string.IsNullOrWhiteSpace(request.EntityId)
        || request.EntityId.Length > 128
        || !request.EntityId.StartsWith("unit:crimean-tom:", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(request.DisplayName)
        || request.DisplayName.Length > 128
        || string.IsNullOrWhiteSpace(request.UnitTypeId)
        || request.UnitTypeId.Length > 128
        || string.IsNullOrWhiteSpace(request.VariantId)
        || request.VariantId.Length > 128
        || string.IsNullOrWhiteSpace(request.LoadoutId)
        || request.LoadoutId.Length > 128
        || string.IsNullOrWhiteSpace(request.TaskforceId)
        || !double.IsFinite(request.Latitude)
        || !double.IsFinite(request.Longitude)
        || !double.IsFinite(request.HeadingDegrees)
        || request.Latitude is < -90d or > 90d
        || request.Longitude is < -180d or > 180d
        || request.Telegraph is < -1 or > 5)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned surface mutation payload is incomplete or invalid.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    if (request.Context.DryRun && !string.IsNullOrEmpty(request.FaultInjectionStep))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "Controlled materialization faults require a live mutation, not dryRun.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return ValidateFaultInjectionRequest(
        request.FaultInjectionStep,
        request.FaultInjectionConfirmation,
        "after-owned-tag",
        "after-state-apply",
        "after-registry-add");
}

static IResult? ValidateLandMutationRequest(
    MaterializeOwnedLandUnitRequestPayload request,
    GamePipeBroker broker)
{
    if (request.Context is null)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned land mutation context is required.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    var sessionError = ValidateMutationSession(request.Context.SessionId, broker);
    if (sessionError is not null)
    {
        return sessionError;
    }
    if (string.IsNullOrWhiteSpace(request.Context.AssignmentId)
        || request.Context.AssignmentId.Length > 128
        || !request.Context.AssignmentId.StartsWith("assignment:", StringComparison.Ordinal)
        || request.Context.AuthorityEpoch < 1
        || request.Context.ExpectedRevision != 0
        || string.IsNullOrWhiteSpace(request.Context.IdempotencyKey)
        || request.Context.IdempotencyKey.Length > 128
        || string.IsNullOrWhiteSpace(request.EntityId)
        || request.EntityId.Length > 128
        || !request.EntityId.StartsWith("unit:crimean-tom:", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(request.UnitTypeId)
        || string.IsNullOrWhiteSpace(request.VariantId)
        || string.IsNullOrWhiteSpace(request.LoadoutId)
        || string.IsNullOrWhiteSpace(request.TaskforceId)
        || !double.IsFinite(request.Latitude)
        || !double.IsFinite(request.Longitude)
        || !double.IsFinite(request.HeadingDegrees)
        || request.Latitude is < -90d or > 90d
        || request.Longitude is < -180d or >= 180d
        || request.Telegraph is < -1 or > 5)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned land mutation payload is incomplete or invalid.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return null;
}

static IResult? ValidateHostedAircraftRequest(
    RegisterOwnedHostedAircraftRequestPayload request,
    GamePipeBroker broker)
{
    if (request.Context is null)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The hosted-aircraft mutation context is required.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    var sessionError = ValidateMutationSession(request.Context.SessionId, broker);
    if (sessionError is not null)
    {
        return sessionError;
    }
    if (string.IsNullOrWhiteSpace(request.Context.AssignmentId)
        || request.Context.AssignmentId.Length > 128
        || !request.Context.AssignmentId.StartsWith("assignment:", StringComparison.Ordinal)
        || request.Context.AuthorityEpoch < 1
        || request.Context.ExpectedRevision != 0
        || string.IsNullOrWhiteSpace(request.Context.IdempotencyKey)
        || request.Context.IdempotencyKey.Length > 128
        || string.IsNullOrWhiteSpace(request.EntityId)
        || request.EntityId.Length > 128
        || !request.EntityId.StartsWith("unit:crimean-tom:", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(request.HostEntityId)
        || request.HostEntityId.Length > 128
        || !request.HostEntityId.StartsWith("unit:crimean-tom:", StringComparison.Ordinal)
        || string.Equals(request.EntityId, request.HostEntityId, StringComparison.Ordinal)
        || request.DisplayName.Length > 128
        || request.UnitTypeId is not ("usn_sh-2f" or "usn_sh-3h")
        || string.IsNullOrWhiteSpace(request.VariantId)
        || request.VariantId.Length > 128
        || string.IsNullOrWhiteSpace(request.LoadoutId)
        || request.LoadoutId.Length > 128)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The hosted-aircraft mutation payload is incomplete or invalid.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return null;
}

static IResult? ValidateFaultInjectionRequest(
    string step,
    string confirmation,
    params string[] allowedSteps)
{
    if (string.IsNullOrEmpty(step) && string.IsNullOrEmpty(confirmation))
    {
        return null;
    }
    if (!string.Equals(
            confirmation,
            "inject-owned-mutation-failure",
            StringComparison.Ordinal)
        || !allowedSteps.Contains(step, StringComparer.Ordinal))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The controlled fault-injection confirmation or step is invalid.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return null;
}

static Task<IResult> ForwardOwnedSurfaceCommand<T>(
    T request,
    OwnedMutationContextPayload? context,
    string entityId,
    string command,
    GamePipeBroker broker,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    var validationError = ValidateOwnedLifecycleRequest(context, entityId, broker);
    return validationError is not null
        ? Task.FromResult(validationError)
        : SendCommandWithPayloadAsync(
            broker,
            command,
            JsonSerializer.Serialize(request, jsonOptions),
            CommandPriorities.Control,
            cancellationToken);
}

static IResult? ValidateOwnedLifecycleRequest(
    OwnedMutationContextPayload? context,
    string entityId,
    GamePipeBroker broker)
{
    if (context is null)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned lifecycle mutation context is required.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    var sessionError = ValidateMutationSession(context.SessionId, broker);
    if (sessionError is not null)
    {
        return sessionError;
    }
    if (string.IsNullOrWhiteSpace(context.AssignmentId)
        || !context.AssignmentId.StartsWith("assignment:", StringComparison.Ordinal)
        || context.AssignmentId.Length > 128
        || context.AuthorityEpoch < 1
        || context.ExpectedRevision < 1
        || string.IsNullOrWhiteSpace(context.IdempotencyKey)
        || context.IdempotencyKey.Length > 128
        || string.IsNullOrWhiteSpace(entityId)
        || !entityId.StartsWith("unit:crimean-tom:", StringComparison.Ordinal)
        || entityId.Length > 128)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned lifecycle mutation identity or fence is invalid.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return null;
}

static IResult? ValidateOwnedEntityFenceRequest(
    OwnedSurfaceEntityRequestPayload request,
    GamePipeBroker broker)
{
    var context = request.Context;
    if (context is null)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned entity mutation context is required.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    var sessionError = ValidateMutationSession(context.SessionId, broker);
    if (sessionError is not null)
    {
        return sessionError;
    }
    if (string.IsNullOrWhiteSpace(context.AssignmentId)
        || !context.AssignmentId.StartsWith("assignment:", StringComparison.Ordinal)
        || context.AssignmentId.Length > 128
        || context.AuthorityEpoch < 1
        || context.ExpectedRevision < 0
        || string.IsNullOrWhiteSpace(context.IdempotencyKey)
        || context.IdempotencyKey.Length > 128
        || string.IsNullOrWhiteSpace(request.EntityId)
        || !request.EntityId.StartsWith("unit:crimean-tom:", StringComparison.Ordinal)
        || request.EntityId.Length > 128)
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.InvalidMessage,
            message = "The owned entity mutation identity or fence is invalid.",
        }, statusCode: StatusCodes.Status400BadRequest);
    }
    return null;
}

static int ReadBoundedInteger(
    string? value,
    int defaultValue,
    int minimum,
    int maximum,
    string name)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }
    if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
    {
        throw new InvalidOperationException(
            $"{name} must be an integer from {minimum} through {maximum}.");
    }
    return parsed;
}

static async Task<string?> TryHashFileAsync(string path, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        return null;
    }

    try
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0)
        {
            return null;
        }

        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
    catch (IOException)
    {
        return null;
    }
}

static async Task<string> WaitForStableFileHashAsync(
    string path,
    TimeSpan timeout,
    CancellationToken cancellationToken,
    string? excludeHash = null,
    DateTime? excludeLastWriteTimeUtc = null)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return string.Empty;
    }

    var deadline = DateTime.UtcNow + timeout;
    string? priorHash = null;
    var consecutiveMatches = 0;
    while (DateTime.UtcNow < deadline)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(
                    stream,
                    cancellationToken)).ToLowerInvariant();
                var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                var isUnchangedExcludedFile = !string.IsNullOrEmpty(excludeHash)
                    && string.Equals(hash, excludeHash, StringComparison.Ordinal)
                    && (!excludeLastWriteTimeUtc.HasValue
                        || lastWriteTimeUtc <= excludeLastWriteTimeUtc.Value);
                if (isUnchangedExcludedFile)
                {
                    priorHash = hash;
                    consecutiveMatches = 0;
                }
                else if (hash == priorHash && stream.Length > 0)
                {
                    consecutiveMatches++;
                    if (consecutiveMatches >= 2)
                    {
                        return hash;
                    }
                }
                else
                {
                    priorHash = hash;
                    consecutiveMatches = 0;
                }
            }
        }
        catch (IOException)
        {
            priorHash = null;
            consecutiveMatches = 0;
        }

        await Task.Delay(100, cancellationToken);
    }

    return string.Empty;
}
