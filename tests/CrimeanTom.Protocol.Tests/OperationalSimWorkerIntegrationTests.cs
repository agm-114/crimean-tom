using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace CrimeanTom.Protocol.Tests;

public sealed class OperationalSimWorkerIntegrationTests
{
    [Fact]
    public async Task CampaignSpawnsOperationalWorkersAndAssignsNonPlayerIslands()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var repositoryRoot = FindRepositoryRoot();
        var runDirectory = CreateTemporaryDirectory();
        var campaignGrpcPort = GetUnusedLoopbackPort();
        var campaignHttpPort = GetUnusedLoopbackPort();
        while (campaignHttpPort == campaignGrpcPort)
        {
            campaignHttpPort = GetUnusedLoopbackPort();
        }

        var campaignGrpcUrl = $"http://127.0.0.1:{campaignGrpcPort}";
        var campaignHttpUrl = $"http://127.0.0.1:{campaignHttpPort}";
        var campaignId = Guid.NewGuid().ToString("N");
        Process? campaignService = null;
        var workerPids = new List<int>();

        try
        {
            campaignService = StartCampaignService(repositoryRoot, runDirectory, campaignGrpcUrl, campaignHttpUrl);
            var token = await WaitForCampaignServiceAsync(runDirectory, campaignHttpUrl, testTimeout.Token);
            using var campaignClient = CreateBearerClient(campaignHttpUrl, token);

            using var created = await campaignClient.PostAsJsonAsync(
                "/control/campaigns",
                new
                {
                    campaignId,
                    name = "Track A operational worker",
                    seed = 17L,
                    catalogFingerprint = new string('a', 64),
                },
                testTimeout.Token);
            created.EnsureSuccessStatusCode();
            using var bootstrapped = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/bootstrap",
                content: null,
                testTimeout.Token);
            bootstrapped.EnsureSuccessStatusCode();

