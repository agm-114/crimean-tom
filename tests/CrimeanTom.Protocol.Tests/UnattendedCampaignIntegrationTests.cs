using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace CrimeanTom.Protocol.Tests;

public sealed class UnattendedCampaignIntegrationTests
{
    [Fact]
    public async Task ServerAdvancesCampaignWithoutClientOrSeaPower()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
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
        try
        {
            campaignService = StartCampaignService(
                repositoryRoot,
                runDirectory,
                campaignGrpcUrl,
                campaignHttpUrl);
            var token = await WaitForCampaignServiceAsync(
                runDirectory,
                campaignHttpUrl,
                testTimeout.Token);
            using var campaignClient = CreateBearerClient(campaignHttpUrl, token);
            using var created = await campaignClient.PostAsJsonAsync(
                "/control/campaigns",
                new
                {
                    campaignId,
                    name = "Unattended kernel analogue",
                    seed = 19L,
                    catalogFingerprint = new string('b', 64),
                },
                testTimeout.Token);
            created.EnsureSuccessStatusCode();
            using var bootstrapped = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/bootstrap",
                content: null,
                testTimeout.Token);
            bootstrapped.EnsureSuccessStatusCode();

            using var before = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            before.EnsureSuccessStatusCode();
            using var beforeBody = JsonDocument.Parse(
                await before.Content.ReadAsStringAsync(testTimeout.Token));
            var beforeTime = beforeBody.RootElement.GetProperty("timeQuanta").GetInt64();

            await WaitUntilAsync(
                async () =>
                {
                    using var snapshot = await campaignClient.GetAsync(
                        $"/campaigns/{campaignId}",
                        testTimeout.Token);
                    if (!snapshot.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    using var body = JsonDocument.Parse(
                        await snapshot.Content.ReadAsStringAsync(testTimeout.Token));
                    return body.RootElement.GetProperty("timeQuanta").GetInt64() > beforeTime;
                },
                testTimeout.Token);

            using var after = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            after.EnsureSuccessStatusCode();
            using var afterBody = JsonDocument.Parse(
                await after.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.True(afterBody.RootElement.GetProperty("timeQuanta").GetInt64() > beforeTime);
            Assert.Contains(
                afterBody.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("revision").GetInt64() > 1
                    || item.GetProperty("headingMilliDegrees").GetInt32() != 0
                    || item.GetProperty("fuelMilliunits").GetInt64() < 1_000_000);

            using var paused = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/pause",
                content: null,
                testTimeout.Token);
            paused.EnsureSuccessStatusCode();
            using var pausedAtSnapshot = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            pausedAtSnapshot.EnsureSuccessStatusCode();
            using var pausedAtBody = JsonDocument.Parse(
                await pausedAtSnapshot.Content.ReadAsStringAsync(testTimeout.Token));
            var pausedTime = pausedAtBody.RootElement.GetProperty("timeQuanta").GetInt64();
            await Task.Delay(TimeSpan.FromMilliseconds(750), testTimeout.Token);
            using var pausedSnapshot = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            pausedSnapshot.EnsureSuccessStatusCode();
            using var pausedBody = JsonDocument.Parse(
                await pausedSnapshot.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(pausedTime, pausedBody.RootElement.GetProperty("timeQuanta").GetInt64());

            using var resumed = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/resume",
                content: null,
                testTimeout.Token);
            resumed.EnsureSuccessStatusCode();
            await WaitUntilAsync(
                async () =>
                {
                    using var snapshot = await campaignClient.GetAsync(
                        $"/campaigns/{campaignId}",
                        testTimeout.Token);
                    if (!snapshot.IsSuccessStatusCode)
                    {
                        return false;
                    }

                    using var body = JsonDocument.Parse(
                        await snapshot.Content.ReadAsStringAsync(testTimeout.Token));
                    return body.RootElement.GetProperty("timeQuanta").GetInt64() > pausedTime;
                },
                testTimeout.Token);
        }
        finally
        {
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

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        CancellationToken cancellationToken)
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
        start.Environment["CRIMEAN_TOM_UNATTENDED_SIMULATION"] = "1";
        start.Environment["CRIMEAN_TOM_UNATTENDED_TICK_WALL_MS"] = "200";
        start.Environment["CRIMEAN_TOM_UNATTENDED_QUANTA_PER_TICK"] = "10";
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
        var directory = Path.Combine(
            Path.GetTempPath(),
            "CrimeanTom.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
