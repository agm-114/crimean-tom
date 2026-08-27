using System.Net;
using System.Text.Json.Serialization;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.ServiceHost;
using CrimeanTom.Campaign.Transport;
using CrimeanTom.Campaign.Transport.V1;

using Microsoft.AspNetCore.Server.Kestrel.Core;

var workingDirectory = Directory.GetCurrentDirectory();
var repositoryRoot = FindRepositoryRoot(workingDirectory);
var workingDirectoryWebRoot = Path.GetFullPath(Path.Combine(workingDirectory, "wwwroot"));
var repositoryWebRoot = repositoryRoot is null
    ? string.Empty
    : Path.Combine(repositoryRoot, "src", "CrimeanTom.Campaign.ServiceHost", "wwwroot");
var deployedWebRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
var webRoot = new[] { workingDirectoryWebRoot, repositoryWebRoot, deployedWebRoot }
    .FirstOrDefault(Directory.Exists)
    ?? deployedWebRoot;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot,
});
var grpcUri = RequireLoopbackUri(
    builder.Configuration["GrpcUrl"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_GRPC_URL")
    ?? "http://127.0.0.1:5082");
var httpUri = RequireLoopbackUri(
    builder.Configuration["HttpUrl"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_HTTP_URL")
    ?? "http://127.0.0.1:5081");
if (grpcUri.Port == httpUri.Port)
{
    throw new InvalidOperationException("Campaign gRPC and HTTP endpoints require distinct loopback ports.");
}
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, grpcUri.Port, endpoint => endpoint.Protocols = HttpProtocols.Http2);
    options.Listen(IPAddress.Loopback, httpUri.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
});

var runDirectory = Path.GetFullPath(
    builder.Configuration["RunDirectory"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_CAMPAIGN_RUN_DIRECTORY")
    ?? Path.Combine(repositoryRoot ?? workingDirectory, ".run", "campaign-service"));
var token = CampaignServiceToken.LoadOrCreate(Path.Combine(runDirectory, "service.token"));
var debugRevealSetting = builder.Configuration["AllowDebugRevealAllEnemies"]
    ?? Environment.GetEnvironmentVariable("CRIMEAN_TOM_ALLOW_DEBUG_REVEAL_ALL_ENEMIES");
var allowDebugRevealAllEnemies = string.IsNullOrWhiteSpace(debugRevealSetting)
    || (bool.TryParse(debugRevealSetting, out var allowDebugEnemyProjection)
        ? allowDebugEnemyProjection
        : throw new InvalidOperationException(
            "AllowDebugRevealAllEnemies must be 'true' or 'false'."));
var monitorSession = new CampaignMonitorSession();
var unattendedSimulation = CampaignUnattendedSimulationOptions.Load();
var journal = new SqliteCampaignJournal(
    Path.Combine(runDirectory, "campaign.db"),
    Path.Combine(runDirectory, "blobs"));
await journal.InitializeAsync();

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 256 * 1024;
    // Campaign snapshots and simulation deltas can exceed gRPC's 4 MiB default
    // while a 4K campaign worker is catching up. The clients already enforce the
    // shared 64 MiB service-frame ceiling, so make the server use the same bound
    // instead of terminating and reconnecting once per oversized event.
    options.MaxSendMessageSize = CampaignTransportLimits.MaximumServiceFrameBytes;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(token);
builder.Services.AddSingleton(monitorSession);
builder.Services.AddSingleton(unattendedSimulation);
builder.Services.AddSingleton(journal);
builder.Services.AddSingleton(new CampaignHostPaths(runDirectory, Path.Combine(runDirectory, "backups")));
builder.Services.AddSingleton<CampaignSessionRegistry>();
builder.Services.AddSingleton<CampaignInteractionIslandRegistry>();
builder.Services.AddSingleton<CampaignPlayerSessionRegistry>();
builder.Services.AddSingleton<CampaignPlayerCommandSequencer>();
builder.Services.AddSingleton<CampaignPerformanceTelemetry>();
builder.Services.AddSingleton(new CampaignInboundSpool(Path.Combine(runDirectory, "inbound-spool")));
builder.Services.AddSingleton<CampaignCoordinator>();
builder.Services.AddHostedService<CampaignUnattendedSimulationHostedService>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    var requestPath = context.Request.Path.Value;
    if (context.Request.Method == HttpMethods.Get
        && context.Request.Path.StartsWithSegments("/monitor")
        && !CampaignMonitorRouting.IsExistingMonitorAsset(webRoot, requestPath))
    {
        context.Response.Cookies.Append(
            CampaignMonitorSession.CookieName,
            monitorSession.Value,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Path = "/player",
                SameSite = SameSiteMode.Strict,
                Secure = false,
            });
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/monitor"))
    {
        await next(context);
        return;
    }
    if (context.Connection.LocalPort == grpcUri.Port
        && context.Request.Protocol.StartsWith("HTTP/2", StringComparison.Ordinal)
        && context.Request.ContentType?.StartsWith("application/grpc", StringComparison.Ordinal) == true)
    {
        await next(context);
        return;
    }
    var supplied = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    var bearerAuthenticated = supplied.StartsWith(prefix, StringComparison.Ordinal)
        && token.IsMatch(supplied[prefix.Length..]);
    var monitorCookieAuthenticated = context.Request.Cookies.TryGetValue(
            CampaignMonitorSession.CookieName,
            out var monitorCookie)
        && monitorSession.IsMatch(monitorCookie);
    var monitorAuthenticated = monitorCookieAuthenticated
        && context.Request.Path.StartsWithSegments("/player");
    if (!bearerAuthenticated && !monitorAuthenticated)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "invalid-campaign-service-token" });
        return;
    }
    await next(context);
});