            using var trackB = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/sim-workers",
                new { workerId = "farm-1", kind = "sea-power-farm", capacity = 1 },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, trackB.StatusCode);

            var small = await SpawnWorkerAsync(campaignClient, campaignId, "op-small", capacity: 1, testTimeout.Token);
            workerPids.Add(small.Pid);
            await WaitForOperationalSessionAsync(
                campaignClient,
                small.ClientInstanceId,
                testTimeout.Token);

            using var overlap = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/sim-workers",
                new { workerId = "op-small", capacity = 1 },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);

            await ScheduleAsync(campaignClient, campaignId, testTimeout.Token);
            var firstOwned = await WaitForOperationalAssignmentsAsync(
                campaignClient,
                campaignId,
                small.ClientInstanceId,
                minimumCount: 1,
                testTimeout.Token);
            Assert.All(firstOwned, item => Assert.StartsWith("operational:", item.BatchId, StringComparison.Ordinal));

            var large = await SpawnWorkerAsync(campaignClient, campaignId, "op-large", capacity: 32, testTimeout.Token);
            workerPids.Add(large.Pid);
            await WaitForOperationalSessionAsync(campaignClient, large.ClientInstanceId, testTimeout.Token);
            await ScheduleAsync(campaignClient, campaignId, testTimeout.Token);
            await WaitForOperationalAssignmentsAsync(
                campaignClient,
                campaignId,
                large.ClientInstanceId,
                minimumCount: 1,
                testTimeout.Token);

            using var afterSecond = await campaignClient.GetAsync($"/campaigns/{campaignId}", testTimeout.Token);
            afterSecond.EnsureSuccessStatusCode();
            using var afterSecondBody = JsonDocument.Parse(await afterSecond.Content.ReadAsStringAsync(testTimeout.Token));
            var stickyIds = AssignmentsOn(afterSecondBody.RootElement, small.ClientInstanceId)
                .Select(item => item.EntityId)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(firstOwned.Select(item => item.EntityId).OrderBy(item => item, StringComparer.Ordinal), stickyIds);

            using var beforeAdvance = await campaignClient.GetAsync($"/campaigns/{campaignId}", testTimeout.Token);
            beforeAdvance.EnsureSuccessStatusCode();
            using var beforeBody = JsonDocument.Parse(await beforeAdvance.Content.ReadAsStringAsync(testTimeout.Token));
            var beforeTime = beforeBody.RootElement.GetProperty("timeQuanta").GetInt64();
            var beforeEntities = beforeBody.RootElement.GetProperty("entities").EnumerateArray()
                .ToDictionary(
                    item => item.GetProperty("entityId").GetString()!,
                    item => (
                        item.GetProperty("revision").GetInt64(),
                        item.GetProperty("fuelMilliunits").GetInt64(),
                        item.GetProperty("latitudeMicrodegrees").GetInt32()));

            using var advanced = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/advance/{beforeTime + 200}",
                content: null,
                testTimeout.Token);
            advanced.EnsureSuccessStatusCode();
            using var afterAdvance = await campaignClient.GetAsync($"/campaigns/{campaignId}", testTimeout.Token);
            afterAdvance.EnsureSuccessStatusCode();
            using var afterBody = JsonDocument.Parse(await afterAdvance.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.True(afterBody.RootElement.GetProperty("timeQuanta").GetInt64() > beforeTime);
            Assert.Contains(
                afterBody.RootElement.GetProperty("entities").EnumerateArray(),
                item =>
                {
                    var id = item.GetProperty("entityId").GetString()!;
                    var prior = beforeEntities[id];
                    return item.GetProperty("revision").GetInt64() != prior.Item1
                        || item.GetProperty("fuelMilliunits").GetInt64() != prior.Item2
                        || item.GetProperty("latitudeMicrodegrees").GetInt32() != prior.Item3;
                });
            Assert.Contains(
                afterBody.RootElement.GetProperty("assignments").EnumerateArray(),
                item => item.GetProperty("lifecycle").GetString() == "Active"
                    && (item.GetProperty("batchId").GetString() ?? string.Empty)
                        .StartsWith("operational:", StringComparison.Ordinal));

            using var deleted = await campaignClient.DeleteAsync(
                $"/control/campaigns/{campaignId}/sim-workers/op-small",
                testTimeout.Token);
            deleted.EnsureSuccessStatusCode();
            await WaitUntilAsync(
                () => Task.FromResult(!IsPidAlive(small.Pid)),
                testTimeout.Token);
            await ScheduleAsync(campaignClient, campaignId, testTimeout.Token);
            using var drained = await campaignClient.GetAsync($"/campaigns/{campaignId}", testTimeout.Token);
            drained.EnsureSuccessStatusCode();
            using var drainedBody = JsonDocument.Parse(await drained.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.DoesNotContain(
                drainedBody.RootElement.GetProperty("assignments").EnumerateArray(),
                item => item.GetProperty("workerClientInstanceId").GetString() == small.ClientInstanceId
                    && item.GetProperty("lifecycle").GetString() is not ("Released" or null));

            var largePid = large.Pid;
            campaignService.Kill();
            campaignService.WaitForExit(5_000);
            campaignService.Dispose();
            campaignService = null;
            await WaitUntilAsync(
                () => Task.FromResult(!IsPidAlive(largePid)),
                testTimeout.Token);

            campaignService = StartCampaignService(repositoryRoot, runDirectory, campaignGrpcUrl, campaignHttpUrl);
            _ = await WaitForCampaignServiceAsync(runDirectory, campaignHttpUrl, testTimeout.Token);
            using var restartedClient = CreateBearerClient(
                campaignHttpUrl,
                (await File.ReadAllTextAsync(Path.Combine(runDirectory, "service.token"), testTimeout.Token)).Trim());
            using var leftover = await restartedClient.GetAsync(
                $"/control/campaigns/{campaignId}/sim-workers",
                testTimeout.Token);
            leftover.EnsureSuccessStatusCode();
            using var leftoverBody = JsonDocument.Parse(await leftover.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.DoesNotContain(
                leftoverBody.RootElement.EnumerateArray(),
                item => item.GetProperty("alive").GetBoolean());
        }
        finally
        {
            foreach (var pid in workerPids)
            {
                TryKill(pid);
            }

            if (campaignService is not null)
            {
                try
                {
                    if (!campaignService.HasExited)
                    {
                        campaignService.Kill(entireProcessTree: true);
                    }

                    campaignService.Dispose();
                }
                catch (InvalidOperationException)
                {
                }
            }

            try
            {
                Directory.Delete(runDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<SpawnedWorker> SpawnWorkerAsync(
        HttpClient client,
        string campaignId,
        string workerId,
        int capacity,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/sim-workers",
            new { workerId, capacity, reserve = 0, kind = "operational" },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new SpawnedWorker(
            body.RootElement.GetProperty("workerId").GetString()!,
            body.RootElement.GetProperty("clientInstanceId").GetString()!,
            body.RootElement.GetProperty("pid").GetInt32());
    }

    private static async Task ScheduleAsync(HttpClient client, string campaignId, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/schedule",
            new
            {
                observedEntityIds = Array.Empty<string>(),
                controlledEntityIds = Array.Empty<string>(),
                health = "Healthy",
                eligibility = "Eligible",
                tacticalEntityCapacity = 4,
                tacticalReserveCapacity = 1,
                backlogDepth = 0,
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task WaitForOperationalSessionAsync(
        HttpClient client,
        string clientInstanceId,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(async () =>
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.GetProperty("sessions").EnumerateArray().Any(session =>
                session.GetProperty("clientInstanceId").GetString() == clientInstanceId
                && session.GetProperty("workerKind").GetString() == "operational"
                && session.GetProperty("playerSessionBound").GetBoolean() == false
                && session.GetProperty("receivedFrameCount").GetInt64() > 0);
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<OwnedAssignment>> WaitForOperationalAssignmentsAsync(
        HttpClient client,
        string campaignId,
        string clientInstanceId,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OwnedAssignment> owned = [];
        await WaitUntilAsync(async () =>
        {
            using var response = await client.GetAsync($"/campaigns/{campaignId}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            owned = AssignmentsOn(document.RootElement, clientInstanceId);
            return owned.Count >= minimumCount
                && owned.Any(item => item.Lifecycle is "Prepared" or "Active");
        }, cancellationToken);
        return owned;
    }

    private static IReadOnlyList<OwnedAssignment> AssignmentsOn(JsonElement campaign, string clientInstanceId) =>
        campaign.GetProperty("assignments").EnumerateArray()
            .Where(item => item.GetProperty("workerClientInstanceId").GetString() == clientInstanceId
                && item.GetProperty("lifecycle").GetString() is not ("Released" or null))
            .Select(item => new OwnedAssignment(
                item.GetProperty("entityId").GetString()!,
                item.GetProperty("batchId").GetString() ?? string.Empty,
                item.GetProperty("lifecycle").GetString() ?? string.Empty))
            .ToArray();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition())
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    private static Process StartCampaignService(
        string repositoryRoot,
        string runDirectory,
        string grpcUrl,
        string httpUrl)
    {
        var normalHostPath = Path.Combine(
            repositoryRoot,
            "src",
            "CrimeanTom.Campaign.ServiceHost",
            "bin",
            "Debug",
            "net8.0",
            "CrimeanTom.Campaign.ServiceHost.dll");
        var artifactsHostPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "CrimeanTom.Campaign.ServiceHost",
            "debug",
            "CrimeanTom.Campaign.ServiceHost.dll"));
        var hostPath = File.Exists(artifactsHostPath) ? artifactsHostPath : normalHostPath;
        Assert.True(File.Exists(hostPath), $"Campaign service host was not built at {hostPath}.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(hostPath);
        start.Environment["CRIMEAN_TOM_CAMPAIGN_RUN_DIRECTORY"] = runDirectory;
        start.Environment["CRIMEAN_TOM_CAMPAIGN_GRPC_URL"] = grpcUrl;
        start.Environment["CRIMEAN_TOM_CAMPAIGN_HTTP_URL"] = httpUrl;
        Directory.CreateDirectory(runDirectory);
        var process = new Process { StartInfo = start };
        process.Start();
        return process;
    }

    private static async Task<string> WaitForCampaignServiceAsync(
        string runDirectory,
        string httpUrl,
        CancellationToken cancellationToken)
    {
        var tokenPath = Path.Combine(runDirectory, "service.token");
        string token;
        while (true)
        {
            try
            {
                token = File.Exists(tokenPath)
                    ? (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim()
                    : string.Empty;
                if (token.Length > 0)
                {
                    break;
                }
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(25, cancellationToken);
        }

        using var client = CreateBearerClient(httpUrl, token);
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return token;
                }
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static HttpClient CreateBearerClient(string baseUrl, string token)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                && File.Exists(Path.Combine(directory.FullName, "scripts", "crimean-tom-control.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Crimean Tom repository root.");
    }

    private static int GetUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CrimeanTom.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record SpawnedWorker(string WorkerId, string ClientInstanceId, int Pid);

    private sealed record OwnedAssignment(string EntityId, string BatchId, string Lifecycle);
}
