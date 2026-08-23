using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CrimeanTom.Campaign.Transport.V1;
using CrimeanTom.LoadAcceleration;
using CrimeanTom.Protocol;

using Google.Protobuf;

using Grpc.Core;
using Grpc.Net.Client;

namespace CrimeanTom.Protocol.Tests;

public sealed class CompanionControlIntegrationTests
{
    private const string MigrationCooldownEnvironmentValue = "100000";
    private static readonly IReadOnlyDictionary<string, string> MigrationCooldownEnvironment =
        new Dictionary<string, string>
        {
            ["CRIMEAN_TOM_MIGRATION_COOLDOWN_QUANTA"] = MigrationCooldownEnvironmentValue,
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task RealHostAuthenticatesAndBridgesReadStateAndTypedUiAutomation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(600));
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "crimean-tom-control.ps1");
        var runDirectory = CreateTemporaryDirectory();
        var colliderCacheDir = Path.Combine(runDirectory, "collider-cache");
        var controlUrl = $"http://127.0.0.1:{GetUnusedLoopbackPort()}";
        var gamePipeName = $"CrimeanTom.Tests.{Guid.NewGuid():N}";
        var probeSavePath = Path.Combine(runDirectory, "_CrimeanTomPhase1Probe.sav");
        var pidPath = Path.Combine(runDirectory, "companion.pid");
        var tokenPath = Path.Combine(runDirectory, "control.token");
        var stdoutPath = Path.Combine(runDirectory, "companion.stdout.log");
        var stderrPath = Path.Combine(runDirectory, "companion.stderr.log");
        var campaignServiceRunDirectory = Path.Combine(runDirectory, "campaign-service");
        var campaignGrpcPort = GetUnusedLoopbackPort();
        var campaignHttpPort = GetUnusedLoopbackPort();
        while (campaignHttpPort == campaignGrpcPort)
        {
            campaignHttpPort = GetUnusedLoopbackPort();
        }
        var campaignGrpcUrl = $"http://127.0.0.1:{campaignGrpcPort}";
        var campaignHttpUrl = $"http://127.0.0.1:{campaignHttpPort}";
        var campaignId = Guid.Parse("10000000-0000-0000-0000-000000000004").ToString("N");
        Process? campaignService = null;
        const string staleMarker = "stale-artifact-marker";

        await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString(), testTimeout.Token);
        await File.WriteAllTextAsync(tokenPath, $"{staleMarker}-token", testTimeout.Token);
        await File.WriteAllTextAsync(stdoutPath, $"{staleMarker}-stdout", testTimeout.Token);
        await File.WriteAllTextAsync(stderrPath, $"{staleMarker}-stderr", testTimeout.Token);

        try
        {
            campaignService = StartCampaignService(
                repositoryRoot,
                campaignServiceRunDirectory,
                campaignGrpcUrl,
                campaignHttpUrl,
                MigrationCooldownEnvironment);
            var campaignToken = await WaitForCampaignServiceAsync(
                campaignServiceRunDirectory,
                campaignHttpUrl,
                testTimeout.Token);
            using (var anonymousCampaignClient = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(campaignHttpUrl),
                Timeout = TimeSpan.FromSeconds(10),
            })
            {
                using var protectedHealthBeforeMonitor = await anonymousCampaignClient.GetAsync(
                    "/health",
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, protectedHealthBeforeMonitor.StatusCode);
                using var monitorResponse = await anonymousCampaignClient.GetAsync("/monitor/", testTimeout.Token);
                monitorResponse.EnsureSuccessStatusCode();
                var monitorHtml = await monitorResponse.Content.ReadAsStringAsync(testTimeout.Token);
                Assert.Contains("Crimean Tom Campaign Monitor", monitorHtml, StringComparison.Ordinal);
                using var protectedHealth = await anonymousCampaignClient.GetAsync("/health", testTimeout.Token);
                Assert.Equal(HttpStatusCode.OK, protectedHealth.StatusCode);
                using var monitorWrite = await anonymousCampaignClient.PostAsJsonAsync(
                    "/control/campaigns",
                    new
                    {
                        campaignId = Guid.NewGuid().ToString("N"),
                        name = "Monitor cookie must be read-only",
                        seed = 1,
                        catalogFingerprint = "must-not-commit",
                    },
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, monitorWrite.StatusCode);
            }
            using var campaignClient = CreateBearerClient(campaignHttpUrl, campaignToken);
            using var durabilityPolicyResponse = await campaignClient.GetAsync(
                "/durability-policy",
                testTimeout.Token);
            durabilityPolicyResponse.EnsureSuccessStatusCode();
            using var durabilityPolicy = JsonDocument.Parse(
                await durabilityPolicyResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(
                "display-first-provisional",
                durabilityPolicy.RootElement.GetProperty("displayPolicy").GetString());
            Assert.Equal(
                "campaign-journal-commit",
                durabilityPolicy.RootElement.GetProperty("canonicalDurabilityBoundary").GetString());
            Assert.Equal(
                "cold-checkpoint-or-operational; hot-shadow-unclaimed",
                durabilityPolicy.RootElement.GetProperty("failoverClaim").GetString());
            Assert.Equal(
                "never-majority; exact-match-or-unsafe-divergence-or-pending",
                durabilityPolicy.RootElement.GetProperty("replicaHashRule").GetString());
            Assert.Equal(
                "trusted-private-multiplayer; a-player-process-holding-full-opposing-truth-is-trusted",
                durabilityPolicy.RootElement.GetProperty("deploymentTrustModel").GetString());
            Assert.Equal(
                "http-coalition-filtered; signalr-not-canonical-commit",
                durabilityPolicy.RootElement.GetProperty("playerProjectionTransport").GetString());
            Assert.Equal(
                "sqlite-wal-local-blobs; no-docker-no-external-db",
                durabilityPolicy.RootElement.GetProperty("packagedDeploymentProfile").GetString());
            Assert.Equal(120, durabilityPolicy.RootElement.GetProperty("maximumRollbackQuanta").GetInt64());
            Assert.Contains(
                durabilityPolicy.RootElement.GetProperty("provisionalEffectFamilies").EnumerateArray(),
                item => item.GetString() == "audio");
            Assert.Contains(
                durabilityPolicy.RootElement.GetProperty("checkpointCommittedEffectFamilies").EnumerateArray(),
                item => item.GetString() == "destruction");
            using var supportedClaimsResponse = await campaignClient.GetAsync(
                "/supported-claims",
                testTimeout.Token);
            supportedClaimsResponse.EnsureSuccessStatusCode();
            using var supportedClaims = JsonDocument.Parse(
                await supportedClaimsResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(4, supportedClaims.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                durabilityPolicy.RootElement.GetProperty("failoverClaim").GetString(),
                supportedClaims.RootElement.GetProperty("failoverClaim").GetString());
            Assert.Equal(
                "assignment-preparation-and-activation-fail-closed-on-pinned-fingerprint-mismatch",
                supportedClaims.RootElement.GetProperty("compatibilityPolicy").GetString());
            Assert.Contains(
                supportedClaims.RootElement.GetProperty("supported").EnumerateArray(),
                item => item.GetString() == "render-proxy-cannot-receive-simulation-authority");
            Assert.Contains(
                supportedClaims.RootElement.GetProperty("supported").EnumerateArray(),
                item => item.GetString() == "hosted-monitor-local-cookie-read-access-without-operator-identity");
            Assert.Contains(
                supportedClaims.RootElement.GetProperty("supported").EnumerateArray(),
                item => item.GetString()
                    == "heartbeat-renewed-tactical-ownership-while-unrelated-campaign-state-advances");
            Assert.Contains(
                supportedClaims.RootElement.GetProperty("supported").EnumerateArray(),
                item => item.GetString()
                    == "friendly-deployed-knox-render-proxies-without-simulation-authority");
            Assert.DoesNotContain(
                supportedClaims.RootElement.GetProperty("unsupported").EnumerateArray(),
                item => item.GetString()
                    == "human-length-tactical-assignment-under-default-unattended-clock");
            Assert.Contains(
                supportedClaims.RootElement.GetProperty("unsupported").EnumerateArray(),
                item => item.GetString() == "hot-shadow-failover");
            Assert.Contains(
                supportedClaims.RootElement.GetProperty("unsupported").EnumerateArray(),
                item => item.GetString() == "existing-campaign-schema-or-algorithm-upgrade");
            using var createdCampaign = await campaignClient.PostAsJsonAsync(
                "/control/campaigns",
                new
                {
                    campaignId,
                    name = "Phase 4 transport integration",
                    seed = 4004,
                    catalogFingerprint = "integration-catalog",
                },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Created, createdCampaign.StatusCode);
            using var bootstrappedCampaign = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/bootstrap",
                content: null,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.OK, bootstrappedCampaign.StatusCode);
            using var bootstrap = JsonDocument.Parse(
                await bootstrappedCampaign.Content.ReadAsStringAsync(testTimeout.Token));
            var primaryEntityId = bootstrap.RootElement.GetProperty("primaryEntityId").GetString()!;
            var fallbackEntityId = bootstrap.RootElement.GetProperty("fallbackEntityId").GetString()!;
            var combatEntityId = bootstrap.RootElement.GetProperty("combatEntityId").GetString()!;
            var mismatchEntityId = bootstrap.RootElement.GetProperty("mismatchEntityId").GetString()!;
            var crashBeforeCheckpointEntityId = bootstrap.RootElement
                .GetProperty("crashBeforeCheckpointEntityId")
                .GetString()!;
            var batchLeadEntityId = bootstrap.RootElement.GetProperty("batchLeadEntityId").GetString()!;
            var batchWingEntityId = bootstrap.RootElement.GetProperty("batchWingEntityId").GetString()!;
            var unrelatedEntityId = bootstrap.RootElement.GetProperty("unrelatedEntityId").GetString()!;
            var fuldaCampaignId = Guid.Parse("10000000-0000-0000-0000-000000000005").ToString("N");
            using var createdFuldaCampaign = await campaignClient.PostAsJsonAsync(
                "/control/campaigns",
                new
                {
                    campaignId = fuldaCampaignId,
                    name = "Fulda Gap Gone Hot",
                    seed = 19890911,
                    catalogFingerprint = "fulda-gap-integration-catalog",
                },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Created, createdFuldaCampaign.StatusCode);
            using var fuldaBootstrapResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{fuldaCampaignId}/bootstrap/fulda-gap/1",
                content: null,
                testTimeout.Token);
            fuldaBootstrapResponse.EnsureSuccessStatusCode();
            using var fuldaBootstrap = JsonDocument.Parse(
                await fuldaBootstrapResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(1, fuldaBootstrap.RootElement.GetProperty("forceMultiplier").GetInt32());
            Assert.Equal(39, fuldaBootstrap.RootElement.GetProperty("regionCount").GetInt32());
            Assert.Equal(61, fuldaBootstrap.RootElement.GetProperty("baseCount").GetInt32());
            Assert.Equal(905, fuldaBootstrap.RootElement.GetProperty("entityCount").GetInt32());
            Assert.Equal(293, fuldaBootstrap.RootElement.GetProperty("taskForceCount").GetInt32());
            Assert.Equal(221, fuldaBootstrap.RootElement.GetProperty("openingOrderCount").GetInt32());
            Assert.Equal(324, fuldaBootstrap.RootElement.GetProperty("maritimeEntityCount").GetInt32());
            Assert.Equal(107, fuldaBootstrap.RootElement.GetProperty("airEntityCount").GetInt32());
            Assert.Equal(21, fuldaBootstrap.RootElement.GetProperty("rotaryWingEntityCount").GetInt32());
            Assert.Equal(165, fuldaBootstrap.RootElement.GetProperty("logisticsEntityCount").GetInt32());
            Assert.Equal(288, fuldaBootstrap.RootElement.GetProperty("landEntityCount").GetInt32());
            Assert.Equal(88, fuldaBootstrap.RootElement.GetProperty("groundUnitCount").GetInt32());
            Assert.Equal(174, fuldaBootstrap.RootElement.GetProperty("installationEntityCount").GetInt32());
            Assert.Equal(26, fuldaBootstrap.RootElement.GetProperty("abstractLandFormationCount").GetInt32());
            Assert.Equal(90, fuldaBootstrap.RootElement.GetProperty("civilianTrafficCount").GetInt32());
            Assert.Equal(220, fuldaBootstrap.RootElement.GetProperty("fixedSiteCount").GetInt32());
            Assert.Equal(73, fuldaBootstrap.RootElement.GetProperty("residentStrategicSystemCount").GetInt32());
            Assert.Equal(879, fuldaBootstrap.RootElement.GetProperty("tacticalEligibleEntityCount").GetInt32());
            var tacticalDemoEntityId = fuldaBootstrap.RootElement.GetProperty("tacticalDemoEntityId").GetString()!;
            var blueCoalitionId = fuldaBootstrap.RootElement.GetProperty("blueCoalitionId").GetString()!;
            var redCoalitionId = fuldaBootstrap.RootElement.GetProperty("redCoalitionId").GetString()!;
            using var fuldaViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{fuldaCampaignId}",
                testTimeout.Token);
            fuldaViewResponse.EnsureSuccessStatusCode();
            using var fuldaView = JsonDocument.Parse(
                await fuldaViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var fuldaEntities = fuldaView.RootElement.GetProperty("entities").EnumerateArray().ToArray();
            Assert.Equal(905, fuldaEntities.Length);
            var tacticalDemoGroup = Assert.Single(
                fuldaView.RootElement.GetProperty("groups").EnumerateArray(),
                item => item.GetProperty("memberEntityIds").EnumerateArray()
                    .Any(member => member.GetString() == tacticalDemoEntityId));
            Assert.Single(tacticalDemoGroup.GetProperty("memberEntityIds").EnumerateArray());
            Assert.All(
                fuldaEntities.Where(item => item.GetProperty("catalogUnitId").GetString() == "abstract_land_formation"),
                item => Assert.Equal("Operational", item.GetProperty("maximumFidelity").GetString()));
            Assert.All(
                fuldaEntities.Where(item => item.GetProperty("simulationDomain").GetString() == "LandAbstract"
                    && item.GetProperty("catalogUnitId").GetString() != "abstract_land_formation"),
                item => Assert.Equal("Tactical", item.GetProperty("maximumFidelity").GetString()));
            var fuldaEntityNames = fuldaEntities
                .Select(item => item.GetProperty("name").GetString()!)
                .ToArray();
            Assert.Contains(fuldaEntityNames, name => name.Contains("US Atlantic carrier", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("US Pacific carrier", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("Cuban air-defense site", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("San Antonio de los Banos GBAD", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("Western transatlantic merchant", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("physical armor target", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("physical low-altitude GBAD target", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("attack or lift helicopter", StringComparison.Ordinal));
            Assert.Contains(fuldaEntityNames, name => name.Contains("ballistic-missile TEL visual proxy", StringComparison.Ordinal));
            Assert.Equal(88, fuldaEntities.Count(item => item.GetProperty("simulationDomain").GetString() == "Ground"));
            Assert.Equal(174, fuldaEntities.Count(item => item.GetProperty("simulationDomain").GetString() == "Installation"));
            Assert.Equal(21, fuldaEntities.Count(item => item.GetProperty("simulationDomain").GetString() == "RotaryWing"));
            using var fuldaAdvanceResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{fuldaCampaignId}/advance/30",
                content: null,
                testTimeout.Token);
            fuldaAdvanceResponse.EnsureSuccessStatusCode();
            using var fuldaAdvance = JsonDocument.Parse(
                await fuldaAdvanceResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(30, fuldaAdvance.RootElement.GetProperty("timeQuanta").GetInt64());
            using var advancedFuldaViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{fuldaCampaignId}",
                testTimeout.Token);
            advancedFuldaViewResponse.EnsureSuccessStatusCode();
            using var advancedFuldaView = JsonDocument.Parse(
                await advancedFuldaViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var fuldaOutcomes = advancedFuldaView.RootElement.GetProperty("recentOperationalOutcomes")
                .EnumerateArray().ToArray();
            Assert.Contains(fuldaOutcomes, item => item.GetProperty("kind").GetString() == "WeaponExpenditure");
            Assert.Contains(fuldaOutcomes, item => item.GetProperty("kind").GetString() == "Detection");
            var fuldaCalibration = advancedFuldaView.RootElement.GetProperty("calibration");
            Assert.True(fuldaCalibration.GetProperty("inEnvelope").GetBoolean());
            Assert.Equal(0, fuldaCalibration.GetProperty("observedRangeViolationCount").GetInt32());
            Assert.True(
                fuldaCalibration.GetProperty("maximumRangeRatioMilliunits").GetInt32()
                <= fuldaCalibration.GetProperty("envelopeMaximumRangeRatioMilliunits").GetInt32());
            Assert.Equal(10, fuldaCalibration.GetProperty("maximumTacticalCompressionTimes").GetInt32());
            using var blueProjectionResponse = await campaignClient.GetAsync(
                $"/campaigns/{fuldaCampaignId}/projections/{blueCoalitionId}",
                testTimeout.Token);
            blueProjectionResponse.EnsureSuccessStatusCode();
            using var blueProjection = JsonDocument.Parse(
                await blueProjectionResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(blueCoalitionId, blueProjection.RootElement.GetProperty("coalitionId").GetString());
            Assert.Equal(
                "coalition-filtered; no-classified-truth; render-proxy-is-not-a-contact",
                blueProjection.RootElement.GetProperty("trustBoundary").GetString());
            var redNames = advancedFuldaView.RootElement.GetProperty("entities").EnumerateArray()
                .Where(item => item.GetProperty("coalitionId").GetString() == redCoalitionId)
                .Select(item => item.GetProperty("name").GetString()!)
                .ToArray();
            Assert.NotEmpty(redNames);
            var blueFriendly = blueProjection.RootElement.GetProperty("friendlyEntities").EnumerateArray().ToArray();
            Assert.NotEmpty(blueFriendly);
            var blueNames = advancedFuldaView.RootElement.GetProperty("entities").EnumerateArray()
                .Where(item => item.GetProperty("coalitionId").GetString() == blueCoalitionId)
                .Select(item => item.GetProperty("name").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(
                blueFriendly,
                item => Assert.Contains(item.GetProperty("name").GetString()!, blueNames));
            var blueProjectionJson = blueProjection.RootElement.GetRawText();
            Assert.All(redNames, name => Assert.DoesNotContain(name, blueProjectionJson, StringComparison.Ordinal));
            Assert.DoesNotContain("ClassifiedTruth", blueProjectionJson, StringComparison.OrdinalIgnoreCase);
            using var issuedSessionResponse = await campaignClient.PostAsJsonAsync(
                "/control/player-sessions",
                new { campaignId = fuldaCampaignId, playerId = "player-blue-1", coalitionId = blueCoalitionId },
                testTimeout.Token);
            issuedSessionResponse.EnsureSuccessStatusCode();
            using var issuedSession = JsonDocument.Parse(
                await issuedSessionResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var playerSessionId = issuedSession.RootElement.GetProperty("sessionId").GetString()!;
            var playerReconnectToken = issuedSession.RootElement.GetProperty("reconnectToken").GetString()!;
            using var reconnectResponse = await campaignClient.PostAsJsonAsync(
                "/control/player-sessions/reconnect",
                new { reconnectToken = playerReconnectToken },
                testTimeout.Token);
            reconnectResponse.EnsureSuccessStatusCode();
            using var reconnected = JsonDocument.Parse(
                await reconnectResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(playerSessionId, reconnected.RootElement.GetProperty("sessionId").GetString());
            var playerSessionGeneration = reconnected.RootElement.GetProperty("generation").GetInt32();
            Assert.Equal(2, playerSessionGeneration);
            Assert.NotEqual(playerReconnectToken, reconnected.RootElement.GetProperty("reconnectToken").GetString());
            using var staleGenerationCommand = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/player-commands",
                new
                {
                    sessionId = playerSessionId,
                    coalitionId = blueCoalitionId,
                    predictionId = "pred-stale-generation",
                    kind = "observe",
                    generation = 1,
                },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, staleGenerationCommand.StatusCode);
            using var staleGenerationBody = JsonDocument.Parse(
                await staleGenerationCommand.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("player-session-mismatch", staleGenerationBody.RootElement.GetProperty("error").GetString());
            using var firstCommand = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/player-commands",
                new
                {
                    sessionId = playerSessionId,
                    coalitionId = blueCoalitionId,
                    predictionId = "pred-observe-1",
                    kind = "observe",
                    generation = playerSessionGeneration,
                },
                testTimeout.Token);
            firstCommand.EnsureSuccessStatusCode();
            using var firstCommandBody = JsonDocument.Parse(await firstCommand.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.True(firstCommandBody.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal(1, firstCommandBody.RootElement.GetProperty("order").GetInt32());
            using var secondCommand = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/player-commands",
                new
                {
                    sessionId = playerSessionId,
                    coalitionId = blueCoalitionId,
                    predictionId = "pred-transfer-1",
                    kind = "transfer-control",
                    generation = playerSessionGeneration,
                },
                testTimeout.Token);
            secondCommand.EnsureSuccessStatusCode();
            using var secondCommandBody = JsonDocument.Parse(
                await secondCommand.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(2, secondCommandBody.RootElement.GetProperty("order").GetInt32());
            using var duplicateCommand = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/player-commands",
                new
                {
                    sessionId = playerSessionId,
                    coalitionId = blueCoalitionId,
                    predictionId = "pred-observe-1",
                    kind = "observe",
                    generation = playerSessionGeneration,
                },
                testTimeout.Token);
            duplicateCommand.EnsureSuccessStatusCode();
            using var duplicateBody = JsonDocument.Parse(
                await duplicateCommand.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(1, duplicateBody.RootElement.GetProperty("order").GetInt32());
            using var rejectedCommand = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/player-commands",
                new
                {
                    sessionId = playerSessionId,
                    coalitionId = blueCoalitionId,
                    predictionId = "pred-fire-1",
                    kind = "fire-weapon",
                    generation = playerSessionGeneration,
                },
                testTimeout.Token);
            rejectedCommand.EnsureSuccessStatusCode();
            using var rejectedBody = JsonDocument.Parse(
                await rejectedCommand.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.False(rejectedBody.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal("unsupported-player-command", rejectedBody.RootElement.GetProperty("status").GetString());
            using var invalidCoalitionSession = await campaignClient.PostAsJsonAsync(
                "/control/player-sessions",
                new
                {
                    campaignId = fuldaCampaignId,
                    playerId = "player-invalid-coalition",
                    coalitionId = Guid.NewGuid().ToString("N"),
                },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, invalidCoalitionSession.StatusCode);
            using var invalidCoalitionBody = JsonDocument.Parse(
                await invalidCoalitionSession.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("coalition-missing", invalidCoalitionBody.RootElement.GetProperty("error").GetString());

            var raceIssueTasks = Enumerable.Range(0, 2).Select(_ => campaignClient.PostAsJsonAsync(
                "/control/player-sessions",
                new { campaignId = fuldaCampaignId, playerId = "player-race", coalitionId = blueCoalitionId },
                testTimeout.Token)).ToArray();
            var raceIssues = await Task.WhenAll(raceIssueTasks);
            using var raceIssueFirst = raceIssues[0];
            using var raceIssueSecond = raceIssues[1];
            raceIssueFirst.EnsureSuccessStatusCode();
            raceIssueSecond.EnsureSuccessStatusCode();
            using var raceSessionFirst = JsonDocument.Parse(
                await raceIssueFirst.Content.ReadAsStringAsync(testTimeout.Token));
            using var raceSessionSecond = JsonDocument.Parse(
                await raceIssueSecond.Content.ReadAsStringAsync(testTimeout.Token));
            var raceCommands = new[] { raceSessionFirst, raceSessionSecond }
                .Select((session, index) => campaignClient.PostAsJsonAsync(
                    $"/control/campaigns/{fuldaCampaignId}/player-commands",
                    new
                    {
                        sessionId = session.RootElement.GetProperty("sessionId").GetString(),
                        coalitionId = blueCoalitionId,
                        predictionId = $"pred-race-{index}",
                        kind = "observe",
                        generation = 1,
                    },
                    testTimeout.Token))
                .ToArray();
            var raceCommandResponses = await Task.WhenAll(raceCommands);
            using var raceCommandFirst = raceCommandResponses[0];
            using var raceCommandSecond = raceCommandResponses[1];
            Assert.Equal(
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
                new[] { raceCommandFirst.StatusCode, raceCommandSecond.StatusCode }.Order().ToArray());
            var backupDirectory = Path.Combine(campaignServiceRunDirectory, "backups", "fulda-operator");
            var restoreDirectory = Path.Combine(campaignServiceRunDirectory, "restore-fulda");
            using var backupResponse = await campaignClient.PostAsJsonAsync(
                "/control/backups",
                new { destinationDirectory = backupDirectory },
                testTimeout.Token);
            Assert.True(
                backupResponse.IsSuccessStatusCode,
                await backupResponse.Content.ReadAsStringAsync(testTimeout.Token));
            using var backupBody = JsonDocument.Parse(await backupResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var backupHead = Assert.Single(
                backupBody.RootElement.GetProperty("campaigns").EnumerateArray(),
                item => item.GetProperty("campaignId").GetString() == fuldaCampaignId);
            Assert.Equal(
                advancedFuldaView.RootElement.GetProperty("canonicalHash").GetString(),
                backupHead.GetProperty("canonicalHash").GetString());
            using var backupListResponse = await campaignClient.GetAsync("/control/backups", testTimeout.Token);
            backupListResponse.EnsureSuccessStatusCode();
            using var backupListBody = JsonDocument.Parse(
                await backupListResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var listedBackup = Assert.Single(backupListBody.RootElement.EnumerateArray());
            Assert.Equal(
                backupBody.RootElement.GetProperty("databaseSha256").GetString(),
                listedBackup.GetProperty("databaseSha256").GetString());
            Assert.True(listedBackup.GetProperty("eventCount").GetInt32() > 0);
            Assert.Equal(
                backupHead.GetProperty("canonicalHash").GetString(),
                Assert.Single(
                    listedBackup.GetProperty("campaigns").EnumerateArray(),
                    item => item.GetProperty("campaignId").GetString() == fuldaCampaignId)
                    .GetProperty("canonicalHash").GetString());
            using var escapedBackupResponse = await campaignClient.PostAsJsonAsync(
                "/control/backups",
                new { destinationDirectory = Path.Combine(campaignServiceRunDirectory, "outside-backup-root") },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, escapedBackupResponse.StatusCode);
            using var escapedBackupBody = JsonDocument.Parse(
                await escapedBackupResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(
                "backup-destination-outside-root",
                escapedBackupBody.RootElement.GetProperty("error").GetString());
            using var restoreResponse = await campaignClient.PostAsJsonAsync(
                "/control/restores",
                new { backupDirectory, destinationStorageDirectory = restoreDirectory },
                testTimeout.Token);
            Assert.True(
                restoreResponse.IsSuccessStatusCode,
                await restoreResponse.Content.ReadAsStringAsync(testTimeout.Token));
            using var restoreBody = JsonDocument.Parse(
                await restoreResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var restoredHead = Assert.Single(
                restoreBody.RootElement.GetProperty("campaigns").EnumerateArray(),
                item => item.GetProperty("campaignId").GetString() == fuldaCampaignId);
            Assert.Equal(
                backupHead.GetProperty("canonicalHash").GetString(),
                restoredHead.GetProperty("canonicalHash").GetString());
            using var escapedRestoreResponse = await campaignClient.PostAsJsonAsync(
                "/control/restores",
                new
                {
                    backupDirectory,
                    destinationStorageDirectory = Path.Combine(campaignServiceRunDirectory, "..", "escaped-restore"),
                },
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, escapedRestoreResponse.StatusCode);
            using var escapedRestoreBody = JsonDocument.Parse(
                await escapedRestoreResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(
                "restore-destination-outside-run-directory",
                escapedRestoreBody.RootElement.GetProperty("error").GetString());
            using var missingPauseResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{Guid.NewGuid():N}/pause",
                content: null,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, missingPauseResponse.StatusCode);
            using var missingPauseBody = JsonDocument.Parse(
                await missingPauseResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("campaign-missing", missingPauseBody.RootElement.GetProperty("error").GetString());
            using var pauseResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{fuldaCampaignId}/pause",
                content: null,
                testTimeout.Token);
            pauseResponse.EnsureSuccessStatusCode();
            using var pausedAdvance = await campaignClient.PostAsync(
                $"/control/campaigns/{fuldaCampaignId}/advance/60",
                content: null,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Conflict, pausedAdvance.StatusCode);
            using var pausedBody = JsonDocument.Parse(await pausedAdvance.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("campaign-paused", pausedBody.RootElement.GetProperty("error").GetString());
            using var resumeResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{fuldaCampaignId}/resume",
                content: null,
                testTimeout.Token);
            resumeResponse.EnsureSuccessStatusCode();
            using var drainResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{fuldaCampaignId}/drain",
                content: null,
                testTimeout.Token);
            drainResponse.EnsureSuccessStatusCode();
            using var drainBody = JsonDocument.Parse(await drainResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("Draining", drainBody.RootElement.GetProperty("health").GetString());
            Assert.Equal("DrainOnly", drainBody.RootElement.GetProperty("eligibility").GetString());
            var landEntityId = Assert.Single(
                advancedFuldaView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("simulationDomain").GetString() == "LandAbstract"
                    && item.GetProperty("name").GetString() == "US V Corps (abstract)")
                .GetProperty("entityId").GetString()!;
            using var promoteResponse = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/schedule",
                new
                {
                    observedEntityIds = new[] { tacticalDemoEntityId, landEntityId },
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 4,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            promoteResponse.EnsureSuccessStatusCode();
            using var promoted = JsonDocument.Parse(
                await promoteResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var promotionTransitions = promoted.RootElement.GetProperty("transitions").EnumerateArray().ToArray();
            Assert.DoesNotContain(promotionTransitions, item =>
                item.GetProperty("entityId").GetString() == tacticalDemoEntityId
                && item.GetProperty("toFidelity").GetString() == "Tactical");
            Assert.Contains(
                promoted.RootElement.GetProperty("islands").EnumerateArray(),
                item => item.GetProperty("entityIds").EnumerateArray().Any(id => id.GetString() == tacticalDemoEntityId)
                    && item.GetProperty("desiredFidelity").GetString() == "Operational"
                    && item.GetProperty("reason").GetString() == "tactical-worker-offline");
            using var pendingPromotionViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{fuldaCampaignId}",
                testTimeout.Token);
            pendingPromotionViewResponse.EnsureSuccessStatusCode();
            using var pendingPromotionView = JsonDocument.Parse(
                await pendingPromotionViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(
                "Operational",
                Assert.Single(
                    pendingPromotionView.RootElement.GetProperty("entities").EnumerateArray(),
                    item => item.GetProperty("entityId").GetString() == tacticalDemoEntityId)
                    .GetProperty("fidelity").GetString());
            Assert.Contains(
                promoted.RootElement.GetProperty("islands").EnumerateArray(),
                item => item.GetProperty("entityIds").EnumerateArray().Any(id => id.GetString() == landEntityId)
                    && item.GetProperty("desiredFidelity").GetString() == "Operational"
                    && item.GetProperty("reason").GetString() is "simulation-domain-ceiling"
                        or "resident-system-domain-ceiling");
            Assert.Contains(
                promoted.RootElement.GetProperty("islands").EnumerateArray(),
                item => item.GetProperty("desiredFidelity").GetString() == "Operational"
                    && item.GetProperty("reason").GetString()!
                        .StartsWith("resident-system-", StringComparison.Ordinal));
            Assert.DoesNotContain(
                promoted.RootElement.GetProperty("islands").EnumerateArray(),
                item => item.GetProperty("reason").GetString()!
                        .StartsWith("resident-system-", StringComparison.Ordinal)
                    && item.GetProperty("desiredFidelity").GetString() == "Strategic");
            var loadSheddingIsland = promoted.RootElement.GetProperty("islands").EnumerateArray()
                .First(item => !item.GetProperty("engaged").GetBoolean()
                    && item.GetProperty("reason").GetString() == "outside-player-interest"
                    && item.GetProperty("entityIds").GetArrayLength() <= 4);
            var loadSheddingEntityId = loadSheddingIsland.GetProperty("entityIds")[0].GetString()!;

            using var loadPromotionResponse = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/schedule",
                new
                {
                    observedEntityIds = new[] { loadSheddingEntityId },
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 4,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            loadPromotionResponse.EnsureSuccessStatusCode();

            using var overloadedResponse = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/schedule",
                new
                {
                    observedEntityIds = new[] { loadSheddingEntityId },
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Pressured",
                    eligibility = "DrainOnly",
                    tacticalEntityCapacity = 4,
                    backlogDepth = 256,
                },
                testTimeout.Token);
            overloadedResponse.EnsureSuccessStatusCode();
            using var overloaded = JsonDocument.Parse(
                await overloadedResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(20, overloaded.RootElement.GetProperty("targetTicksPerWallSecond").GetInt32());
            Assert.Equal("global-tempo-reduced",
                overloaded.RootElement.GetProperty("loadAction").GetString());
            Assert.Empty(overloaded.RootElement.GetProperty("transitions").EnumerateArray());
            Assert.DoesNotContain(
                overloaded.RootElement.GetProperty("transitions").EnumerateArray(),
                item => item.GetProperty("fromFidelity").GetString() == "Strategic"
                    && item.GetProperty("toFidelity").GetString() == "Operational");

            using var rePromoteResponse = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/schedule",
                new
                {
                    observedEntityIds = new[] { loadSheddingEntityId },
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 4,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            rePromoteResponse.EnsureSuccessStatusCode();

            using var demoteResponse = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{fuldaCampaignId}/schedule",
                new
                {
                    observedEntityIds = Array.Empty<string>(),
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 4,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            demoteResponse.EnsureSuccessStatusCode();
            using var demoted = JsonDocument.Parse(
                await demoteResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var demotionTransitions = demoted.RootElement.GetProperty("transitions").EnumerateArray().ToArray();
            Assert.NotEmpty(demotionTransitions);
            Assert.Single(demotionTransitions.Select(item => item.GetProperty("islandId").GetString()).Distinct());
            Assert.All(demotionTransitions, item =>
                Assert.Equal("Strategic", item.GetProperty("toFidelity").GetString()));

            var start = await RunControlScriptAsync(
                scriptPath,
                "start",
                runDirectory,
                controlUrl,
                gamePipeName,
                skipBuild: true,
                testTimeout.Token,
                new Dictionary<string, string>
                {
                    ["CRIMEAN_TOM_CAMPAIGN_GRPC_URL"] = campaignGrpcUrl,
                    ["CRIMEAN_TOM_CAMPAIGN_HTTP_URL"] = campaignHttpUrl,
                    ["CRIMEAN_TOM_CAMPAIGN_ID"] = campaignId,
                    ["CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE"] = Path.Combine(campaignServiceRunDirectory, "service.token"),
                    ["CRIMEAN_TOM_CAMPAIGN_STATE_DIRECTORY"] = Path.Combine(runDirectory, "campaign-companion"),
                    ["CRIMEAN_TOM_COLLIDER_CACHE_DIR"] = colliderCacheDir,
                });
            Assert.True(start.ExitCode == 0, start.FormatFailure("start"));

            var companionProcessId = int.Parse(
                (await File.ReadAllTextAsync(pidPath, testTimeout.Token)).Trim());
            Assert.NotEqual(Environment.ProcessId, companionProcessId);
            Assert.NotEqual(
                $"{staleMarker}-token",
                (await File.ReadAllTextAsync(tokenPath, testTimeout.Token)).Trim());

            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(controlUrl),
                Timeout = TimeSpan.FromSeconds(8),
            };

            using var missingTokenResponse = await client.GetAsync("/health", testTimeout.Token);
            await AssertUnauthorizedAsync(missingTokenResponse, testTimeout.Token);

            using var wrongTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/game/ping");
            wrongTokenRequest.Headers.TryAddWithoutValidation(
                ProtocolConstants.ControlTokenHeader,
                "wrong-token");
            using var wrongTokenResponse = await client.SendAsync(
                wrongTokenRequest,
                testTimeout.Token);
            await AssertUnauthorizedAsync(wrongTokenResponse, testTimeout.Token);

            var token = (await File.ReadAllTextAsync(tokenPath, testTimeout.Token)).Trim();
            AssertAnonymousPipeAccessDenied(gamePipeName);
            var couplingState = new FakeTacticalCouplingState();
            var tacticalCheckpointState = new FakeTacticalCheckpointState();
            var gamePeer = RunGamePeerAsync(
                gamePipeName,
                probeSavePath,
                couplingState,
                testTimeout.Token,
                tacticalCheckpointState);
            await WaitForConnectedGameAsync(client, token, testTimeout.Token);
            var firstCampaignSession = await WaitForCampaignSessionAsync(campaignClient, null, testTimeout.Token);
            using var activeCampaignResponse = await campaignClient.GetAsync(
                "/active-campaign",
                testTimeout.Token);
            activeCampaignResponse.EnsureSuccessStatusCode();
            using var activeCampaignDocument = JsonDocument.Parse(
                await activeCampaignResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(
                campaignId,
                activeCampaignDocument.RootElement.GetProperty("campaignId").GetString());
            using (var monitorRedirectHandler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
            })
            using (var monitorRedirectClient = new HttpClient(monitorRedirectHandler)
            {
                BaseAddress = new Uri(campaignHttpUrl),
                Timeout = TimeSpan.FromSeconds(10),
            })
            {
                var expectedMonitorLocation = $"/monitor/?campaign={campaignId}";
                using var bareMonitor = await monitorRedirectClient.GetAsync(
                    "/monitor/",
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Redirect, bareMonitor.StatusCode);
                Assert.Equal(expectedMonitorLocation, bareMonitor.Headers.Location?.OriginalString);
                using var staleMonitor = await monitorRedirectClient.GetAsync(
                    $"/monitor/?campaign={fuldaCampaignId}",
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Redirect, staleMonitor.StatusCode);
                Assert.Equal(expectedMonitorLocation, staleMonitor.Headers.Location?.OriginalString);
                using var malformedMonitor = await monitorRedirectClient.GetAsync(
                    "/monitor/it",
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Redirect, malformedMonitor.StatusCode);
                Assert.Equal(expectedMonitorLocation, malformedMonitor.Headers.Location?.OriginalString);
                using var currentMonitor = await monitorRedirectClient.GetAsync(
                    expectedMonitorLocation,
                    testTimeout.Token);
                currentMonitor.EnsureSuccessStatusCode();
                Assert.Contains(
                    "Crimean Tom Campaign Monitor",
                    await currentMonitor.Content.ReadAsStringAsync(testTimeout.Token),
                    StringComparison.Ordinal);
            }
            await ProveConcurrentCampaignSessionsAsync(
                campaignClient,
                campaignGrpcUrl,
                campaignToken,
                campaignId,
                firstCampaignSession,
                testTimeout.Token);
            await WaitForCampaignResourceCountsAsync(
                campaignClient,
                materialized: 0,
                active: 0,
                retainedSuspended: 0,
                recoveryRequired: 0,
                cancellationToken: testTimeout.Token);
            var gameEntityId = $"unit:crimean-tom:{primaryEntityId}";
            var pinnedSourceProof = await ProvePinnedSourceWorkerPreparationAsync(
                campaignClient,
                campaignHttpUrl,
                campaignId,
                primaryEntityId,
                campaignServiceRunDirectory,
                testTimeout.Token);
            var assignmentId = pinnedSourceProof.AssignmentId;
            var authorityEpoch = pinnedSourceProof.AuthorityEpoch;
            using var checkpointDownload = await campaignClient.GetAsync(
                $"/content/{pinnedSourceProof.CheckpointSha256}",
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.OK, checkpointDownload.StatusCode);
            Assert.Equal(pinnedSourceProof.CheckpointLength,
                (await checkpointDownload.Content.ReadAsByteArrayAsync(testTimeout.Token)).Length);

            using var scheduleHealthy = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/schedule",
                new
                {
                    observedEntityIds = new[] { primaryEntityId },
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 1,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            scheduleHealthy.EnsureSuccessStatusCode();

            using var activeCampaign = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("lifecycle").GetString() == "Active"),
                testTimeout.Token);
            var activeWorker = Assert.Single(activeCampaign.RootElement.GetProperty("workers").EnumerateArray());
            Assert.Equal(4, activeWorker.GetProperty("tacticalEntityCapacity").GetInt32());
            Assert.Equal(1, activeWorker.GetProperty("tacticalReserveCapacity").GetInt32());
            Assert.Equal("Healthy", activeWorker.GetProperty("health").GetString());
            Assert.Equal("Eligible", activeWorker.GetProperty("eligibility").GetString());
            Assert.Equal(1, activeWorker.GetProperty("occupiedTacticalEntityCount").GetInt32());
            Assert.Equal(0, activeWorker.GetProperty("promotionHeadroom").GetInt32());
            Assert.Equal(1, activeWorker.GetProperty("migrationOverlapHeadroom").GetInt32());
            var activeAssignment = Assert.Single(activeCampaign.RootElement.GetProperty("assignments").EnumerateArray());
            Assert.Equal("Active", activeAssignment.GetProperty("lifecycle").GetString());
            await WaitForCampaignResourceCountsAsync(
                campaignClient,
                materialized: 1,
                active: 1,
                retainedSuspended: 0,
                recoveryRequired: 0,
                cancellationToken: testTimeout.Token);
            var renderProxyRequest = await tacticalCheckpointState.WaitForRenderProxyCountAsync(
                6,
                testTimeout.Token);
            Assert.Equal(campaignId, renderProxyRequest.CampaignId);
            Assert.Equal("taskforce:Blue", renderProxyRequest.TaskforceId);
            Assert.DoesNotContain(
                renderProxyRequest.Proxies,
                item => item.EntityId == gameEntityId);
            Assert.All(renderProxyRequest.Proxies, item =>
            {
                Assert.StartsWith("unit:crimean-tom:", item.EntityId, StringComparison.Ordinal);
                Assert.Equal("usn_ff_knox_84", item.UnitTypeId);
                Assert.Equal("Variant43", item.VariantId);
                Assert.Equal("Default", item.LoadoutId);
            });
            var activeEntities = activeCampaign.RootElement.GetProperty("entities").EnumerateArray().ToArray();
            await ProveShadowDestinationPreparationAsync(
                campaignClient,
                campaignHttpUrl,
                campaignGrpcUrl,
                campaignToken,
                campaignId,
                primaryEntityId,
                testTimeout.Token);
            var primaryBeforeAdvance = Assert.Single(activeEntities,
                item => item.GetProperty("entityId").GetString() == primaryEntityId);
            var unrelatedBeforeAdvance = Assert.Single(activeEntities,
                item => item.GetProperty("entityId").GetString() == unrelatedEntityId);

            using var advancedCampaign = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/advance/30",
                content: null,
                testTimeout.Token);
            advancedCampaign.EnsureSuccessStatusCode();
            using var advance = JsonDocument.Parse(
                await advancedCampaign.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(30, advance.RootElement.GetProperty("timeQuanta").GetInt64());
            Assert.True(advance.RootElement.GetProperty("tacticalEntitiesFrozen").GetBoolean());
            using var advancedViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            advancedViewResponse.EnsureSuccessStatusCode();
            using var advancedView = JsonDocument.Parse(
                await advancedViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var performance = advancedView.RootElement.GetProperty("performance");
            Assert.True(performance.GetProperty("recovery").GetProperty("completedCount").GetInt64() >= 1);
            Assert.True(performance.GetProperty("advance").GetProperty("completedCount").GetInt64() >= 1);
            Assert.Equal(30, performance.GetProperty("advance").GetProperty("lastWorkUnits").GetInt64());
            Assert.True(performance.GetProperty("advance").GetProperty("lastWorkUnitsPerSecondMilliunits").GetInt64() > 0);
            var advancedEntities = advancedView.RootElement.GetProperty("entities").EnumerateArray().ToArray();
            var primaryAfterAdvance = Assert.Single(advancedEntities,
                item => item.GetProperty("entityId").GetString() == primaryEntityId);
            var unrelatedAfterAdvance = Assert.Single(advancedEntities,
                item => item.GetProperty("entityId").GetString() == unrelatedEntityId);
            Assert.Equal(
                primaryBeforeAdvance.GetProperty("revision").GetInt64(),
                primaryAfterAdvance.GetProperty("revision").GetInt64());
            Assert.True(
                unrelatedAfterAdvance.GetProperty("revision").GetInt64()
                > unrelatedBeforeAdvance.GetProperty("revision").GetInt64());

            tacticalCheckpointState.SetObservedState(
                checkpointSha256: new string('d', 64),
                latitude: 12.27,
                longitude: 44.52,
                headingDegrees: 110,
                telegraph: 2,
                fuelFractionMilliunits: 900_000,
                storesFractionMilliunits: 800_000,
                damageFractionMilliunits: 100_000);
            var stateSyncFrames = await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 3,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                    && item.TacticalEvent.AssignmentId == assignmentId
                    && item.TacticalEvent.Kind == TacticalEventKind.StateSync
                    && item.TacticalEvent.CheckpointSha256 == new string('d', 64));
            Assert.Contains(stateSyncFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                && item.TacticalEvent.AssignmentId == assignmentId
                && item.TacticalEvent.Kind == TacticalEventKind.StateSync);
            using var syncedView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("entities").EnumerateArray().Any(item =>
                    item.GetProperty("entityId").GetString() == primaryEntityId
                    && item.GetProperty("latitudeMicrodegrees").GetInt32() == 12_270_000
                    && item.GetProperty("damageMilliunits").GetInt32() == 100_000),
                testTimeout.Token);
            var syncedPrimary = Assert.Single(
                syncedView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == primaryEntityId);
            Assert.Equal(44_520_000, syncedPrimary.GetProperty("longitudeMicrodegrees").GetInt32());
            Assert.Equal(110_000, syncedPrimary.GetProperty("headingMilliDegrees").GetInt32());
            Assert.Equal(2, syncedPrimary.GetProperty("telegraph").GetInt32());
            Assert.Equal(900_000, syncedPrimary.GetProperty("fuelMilliunits").GetInt64());
            Assert.Equal(400_000, syncedPrimary.GetProperty("storesMilliunits").GetInt64());

            using var liveLeaseResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/resolve-expired/150",
                content: null,
                testTimeout.Token);
            liveLeaseResponse.EnsureSuccessStatusCode();
            using var liveLease = JsonDocument.Parse(
                await liveLeaseResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(150, liveLease.RootElement.GetProperty("timeQuanta").GetInt64());
            Assert.Empty(liveLease.RootElement.GetProperty("assignments").EnumerateArray());
            using var liveLeaseViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            liveLeaseViewResponse.EnsureSuccessStatusCode();
            using var liveLeaseView = JsonDocument.Parse(
                await liveLeaseViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var leasedAssignment = Assert.Single(
                liveLeaseView.RootElement.GetProperty("assignments").EnumerateArray(),
                item => item.GetProperty("assignmentId").GetString()
                    == assignmentId["assignment:".Length..]);
            Assert.Equal("Active", leasedAssignment.GetProperty("lifecycle").GetString());
            Assert.True(leasedAssignment.GetProperty("leaseLive").GetBoolean());

            using var queuedCommand = await campaignClient.PostAsJsonAsync(
                "/control/commands",
                new OrderedTacticalCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"),
                    IdempotencyKey = "transport-move-1",
                    AssignmentId = assignmentId,
                    EntityId = gameEntityId,
                    AuthorityEpoch = authorityEpoch,
                    ExpectedEntityRevision = 1,
                    EffectiveTimeQuanta = 1,
                    LatitudeMicrodegrees = 12_260_000,
                    LongitudeMicrodegrees = 44_510_000,
                    HeadingMillidegrees = 100_000,
                    Telegraph = 1,
                },
                JsonOptions,
                testTimeout.Token);
            Assert.True(
                queuedCommand.StatusCode == HttpStatusCode.Accepted,
                $"Tactical command was not queued: {queuedCommand.StatusCode} "
                + await queuedCommand.Content.ReadAsStringAsync(testTimeout.Token));
            var commandFrames = await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 3,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                    && item.TacticalEvent.IdempotencyKey == "transport-move-1");
            Assert.Contains(commandFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                && item.TacticalEvent.IdempotencyKey == "transport-move-1");
            using var movedView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("entities").EnumerateArray().Any(item =>
                    item.GetProperty("entityId").GetString() == primaryEntityId
                    && item.GetProperty("latitudeMicrodegrees").GetInt32() == 12_260_000),
                testTimeout.Token);
            var movedPrimary = Assert.Single(
                movedView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == primaryEntityId);
            Assert.Equal(12_260_000, movedPrimary.GetProperty("latitudeMicrodegrees").GetInt32());
            Assert.Equal(44_510_000, movedPrimary.GetProperty("longitudeMicrodegrees").GetInt32());
            Assert.Equal(100_000, movedPrimary.GetProperty("headingMilliDegrees").GetInt32());
            Assert.Equal(1, movedPrimary.GetProperty("telegraph").GetInt32());

            campaignService.Kill(entireProcessTree: true);
            await campaignService.WaitForExitAsync(testTimeout.Token);
            campaignService.Dispose();
            campaignService = StartCampaignService(
                repositoryRoot,
                campaignServiceRunDirectory,
                campaignGrpcUrl,
                campaignHttpUrl,
                MigrationCooldownEnvironment);
            await WaitForCampaignServiceAsync(campaignServiceRunDirectory, campaignHttpUrl, testTimeout.Token);
            var secondCampaignSession = await WaitForCampaignSessionAsync(
                campaignClient,
                firstCampaignSession,
                testTimeout.Token);
            Assert.NotEqual(firstCampaignSession, secondCampaignSession);
            using var recoveredSchedulerResponse = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            recoveredSchedulerResponse.EnsureSuccessStatusCode();
            using var recoveredScheduler = JsonDocument.Parse(
                await recoveredSchedulerResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("Healthy", recoveredScheduler.RootElement.GetProperty("scheduler").GetProperty("health").GetString());
            Assert.Equal("Eligible", recoveredScheduler.RootElement.GetProperty("scheduler").GetProperty("eligibility").GetString());
            Assert.Equal(1, recoveredScheduler.RootElement.GetProperty("scheduler").GetProperty("stableSampleCount").GetInt32());

            using var queuedQuiesce = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/schedule",
                new
                {
                    observedEntityIds = new[] { primaryEntityId },
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Draining",
                    eligibility = "DrainOnly",
                    tacticalEntityCapacity = 1,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            queuedQuiesce.EnsureSuccessStatusCode();
            var quiesceFrames = await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 5,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                    && item.TacticalEvent.AssignmentId == assignmentId
                    && item.TacticalEvent.Kind == TacticalEventKind.Checkpoint);
            Assert.Contains(quiesceFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished
                && item.CheckpointPublished.AssignmentId == assignmentId
                && item.CheckpointPublished.EntityRevision == 2);
            Assert.Contains(quiesceFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                && item.TacticalEvent.AssignmentId == assignmentId
                && item.TacticalEvent.Kind == TacticalEventKind.Checkpoint);
            using var releasedView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("lifecycle").GetString() == "Released")
                    && root.GetProperty("entities").EnumerateArray().Any(item =>
                        item.GetProperty("entityId").GetString() == primaryEntityId
                        && item.GetProperty("fidelity").GetString() == "Operational"),
                testTimeout.Token);
            Assert.Equal(
                "Released",
                Assert.Single(
                    releasedView.RootElement.GetProperty("assignments").EnumerateArray(),
                    item => item.GetProperty("role").GetString() == "Primary")
                    .GetProperty("lifecycle").GetString());
            var releasedPrimary = Assert.Single(
                releasedView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == primaryEntityId);
            Assert.Equal("Operational", releasedPrimary.GetProperty("fidelity").GetString());
            Assert.Equal(800_000, releasedPrimary.GetProperty("fuelMilliunits").GetInt64());
            Assert.Equal(300_000, releasedPrimary.GetProperty("storesMilliunits").GetInt64());
            Assert.Equal(250_000, releasedPrimary.GetProperty("damageMilliunits").GetInt32());
            await WaitForCampaignResourceCountsAsync(
                campaignClient,
                materialized: 1,
                active: 0,
                retainedSuspended: 1,
                recoveryRequired: 0,
                cancellationToken: testTimeout.Token);

            using var batchPromotion = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/schedule",
                new
                {
                    observedEntityIds = new[] { batchLeadEntityId },
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 3,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            batchPromotion.EnsureSuccessStatusCode();
            using var activeBatchView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Count(item =>
                    item.GetProperty("batchId").GetString() is { Length: > 0 }
                    && item.GetProperty("lifecycle").GetString() == "Active") == 2,
                testTimeout.Token);
            var activeBatchAssignments = activeBatchView.RootElement.GetProperty("assignments")
                .EnumerateArray()
                .Where(item => item.GetProperty("batchId").GetString() is { Length: > 0 })
                .ToArray();
            Assert.Equal(2, activeBatchAssignments.Length);
            Assert.Single(activeBatchAssignments.Select(item => item.GetProperty("batchId").GetString()).Distinct());
            Assert.All(activeBatchAssignments, item => Assert.Equal(2, item.GetProperty("batchSize").GetInt32()));
            await WaitForCampaignResourceCountsAsync(
                campaignClient,
                materialized: 3,
                active: 2,
                retainedSuspended: 1,
                recoveryRequired: 0,
                cancellationToken: testTimeout.Token);
            var activeBatchEntities = activeBatchView.RootElement.GetProperty("entities").EnumerateArray().ToArray();
            Assert.All(new[] { batchLeadEntityId, batchWingEntityId }, entityId =>
                Assert.Equal("Tactical", Assert.Single(activeBatchEntities,
                    item => item.GetProperty("entityId").GetString() == entityId)
                    .GetProperty("fidelity").GetString()));

            couplingState.SetActiveWeapon(batchLeadEntityId, batchWingEntityId);
            couplingState.SetRelationship(
                "plotting-track",
                batchLeadEntityId,
                batchWingEntityId,
                "SearchRadar:Surface");
            couplingState.SetRelationship(
                "physical-dependency",
                batchWingEntityId,
                batchLeadEntityId,
                "collision-avoidance");
            await WaitForCampaignCouplingCountAsync(campaignClient, 3, testTimeout.Token);
            using var coupledInterestLoss = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/schedule",
                new
                {
                    observedEntityIds = Array.Empty<string>(),
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 3,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            coupledInterestLoss.EnsureSuccessStatusCode();
            using var coupledView = JsonDocument.Parse(
                await coupledInterestLoss.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Contains(coupledView.RootElement.GetProperty("islands").EnumerateArray(), item =>
                item.GetProperty("entityIds").EnumerateArray().Any(id => id.GetString() == batchLeadEntityId)
                && item.GetProperty("reason").GetString() == "active-coupling-fence"
                && item.GetProperty("unsafeToSplit").GetBoolean()
                && item.GetProperty("couplingReasons").EnumerateArray().Any(reason =>
                    reason.GetString() == "active-weapon:SemiActiveRadarHoming")
                && item.GetProperty("couplingReasons").EnumerateArray().Any(reason =>
                    reason.GetString() == "plotting-track:SearchRadar:Surface")
                && item.GetProperty("couplingReasons").EnumerateArray().Any(reason =>
                    reason.GetString() == "physical-dependency:collision-avoidance"));
            couplingState.Clear();
            await WaitForCampaignCouplingCountAsync(campaignClient, 0, testTimeout.Token);

            using var batchInterestLoss = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/schedule",
                new
                {
                    observedEntityIds = Array.Empty<string>(),
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 3,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            batchInterestLoss.EnsureSuccessStatusCode();
            using var heldBatch = JsonDocument.Parse(
                await batchInterestLoss.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(1, heldBatch.RootElement.GetProperty("stableSampleCount").GetInt32());
            Assert.Contains(heldBatch.RootElement.GetProperty("islands").EnumerateArray(), item =>
                item.GetProperty("entityIds").EnumerateArray().Any(id => id.GetString() == batchLeadEntityId)
                && item.GetProperty("reason").GetString() == "scheduler-hysteresis-hold");
            using var heldBatchView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Count(item =>
                    item.GetProperty("batchId").GetString() is { Length: > 0 }
                    && item.GetProperty("lifecycle").GetString() == "Active") == 2,
                testTimeout.Token);

            using var confirmedBatchInterestLoss = await campaignClient.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/schedule",
                new
                {
                    observedEntityIds = Array.Empty<string>(),
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 4,
                    tacticalReserveCapacity = 2,
                    backlogDepth = 0,
                },
                testTimeout.Token);
            confirmedBatchInterestLoss.EnsureSuccessStatusCode();
            using var reserveLoadShed = JsonDocument.Parse(
                await confirmedBatchInterestLoss.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(4, reserveLoadShed.RootElement.GetProperty("tacticalEntityCapacity").GetInt32());
            Assert.Equal(2, reserveLoadShed.RootElement.GetProperty("tacticalReserveCapacity").GetInt32());
            Assert.Equal(3, reserveLoadShed.RootElement.GetProperty("occupiedTacticalEntityCount").GetInt32());
            Assert.Equal(2, reserveLoadShed.RootElement.GetProperty("activeTacticalEntityCount").GetInt32());
            Assert.Equal(1, reserveLoadShed.RootElement.GetProperty("retainedSuspendedTacticalEntityCount").GetInt32());
            Assert.Equal(0, reserveLoadShed.RootElement.GetProperty("availableTacticalEntityCapacity").GetInt32());
            Assert.Equal("demote-whole-islands-before-tempo",
                reserveLoadShed.RootElement.GetProperty("loadAction").GetString());
            Assert.Contains(reserveLoadShed.RootElement.GetProperty("islands").EnumerateArray(), item =>
                item.GetProperty("entityIds").EnumerateArray().Any(id => id.GetString() == batchLeadEntityId)
                && item.GetProperty("reason").GetString() == "tactical-reserve-load-shed");
            using var releasedBatchView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Count(item =>
                    item.GetProperty("batchId").GetString() is { Length: > 0 }
                    && item.GetProperty("lifecycle").GetString() == "Released") == 2,
                testTimeout.Token);
            var releasedBatchEntities = releasedBatchView.RootElement.GetProperty("entities").EnumerateArray().ToArray();
            Assert.All(new[] { batchLeadEntityId, batchWingEntityId }, entityId =>
                Assert.Equal("Operational", Assert.Single(releasedBatchEntities,
                    item => item.GetProperty("entityId").GetString() == entityId)
                    .GetProperty("fidelity").GetString()));

            var mismatchAssignmentId = $"assignment:{Guid.Parse("40000000-0000-0000-0000-000000000006"):N}";
            using var mismatchHandoff = await campaignClient.PostAsJsonAsync(
                "/control/handoffs",
                new
                {
                    campaignId,
                    canonicalEntityId = mismatchEntityId,
                    gameEntityId = "unit:crimean-tom:mismatch-knox",
                    assignmentId = mismatchAssignmentId,
                    authorityEpoch = 14,
                    idempotencyKey = "mismatch-materialize-1",
                    requiredRuntimeFingerprintSha256 = new string('a', 64),
                    requiredCatalogFingerprintSha256 = "deliberately-wrong-catalog",
                    unitTypeId = "usn_ff_knox_84",
                    variantId = "Variant43",
                    loadoutId = "Default",
                    taskforceId = "taskforce:Blue",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, mismatchHandoff.StatusCode);
            var mismatchFrames = await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 1,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                    && item.AssignmentPrepared.AssignmentId == mismatchAssignmentId);
            var rejectedMismatch = Assert.Single(mismatchFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                && item.AssignmentPrepared.AssignmentId == mismatchAssignmentId).AssignmentPrepared;
            Assert.False(rejectedMismatch.Ready);
            Assert.Equal("assignment-preparation-failed", rejectedMismatch.ErrorCode);
            using var rejectedMismatchView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("assignmentId").GetString()
                        == mismatchAssignmentId["assignment:".Length..]
                    && item.GetProperty("lifecycle").GetString() == "Released"),
                testTimeout.Token);

            var combatAssignmentId = $"assignment:{Guid.Parse("40000000-0000-0000-0000-000000000007"):N}";
            const string combatGameEntityId = "unit:crimean-tom:combat-knox";
            using var combatHandoff = await campaignClient.PostAsJsonAsync(
                "/control/handoffs",
                new
                {
                    campaignId,
                    canonicalEntityId = combatEntityId,
                    gameEntityId = combatGameEntityId,
                    assignmentId = combatAssignmentId,
                    authorityEpoch = 13,
                    idempotencyKey = "combat-materialize-1",
                    requiredRuntimeFingerprintSha256 = new string('a', 64),
                    requiredCatalogFingerprintSha256 = string.Empty,
                    unitTypeId = "usn_ff_knox_84",
                    variantId = "Variant43",
                    loadoutId = "Default",
                    taskforceId = "taskforce:Blue",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, combatHandoff.StatusCode);
            await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 1,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                    && item.AssignmentPrepared.AssignmentId == combatAssignmentId
                    && item.AssignmentPrepared.Ready);
            using var activeCombatView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("assignmentId").GetString()
                        == combatAssignmentId["assignment:".Length..]
                    && item.GetProperty("lifecycle").GetString() == "Active"),
                testTimeout.Token);

            using var ping = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/ping",
                testTimeout.Token);
            Assert.True(ping.RootElement.GetProperty("isMainThread").GetBoolean());

            using var capabilities = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/capabilities",
                testTimeout.Token);
            var supportedCommands = capabilities.RootElement
                .GetProperty("supportedCommands")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Equal(GameCommandTypes.AllCommands, supportedCommands);
            Assert.True(capabilities.RootElement.GetProperty("uiAutomationEnabled").GetBoolean());
            Assert.Equal(
                GameCommandTypes.OpenTaskForceBuilder,
                capabilities.RootElement
                    .GetProperty("supportedUiAutomationCommands")[0]
                    .GetString());
            Assert.False(
                capabilities.RootElement.GetProperty("mutatingCommandsEnabled").GetBoolean());

            using var scene = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/scene",
                testTimeout.Token);
            Assert.Equal("IntegrationScenario", scene.RootElement.GetProperty("sceneName").GetString());

            using var world = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/world",
                testTimeout.Token);
            Assert.Equal("loaded-scene", world.RootElement.GetProperty("identityScope").GetString());
            Assert.Equal(
                "unit:42",
                world.RootElement.GetProperty("units")[0].GetProperty("id").GetString());
            Assert.Equal(
                "taskforce:Blue",
                world.RootElement.GetProperty("taskforces")[0].GetProperty("id").GetString());
            Assert.Equal(
                "integration-canonical-sha256",
                world.RootElement.GetProperty("canonicalSha256").GetString());
            Assert.Equal(
                "player-track:7001",
                world.RootElement.GetProperty("contacts")[0].GetProperty("id").GetString());
            Assert.Equal(
                "estimated",
                world.RootElement.GetProperty("contacts")[0].GetProperty("positionState").GetString());

            using var catalog = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/catalog/units?offset=0&limit=5&folder=vessels",
                testTimeout.Token);
            Assert.Equal("ready", catalog.RootElement.GetProperty("state").GetString());
            Assert.Equal(
                "unit:vessels/usn_ff_knox",
                catalog.RootElement.GetProperty("records")[0].GetProperty("id").GetString());
            Assert.Equal(
                "base",
                catalog.RootElement.GetProperty("records")[0].GetProperty("sourceId").GetString());

            using var ammunitionCatalog = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/catalog/references?kind=ammunition&offset=0&limit=5",
                testTimeout.Token);
            Assert.Equal(
                "ammunition:rim-7m",
                ammunitionCatalog.RootElement.GetProperty("records")[0].GetProperty("id").GetString());

            using var catalogDiagnostics = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/catalog/diagnostics",
                testTimeout.Token);
            Assert.Equal("ready", catalogDiagnostics.RootElement.GetProperty("state").GetString());
            Assert.Equal(
                "conflicting-override",
                catalogDiagnostics.RootElement
                    .GetProperty("overrides")[0]
                    .GetProperty("classification")
                    .GetString());

            using var campaign = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/campaign",
                testTimeout.Token);
            Assert.True(campaign.RootElement.GetProperty("isTaskForceModeCampaign").GetBoolean());

            using var uiInspection = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/ui",
                testTimeout.Token);
            Assert.Equal("IntegrationScenario", uiInspection.RootElement.GetProperty("sceneName").GetString());
            Assert.Equal(1920, uiInspection.RootElement.GetProperty("screenWidth").GetInt32());

            using var screenshot = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/screenshot",
                testTimeout.Token);
            Assert.Equal("completed", screenshot.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("screenshot-png-materialized", screenshot.RootElement.GetProperty("postcondition").GetString());
            var screenshotHash = screenshot.RootElement.GetProperty("sha256").GetString();
            Assert.False(string.IsNullOrWhiteSpace(screenshotHash));
            Assert.Equal(1, screenshot.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(1, screenshot.RootElement.GetProperty("height").GetInt32());

            using var screenshotImage = new HttpRequestMessage(HttpMethod.Get, "/game/screenshot/image");
            screenshotImage.Headers.Add(ProtocolConstants.ControlTokenHeader, token);
            using var screenshotImageResponse = await client.SendAsync(screenshotImage, testTimeout.Token);
            screenshotImageResponse.EnsureSuccessStatusCode();
            Assert.Equal("image/png", screenshotImageResponse.Content.Headers.ContentType?.MediaType);

            using var repeatedScreenshot = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/screenshot",
                testTimeout.Token);
            Assert.Equal("completed", repeatedScreenshot.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                screenshotHash,
                repeatedScreenshot.RootElement.GetProperty("sha256").GetString());

            using var colliderCapture = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/colliders/capture",
                testTimeout.Token);
            Assert.Equal(
                "no-unit-colliders-captured",
                colliderCapture.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(0, colliderCapture.RootElement.GetProperty("memoryCount").GetInt32());

            new ColliderGeometryCache(ColliderGeometryCache.ResolveRoot(colliderCacheDir, "integration"))
                .Upsert(new ColliderUnitGeometryPayload
                {
                    GameVersion = "integration",
                    UnitTypeId = "usn_ff_knox_84",
                    ClassName = "Knox",
                    Source = "test",
                    Colliders =
                    [
                        new ColliderPrimitivePayload
                        {
                            Kind = "box",
                            Name = "hull",
                            Enabled = true,
                            Size = [10f, 4f, 20f],
                        },
                    ],
                });

            using var colliderCatalog = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/colliders?unitTypeId=usn_ff_knox_84",
                testTimeout.Token);
            Assert.Equal("file-cache", colliderCatalog.RootElement.GetProperty("source").GetString());
            Assert.Equal(1, colliderCatalog.RootElement.GetProperty("memoryCount").GetInt32());
            Assert.Equal(
                "usn_ff_knox_84",
                colliderCatalog.RootElement.GetProperty("geometry").GetProperty("unitTypeId").GetString());
            Assert.Equal(
                "box",
                colliderCatalog.RootElement.GetProperty("geometry").GetProperty("colliders")[0].GetProperty("kind").GetString());

            using var colliderList = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/colliders",
                testTimeout.Token);
            Assert.Equal("file-cache", colliderList.RootElement.GetProperty("source").GetString());
            Assert.Equal("integration", colliderList.RootElement.GetProperty("gameVersion").GetString());
            Assert.Equal(1, colliderList.RootElement.GetProperty("fileCount").GetInt32());
            Assert.Equal(
                "integration",
                colliderList.RootElement.GetProperty("entries")[0].GetProperty("gameVersion").GetString());

            using var focusCamera = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/automation/camera/focus",
                """{"unitId":"unit:42"}""",
                testTimeout.Token);
            Assert.Equal("camera-focused-on-unit", focusCamera.RootElement.GetProperty("postcondition").GetString());

            using var hudPanel = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/automation/hud",
                """{"panel":"map"}""",
                testTimeout.Token);
            Assert.Equal("tactical-map-visible", hudPanel.RootElement.GetProperty("postcondition").GetString());
            Assert.True(campaign.RootElement.GetProperty("taskForceBuilderEnabled").GetBoolean());
            Assert.False(campaign.RootElement.GetProperty("taskForceBuilderLoaded").GetBoolean());

            using var startPortOperations = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/port-operations/start",
                testTimeout.Token);
            Assert.Equal(
                "port-operations-campaign-load-requested",
                startPortOperations.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                "campaigns/crimean-tom-port-operations/campaign.ini",
                startPortOperations.RootElement.GetProperty("campaignPath").GetString());

            using var openBuilder = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/task-force-builder",
                testTimeout.Token);
            Assert.Equal("completed", openBuilder.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "task-force-builder-loaded-for-selected-mission",
                openBuilder.RootElement.GetProperty("postcondition").GetString());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("taskForceBuilderLoaded")
                    .GetBoolean());
            Assert.Equal(
                12,
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("availableRosterVariantCount")
                    .GetInt32());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("hasShipRosterUnits")
                    .GetBoolean());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("hasSubmarineRosterUnits")
                    .GetBoolean());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("hasAirRosterUnits")
                    .GetBoolean());
            var purchasedUnit = openBuilder.RootElement
                .GetProperty("campaign")
                .GetProperty("purchasedUnits")[0];
            Assert.Equal("port-unit:1", purchasedUnit.GetProperty("instanceId").GetString());
            Assert.Equal("usn_ff_knox_84", purchasedUnit.GetProperty("unitType").GetString());
            Assert.Equal("Default", purchasedUnit.GetProperty("selectedLoadoutVariant").GetString());
            Assert.Equal(1, purchasedUnit.GetProperty("quantity").GetInt32());
            Assert.Equal(
                "ready",
                openBuilder.RootElement.GetProperty("campaign")
                    .GetProperty("nativeMaintenanceState").GetString());
            Assert.True(
                openBuilder.RootElement.GetProperty("campaign")
                    .GetProperty("nativeRearmAvailable").GetBoolean());

            using var addMaintenanceUnit = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/automation/port-operations/draft/units",
                JsonSerializer.Serialize(new AddMaintenanceDraftUnitRequestPayload
                {
                    UnitType = "usn_ffg_oliver_hazard_perry",
                    VariantReference = "Variant17",
                    Quantity = 1,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "maintenance-unit-staged-uncommitted",
                addMaintenanceUnit.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                "usn_ffg_oliver_hazard_perry",
                addMaintenanceUnit.RootElement
                    .GetProperty("campaign")
                    .GetProperty("pendingUnits")[0]
                    .GetProperty("unitType")
                    .GetString());

            using var startScenario = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/automation/scenario/start",
                JsonSerializer.Serialize(new StartScenarioRequestPayload
                {
                    ScenarioId = "scenario:missions/intro/integration",
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "scenario-load-requested",
                startScenario.RootElement.GetProperty("postcondition").GetString());

            using var continueBriefing = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/briefing/continue",
                testTimeout.Token);
            Assert.Equal(
                "mission-running-unpaused",
                continueBriefing.RootElement.GetProperty("postcondition").GetString());

            using var pause = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/experimental/pause",
                JsonSerializer.Serialize(new ExperimentalPauseRequestPayload
                {
                    IsPaused = true,
                }, JsonOptions),
                testTimeout.Token);
            Assert.True(pause.RootElement.GetProperty("isPaused").GetBoolean());

            using var connectedHealth = await GetAuthorizedJsonAsync(
                client,
                token,
                "/health",
                testTimeout.Token);
            var sessionId = connectedHealth.RootElement.GetProperty("sessionId").GetString()!;
            using var armedMutations = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/arm",
                JsonSerializer.Serialize(new ArmOwnedMutationModeRequestPayload
                {
                    SessionId = sessionId,
                    RuntimeFingerprintSha256 = new string('a', 64),
                    Confirmation = "enable-owned-mutations",
                    DurationSeconds = 60,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "owned-mutations-armed-for-current-session",
                armedMutations.RootElement.GetProperty("postcondition").GetString());

            using var materializedSurface = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/materialize",
                JsonSerializer.Serialize(new MaterializeOwnedSurfaceUnitRequestPayload
                {
                    Context = new OwnedMutationContextPayload
                    {
                        SessionId = sessionId,
                        AssignmentId = "assignment:integration-surface",
                        AuthorityEpoch = 7,
                        ExpectedRevision = 0,
                        IdempotencyKey = "integration-knox-1",
                    },
                    EntityId = "unit:crimean-tom:integration-knox-1",
                    UnitTypeId = "usn_ff_knox_84",
                    VariantId = "Variant43",
                    LoadoutId = "Default",
                    TaskforceId = "taskforce:Blue",
                    Latitude = 12.25,
                    Longitude = 44.5,
                    HeadingDegrees = 90,
                    Telegraph = 0,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "owned-surface-unit-materialized-and-read-back",
                materializedSurface.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(1, materializedSurface.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(
                "campaign-unit:unit:crimean-tom:integration-knox-1",
                materializedSurface.RootElement
                    .GetProperty("realizedUnit")
                    .GetProperty("stableUnitId")
                    .GetString());

            using var ownedResources = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/mutations/surface/resources",
                testTimeout.Token);
            Assert.Equal(2, ownedResources.RootElement.GetProperty("activeObjectCount").GetInt32());
            Assert.False(
                ownedResources.RootElement.GetProperty("memoryReclamationSupported").GetBoolean());

            using var renderProxyState = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/render-proxies",
                testTimeout.Token);
            Assert.Equal(
                "visual-only-preferred",
                renderProxyState.RootElement.GetProperty("representationProvider").GetString());
            var reportedRenderProxies = renderProxyState.RootElement
                .GetProperty("proxies")
                .EnumerateArray()
                .ToArray();
            Assert.NotEmpty(reportedRenderProxies);
            Assert.All(
                reportedRenderProxies,
                proxy =>
                {
                    Assert.Equal(
                        "visual-model-and-map-annotation",
                        proxy.GetProperty("representationKind").GetString());
                    Assert.False(proxy.GetProperty("hasNativeSimulationObject").GetBoolean());
                    Assert.False(proxy.GetProperty("isSimulated").GetBoolean());
                    Assert.True(proxy.GetProperty("worldModelVisible").GetBoolean());
                    Assert.True(proxy.GetProperty("mapMarkerVisible").GetBoolean());
                });

            using var releasedRecovery = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/recovery/release",
                JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                {
                    Context = CreateOwnedContext(sessionId, 0, "integration-release-recovery-1"),
                    EntityId = "unit:crimean-tom:integration-recovery-1",
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "detached-owned-recovery-record-released-without-unity-mutation",
                releasedRecovery.RootElement.GetProperty("postcondition").GetString());

            using var checkpoint = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/checkpoint",
                CreateOwnedEntityRequestJson(sessionId, 1, "integration-checkpoint-1"),
                testTimeout.Token);
            Assert.Equal(64, checkpoint.RootElement.GetProperty("checkpointSha256").GetString()!.Length);

            using var updatedSurface = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/update",
                JsonSerializer.Serialize(new UpdateOwnedSurfaceUnitRequestPayload
                {
                    Context = CreateOwnedContext(sessionId, 1, "integration-update-1"),
                    EntityId = "unit:crimean-tom:integration-knox-1",
                    Latitude = 12.26,
                    Longitude = 44.51,
                    HeadingDegrees = 100,
                    Telegraph = 1,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(2, updatedSurface.RootElement.GetProperty("revision").GetInt64());

            using var suspendedSurface = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/suspension",
                CreateSuspensionRequestJson(sessionId, 2, "integration-suspend-1", true),
                testTimeout.Token);
            Assert.True(
                suspendedSurface.RootElement.GetProperty("unit").GetProperty("isDisabled").GetBoolean());

            using var restoredSurface = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/restore",
                JsonSerializer.Serialize(new RestoreOwnedSurfaceCheckpointRequestPayload
                {
                    Context = CreateOwnedContext(sessionId, 3, "integration-restore-1"),
                    EntityId = "unit:crimean-tom:integration-knox-1",
                    CheckpointId = checkpoint.RootElement.GetProperty("checkpointId").GetString()!,
                    CheckpointSha256 = checkpoint.RootElement
                        .GetProperty("checkpointSha256")
                        .GetString()!,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "owned-surface-unit-reenabled-from-portable-checkpoint",
                restoredSurface.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(4, restoredSurface.RootElement.GetProperty("revision").GetInt64());

            using var resuspendedSurface = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/suspension",
                CreateSuspensionRequestJson(sessionId, 4, "integration-suspend-2", true),
                testTimeout.Token);
            Assert.True(
                resuspendedSurface.RootElement.GetProperty("unit").GetProperty("isDisabled").GetBoolean());

            using var enabledSurface = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/suspension",
                CreateSuspensionRequestJson(sessionId, 5, "integration-enable-1", false),
                testTimeout.Token);
            Assert.False(
                enabledSurface.RootElement.GetProperty("unit").GetProperty("isDisabled").GetBoolean());
            Assert.Equal(6, enabledSurface.RootElement.GetProperty("revision").GetInt64());

            AssertOwnedMutationRecoveryContracts();

            var invalidSuspension = await PostAuthorizedJsonAllowingFailureAsync(
                client,
                token,
                "/game/mutations/surface/suspension",
                CreateSuspensionRequestJson(sessionId, 6, string.Empty, false),
                testTimeout.Token);
            using (invalidSuspension.Body)
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalidSuspension.StatusCode);
                Assert.Equal(
                    ProtocolErrorCodes.InvalidMessage,
                    invalidSuspension.Body.RootElement.GetProperty("error").GetString());
            }

            var staleEnable = await PostAuthorizedJsonAllowingFailureAsync(
                client,
                token,
                "/game/mutations/surface/suspension",
                CreateSuspensionRequestJson(sessionId, 5, "integration-enable-stale-revision", false),
                testTimeout.Token);
            using (staleEnable.Body)
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, staleEnable.StatusCode);
                Assert.Equal(
                    ProtocolErrorCodes.StaleRevision,
                    staleEnable.Body.RootElement.GetProperty("error").GetString());
            }

            using var idempotentEnable = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/mutations/surface/suspension",
                CreateSuspensionRequestJson(sessionId, 6, "integration-enable-idempotent-replay", false),
                testTimeout.Token);
            Assert.True(idempotentEnable.RootElement.GetProperty("idempotentReplay").GetBoolean());
            Assert.Equal(6, idempotentEnable.RootElement.GetProperty("revision").GetInt64());

            using var controlledTicks = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/experimental/ticks/run",
                JsonSerializer.Serialize(new ControlledTickExperimentRequestPayload
                {
                    TickCount = 30,
                    StepSeconds = 1d / 30d,
                    Seed = 1042,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "controlled-ticks-running",
                controlledTicks.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(30, controlledTicks.RootElement.GetProperty("requestedTicks").GetInt32());

            using var launchProbeWeapon = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/experimental/save-load/launch",
                JsonSerializer.Serialize(new ExperimentalWeaponLaunchRequestPayload
                {
                    WeaponType = "Missile",
                    AmmunitionId = "integration_missile",
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "serializable-active-weapon-launched",
                launchProbeWeapon.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                "integration_missile",
                launchProbeWeapon.RootElement.GetProperty("ammunitionId").GetString());
            Assert.Equal(1, launchProbeWeapon.RootElement.GetProperty("activeWeaponCount").GetInt32());
            Assert.True(launchProbeWeapon.RootElement.GetProperty("nativeSaveRegistered").GetBoolean());

            using var saveProbe = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/experimental/save-load/save",
                testTimeout.Token);
            Assert.Equal("completed", saveProbe.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "active-weapon-save-materialized",
                saveProbe.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                64,
                saveProbe.RootElement.GetProperty("saveFileSha256").GetString()!.Length);

            using var loadProbe = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/experimental/save-load/load",
                testTimeout.Token);
            Assert.Equal("scheduled", loadProbe.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "native-mission-reload-scheduled",
                loadProbe.RootElement.GetProperty("postcondition").GetString());

            using var projection = await GetAuthorizedJsonAsync(
                client,
                token,
                "/port-operations/projection",
                testTimeout.Token);
            Assert.Equal(
                "external-campaign-service",
                projection.RootElement.GetProperty("authority").GetString());
            Assert.Equal(1042, projection.RootElement.GetProperty("campaignRevision").GetInt64());

            using var importedDraft = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/port-operations/drafts/import",
                JsonSerializer.Serialize(new
                {
                    projectionId = "port-projection:sevastopol:blue-fleet:1042",
                    expectedCampaignRevision = 1042,
                    idempotencyKey = "integration-import-1042",
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "valid-uncommitted",
                importedDraft.RootElement.GetProperty("status").GetString());
            Assert.True(importedDraft.RootElement.GetProperty("committable").GetBoolean());
            Assert.False(importedDraft.RootElement.GetProperty("committed").GetBoolean());
            Assert.Equal(
                "external-campaign-service",
                importedDraft.RootElement.GetProperty("authority").GetString());
            Assert.Equal(
                "ready",
                importedDraft.RootElement.GetProperty("nativeMaintenanceState").GetString());

            using var exitTactical = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/tactical/exit",
                testTimeout.Token);
            Assert.Equal("scheduled", exitTactical.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "native-tactical-exit-scheduled",
                exitTactical.RootElement.GetProperty("postcondition").GetString());

            using var combatQuiesce = await campaignClient.PostAsJsonAsync(
                "/control/quiesce",
                new
                {
                    campaignId,
                    assignmentId = combatAssignmentId,
                    gameEntityId = combatGameEntityId,
                    expectedGameRevision = 1,
                    idempotencyKey = "combat-quiesce-1",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, combatQuiesce.StatusCode);
            await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 1,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                    && item.TacticalEvent.IdempotencyKey == "combat-quiesce-1"
                    && item.TacticalEvent.Destroyed);
            using var destroyedCombatView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("assignmentId").GetString()
                        == combatAssignmentId["assignment:".Length..]
                    && item.GetProperty("lifecycle").GetString() == "Released")
                    && root.GetProperty("entities").EnumerateArray().Any(item =>
                        item.GetProperty("entityId").GetString() == combatEntityId
                        && item.GetProperty("lifecycle").GetString() == "Destroyed"
                        && item.GetProperty("fidelity").GetString() == "Operational"),
                testTimeout.Token);
            var destroyedCombat = Assert.Single(
                destroyedCombatView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == combatEntityId);
            Assert.Equal(1_000_000, destroyedCombat.GetProperty("damageMilliunits").GetInt32());

            var fallbackAssignmentId = $"assignment:{Guid.Parse("40000000-0000-0000-0000-000000000005"):N}";
            const string fallbackGameEntityId = "unit:crimean-tom:fallback-knox";
            using var fallbackHandoff = await campaignClient.PostAsJsonAsync(
                "/control/handoffs",
                new
                {
                    campaignId,
                    canonicalEntityId = fallbackEntityId,
                    gameEntityId = fallbackGameEntityId,
                    assignmentId = fallbackAssignmentId,
                    authorityEpoch = 12,
                    idempotencyKey = "fallback-materialize-1",
                    requiredRuntimeFingerprintSha256 = new string('a', 64),
                    requiredCatalogFingerprintSha256 = string.Empty,
                    unitTypeId = "usn_ff_knox_84",
                    variantId = "Variant43",
                    loadoutId = "Default",
                    taskforceId = "taskforce:Blue",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, fallbackHandoff.StatusCode);
            await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 7,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                    && item.AssignmentPrepared.AssignmentId == fallbackAssignmentId);
            using var activeFallbackView = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("assignmentId").GetString()
                        == fallbackAssignmentId["assignment:".Length..]
                    && item.GetProperty("lifecycle").GetString() == "Active"),
                testTimeout.Token);
            await gamePeer;

            var crashAfterCheckpointPeer = RunGameCrashPeerAsync(
                gamePipeName,
                CrashPeerBoundary.AfterMutationBeforeProposal,
                "crash-local-only-1",
                testTimeout.Token);
            await WaitForConnectedGameAsync(client, token, testTimeout.Token);
            using var crashLocalCommand = await campaignClient.PostAsJsonAsync(
                "/control/commands",
                new OrderedTacticalCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"),
                    IdempotencyKey = "crash-local-only-1",
                    AssignmentId = fallbackAssignmentId,
                    EntityId = fallbackGameEntityId,
                    AuthorityEpoch = 12,
                    ExpectedEntityRevision = 1,
                    EffectiveTimeQuanta = 31,
                    LatitudeMicrodegrees = 13_500_000,
                    LongitudeMicrodegrees = 45_500_000,
                    HeadingMillidegrees = 180_000,
                    Telegraph = 2,
                },
                JsonOptions,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, crashLocalCommand.StatusCode);
            var localOnlyMutation = await crashAfterCheckpointPeer;
            Assert.Equal(GameCommandTypes.UpdateOwnedSurfaceUnit, localOnlyMutation.CommandType);
            Assert.Equal("crash-local-only-1", localOnlyMutation.IdempotencyKey);

            var crashBeforeCheckpointPeer = RunGameCrashPeerAsync(
                gamePipeName,
                CrashPeerBoundary.BeforeInitialCheckpoint,
                "crash-before-checkpoint-1-checkpoint",
                testTimeout.Token);
            await WaitForConnectedGameAsync(client, token, testTimeout.Token);
            await WaitForCampaignSessionAsync(campaignClient, secondCampaignSession, testTimeout.Token);
            Assert.DoesNotContain(
                ReadInboundAuthorityFrames(campaignServiceRunDirectory),
                item => item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                    && item.TacticalEvent.IdempotencyKey == "crash-local-only-1");
            using var localOnlyViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            localOnlyViewResponse.EnsureSuccessStatusCode();
            using var localOnlyView = JsonDocument.Parse(
                await localOnlyViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var unchangedFallback = Assert.Single(
                localOnlyView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == fallbackEntityId);
            Assert.NotEqual(13_500_000, unchangedFallback.GetProperty("latitudeMicrodegrees").GetInt32());

            var crashBeforeCheckpointAssignmentId =
                $"assignment:{Guid.Parse("40000000-0000-0000-0000-000000000008"):N}";
            using var crashBeforeCheckpointHandoff = await campaignClient.PostAsJsonAsync(
                "/control/handoffs",
                new
                {
                    campaignId,
                    canonicalEntityId = crashBeforeCheckpointEntityId,
                    gameEntityId = "unit:crimean-tom:crash-before-checkpoint-knox",
                    assignmentId = crashBeforeCheckpointAssignmentId,
                    authorityEpoch = 15,
                    idempotencyKey = "crash-before-checkpoint-1",
                    requiredRuntimeFingerprintSha256 = new string('a', 64),
                    requiredCatalogFingerprintSha256 = string.Empty,
                    unitTypeId = "usn_ff_knox_84",
                    variantId = "Variant43",
                    loadoutId = "Default",
                    taskforceId = "taskforce:Blue",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, crashBeforeCheckpointHandoff.StatusCode);
            var beforeCheckpointCrash = await crashBeforeCheckpointPeer;
            Assert.Equal(GameCommandTypes.CaptureOwnedSurfaceCheckpoint, beforeCheckpointCrash.CommandType);
            Assert.Equal("crash-before-checkpoint-1-checkpoint", beforeCheckpointCrash.IdempotencyKey);
            using var releasedAfterPreparationCrash = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("assignmentId").GetString()
                        == crashBeforeCheckpointAssignmentId["assignment:".Length..]
                    && item.GetProperty("lifecycle").GetString() == "Released")
                    && root.GetProperty("entities").EnumerateArray().Any(item =>
                        item.GetProperty("entityId").GetString() == crashBeforeCheckpointEntityId
                        && item.GetProperty("fidelity").GetString() == "Operational"),
                testTimeout.Token);

            string? sessionBeforeCompanionRestart = null;
            using (var healthBeforeCompanionStop = await campaignClient.GetAsync("/health", testTimeout.Token))
            {
                healthBeforeCompanionStop.EnsureSuccessStatusCode();
                using var healthBeforeCompanionStopBody = JsonDocument.Parse(
                    await healthBeforeCompanionStop.Content.ReadAsStringAsync(testTimeout.Token));
                if (healthBeforeCompanionStopBody.RootElement.TryGetProperty("session", out var session)
                    && session.ValueKind == JsonValueKind.Object
                    && session.TryGetProperty("sessionId", out var campaignSessionId)
                    && campaignSessionId.GetString() is { Length: > 0 } value)
                {
                    sessionBeforeCompanionRestart = value;
                }
            }

            using var stopBeforeFallback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stoppedCompanion = await RunControlScriptAsync(
                scriptPath,
                "stop",
                runDirectory,
                controlUrl,
                gamePipeName,
                skipBuild: true,
                stopBeforeFallback.Token);
            Assert.True(stoppedCompanion.ExitCode == 0, stoppedCompanion.FormatFailure("stop-before-fallback"));
            using var fallbackResponse = await campaignClient.PostAsync(
                $"/control/campaigns/{campaignId}/resolve-expired/270",
                content: null,
                testTimeout.Token);
            fallbackResponse.EnsureSuccessStatusCode();
            using var fallback = JsonDocument.Parse(
                await fallbackResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(270, fallback.RootElement.GetProperty("timeQuanta").GetInt64());
            var fallbackAssignments = fallback.RootElement.GetProperty("assignments").EnumerateArray().ToArray();
            Assert.Single(fallbackAssignments);
            var fallbackAssignment = Assert.Single(fallbackAssignments, item =>
                item.GetProperty("assignmentId").GetString() == fallbackAssignmentId);
            Assert.Equal(270, fallbackAssignment.GetProperty("resolvedAtQuanta").GetInt64());
            Assert.Equal(120, fallbackAssignment.GetProperty("maximumLossQuanta").GetInt64());
            Assert.Equal(
                "operational-resolution-from-last-committed-checkpoint",
                fallbackAssignment.GetProperty("resolution").GetString());
            Assert.Equal(64, fallbackAssignment.GetProperty("lastCheckpointSha256").GetString()!.Length);
            using var fallbackViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{campaignId}",
                testTimeout.Token);
            fallbackViewResponse.EnsureSuccessStatusCode();
            using var fallbackView = JsonDocument.Parse(
                await fallbackViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(
                "Released",
                Assert.Single(
                    fallbackView.RootElement.GetProperty("assignments").EnumerateArray(),
                    item => item.GetProperty("assignmentId").GetString()
                        == fallbackAssignmentId["assignment:".Length..])
                    .GetProperty("lifecycle").GetString());
            Assert.Equal(
                "Operational",
                Assert.Single(
                    fallbackView.RootElement.GetProperty("entities").EnumerateArray(),
                    item => item.GetProperty("entityId").GetString() == fallbackEntityId)
                    .GetProperty("fidelity").GetString());

            var restartedCompanion = await RunControlScriptAsync(
                scriptPath,
                "start",
                runDirectory,
                controlUrl,
                gamePipeName,
                skipBuild: true,
                testTimeout.Token,
                new Dictionary<string, string>
                {
                    ["CRIMEAN_TOM_CAMPAIGN_GRPC_URL"] = campaignGrpcUrl,
                    ["CRIMEAN_TOM_CAMPAIGN_HTTP_URL"] = campaignHttpUrl,
                    ["CRIMEAN_TOM_CAMPAIGN_ID"] = campaignId,
                    ["CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE"] = Path.Combine(campaignServiceRunDirectory, "service.token"),
                    ["CRIMEAN_TOM_CAMPAIGN_STATE_DIRECTORY"] = Path.Combine(runDirectory, "campaign-companion"),
                    ["CRIMEAN_TOM_COLLIDER_CACHE_DIR"] = colliderCacheDir,
                });
            Assert.True(restartedCompanion.ExitCode == 0, restartedCompanion.FormatFailure("start-after-fallback"));
            token = (await File.ReadAllTextAsync(tokenPath, testTimeout.Token)).Trim();
            var migrateGamePeer = RunGamePeerAsync(
                gamePipeName,
                probeSavePath,
                couplingState,
                testTimeout.Token);
            _ = migrateGamePeer.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            await WaitForCampaignSessionAsync(
                campaignClient,
                sessionBeforeCompanionRestart,
                testTimeout.Token);
            await WaitForConnectedGameAsync(client, token, testTimeout.Token);
            await ProveOperationalIslandPinnedSourcePreparationAfterGhostReleaseAsync(
                campaignClient,
                campaignHttpUrl,
                campaignId,
                fallbackEntityId,
                campaignServiceRunDirectory,
                testTimeout.Token);
            const string cooldownProbeAssignmentId = "assignment:50000000000000000000000000000001";
            using (var cooldownProbeHandoff = await campaignClient.PostAsJsonAsync(
                "/control/handoffs",
                new
                {
                    campaignId,
                    canonicalEntityId = mismatchEntityId,
                    gameEntityId = "unit:crimean-tom:mismatch-knox",
                    assignmentId = cooldownProbeAssignmentId,
                    authorityEpoch = 31,
                    idempotencyKey = "cooldown-probe-handoff-setup",
                    requiredRuntimeFingerprintSha256 = new string('a', 64),
                    requiredCatalogFingerprintSha256 = string.Empty,
                    unitTypeId = "usn_ff_knox_84",
                    variantId = "Variant43",
                    loadoutId = "Default",
                    taskforceId = "taskforce:Blue",
                },
                JsonOptions,
                testTimeout.Token))
            {
                Assert.True(
                    cooldownProbeHandoff.IsSuccessStatusCode,
                    await cooldownProbeHandoff.Content.ReadAsStringAsync(testTimeout.Token));
            }
            await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 1,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                    && item.AssignmentPrepared.AssignmentId == cooldownProbeAssignmentId
                    && item.AssignmentPrepared.Ready);
            await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("entityId").GetString() == mismatchEntityId
                    && item.GetProperty("role").GetString() == "Primary"
                    && item.GetProperty("lifecycle").GetString() == "Active"),
                testTimeout.Token);
            await ProveFinalCheckpointMigrationAsync(
                campaignClient,
                campaignHttpUrl,
                campaignGrpcUrl,
                campaignToken,
                campaignId,
                primaryEntityId,
                mismatchEntityId,
                campaignServiceRunDirectory,
                runDirectory,
                scriptPath,
                async cancellationToken =>
                {
                    campaignService!.Kill(entireProcessTree: true);
                    await campaignService.WaitForExitAsync(cancellationToken);
                    campaignService.Dispose();
                    campaignService = StartCampaignService(
                        repositoryRoot,
                        campaignServiceRunDirectory,
                        campaignGrpcUrl,
                        campaignHttpUrl,
                        MigrationCooldownEnvironment);
                    await WaitForCampaignServiceAsync(
                        campaignServiceRunDirectory,
                        campaignHttpUrl,
                        cancellationToken);
                },
                testTimeout.Token);
        }
        finally
        {
            if (campaignService is { HasExited: false })
            {
                campaignService.Kill(entireProcessTree: true);
                await campaignService.WaitForExitAsync();
            }
            campaignService?.Dispose();
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stop = await RunControlScriptAsync(
                scriptPath,
                "stop",
                runDirectory,
                controlUrl,
                gamePipeName,
                skipBuild: true,
                cleanupTimeout.Token);
            Assert.True(stop.ExitCode == 0, stop.FormatFailure("stop"));
            Assert.False(File.Exists(pidPath));
            Assert.False(File.Exists(tokenPath));
            var destinationPidPath = Path.Combine(runDirectory, "destination-worker", "companion.pid");
            if (File.Exists(destinationPidPath)
                && int.TryParse((await File.ReadAllTextAsync(destinationPidPath, cleanupTimeout.Token)).Trim(), out var destinationPid))
            {
                try
                {
                    var destinationProcess = Process.GetProcessById(destinationPid);
                    if (!destinationProcess.HasExited)
                    {
                        destinationProcess.Kill(entireProcessTree: true);
                        await destinationProcess.WaitForExitAsync(cleanupTimeout.Token);
                    }
                }
                catch
                {
                }
            }
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AssertAnonymousPipeAccessDenied(string pipeName)
    {
        if (!ImpersonateAnonymousToken(GetCurrentThread()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not impersonate the Windows anonymous token for the pipe ACL probe.");
        }

        try
        {
            using var anonymousClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            var exception = Record.Exception(() => anonymousClient.Connect(1_000));
            Assert.True(
                exception is UnauthorizedAccessException or IOException,
                $"Anonymous pipe access was not rejected by the kernel ACL: {exception}");
        }
        finally
        {
            if (!RevertToSelf())
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not restore the test process identity after the pipe ACL probe.");
            }
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateAnonymousToken(IntPtr threadHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    private static Task RunGamePeerAsync(
        string pipeName,
        string probeSavePath,
        FakeTacticalCouplingState couplingState,
        CancellationToken cancellationToken,
        FakeTacticalCheckpointState? tacticalCheckpointState = null) =>
        RunGamePeerAsync(
            pipeName,
            probeSavePath,
            couplingState,
            ownedResources: null,
            runtimeFingerprintSha256: new string('a', 64),
            catalogFingerprintSha256: "integration-catalog-sha256",
            cancellationToken,
            tacticalCheckpointState);

    private static async Task RunGamePeerAsync(
        string pipeName,
        string probeSavePath,
        FakeTacticalCouplingState couplingState,
        FakeOwnedSurfaceResourceState? ownedResources,
        CancellationToken cancellationToken) =>
        await RunGamePeerAsync(
            pipeName,
            probeSavePath,
            couplingState,
            ownedResources,
            new string('a', 64),
            "integration-catalog-sha256",
            cancellationToken);

    private static async Task RunGamePeerAsync(
        string pipeName,
        string probeSavePath,
        FakeTacticalCouplingState couplingState,
        FakeOwnedSurfaceResourceState? ownedResources,
        string runtimeFingerprintSha256,
        string catalogFingerprintSha256,
        CancellationToken cancellationToken,
        FakeTacticalCheckpointState? tacticalCheckpointState = null)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);

        var hello = new WireEnvelope
        {
            Kind = MessageKinds.Hello,
            MessageId = Guid.NewGuid().ToString("N"),
            MessageType = "game",
            PayloadJson = JsonSerializer.Serialize(new GameHelloPayload
            {
                ClientInstanceId = Guid.NewGuid().ToString("N"),
                PluginVersion = "integration",
                ProcessId = Environment.ProcessId,
                GameVersion = "integration",
                InitialScene = "IntegrationScenario",
                ExperimentalSaveDirectory = Path.GetDirectoryName(probeSavePath) ?? ".",
            }, JsonOptions),
        };
        await WriteEnvelopeAsync(pipe, hello, cancellationToken);

        var accepted = await ReadEnvelopeAsync(pipe, cancellationToken);
        Assert.Equal(MessageKinds.HelloAccepted, accepted.Kind);
        var handshake = JsonSerializer.Deserialize<HandshakeResponsePayload>(
            accepted.PayloadJson,
            JsonOptions)!;
        Assert.True(handshake.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(handshake.SessionId));

        var builderLoaded = false;
        var draftPending = false;
        ownedResources ??= new FakeOwnedSurfaceResourceState();
        tacticalCheckpointState ??= new FakeTacticalCheckpointState();
        while (true)
        {
            var command = await ReadEnvelopeAsync(pipe, cancellationToken);
            Assert.Equal(MessageKinds.Command, command.Kind);
            Assert.Equal(handshake.SessionId, command.SessionId);
            var completesPrimaryPeer = command.MessageType == GameCommandTypes.CaptureOwnedSurfaceCheckpoint
                && JsonSerializer.Deserialize<OwnedSurfaceEntityRequestPayload>(
                    command.PayloadJson,
                    JsonOptions)?.Context.IdempotencyKey == "fallback-materialize-1-checkpoint";
            if (command.MessageType == GameCommandTypes.OpenTaskForceBuilder)
            {
                builderLoaded = true;
            }
            if (command.MessageType == GameCommandTypes.AddMaintenanceDraftUnit)
            {
                draftPending = true;
            }
            if (command.MessageType == GameCommandTypes.SaveExperimentalProbe)
            {
                await File.WriteAllTextAsync(
                    probeSavePath,
                    "[File]\nSaveFile=true\n[Taskforce1Weapon1]\nType=integration_weapon\n",
                    cancellationToken);
            }
            if (command.MessageType == GameCommandTypes.CaptureGameScreenshot)
            {
                var screenshotPath = Path.Combine(
                    Path.GetDirectoryName(probeSavePath) ?? ".",
                    "_CrimeanTomScreenshot.png");
                await File.WriteAllBytesAsync(
                    screenshotPath,
                    Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="),
                    cancellationToken);
            }
            if (command.MessageType != GameCommandTypes.SetOwnedSurfaceSuspension)
            {
                ownedResources.Observe(command, handshake.SessionId!);
            }

            CommandResponsePayload? suspensionFailure = null;
            if (command.MessageType == GameCommandTypes.SetOwnedSurfaceSuspension)
            {
                suspensionFailure = ownedResources.TryObserveSuspension(
                    command.PayloadJson,
                    handshake.SessionId!);
            }

            var mutationFailure = ownedResources.TryRequireArmed(
                handshake.SessionId!,
                command.MessageType);
            if (mutationFailure is not null && command.MessageType != GameCommandTypes.SetOwnedSurfaceSuspension)
            {
                suspensionFailure = mutationFailure;
            }

            if (command.MessageType == GameCommandTypes.ArmOwnedMutationMode)
            {
                ownedResources.Arm(handshake.SessionId!);
            }

            var resultJson = command.MessageType switch
            {
                GameCommandTypes.Ping => JsonSerializer.Serialize(new PongPayload
                {
                    PluginVersion = "integration",
                    ProcessId = Environment.ProcessId,
                    InitializedMainThreadId = 1,
                    ExecutingThreadId = 1,
                    IsMainThread = true,
                    RespondedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }, JsonOptions),
                GameCommandTypes.GetCapabilities => JsonSerializer.Serialize(new CapabilitiesPayload
                {
                    ProtocolVersion = ProtocolConstants.Version,
                    SupportedCommands = GameCommandTypes.AllCommands,
                    SupportedUiAutomationCommands = GameCommandTypes.UiAutomationCommands,
                    UiAutomationEnabled = true,
                    ExperimentalRuntimeMode = "ControlledExperimental",
                    SupportedExperimentalCommands = GameCommandTypes.ExperimentalCommands,
                    MutatingCommandsEnabled = false,
                    MutationMode = "disabled",
                    CommandCancellationSupported = true,
                    SupportedPriorities = CommandPriorities.Supported,
                    ExecutionThreadGuardSelfTest = "passed",
                    PlayerTaskforceId = "taskforce:Blue",
                    OwnedSurfaceMaterializationProfiles =
                    [
                        new OwnedSurfaceMaterializationProfilePayload
                        {
                            UnitTypeId = "usn_ff_knox_84",
                            VariantId = "Variant43",
                            LoadoutId = "Default",
                        },
                    ],
                    RuntimeFingerprint = new RuntimeCapabilityFingerprintPayload
                    {
                        FingerprintSha256 = runtimeFingerprintSha256,
                        CatalogSha256 = catalogFingerprintSha256,
                    },
                }, JsonOptions),
                GameCommandTypes.GetSceneState => JsonSerializer.Serialize(new SceneStatePayload
                {
                    SceneName = "IntegrationScenario",
                    SceneLoaded = true,
                    GameMainAvailable = true,
                    ObjectsManagerAvailable = true,
                    TaskforceManagerAvailable = true,
                    ObjectCount = 1,
                    TaskforceCount = 1,
                    InitializedMainThreadId = 1,
                    ExecutingThreadId = 1,
                    IsMainThread = true,
                }, JsonOptions),
                GameCommandTypes.GetWorldSnapshot => JsonSerializer.Serialize(CreateWorldSnapshot(), JsonOptions),
                GameCommandTypes.GetTacticalCouplingState => JsonSerializer.Serialize(
                    couplingState.Capture(),
                    JsonOptions),
                GameCommandTypes.GetUnitCatalogPage => JsonSerializer.Serialize(
                    CreateUnitCatalogPage(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.GetReferenceCatalogPage => JsonSerializer.Serialize(
                    CreateReferenceCatalogPage(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.GetCatalogDiagnostics => JsonSerializer.Serialize(
                    new CatalogDiagnosticsPayload
                    {
                        State = "ready",
                        CatalogSha256 = "integration-catalog-sha256",
                        WinningFileCount = 1,
                        OverrideCount = 1,
                        ConflictingOverrideCount = 1,
                        Overrides =
                        [
                            new CatalogOverrideDiagnosticPayload
                            {
                                VirtualPath = "vessels/usn_ff_knox.ini",
                                WinningSourceId = "user",
                                WinningSha256 = new string('a', 64),
                                ShadowedSourceId = "base",
                                ShadowedSha256 = new string('b', 64),
                                Classification = "conflicting-override",
                            },
                        ],
                    },
                    JsonOptions),
                GameCommandTypes.GetCampaignManagementState => JsonSerializer.Serialize(
                    CreateCampaignManagementState(builderLoaded, draftPending),
                    JsonOptions),
                GameCommandTypes.GetUiInspection => JsonSerializer.Serialize(
                    new UiInspectionPayload
                    {
                        SceneName = "IntegrationScenario",
                        CurrentWindowType = "MissionBriefView",
                        GameMainAvailable = true,
                        MainGameViewAvailable = true,
                        ScreenWidth = 1920,
                        ScreenHeight = 1080,
                        PlayerTaskforceId = "taskforce:Blue",
                    },
                    JsonOptions),
                GameCommandTypes.CaptureGameScreenshot => JsonSerializer.Serialize(
                    new CaptureGameScreenshotResultPayload
                    {
                        Outcome = "scheduled",
                        Postcondition = "screenshot-capture-scheduled",
                        FileName = "_CrimeanTomScreenshot.png",
                        FilePath = Path.Combine(
                            Path.GetDirectoryName(probeSavePath) ?? ".",
                            "_CrimeanTomScreenshot.png"),
                        SceneName = "IntegrationScenario",
                        Width = 1,
                        Height = 1,
                        CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                    JsonOptions),
                GameCommandTypes.CaptureColliderCatalog => JsonSerializer.Serialize(
                    CreateEmptyColliderCapture(probeSavePath),
                    JsonOptions),
                GameCommandTypes.GetColliderCatalog => JsonSerializer.Serialize(
                    CreateEmptyColliderCatalog(probeSavePath),
                    JsonOptions),
                GameCommandTypes.FocusCameraOnUnit => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.FocusCameraOnUnit,
                        Outcome = "completed",
                        Postcondition = "camera-focused-on-unit",
                    },
                    JsonOptions),
                GameCommandTypes.SetTacticalHudPanel => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.SetTacticalHudPanel,
                        Outcome = "completed",
                        Postcondition = "tactical-map-visible",
                    },
                    JsonOptions),
                GameCommandTypes.OpenTaskForceBuilder => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.OpenTaskForceBuilder,
                        Outcome = "completed",
                        Postcondition = "task-force-builder-loaded-for-selected-mission",
                        Campaign = CreateCampaignManagementState(builderLoaded: true),
                    },
                    JsonOptions),
                GameCommandTypes.StartPortOperations => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.StartPortOperations,
                        Outcome = "completed",
                        Postcondition = "port-operations-campaign-load-requested",
                        CampaignPath = "campaigns/crimean-tom-port-operations/campaign.ini",
                    },
                    JsonOptions),
                GameCommandTypes.AddMaintenanceDraftUnit => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.AddMaintenanceDraftUnit,
                        Outcome = "completed",
                        Postcondition = "maintenance-unit-staged-uncommitted",
                        Campaign = CreateCampaignManagementState(
                            builderLoaded: true,
                            draftPending: true),
                    },
                    JsonOptions),
                GameCommandTypes.StartScenario => JsonSerializer.Serialize(
                    CreateScenarioActionResult(
                        command.PayloadJson,
                        GameCommandTypes.StartScenario,
                        "scenario-load-requested",
                        briefingPaused: true),
                    JsonOptions),
                GameCommandTypes.ContinueMissionBriefing => JsonSerializer.Serialize(
                    CreateScenarioActionResult(
                        command.PayloadJson,
                        GameCommandTypes.ContinueMissionBriefing,
                        "mission-running-unpaused",
                        briefingPaused: false),
                    JsonOptions),
                GameCommandTypes.ExitTacticalSession => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.ExitTacticalSession,
                        Outcome = "scheduled",
                        Postcondition = "native-tactical-exit-scheduled",
                    },
                    JsonOptions),
                GameCommandTypes.SetExperimentalPauseState => JsonSerializer.Serialize(
                    CreateExperimentalPauseResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.RunControlledTickExperiment => JsonSerializer.Serialize(
                    new ControlledTickExperimentPayload
                    {
                        State = "running",
                        Postcondition = "controlled-ticks-running",
                        HooksInstalled = true,
                        RequestedTicks = 30,
                        StepSeconds = 1d / 30d,
                        Seed = 1042,
                        SeededSources = ["Globals._rnd"],
                    },
                    JsonOptions),
                GameCommandTypes.ArmOwnedMutationMode => JsonSerializer.Serialize(
                    new ArmOwnedMutationModeResultPayload
                    {
                        Mode = "owned-mutations-armed",
                        SessionId = handshake.SessionId!,
                        ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow
                            .AddMinutes(1)
                            .ToUnixTimeMilliseconds(),
                        Postcondition = "owned-mutations-armed-for-current-session",
                    },
                    JsonOptions),
                GameCommandTypes.MaterializeOwnedSurfaceUnit => JsonSerializer.Serialize(
                    new MaterializeOwnedSurfaceUnitResultPayload
                    {
                        Outcome = "materialized",
                        Postcondition = "owned-surface-unit-materialized-and-read-back",
                        AssignmentId = "assignment:integration-surface",
                        AuthorityEpoch = 7,
                        Revision = 1,
                        IdempotencyKey = "integration-knox-1",
                        RecoverableOwnedObject = true,
                        SynchronousDurationMicroseconds = 4200,
                        RealizedUnit = new OwnedSurfaceUnitStatePayload
                        {
                            StableUnitId =
                                "campaign-unit:unit:crimean-tom:integration-knox-1",
                            GameUniqueId = 44,
                            EntityId = "unit:crimean-tom:integration-knox-1",
                            UnitTypeId = "usn_ff_knox_84",
                            VariantId = "Variant43",
                            LoadoutId = "Default",
                            TaskforceId = "taskforce:Blue",
                            Latitude = 12.25,
                            Longitude = 44.5,
                            HeadingDegrees = 90,
                            Telegraph = 0,
                        },
                    },
                    JsonOptions),
                GameCommandTypes.CaptureOwnedSurfaceCheckpoint => JsonSerializer.Serialize(
                    tacticalCheckpointState.Capture(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.UpdateOwnedSurfaceUnit => JsonSerializer.Serialize(
                    CreateLifecycleResult(command.PayloadJson, "updated", 2, disabled: false),
                    JsonOptions),
                GameCommandTypes.SetOwnedSurfaceSuspension => JsonSerializer.Serialize(
                    ownedResources.CreateSuspensionLifecycleResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.RestoreOwnedSurfaceCheckpoint => JsonSerializer.Serialize(
                    CreateRestoreLifecycleResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.StageOwnedSurfaceCheckpoint => JsonSerializer.Serialize(
                    CreateStageCheckpointLifecycleResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.GetOwnedSurfaceResourceState => JsonSerializer.Serialize(
                    ownedResources.Capture(),
                    JsonOptions),
                GameCommandTypes.GetSurfaceRenderProxyState => JsonSerializer.Serialize(
                    tacticalCheckpointState.CaptureRenderProxies(),
                    JsonOptions),
                GameCommandTypes.ReconcileSurfaceRenderProxies => JsonSerializer.Serialize(
                    tacticalCheckpointState.ReconcileRenderProxies(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery => JsonSerializer.Serialize(
                    new OwnedSurfaceRecoveryReleaseResultPayload
                    {
                        Outcome = "released",
                        Postcondition =
                            "detached-owned-recovery-record-released-without-unity-mutation",
                        EntityId = "unit:crimean-tom:integration-recovery-1",
                        AssignmentId = "assignment:integration-surface",
                        AuthorityEpoch = 7,
                        Revision = 0,
                        IdempotencyKey = "integration-release-recovery-1",
                    },
                    JsonOptions),
                GameCommandTypes.LaunchExperimentalProbeWeapon => JsonSerializer.Serialize(
                    new ExperimentalWeaponLaunchResultPayload
                    {
                        Action = GameCommandTypes.LaunchExperimentalProbeWeapon,
                        Outcome = "completed",
                        Postcondition = "serializable-active-weapon-launched",
                        UnitId = "unit:42",
                        WeaponType = "Missile",
                        AmmunitionId = "integration_missile",
                        ActiveWeaponId = "weapon:43",
                        ActiveWeaponCount = 1,
                        NativeSaveRegistered = true,
                        SerializableActiveWeaponCount = 1,
                    },
                    JsonOptions),
                GameCommandTypes.SaveExperimentalProbe => JsonSerializer.Serialize(
                    new ExperimentalSaveLoadProbeResultPayload
                    {
                        Action = GameCommandTypes.SaveExperimentalProbe,
                        Outcome = "scheduled",
                        Postcondition = "native-save-write-scheduled",
                        SaveFileName = Path.GetFileName(probeSavePath),
                        SaveFilePath = probeSavePath,
                        PreTransitionWeaponCount = 1,
                        PreTransitionWeaponsSha256 = "integration-weapons-sha256",
                        PreTransitionCanonicalSha256 = "integration-canonical-sha256",
                    },
                    JsonOptions),
                GameCommandTypes.LoadExperimentalProbe => JsonSerializer.Serialize(
                    new ExperimentalSaveLoadProbeResultPayload
                    {
                        Action = GameCommandTypes.LoadExperimentalProbe,
                        Outcome = "scheduled",
                        Postcondition = "native-mission-reload-scheduled",
                        SaveFileName = Path.GetFileName(probeSavePath),
                        SaveFilePath = probeSavePath,
                        SaveFileSha256 = new string('a', 64),
                        PreTransitionWeaponCount = 1,
                        PreTransitionWeaponsSha256 = "integration-weapons-sha256",
                        PreTransitionCanonicalSha256 = "integration-canonical-sha256",
                    },
                    JsonOptions),
                _ => throw new InvalidOperationException($"Unexpected command '{command.MessageType}'."),
            };

            await WriteEnvelopeAsync(pipe, new WireEnvelope
            {
                Kind = MessageKinds.Response,
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = command.MessageId,
                SessionId = handshake.SessionId,
                MessageType = command.MessageType,
                PayloadJson = JsonSerializer.Serialize(
                    suspensionFailure
                        ?? new CommandResponsePayload
                        {
                            Success = true,
                            ResultJson = resultJson,
                        },
                    JsonOptions),
                Priority = command.Priority,
            }, cancellationToken);
            if (completesPrimaryPeer)
            {
                return;
            }
        }
    }

    private static async Task<CrashPeerObservation> RunGameCrashPeerAsync(
        string pipeName,
        CrashPeerBoundary boundary,
        string targetIdempotencyKey,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        await WriteEnvelopeAsync(pipe, new WireEnvelope
        {
            Kind = MessageKinds.Hello,
            MessageId = Guid.NewGuid().ToString("N"),
            MessageType = "game",
            PayloadJson = JsonSerializer.Serialize(new GameHelloPayload
            {
                ClientInstanceId = Guid.NewGuid().ToString("N"),
                PluginVersion = "integration-crash-peer",
                ProcessId = Environment.ProcessId,
                GameVersion = "integration",
                InitialScene = "IntegrationScenario",
            }, JsonOptions),
        }, cancellationToken);
        var accepted = await ReadEnvelopeAsync(pipe, cancellationToken);
        var handshake = JsonSerializer.Deserialize<HandshakeResponsePayload>(
            accepted.PayloadJson,
            JsonOptions)!;
        Assert.True(handshake.Accepted);

        while (true)
        {
            var command = await ReadEnvelopeAsync(pipe, cancellationToken);
            if (command.MessageType == GameCommandTypes.UpdateOwnedSurfaceUnit)
            {
                var request = JsonSerializer.Deserialize<UpdateOwnedSurfaceUnitRequestPayload>(
                    command.PayloadJson,
                    JsonOptions)!;
                if (boundary == CrashPeerBoundary.AfterMutationBeforeProposal
                    && request.Context.IdempotencyKey == targetIdempotencyKey)
                {
                    return new CrashPeerObservation(
                        command.MessageType,
                        request.Context.IdempotencyKey,
                        request.EntityId);
                }
            }
            if (command.MessageType == GameCommandTypes.CaptureOwnedSurfaceCheckpoint)
            {
                var request = JsonSerializer.Deserialize<OwnedSurfaceEntityRequestPayload>(
                    command.PayloadJson,
                    JsonOptions)!;
                if (boundary == CrashPeerBoundary.BeforeInitialCheckpoint
                    && request.Context.IdempotencyKey == targetIdempotencyKey)
                {
                    return new CrashPeerObservation(
                        command.MessageType,
                        request.Context.IdempotencyKey,
                        request.EntityId);
                }
            }

            var resultJson = command.MessageType switch
            {
                GameCommandTypes.GetCapabilities => JsonSerializer.Serialize(new CapabilitiesPayload
                {
                    ProtocolVersion = ProtocolConstants.Version,
                    SupportedCommands = GameCommandTypes.AllCommands,
                    SupportedUiAutomationCommands = GameCommandTypes.UiAutomationCommands,
                    UiAutomationEnabled = true,
                    PlayerTaskforceId = "taskforce:Blue",
                    OwnedSurfaceMaterializationProfiles =
                    [
                        new OwnedSurfaceMaterializationProfilePayload
                        {
                            UnitTypeId = "usn_ff_knox_84",
                            VariantId = "Variant43",
                            LoadoutId = "Default",
                        },
                    ],
                    RuntimeFingerprint = new RuntimeCapabilityFingerprintPayload
                    {
                        FingerprintSha256 = new string('a', 64),
                        CatalogSha256 = "integration-catalog-sha256",
                    },
                }, JsonOptions),
                GameCommandTypes.GetTacticalCouplingState => JsonSerializer.Serialize(
                    new TacticalCouplingStatePayload
                    {
                        SceneName = "IntegrationScenario",
                        CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                    JsonOptions),
                GameCommandTypes.GetOwnedSurfaceResourceState => JsonSerializer.Serialize(
                    new OwnedSurfaceResourceStatePayload
                    {
                        MemoryReclamationSupported = false,
                        SchedulerPolicy =
                            "active-and-suspended-retained-objects-consume-materialized-capacity",
                    },
                    JsonOptions),
                GameCommandTypes.GetUiInspection => JsonSerializer.Serialize(
                    new UiInspectionPayload
                    {
                        SceneName = "IntegrationScenario",
                        CurrentWindowType = "MissionBriefView",
                        GameMainAvailable = true,
                        MainGameViewAvailable = true,
                        IsPaused = false,
                        PlayerTaskforceId = "taskforce:Blue",
                    },
                    JsonOptions),
                GameCommandTypes.SetExperimentalPauseState => JsonSerializer.Serialize(
                    CreateExperimentalPauseResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.ArmOwnedMutationMode => JsonSerializer.Serialize(
                    new ArmOwnedMutationModeResultPayload
                    {
                        Mode = "owned-mutations-armed",
                        SessionId = handshake.SessionId!,
                        ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                        Postcondition = "owned-mutations-armed-for-current-session",
                    },
                    JsonOptions),
                GameCommandTypes.MaterializeOwnedSurfaceUnit => CreateCrashPeerMaterialization(
                    command.PayloadJson),
                GameCommandTypes.CaptureOwnedSurfaceCheckpoint => JsonSerializer.Serialize(
                    CreateOwnedSurfaceCheckpoint(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.ReconcileSurfaceRenderProxies =>
                    CreateCrashPeerRenderProxyReconciliation(command.PayloadJson),
                _ => throw new InvalidOperationException(
                    $"Unexpected crash-peer command '{command.MessageType}'."),
            };
            await WriteEnvelopeAsync(pipe, new WireEnvelope
            {
                Kind = MessageKinds.Response,
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = command.MessageId,
                SessionId = handshake.SessionId,
                MessageType = command.MessageType,
                PayloadJson = JsonSerializer.Serialize(new CommandResponsePayload
                {
                    Success = true,
                    ResultJson = resultJson,
                }, JsonOptions),
                Priority = command.Priority,
            }, cancellationToken);
        }
    }

    private static string CreateCrashPeerMaterialization(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<MaterializeOwnedSurfaceUnitRequestPayload>(
            payloadJson,
            JsonOptions)!;
        return JsonSerializer.Serialize(new MaterializeOwnedSurfaceUnitResultPayload
        {
            Outcome = "materialized",
            Postcondition = "owned-surface-unit-materialized-and-read-back",
            AssignmentId = request.Context.AssignmentId,
            AuthorityEpoch = request.Context.AuthorityEpoch,
            Revision = 1,
            IdempotencyKey = request.Context.IdempotencyKey,
            RecoverableOwnedObject = true,
            SynchronousDurationMicroseconds = 4200,
            RealizedUnit = new OwnedSurfaceUnitStatePayload
            {
                StableUnitId = $"campaign-unit:{request.EntityId}",
                GameUniqueId = 55,
                EntityId = request.EntityId,
                UnitTypeId = request.UnitTypeId,
                VariantId = request.VariantId,
                LoadoutId = request.LoadoutId,
                TaskforceId = request.TaskforceId,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                HeadingDegrees = request.HeadingDegrees,
                Telegraph = request.Telegraph,
                FuelFractionMilliunits = 1_000_000,
                StoresFractionMilliunits = 1_000_000,
                DamageFractionMilliunits = 0,
            },
        }, JsonOptions);
    }

    private static string CreateCrashPeerRenderProxyReconciliation(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<ReconcileSurfaceRenderProxiesRequestPayload>(
            payloadJson,
            JsonOptions)!;
        return JsonSerializer.Serialize(new ReconcileSurfaceRenderProxiesResultPayload
        {
            Outcome = "reconciled",
            Postcondition =
                "friendly-campaign-render-proxies-visible-without-simulation-authority",
            CampaignId = request.CampaignId,
            RequestedCount = request.Proxies.Length,
        }, JsonOptions);
    }

    private static OwnedMutationContextPayload CreateOwnedContext(
        string sessionId,
        long revision,
        string idempotencyKey) => new()
        {
            SessionId = sessionId,
            AssignmentId = "assignment:integration-surface",
            AuthorityEpoch = 7,
            ExpectedRevision = revision,
            IdempotencyKey = idempotencyKey,
        };

    private static string CreateOwnedEntityRequestJson(
        string sessionId,
        long revision,
        string idempotencyKey) => JsonSerializer.Serialize(
            new OwnedSurfaceEntityRequestPayload
            {
                Context = CreateOwnedContext(sessionId, revision, idempotencyKey),
                EntityId = "unit:crimean-tom:integration-knox-1",
            },
            JsonOptions);

    private static string CreateSuspensionRequestJson(
        string sessionId,
        long revision,
        string idempotencyKey,
        bool isSuspended) => JsonSerializer.Serialize(
            new SetOwnedSurfaceSuspensionRequestPayload
            {
                Context = CreateOwnedContext(sessionId, revision, idempotencyKey),
                EntityId = "unit:crimean-tom:integration-knox-1",
                IsSuspended = isSuspended,
            },
            JsonOptions);

    private static OwnedSurfaceUnitStatePayload CreateOwnedSurfaceState(bool disabled) => new()
    {
        StableUnitId = "campaign-unit:unit:crimean-tom:integration-knox-1",
        GameUniqueId = 44,
        EntityId = "unit:crimean-tom:integration-knox-1",
        UnitTypeId = "usn_ff_knox_84",
        VariantId = "Variant43",
        LoadoutId = "Default",
        TaskforceId = "taskforce:Blue",
        Latitude = 12.26,
        Longitude = 44.51,
        HeadingDegrees = 100,
        Telegraph = 1,
        FuelFractionMilliunits = 1_000_000,
        StoresFractionMilliunits = 1_000_000,
        DamageFractionMilliunits = 0,
        IsDisabled = disabled,
    };

    private static OwnedSurfaceCheckpointPayload CreateOwnedSurfaceCheckpoint(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<OwnedSurfaceEntityRequestPayload>(payloadJson, JsonOptions)!;
        var unit = CreateOwnedSurfaceState(disabled: false);
        if (request.Context.IdempotencyKey.StartsWith("scheduler-quiesce-", StringComparison.Ordinal))
        {
            unit.FuelFractionMilliunits = 800_000;
            unit.StoresFractionMilliunits = 600_000;
            unit.DamageFractionMilliunits = 250_000;
        }
        else if (request.Context.IdempotencyKey == "combat-quiesce-1-checkpoint")
        {
            unit.FuelFractionMilliunits = 700_000;
            unit.StoresFractionMilliunits = 400_000;
            unit.DamageFractionMilliunits = 1_000_000;
            unit.IsDestroyed = true;
        }
        return new OwnedSurfaceCheckpointPayload
        {
            CheckpointId = $"checkpoint:{request.EntityId}:{request.Context.ExpectedRevision}",
            CheckpointSha256 = new string('c', 64),
            AssignmentId = request.Context.AssignmentId,
            AuthorityEpoch = request.Context.AuthorityEpoch,
            Revision = request.Context.ExpectedRevision,
            Unit = unit,
        };
    }

    private sealed class FakeTacticalCheckpointState
    {
        private readonly object _gate = new();
        private string _checkpointSha256 = new('c', 64);
        private double _latitude = 12.26;
        private double _longitude = 44.51;
        private double _headingDegrees = 100;
        private int _telegraph = 1;
        private long _fuelFractionMilliunits = 1_000_000;
        private long _storesFractionMilliunits = 1_000_000;
        private int _damageFractionMilliunits;
        private ReconcileSurfaceRenderProxiesRequestPayload? _renderProxyRequest;

        public void SetObservedState(
            string checkpointSha256,
            double latitude,
            double longitude,
            double headingDegrees,
            int telegraph,
            long fuelFractionMilliunits,
            long storesFractionMilliunits,
            int damageFractionMilliunits)
        {
            lock (_gate)
            {
                _checkpointSha256 = checkpointSha256;
                _latitude = latitude;
                _longitude = longitude;
                _headingDegrees = headingDegrees;
                _telegraph = telegraph;
                _fuelFractionMilliunits = fuelFractionMilliunits;
                _storesFractionMilliunits = storesFractionMilliunits;
                _damageFractionMilliunits = damageFractionMilliunits;
            }
        }

        public OwnedSurfaceCheckpointPayload Capture(string payloadJson)
        {
            var checkpoint = CreateOwnedSurfaceCheckpoint(payloadJson);
            var request = JsonSerializer.Deserialize<OwnedSurfaceEntityRequestPayload>(
                payloadJson,
                JsonOptions)!;
            if (!request.Context.IdempotencyKey.StartsWith("state-sync-read-", StringComparison.Ordinal))
            {
                return checkpoint;
            }

            lock (_gate)
            {
                checkpoint.CheckpointSha256 = _checkpointSha256;
                checkpoint.Unit.Latitude = _latitude;
                checkpoint.Unit.Longitude = _longitude;
                checkpoint.Unit.HeadingDegrees = _headingDegrees;
                checkpoint.Unit.Telegraph = _telegraph;
                checkpoint.Unit.FuelFractionMilliunits = _fuelFractionMilliunits;
                checkpoint.Unit.StoresFractionMilliunits = _storesFractionMilliunits;
                checkpoint.Unit.DamageFractionMilliunits = _damageFractionMilliunits;
                return checkpoint;
            }
        }

        public ReconcileSurfaceRenderProxiesResultPayload ReconcileRenderProxies(string payloadJson)
        {
            var request = JsonSerializer.Deserialize<ReconcileSurfaceRenderProxiesRequestPayload>(
                payloadJson,
                JsonOptions)!;
            lock (_gate)
            {
                _renderProxyRequest = request;
            }
            return new ReconcileSurfaceRenderProxiesResultPayload
            {
                Outcome = "reconciled",
                Postcondition =
                    "friendly-campaign-render-proxies-visible-without-simulation-authority",
                CampaignId = request.CampaignId,
                RequestedCount = request.Proxies.Length,
                CreatedCount = request.Proxies.Length,
                Proxies = request.Proxies.Select(item => new SurfaceRenderProxyStatePayload
                {
                    EntityId = item.EntityId,
                    StableUnitId = $"campaign-unit:render-proxy:{request.CampaignId}:{item.EntityId}",
                    GameUniqueId = null,
                    IsVisible = true,
                    IsSimulated = false,
                    RepresentationKind = "visual-model-and-map-annotation",
                    HasNativeSimulationObject = false,
                    WorldModelVisible = true,
                    MapMarkerVisible = true,
                    Latitude = item.Latitude,
                    Longitude = item.Longitude,
                    HeadingDegrees = item.HeadingDegrees,
                    CampaignTelegraph = item.CampaignTelegraph,
                }).ToArray(),
            };
        }

        public SurfaceRenderProxySnapshotPayload CaptureRenderProxies()
        {
            lock (_gate)
            {
                var request = _renderProxyRequest
                    ?? throw new InvalidOperationException("Render proxies were not reconciled.");
                return new SurfaceRenderProxySnapshotPayload
                {
                    RepresentationProvider = "visual-only-preferred",
                    CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Proxies = request.Proxies.Select(item => new SurfaceRenderProxyStatePayload
                    {
                        EntityId = item.EntityId,
                        StableUnitId =
                            $"campaign-unit:render-proxy:{request.CampaignId}:{item.EntityId}",
                        IsVisible = true,
                        IsSimulated = false,
                        RepresentationKind = "visual-model-and-map-annotation",
                        HasNativeSimulationObject = false,
                        WorldModelVisible = true,
                        MapMarkerVisible = true,
                        Latitude = item.Latitude,
                        Longitude = item.Longitude,
                        HeadingDegrees = item.HeadingDegrees,
                        CampaignTelegraph = item.CampaignTelegraph,
                    }).ToArray(),
                };
            }
        }

        public async Task<ReconcileSurfaceRenderProxiesRequestPayload> WaitForRenderProxyCountAsync(
            int expectedCount,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_renderProxyRequest is { } request
                        && request.Proxies.Length == expectedCount)
                    {
                        return request;
                    }
                }
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static OwnedSurfaceLifecycleResultPayload CreateLifecycleResult(
        string payloadJson,
        string outcome,
        long revision,
        bool disabled)
    {
        var request = JsonSerializer.Deserialize<UpdateOwnedSurfaceUnitRequestPayload>(
            payloadJson,
            JsonOptions)!;
        return new OwnedSurfaceLifecycleResultPayload
        {
            Outcome = outcome,
            Postcondition = "owned-surface-unit-updated-and-read-back",
            AssignmentId = request.Context.AssignmentId,
            AuthorityEpoch = request.Context.AuthorityEpoch,
            Revision = revision,
            IdempotencyKey = request.Context.IdempotencyKey,
            Unit = CreateOwnedSurfaceState(disabled),
        };
    }

    private static OwnedSurfaceLifecycleResultPayload CreateRestoreLifecycleResult(
        string payloadJson)
    {
        var request = JsonSerializer.Deserialize<RestoreOwnedSurfaceCheckpointRequestPayload>(
            payloadJson,
            JsonOptions)!;
        return new OwnedSurfaceLifecycleResultPayload
        {
            Outcome = "restored-from-checkpoint",
            Postcondition = "owned-surface-unit-reenabled-from-portable-checkpoint",
            AssignmentId = request.Context.AssignmentId,
            AuthorityEpoch = request.Context.AuthorityEpoch,
            Revision = request.Context.ExpectedRevision + 1,
            IdempotencyKey = request.Context.IdempotencyKey,
            SourceCheckpointId = request.CheckpointId,
            SourceCheckpointSha256 = request.CheckpointSha256,
            Unit = CreateOwnedSurfaceState(disabled: false),
        };
    }

    private static OwnedSurfaceLifecycleResultPayload CreateStageCheckpointLifecycleResult(
        string payloadJson)
    {
        var request = JsonSerializer.Deserialize<StageOwnedSurfaceCheckpointRequestPayload>(
            payloadJson,
            JsonOptions)!;
        var unit = CreateOwnedSurfaceState(disabled: true);
        unit.EntityId = request.EntityId;
        unit.StableUnitId = $"campaign-unit:{request.EntityId}";
        return new OwnedSurfaceLifecycleResultPayload
        {
            Outcome = "migration-checkpoint-staged",
            Postcondition = "owned-surface-unit-remains-suspended-with-verified-migration-checkpoint",
            AssignmentId = request.Context.AssignmentId,
            AuthorityEpoch = request.Context.AuthorityEpoch,
            Revision = request.Context.ExpectedRevision + 1,
            IdempotencyKey = request.Context.IdempotencyKey,
            SourceCheckpointId = request.SourceCheckpoint.CheckpointId,
            SourceCheckpointSha256 = request.SourceCheckpoint.CheckpointSha256,
            Unit = unit,
        };
    }

    private static CaptureColliderCatalogResultPayload CreateEmptyColliderCapture(string probeSavePath)
    {
        var catalog = CreateEmptyColliderCatalog(probeSavePath);
        return new CaptureColliderCatalogResultPayload
        {
            SchemaVersion = catalog.SchemaVersion,
            CacheDirectory = catalog.CacheDirectory,
            GameVersion = catalog.GameVersion,
            ScannedObjectCount = 0,
            CapturedUnitCount = 0,
            MemoryCount = catalog.MemoryCount,
            FileCount = catalog.FileCount,
            Postcondition = "no-unit-colliders-captured",
            Entries = catalog.Entries,
        };
    }

    private static GetColliderCatalogResultPayload CreateEmptyColliderCatalog(string probeSavePath) =>
        new()
        {
            SchemaVersion = ColliderGeometryCache.SchemaVersion,
            CacheDirectory = Path.Combine(Path.GetDirectoryName(probeSavePath) ?? ".", "collider-catalog"),
            GameVersion = "integration",
            MemoryCount = 0,
            FileCount = 0,
            Entries = [],
        };

    private static WorldSnapshotPayload CreateWorldSnapshot()
    {
        return new WorldSnapshotPayload
        {
            SceneName = "IntegrationScenario",
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CanonicalSha256 = "integration-canonical-sha256",
            Taskforces =
            [
                new TaskforceSnapshotPayload
                {
                    Id = "taskforce:Blue",
                    MissionName = "Blue",
                    Side = "Player",
                    Nation = "us",
                    UnitIds = ["unit:42"],
                },
            ],
            Units =
            [
                new UnitSnapshotPayload
                {
                    Id = "unit:42",
                    GameUniqueId = 42,
                    TaskforceId = "taskforce:Blue",
                    Name = "USS Integration",
                    ClassName = "Test Ship",
                    Type = "Vessel",
                    Nation = "us",
                    Latitude = 35,
                    Longitude = 20,
                    HeadingDegrees = 90,
                    SpeedKnots = 18,
                },
            ],
            Contacts =
            [
                new ContactSnapshotPayload
                {
                    Id = "player-track:7001",
                    TrackNumber = 7001,
                    Relationship = "Hostile",
                    PositionState = "estimated",
                    Latitude = 35.5,
                    Longitude = 20.5,
                    PositionMajorStdDevMeters = 1200,
                    PositionMinorStdDevMeters = 350,
                    DetectingSensors = "PassiveSonar",
                    ObservationSha256 = "integration-contact-sha256",
                    TrackSources =
                    [
                        new ContactTrackSourcePayload
                        {
                            SensorType = "PassiveSonar",
                            SensorDisplayName = "AN/SQS-26",
                            DetectionKind = "Subsurface",
                            Confidence = 0.75,
                            ObservationCount = 3,
                        },
                    ],
                },
            ],
        };
    }

    private static CampaignManagementStatePayload CreateCampaignManagementState(
        bool builderLoaded,
        bool draftPending = false)
    {
        return new CampaignManagementStatePayload
        {
            SceneName = "LinearCampaign",
            LinearCampaignViewAvailable = true,
            LinearCampaignMapAvailable = true,
            IsTaskForceModeCampaign = true,
            HasSelectedCampaignEvent = true,
            SelectedCampaignEventName = "Port Operations",
            TaskForceBuilderVisible = true,
            TaskForceBuilderEnabled = true,
            TaskForceBuilderTargetMissionIndex = builderLoaded ? 1 : -1,
            TaskForceBuilderLoaded = builderLoaded,
            TaskForceName = "Integration Task Force",
            AvailablePoints = 500,
            PointCap = 500,
            PurchasedUnitCount = 1,
            VisibleTaskForceUnitCount = 1,
            AvailableRosterVariantCount = builderLoaded ? 12 : 0,
            HasShipRosterUnits = builderLoaded,
            HasSubmarineRosterUnits = builderLoaded,
            HasAirRosterUnits = builderLoaded,
            PurchasedUnits = builderLoaded
                ?
                [
                    new CampaignMaintenanceUnitPayload
                    {
                        InstanceId = "port-unit:1",
                        UnitFolder = "vessels",
                        UnitType = "usn_ff_knox_84",
                        Category = "Ship",
                        VariantReference = "Variant43",
                        DisplayName = "USS Integration",
                        Nation = "us",
                        SelectedLoadoutVariant = "Default",
                        Quantity = 1,
                        Cost = 120,
                    },
                ]
                : [],
            PendingUnitCount = draftPending ? 1 : 0,
            PendingPurchaseCost = draftPending ? 135 : 0,
            HasPendingTransactions = draftPending,
            PendingUnits = draftPending
                ?
                [
                    new CampaignMaintenanceUnitPayload
                    {
                        InstanceId = "pending-port-unit:1",
                        UnitFolder = "vessels",
                        UnitType = "usn_ffg_oliver_hazard_perry",
                        Category = "Ship",
                        VariantReference = "Variant17",
                        DisplayName = "USS Integration Reinforcement",
                        Nation = "us",
                        SelectedLoadoutVariant = "Default",
                        Quantity = 1,
                        Cost = 135,
                    },
                ]
                : [],
            NativeMaintenanceState = "ready",
            NativeRearmAvailable = true,
            NativeMaintenanceUnits =
            [
                new CampaignNativeMaintenanceStatePayload
                {
                    InstanceId = "port-unit:1",
                    UnitType = "usn_ff_knox_84",
                    DisplayName = "USS Integration",
                    DamageSummary = "No Damage",
                    RepairStatus = "not-needed",
                    HasRearmedBadge = true,
                    Ammunition =
                    [
                        new CampaignNativeAmmunitionStatePayload
                        {
                            Reference = "usn_rim66e_sm1mr",
                            NativeSummary = "SM-1MR: 20/20",
                        },
                    ],
                },
            ],
        };
    }

    private static UnitCatalogPagePayload CreateUnitCatalogPage(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<UnitCatalogPageRequestPayload>(
            payloadJson,
            JsonOptions)!;
        Assert.Equal(0, request.Offset);
        Assert.Equal(5, request.Limit);
        Assert.Equal("vessels", request.Folder);
        return new UnitCatalogPagePayload
        {
            State = "ready",
            CatalogSha256 = new string('a', 64),
            TotalRecords = 1,
            Offset = request.Offset,
            Limit = request.Limit,
            Records =
            [
                new UnitCatalogRecordPayload
                {
                    Id = "unit:vessels/usn_ff_knox",
                    RuntimeMetadataId = 42,
                    UnitFolder = "vessels",
                    UnitTypeId = "usn_ff_knox",
                    ObjectType = "Vessel",
                    DefaultName = "USS Integration",
                    ClassName = "Knox",
                    DefaultNation = "us",
                    SourceId = "base",
                    SourceFileSha256 = new string('b', 64),
                    Variants =
                    [
                        new UnitCatalogVariantPayload
                        {
                            Id = "Variant1",
                            Index = 1,
                            Name = "USS Integration",
                            Nation = "us",
                            ServiceEntryYear = 1969,
                            ServiceExitYear = 1999,
                        },
                    ],
                },
            ],
        };
    }

    private static ReferenceCatalogPagePayload CreateReferenceCatalogPage(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<ReferenceCatalogPageRequestPayload>(
            payloadJson,
            JsonOptions)!;
        Assert.Equal("ammunition", request.Kind);
        Assert.Equal(5, request.Limit);
        return new ReferenceCatalogPagePayload
        {
            State = "ready",
            CatalogSha256 = new string('a', 64),
            Kind = request.Kind,
            TotalRecords = 1,
            Offset = request.Offset,
            Limit = request.Limit,
            Records =
            [
                new ReferenceCatalogRecordPayload
                {
                    Id = "ammunition:rim-7m",
                    Kind = "ammunition",
                    Name = "rim-7m",
                    DisplayName = "RIM-7M Sea Sparrow",
                    Type = "Missile",
                    GuidanceType = "SemiActiveRadarHoming",
                    SourceId = "base",
                    VirtualPath = "ammunition/rim-7m.ini",
                    SourceFileSha256 = new string('c', 64),
                },
            ],
        };
    }

    private static UiAutomationActionResultPayload CreateScenarioActionResult(
        string payloadJson,
        string action,
        string postcondition,
        bool briefingPaused)
    {
        if (string.Equals(action, GameCommandTypes.StartScenario, StringComparison.Ordinal))
        {
            var request = JsonSerializer.Deserialize<StartScenarioRequestPayload>(
                payloadJson,
                JsonOptions)!;
            Assert.Equal("scenario:missions/intro/integration", request.ScenarioId);
        }

        return new UiAutomationActionResultPayload
        {
            Action = action,
            Outcome = "completed",
            Postcondition = postcondition,
            Scenario = new ScenarioAutomationStatePayload
            {
                SceneName = "IntegrationScenario",
                CurrentWindowType = "MissionBriefView",
                ScenarioId = "scenario:missions/intro/integration",
                ScenarioPath = "missions/intro/integration.ini",
                BriefingVisible = true,
                BriefingPaused = briefingPaused,
                BriefingCanContinue = briefingPaused,
                GameMainAvailable = true,
            },
        };
    }

    private static ExperimentalPauseResultPayload CreateExperimentalPauseResult(
        string payloadJson)
    {
        var request = JsonSerializer.Deserialize<ExperimentalPauseRequestPayload>(
            payloadJson,
            JsonOptions)!;
        return new ExperimentalPauseResultPayload
        {
            RequestedPaused = request.IsPaused,
            IsPaused = request.IsPaused,
            SimulationTimeSeconds = 10,
            TimeCompression = 1,
            Postcondition = request.IsPaused
                ? "tactical-time-paused"
                : "tactical-time-running",
        };
    }

    private static async Task WaitForConnectedGameAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var health = await GetAuthorizedJsonAsync(
                client,
                token,
                "/health",
                cancellationToken);
            if (health.RootElement.GetProperty("gameConnected").GetBoolean())
            {
                Assert.Equal(
                    "connected",
                    health.RootElement.GetProperty("connectionState").GetString());
                Assert.True(health.RootElement.GetProperty("sessionGeneration").GetInt64() >= 1);
                return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task<JsonDocument> GetAuthorizedJsonAsync(
        HttpClient client,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ControlTokenHeader, token);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {path} returned {response.StatusCode}: "
            + await response.Content.ReadAsStringAsync(cancellationToken));
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static async Task<JsonDocument> PostAuthorizedJsonAsync(
        HttpClient client,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ControlTokenHeader, token);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"POST {path} returned {response.StatusCode}: "
            + await response.Content.ReadAsStringAsync(cancellationToken));
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static async Task<(HttpStatusCode StatusCode, JsonDocument Body)> PostAuthorizedJsonAllowingFailureAsync(
        HttpClient client,
        string token,
        string path,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ControlTokenHeader, token);
        using var response = await client.SendAsync(request, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (response.StatusCode, await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken));
    }

    private static async Task<JsonDocument> PostAuthorizedJsonWithBodyAsync(
        HttpClient client,
        string token,
        string path,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ControlTokenHeader, token);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"POST {path} returned {response.StatusCode}: "
            + await response.Content.ReadAsStringAsync(cancellationToken));
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static async Task AssertUnauthorizedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        Assert.Equal(
            "invalid-control-token",
            document.RootElement.GetProperty("error").GetString());
    }

    private static async Task<WireEnvelope> ReadEnvelopeAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var json = await PipeFrameCodec.ReadAsync(
            pipe,
            ProtocolConstants.MaximumFrameBytes,
            cancellationToken);
        Assert.NotNull(json);
        return JsonSerializer.Deserialize<WireEnvelope>(json, JsonOptions)!;
    }

    private static Task WriteEnvelopeAsync(
        Stream pipe,
        WireEnvelope envelope,
        CancellationToken cancellationToken)
    {
        return PipeFrameCodec.WriteAsync(
            pipe,
            JsonSerializer.Serialize(envelope, JsonOptions),
            ProtocolConstants.MaximumFrameBytes,
            cancellationToken);
    }

    private static Process StartCampaignService(
        string repositoryRoot,
        string runDirectory,
        string grpcUrl,
        string httpUrl,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var normalHostPath = Path.Combine(
            repositoryRoot,
            "src",
            "CrimeanTom.Campaign.ServiceHost",
            "bin",
            "Debug",
            "net8.0",
            "CrimeanTom.Campaign.ServiceHost.dll");
        var artifactsHostPath = ResolveArtifactsProjectDll("CrimeanTom.Campaign.ServiceHost");
        var hostPath = artifactsHostPath ?? normalHostPath;
        Assert.True(File.Exists(hostPath), $"Campaign service host was not built at {hostPath}.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(hostPath);
        start.Environment["CRIMEAN_TOM_CAMPAIGN_RUN_DIRECTORY"] = runDirectory;
        start.Environment["CRIMEAN_TOM_CAMPAIGN_GRPC_URL"] = grpcUrl;
        start.Environment["CRIMEAN_TOM_CAMPAIGN_HTTP_URL"] = httpUrl;
        start.Environment["CRIMEAN_TOM_UNATTENDED_SIMULATION"] = "0";
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                start.Environment[key] = value;
            }
        }
        Directory.CreateDirectory(runDirectory);
        var outputPath = Path.Combine(runDirectory, "service.stdout.log");
        var outputGate = new object();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AppendLine(args.Data);
        process.ErrorDataReceived += (_, args) => AppendLine(args.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;

        void AppendLine(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (outputGate)
            {
                File.AppendAllText(outputPath, line + Environment.NewLine);
            }
        }
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

    private static async Task ProveConcurrentCampaignSessionsAsync(
        HttpClient campaignClient,
        string campaignGrpcUrl,
        string campaignToken,
        string campaignId,
        string primarySessionId,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(campaignGrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
        });
        var transport = new CampaignCompanionTransport.CampaignCompanionTransportClient(channel);
        using var call = transport.Connect(
            new Metadata { { "authorization", $"Bearer {campaignToken}" } },
            cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 1,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = "20000000000000000000000000000002",
                GameSessionId = "container-worker-probe",
                CampaignId = campaignId,
                ReconnectCommitPosition = 0,
                RuntimeFingerprintSha256 = new string('2', 64),
                CatalogFingerprintSha256 = new string('3', 64),
            },
        });
        Assert.True(await call.ResponseStream.MoveNext(cancellationToken));
        Assert.Equal(ServiceFrame.PayloadOneofCase.Welcome, call.ResponseStream.Current.PayloadCase);
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);

        await call.RequestStream.CompleteAsync();
        call.Dispose();
        await WaitForCampaignSessionCountAsync(campaignClient, 1, cancellationToken);
        Assert.Equal(
            primarySessionId,
            await WaitForCampaignSessionAsync(campaignClient, null, cancellationToken));
    }

    private sealed record PinnedSourceWorkerPreparationProof(
        string AssignmentId,
        long AuthorityEpoch,
        string CheckpointSha256,
        long CheckpointLength);

    private static async Task ProveOperationalIslandPinnedSourcePreparationAfterGhostReleaseAsync(
        HttpClient campaignClient,
        string campaignHttpUrl,
        string campaignId,
        string canonicalEntityId,
        string serviceRunDirectory,
        CancellationToken cancellationToken)
    {
        var gameEntityId = $"unit:crimean-tom:{canonicalEntityId}";
        using var operationalView = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        operationalView.EnsureSuccessStatusCode();
        using var operationalBody = JsonDocument.Parse(
            await operationalView.Content.ReadAsStringAsync(cancellationToken));
        var entity = Assert.Single(
            operationalBody.RootElement.GetProperty("entities").EnumerateArray(),
            item => item.GetProperty("entityId").GetString() == canonicalEntityId);
        Assert.Equal("Operational", entity.GetProperty("fidelity").GetString());
        Assert.DoesNotContain(
            operationalBody.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("entityId").GetString() == canonicalEntityId
                && item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() != "Released");

        var sourceClientId = Assert.Single(
                operationalBody.RootElement.GetProperty("workers").EnumerateArray())
            .GetProperty("clientInstanceId").GetString()!;

        using var handoff = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-source",
            new
            {
                campaignId,
                canonicalEntityId,
                preferredWorkerClientInstanceId = sourceClientId,
                idempotencyKey = "operational-ghost-release-source-prep",
            },
            cancellationToken);
        Assert.True(
            handoff.IsSuccessStatusCode,
            await handoff.Content.ReadAsStringAsync(cancellationToken));
        using var handoffBody = JsonDocument.Parse(await handoff.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "source-handoff-queued",
            handoffBody.RootElement.GetProperty("phase").GetString());

        using var activeView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                item.GetProperty("entityId").GetString() == canonicalEntityId
                && item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Active"
                && item.GetProperty("workerClientInstanceId").GetString() == sourceClientId)
                && root.GetProperty("entities").EnumerateArray().Any(item =>
                    item.GetProperty("entityId").GetString() == canonicalEntityId
                    && item.GetProperty("fidelity").GetString() == "Tactical"),
            cancellationToken);
        var activeAssignmentId = Assert.Single(
            activeView.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("entityId").GetString() == canonicalEntityId
                && item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Active")
            .GetProperty("assignmentId").GetString()!;
        var scopedAssignmentId = $"assignment:{activeAssignmentId}";

        await WaitForInboundFramesAsync(
            serviceRunDirectory,
            count: 1,
            cancellationToken,
            required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                && item.AssignmentPrepared.AssignmentId == scopedAssignmentId
                && item.AssignmentPrepared.Ready);
        var prepared = ReadInboundAuthorityFrames(serviceRunDirectory)
            .Last(item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                && item.AssignmentPrepared.AssignmentId == scopedAssignmentId
                && item.AssignmentPrepared.Ready)
            .AssignmentPrepared;
        Assert.Equal(gameEntityId, prepared.EntityId);
        Assert.Contains(
            ReadInboundAuthorityFrames(serviceRunDirectory),
            item => item.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished
                && item.CheckpointPublished.AssignmentId == scopedAssignmentId);

        using var alreadyActive = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-source",
            new
            {
                campaignId,
                canonicalEntityId,
                preferredWorkerClientInstanceId = sourceClientId,
                idempotencyKey = "operational-ghost-release-source-prep-idempotent",
            },
            cancellationToken);
        Assert.True(
            alreadyActive.IsSuccessStatusCode,
            await alreadyActive.Content.ReadAsStringAsync(cancellationToken));
        using var alreadyActiveBody = JsonDocument.Parse(
            await alreadyActive.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "source-already-active",
            alreadyActiveBody.RootElement.GetProperty("phase").GetString());
    }

    private static async Task<PinnedSourceWorkerPreparationProof> ProvePinnedSourceWorkerPreparationAsync(
        HttpClient campaignClient,
        string campaignHttpUrl,
        string campaignId,
        string primaryEntityId,
        string serviceRunDirectory,
        CancellationToken cancellationToken)
    {
        var gameEntityId = $"unit:crimean-tom:{primaryEntityId}";
        using var view = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        view.EnsureSuccessStatusCode();
        using var viewBody = JsonDocument.Parse(await view.Content.ReadAsStringAsync(cancellationToken));
        var sourceClientId = Assert.Single(
                viewBody.RootElement.GetProperty("workers").EnumerateArray())
            .GetProperty("clientInstanceId").GetString()!;
        Assert.DoesNotContain(
            viewBody.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() != "Released");

        using (var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(campaignHttpUrl),
            Timeout = TimeSpan.FromSeconds(10),
        })
        {
            using var unauthorized = await anonymous.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/prepare-source",
                new
                {
                    campaignId,
                    canonicalEntityId = primaryEntityId,
                    preferredWorkerClientInstanceId = sourceClientId,
                    idempotencyKey = "source-unauth",
                },
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        using var handoff = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-source",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                preferredWorkerClientInstanceId = sourceClientId,
                idempotencyKey = "source-prep-handoff",
            },
            cancellationToken);
        Assert.True(
            handoff.IsSuccessStatusCode,
            await handoff.Content.ReadAsStringAsync(cancellationToken));
        using var handoffBody = JsonDocument.Parse(await handoff.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            sourceClientId,
            handoffBody.RootElement.GetProperty("preferredWorkerClientInstanceId").GetString());
        Assert.Equal(
            "source-handoff-queued",
            handoffBody.RootElement.GetProperty("phase").GetString());

        var assignmentFrames = await WaitForInboundFramesAsync(
            serviceRunDirectory,
            count: 2,
            cancellationToken);
        var prepared = Assert.Single(assignmentFrames, item =>
            item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared).AssignmentPrepared;
        Assert.True(prepared.Ready, prepared.ErrorMessage);
        var assignmentId = prepared.AssignmentId;
        var authorityEpoch = prepared.AuthorityEpoch;
        Assert.Equal(gameEntityId, prepared.EntityId);
        var checkpointPublished = Assert.Single(assignmentFrames, item =>
            item.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished).CheckpointPublished;
        Assert.Equal(assignmentId, checkpointPublished.AssignmentId);

        using var activeView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                item.GetProperty("entityId").GetString() == primaryEntityId
                && item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Active"
                && item.GetProperty("workerClientInstanceId").GetString() == sourceClientId),
            cancellationToken);
        Assert.Contains(
            activeView.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("assignmentId").GetString() == assignmentId["assignment:".Length..]
                && item.GetProperty("lifecycle").GetString() == "Active");
        Assert.Equal(
            "Tactical",
            Assert.Single(
                activeView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == primaryEntityId)
                .GetProperty("fidelity").GetString());

        using var alreadyActive = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-source",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                preferredWorkerClientInstanceId = sourceClientId,
                idempotencyKey = "source-prep-idempotent",
            },
            cancellationToken);
        Assert.True(
            alreadyActive.IsSuccessStatusCode,
            await alreadyActive.Content.ReadAsStringAsync(cancellationToken));
        using var alreadyActiveBody = JsonDocument.Parse(
            await alreadyActive.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            sourceClientId,
            alreadyActiveBody.RootElement.GetProperty("preferredWorkerClientInstanceId").GetString());
        Assert.Equal(
            "source-already-active",
            alreadyActiveBody.RootElement.GetProperty("phase").GetString());

        using var unavailable = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-source",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                preferredWorkerClientInstanceId = "20000000000000000000000000000002",
                idempotencyKey = "source-prep-unavailable",
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "source-worker-unavailable",
            unavailableBody.RootElement.GetProperty("error").GetString());

        return new PinnedSourceWorkerPreparationProof(
            assignmentId,
            authorityEpoch,
            checkpointPublished.CheckpointSha256,
            checkpointPublished.CheckpointLength);
    }

    private static async Task ProveShadowDestinationPreparationAsync(
        HttpClient campaignClient,
        string campaignHttpUrl,
        string campaignGrpcUrl,
        string campaignToken,
        string campaignId,
        string primaryEntityId,
        CancellationToken cancellationToken)
    {
        const string destinationClientId = "20000000000000000000000000000002";
        using (var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(campaignHttpUrl),
            Timeout = TimeSpan.FromSeconds(10),
        })
        {
            using var unauthorized = await anonymous.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/prepare-shadow",
                new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "shadow-unauth" },
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        using var sameWorker = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-shadow",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "shadow-same-worker" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, sameWorker.StatusCode);
        using var sameWorkerBody = JsonDocument.Parse(await sameWorker.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "shadow-destination-same-worker",
            sameWorkerBody.RootElement.GetProperty("error").GetString());

        using var channel = GrpcChannel.ForAddress(campaignGrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
        });
        var transport = new CampaignCompanionTransport.CampaignCompanionTransportClient(channel);
        using var call = transport.Connect(
            new Metadata { { "authorization", $"Bearer {campaignToken}" } },
            cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 1,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = destinationClientId,
                GameSessionId = "shadow-destination-worker",
                CampaignId = campaignId,
                ReconnectCommitPosition = 0,
                RuntimeFingerprintSha256 = new string('d', 64),
                CatalogFingerprintSha256 = new string('e', 64),
                PlayerTaskforceId = "taskforce:Blue",
                TacticalEntityCapacity = 1,
                TacticalReserveCapacity = 1,
                MaterializationProfiles =
                {
                    new TacticalMaterializationProfile
                    {
                        UnitTypeId = "usn_ff_knox_84",
                        VariantId = "Variant43",
                        LoadoutId = "Default",
                    },
                },
            },
        });
        Assert.True(await call.ResponseStream.MoveNext(cancellationToken));
        Assert.Equal(ServiceFrame.PayloadOneofCase.Welcome, call.ResponseStream.Current.PayloadCase);
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);

        const string quarantinedClientId = "30000000000000000000000000000003";
        using var quarantinedCall = transport.Connect(
            new Metadata { { "authorization", $"Bearer {campaignToken}" } },
            cancellationToken: cancellationToken);
        await quarantinedCall.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 1,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = quarantinedClientId,
                GameSessionId = "quarantined-destination-worker",
                CampaignId = campaignId,
                ReconnectCommitPosition = 0,
                RuntimeFingerprintSha256 = new string('f', 64),
                CatalogFingerprintSha256 = new string('1', 64),
                PlayerTaskforceId = "taskforce:Blue",
                TacticalEntityCapacity = 4,
                TacticalReserveCapacity = 1,
                Health = "Quarantined",
                Eligibility = "Quarantined",
                MaterializationProfiles =
                {
                    new TacticalMaterializationProfile
                    {
                        UnitTypeId = "usn_ff_knox_84",
                        VariantId = "Variant43",
                        LoadoutId = "Default",
                    },
                },
            },
        });
        Assert.True(await quarantinedCall.ResponseStream.MoveNext(cancellationToken));
        Assert.Equal(ServiceFrame.PayloadOneofCase.Welcome, quarantinedCall.ResponseStream.Current.PayloadCase);
        await WaitForCampaignSessionCountAsync(campaignClient, 3, cancellationToken);

        using var quarantinedView = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        quarantinedView.EnsureSuccessStatusCode();
        using var quarantinedViewBody = JsonDocument.Parse(
            await quarantinedView.Content.ReadAsStringAsync(cancellationToken));
        var quarantinedWorker = Assert.Single(
            quarantinedViewBody.RootElement.GetProperty("workers").EnumerateArray(),
            item => item.GetProperty("clientInstanceId").GetString() == quarantinedClientId);
        Assert.Equal("Quarantined", quarantinedWorker.GetProperty("health").GetString());
        Assert.Equal("Quarantined", quarantinedWorker.GetProperty("eligibility").GetString());

        using var quarantinedPreferred = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-shadow",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                preferredDestinationClientInstanceId = quarantinedClientId,
                idempotencyKey = "shadow-quarantined-pref",
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, quarantinedPreferred.StatusCode);
        using var quarantinedPreferredBody = JsonDocument.Parse(
            await quarantinedPreferred.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "shadow-destination-mismatch",
            quarantinedPreferredBody.RootElement.GetProperty("error").GetString());

        await quarantinedCall.RequestStream.CompleteAsync();
        quarantinedCall.Dispose();
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);

        using var sourceView = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        sourceView.EnsureSuccessStatusCode();
        using var sourceViewBody = JsonDocument.Parse(await sourceView.Content.ReadAsStringAsync(cancellationToken));
        var sourceClientId = Assert.Single(
            sourceViewBody.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Active")
            .GetProperty("workerClientInstanceId").GetString();

        using var preferredMismatch = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-shadow",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                preferredDestinationClientInstanceId = sourceClientId,
                idempotencyKey = "shadow-pref-mismatch",
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, preferredMismatch.StatusCode);
        using var preferredMismatchBody = JsonDocument.Parse(
            await preferredMismatch.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "shadow-destination-mismatch",
            preferredMismatchBody.RootElement.GetProperty("error").GetString());

        using var sourceMismatch = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-source",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                preferredWorkerClientInstanceId = destinationClientId,
                idempotencyKey = "source-prep-mismatch",
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, sourceMismatch.StatusCode);
        using var sourceMismatchBody = JsonDocument.Parse(
            await sourceMismatch.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "source-worker-mismatch",
            sourceMismatchBody.RootElement.GetProperty("error").GetString());

        using var preparedShadow = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-shadow",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                preferredDestinationClientInstanceId = destinationClientId,
                idempotencyKey = "shadow-prep-1",
            },
            cancellationToken);
        Assert.True(
            preparedShadow.IsSuccessStatusCode,
            await preparedShadow.Content.ReadAsStringAsync(cancellationToken));
        using var preparedShadowBody = JsonDocument.Parse(
            await preparedShadow.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            destinationClientId,
            preparedShadowBody.RootElement.GetProperty("destinationClientInstanceId").GetString());

        TacticalAssignment? queued = null;
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (call.ResponseStream.Current.PayloadCase == ServiceFrame.PayloadOneofCase.Assignment
                && call.ResponseStream.Current.Assignment.Role == AssignmentRole.Shadow)
            {
                queued = call.ResponseStream.Current.Assignment;
                break;
            }
        }
        Assert.NotNull(queued);
        Assert.Equal("taskforce:Blue", queued.TaskforceId);
        Assert.Equal(primaryEntityId, queued.CanonicalEntityId);
        Assert.Equal(AssignmentRole.Shadow, queued.Role);
        Assert.StartsWith("assignment:", queued.AssignmentId, StringComparison.Ordinal);

        using (var preparingShadow = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                item.GetProperty("role").GetString() == "Shadow"
                && item.GetProperty("lifecycle").GetString() == "Preparing"),
            cancellationToken))
        {
            Assert.Contains(
                preparingShadow.RootElement.GetProperty("assignments").EnumerateArray(),
                item => item.GetProperty("role").GetString() == "Primary"
                    && item.GetProperty("lifecycle").GetString() == "Active");
        }
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 2,
            AssignmentPrepared = new AssignmentPrepared
            {
                AssignmentId = queued.AssignmentId,
                EntityId = queued.EntityId,
                AuthorityEpoch = queued.AuthorityEpoch,
                RealizedRevision = 2,
                CanonicalStateSha256 = new string('a', 64),
                Ready = true,
            },
        });

        using var shadowView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root =>
            {
                var assignments = root.GetProperty("assignments").EnumerateArray().ToArray();
                var workers = root.GetProperty("workers").EnumerateArray().ToArray();
                return assignments.Any(item =>
                        item.GetProperty("role").GetString() == "Primary"
                        && item.GetProperty("lifecycle").GetString() == "Active")
                    && assignments.Any(item =>
                        item.GetProperty("role").GetString() == "Shadow"
                        && item.GetProperty("lifecycle").GetString() == "Prepared"
                        && item.GetProperty("workerClientInstanceId").GetString() == destinationClientId)
                    && workers.Any(item =>
                        item.GetProperty("clientInstanceId").GetString() == destinationClientId
                        && item.GetProperty("promotionHeadroom").GetInt32() == 0
                        && item.GetProperty("migrationOverlapHeadroom").GetInt32() == 0);
            },
            cancellationToken);
        var assignments = shadowView.RootElement.GetProperty("assignments").EnumerateArray().ToArray();
        var source = Assert.Single(assignments, item => item.GetProperty("role").GetString() == "Primary");
        var shadow = Assert.Single(assignments, item => item.GetProperty("role").GetString() == "Shadow");
        Assert.Equal("Active", source.GetProperty("lifecycle").GetString());
        Assert.Equal("Prepared", shadow.GetProperty("lifecycle").GetString());
        Assert.NotEqual(
            source.GetProperty("workerClientInstanceId").GetString(),
            shadow.GetProperty("workerClientInstanceId").GetString());
        Assert.Equal(destinationClientId, shadow.GetProperty("workerClientInstanceId").GetString());
        Assert.True(shadow.GetProperty("authorityEpoch").GetInt64() > source.GetProperty("authorityEpoch").GetInt64());
        Assert.Equal(source.GetProperty("entityId").GetString(), shadow.GetProperty("entityId").GetString());
        var destinationWorker = Assert.Single(
            shadowView.RootElement.GetProperty("workers").EnumerateArray(),
            item => item.GetProperty("clientInstanceId").GetString() == destinationClientId);
        Assert.Equal(1, destinationWorker.GetProperty("occupiedTacticalEntityCount").GetInt32());
        Assert.Equal(0, destinationWorker.GetProperty("promotionHeadroom").GetInt32());
        Assert.Equal(0, destinationWorker.GetProperty("migrationOverlapHeadroom").GetInt32());

        using (var anonymousMigrate = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(campaignHttpUrl),
            Timeout = TimeSpan.FromSeconds(10),
        })
        {
            using var unauthorized = await anonymousMigrate.PostAsJsonAsync(
                $"/control/campaigns/{campaignId}/migrate-tactical-island",
                new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-unauth" },
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        using var preCommit = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-before-quiesce" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, preCommit.StatusCode);
        using var preCommitBody = JsonDocument.Parse(await preCommit.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "migration-source-not-quiesced",
            preCommitBody.RootElement.GetProperty("error").GetString());

        using var repeated = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-shadow",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "shadow-prep-1" },
            cancellationToken);
        Assert.True(
            repeated.IsSuccessStatusCode,
            $"Repeated shadow preparation failed: {await repeated.Content.ReadAsStringAsync(cancellationToken)}");
        using var repeatedView = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        repeatedView.EnsureSuccessStatusCode();
        using var repeatedDocument = JsonDocument.Parse(await repeatedView.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            1,
            repeatedDocument.RootElement.GetProperty("assignments").EnumerateArray()
                .Count(item => item.GetProperty("role").GetString() == "Shadow"
                    && item.GetProperty("lifecycle").GetString() != "Released"));

        using var cancel = await campaignClient.PostAsJsonAsync(
            "/control/handoffs/cancel",
            new
            {
                campaignId,
                canonicalEntityId = primaryEntityId,
                gameEntityId = $"unit:crimean-tom:{primaryEntityId}",
                assignmentId = $"assignment:{shadow.GetProperty("assignmentId").GetString()}",
                authorityEpoch = shadow.GetProperty("authorityEpoch").GetInt64(),
                idempotencyKey = "shadow-prep-cancel",
                requiredRuntimeFingerprintSha256 = new string('d', 64),
                requiredCatalogFingerprintSha256 = new string('e', 64),
                unitTypeId = "usn_ff_knox_84",
                variantId = "Variant43",
                loadoutId = "Default",
                taskforceId = "taskforce:Blue",
            },
            cancellationToken);
        Assert.True(
            cancel.IsSuccessStatusCode,
            $"Shadow preparation cancellation failed: {await cancel.Content.ReadAsStringAsync(cancellationToken)}");
        using var releasedShadow = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Count(item =>
                item.GetProperty("lifecycle").GetString() != "Released") == 1
                && root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("role").GetString() == "Primary"
                    && item.GetProperty("lifecycle").GetString() == "Active"),
            cancellationToken);
        Assert.Equal(
            "Primary",
            Assert.Single(
                    releasedShadow.RootElement.GetProperty("assignments").EnumerateArray(),
                    item => item.GetProperty("lifecycle").GetString() != "Released")
                .GetProperty("role").GetString());

        await call.RequestStream.CompleteAsync();
        call.Dispose();
        await WaitForCampaignSessionCountAsync(campaignClient, 1, cancellationToken);
    }

    private static async Task ProveFinalCheckpointMigrationAsync(
        HttpClient campaignClient,
        string campaignHttpUrl,
        string campaignGrpcUrl,
        string campaignToken,
        string campaignId,
        string primaryEntityId,
        string cooldownProbeEntityId,
        string serviceRunDirectory,
        string runDirectory,
        string scriptPath,
        Func<CancellationToken, Task> restartCampaignService,
        CancellationToken cancellationToken)
    {
        const string destinationClientId = "20000000000000000000000000000002";
        var gameEntityId = $"unit:crimean-tom:{primaryEntityId}";
        await WaitForCampaignSessionCountAsync(campaignClient, 1, cancellationToken);
        var sourceAssignmentId = await WaitForAcceptedMigrationSourceHandoffAsync(
            campaignClient,
            campaignId,
            primaryEntityId,
            gameEntityId,
            cancellationToken);
        var sourceAssignmentKey = sourceAssignmentId["assignment:".Length..];
        var preparedFrames = await WaitForInboundFramesAsync(
            serviceRunDirectory,
            count: 1,
            cancellationToken,
            required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                && item.AssignmentPrepared.AssignmentId == sourceAssignmentId
                && item.AssignmentPrepared.Ready);
        var preparedSource = Assert.Single(
            preparedFrames,
            item => item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared
                && item.AssignmentPrepared.AssignmentId == sourceAssignmentId).AssignmentPrepared;
        var sourceCheckpointFrames = await WaitForInboundFramesAsync(
            serviceRunDirectory,
            count: 1,
            cancellationToken,
            required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished
                && item.CheckpointPublished.AssignmentId == sourceAssignmentId);
        var sourceCheckpoint = sourceCheckpointFrames.Last(item =>
            item.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished
            && item.CheckpointPublished.AssignmentId == sourceAssignmentId).CheckpointPublished;
        using var activeSource = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root =>
            {
                var assignments = root.GetProperty("assignments").EnumerateArray().ToArray();
                return assignments.Any(item =>
                    item.GetProperty("assignmentId").GetString() == sourceAssignmentKey
                    && item.GetProperty("entityId").GetString() == primaryEntityId
                    && item.GetProperty("role").GetString() == "Primary"
                    && item.GetProperty("lifecycle").GetString() == "Active");
            },
            cancellationToken);
        var sourceAssignment = Assert.Single(
            activeSource.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("assignmentId").GetString() == sourceAssignmentKey
                && item.GetProperty("lifecycle").GetString() == "Active");
        Assert.Equal("Primary", sourceAssignment.GetProperty("role").GetString());
        Assert.Equal(primaryEntityId, sourceAssignment.GetProperty("entityId").GetString());
        Assert.DoesNotContain(
            activeSource.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("assignmentId").GetString() == sourceAssignmentKey
                && item.GetProperty("lifecycle").GetString() == "Released");

        using var channel = GrpcChannel.ForAddress(campaignGrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
        });
        var transport = new CampaignCompanionTransport.CampaignCompanionTransportClient(channel);
        using var call = transport.Connect(
            new Metadata { { "authorization", $"Bearer {campaignToken}" } },
            cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 1,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = destinationClientId,
                GameSessionId = "shadow-migration-worker",
                CampaignId = campaignId,
                ReconnectCommitPosition = 0,
                RuntimeFingerprintSha256 = new string('d', 64),
                CatalogFingerprintSha256 = new string('e', 64),
                PlayerTaskforceId = "taskforce:Blue",
                TacticalEntityCapacity = 2,
                TacticalReserveCapacity = 1,
                MaterializationProfiles =
                {
                    new TacticalMaterializationProfile
                    {
                        UnitTypeId = "usn_ff_knox_84",
                        VariantId = "Variant43",
                        LoadoutId = "Default",
                    },
                },
            },
        });
        Assert.True(await call.ResponseStream.MoveNext(cancellationToken));
        Assert.Equal(ServiceFrame.PayloadOneofCase.Welcome, call.ResponseStream.Current.PayloadCase);
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);
        ulong destinationSequence = 1;

        using var preparedShadow = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-shadow",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-shadow-1" },
            cancellationToken);
        Assert.True(
            preparedShadow.IsSuccessStatusCode,
            await preparedShadow.Content.ReadAsStringAsync(cancellationToken));
        var queued = await WaitForServicePayloadAsync(
            call,
            item => item.PayloadCase == ServiceFrame.PayloadOneofCase.Assignment
                && item.Assignment.Role == AssignmentRole.Shadow,
            cancellationToken);
        Assert.Equal(primaryEntityId, queued.Assignment.CanonicalEntityId);
        destinationSequence++;
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = destinationSequence,
            AssignmentPrepared = new AssignmentPrepared
            {
                AssignmentId = queued.Assignment.AssignmentId,
                EntityId = queued.Assignment.EntityId,
                AuthorityEpoch = queued.Assignment.AuthorityEpoch,
                RealizedRevision = 2,
                CanonicalStateSha256 = new string('a', 64),
                Ready = true,
            },
        });
        using var preparedView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                item.GetProperty("role").GetString() == "Shadow"
                && item.GetProperty("lifecycle").GetString() == "Prepared"
                && item.GetProperty("entityId").GetString() == primaryEntityId),
            cancellationToken);
        Assert.Contains(
            preparedView.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Active");

        using var drain = await campaignClient.PostAsJsonAsync(
            "/control/quiesce",
            new
            {
                campaignId,
                assignmentId = sourceAssignmentId,
                gameEntityId,
                expectedGameRevision = preparedSource.RealizedRevision,
                idempotencyKey = "migrate-source-quiesce-1",
            },
            JsonOptions,
            cancellationToken);
        Assert.True(
            drain.IsSuccessStatusCode,
            $"Migration source drain failed: {drain.StatusCode} {await drain.Content.ReadAsStringAsync(cancellationToken)}");
        using var quiescedView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                item.GetProperty("assignmentId").GetString() == sourceAssignment.GetProperty("assignmentId").GetString()
                && item.GetProperty("lifecycle").GetString() == "Quiesced")
                && root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("role").GetString() == "Shadow"
                    && item.GetProperty("lifecycle").GetString() == "Prepared")
                && root.GetProperty("entities").EnumerateArray().Any(item =>
                    item.GetProperty("entityId").GetString() == primaryEntityId
                    && item.GetProperty("fidelity").GetString() == "Tactical"),
            cancellationToken);
        Assert.DoesNotContain(
            quiescedView.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("assignmentId").GetString() == sourceAssignment.GetProperty("assignmentId").GetString()
                && item.GetProperty("lifecycle").GetString() == "Released");

        var divergingBytes = Encoding.UTF8.GetBytes("replica-hash-disagreement");
        var divergingHash = Convert.ToHexString(SHA256.HashData(divergingBytes)).ToLowerInvariant();
        Assert.NotEqual(sourceCheckpoint.CheckpointSha256, divergingHash);
        using (var putDiverging = await campaignClient.PutAsync(
            $"/content/{divergingHash}",
            new ByteArrayContent(divergingBytes),
            cancellationToken))
        {
            Assert.True(
                putDiverging.IsSuccessStatusCode,
                await putDiverging.Content.ReadAsStringAsync(cancellationToken));
        }
        destinationSequence++;
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = destinationSequence,
            CheckpointPublished = new CheckpointPublished
            {
                AssignmentId = queued.Assignment.AssignmentId,
                EntityId = queued.Assignment.EntityId,
                AuthorityEpoch = queued.Assignment.AuthorityEpoch,
                EntityRevision = sourceCheckpoint.EntityRevision + 1,
                CheckpointSha256 = divergingHash,
                CheckpointLength = divergingBytes.Length,
            },
        });
        using var laggedView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("replicaConvergence").GetProperty("entities").EnumerateArray().Any(item =>
                item.GetProperty("entityId").GetString() == primaryEntityId
                && item.GetProperty("diagnosis").GetString() == "pending-insufficient-samples"
                && item.GetProperty("sampleCount").GetInt32() == 2),
            cancellationToken);

        destinationSequence++;
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = destinationSequence,
            CheckpointPublished = new CheckpointPublished
            {
                AssignmentId = queued.Assignment.AssignmentId,
                EntityId = queued.Assignment.EntityId,
                AuthorityEpoch = queued.Assignment.AuthorityEpoch,
                EntityRevision = sourceCheckpoint.EntityRevision,
                CheckpointSha256 = divergingHash,
                CheckpointLength = divergingBytes.Length,
            },
        });
        using var disagreedView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("replicaConvergence").GetProperty("entities").EnumerateArray().Any(item =>
                item.GetProperty("entityId").GetString() == primaryEntityId
                && item.GetProperty("diagnosis").GetString() == "unsafe-divergence"),
            cancellationToken);
        Assert.Equal(
            "never-majority; exact-match-or-unsafe-divergence-or-pending",
            disagreedView.RootElement.GetProperty("replicaConvergence").GetProperty("majorityRule").GetString());
        using var disagreedMigrate = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-island-hash-disagree" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, disagreedMigrate.StatusCode);
        using var disagreedBody = JsonDocument.Parse(await disagreedMigrate.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("replica-hash-disagreement", disagreedBody.RootElement.GetProperty("error").GetString());

        destinationSequence++;
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = destinationSequence,
            CheckpointPublished = new CheckpointPublished
            {
                AssignmentId = queued.Assignment.AssignmentId,
                EntityId = queued.Assignment.EntityId,
                AuthorityEpoch = queued.Assignment.AuthorityEpoch,
                EntityRevision = sourceCheckpoint.EntityRevision,
                CheckpointSha256 = sourceCheckpoint.CheckpointSha256,
                CheckpointLength = sourceCheckpoint.CheckpointLength,
            },
        });
        using var reconciledView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("replicaConvergence").GetProperty("entities").EnumerateArray().Any(item =>
                item.GetProperty("entityId").GetString() == primaryEntityId
                && item.GetProperty("diagnosis").GetString() == "exact-match"),
            cancellationToken);
        Assert.Equal(
            "exact-match",
            Assert.Single(
                reconciledView.RootElement.GetProperty("replicaConvergence").GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == primaryEntityId)
                .GetProperty("diagnosis").GetString());

        var cooldownProbeIsland = await PrepareCooldownProbeIslandForMigrationAsync(
            campaignClient,
            call,
            destinationSequence,
            campaignId,
            cooldownProbeEntityId,
            cancellationToken);
        destinationSequence = cooldownProbeIsland.DestinationSequence;

        using var migrate = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-island-1" },
            cancellationToken);
        Assert.True(
            migrate.IsSuccessStatusCode,
            await migrate.Content.ReadAsStringAsync(cancellationToken));
        using var migrateBody = JsonDocument.Parse(await migrate.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("staging-queued", migrateBody.RootElement.GetProperty("phase").GetString());
        Assert.Equal(destinationClientId, migrateBody.RootElement.GetProperty("destinationClientInstanceId").GetString());

        using var concurrentMigrate = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-island-concurrent" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, concurrentMigrate.StatusCode);
        using var concurrentBody = JsonDocument.Parse(await concurrentMigrate.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "migration-orchestration-busy",
            concurrentBody.RootElement.GetProperty("error").GetString());
        using var unchangedDuringBusy = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        unchangedDuringBusy.EnsureSuccessStatusCode();
        using var unchangedDuringBusyBody = JsonDocument.Parse(
            await unchangedDuringBusy.Content.ReadAsStringAsync(cancellationToken));
        Assert.DoesNotContain(
            unchangedDuringBusyBody.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("workerClientInstanceId").GetString() == destinationClientId);

        destinationSequence++;
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = destinationSequence,
            FinalCheckpointStaged = new FinalCheckpointStaged
            {
                AssignmentId = cooldownProbeIsland.ShadowAssignmentId,
                EntityId = cooldownProbeIsland.ShadowEntityId,
                AuthorityEpoch = cooldownProbeIsland.ShadowAuthorityEpoch,
                RealizedRevision = 1,
                StagedCheckpointSha256 = new string('b', 64),
                Ready = false,
                IdempotencyKey = "unrelated-probe-staging-fail",
            },
        });
        using var stillBusyAfterUnrelatedFailure = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-island-still-busy" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, stillBusyAfterUnrelatedFailure.StatusCode);
        using var stillBusyBody = JsonDocument.Parse(
            await stillBusyAfterUnrelatedFailure.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "migration-orchestration-busy",
            stillBusyBody.RootElement.GetProperty("error").GetString());

        var staging = await WaitForServicePayloadAsync(
            call,
            item => item.PayloadCase == ServiceFrame.PayloadOneofCase.StageFinalCheckpoint,
            cancellationToken);
        Assert.Equal(queued.Assignment.AssignmentId, staging.StageFinalCheckpoint.DestinationAssignmentId);
        Assert.False(string.IsNullOrWhiteSpace(staging.StageFinalCheckpoint.CheckpointSha256));
        Assert.Equal(
            $"/content/{staging.StageFinalCheckpoint.CheckpointSha256}",
            staging.StageFinalCheckpoint.CheckpointUri);
        using var checkpointDownload = await campaignClient.GetAsync(
            staging.StageFinalCheckpoint.CheckpointUri,
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, checkpointDownload.StatusCode);

        ulong nextSequence = destinationSequence + 1;
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = nextSequence,
            FinalCheckpointStaged = new FinalCheckpointStaged
            {
                AssignmentId = staging.StageFinalCheckpoint.DestinationAssignmentId,
                EntityId = staging.StageFinalCheckpoint.EntityId,
                AuthorityEpoch = staging.StageFinalCheckpoint.DestinationAuthorityEpoch,
                RealizedRevision = 3,
                StagedCheckpointSha256 = staging.StageFinalCheckpoint.CheckpointSha256,
                Ready = true,
                IdempotencyKey = staging.StageFinalCheckpoint.IdempotencyKey,
            },
        });

        using var migratedView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root =>
            {
                var assignments = root.GetProperty("assignments").EnumerateArray().ToArray();
                return assignments.Any(item =>
                        item.GetProperty("assignmentId").GetString()
                            == queued.Assignment.AssignmentId["assignment:".Length..]
                        && item.GetProperty("role").GetString() == "Primary"
                        && item.GetProperty("lifecycle").GetString() == "Active"
                        && item.GetProperty("workerClientInstanceId").GetString() == destinationClientId)
                    && assignments.Any(item =>
                        item.GetProperty("assignmentId").GetString()
                            == sourceAssignment.GetProperty("assignmentId").GetString()
                        && item.GetProperty("lifecycle").GetString() == "Released")
                    && root.GetProperty("entities").EnumerateArray().Any(item =>
                        item.GetProperty("entityId").GetString() == primaryEntityId
                        && item.GetProperty("fidelity").GetString() == "Tactical");
            },
            cancellationToken);

        var grant = await WaitForServicePayloadAsync(
            call,
            item => item.PayloadCase == ServiceFrame.PayloadOneofCase.ActivationGrant,
            cancellationToken);
        Assert.Equal(queued.Assignment.AssignmentId, grant.ActivationGrant.AssignmentId);
        Assert.Equal(
            staging.StageFinalCheckpoint.CheckpointSha256,
            grant.ActivationGrant.StagedCheckpointSha256);
        Assert.True(grant.ActivationGrant.MigrationCommitPosition > 0);
        Assert.True(
            OwnedMutationContracts.IsValidIdempotencyKey(grant.ActivationGrant.IdempotencyKey),
            grant.ActivationGrant.IdempotencyKey);
        AssertOwnedMutationRecoveryContracts();
        var originalCommitPosition = grant.ActivationGrant.MigrationCommitPosition;
        var originalHash = grant.ActivationGrant.StagedCheckpointSha256;

        await call.RequestStream.CompleteAsync();
        call.Dispose();
        await WaitForCampaignSessionCountAsync(campaignClient, 1, cancellationToken);

        var destinationRunDirectory = Path.Combine(runDirectory, "destination-worker");
        var destinationPipeName = $"CrimeanTom.Tests.Destination.{Guid.NewGuid():N}";
        var destinationControlUrl = $"http://127.0.0.1:{GetUnusedLoopbackPort()}";
        var destinationStateDirectory = Path.Combine(destinationRunDirectory, "campaign-companion");
        var destinationProbeSavePath = Path.Combine(destinationRunDirectory, "_DestinationProbe.sav");
        var destinationPidPath = Path.Combine(destinationRunDirectory, "companion.pid");
        var destinationTokenPath = Path.Combine(destinationRunDirectory, "control.token");
        var destinationOwnedResources = new FakeOwnedSurfaceResourceState();
        var destinationStaged = new FinalCheckpointStaged
        {
            AssignmentId = staging.StageFinalCheckpoint.DestinationAssignmentId,
            EntityId = staging.StageFinalCheckpoint.EntityId,
            AuthorityEpoch = staging.StageFinalCheckpoint.DestinationAuthorityEpoch,
            RealizedRevision = 3,
            StagedCheckpointSha256 = staging.StageFinalCheckpoint.CheckpointSha256,
            Ready = true,
            IdempotencyKey = staging.StageFinalCheckpoint.IdempotencyKey,
        };
        await SeedDestinationActivationRecoveryStateAsync(
            Path.Combine(runDirectory, "campaign-companion"),
            destinationStateDirectory,
            destinationClientId,
            queued.Assignment,
            destinationStaged,
            originalCommitPosition,
            cancellationToken);
        var destinationGamePeer = RunGamePeerAsync(
            destinationPipeName,
            destinationProbeSavePath,
            new FakeTacticalCouplingState(),
            destinationOwnedResources,
            new string('d', 64),
            new string('e', 64),
            cancellationToken);
        _ = destinationGamePeer.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        var destinationStart = await RunControlScriptAsync(
            scriptPath,
            "start",
            destinationRunDirectory,
            destinationControlUrl,
            destinationPipeName,
            skipBuild: true,
            cancellationToken,
            new Dictionary<string, string>
            {
                ["CRIMEAN_TOM_CAMPAIGN_GRPC_URL"] = campaignGrpcUrl,
                ["CRIMEAN_TOM_CAMPAIGN_HTTP_URL"] = campaignHttpUrl,
                ["CRIMEAN_TOM_CAMPAIGN_ID"] = campaignId,
                ["CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE"] = Path.Combine(serviceRunDirectory, "service.token"),
                ["CRIMEAN_TOM_CAMPAIGN_STATE_DIRECTORY"] = destinationStateDirectory,
                ["CRIMEAN_TOM_COLLIDER_CACHE_DIR"] = Path.Combine(destinationRunDirectory, "collider-cache"),
            });
        Assert.True(destinationStart.ExitCode == 0, destinationStart.FormatFailure("destination-start"));
        var destinationToken = (await File.ReadAllTextAsync(destinationTokenPath, cancellationToken)).Trim();
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);
        await WaitForOwnedSurfaceLifecycleAsync(
            destinationControlUrl,
            destinationToken,
            grant.ActivationGrant.AssignmentId,
            queued.Assignment.EntityId,
            grant.ActivationGrant.AuthorityEpoch,
            "active",
            cancellationToken);
        await WaitForActivationGrantFileAsync(
            destinationStateDirectory,
            grant.ActivationGrant.AssignmentId,
            grant.ActivationGrant.AuthorityEpoch,
            cancellationToken);

        using var stopDestination = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stoppedDestination = await RunControlScriptAsync(
            scriptPath,
            "stop",
            destinationRunDirectory,
            destinationControlUrl,
            destinationPipeName,
            skipBuild: true,
            stopDestination.Token);
        Assert.True(stoppedDestination.ExitCode == 0, stoppedDestination.FormatFailure("destination-stop"));
        await WaitForCampaignSessionCountAsync(campaignClient, 1, cancellationToken);

        destinationOwnedResources.ResetRuntime();
        var destinationReplayPeer = RunGamePeerAsync(
            destinationPipeName,
            destinationProbeSavePath,
            new FakeTacticalCouplingState(),
            destinationOwnedResources,
            new string('d', 64),
            new string('e', 64),
            cancellationToken);
        _ = destinationReplayPeer.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        var destinationRestart = await RunControlScriptAsync(
            scriptPath,
            "start",
            destinationRunDirectory,
            destinationControlUrl,
            destinationPipeName,
            skipBuild: true,
            cancellationToken,
            new Dictionary<string, string>
            {
                ["CRIMEAN_TOM_CAMPAIGN_GRPC_URL"] = campaignGrpcUrl,
                ["CRIMEAN_TOM_CAMPAIGN_HTTP_URL"] = campaignHttpUrl,
                ["CRIMEAN_TOM_CAMPAIGN_ID"] = campaignId,
                ["CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE"] = Path.Combine(serviceRunDirectory, "service.token"),
                ["CRIMEAN_TOM_CAMPAIGN_STATE_DIRECTORY"] = destinationStateDirectory,
                ["CRIMEAN_TOM_COLLIDER_CACHE_DIR"] = Path.Combine(destinationRunDirectory, "collider-cache"),
            });
        Assert.True(destinationRestart.ExitCode == 0, destinationRestart.FormatFailure("destination-restart"));
        destinationToken = (await File.ReadAllTextAsync(destinationTokenPath, cancellationToken)).Trim();
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);
        using var replayResources = await GetAuthorizedJsonAsync(
            new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(destinationControlUrl),
                Timeout = TimeSpan.FromSeconds(10),
            },
            destinationToken,
            "/game/mutations/surface/resources",
            cancellationToken);
        Assert.Equal(0, replayResources.RootElement.GetProperty("ownedRecordCount").GetInt32());

        using var stopDestinationAfterReplay = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stoppedDestinationAfterReplay = await RunControlScriptAsync(
            scriptPath,
            "stop",
            destinationRunDirectory,
            destinationControlUrl,
            destinationPipeName,
            skipBuild: true,
            stopDestinationAfterReplay.Token);
        Assert.True(
            stoppedDestinationAfterReplay.ExitCode == 0,
            stoppedDestinationAfterReplay.FormatFailure("destination-stop-after-replay"));

        using var resumedManualChannel = GrpcChannel.ForAddress(campaignGrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
        });
        var resumedManualTransport = new CampaignCompanionTransport.CampaignCompanionTransportClient(resumedManualChannel);
        using var resumedManualCall = resumedManualTransport.Connect(
            new Metadata { { "authorization", $"Bearer {campaignToken}" } },
            cancellationToken: cancellationToken);
        await resumedManualCall.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 1,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = destinationClientId,
                GameSessionId = "shadow-migration-worker",
                CampaignId = campaignId,
                ReconnectCommitPosition = 0,
                RuntimeFingerprintSha256 = new string('d', 64),
                CatalogFingerprintSha256 = new string('e', 64),
                PlayerTaskforceId = "taskforce:Blue",
                TacticalEntityCapacity = 2,
                TacticalReserveCapacity = 1,
                MaterializationProfiles =
                {
                    new TacticalMaterializationProfile
                    {
                        UnitTypeId = "usn_ff_knox_84",
                        VariantId = "Variant43",
                        LoadoutId = "Default",
                    },
                },
            },
        });
        Assert.True(await resumedManualCall.ResponseStream.MoveNext(cancellationToken));
        Assert.Equal(ServiceFrame.PayloadOneofCase.Welcome, resumedManualCall.ResponseStream.Current.PayloadCase);
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);

        using var repeated = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-island-1" },
            cancellationToken);
        Assert.True(
            repeated.IsSuccessStatusCode,
            await repeated.Content.ReadAsStringAsync(cancellationToken));
        using var repeatedBody = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync(cancellationToken));
        Assert.Contains(
            repeatedBody.RootElement.GetProperty("phase").GetString(),
            new[] { "activation-granted", "migration-committed-grants-pending" });
        Assert.Equal(originalCommitPosition, repeatedBody.RootElement.GetProperty("migrationCommitPosition").GetInt64());

        await AssertCampaignMigrationCooldownBlocksSecondIslandAsync(
            campaignClient,
            campaignId,
            cooldownProbeEntityId,
            "live",
            cancellationToken);

        using var currentViewResponse = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        currentViewResponse.EnsureSuccessStatusCode();
        using var currentView = JsonDocument.Parse(await currentViewResponse.Content.ReadAsStringAsync(cancellationToken));
        var laterTime = currentView.RootElement.GetProperty("timeQuanta").GetInt64() + 1;
        using var laterAdvance = await campaignClient.PostAsync(
            $"/control/campaigns/{campaignId}/advance/{laterTime}",
            content: null,
            cancellationToken);
        Assert.True(
            laterAdvance.IsSuccessStatusCode,
            await laterAdvance.Content.ReadAsStringAsync(cancellationToken));
        using var laterAdvanceBody = JsonDocument.Parse(await laterAdvance.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(laterAdvanceBody.RootElement.GetProperty("commitPosition").GetInt64() > originalCommitPosition);

        await resumedManualCall.RequestStream.CompleteAsync();
        resumedManualCall.Dispose();
        await restartCampaignService(cancellationToken);
        await WaitForCampaignSessionCountAsync(campaignClient, 1, cancellationToken);

        using var recoveredChannel = GrpcChannel.ForAddress(campaignGrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
        });
        var recoveredTransport = new CampaignCompanionTransport.CampaignCompanionTransportClient(recoveredChannel);
        using var recoveredCall = recoveredTransport.Connect(
            new Metadata { { "authorization", $"Bearer {campaignToken}" } },
            cancellationToken: cancellationToken);
        await recoveredCall.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 1,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = destinationClientId,
                GameSessionId = "shadow-migration-worker",
                CampaignId = campaignId,
                ReconnectCommitPosition = 0,
                RuntimeFingerprintSha256 = new string('d', 64),
                CatalogFingerprintSha256 = new string('e', 64),
                PlayerTaskforceId = "taskforce:Blue",
                TacticalEntityCapacity = 2,
                TacticalReserveCapacity = 1,
                MaterializationProfiles =
                {
                    new TacticalMaterializationProfile
                    {
                        UnitTypeId = "usn_ff_knox_84",
                        VariantId = "Variant43",
                        LoadoutId = "Default",
                    },
                },
            },
        });
        Assert.True(await recoveredCall.ResponseStream.MoveNext(cancellationToken));
        Assert.Equal(ServiceFrame.PayloadOneofCase.Welcome, recoveredCall.ResponseStream.Current.PayloadCase);
        await WaitForCampaignSessionCountAsync(campaignClient, 2, cancellationToken);

        using var recoveredRetry = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = primaryEntityId, idempotencyKey = "migrate-island-restart-1" },
            cancellationToken);
        Assert.True(
            recoveredRetry.IsSuccessStatusCode,
            await recoveredRetry.Content.ReadAsStringAsync(cancellationToken));
        using var recoveredRetryBody = JsonDocument.Parse(
            await recoveredRetry.Content.ReadAsStringAsync(cancellationToken));
        Assert.Contains(
            recoveredRetryBody.RootElement.GetProperty("phase").GetString(),
            new[] { "activation-granted", "migration-committed-grants-pending" });
        Assert.Equal(
            originalCommitPosition,
            recoveredRetryBody.RootElement.GetProperty("migrationCommitPosition").GetInt64());

        var recoveredGrant = await WaitForServicePayloadAsync(
            recoveredCall,
            item => item.PayloadCase == ServiceFrame.PayloadOneofCase.ActivationGrant
                && item.ActivationGrant.AssignmentId == queued.Assignment.AssignmentId
                && item.ActivationGrant.MigrationCommitPosition == originalCommitPosition,
            cancellationToken);
        Assert.Equal(originalHash, recoveredGrant.ActivationGrant.StagedCheckpointSha256);
        Assert.Equal(originalCommitPosition, recoveredGrant.ActivationGrant.MigrationCommitPosition);

        await recoveredCall.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = 2,
            FinalCheckpointStaged = new FinalCheckpointStaged
            {
                AssignmentId = staging.StageFinalCheckpoint.DestinationAssignmentId,
                EntityId = staging.StageFinalCheckpoint.EntityId,
                AuthorityEpoch = staging.StageFinalCheckpoint.DestinationAuthorityEpoch,
                RealizedRevision = 3,
                StagedCheckpointSha256 = staging.StageFinalCheckpoint.CheckpointSha256,
                Ready = true,
                IdempotencyKey = staging.StageFinalCheckpoint.IdempotencyKey,
            },
        });
        var resentGrant = await WaitForServicePayloadAsync(
            recoveredCall,
            item => item.PayloadCase == ServiceFrame.PayloadOneofCase.ActivationGrant
                && item.ActivationGrant.AssignmentId == queued.Assignment.AssignmentId
                && item.ActivationGrant.MigrationCommitPosition == originalCommitPosition,
            cancellationToken);
        Assert.Equal(originalHash, resentGrant.ActivationGrant.StagedCheckpointSha256);

        await AssertCampaignMigrationCooldownBlocksSecondIslandAsync(
            campaignClient,
            campaignId,
            cooldownProbeEntityId,
            "restart",
            cancellationToken);

        await recoveredCall.RequestStream.CompleteAsync();
        recoveredCall.Dispose();
        await WaitForCampaignSessionCountAsync(campaignClient, 1, cancellationToken);
    }

    private sealed record CooldownProbeIslandStaging(
        ulong DestinationSequence,
        string ShadowAssignmentId,
        string ShadowEntityId,
        long ShadowAuthorityEpoch);

    private static async Task<CooldownProbeIslandStaging> PrepareCooldownProbeIslandForMigrationAsync(
        HttpClient campaignClient,
        AsyncDuplexStreamingCall<CompanionFrame, ServiceFrame> destinationCall,
        ulong destinationSequence,
        string campaignId,
        string probeEntityId,
        CancellationToken cancellationToken)
    {
        const string probeGameEntityId = "unit:crimean-tom:mismatch-knox";

        using var prepare = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/prepare-shadow",
            new { campaignId, canonicalEntityId = probeEntityId, idempotencyKey = "cooldown-probe-shadow-setup" },
            cancellationToken);
        Assert.True(
            prepare.IsSuccessStatusCode,
            await prepare.Content.ReadAsStringAsync(cancellationToken));

        var shadowQueued = await WaitForServicePayloadAsync(
            destinationCall,
            item => item.PayloadCase == ServiceFrame.PayloadOneofCase.Assignment
                && item.Assignment.Role == AssignmentRole.Shadow
                && item.Assignment.CanonicalEntityId == probeEntityId,
            cancellationToken);
        destinationSequence++;
        await destinationCall.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = destinationSequence,
            AssignmentPrepared = new AssignmentPrepared
            {
                AssignmentId = shadowQueued.Assignment.AssignmentId,
                EntityId = shadowQueued.Assignment.EntityId,
                AuthorityEpoch = shadowQueued.Assignment.AuthorityEpoch,
                RealizedRevision = 1,
                CanonicalStateSha256 = new string('a', 64),
                Ready = true,
            },
        });

        using var preparedView = await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                item.GetProperty("entityId").GetString() == probeEntityId
                && item.GetProperty("role").GetString() == "Shadow"
                && item.GetProperty("lifecycle").GetString() == "Prepared"),
            cancellationToken);
        var probePrimary = Assert.Single(
            preparedView.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("entityId").GetString() == probeEntityId
                && item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Active");
        using var quiesce = await campaignClient.PostAsJsonAsync(
            "/control/quiesce",
            new
            {
                campaignId,
                assignmentId = $"assignment:{probePrimary.GetProperty("assignmentId").GetString()}",
                gameEntityId = probeGameEntityId,
                expectedGameRevision = 1,
                idempotencyKey = "cooldown-probe-quiesce-setup",
            },
            JsonOptions,
            cancellationToken);
        Assert.True(
            quiesce.IsSuccessStatusCode,
            await quiesce.Content.ReadAsStringAsync(cancellationToken));
        await WaitForCampaignViewAsync(
            campaignClient,
            campaignId,
            root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                item.GetProperty("entityId").GetString() == probeEntityId
                && item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Quiesced"),
            cancellationToken);

        return new CooldownProbeIslandStaging(
            destinationSequence,
            shadowQueued.Assignment.AssignmentId,
            shadowQueued.Assignment.EntityId,
            shadowQueued.Assignment.AuthorityEpoch);
    }

    private static async Task AssertCampaignMigrationCooldownBlocksSecondIslandAsync(
        HttpClient campaignClient,
        string campaignId,
        string probeEntityId,
        string label,
        CancellationToken cancellationToken)
    {
        using var beforeView = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        beforeView.EnsureSuccessStatusCode();
        using var beforeBody = JsonDocument.Parse(await beforeView.Content.ReadAsStringAsync(cancellationToken));
        var commitBefore = beforeBody.RootElement.GetProperty("commitPosition").GetInt64();

        using var cooledMigrate = await campaignClient.PostAsJsonAsync(
            $"/control/campaigns/{campaignId}/migrate-tactical-island",
            new { campaignId, canonicalEntityId = probeEntityId, idempotencyKey = $"cooldown-probe-migrate-{label}" },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, cooledMigrate.StatusCode);
        using var cooledBody = JsonDocument.Parse(await cooledMigrate.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(
            "migration-cooldown-active",
            cooledBody.RootElement.GetProperty("error").GetString());

        using var afterView = await campaignClient.GetAsync($"/campaigns/{campaignId}", cancellationToken);
        afterView.EnsureSuccessStatusCode();
        using var afterBody = JsonDocument.Parse(await afterView.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(commitBefore, afterBody.RootElement.GetProperty("commitPosition").GetInt64());
        Assert.Contains(
            afterBody.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("entityId").GetString() == probeEntityId
                && item.GetProperty("role").GetString() == "Primary"
                && item.GetProperty("lifecycle").GetString() == "Quiesced");
        Assert.Contains(
            afterBody.RootElement.GetProperty("assignments").EnumerateArray(),
            item => item.GetProperty("entityId").GetString() == probeEntityId
                && item.GetProperty("role").GetString() == "Shadow"
                && item.GetProperty("lifecycle").GetString() == "Prepared");
    }

    private static async Task<string> WaitForAcceptedMigrationSourceHandoffAsync(
        HttpClient campaignClient,
        string campaignId,
        string primaryEntityId,
        string gameEntityId,
        CancellationToken cancellationToken)
    {
        HttpStatusCode? lastStatus = null;
        var lastBody = "migration source handoff was never attempted";
        var attempt = 0;
        try
        {
            while (true)
            {
                var candidate = $"assignment:{Guid.NewGuid():N}";
                using var handoff = await campaignClient.PostAsJsonAsync(
                    "/control/handoffs",
                    new
                    {
                        campaignId,
                        canonicalEntityId = primaryEntityId,
                        gameEntityId,
                        assignmentId = candidate,
                        authorityEpoch = 21 + attempt,
                        idempotencyKey = $"migrate-source-1-{attempt}",
                        requiredRuntimeFingerprintSha256 = new string('a', 64),
                        requiredCatalogFingerprintSha256 = string.Empty,
                        unitTypeId = "usn_ff_knox_84",
                        variantId = "Variant43",
                        loadoutId = "Default",
                        taskforceId = "taskforce:Blue",
                    },
                    JsonOptions,
                    cancellationToken);
                lastStatus = handoff.StatusCode;
                lastBody = await handoff.Content.ReadAsStringAsync(cancellationToken);
                if (handoff.IsSuccessStatusCode)
                {
                    return candidate;
                }

                if (handoff.StatusCode != HttpStatusCode.Conflict
                    || !lastBody.Contains("companion-session-unavailable-or-backpressured", StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"Migration source handoff was rejected instead of waiting for the restarted companion session. "
                        + $"{handoff.StatusCode}: {lastBody}");
                }

                attempt++;
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Restarted companion campaign session did not accept a fresh Primary migration-source assignment. "
                + $"Last status {lastStatus}: {lastBody}",
                exception);
        }
    }

    private static async Task<ServiceFrame> WaitForServicePayloadAsync(
        AsyncDuplexStreamingCall<CompanionFrame, ServiceFrame> call,
        Func<ServiceFrame, bool> required,
        CancellationToken cancellationToken)
    {
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            if (required(call.ResponseStream.Current))
            {
                return call.ResponseStream.Current;
            }
        }
        throw new TimeoutException("The destination worker stream ended before the required service frame arrived.");
    }

    private static async Task<string> WaitForCampaignSessionAsync(
        HttpClient client,
        string? previousSessionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("session", out var session)
                    && session.ValueKind == JsonValueKind.Object
                    && session.TryGetProperty("sessionId", out var idElement)
                    && idElement.GetString() is { Length: > 0 } id
                    && !string.Equals(id, previousSessionId, StringComparison.Ordinal))
                {
                    return id;
                }
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForCampaignCouplingCountAsync(
        HttpClient client,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("session", out var session)
                    && session.ValueKind == JsonValueKind.Object
                    && session.TryGetProperty("couplingRelationships", out var relationships)
                    && relationships.GetArrayLength() == expectedCount)
                {
                    return;
                }
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForCampaignSessionCountAsync(
        HttpClient client,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("sessions", out var sessions)
                    && sessions.ValueKind == JsonValueKind.Array
                    && sessions.GetArrayLength() == expectedCount)
                {
                    return;
                }
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForCampaignResourceCountsAsync(
        HttpClient client,
        int materialized,
        int active,
        int retainedSuspended,
        int recoveryRequired,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync("/health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("session", out var session)
                    && session.ValueKind == JsonValueKind.Object
                    && session.GetProperty("ownedMaterializedEntityCount").GetInt32() == materialized
                    && session.GetProperty("ownedActiveEntityCount").GetInt32() == active
                    && session.GetProperty("ownedRetainedSuspendedEntityCount").GetInt32() == retainedSuspended
                    && session.GetProperty("ownedRecoveryRequiredEntityCount").GetInt32() == recoveryRequired)
                {
                    return;
                }
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<CompanionFrame>> WaitForInboundFramesAsync(
        string runDirectory,
        int count,
        CancellationToken cancellationToken,
        Func<CompanionFrame, bool>? required = null,
        [CallerLineNumber] int callerLine = 0)
    {
        var spool = Path.Combine(runDirectory, "inbound-spool");
        IReadOnlyList<CompanionFrame> authorityFrames = [];
        try
        {
            while (true)
            {
                if (Directory.Exists(spool))
                {
                    authorityFrames = ReadInboundAuthorityFrames(runDirectory);
                    if (authorityFrames.Count >= count
                        && (required is null || authorityFrames.Any(required)))
                    {
                        return authorityFrames;
                    }
                }
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var observed = string.Join(", ", authorityFrames
                .GroupBy(frame => frame.PayloadCase)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
            throw new TimeoutException(
                $"Inbound-frame wait from test line {callerLine} expected count >= {count}"
                + $" and required match={required is not null}; observed {authorityFrames.Count}"
                + $" authority frames ({observed}).");
        }
    }

    private static IReadOnlyList<CompanionFrame> ReadInboundAuthorityFrames(string runDirectory)
    {
        var spool = Path.Combine(runDirectory, "inbound-spool");
        return Directory.Exists(spool)
            ? Directory.EnumerateFiles(spool, "*.*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".frame" or ".processed")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(TryReadInboundFrame)
                .Where(frame => frame is not null)
                .Select(frame => frame!)
                .Where(frame => frame.PayloadCase is
                    CompanionFrame.PayloadOneofCase.AssignmentPrepared
                    or CompanionFrame.PayloadOneofCase.CheckpointPublished
                    or CompanionFrame.PayloadOneofCase.TacticalEvent
                    or CompanionFrame.PayloadOneofCase.FinalCheckpointStaged)
                .ToArray()
            : [];
    }

    private static CompanionFrame? TryReadInboundFrame(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return CompanionFrame.Parser.ParseFrom(stream);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void AssertOwnedMutationRecoveryContracts(
        string? migrationActivateKey = null,
        string? canonicalEntityId = null)
    {
        const string defaultEntityId = "unit:crimean-tom:a2d904987998596a48868b21ca66d3ec";
        const string defaultStageKey =
            "live-migration-b5735979a41f4b698d5fac0cc56c4069-migrate-a2d904987998596a48868b21ca66d3ec-stage";
        var entityId = canonicalEntityId is null
            ? defaultEntityId
            : $"unit:crimean-tom:{canonicalEntityId}";
        var stageKey = defaultStageKey;
        var expectedActivateKey = migrationActivateKey
            ?? "live-migration-b5735979a41f4b698d5fac0cc56c4069-migrate-a2d904987998596a48868b21ca66d3ec-activate";
        var activateKey = OwnedMutationContracts.ResolveActivationIdempotencyKey(stageKey, entityId);
        Assert.Equal(expectedActivateKey, activateKey);
        Assert.True(activateKey.Length <= OwnedMutationContracts.MaximumScopedIdentifierLength);
        var legacyOversized =
            $"{stageKey}-{OwnedMutationContracts.CanonicalEntityId(entityId)}-activate";
        Assert.False(OwnedMutationContracts.IsValidIdempotencyKey(legacyOversized));
        var normalizedLegacy = OwnedMutationContracts.ResolveActivationIdempotencyKey(legacyOversized, entityId);
        Assert.Equal(expectedActivateKey, normalizedLegacy);
        var stagingKey = OwnedMutationContracts.ResolveStagingIdempotencyKey(expectedActivateKey, entityId);
        Assert.Equal(stageKey, stagingKey);

        var invalid = new SetOwnedSurfaceSuspensionRequestPayload
        {
            Context = new OwnedMutationContextPayload
            {
                SessionId = "session-1",
                AssignmentId = "assignment:84ab0b5a494098d4b8371d69a01da596",
                AuthorityEpoch = 4,
                ExpectedRevision = 3,
                IdempotencyKey = string.Empty,
            },
            EntityId = entityId,
            IsSuspended = false,
        };
        var exception = Assert.Throws<InvalidDataException>(() =>
            OwnedMutationContracts.ValidateOwnedLifecycleIdentifiers(invalid.Context, invalid.EntityId));
        Assert.Contains("idempotency", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SeedDestinationActivationRecoveryStateAsync(
        string sourceCompanionStateDirectory,
        string destinationCompanionStateDirectory,
        string destinationClientId,
        TacticalAssignment shadowAssignment,
        FinalCheckpointStaged staged,
        long migrationCommitPosition,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(destinationCompanionStateDirectory, "assignments"));
        var sourceEventsDirectory = Path.Combine(sourceCompanionStateDirectory, "committed-events");
        var destinationEventsDirectory = Path.Combine(destinationCompanionStateDirectory, "committed-events");
        if (Directory.Exists(sourceEventsDirectory))
        {
            Directory.CreateDirectory(destinationEventsDirectory);
            foreach (var eventPath in Directory.EnumerateFiles(sourceEventsDirectory, "*.json"))
            {
                var destinationPath = Path.Combine(destinationEventsDirectory, Path.GetFileName(eventPath));
                File.Copy(eventPath, destinationPath, overwrite: true);
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(destinationCompanionStateDirectory, "client-instance-id"),
            destinationClientId,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(destinationCompanionStateDirectory, "cursor"),
            migrationCommitPosition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        var assignmentSafeKey = SafeCompanionStateKey(shadowAssignment.AssignmentId);
        await File.WriteAllBytesAsync(
            Path.Combine(destinationCompanionStateDirectory, "assignments", $"{assignmentSafeKey}.assignment"),
            shadowAssignment.ToByteArray(),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(
                destinationCompanionStateDirectory,
                "assignments",
                $"{assignmentSafeKey}.revision"),
            staged.RealizedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(
                destinationCompanionStateDirectory,
                "assignments",
                $"{assignmentSafeKey}.{shadowAssignment.AuthorityEpoch}.staged-checkpoint"),
            staged.StagedCheckpointSha256,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(
                destinationCompanionStateDirectory,
                "assignments",
                $"{assignmentSafeKey}.{shadowAssignment.AuthorityEpoch}.staged-idempotency"),
            staged.IdempotencyKey,
            cancellationToken);

        var outboundDirectory = Path.Combine(destinationCompanionStateDirectory, "outbound");
        Directory.CreateDirectory(outboundDirectory);
        var outboundFrame = new CompanionFrame { FinalCheckpointStaged = staged };
        outboundFrame.Sequence = 0;
        var outboundKey = $"staged-{assignmentSafeKey}-{shadowAssignment.AuthorityEpoch}";
        await File.WriteAllBytesAsync(
            Path.Combine(outboundDirectory, $"{outboundKey}.frame"),
            outboundFrame.ToByteArray(),
            cancellationToken);
    }

    private static string SafeCompanionStateKey(string key) =>
        string.Concat(key.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private static async Task WaitForOwnedSurfaceLifecycleAsync(
        string controlUrl,
        string token,
        string assignmentId,
        string entityId,
        long authorityEpoch,
        string lifecycleState,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(controlUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        while (true)
        {
            using var response = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/mutations/surface/resources",
                cancellationToken);
            if (response.RootElement.GetProperty("entities").EnumerateArray().Any(item =>
                    item.GetProperty("assignmentId").GetString() == assignmentId
                    && item.GetProperty("entityId").GetString() == entityId
                    && item.GetProperty("authorityEpoch").GetInt64() == authorityEpoch
                    && string.Equals(
                        item.GetProperty("lifecycleState").GetString(),
                        lifecycleState,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForActivationGrantFileAsync(
        string companionStateDirectory,
        string assignmentId,
        long authorityEpoch,
        CancellationToken cancellationToken)
    {
        var grantPath = Path.Combine(
            companionStateDirectory,
            "assignments",
            $"{SafeCompanionStateKey(assignmentId)}.{authorityEpoch}.activation-grant");
        while (true)
        {
            if (File.Exists(grantPath))
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed class FakeOwnedSurfaceResourceState
    {
        private readonly Dictionary<string, FakeOwnedSurfaceResource> _entities =
            new(StringComparer.Ordinal);
        private bool _lastSuspensionWasIdempotentReplay;
        private string? _armedSessionId;

        public void ResetRuntime()
        {
            _entities.Clear();
            _armedSessionId = null;
            _lastSuspensionWasIdempotentReplay = false;
        }

        public void Arm(string sessionId) => _armedSessionId = sessionId;

        public bool IsArmedFor(string sessionId) =>
            !string.IsNullOrWhiteSpace(_armedSessionId)
            && string.Equals(_armedSessionId, sessionId, StringComparison.Ordinal);

        public CommandResponsePayload? TryRequireArmed(string sessionId, string commandType)
        {
            if (commandType is GameCommandTypes.ArmOwnedMutationMode
                or GameCommandTypes.GetOwnedSurfaceResourceState
                or GameCommandTypes.GetSceneState
                or GameCommandTypes.GetCapabilities
                or GameCommandTypes.SetExperimentalPauseState
                or GameCommandTypes.GetTacticalCouplingState
                or GameCommandTypes.GetWorldSnapshot
                or GameCommandTypes.GetUiInspection
                or GameCommandTypes.CaptureGameScreenshot
                or GameCommandTypes.CaptureColliderCatalog
                or GameCommandTypes.GetColliderCatalog
                or GameCommandTypes.FocusCameraOnUnit
                or GameCommandTypes.SetTacticalHudPanel)
            {
                return null;
            }

            if (IsMutationCommand(commandType) && !IsArmedFor(sessionId))
            {
                return new CommandResponsePayload
                {
                    Success = false,
                    ErrorCode = ProtocolErrorCodes.MutationDisabled,
                    ErrorMessage = "Owned mutations are not armed for the current pipe session.",
                };
            }

            return null;
        }

        private static bool IsMutationCommand(string commandType) =>
            commandType is GameCommandTypes.MaterializeOwnedSurfaceUnit
                or GameCommandTypes.CaptureOwnedSurfaceCheckpoint
                or GameCommandTypes.UpdateOwnedSurfaceUnit
                or GameCommandTypes.SetOwnedSurfaceSuspension
                or GameCommandTypes.RestoreOwnedSurfaceCheckpoint
                or GameCommandTypes.StageOwnedSurfaceCheckpoint
                or GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery;

        public CommandResponsePayload? TryObserveSuspension(string payloadJson, string sessionId)
        {
            if (!IsArmedFor(sessionId))
            {
                return new CommandResponsePayload
                {
                    Success = false,
                    ErrorCode = ProtocolErrorCodes.MutationDisabled,
                    ErrorMessage = "Owned mutations are not armed for the current pipe session.",
                };
            }

            var request = JsonSerializer.Deserialize<SetOwnedSurfaceSuspensionRequestPayload>(
                payloadJson,
                JsonOptions)!;
            try
            {
                OwnedMutationContracts.ValidateOwnedLifecycleIdentifiers(request.Context, request.EntityId);
            }
            catch (InvalidDataException exception)
            {
                return new CommandResponsePayload
                {
                    Success = false,
                    ErrorCode = ProtocolErrorCodes.MutationPreconditionFailed,
                    ErrorMessage = exception.Message,
                };
            }

            _lastSuspensionWasIdempotentReplay = false;
            if (!request.IsSuspended
                && _entities.TryGetValue(request.EntityId, out var existing)
                && string.Equals(existing.AssignmentId, request.Context.AssignmentId, StringComparison.Ordinal)
                && existing.AuthorityEpoch == request.Context.AuthorityEpoch)
            {
                if (!existing.IsSuspended
                    && existing.Revision == request.Context.ExpectedRevision)
                {
                    _lastSuspensionWasIdempotentReplay = true;
                    return null;
                }

                if (existing.Revision != request.Context.ExpectedRevision)
                {
                    return new CommandResponsePayload
                    {
                        Success = false,
                        ErrorCode = ProtocolErrorCodes.StaleRevision,
                        ErrorMessage =
                            $"Expected entity revision {existing.Revision}, received {request.Context.ExpectedRevision}.",
                    };
                }
            }

            _entities[request.EntityId] = new FakeOwnedSurfaceResource(
                request.Context.AssignmentId,
                request.Context.AuthorityEpoch,
                request.Context.ExpectedRevision + 1,
                request.IsSuspended);
            return null;
        }

        public OwnedSurfaceLifecycleResultPayload CreateSuspensionLifecycleResult(string payloadJson)
        {
            var request = JsonSerializer.Deserialize<SetOwnedSurfaceSuspensionRequestPayload>(
                payloadJson,
                JsonOptions)!;
            if (_lastSuspensionWasIdempotentReplay
                && _entities.TryGetValue(request.EntityId, out var existing))
            {
                return new OwnedSurfaceLifecycleResultPayload
                {
                    Outcome = "enabled",
                    Postcondition = "owned-surface-unit-enabled-and-read-back",
                    AssignmentId = request.Context.AssignmentId,
                    AuthorityEpoch = request.Context.AuthorityEpoch,
                    Revision = existing.Revision,
                    IdempotencyKey = request.Context.IdempotencyKey,
                    IdempotentReplay = true,
                    Unit = CreateOwnedSurfaceState(existing.IsSuspended),
                };
            }

            return new OwnedSurfaceLifecycleResultPayload
            {
                Outcome = request.IsSuspended ? "suspended" : "enabled",
                Postcondition = request.IsSuspended
                    ? "owned-surface-unit-suspended-outside-active-world"
                    : "owned-surface-unit-enabled-and-read-back",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = request.Context.ExpectedRevision + 1,
                IdempotencyKey = request.Context.IdempotencyKey,
                Unit = CreateOwnedSurfaceState(request.IsSuspended),
            };
        }

        public void Observe(WireEnvelope command, string sessionId)
        {
            if (TryRequireArmed(sessionId, command.MessageType) is not null)
            {
                return;
            }

            switch (command.MessageType)
            {
                case GameCommandTypes.MaterializeOwnedSurfaceUnit:
                {
                    var request = JsonSerializer.Deserialize<MaterializeOwnedSurfaceUnitRequestPayload>(
                        command.PayloadJson,
                        JsonOptions)!;
                    _entities[request.EntityId] = new FakeOwnedSurfaceResource(
                        request.Context.AssignmentId,
                        request.Context.AuthorityEpoch,
                        1,
                        IsSuspended: false);
                    break;
                }
                case GameCommandTypes.RestoreOwnedSurfaceCheckpoint:
                {
                    var request = JsonSerializer.Deserialize<RestoreOwnedSurfaceCheckpointRequestPayload>(
                        command.PayloadJson,
                        JsonOptions)!;
                    _entities[request.EntityId] = new FakeOwnedSurfaceResource(
                        request.Context.AssignmentId,
                        request.Context.AuthorityEpoch,
                        request.Context.ExpectedRevision + 1,
                        IsSuspended: false);
                    break;
                }
                case GameCommandTypes.StageOwnedSurfaceCheckpoint:
                {
                    var request = JsonSerializer.Deserialize<StageOwnedSurfaceCheckpointRequestPayload>(
                        command.PayloadJson,
                        JsonOptions)!;
                    _entities[request.EntityId] = new FakeOwnedSurfaceResource(
                        request.Context.AssignmentId,
                        request.Context.AuthorityEpoch,
                        request.Context.ExpectedRevision + 1,
                        IsSuspended: true);
                    break;
                }
                case GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery:
                {
                    var request = JsonSerializer.Deserialize<OwnedSurfaceEntityRequestPayload>(
                        command.PayloadJson,
                        JsonOptions)!;
                    _entities.Remove(request.EntityId);
                    break;
                }
            }
        }

        public OwnedSurfaceResourceStatePayload Capture()
        {
            var entries = _entities
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select((pair, index) => new OwnedSurfaceResourceEntryPayload
                {
                    EntityId = pair.Key,
                    AssignmentId = pair.Value.AssignmentId,
                    AuthorityEpoch = pair.Value.AuthorityEpoch,
                    Revision = pair.Value.Revision,
                    LifecycleState = pair.Value.IsSuspended ? "suspended-retained" : "active",
                    GameUniqueId = 44 + index,
                })
                .ToArray();
            var active = entries.Count(item => item.LifecycleState == "active");
            var suspended = entries.Length - active;
            return new OwnedSurfaceResourceStatePayload
            {
                OwnedRecordCount = entries.Length,
                ActiveObjectCount = active,
                SuspendedRetainedObjectCount = suspended,
                RetainedConstructedObjectCount = entries.Length,
                MemoryReclamationSupported = false,
                SchedulerPolicy =
                    "active-and-suspended-retained-objects-consume-materialized-capacity",
                Entities = entries,
            };
        }

        private sealed record FakeOwnedSurfaceResource(
            string AssignmentId,
            long AuthorityEpoch,
            long Revision,
            bool IsSuspended);
    }

    private sealed class FakeTacticalCouplingState
    {
        private readonly object _gate = new();
        private string? _sourceEntityId;
        private string? _targetEntityId;
        private readonly List<TacticalCouplingObservationPayload> _relationships = [];

        public void SetActiveWeapon(string sourceEntityId, string targetEntityId)
        {
            lock (_gate)
            {
                _sourceEntityId = sourceEntityId;
                _targetEntityId = targetEntityId;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _sourceEntityId = null;
                _targetEntityId = null;
                _relationships.Clear();
            }
        }

        public void SetRelationship(
            string kind,
            string sourceEntityId,
            string targetEntityId,
            string detail)
        {
            lock (_gate)
            {
                _relationships.Add(new TacticalCouplingObservationPayload
                {
                    Kind = kind,
                    SourceObjectId = $"campaign-unit:unit:crimean-tom:{sourceEntityId}",
                    TargetObjectId = $"campaign-unit:unit:crimean-tom:{targetEntityId}",
                    EvidenceId = $"{kind}:integration-coupling",
                    Detail = detail,
                    UnsafeToSplit = true,
                });
            }
        }

        public TacticalCouplingStatePayload Capture()
        {
            lock (_gate)
            {
                return new TacticalCouplingStatePayload
                {
                    SceneName = "IntegrationScenario",
                    CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ActiveWeapons = _sourceEntityId is null || _targetEntityId is null
                        ? []
                        :
                        [
                            new WeaponSnapshotPayload
                            {
                                Id = "weapon:integration-coupling",
                                LaunchPlatformId = $"campaign-unit:unit:crimean-tom:{_sourceEntityId}",
                                IntendedTargetId = $"campaign-unit:unit:crimean-tom:{_targetEntityId}",
                                GuidanceType = "SemiActiveRadarHoming",
                                FlightStage = "TerminalGuidance",
                                IsLaunched = true,
                                IsDestroyed = false,
                            },
                        ],
                    Relationships = _relationships.ToArray(),
                };
            }
        }
    }

    private static async Task<JsonDocument> WaitForCampaignViewAsync(
        HttpClient client,
        string campaignId,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken,
        [CallerLineNumber] int callerLine = 0)
    {
        string lastView = "campaign view was never available";
        HttpStatusCode? lastStatus = null;
        try
        {
            while (true)
            {
                using var response = await client.GetAsync($"/campaigns/{campaignId}", cancellationToken);
                lastStatus = response.StatusCode;
                lastView = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var document = JsonDocument.Parse(lastView);
                    if (predicate(document.RootElement))
                    {
                        return document;
                    }
                    document.Dispose();
                }
                await Task.Delay(25, cancellationToken);
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException(
                $"Campaign view predicate from test line {callerLine} was not reached. "
                + $"Last HTTP {lastStatus}. Last view: {lastView}",
                exception);
        }
    }

    private static async Task<ScriptResult> RunControlScriptAsync(
        string scriptPath,
        string action,
        string runDirectory,
        string controlUrl,
        string gamePipeName,
        bool skipBuild,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(action);
        startInfo.ArgumentList.Add("-RunDirectory");
        startInfo.ArgumentList.Add(runDirectory);
        startInfo.ArgumentList.Add("-ControlUrl");
        startInfo.ArgumentList.Add(controlUrl);
        startInfo.ArgumentList.Add("-GamePipeName");
        startInfo.ArgumentList.Add(gamePipeName);
        if (ResolveArtifactsProjectDll("CrimeanTom.Companion") is { } companionDllPath)
        {
            startInfo.ArgumentList.Add("-CompanionDllPath");
            startInfo.ArgumentList.Add(companionDllPath);
        }
        if (skipBuild)
        {
            startInfo.ArgumentList.Add("-SkipBuild");
        }
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ScriptResult(process.ExitCode);
    }

    private static string? ResolveArtifactsProjectDll(string projectName)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            projectName,
            "debug",
            $"{projectName}.dll"));
        return File.Exists(path) ? path : null;
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

    private enum CrashPeerBoundary
    {
        AfterMutationBeforeProposal,
        BeforeInitialCheckpoint,
    }

    private sealed record CrashPeerObservation(
        string CommandType,
        string IdempotencyKey,
        string EntityId);

    private sealed record ScriptResult(int ExitCode)
    {
        public string FormatFailure(string action)
        {
            return $"Companion {action} failed with exit code {ExitCode}.";
        }
    }
}
