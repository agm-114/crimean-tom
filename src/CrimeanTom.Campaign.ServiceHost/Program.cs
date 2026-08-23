using System.Net;
using System.Text.Json.Serialization;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.ServiceHost;
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
var monitorSession = new CampaignMonitorSession();
var journal = new SqliteCampaignJournal(
    Path.Combine(runDirectory, "campaign.db"),
    Path.Combine(runDirectory, "blobs"));
await journal.InitializeAsync();

builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 256 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(token);
builder.Services.AddSingleton(monitorSession);
builder.Services.AddSingleton(journal);
builder.Services.AddSingleton(new CampaignHostPaths(runDirectory, Path.Combine(runDirectory, "backups")));
builder.Services.AddSingleton<CampaignSessionRegistry>();
builder.Services.AddSingleton<CampaignPlayerSessionRegistry>();
builder.Services.AddSingleton<CampaignPlayerCommandSequencer>();
builder.Services.AddSingleton<CampaignPerformanceTelemetry>();
builder.Services.AddSingleton(new CampaignInboundSpool(Path.Combine(runDirectory, "inbound-spool")));
builder.Services.AddSingleton<CampaignCoordinator>();
builder.Services.AddSingleton(new CampaignOperationalWorkerOptions(grpcUri.ToString()));
builder.Services.AddSingleton<CampaignOperationalWorkerSupervisor>();
builder.Services.AddHostedService<CampaignOperationalWorkerHostedService>();
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
                Path = "/",
                SameSite = SameSiteMode.Strict,
                Secure = false,
            });
        var sessions = context.RequestServices.GetRequiredService<CampaignSessionRegistry>();
        var activeCampaignId = CampaignMonitorRouting.ResolveActiveCampaignId(
            await journal.ListCampaignsAsync(context.RequestAborted),
            sessions.Snapshots());
        var requestedCampaignId = context.Request.Query["campaign"].ToString();
        if (activeCampaignId is not null
            && (!CampaignMonitorRouting.IsMonitorEntryPath(requestPath)
                || !CampaignMonitorRouting.MatchesActiveCampaign(requestedCampaignId, activeCampaignId)))
        {
            context.Response.Redirect(CampaignMonitorRouting.MonitorLocation(activeCampaignId));
            return;
        }
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
    if (context.Request.ContentType?.StartsWith("application/grpc", StringComparison.Ordinal) == true)
    {
        await next(context);
        return;
    }
    var supplied = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    var bearerAuthenticated = supplied.StartsWith(prefix, StringComparison.Ordinal)
        && token.IsMatch(supplied[prefix.Length..]);
    var monitorAuthenticated = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && context.Request.Cookies.TryGetValue(CampaignMonitorSession.CookieName, out var monitorCookie)
        && monitorSession.IsMatch(monitorCookie);
    if (!bearerAuthenticated && !monitorAuthenticated)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "invalid-campaign-service-token" });
        return;
    }
    await next(context);
});

app.MapGrpcService<CampaignCompanionGrpcService>();
app.MapGet("/health", (CampaignSessionRegistry sessions) => Results.Ok(new
{
    status = "ok",
    protocolVersion = 1,
    grpcUrl = grpcUri.ToString(),
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
app.MapPost("/control/campaigns/{campaignId}/bootstrap/fulda-gap/{forceMultiplier:int}", async (
    string campaignId,
    int forceMultiplier,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed) && forceMultiplier is >= 1 and <= 100
        ? Results.Ok(await coordinator.BootstrapFuldaGapAsync(
            new CampaignId(parsed), cancellationToken, forceMultiplier))
        : Results.BadRequest(new { error = "invalid-campaign-id-or-force-multiplier" }));
app.MapPost("/control/campaigns/{campaignId}/bootstrap/fulda-gap/subset/{entityCount:int}", async (
    string campaignId,
    int entityCount,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed) && entityCount is >= 1 and <= 4_096
        ? Results.Ok(await coordinator.BootstrapFuldaGapAsync(
            new CampaignId(parsed), cancellationToken, forceMultiplier: 1, maximumEntities: entityCount))
        : Results.BadRequest(new { error = "invalid-campaign-id-or-entity-count" }));
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
app.MapPost("/control/campaigns/{campaignId}/sim-workers", async (
    string campaignId,
    CampaignSimWorkerSpawnRequest request,
    SqliteCampaignJournal store,
    CampaignOperationalWorkerSupervisor supervisor,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }

    try
    {
        await store.ReadMetadataAsync(new CampaignId(parsed), cancellationToken);
        var spawned = supervisor.Spawn(new CampaignId(parsed), request);
        return Results.Created($"/control/campaigns/{campaignId}/sim-workers/{spawned.WorkerId}", spawned);
    }
    catch (CampaignStoreConflict exception)
    {
        return Results.Conflict(new { error = exception.Code, message = exception.Message });
    }
});
app.MapGet("/control/campaigns/{campaignId}/sim-workers", (
    string campaignId,
    CampaignOperationalWorkerSupervisor supervisor) =>
    Guid.TryParseExact(campaignId, "N", out var parsed)
        ? Results.Ok(supervisor.List(new CampaignId(parsed)))
        : Results.BadRequest(new { error = "invalid-campaign-id" }));
app.MapDelete("/control/campaigns/{campaignId}/sim-workers/{workerId}", async (
    string campaignId,
    string workerId,
    CampaignOperationalWorkerSupervisor supervisor,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParseExact(campaignId, "N", out var parsed))
    {
        return Results.BadRequest(new { error = "invalid-campaign-id" });
    }

    try
    {
        return Results.Ok(await supervisor.StopAsync(new CampaignId(parsed), workerId, cancellationToken));
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
