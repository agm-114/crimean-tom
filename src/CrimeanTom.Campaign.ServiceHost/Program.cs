using System.Net;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.ServiceHost;
using CrimeanTom.Campaign.Transport.V1;

using Microsoft.AspNetCore.Server.Kestrel.Core;

var repositoryWebRoot = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    "src",
    "CrimeanTom.Campaign.ServiceHost",
    "wwwroot"));
var deployedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Directory.Exists(repositoryWebRoot) ? repositoryWebRoot : deployedWebRoot,
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
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".run", "campaign-service"));
var token = CampaignServiceToken.LoadOrCreate(Path.Combine(runDirectory, "service.token"));
var journal = new SqliteCampaignJournal(
    Path.Combine(runDirectory, "campaign.db"),
    Path.Combine(runDirectory, "blobs"));
await journal.InitializeAsync();

builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = 256 * 1024);
builder.Services.AddSingleton(token);
builder.Services.AddSingleton(journal);
builder.Services.AddSingleton<CampaignSessionRegistry>();
builder.Services.AddSingleton(new CampaignInboundSpool(Path.Combine(runDirectory, "inbound-spool")));
builder.Services.AddSingleton<CampaignCoordinator>();
var app = builder.Build();

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
    if (!supplied.StartsWith(prefix, StringComparison.Ordinal) || !token.IsMatch(supplied[prefix.Length..]))
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
}));
app.MapGet("/durability-policy", () => Results.Ok(CampaignDurabilityPolicy.Current));
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
app.MapPost("/control/handoffs", async (
    CampaignHandoffRequest request,
    CampaignCoordinator coordinator,
    CampaignSessionRegistry sessions,
    CancellationToken cancellationToken) =>
{
    var assignment = await coordinator.PrepareHandoffAsync(request, cancellationToken);
    return sessions.TryQueueForCampaign(request.CampaignId, new ServiceFrame { Assignment = assignment })
        ? Results.Accepted(value: assignment)
        : Results.Conflict(new { error = "companion-session-unavailable-or-backpressured" });
});
app.MapPost("/control/campaigns/{campaignId}/advance/{target:long}", async (
    string campaignId,
    long target,
    CampaignCoordinator coordinator,
    CancellationToken cancellationToken) =>
    Guid.TryParseExact(campaignId, "N", out var parsed)
        ? Results.Ok(await coordinator.AdvanceAsync(
            new CampaignId(parsed), new CampaignTime(target), cancellationToken))
        : Results.BadRequest(new { error = "invalid-campaign-id" }));
app.MapPost("/control/quiesce", async (
    CampaignQuiesceRequest request,
    CampaignCoordinator coordinator,
    CampaignSessionRegistry sessions,
    CancellationToken cancellationToken) =>
{
    var fence = await coordinator.BeginQuiesceAsync(request, cancellationToken);
    return sessions.TryQueueForCampaign(request.CampaignId, new ServiceFrame { Fence = fence })
        ? Results.Accepted(value: fence)
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
