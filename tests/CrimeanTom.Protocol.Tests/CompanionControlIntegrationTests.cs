using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

using CrimeanTom.Campaign.Transport.V1;
using CrimeanTom.Protocol;

namespace CrimeanTom.Protocol.Tests;

public sealed class CompanionControlIntegrationTests
{
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

        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "crimean-tom-control.ps1");
        var runDirectory = CreateTemporaryDirectory();
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
                campaignHttpUrl);
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
                using var monitorResponse = await anonymousCampaignClient.GetAsync("/monitor/", testTimeout.Token);
                monitorResponse.EnsureSuccessStatusCode();
                var monitorHtml = await monitorResponse.Content.ReadAsStringAsync(testTimeout.Token);
                Assert.Contains("Crimean Tom Campaign Monitor", monitorHtml, StringComparison.Ordinal);
                using var protectedHealth = await anonymousCampaignClient.GetAsync("/health", testTimeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, protectedHealth.StatusCode);
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
            Assert.Equal(120, durabilityPolicy.RootElement.GetProperty("maximumRollbackQuanta").GetInt64());
            Assert.Contains(
                durabilityPolicy.RootElement.GetProperty("provisionalEffectFamilies").EnumerateArray(),
                item => item.GetString() == "audio");
            Assert.Contains(
                durabilityPolicy.RootElement.GetProperty("checkpointCommittedEffectFamilies").EnumerateArray(),
                item => item.GetString() == "destruction");
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
            var unrelatedEntityId = bootstrap.RootElement.GetProperty("unrelatedEntityId").GetString()!;
            var assignmentId = $"assignment:{Guid.Parse("40000000-0000-0000-0000-000000000004"):N}";
            const string gameEntityId = "unit:crimean-tom:transport-knox";

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
                $"/control/campaigns/{fuldaCampaignId}/bootstrap/fulda-gap",
                content: null,
                testTimeout.Token);
            fuldaBootstrapResponse.EnsureSuccessStatusCode();
            using var fuldaBootstrap = JsonDocument.Parse(
                await fuldaBootstrapResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(12, fuldaBootstrap.RootElement.GetProperty("regionCount").GetInt32());
            Assert.Equal(15, fuldaBootstrap.RootElement.GetProperty("entityCount").GetInt32());
            Assert.Equal(2, fuldaBootstrap.RootElement.GetProperty("abstractLandFormationCount").GetInt32());
            using var fuldaViewResponse = await campaignClient.GetAsync(
                $"/campaigns/{fuldaCampaignId}",
                testTimeout.Token);
            fuldaViewResponse.EnsureSuccessStatusCode();
            using var fuldaView = JsonDocument.Parse(
                await fuldaViewResponse.Content.ReadAsStringAsync(testTimeout.Token));
            var fuldaEntities = fuldaView.RootElement.GetProperty("entities").EnumerateArray().ToArray();
            Assert.Equal(15, fuldaEntities.Length);
            Assert.All(
                fuldaEntities.Where(item => item.GetProperty("simulationDomain").GetString() == "LandAbstract"),
                item => Assert.Equal("Operational", item.GetProperty("maximumFidelity").GetString()));
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
            Assert.Contains(fuldaOutcomes, item => item.GetProperty("kind").GetString() == "Damage");

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
            var gamePeer = RunGamePeerAsync(gamePipeName, probeSavePath, testTimeout.Token);
            await WaitForConnectedGameAsync(client, token, testTimeout.Token);
            var firstCampaignSession = await WaitForCampaignSessionAsync(campaignClient, null, testTimeout.Token);
            using var queuedAssignment = await campaignClient.PostAsJsonAsync(
                "/control/handoffs",
                new
                {
                    campaignId,
                    canonicalEntityId = primaryEntityId,
                    gameEntityId,
                    assignmentId,
                    authorityEpoch = 11,
                    idempotencyKey = "transport-materialize-1",
                    requiredRuntimeFingerprintSha256 = new string('a', 64),
                    requiredCatalogFingerprintSha256 = string.Empty,
                    unitTypeId = "usn_ff_knox_84",
                    variantId = "Variant13",
                    loadoutId = "Default",
                    taskforceId = "taskforce:Blue",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.True(
                queuedAssignment.StatusCode == HttpStatusCode.Accepted,
                $"Canonical handoff was not queued: {queuedAssignment.StatusCode} "
                + await queuedAssignment.Content.ReadAsStringAsync(testTimeout.Token));
            var assignmentFrames = await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 2,
                testTimeout.Token);
            var checkpointPublished = Assert.Single(assignmentFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished).CheckpointPublished;
            Assert.Equal(assignmentId, checkpointPublished.AssignmentId);
            using var checkpointDownload = await campaignClient.GetAsync(
                $"/content/{checkpointPublished.CheckpointSha256}",
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.OK, checkpointDownload.StatusCode);
            Assert.Equal(checkpointPublished.CheckpointLength,
                (await checkpointDownload.Content.ReadAsByteArrayAsync(testTimeout.Token)).Length);
            var prepared = Assert.Single(assignmentFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared).AssignmentPrepared;
            Assert.True(prepared.Ready, prepared.ErrorMessage);

            using var activeCampaign = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("lifecycle").GetString() == "Active"),
                testTimeout.Token);
            var activeAssignment = Assert.Single(activeCampaign.RootElement.GetProperty("assignments").EnumerateArray());
            Assert.Equal("Active", activeAssignment.GetProperty("lifecycle").GetString());
            var activeEntities = activeCampaign.RootElement.GetProperty("entities").EnumerateArray().ToArray();
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

            using var queuedCommand = await campaignClient.PostAsJsonAsync(
                "/control/commands",
                new OrderedTacticalCommand
                {
                    CommandId = Guid.NewGuid().ToString("N"),
                    IdempotencyKey = "transport-move-1",
                    AssignmentId = assignmentId,
                    EntityId = gameEntityId,
                    AuthorityEpoch = 11,
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
                testTimeout.Token);
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
                campaignHttpUrl);
            await WaitForCampaignServiceAsync(campaignServiceRunDirectory, campaignHttpUrl, testTimeout.Token);
            var secondCampaignSession = await WaitForCampaignSessionAsync(
                campaignClient,
                firstCampaignSession,
                testTimeout.Token);
            Assert.NotEqual(firstCampaignSession, secondCampaignSession);

            using var queuedQuiesce = await campaignClient.PostAsJsonAsync(
                "/control/quiesce",
                new
                {
                    campaignId,
                    assignmentId,
                    gameEntityId,
                    expectedGameRevision = 2,
                    idempotencyKey = "transport-quiesce-1",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.True(
                queuedQuiesce.StatusCode == HttpStatusCode.Accepted,
                $"Quiesce fence was not queued: {queuedQuiesce.StatusCode} "
                + await queuedQuiesce.Content.ReadAsStringAsync(testTimeout.Token)
                + Environment.NewLine
                + ReadSharedText(stdoutPath)
                + Environment.NewLine
                + ReadSharedText(Path.Combine(campaignServiceRunDirectory, "service.stdout.log")));
            var quiesceFrames = await WaitForInboundFramesAsync(
                campaignServiceRunDirectory,
                count: 5,
                testTimeout.Token,
                required: item => item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                    && item.TacticalEvent.IdempotencyKey == "transport-quiesce-1");
            Assert.Contains(quiesceFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished
                && item.CheckpointPublished.EntityRevision == 2);
            Assert.Contains(quiesceFrames, item =>
                item.PayloadCase == CompanionFrame.PayloadOneofCase.TacticalEvent
                && item.TacticalEvent.IdempotencyKey == "transport-quiesce-1");
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
                Assert.Single(releasedView.RootElement.GetProperty("assignments").EnumerateArray())
                    .GetProperty("lifecycle").GetString());
            var releasedPrimary = Assert.Single(
                releasedView.RootElement.GetProperty("entities").EnumerateArray(),
                item => item.GetProperty("entityId").GetString() == primaryEntityId);
            Assert.Equal("Operational", releasedPrimary.GetProperty("fidelity").GetString());
            Assert.Equal(800_000, releasedPrimary.GetProperty("fuelMilliunits").GetInt64());
            Assert.Equal(300_000, releasedPrimary.GetProperty("storesMilliunits").GetInt64());
            Assert.Equal(250_000, releasedPrimary.GetProperty("damageMilliunits").GetInt32());

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
                    variantId = "Variant13",
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
                    variantId = "Variant13",
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
                    VariantId = "Variant13",
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
            Assert.Equal(1, ownedResources.RootElement.GetProperty("activeObjectCount").GetInt32());
            Assert.False(
                ownedResources.RootElement.GetProperty("memoryReclamationSupported").GetBoolean());

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
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "serializable-active-weapon-launched",
                launchProbeWeapon.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                "integration_missile",
                launchProbeWeapon.RootElement.GetProperty("ammunitionId").GetString());

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
                    variantId = "Variant13",
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
                    variantId = "Variant13",
                    loadoutId = "Default",
                    taskforceId = "taskforce:Blue",
                },
                JsonOptions,
                testTimeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, crashBeforeCheckpointHandoff.StatusCode);
            var beforeCheckpointCrash = await crashBeforeCheckpointPeer;
            Assert.Equal(GameCommandTypes.CaptureOwnedSurfaceCheckpoint, beforeCheckpointCrash.CommandType);
            Assert.Equal("crash-before-checkpoint-1-checkpoint", beforeCheckpointCrash.IdempotencyKey);
            using var preparingAfterCrash = await WaitForCampaignViewAsync(
                campaignClient,
                campaignId,
                root => root.GetProperty("assignments").EnumerateArray().Any(item =>
                    item.GetProperty("assignmentId").GetString()
                        == crashBeforeCheckpointAssignmentId["assignment:".Length..]
                    && item.GetProperty("lifecycle").GetString() == "Preparing"),
                testTimeout.Token);

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
                $"/control/campaigns/{campaignId}/resolve-expired/150",
                content: null,
                testTimeout.Token);
            fallbackResponse.EnsureSuccessStatusCode();
            using var fallback = JsonDocument.Parse(
                await fallbackResponse.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(150, fallback.RootElement.GetProperty("timeQuanta").GetInt64());
            var fallbackAssignments = fallback.RootElement.GetProperty("assignments").EnumerateArray().ToArray();
            Assert.Equal(3, fallbackAssignments.Length);
            var fallbackAssignment = Assert.Single(fallbackAssignments, item =>
                item.GetProperty("assignmentId").GetString() == fallbackAssignmentId);
            Assert.Equal(150, fallbackAssignment.GetProperty("resolvedAtQuanta").GetInt64());
            Assert.Equal(120, fallbackAssignment.GetProperty("maximumLossQuanta").GetInt64());
            Assert.Equal(
                "operational-resolution-from-last-committed-checkpoint",
                fallbackAssignment.GetProperty("resolution").GetString());
            Assert.Equal(64, fallbackAssignment.GetProperty("lastCheckpointSha256").GetString()!.Length);
            var mismatchFallback = Assert.Single(fallbackAssignments, item =>
                item.GetProperty("assignmentId").GetString() == mismatchAssignmentId);
            Assert.Equal(
                "operational-resolution-without-worker-checkpoint",
                mismatchFallback.GetProperty("resolution").GetString());
            var beforeCheckpointFallback = Assert.Single(fallbackAssignments, item =>
                item.GetProperty("assignmentId").GetString() == crashBeforeCheckpointAssignmentId);
            Assert.Equal(
                "operational-resolution-without-worker-checkpoint",
                beforeCheckpointFallback.GetProperty("resolution").GetString());
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

    private static async Task RunGamePeerAsync(
        string pipeName,
        string probeSavePath,
        CancellationToken cancellationToken)
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
        for (var commandIndex = 0; commandIndex < GameCommandTypes.AllCommands.Length + 26; commandIndex++)
        {
            var command = await ReadEnvelopeAsync(pipe, cancellationToken);
            Assert.Equal(MessageKinds.Command, command.Kind);
            Assert.Equal(handshake.SessionId, command.SessionId);
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
                    RuntimeFingerprint = new RuntimeCapabilityFingerprintPayload
                    {
                        FingerprintSha256 = new string('a', 64),
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
                            VariantId = "Variant13",
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
                    CreateOwnedSurfaceCheckpoint(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.UpdateOwnedSurfaceUnit => JsonSerializer.Serialize(
                    CreateLifecycleResult(command.PayloadJson, "updated", 2, disabled: false),
                    JsonOptions),
                GameCommandTypes.SetOwnedSurfaceSuspension => JsonSerializer.Serialize(
                    CreateSuspensionLifecycleResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.RestoreOwnedSurfaceCheckpoint => JsonSerializer.Serialize(
                    CreateRestoreLifecycleResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.GetOwnedSurfaceResourceState => JsonSerializer.Serialize(
                    new OwnedSurfaceResourceStatePayload
                    {
                        OwnedRecordCount = 1,
                        ActiveObjectCount = 1,
                        RetainedConstructedObjectCount = 1,
                        MemoryReclamationSupported = false,
                        SchedulerPolicy =
                            "active-and-suspended-retained-objects-consume-materialized-capacity",
                        Entities =
                        [
                            new OwnedSurfaceResourceEntryPayload
                            {
                                EntityId = "unit:crimean-tom:integration-knox-1",
                                AssignmentId = "assignment:integration-surface",
                                AuthorityEpoch = 7,
                                Revision = 1,
                                LifecycleState = "active",
                                GameUniqueId = 44,
                            },
                        ],
                    },
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
                PayloadJson = JsonSerializer.Serialize(new CommandResponsePayload
                {
                    Success = true,
                    ResultJson = resultJson,
                }, JsonOptions),
                Priority = command.Priority,
            }, cancellationToken);
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
                    RuntimeFingerprint = new RuntimeCapabilityFingerprintPayload
                    {
                        FingerprintSha256 = new string('a', 64),
                        CatalogSha256 = "integration-catalog-sha256",
                    },
                }, JsonOptions),
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
        VariantId = "Variant13",
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
        if (request.Context.IdempotencyKey == "transport-quiesce-1-checkpoint")
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

    private static OwnedSurfaceLifecycleResultPayload CreateSuspensionLifecycleResult(
        string payloadJson)
    {
        var request = JsonSerializer.Deserialize<SetOwnedSurfaceSuspensionRequestPayload>(
            payloadJson,
            JsonOptions)!;
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
                        VariantReference = "Variant13",
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
        string httpUrl)
    {
        var hostPath = Path.Combine(
            repositoryRoot,
            "src",
            "CrimeanTom.Campaign.ServiceHost",
            "bin",
            "Debug",
            "net8.0",
            "CrimeanTom.Campaign.ServiceHost.dll");
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
        while (!File.Exists(tokenPath))
        {
            await Task.Delay(25, cancellationToken);
        }
        var token = (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim();
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
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
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

    private static async Task<IReadOnlyList<CompanionFrame>> WaitForInboundFramesAsync(
        string runDirectory,
        int count,
        CancellationToken cancellationToken,
        Func<CompanionFrame, bool>? required = null)
    {
        var spool = Path.Combine(runDirectory, "inbound-spool");
        while (true)
        {
            if (Directory.Exists(spool))
            {
                var authorityFrames = ReadInboundAuthorityFrames(runDirectory);
                if (authorityFrames.Count >= count
                    && (required is null || authorityFrames.Any(required)))
                {
                    return authorityFrames;
                }
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private static IReadOnlyList<CompanionFrame> ReadInboundAuthorityFrames(string runDirectory)
    {
        var spool = Path.Combine(runDirectory, "inbound-spool");
        return Directory.Exists(spool)
            ? Directory.EnumerateFiles(spool, "*.frame", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => CompanionFrame.Parser.ParseFrom(File.ReadAllBytes(path)))
                .Where(frame => frame.PayloadCase is
                    CompanionFrame.PayloadOneofCase.AssignmentPrepared
                    or CompanionFrame.PayloadOneofCase.CheckpointPublished
                    or CompanionFrame.PayloadOneofCase.TacticalEvent)
                .ToArray()
            : [];
    }

    private static async Task<JsonDocument> WaitForCampaignViewAsync(
        HttpClient client,
        string campaignId,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        string lastView = "campaign view was never available";
        try
        {
            while (true)
            {
                using var response = await client.GetAsync($"/campaigns/{campaignId}", cancellationToken);
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
            throw new TimeoutException($"Campaign view predicate was not reached. Last view: {lastView}", exception);
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