app.MapGrpcService<CampaignCompanionGrpcService>();
app.MapGet("/monitor/session", () => Results.NoContent());
app.MapGet("/player/status", () => Results.Ok(new
{
    status = "ready",
    scenarioId = "ww3-plus-1989",
}));
app.MapGet("/player/campaigns", async (
    SqliteCampaignJournal store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.ListPlayerCampaignsAsync(cancellationToken)));
app.MapPost("/player/campaigns", async (
    PlayerCampaignCreateRequest request,
    SqliteCampaignJournal store,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var name = request.Name?.Trim() ?? string.Empty;
    if (name.Length is < 1 or > 80)
    {
        return Results.BadRequest(new { error = "invalid-campaign-name" });
    }
    if (!string.Equals(request.Coalition, "Blue", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(request.Coalition, "Red", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "invalid-player-coalition" });
    }

    var campaignId = new CampaignId(Guid.NewGuid());
    try
    {
        await store.CreateCampaignAsync(new CampaignMetadata(
            campaignId,
            name,
            19890911,
            "ww3-plus-1989-v1",
            CampaignSchema.Version,
            CampaignSchema.AlgorithmVersion), cancellationToken);
        await store.CreatePlayerProfileAsync(campaignId, "local-player", cancellationToken);
        var seeded = await coordinator.BootstrapFuldaGapAsync(campaignId, cancellationToken);
        var coalitionId = string.Equals(request.Coalition, "Red", StringComparison.OrdinalIgnoreCase)
            ? seeded.RedCoalitionId
            : seeded.BlueCoalitionId;
        await store.SetPlayerCoalitionAsync(campaignId, coalitionId, cancellationToken);
        _ = await coordinator.SavePlayerAsync(campaignId, cancellationToken);
        return Results.Created(
            $"/player/campaigns/{campaignId}",
            await coordinator.ViewPlayerAsync(campaignId, cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapGet("/player/campaigns/{campaignId}", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.ViewPlayerAsync(new CampaignId(parsed), cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/player/campaigns/{campaignId}/pause", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await SetPlayerPauseAsync(campaignId, paused: true, coordinator, cancellationToken));
app.MapPost("/player/campaigns/{campaignId}/resume", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await SetPlayerPauseAsync(campaignId, paused: false, coordinator, cancellationToken));
app.MapPost("/player/campaigns/{campaignId}/save", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.SavePlayerAsync(new CampaignId(parsed), cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/player/campaigns/{campaignId}/groups/{groupId}/orders/move", async (
    string campaignId,
    string groupId,
    PlayerMoveOrderRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await IssuePlayerOrderAsync(
        campaignId,
        groupId,
        request.DestinationRegionId,
        request.ExpectedGroupRevision,
        request.IdempotencyKey,
        holdPosition: false,
        coordinator,
        cancellationToken));
app.MapPost("/player/campaigns/{campaignId}/groups/{groupId}/orders/cancel", async (
    string campaignId,
    string groupId,
    PlayerCancelOrderRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await IssuePlayerOrderAsync(
        campaignId,
        groupId,
        destinationRegionId: null,
        request.ExpectedGroupRevision,
        request.IdempotencyKey,
        holdPosition: true,
        coordinator,
        cancellationToken));
app.MapGet("/player/campaigns/{campaignId}/groups/{groupId}/tactical-entry", async (
    string campaignId,
    string groupId,
    string? entityId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await InspectPlayerTacticalEntryAsync(
        campaignId,
        groupId,
        entityId,
        coordinator,
        cancellationToken));
app.MapPost("/player/campaigns/{campaignId}/groups/{groupId}/tactical-entry", async (
    string campaignId,
    string groupId,
    PlayerTacticalEntryRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await RequestPlayerTacticalEntryAsync(
        campaignId,
        groupId,
        request,
        coordinator,
        cancellationToken));
app.MapGet("/player/campaigns/{campaignId}/groups/{groupId}/tactical-exit", async (
    string campaignId,
    string groupId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await InspectPlayerTacticalExitAsync(
        campaignId,
        groupId,
        coordinator,
        cancellationToken));
app.MapPost("/player/campaigns/{campaignId}/groups/{groupId}/tactical-exit", async (
    string campaignId,
    string groupId,
    PlayerTacticalExitRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    await RequestPlayerTacticalExitAsync(
        campaignId,
        groupId,
        request,
        coordinator,
        cancellationToken));
app.MapGet("/health", (CampaignSessionRegistry sessions) => Results.Ok(new
{
    status = "ok",
    protocolVersion = 1,
    grpcUrl = grpcUri.ToString(),
    tacticalLeasePolicy = sessions.TacticalLeasePolicy,
    session = sessions.Snapshot(),
    sessions = sessions.Snapshots(),
}));
app.MapGet("/durability-policy", () => Results.Ok(CampaignDurabilityPolicy.Current));
app.MapGet("/supported-claims", () => Results.Ok(CampaignSupportedClaims.Current));
app.MapGet("/active-campaign", async (
    SqliteCampaignJournal store,
    CampaignSessionRegistry sessions,
    CancellationToken cancellationToken) =>
    Results.Ok(new
    {
        campaignId = CampaignMonitorRouting.ResolveActiveCampaignId(
            await store.ListCampaignsAsync(cancellationToken),
            sessions.Snapshots()),
    }));
app.MapGet("/campaigns", async (
    SqliteCampaignJournal store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.ListCampaignsAsync(cancellationToken)));
app.MapPost("/control/campaigns", async (
    CampaignCreateRequest request,
    SqliteCampaignJournal store,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(request.CampaignId, "N", out var campaignId))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    await store.CreateCampaignAsync(new CampaignMetadata(
        new CampaignId(campaignId),
        request.Name,
        request.Seed,
        request.CatalogFingerprint,
        CampaignSchema.Version,
        CampaignSchema.AlgorithmVersion), cancellationToken);
    return Results.Created($"/campaigns/{request.CampaignId}", new { request.CampaignId });
});
app.MapPost("/control/campaigns/{campaignId}/bootstrap", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed)
        ? Results.Ok(await coordinator.BootstrapAsync(new CampaignId(parsed), cancellationToken))
        : Results.BadRequest(new { error = "invalid-campaign-id" }));
app.MapPost("/control/campaigns/{campaignId}/bootstrap/fulda-gap", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed)
        ? Results.Ok(await coordinator.BootstrapFuldaGapAsync(new CampaignId(parsed), cancellationToken))
        : Results.BadRequest(new { error = "invalid-campaign-id" }));
app.MapGet("/campaigns/{campaignId}", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed)
        ? Results.Ok(await coordinator.ViewAsync(new CampaignId(parsed), cancellationToken))
        : Results.BadRequest(new { error = "invalid-campaign-id" }));
app.MapGet("/campaigns/{campaignId}/projections/{coalitionId}", async (
    string campaignId,
    string coalitionId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsedCampaign)
        || !Guid.TryParseExact(coalitionId, "N", out var parsedCoalition))
    {
        return Results.BadRequest(new { error = "invalid-campaign-or-coalition-id" });
    }
    try
    {
        return Results.Ok(await coordinator.ProjectCoalitionAsync(
            new CampaignId(parsedCampaign),
            new CoalitionId(parsedCoalition),
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapGet("/campaigns/{campaignId}/tactical-workers/{clientInstanceId}/projection", async (
    string campaignId,
    string clientInstanceId,
    bool? debugRevealAllEnemies,
    CampaignCoordinator coordinator,
    CampaignSessionRegistry sessions,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsedCampaign)
        || !Guid.TryParseExact(clientInstanceId, "N", out var parsedClient))
    {
        return Results.BadRequest(new { error = "invalid-campaign-or-client-instance-id" });
    }
    if (debugRevealAllEnemies == true && !allowDebugRevealAllEnemies)
    {
        return Results.Json(
            new { error = "debug-enemy-projection-disabled" },
            statusCode: StatusCodes.Status403Forbidden);
    }
    try
    {
        return Results.Ok(await coordinator.ProjectTacticalWorkerAsync(
            new CampaignId(parsedCampaign),
            parsedClient.ToString("N"),
            sessions.SnapshotForClient(campaignId, parsedClient.ToString("N"))?.PlayerCoalitionId,
            debugRevealAllEnemies ?? false,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/player-sessions", async (
    PlayerSessionIssueRequest request,
    CampaignPlayerSessionRegistry playerSessions,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(request.CampaignId, "N", out var campaignId)
        || !Guid.TryParseExact(request.CoalitionId, "N", out var coalitionId))
    {
        return Results.BadRequest(new { error = "invalid-campaign-or-coalition-id" });
    }
    try
    {
        await coordinator.ProjectCoalitionAsync(
            new CampaignId(campaignId),
            new CoalitionId(coalitionId),
            cancellationToken);
        return Results.Ok(playerSessions.Issue(
            campaignId.ToString("N"),
            request.PlayerId,
            coalitionId.ToString("N")));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/player-sessions/reconnect", (
    PlayerSessionReconnectRequest request,
    CampaignPlayerSessionRegistry playerSessions) =>
{
    try
    {
        return Results.Ok(playerSessions.Reconnect(request.ReconnectToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/campaigns/{campaignId}/player-commands", async (
    string campaignId,
    PlayerCommandRequest request,
    CampaignCoordinator coordinator,
    CampaignPlayerSessionRegistry playerSessions,
    CampaignPlayerCommandSequencer commands,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed)
        || !Guid.TryParseExact(request.CoalitionId, "N", out var parsedCoalition))
    {
        return Results.BadRequest(new { error = "invalid-campaign-or-coalition-id" });
    }
    try
    {
        var normalizedCampaignId = parsed.ToString("N");
        var normalizedRequest = request with { CoalitionId = parsedCoalition.ToString("N") };
        var session = playerSessions.Require(
            normalizedRequest.SessionId,
            normalizedCampaignId,
            normalizedRequest.CoalitionId,
            normalizedRequest.Generation);
        var timeQuanta = await coordinator.ReadTimeQuantaAsync(new CampaignId(parsed), cancellationToken);
        return Results.Ok(commands.Submit(normalizedCampaignId, timeQuanta, session, normalizedRequest));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/handoffs", async (
    CampaignHandoffRequest request,
    CampaignCoordinator coordinator,
    CampaignSessionRegistry sessions,
    CancellationToken cancellationToken) =>
{
    var session = sessions.Snapshot();
    if (session is null || !string.Equals(session.CampaignId, request.CampaignId, StringComparison.Ordinal))
    {
        return Results.Conflict(new { error = "companion-session-unavailable-or-backpressured" });
    }
    var ownedRequest = request with { WorkerClientInstanceId = session.ClientInstanceId };
    var assignment = await coordinator.PrepareHandoffAsync(ownedRequest, cancellationToken);
    if (sessions.TryQueueForClient(
        request.CampaignId,
        session.ClientInstanceId,
        new ServiceFrame { Assignment = assignment }))
    {
        return Results.Accepted(value: assignment);
    }
    await coordinator.CancelPreparingHandoffAsync(
        ownedRequest, "session-lost-before-queue", cancellationToken);
    return Results.Conflict(new { error = "companion-session-unavailable-or-backpressured" });
});
app.MapPost("/control/campaigns/{campaignId}/prepare-source", async (
    string campaignId,
    CampaignSourcePreparationRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out _))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Accepted(value: await coordinator.PrepareSourceWorkerAsync(
            request with { CampaignId = campaignId },
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/campaigns/{campaignId}/prepare-shadow", async (
    string campaignId,
    CampaignShadowPreparationRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out _))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Accepted(value: await coordinator.PrepareShadowDestinationAsync(
            request with { CampaignId = campaignId },
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/campaigns/{campaignId}/migrate-tactical-island", async (
    string campaignId,
    CampaignIslandMigrationRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out _))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Accepted(value: await coordinator.MigrateTacticalIslandAsync(
            request with { CampaignId = campaignId },
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/handoffs/cancel", async (
    CampaignHandoffRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    await coordinator.CancelPreparingHandoffAsync(request, "operator-cancel", cancellationToken);
    return Results.Accepted();
});
app.MapPost("/control/campaigns/{campaignId}/advance/{target:long}", async (
    string campaignId,
    long target,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.AdvanceAsync(
            new CampaignId(parsed), new CampaignTime(target), cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/campaigns/{campaignId}/drain", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.DrainAsync(new CampaignId(parsed), cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/campaigns/{campaignId}/pause", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.PauseAsync(new CampaignId(parsed), cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/campaigns/{campaignId}/resume", async (
    string campaignId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.ResumeAsync(new CampaignId(parsed), cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/backups", async (
    CampaignBackupRequest request,
    SqliteCampaignJournal store,
    CampaignHostPaths paths,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await CampaignBackupHttp.CreateAsync(store, paths, request, cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapGet("/control/backups", async (CampaignHostPaths paths, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await CampaignBackupHttp.ListAsync(paths, cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/restores", async (
    CampaignRestoreRequest request,
    CampaignHostPaths paths,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await CampaignBackupHttp.RestoreAsync(paths, request, cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/campaigns/{campaignId}/schedule", async (
    string campaignId,
    CampaignSchedulerRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed)
        ? Results.Ok(await coordinator.ScheduleFidelityAsync(
            new CampaignId(parsed), request, cancellationToken))
        : Results.BadRequest(new { error = "invalid-campaign-id" }));
app.MapPost("/control/campaigns/{campaignId}/tactical-workers/{clientInstanceId}/recover-missing", async (
    string campaignId,
    string clientInstanceId,
    CampaignTacticalAssignmentRecoveryRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }

    try
    {
        return Results.Ok(await coordinator.RecoverMissingTacticalAssignmentsAsync(
            new CampaignId(parsed),
            clientInstanceId,
            request,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPost("/control/quiesce", async (
    CampaignQuiesceRequest request,
    CampaignCoordinator coordinator,
    CampaignSessionRegistry sessions,
    CancellationToken cancellationToken) =>
{
    var quiesced = await coordinator.BeginQuiesceAsync(request, cancellationToken);
    var queued = string.IsNullOrWhiteSpace(quiesced.WorkerClientInstanceId)
        ? sessions.TryQueueForCampaign(request.CampaignId, new ServiceFrame { Fence = quiesced.Fence })
        : sessions.TryQueueForClient(
            request.CampaignId,
            quiesced.WorkerClientInstanceId,
            new ServiceFrame { Fence = quiesced.Fence });
    return queued
        ? Results.Accepted(value: quiesced.Fence)
        : Results.Conflict(new { error = "companion-session-unavailable-or-backpressured" });
});
app.MapPost("/control/campaigns/{campaignId}/resolve-expired/{target:long}", async (
    string campaignId,
    long target,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed)
        ? Results.Ok(await coordinator.ResolveExpiredAssignmentsAsync(
            new CampaignId(parsed), new CampaignTime(target), cancellationToken))
        : Results.BadRequest(new { error = "invalid-campaign-id" }));
app.MapGet("/content/{sha256}", async (
    string sha256,
    SqliteCampaignJournal store,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.File(await store.ReadBlobAsync(sha256, cancellationToken), "application/octet-stream");
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.NotFound(new { error = exception.Code, message = exception.Message });
    }
});
app.MapPut("/content/{sha256}", async (
    string sha256,
    HttpRequest request,
    SqliteCampaignJournal store,
    CancellationToken cancellationToken) =>
{
    if (request.ContentLength is < 0 or > 16 * 1024 * 1024)
    {
        return Results.BadRequest(new { error = "invalid-content-length" });
    }
    await using var memory = new MemoryStream();
    await request.Body.CopyToAsync(memory, cancellationToken);
    if (memory.Length > 16 * 1024 * 1024)
    {
        return Results.BadRequest(new { error = "content-too-large" });
    }
    var blob = await store.PutUnreferencedBlobAsync(memory.ToArray(), cancellationToken);
    return string.Equals(blob.Sha256, sha256, StringComparison.Ordinal)
        ? Results.Ok(blob)
        : Results.BadRequest(new { error = "content-hash-mismatch", actualSha256 = blob.Sha256 });
});
app.MapPost("/control/assignments", (TacticalAssignment assignment, CampaignSessionRegistry sessions) =>
    sessions.TryQueue(new ServiceFrame { Assignment = assignment })
        ? Results.Accepted()
        : Results.Conflict(new { error = "companion-session-unavailable-or-backpressured" }));
app.MapPost("/control/commands", (OrderedTacticalCommand command, CampaignSessionRegistry sessions) =>
    sessions.TryQueue(new ServiceFrame { Command = command })
        ? Results.Accepted()
        : Results.Conflict(new { error = "companion-session-unavailable-or-backpressured" }));
app.MapPost("/control/fences", (AuthorityFence fence, CampaignSessionRegistry sessions) =>
    sessions.TryQueue(new ServiceFrame { Fence = fence })
        ? Results.Accepted()
        : Results.Conflict(new { error = "companion-session-unavailable-or-backpressured" }));

app.Run();

static async Task<IResult> SetPlayerPauseAsync(
    string campaignId,
    bool paused,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.SetPlayerPausedAsync(
            new CampaignId(parsed),
            paused,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
}

static async Task<IResult> IssuePlayerOrderAsync(
    string campaignId,
    string groupId,
    string? destinationRegionId,
    long expectedGroupRevision,
    string idempotencyKey,
    bool holdPosition,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.IssuePlayerOrderAsync(
            new CampaignId(parsed),
            groupId,
            destinationRegionId,
            expectedGroupRevision,
            idempotencyKey,
            holdPosition,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        var statusCode = exception.Code == "player-group-forbidden"
            ? StatusCodes.Status403Forbidden
            : exception.Code is "player-group-missing" or "player-destination-missing"
                ? StatusCodes.Status404NotFound
                : exception.Code.StartsWith("invalid-", StringComparison.Ordinal)
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status409Conflict;
        return Results.Json(
            new { error = exception.Code, message = exception.Message },
            statusCode: statusCode);
    }
}

static async Task<IResult> InspectPlayerTacticalEntryAsync(
    string campaignId,
    string groupId,
    string? entityId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.InspectPlayerTacticalEntryAsync(
            new CampaignId(parsed),
            groupId,
            entityId,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return PlayerConflictResult(exception);
    }
}

static async Task<IResult> RequestPlayerTacticalEntryAsync(
    string campaignId,
    string groupId,
    PlayerTacticalEntryRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Accepted(value: await coordinator.RequestPlayerTacticalEntryAsync(
            new CampaignId(parsed),
            groupId,
            request.ExpectedGroupRevision,
            request.IdempotencyKey,
            request.EntityId,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return PlayerConflictResult(exception);
    }
}

static async Task<IResult> InspectPlayerTacticalExitAsync(
    string campaignId,
    string groupId,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Ok(await coordinator.InspectPlayerTacticalExitAsync(
            new CampaignId(parsed),
            groupId,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return PlayerConflictResult(exception);
    }
}

static async Task<IResult> RequestPlayerTacticalExitAsync(
    string campaignId,
    string groupId,
    PlayerTacticalExitRequest request,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken)
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }
    try
    {
        return Results.Accepted(value: await coordinator.RequestPlayerTacticalExitAsync(
            new CampaignId(parsed),
            groupId,
            request.ExpectedGroupRevision,
            request.IdempotencyKey,
            cancellationToken));
    }
    catch (CampaignStoreConflict exception)
    {
        return PlayerConflictResult(exception);
    }
}

static IResult PlayerConflictResult(CampaignStoreConflict exception)
{
    var statusCode = exception.Code == "player-group-forbidden"
        ? StatusCodes.Status403Forbidden
        : exception.Code == "player-group-missing"
            ? StatusCodes.Status404NotFound
            : exception.Code.StartsWith("invalid-", StringComparison.Ordinal)
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict;
    return Results.Json(
        new { error = exception.Code, message = exception.Message },
        statusCode: statusCode);
}

static string? FindRepositoryRoot(string startPath)
{
    for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
            || File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            return directory.FullName;
        }
    }
    return null;
}

static Uri RequireLoopbackUri(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || uri.Scheme != Uri.UriSchemeHttp
        || !IPAddress.TryParse(uri.Host, out var address)
        || !IPAddress.IsLoopback(address)
        || uri.Port <= 0)
    {
        throw new InvalidOperationException($"Endpoint '{value}' must be an explicit loopback HTTP URL.");
    }
    return uri;
}

public sealed record CampaignCreateRequest(string CampaignId, string Name, long Seed, string CatalogFingerprint);

public sealed record PlayerCampaignCreateRequest(string Name, string Coalition = "Blue");

public sealed record PlayerMoveOrderRequest(
    string DestinationRegionId,
    long ExpectedGroupRevision,
    string IdempotencyKey);

public sealed record PlayerCancelOrderRequest(
    long ExpectedGroupRevision,
    string IdempotencyKey);

public sealed record PlayerTacticalEntryRequest(
    long ExpectedGroupRevision,
    string IdempotencyKey,
    string? EntityId = null);

public sealed record PlayerTacticalExitRequest(
    long ExpectedGroupRevision,
    string IdempotencyKey);
