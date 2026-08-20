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
builder.Services.AddSingleton<PortOperationsProjectionStore>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<GamePipeBroker>());

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

app.MapPost("/game/experimental/save-load/save", async (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
{
    var result = await broker.SendCommandAsync(
        GameCommandTypes.SaveExperimentalProbe,
        "{}",
        CommandPriorities.Control,
        cancellationToken);
    if (!result.Success)
    {
        return CommandFailure(result);
    }

    var scheduled = JsonSerializer.Deserialize<ExperimentalSaveLoadProbeResultPayload>(
        result.ResultJson,
        wireJsonOptions) ?? new ExperimentalSaveLoadProbeResultPayload();
    var stableHash = await WaitForStableFileHashAsync(
        scheduled.SaveFilePath,
        TimeSpan.FromSeconds(8),
        cancellationToken);
    if (string.IsNullOrEmpty(stableHash))
    {
        return Results.Json(new
        {
            error = ProtocolErrorCodes.AutomationPostconditionFailed,
            message = "Sea Power scheduled the native save but no stable file materialized before the deadline.",
        }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    scheduled.Outcome = "completed";
    scheduled.Postcondition = "active-weapon-save-materialized";
    scheduled.SaveFileSha256 = stableHash;
    return Results.Ok(scheduled);
});

app.MapPost("/game/experimental/save-load/launch", (
    ExperimentalWeaponLaunchRequestPayload request,
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandWithPayloadAsync(
        broker,
        GameCommandTypes.LaunchExperimentalProbeWeapon,
        JsonSerializer.Serialize(request, wireJsonOptions),
        CommandPriorities.Control,
        cancellationToken));

app.MapPost("/game/experimental/save-load/load", (
    GamePipeBroker broker,
    CancellationToken cancellationToken) =>
    SendCommandAsync(
        broker,
        GameCommandTypes.LoadExperimentalProbe,
        CommandPriorities.Control,
        cancellationToken));

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

static async Task<string> WaitForStableFileHashAsync(
    string path,
    TimeSpan timeout,
    CancellationToken cancellationToken)
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
                if (hash == priorHash && stream.Length > 0)
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
