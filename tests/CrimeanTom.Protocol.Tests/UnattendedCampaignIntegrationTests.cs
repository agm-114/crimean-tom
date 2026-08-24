using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using CrimeanTom.Campaign.Transport.V1;
using Grpc.Core;
using Grpc.Net.Client;

namespace CrimeanTom.Protocol.Tests;

public sealed class UnattendedCampaignIntegrationTests
{
    [Fact]
    public async Task PlayerCreatesControlsSavesAndRestoresFullCampaignWithoutOperatorAccess()
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
        Process? campaignService = null;
        try
        {
            campaignService = StartCampaignService(
                repositoryRoot,
                runDirectory,
                campaignGrpcUrl,
                campaignHttpUrl);
            var campaignToken = await WaitForCampaignServiceAsync(runDirectory, campaignHttpUrl, testTimeout.Token);
            using var playerClient = CreatePlayerClient(campaignHttpUrl);
            using var operatorClient = CreateBearerClient(campaignHttpUrl, campaignToken);
            using (var monitor = await playerClient.GetAsync("/monitor/", testTimeout.Token))
            {
                monitor.EnsureSuccessStatusCode();
                Assert.Contains(
                    "Crimean Tom Dynamic Campaign",
                    await monitor.Content.ReadAsStringAsync(testTimeout.Token),
                    StringComparison.Ordinal);
            }

            using (var operatorRead = await playerClient.GetAsync("/campaigns", testTimeout.Token))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, operatorRead.StatusCode);
            }
            using (var grpcSpoofedOperatorWrite = new HttpRequestMessage(
                HttpMethod.Post,
                "/control/campaigns")
            {
                Content = new StringContent("{}"),
            })
            {
                grpcSpoofedOperatorWrite.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/grpc");
                using var response = await playerClient.SendAsync(
                    grpcSpoofedOperatorWrite,
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            using (var sameOriginOperatorWrite = new HttpRequestMessage(
                HttpMethod.Post,
                $"/control/campaigns/{Guid.NewGuid():N}/schedule")
            {
                Content = JsonContent.Create(new
                {
                    observedEntityIds = Array.Empty<string>(),
                    controlledEntityIds = Array.Empty<string>(),
                    health = "Healthy",
                    eligibility = "Eligible",
                    tacticalEntityCapacity = 4,
                    backlogDepth = 0,
                }),
            })
            {
                sameOriginOperatorWrite.Headers.TryAddWithoutValidation(
                    "Origin",
                    new Uri(campaignHttpUrl).GetLeftPart(UriPartial.Authority));
                sameOriginOperatorWrite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                using var response = await playerClient.SendAsync(sameOriginOperatorWrite, testTimeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using var created = await playerClient.PostAsJsonAsync(
                "/player/campaigns",
                new { name = "Player verification campaign", coalition = "Blue" },
                testTimeout.Token);
            created.EnsureSuccessStatusCode();
            using var createdBody = JsonDocument.Parse(
                await created.Content.ReadAsStringAsync(testTimeout.Token));
            var root = createdBody.RootElement;
            var campaignId = root.GetProperty("campaignId").GetString()!;
            var playerCoalitionId = root.GetProperty("coalitionId").GetString()!;
            var initialTime = root.GetProperty("timeQuanta").GetInt64();
            Assert.Equal(4_096, root.GetProperty("scenarioEntityCount").GetInt32());
            Assert.True(root.GetProperty("paused").GetBoolean());
            Assert.True(root.GetProperty("friendlyEntities").GetArrayLength() > 0);
            Assert.True(root.GetProperty("friendlyGroups").GetArrayLength() > 0);
            Assert.False(root.TryGetProperty("entities", out _));
            Assert.False(root.TryGetProperty("workers", out _));
            Assert.False(root.TryGetProperty("canonicalHash", out _));

            var friendlyEntities = root.GetProperty("friendlyEntities").EnumerateArray().ToArray();
            var tacticalAnchorEntityIds = friendlyEntities
                .Where(item => item.GetProperty("name").GetString() is "USS Yorktown" or "USS Spruance")
                .ToDictionary(
                    item => item.GetProperty("name").GetString()!,
                    item => item.GetProperty("entityId").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal(2, tacticalAnchorEntityIds.Count);
            var yorktownEntityId = tacticalAnchorEntityIds["USS Yorktown"];
            var hostedAircraft = friendlyEntities
                .Where(item => item.GetProperty("hostEntityId").GetString() == yorktownEntityId)
                .ToArray();
            Assert.Equal(2, hostedAircraft.Length);
            Assert.Equal(
                new[] { "usn_sh-2f", "usn_sh-3h" },
                hostedAircraft.Select(item => item.GetProperty("catalogUnitId").GetString())
                    .Order(StringComparer.Ordinal));
            var hostedAircraftEntityIds = hostedAircraft
                .Select(item => item.GetProperty("entityId").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var tacticalEntryEntityIds = tacticalAnchorEntityIds.Values
                .Concat(hostedAircraftEntityIds)
                .ToHashSet(StringComparer.Ordinal);
            var tacticalAnchorGroup = root.GetProperty("friendlyGroups").EnumerateArray()
                .Single(item => item.GetProperty("memberEntityIds").EnumerateArray()
                    .Any(member => member.GetString() == yorktownEntityId));
            var tacticalAnchorGroupId = tacticalAnchorGroup.GetProperty("groupId").GetString()!;
            Assert.Equal(
                tacticalEntryEntityIds.Order(StringComparer.Ordinal),
                tacticalAnchorGroup.GetProperty("memberEntityIds").EnumerateArray()
                    .Select(member => member.GetString()!)
                    .Order(StringComparer.Ordinal));
            var submarineEntity = root.GetProperty("friendlyEntities").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "USS Honolulu");
            var submarineEntityId = submarineEntity.GetProperty("entityId").GetString()!;
            var submarineGroup = root.GetProperty("friendlyGroups").EnumerateArray()
                .Single(item => item.GetProperty("memberEntityIds").EnumerateArray()
                    .Any(member => member.GetString() == submarineEntityId));
            var submarineGroupId = submarineGroup.GetProperty("groupId").GetString()!;
            var helicopterEntityIds = root.GetProperty("friendlyEntities").EnumerateArray()
                .Where(item => item.GetProperty("name").GetString()
                    is "NATO maritime helicopter flight 003" or "NATO maritime helicopter flight 004")
                .ToDictionary(
                    item => item.GetProperty("name").GetString()!,
                    item => item.GetProperty("entityId").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal(2, helicopterEntityIds.Count);
            var helicopterGroup = root.GetProperty("friendlyGroups").EnumerateArray()
                .Single(item => item.GetProperty("memberEntityIds").EnumerateArray()
                    .Any(member => member.GetString() == helicopterEntityIds.Values.First()));
            var helicopterGroupId = helicopterGroup.GetProperty("groupId").GetString()!;
            Assert.Equal(
                helicopterEntityIds.Values.Order(StringComparer.Ordinal),
                helicopterGroup.GetProperty("memberEntityIds").EnumerateArray()
                    .Select(member => member.GetString()!)
                    .Order(StringComparer.Ordinal));
            using (var offlineEntry = await playerClient.GetAsync(
                $"/player/campaigns/{campaignId}/groups/{tacticalAnchorGroupId}/tactical-entry",
                testTimeout.Token))
            {
                offlineEntry.EnsureSuccessStatusCode();
                using var offlineEntryBody = JsonDocument.Parse(
                    await offlineEntry.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.Equal("waiting-for-sea-power", offlineEntryBody.RootElement.GetProperty("state").GetString());
                Assert.False(offlineEntryBody.RootElement.GetProperty("workerConnected").GetBoolean());
                Assert.False(offlineEntryBody.RootElement.GetProperty("canEnter").GetBoolean());
                Assert.Equal(4, offlineEntryBody.RootElement.GetProperty("requiredMemberCount").GetInt32());
                Assert.All(
                    offlineEntryBody.RootElement.GetProperty("members").EnumerateArray(),
                    member => Assert.Equal("waiting-for-sea-power", member.GetProperty("status").GetString()));
            }
            using (var rejectedOfflineEntry = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{tacticalAnchorGroupId}/tactical-entry",
                new
                {
                    expectedGroupRevision = tacticalAnchorGroup.GetProperty("revision").GetInt64(),
                    idempotencyKey = "player-entry-offline-0001",
                },
                testTimeout.Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, rejectedOfflineEntry.StatusCode);
                using var rejectedBody = JsonDocument.Parse(
                    await rejectedOfflineEntry.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.Equal("tactical-entry-not-ready", rejectedBody.RootElement.GetProperty("error").GetString());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), testTimeout.Token);
            using (var stillPaused = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token))
            {
                Assert.True(stillPaused.RootElement.GetProperty("paused").GetBoolean());
                Assert.Equal(initialTime, stillPaused.RootElement.GetProperty("timeQuanta").GetInt64());
            }

            using (var resumed = await playerClient.PostAsync(
                $"/player/campaigns/{campaignId}/resume",
                content: null,
                testTimeout.Token))
            {
                resumed.EnsureSuccessStatusCode();
            }
            await WaitUntilAsync(async () =>
            {
                using var view = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token);
                return view.RootElement.GetProperty("timeQuanta").GetInt64() > initialTime;
            }, testTimeout.Token);

            using (var paused = await playerClient.PostAsync(
                $"/player/campaigns/{campaignId}/pause",
                content: null,
                testTimeout.Token))
            {
                paused.EnsureSuccessStatusCode();
            }
            using var pausedView = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token);
            var firstPausedTime = pausedView.RootElement.GetProperty("timeQuanta").GetInt64();
            await Task.Delay(TimeSpan.FromMilliseconds(500), testTimeout.Token);
            using (var stableView = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token))
            {
                Assert.Equal(firstPausedTime, stableView.RootElement.GetProperty("timeQuanta").GetInt64());
            }

            var objective = pausedView.RootElement.GetProperty("objectives").EnumerateArray().First();
            var destinationRegionId = objective.GetProperty("regionId").GetString()!;
            var group = pausedView.RootElement.GetProperty("friendlyGroups").EnumerateArray()
                .First(item => item.GetProperty("canMove").GetBoolean()
                    && item.GetProperty("groupId").GetString() != tacticalAnchorGroupId
                    && item.GetProperty("currentRegionId").GetString() != destinationRegionId);
            var groupId = group.GetProperty("groupId").GetString()!;
            var originalGroupRevision = group.GetProperty("revision").GetInt64();
            var leadEntityId = group.GetProperty("memberEntityIds")[0].GetString()!;
            var leadBefore = pausedView.RootElement.GetProperty("friendlyEntities").EnumerateArray()
                .First(item => item.GetProperty("entityId").GetString() == leadEntityId);
            var latitudeBefore = leadBefore.GetProperty("latitudeMicrodegrees").GetInt32();
            var longitudeBefore = leadBefore.GetProperty("longitudeMicrodegrees").GetInt32();
            const string moveIdempotencyKey = "player-move-e2e-0001";

            using var issuedMove = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{groupId}/orders/move",
                new
                {
                    destinationRegionId,
                    expectedGroupRevision = originalGroupRevision,
                    idempotencyKey = moveIdempotencyKey,
                },
                testTimeout.Token);
            issuedMove.EnsureSuccessStatusCode();
            using var issuedMoveBody = JsonDocument.Parse(
                await issuedMove.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("accepted", issuedMoveBody.RootElement.GetProperty("status").GetString());
            var moveOrderId = issuedMoveBody.RootElement.GetProperty("orderId").GetString()!;
            var moveCampaign = issuedMoveBody.RootElement.GetProperty("campaign");
            var orderedGroup = moveCampaign.GetProperty("friendlyGroups").EnumerateArray()
                .First(item => item.GetProperty("groupId").GetString() == groupId);
            Assert.Equal("Transit", orderedGroup.GetProperty("activeOrderKind").GetString());
            Assert.Equal(destinationRegionId, orderedGroup.GetProperty("activeOrderDestinationRegionId").GetString());
            Assert.True(orderedGroup.GetProperty("playerControlled").GetBoolean());
            Assert.Equal("Moving", orderedGroup.GetProperty("orderStatus").GetString());
            var moveHead = moveCampaign.GetProperty("head").GetInt64();
            var affectedObjective = moveCampaign.GetProperty("objectives").EnumerateArray()
                .First(item => item.GetProperty("regionId").GetString() == destinationRegionId);
            Assert.True(affectedObjective.GetProperty("playerAssignedGroupCount").GetInt32() > 0);

            using (var duplicateMove = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{groupId}/orders/move",
                new
                {
                    destinationRegionId,
                    expectedGroupRevision = originalGroupRevision,
                    idempotencyKey = moveIdempotencyKey,
                },
                testTimeout.Token))
            {
                duplicateMove.EnsureSuccessStatusCode();
                using var duplicateBody = JsonDocument.Parse(
                    await duplicateMove.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.Equal("duplicate", duplicateBody.RootElement.GetProperty("status").GetString());
                Assert.Equal(moveHead, duplicateBody.RootElement.GetProperty("campaign").GetProperty("head").GetInt64());
            }
            using (var staleMove = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{groupId}/orders/move",
                new
                {
                    destinationRegionId,
                    expectedGroupRevision = originalGroupRevision,
                    idempotencyKey = "player-move-stale-0002",
                },
                testTimeout.Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleMove.StatusCode);
            }

            using (var diagnostic = await operatorClient.GetAsync($"/campaigns/{campaignId}", testTimeout.Token))
            {
                diagnostic.EnsureSuccessStatusCode();
                using var diagnosticBody = JsonDocument.Parse(
                    await diagnostic.Content.ReadAsStringAsync(testTimeout.Token));
                var enemyGroup = diagnosticBody.RootElement.GetProperty("groups").EnumerateArray()
                    .First(item => item.GetProperty("coalitionId").GetString() != playerCoalitionId);
                using var forbidden = await playerClient.PostAsJsonAsync(
                    $"/player/campaigns/{campaignId}/groups/{enemyGroup.GetProperty("groupId").GetString()}/orders/move",
                    new
                    {
                        destinationRegionId,
                        expectedGroupRevision = enemyGroup.GetProperty("revision").GetInt64(),
                        idempotencyKey = "player-enemy-forbidden-0003",
                    },
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                using var forbiddenEntry = await playerClient.GetAsync(
                    $"/player/campaigns/{campaignId}/groups/{enemyGroup.GetProperty("groupId").GetString()}/tactical-entry",
                    testTimeout.Token);
                Assert.Equal(HttpStatusCode.Forbidden, forbiddenEntry.StatusCode);
            }

            using var tacticalChannel = GrpcChannel.ForAddress(campaignGrpcUrl, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler { UseProxy = false },
            });
            var transport = new CampaignCompanionTransport.CampaignCompanionTransportClient(tacticalChannel);
            using var tacticalCall = transport.Connect(
                new Metadata { { "authorization", $"Bearer {campaignToken}" } },
                cancellationToken: testTimeout.Token);
            await tacticalCall.RequestStream.WriteAsync(new CompanionFrame
            {
                Sequence = 1,
                Hello = new CompanionHello
                {
                    ProtocolVersion = 1,
                    ClientInstanceId = "40000000000000000000000000000004",
                    GameSessionId = "player-tactical-entry-proof",
                    CampaignId = campaignId,
                    ReconnectCommitPosition = 0,
                    RuntimeFingerprintSha256 = new string('4', 64),
                    CatalogFingerprintSha256 = new string('5', 64),
                    PlayerTaskforceId = "taskforce:Blue",
                    PlayerCoalitionId = playerCoalitionId,
                    TacticalEntityCapacity = 8,
                    TacticalReserveCapacity = 1,
                    MaterializationProfiles =
                    {
                        new TacticalMaterializationProfile
                        {
                            UnitTypeId = "usn_cg_ticonderoga",
                            VariantId = "Default",
                            LoadoutId = "Default",
                        },
                        new TacticalMaterializationProfile
                        {
                            UnitTypeId = "usn_dd_spruance",
                            VariantId = "Default",
                            LoadoutId = "Default",
                        },
                        new TacticalMaterializationProfile
                        {
                            UnitTypeId = "usn_ssn_los_angeles",
                            VariantId = "Variant31",
                            LoadoutId = "Default",
                        },
                        new TacticalMaterializationProfile
                        {
                            UnitTypeId = "usn_sh-2f",
                            VariantId = "Squadron4",
                            LoadoutId = "ASW",
                        },
                        new TacticalMaterializationProfile
                        {
                            UnitTypeId = "usn_sh-3h",
                            VariantId = "Squadron1",
                            LoadoutId = "ASW",
                        },
                    },
                },
            });
            Assert.True(await tacticalCall.ResponseStream.MoveNext(testTimeout.Token));
            Assert.Equal(ServiceFrame.PayloadOneofCase.Welcome, tacticalCall.ResponseStream.Current.PayloadCase);

            long tacticalEntryGroupRevision;
            using (var readyEntry = await playerClient.GetAsync(
                $"/player/campaigns/{campaignId}/groups/{tacticalAnchorGroupId}/tactical-entry",
                testTimeout.Token))
            {
                readyEntry.EnsureSuccessStatusCode();
                using var readyBody = JsonDocument.Parse(
                    await readyEntry.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.Equal("ready", readyBody.RootElement.GetProperty("state").GetString());
                Assert.True(readyBody.RootElement.GetProperty("workerConnected").GetBoolean());
                Assert.True(readyBody.RootElement.GetProperty("canEnter").GetBoolean());
                tacticalEntryGroupRevision = readyBody.RootElement.GetProperty("groupRevision").GetInt64();
                Assert.Equal(4, readyBody.RootElement.GetProperty("supportedMemberCount").GetInt32());
                Assert.Equal(7, readyBody.RootElement.GetProperty("availableTacticalCapacity").GetInt32());
                var readyMembers = readyBody.RootElement.GetProperty("members").EnumerateArray().ToArray();
                Assert.Equal(2, readyMembers.Count(member =>
                    member.GetProperty("status").GetString() == "supported"));
                Assert.Equal(2, readyMembers.Count(member =>
                    member.GetProperty("status").GetString() == "supported-hosted"));
                Assert.All(
                    readyMembers.Where(member => member.GetProperty("status").GetString() == "supported-hosted"),
                    member => Assert.Equal(yorktownEntityId, member.GetProperty("hostEntityId").GetString()));
            }

            const string tacticalEntryKey = "player-entry-connected-0002";
            using var requestedEntry = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{tacticalAnchorGroupId}/tactical-entry",
                new
                {
                    expectedGroupRevision = tacticalEntryGroupRevision,
                    idempotencyKey = tacticalEntryKey,
                },
                testTimeout.Token);
            if (requestedEntry.StatusCode != HttpStatusCode.Accepted)
            {
                throw new InvalidOperationException(
                    $"Player tactical entry returned {(int)requestedEntry.StatusCode}: "
                    + await requestedEntry.Content.ReadAsStringAsync(testTimeout.Token));
            }
            using var requestedEntryBody = JsonDocument.Parse(
                await requestedEntry.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("preparing", requestedEntryBody.RootElement.GetProperty("status").GetString());
            Assert.StartsWith("player-entry:", requestedEntryBody.RootElement.GetProperty("batchId").GetString());

            var queuedEntries = new Dictionary<string, TacticalAssignment>(StringComparer.Ordinal);
            while (queuedEntries.Count < 4
                && await tacticalCall.ResponseStream.MoveNext(testTimeout.Token))
            {
                if (tacticalCall.ResponseStream.Current.PayloadCase == ServiceFrame.PayloadOneofCase.Assignment
                    && tacticalEntryEntityIds.Contains(
                        tacticalCall.ResponseStream.Current.Assignment.CanonicalEntityId,
                        StringComparer.Ordinal))
                {
                    var assignment = tacticalCall.ResponseStream.Current.Assignment;
                    queuedEntries[assignment.CanonicalEntityId] = assignment;
                }
            }
            Assert.Equal(4, queuedEntries.Count);
            Assert.Equal(
                new[] { "usn_cg_ticonderoga", "usn_dd_spruance", "usn_sh-2f", "usn_sh-3h" },
                queuedEntries.Values.Select(entry => entry.UnitTypeId).Order(StringComparer.Ordinal));
            Assert.Equal(
                new[]
                {
                    "NATO maritime helicopter flight 001",
                    "NATO maritime helicopter flight 002",
                    "USS Spruance",
                    "USS Yorktown",
                },
                queuedEntries.Values.Select(entry => entry.DisplayName).Order(StringComparer.Ordinal));
            Assert.All(queuedEntries.Values, entry => Assert.Equal("taskforce:Blue", entry.TaskforceId));
            Assert.Single(queuedEntries.Values, entry => entry.FocusAfterActivation);
            Assert.All(
                queuedEntries.Values.Where(entry => hostedAircraftEntityIds.Contains(entry.CanonicalEntityId)),
                entry =>
                {
                    Assert.Equal($"unit:crimean-tom:{yorktownEntityId}", entry.HostEntityId);
                    Assert.Equal("ASW", entry.LoadoutId);
                });
            Assert.All(
                queuedEntries.Values.Where(entry => !hostedAircraftEntityIds.Contains(entry.CanonicalEntityId)),
                entry => Assert.Empty(entry.HostEntityId));

            var orderedEntries = queuedEntries.Values
                .OrderBy(entry => entry.CanonicalEntityId, StringComparer.Ordinal)
                .ToArray();
            ulong nextCompanionSequence = 2;
            for (var index = 0; index < orderedEntries.Length; index++)
            {
                var entry = orderedEntries[index];
                await tacticalCall.RequestStream.WriteAsync(new CompanionFrame
                {
                    Sequence = nextCompanionSequence++,
                    AssignmentPrepared = new AssignmentPrepared
                    {
                        AssignmentId = entry.AssignmentId,
                        EntityId = entry.EntityId,
                        AuthorityEpoch = entry.AuthorityEpoch,
                        RealizedRevision = 1,
                        CanonicalStateSha256 = new string((char)('6' + index), 64),
                        Ready = true,
                    },
                });
                if (index == 0)
                {
                    // Runtime resource discovery and assignment activation run
                    // concurrently. Prove that an initial readback observed in
                    // that window is ignored instead of terminating the stream.
                    await tacticalCall.RequestStream.WriteAsync(new CompanionFrame
                    {
                        Sequence = nextCompanionSequence++,
                        TacticalEvent = new TacticalEventProposal
                        {
                            EventId = "pre-activation-state-sync-race",
                            IdempotencyKey = "pre-activation-state-sync-race-0001",
                            AssignmentId = entry.AssignmentId,
                            EntityId = entry.EntityId,
                            AuthorityEpoch = entry.AuthorityEpoch,
                            ExpectedEntityRevision = entry.ExpectedEntityRevision,
                            EffectiveTimeQuanta = entry.HandoffTimeQuanta,
                            Kind = TacticalEventKind.StateSync,
                            LatitudeMicrodegrees = entry.LatitudeMicrodegrees,
                            LongitudeMicrodegrees = entry.LongitudeMicrodegrees,
                            HeadingMillidegrees = entry.HeadingMillidegrees,
                            FuelMilliunits = entry.InitialFuelMilliunits,
                            StoresMilliunits = entry.InitialStoresMilliunits,
                            DamageMilliunits = entry.InitialDamageMilliunits,
                            Telegraph = entry.Telegraph,
                            CheckpointSha256 = new string('b', 64),
                        },
                    });
                    using var partiallyPrepared = await playerClient.GetAsync(
                        $"/player/campaigns/{campaignId}/groups/{tacticalAnchorGroupId}/tactical-entry",
                        testTimeout.Token);
                    partiallyPrepared.EnsureSuccessStatusCode();
                    using var body = JsonDocument.Parse(
                        await partiallyPrepared.Content.ReadAsStringAsync(testTimeout.Token));
                    Assert.NotEqual("active", body.RootElement.GetProperty("state").GetString());
                }
            }
            await WaitUntilAsync(async () =>
            {
                using var entry = await playerClient.GetAsync(
                    $"/player/campaigns/{campaignId}/groups/{tacticalAnchorGroupId}/tactical-entry",
                    testTimeout.Token);
                entry.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await entry.Content.ReadAsStringAsync(testTimeout.Token));
                return body.RootElement.GetProperty("state").GetString() == "active";
            }, testTimeout.Token);
            using (var activePlayerView = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token))
            {
                var tacticalEntities = activePlayerView.RootElement.GetProperty("friendlyEntities").EnumerateArray()
                    .Where(item => tacticalEntryEntityIds.Contains(
                        item.GetProperty("entityId").GetString()!,
                        StringComparer.Ordinal))
                    .ToArray();
                var tacticalGroup = activePlayerView.RootElement.GetProperty("friendlyGroups").EnumerateArray()
                    .Single(item => item.GetProperty("groupId").GetString() == tacticalAnchorGroupId);
                Assert.Equal(4, tacticalEntities.Length);
                Assert.All(
                    tacticalEntities,
                    tacticalEntity => Assert.Equal(
                        "Tactical",
                        tacticalEntity.GetProperty("fidelity").GetString()));
                Assert.False(tacticalGroup.GetProperty("canMove").GetBoolean());
            }

            long submarineGroupRevision;
            using (var readySubmarineEntry = await playerClient.GetAsync(
                $"/player/campaigns/{campaignId}/groups/{submarineGroupId}/tactical-entry",
                testTimeout.Token))
            {
                readySubmarineEntry.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(
                    await readySubmarineEntry.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.Equal("ready", body.RootElement.GetProperty("state").GetString());
                Assert.Equal(1, body.RootElement.GetProperty("requiredMemberCount").GetInt32());
                Assert.Equal(5, body.RootElement.GetProperty("availableTacticalCapacity").GetInt32());
                Assert.Equal("supported", body.RootElement.GetProperty("members")[0]
                    .GetProperty("status").GetString());
                submarineGroupRevision = body.RootElement.GetProperty("groupRevision").GetInt64();
            }
            using (var requestedSubmarineEntry = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{submarineGroupId}/tactical-entry",
                new
                {
                    expectedGroupRevision = submarineGroupRevision,
                    idempotencyKey = "player-submarine-entry-0003",
                },
                testTimeout.Token))
            {
                Assert.Equal(HttpStatusCode.Accepted, requestedSubmarineEntry.StatusCode);
            }
            TacticalAssignment? queuedSubmarine = null;
            while (queuedSubmarine is null
                && await tacticalCall.ResponseStream.MoveNext(testTimeout.Token))
            {
                if (tacticalCall.ResponseStream.Current.PayloadCase == ServiceFrame.PayloadOneofCase.Assignment
                    && tacticalCall.ResponseStream.Current.Assignment.CanonicalEntityId == submarineEntityId)
                {
                    queuedSubmarine = tacticalCall.ResponseStream.Current.Assignment;
                }
            }
            Assert.NotNull(queuedSubmarine);
            Assert.Equal("USS Honolulu", queuedSubmarine.DisplayName);
            Assert.Equal("usn_ssn_los_angeles", queuedSubmarine.UnitTypeId);
            Assert.Equal("Variant31", queuedSubmarine.VariantId);
            Assert.Equal("Default", queuedSubmarine.LoadoutId);
            Assert.Equal("taskforce:Blue", queuedSubmarine.TaskforceId);
            await tacticalCall.RequestStream.WriteAsync(new CompanionFrame
            {
                Sequence = nextCompanionSequence++,
                AssignmentPrepared = new AssignmentPrepared
                {
                    AssignmentId = queuedSubmarine.AssignmentId,
                    EntityId = queuedSubmarine.EntityId,
                    AuthorityEpoch = queuedSubmarine.AuthorityEpoch,
                    RealizedRevision = 1,
                    CanonicalStateSha256 = new string('8', 64),
                    Ready = true,
                },
            });
            await WaitUntilAsync(async () =>
            {
                using var entry = await playerClient.GetAsync(
                    $"/player/campaigns/{campaignId}/groups/{submarineGroupId}/tactical-entry",
                    testTimeout.Token);
                entry.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await entry.Content.ReadAsStringAsync(testTimeout.Token));
                return body.RootElement.GetProperty("state").GetString() == "active";
            }, testTimeout.Token);
            using (var activeSubmarineView = await GetPlayerCampaignAsync(
                playerClient,
                campaignId,
                testTimeout.Token))
            {
                var activeSubmarine = activeSubmarineView.RootElement.GetProperty("friendlyEntities")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("entityId").GetString() == submarineEntityId);
                Assert.Equal("Tactical", activeSubmarine.GetProperty("fidelity").GetString());
                var activeSubmarineGroup = activeSubmarineView.RootElement.GetProperty("friendlyGroups")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("groupId").GetString() == submarineGroupId);
                Assert.False(activeSubmarineGroup.GetProperty("canMove").GetBoolean());
            }

            long helicopterGroupRevision;
            using (var readyHelicopterEntry = await playerClient.GetAsync(
                $"/player/campaigns/{campaignId}/groups/{helicopterGroupId}/tactical-entry",
                testTimeout.Token))
            {
                readyHelicopterEntry.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(
                    await readyHelicopterEntry.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.Equal("ready", body.RootElement.GetProperty("state").GetString());
                Assert.Equal(2, body.RootElement.GetProperty("requiredMemberCount").GetInt32());
                Assert.Equal(4, body.RootElement.GetProperty("availableTacticalCapacity").GetInt32());
                Assert.All(
                    body.RootElement.GetProperty("members").EnumerateArray(),
                    member => Assert.Equal("supported", member.GetProperty("status").GetString()));
                helicopterGroupRevision = body.RootElement.GetProperty("groupRevision").GetInt64();
            }
            using (var requestedHelicopterEntry = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{helicopterGroupId}/tactical-entry",
                new
                {
                    expectedGroupRevision = helicopterGroupRevision,
                    idempotencyKey = "player-helicopter-entry-0004",
                },
                testTimeout.Token))
            {
                Assert.Equal(HttpStatusCode.Accepted, requestedHelicopterEntry.StatusCode);
            }
            var queuedHelicopters = new Dictionary<string, TacticalAssignment>(StringComparer.Ordinal);
            while (queuedHelicopters.Count < 2
                && await tacticalCall.ResponseStream.MoveNext(testTimeout.Token))
            {
                if (tacticalCall.ResponseStream.Current.PayloadCase == ServiceFrame.PayloadOneofCase.Assignment
                    && helicopterEntityIds.Values.Contains(
                        tacticalCall.ResponseStream.Current.Assignment.CanonicalEntityId,
                        StringComparer.Ordinal))
                {
                    var assignment = tacticalCall.ResponseStream.Current.Assignment;
                    queuedHelicopters[assignment.CanonicalEntityId] = assignment;
                }
            }
            Assert.Equal(2, queuedHelicopters.Count);
            Assert.Equal(
                new[] { "usn_sh-2f", "usn_sh-3h" },
                queuedHelicopters.Values.Select(entry => entry.UnitTypeId).Order(StringComparer.Ordinal));
            Assert.Equal(
                new[] { "ASW", "ASW" },
                queuedHelicopters.Values.Select(entry => entry.LoadoutId).Order(StringComparer.Ordinal));
            Assert.All(queuedHelicopters.Values, entry =>
            {
                Assert.Equal("taskforce:Blue", entry.TaskforceId);
                Assert.Equal(500, entry.AltitudeFeet);
            });
            Assert.Single(queuedHelicopters.Values, entry => entry.FocusAfterActivation);

            var orderedHelicopters = queuedHelicopters.Values
                .OrderBy(entry => entry.CanonicalEntityId, StringComparer.Ordinal)
                .ToArray();
            await tacticalCall.RequestStream.WriteAsync(new CompanionFrame
            {
                Sequence = nextCompanionSequence++,
                AssignmentPrepared = new AssignmentPrepared
                {
                    AssignmentId = orderedHelicopters[0].AssignmentId,
                    EntityId = orderedHelicopters[0].EntityId,
                    AuthorityEpoch = orderedHelicopters[0].AuthorityEpoch,
                    RealizedRevision = 1,
                    CanonicalStateSha256 = new string('9', 64),
                    Ready = true,
                },
            });
            using (var partiallyPrepared = await playerClient.GetAsync(
                $"/player/campaigns/{campaignId}/groups/{helicopterGroupId}/tactical-entry",
                testTimeout.Token))
            {
                partiallyPrepared.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(
                    await partiallyPrepared.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.NotEqual("active", body.RootElement.GetProperty("state").GetString());
            }
            await tacticalCall.RequestStream.WriteAsync(new CompanionFrame
            {
                Sequence = nextCompanionSequence++,
                AssignmentPrepared = new AssignmentPrepared
                {
                    AssignmentId = orderedHelicopters[1].AssignmentId,
                    EntityId = orderedHelicopters[1].EntityId,
                    AuthorityEpoch = orderedHelicopters[1].AuthorityEpoch,
                    RealizedRevision = 1,
                    CanonicalStateSha256 = new string('a', 64),
                    Ready = true,
                },
            });
            await WaitUntilAsync(async () =>
            {
                using var entry = await playerClient.GetAsync(
                    $"/player/campaigns/{campaignId}/groups/{helicopterGroupId}/tactical-entry",
                    testTimeout.Token);
                entry.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await entry.Content.ReadAsStringAsync(testTimeout.Token));
                return body.RootElement.GetProperty("state").GetString() == "active";
            }, testTimeout.Token);
            using (var activeHelicopterView = await GetPlayerCampaignAsync(
                playerClient,
                campaignId,
                testTimeout.Token))
            {
                var activeHelicopters = activeHelicopterView.RootElement.GetProperty("friendlyEntities")
                    .EnumerateArray()
                    .Where(item => helicopterEntityIds.Values.Contains(
                        item.GetProperty("entityId").GetString()!,
                        StringComparer.Ordinal))
                    .ToArray();
                Assert.Equal(2, activeHelicopters.Length);
                Assert.All(
                    activeHelicopters,
                    helicopter => Assert.Equal("Tactical", helicopter.GetProperty("fidelity").GetString()));
                var activeHelicopterGroup = activeHelicopterView.RootElement.GetProperty("friendlyGroups")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("groupId").GetString() == helicopterGroupId);
                Assert.False(activeHelicopterGroup.GetProperty("canMove").GetBoolean());
            }
            using (var duplicateEntry = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{tacticalAnchorGroupId}/tactical-entry",
                new
                {
                    expectedGroupRevision = tacticalEntryGroupRevision,
                    idempotencyKey = tacticalEntryKey,
                },
                testTimeout.Token))
            {
                duplicateEntry.EnsureSuccessStatusCode();
                using var duplicateBody = JsonDocument.Parse(
                    await duplicateEntry.Content.ReadAsStringAsync(testTimeout.Token));
                Assert.Equal("duplicate", duplicateBody.RootElement.GetProperty("status").GetString());
            }

            using (var resumedMove = await playerClient.PostAsync(
                $"/player/campaigns/{campaignId}/resume",
                content: null,
                testTimeout.Token))
            {
                resumedMove.EnsureSuccessStatusCode();
            }
            await WaitUntilAsync(async () =>
            {
                using var view = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token);
                var current = view.RootElement.GetProperty("friendlyEntities").EnumerateArray()
                    .First(item => item.GetProperty("entityId").GetString() == leadEntityId);
                var currentGroup = view.RootElement.GetProperty("friendlyGroups").EnumerateArray()
                    .First(item => item.GetProperty("groupId").GetString() == groupId);
                return currentGroup.GetProperty("activeOrderId").GetString() == moveOrderId
                    && currentGroup.GetProperty("playerControlled").GetBoolean()
                    && (current.GetProperty("latitudeMicrodegrees").GetInt32() != latitudeBefore
                        || current.GetProperty("longitudeMicrodegrees").GetInt32() != longitudeBefore);
            }, testTimeout.Token);
            using (var pausedMove = await playerClient.PostAsync(
                $"/player/campaigns/{campaignId}/pause",
                content: null,
                testTimeout.Token))
            {
                pausedMove.EnsureSuccessStatusCode();
            }

            using var movedView = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token);
            var movedGroup = movedView.RootElement.GetProperty("friendlyGroups").EnumerateArray()
                .First(item => item.GetProperty("groupId").GetString() == groupId);
            using var held = await playerClient.PostAsJsonAsync(
                $"/player/campaigns/{campaignId}/groups/{groupId}/orders/cancel",
                new
                {
                    expectedGroupRevision = movedGroup.GetProperty("revision").GetInt64(),
                    idempotencyKey = "player-hold-e2e-0004",
                },
                testTimeout.Token);
            held.EnsureSuccessStatusCode();
            using var heldBody = JsonDocument.Parse(await held.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal("holding", heldBody.RootElement.GetProperty("status").GetString());
            var holdOrderId = heldBody.RootElement.GetProperty("orderId").GetString()!;
            var heldCampaign = heldBody.RootElement.GetProperty("campaign");
            var heldGroup = heldCampaign.GetProperty("friendlyGroups").EnumerateArray()
                .First(item => item.GetProperty("groupId").GetString() == groupId);
            Assert.Equal("Hold", heldGroup.GetProperty("activeOrderKind").GetString());
            Assert.Equal("Holding", heldGroup.GetProperty("orderStatus").GetString());
            Assert.True(heldGroup.GetProperty("playerControlled").GetBoolean());
            var pausedTime = heldCampaign.GetProperty("timeQuanta").GetInt64();
            var pausedHead = heldCampaign.GetProperty("head").GetInt64();

            using var saved = await playerClient.PostAsync(
                $"/player/campaigns/{campaignId}/save",
                content: null,
                testTimeout.Token);
            saved.EnsureSuccessStatusCode();
            using var savedBody = JsonDocument.Parse(
                await saved.Content.ReadAsStringAsync(testTimeout.Token));
            Assert.Equal(pausedHead, savedBody.RootElement.GetProperty("head").GetInt64());
            Assert.Equal(pausedTime, savedBody.RootElement.GetProperty("timeQuanta").GetInt64());
            Assert.Equal(64, savedBody.RootElement.GetProperty("canonicalHash").GetString()!.Length);

            StopProcess(campaignService);
            campaignService.Dispose();
            campaignService = StartCampaignService(
                repositoryRoot,
                runDirectory,
                campaignGrpcUrl,
                campaignHttpUrl);
            _ = await WaitForCampaignServiceAsync(runDirectory, campaignHttpUrl, testTimeout.Token);
            using (var expiredSession = await playerClient.GetAsync(
                $"/player/campaigns/{campaignId}",
                testTimeout.Token))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, expiredSession.StatusCode);
            }
            using (var renewedSession = await playerClient.GetAsync("/monitor/session", testTimeout.Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, renewedSession.StatusCode);
            }

            using (var campaigns = await playerClient.GetAsync("/player/campaigns", testTimeout.Token))
            {
                campaigns.EnsureSuccessStatusCode();
                using var campaignsBody = JsonDocument.Parse(
                    await campaigns.Content.ReadAsStringAsync(testTimeout.Token));
                var restoredSummary = Assert.Single(
                    campaignsBody.RootElement.EnumerateArray(),
                    item => item.GetProperty("campaignId").GetString() == campaignId);
                Assert.True(restoredSummary.GetProperty("paused").GetBoolean());
                Assert.Equal(pausedHead, restoredSummary.GetProperty("lastSavedHead").GetInt64());
            }
            using var restored = await GetPlayerCampaignAsync(playerClient, campaignId, testTimeout.Token);
            Assert.Equal("Player verification campaign", restored.RootElement.GetProperty("name").GetString());
            Assert.True(restored.RootElement.GetProperty("paused").GetBoolean());
            Assert.Equal(pausedTime, restored.RootElement.GetProperty("timeQuanta").GetInt64());
            Assert.Equal(pausedHead, restored.RootElement.GetProperty("head").GetInt64());
            Assert.Equal(pausedHead, restored.RootElement.GetProperty("lastSavedHead").GetInt64());
            var restoredGroup = restored.RootElement.GetProperty("friendlyGroups").EnumerateArray()
                .First(item => item.GetProperty("groupId").GetString() == groupId);
            Assert.Equal(holdOrderId, restoredGroup.GetProperty("activeOrderId").GetString());
            Assert.Equal("Hold", restoredGroup.GetProperty("activeOrderKind").GetString());
            Assert.True(restoredGroup.GetProperty("playerControlled").GetBoolean());
        }
        finally
        {
            if (campaignService is not null)
            {
                StopProcess(campaignService);
                campaignService.Dispose();
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

    private static async Task<JsonDocument> GetPlayerCampaignAsync(
        HttpClient client,
        string campaignId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"/player/campaigns/{campaignId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static Process StartCampaignService(
        string repositoryRoot,
        string runDirectory,
        string grpcUrl,
        string httpUrl)
    {
        var configuration = ActiveBuildConfiguration();
        var normalHostPath = Path.Combine(
            repositoryRoot,
            "src",
            "CrimeanTom.Campaign.ServiceHost",
            "bin",
            configuration,
            "net8.0",
            "CrimeanTom.Campaign.ServiceHost.dll");
        var artifactsHostPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "CrimeanTom.Campaign.ServiceHost",
            configuration.ToLowerInvariant(),
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

    private static string ActiveBuildConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        foreach (var candidate in new[] { directory.Name, directory.Parent?.Name })
        {
            if (string.Equals(candidate, "Release", StringComparison.OrdinalIgnoreCase))
            {
                return "Release";
            }
            if (string.Equals(candidate, "Debug", StringComparison.OrdinalIgnoreCase))
            {
                return "Debug";
            }
        }

        return "Debug";
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

    private static HttpClient CreatePlayerClient(string baseUrl) => new(
        new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
        })
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
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
