using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CrimeanTom.Campaign.Transport;
using CrimeanTom.Campaign.Transport.V1;
using CrimeanTom.Protocol;

using Grpc.Core;
using Grpc.Net.Client;

namespace CrimeanTom.Companion;

public sealed class CampaignCompanionClient(
    CampaignCompanionClientOptions options,
    CampaignCompanionStateStore stateStore,
    GamePipeBroker game,
    ILogger<CampaignCompanionClient> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaterializationReadinessPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaterializationReadinessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NativeUnitReadinessPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NativeUnitReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerrainStreamingPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TerrainStreamingTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TacticalStateSyncInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AssignmentRecoveryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AuthorityRestorationStabilityDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GameSessionAuthorityDrainTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OutboundStagingTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RenderProxySyncInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TacticalSelectionFallbackInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TacticalLocalityMaintenanceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PlayerFocusQuietPeriod = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PlayerFocusRetryInterval = TimeSpan.FromMilliseconds(200);
    private const int PlayerFocusAttempts = 80;
    private const double TacticalLocalityUnloadRadiusMeters = 45_000d;
    private const double MeanEarthRadiusMeters = 6_371_000d;

    private string? _armedMutationSessionId;
    private long _armedMutationExpiresAtUnixMilliseconds;
    private readonly ConcurrentDictionary<string, string> _lastPublishedTacticalState =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _authorityRestorationObservations =
        new(StringComparer.Ordinal);
    private readonly TacticalPromotionOrchestrator _promotion = new();
    private readonly object _playerFocusGate = new();
    private readonly object _projectionGate = new();
    private readonly SemaphoreSlim _renderProjectionGate = new(1, 1);
    private readonly SemaphoreSlim _tacticalPauseGate = new(1, 1);
    private TacticalAssignment? _pendingPlayerFocus;
    private long _playerFocusGeneration;
    private long _projectionGameSessionGeneration;
    private long _projectionGeneration;
    private long _projectionRevision;
    private string? _lastAppliedRenderProjectionSignature;
    private string? _pendingRenderProjectionSignature;
    private long _pendingProjectionGeneration;
    private long _pendingProjectionRevision;
    private Dictionary<string, SurfaceRenderProxySpecPayload>? _lastProjectionSpecs;
    private Dictionary<string, SurfaceRenderProxySpecPayload>? _pendingProjectionSpecs;
    private bool _projectionRequiresFullSnapshot = true;
    private bool _debugRevealDenied;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!game.IsGameConnected || string.IsNullOrEmpty(game.SessionId))
                {
                    await Task.Delay(250, stoppingToken);
                    continue;
                }
                var expectedGameSessionGeneration = game.SessionGeneration;
                using var sessionCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                using var authorityDrain = new GameSessionAuthorityDrain();
                var sessionWatch = CancelWhenGameSessionChangesAsync(
                    expectedGameSessionGeneration,
                    sessionCancellation,
                    authorityDrain,
                    stoppingToken);
                try
                {
                    await ConnectOnceAsync(sessionCancellation.Token, authorityDrain);
                }
                finally
                {
                    sessionCancellation.Cancel();
                    await sessionWatch;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // A Sea Power reconnect is a campaign transport boundary too.
                // Re-enter ConnectOnceAsync so Hello, capabilities, event reads,
                // projection generations, and mutation arming bind to the new
                // game session rather than leaking state from the old pipe.
            }
            catch (RpcException exception) when (
                stoppingToken.IsCancellationRequested
                && exception.StatusCode == StatusCode.Cancelled)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Campaign-service session ended; reconnecting from durable cursor {Cursor}.", stateStore.ReadCursor());
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task CancelWhenGameSessionChangesAsync(
        long expectedGeneration,
        CancellationTokenSource sessionCancellation,
        GameSessionAuthorityDrain authorityDrain,
        CancellationToken lifetimeToken)
    {
        try
        {
            while (!sessionCancellation.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), lifetimeToken);
                if (!game.IsGameConnected || game.SessionGeneration != expectedGeneration)
                {
                    authorityDrain.RequestDrain();
                    var drained = await authorityDrain.WaitForDrainAsync(
                        GameSessionAuthorityDrainTimeout,
                        lifetimeToken);
                    if (!drained)
                    {
                        logger.LogWarning(
                            "Timed out draining in-flight campaign authority work after Sea Power session {SessionGeneration} ended; cancelling the stale session and relying on durable replay.",
                            expectedGeneration);
                    }
                    try
                    {
                        sessionCancellation.Cancel();
                    }
                    finally
                    {
                        if (drained)
                        {
                            authorityDrain.ReleaseDrain();
                        }
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            lifetimeToken.IsCancellationRequested
            || sessionCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task ConnectOnceAsync(
        CancellationToken cancellationToken,
        GameSessionAuthorityDrain authorityDrain)
    {
        var token = (await File.ReadAllTextAsync(options.TokenFile, cancellationToken)).Trim();
        using var channel = GrpcChannel.ForAddress(options.GrpcUri, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
            DisposeHttpClient = true,
            MaxReceiveMessageSize = CampaignTransportLimits.MaximumServiceFrameBytes,
        });
        var client = new CampaignCompanionTransport.CampaignCompanionTransportClient(channel);
        var headers = new Metadata { { "authorization", $"Bearer {token}" } };
        using var call = client.Connect(headers, cancellationToken: cancellationToken);
        var writeGate = new SemaphoreSlim(1, 1);
        ulong nextSequence = 1;
        var pending = new ConcurrentDictionary<ulong, string>();
        var capabilities = await ReadCapabilitiesAsync(cancellationToken);
        var helloResources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        var helloHealth = CompanionWorkerHealthClassifier.Classify(
            capabilities,
            helloResources,
            options.TacticalEntityCapacity,
            options.TacticalReserveCapacity);
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = nextSequence,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = options.ClientInstanceId,
                GameSessionId = game.SessionId,
                CampaignId = options.CampaignId,
                ReconnectCommitPosition = stateStore.ReadCursor(),
                RuntimeFingerprintSha256 = capabilities.RuntimeFingerprint.FingerprintSha256 ?? string.Empty,
                CatalogFingerprintSha256 = capabilities.RuntimeFingerprint.CatalogSha256 ?? string.Empty,
                Capabilities = { capabilities.SupportedMutationCommands },
                PlayerTaskforceId = capabilities.PlayerTaskforceId,
                PlayerCoalitionId = options.PlayerCoalitionId,
                TacticalEntityCapacity = options.TacticalEntityCapacity,
                TacticalReserveCapacity = options.TacticalReserveCapacity,
                Health = helloHealth.Health,
                Eligibility = helloHealth.Eligibility,
                MaterializationProfiles =
                {
                    capabilities.OwnedSurfaceMaterializationProfiles.Select(profile =>
                            new TacticalMaterializationProfile
                            {
                                UnitTypeId = profile.UnitTypeId,
                                VariantId = profile.VariantId,
                                LoadoutId = profile.LoadoutId,
                            })
                        .Concat(capabilities.OwnedLandMaterializationProfiles.Select(profile =>
                            new TacticalMaterializationProfile
                            {
                                UnitTypeId = profile.UnitTypeId,
                                VariantId = profile.VariantId,
                                LoadoutId = profile.LoadoutId,
                            })),
                },
            },
        });

        foreach (var staged in stateStore.ReadOutbound())
        {
            await SendAsync(staged.Frame, staged.Key);
        }

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeatAsync(heartbeatCancellation.Token);
        var tacticalSelectionTask = RunTacticalSelectionWatchAsync(
            token,
            heartbeatCancellation.Token);
        try
        {
            ulong previousServiceSequence = 0;
            await foreach (var frame in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                await authorityDrain.EnterAsync(cancellationToken);
                try
                {
                    if (frame.Sequence != previousServiceSequence + 1)
                    {
                        throw new InvalidDataException("Campaign-service frame sequence is non-contiguous.");
                    }
                    previousServiceSequence = frame.Sequence;
                    switch (frame.PayloadCase)
                    {
                        case ServiceFrame.PayloadOneofCase.Welcome:
                            if (frame.Welcome.ProtocolVersion != 1
                                || !string.Equals(frame.Welcome.CampaignId, options.CampaignId, StringComparison.Ordinal))
                            {
                                throw new InvalidDataException("Campaign-service welcome is incompatible.");
                            }
                            break;
                        case ServiceFrame.PayloadOneofCase.CommittedEvent:
                            await stateStore.AcceptCommittedEventAsync(frame.CommittedEvent, cancellationToken);
                            await SendAcknowledgementAsync(frame.Sequence, stateStore.ReadCursor());
                            break;
                        case ServiceFrame.PayloadOneofCase.Assignment:
                            {
                                var currentCapabilities = await ReadCapabilitiesAsync(cancellationToken);
                                var preparation = await PrepareAssignmentAsync(
                                    frame.Assignment,
                                    currentCapabilities,
                                    token,
                                    cancellationToken);
                                if (preparation.Checkpoint is { } checkpoint)
                                {
                                    await SendAsync(
                                        new CompanionFrame { CheckpointPublished = checkpoint },
                                        CheckpointSpoolKey(checkpoint));
                                }
                                var prepared = preparation.Prepared;
                                await SendAsync(
                                    new CompanionFrame { AssignmentPrepared = prepared },
                                    $"assignment-{prepared.AssignmentId}-{prepared.AuthorityEpoch}");
                                ObserveAssignmentPreparationForPlayerFocus(
                                    frame.Assignment,
                                    prepared.Ready,
                                    cancellationToken);
                                break;
                            }
                        case ServiceFrame.PayloadOneofCase.Command:
                            {
                                var proposal = await ApplyCommandAsync(frame.Command, cancellationToken);
                                await SendAsync(
                                    new CompanionFrame { TacticalEvent = proposal },
                                    $"event-{proposal.EventId}");
                                break;
                            }
                        case ServiceFrame.PayloadOneofCase.Fence:
                            {
                                var preparation = await ApplyFenceAsync(frame.Fence, token, cancellationToken);
                                await SendAsync(
                                    new CompanionFrame { CheckpointPublished = preparation.Checkpoint },
                                    CheckpointSpoolKey(preparation.Checkpoint));
                                var proposal = preparation.Proposal;
                                await SendAsync(
                                    new CompanionFrame { TacticalEvent = proposal },
                                    $"event-{proposal.EventId}");
                                break;
                            }
                        case ServiceFrame.PayloadOneofCase.StageFinalCheckpoint:
                            {
                                var staged = await StageFinalCheckpointAsync(
                                    frame.StageFinalCheckpoint,
                                    token,
                                    cancellationToken);
                                await SendAsync(
                                    new CompanionFrame { FinalCheckpointStaged = staged },
                                    StagedCheckpointSpoolKey(staged));
                                break;
                            }
                        case ServiceFrame.PayloadOneofCase.ActivationGrant:
                            await ApplyActivationGrantAsync(frame.ActivationGrant, token, cancellationToken);
                            await SendAcknowledgementAsync(frame.Sequence, stateStore.ReadCursor());
                            break;
                        case ServiceFrame.PayloadOneofCase.Acknowledgement:
                            if (pending.TryRemove(frame.Acknowledgement.CompanionSequence, out var key)
                                && frame.Acknowledgement.DurablyAccepted)
                            {
                                await stateStore.AcknowledgeOutboundAsync(key, cancellationToken);
                            }
                            break;
                        case ServiceFrame.PayloadOneofCase.Error:
                            if (frame.Error.Terminal)
                            {
                                throw new InvalidDataException($"Campaign service rejected the session: {frame.Error.Code}.");
                            }
                            break;
                    }
                }
                finally
                {
                    authorityDrain.Exit();
                }
            }
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await Task.WhenAll(heartbeatTask, tacticalSelectionTask);
            }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
            {
            }
        }

        async Task SendAcknowledgementAsync(ulong serviceSequence, long cursor)
        {
            await writeGate.WaitAsync(cancellationToken);
            try
            {
                await call.RequestStream.WriteAsync(new CompanionFrame
                {
                    Sequence = ++nextSequence,
                    Acknowledgement = new CompanionAcknowledgement
                    {
                        ServiceSequence = serviceSequence,
                        AppliedCommitPosition = cursor,
                    },
                });
            }
            finally
            {
                writeGate.Release();
            }
        }

        async Task SendAsync(CompanionFrame outbound, string spoolKey)
        {
            using var stagingCancellation = new CancellationTokenSource(OutboundStagingTimeout);
            await stateStore.StageOutboundAsync(
                spoolKey,
                outbound,
                stagingCancellation.Token);
            await writeGate.WaitAsync(cancellationToken);
            try
            {
                var sent = outbound.Clone();
                sent.Sequence = ++nextSequence;
                pending[sent.Sequence] = spoolKey;
                await call.RequestStream.WriteAsync(sent);
            }
            finally
            {
                writeGate.Release();
            }
        }

        async Task RunHeartbeatAsync(CancellationToken heartbeatToken)
        {
            var nextStateSyncAt = DateTimeOffset.UtcNow;
            var nextRenderProxySyncAt = DateTimeOffset.UtcNow;
            var nextAssignmentRecoveryAt = DateTimeOffset.UtcNow;
            var nextLocalityMaintenanceAt = DateTimeOffset.UtcNow;
            var observedGameSessionGeneration = game.SessionGeneration;
            var observedRenderResourceSignature = string.Empty;
            while (!heartbeatToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), heartbeatToken);
                if (observedGameSessionGeneration != game.SessionGeneration)
                {
                    observedGameSessionGeneration = game.SessionGeneration;
                    nextRenderProxySyncAt = DateTimeOffset.UtcNow;
                }
                CompanionHeartbeat heartbeat;
                OwnedSurfaceResourceStatePayload? resources = null;
                CapabilitiesPayload? heartbeatCapabilities = null;
                try
                {
                    var current = await ReadCapabilitiesAsync(heartbeatToken);
                    heartbeatCapabilities = current;
                    var couplingRelationships = await ReadCouplingRelationshipsAsync(heartbeatToken);
                    resources = await ReadOwnedSurfaceResourcesAsync(heartbeatToken);
                    await RecordMaterializedGameSessionsAsync(resources, heartbeatToken);
                    var workerHealth = CompanionWorkerHealthClassifier.Classify(
                        current,
                        resources,
                        options.TacticalEntityCapacity,
                        options.TacticalReserveCapacity);
                    heartbeat = new CompanionHeartbeat
                    {
                        ObservedCommitPosition = stateStore.ReadCursor(),
                        SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        GameSessionId = CurrentGameSessionId(),
                        RuntimeFingerprintSha256 = current.RuntimeFingerprint.FingerprintSha256 ?? string.Empty,
                        CatalogFingerprintSha256 = current.RuntimeFingerprint.CatalogSha256 ?? string.Empty,
                        PlayerTaskforceId = current.PlayerTaskforceId,
                        PlayerCoalitionId = options.PlayerCoalitionId,
                        MaterializationProfiles =
                        {
                            current.OwnedSurfaceMaterializationProfiles.Select(profile =>
                                    new TacticalMaterializationProfile
                                    {
                                        UnitTypeId = profile.UnitTypeId,
                                        VariantId = profile.VariantId,
                                        LoadoutId = profile.LoadoutId,
                                    })
                                .Concat(current.OwnedLandMaterializationProfiles.Select(profile =>
                                    new TacticalMaterializationProfile
                                    {
                                        UnitTypeId = profile.UnitTypeId,
                                        VariantId = profile.VariantId,
                                        LoadoutId = profile.LoadoutId,
                                    })),
                        },
                        CouplingRelationships = { couplingRelationships },
                        OwnedMaterializedEntityCount = resources.RetainedConstructedObjectCount,
                        OwnedActiveEntityCount = resources.ActiveObjectCount,
                        OwnedRetainedSuspendedEntityCount = resources.SuspendedRetainedObjectCount,
                        OwnedRecoveryRequiredEntityCount = resources.RecoveryRequiredCount,
                        TacticalEntityCapacity = options.TacticalEntityCapacity,
                        TacticalReserveCapacity = options.TacticalReserveCapacity,
                        Health = workerHealth.Health,
                        Eligibility = workerHealth.Eligibility,
                    };
                }
                catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Campaign heartbeat capability refresh failed; advertising the worker as offline.");
                    heartbeat = new CompanionHeartbeat
                    {
                        ObservedCommitPosition = stateStore.ReadCursor(),
                        SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        GameSessionId = CurrentGameSessionId(),
                        Health = "Offline",
                        Eligibility = "Disabled",
                    };
                }

                await writeGate.WaitAsync(heartbeatToken);
                try
                {
                    await call.RequestStream.WriteAsync(new CompanionFrame
                    {
                        Sequence = ++nextSequence,
                        Heartbeat = heartbeat,
                    });
                }
                finally
                {
                    writeGate.Release();
                }

                if (resources is not null && DateTimeOffset.UtcNow >= nextStateSyncAt)
                {
                    nextStateSyncAt = DateTimeOffset.UtcNow + TacticalStateSyncInterval;
                    await authorityDrain.EnterAsync(heartbeatToken);
                    try
                    {
                        var proposals = await CaptureTacticalStateChangesAsync(resources, heartbeatToken);
                        foreach (var proposal in proposals)
                        {
                            await SendAsync(
                                new CompanionFrame { TacticalEvent = proposal },
                                $"event-{proposal.EventId}");
                            _lastPublishedTacticalState[
                                StateSyncKey(proposal.AssignmentId, proposal.AuthorityEpoch)] =
                                proposal.CheckpointSha256;
                        }
                    }
                    finally
                    {
                        authorityDrain.Exit();
                    }
                }

                if (resources is not null
                    && heartbeatCapabilities is not null
                    && DateTimeOffset.UtcNow >= nextLocalityMaintenanceAt)
                {
                    nextLocalityMaintenanceAt = DateTimeOffset.UtcNow
                        + TacticalLocalityMaintenanceInterval;
                    try
                    {
                        if (await MaintainSelectedTacticalLocalityAsync(
                                resources,
                                token,
                                heartbeatToken))
                        {
                            nextRenderProxySyncAt = DateTimeOffset.UtcNow;
                        }
                    }
                    catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Selected tactical-locality maintenance failed; current campaign authority remains fenced.");
                    }
                }

                if (resources is not null)
                {
                    var resourceSignature = RenderProjectionResourceSignature(resources);
                    if (!string.Equals(
                        observedRenderResourceSignature,
                        resourceSignature,
                        StringComparison.Ordinal))
                    {
                        observedRenderResourceSignature = resourceSignature;
                        nextRenderProxySyncAt = DateTimeOffset.UtcNow;
                    }
                }

                if (resources is not null && DateTimeOffset.UtcNow >= nextAssignmentRecoveryAt)
                {
                    nextAssignmentRecoveryAt = DateTimeOffset.UtcNow + AssignmentRecoveryInterval;
                    try
                    {
                        await RecoverMissingActiveAssignmentsFromProjectionAsync(
                            resources,
                            token,
                            heartbeatToken);
                    }
                    catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        nextRenderProxySyncAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                        logger.LogWarning(
                            exception,
                            "Missing local tactical-assignment recovery failed; campaign authority remains canonical.");
                    }
                }

                if (resources is not null
                    && heartbeatCapabilities is not null
                    && DateTimeOffset.UtcNow >= nextRenderProxySyncAt)
                {
                    nextRenderProxySyncAt = DateTimeOffset.UtcNow + RenderProxySyncInterval;
                    try
                    {
                        await ReconcileFriendlyRenderProxiesAsync(
                            resources,
                            heartbeatCapabilities,
                            token,
                            heartbeatToken);
                    }
                    catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Friendly campaign render-proxy reconciliation failed; tactical authority is unaffected.");
                    }
                }
            }
        }
    }

    private async Task RunTacticalSelectionWatchAsync(
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var eventTask = game.ReadRuntimeEventAsync(cancellationToken).AsTask();
        while (!cancellationToken.IsCancellationRequested)
        {
            var fallbackTask = Task.Delay(TacticalSelectionFallbackInterval, cancellationToken);
            var completed = await Task.WhenAny(eventTask, fallbackTask);
            SelectedSurfaceRenderProxyPayload? observedSelection = null;
            if (ReferenceEquals(completed, eventTask))
            {
                var runtimeEvent = await eventTask;
                eventTask = game.ReadRuntimeEventAsync(cancellationToken).AsTask();
                if (!string.Equals(
                        runtimeEvent.EventType,
                        GameEventTypes.CampaignMapSelectionChanged,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                observedSelection = JsonSerializer.Deserialize<SelectedSurfaceRenderProxyPayload>(
                    runtimeEvent.PayloadJson,
                    JsonOptions);
            }
            try
            {
                await RequestSelectedTacticalEntryAsync(
                    serviceToken,
                    observedSelection,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Selected friendly campaign-unit tactical entry request failed.");
            }
        }
    }

    private async Task RequestSelectedTacticalEntryAsync(
        string serviceToken,
        SelectedSurfaceRenderProxyPayload? observedSelection,
        CancellationToken cancellationToken)
    {
        var selection = observedSelection;
        if (selection is null)
        {
            var result = await game.SendCommandAsync(
                GameCommandTypes.GetSelectedSurfaceRenderProxy,
                "{}",
                CommandPriorities.Control,
                cancellationToken);
            if (!result.Success)
            {
                return;
            }
            selection = JsonSerializer.Deserialize<SelectedSurfaceRenderProxyPayload>(
                result.ResultJson,
                JsonOptions);
        }

        var proxy = selection?.Proxy;
        if (selection?.HasSelection != true
            || proxy is null
            || !proxy.PhysicalProxyRequested
            || !string.Equals(proxy.Relationship, "Friendly", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(proxy.GroupId)
            || proxy.GroupRevision < 1)
        {
            _promotion.ResetSelection();
            return;
        }

        var selectedEntityId = BareCampaignEntityId(proxy.EntityId);
        if (selectedEntityId is null)
        {
            return;
        }
        var attempt = _promotion.Observe(proxy, selectedEntityId);
        if (proxy.PhysicalProxyState is "selected" or "relocating" or "terrain-streaming" or "spawning")
        {
            return;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_promotion.ShouldAttempt(attempt.RequestKey, now, 5_000))
        {
            return;
        }

        var resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        if (await BeginSelectedLocalityHandoffAsync(
                resources,
                proxy,
                serviceToken,
                cancellationToken))
        {
            return;
        }

        using var client = CreateCampaignHttpClient(serviceToken);
        client.Timeout = TimeSpan.FromSeconds(5);
        var route = $"/player/campaigns/{options.CampaignId}/groups/{proxy.GroupId}/tactical-entry";
        var focusedRoute = $"{route}?entityId={Uri.EscapeDataString(selectedEntityId)}";
        var idempotencyKey = $"sea-power-select-{selectedEntityId}-{attempt.AttemptId}";
        for (var revisionAttempt = 0; revisionAttempt < 2; revisionAttempt++)
        {
            using var inspectionResponse = await client.GetAsync(focusedRoute, cancellationToken);
            if (!inspectionResponse.IsSuccessStatusCode)
            {
                var inspectionDetail = await inspectionResponse.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Selected friendly group {GroupId} tactical-entry inspection returned {StatusCode}: {Detail}",
                    proxy.GroupId,
                    (int)inspectionResponse.StatusCode,
                    inspectionDetail);
                return;
            }

            await using var inspectionStream = await inspectionResponse.Content.ReadAsStreamAsync(
                cancellationToken);
            using var inspection = await JsonDocument.ParseAsync(
                inspectionStream,
                cancellationToken: cancellationToken);
            var root = inspection.RootElement;
            var canEnter = root.TryGetProperty("canEnter", out var canEnterElement)
                && canEnterElement.GetBoolean();
            if (!canEnter)
            {
                logger.LogDebug(
                    "Selected friendly group {GroupId} is not ready for tactical entry ({State}).",
                    proxy.GroupId,
                    root.TryGetProperty("state", out var state)
                        ? state.GetString()
                        : "unknown");
                return;
            }

            var expectedRevision = root.TryGetProperty("groupRevision", out var groupRevision)
                ? groupRevision.GetInt64()
                : proxy.GroupRevision;
            using var entryResponse = await client.PostAsJsonAsync(
                route,
                new
                {
                    expectedGroupRevision = expectedRevision,
                    idempotencyKey,
                    entityId = selectedEntityId,
                },
                JsonOptions,
                cancellationToken);
            if (entryResponse.IsSuccessStatusCode)
            {
                _promotion.MarkAccepted(attempt.RequestKey);
                logger.LogInformation(
                    "Selected friendly campaign unit {EntityId}; requested tactical entry for group {GroupId} at revision {Revision}.",
                    proxy.EntityId,
                    proxy.GroupId,
                    expectedRevision);
                return;
            }

            var detail = await entryResponse.Content.ReadAsStringAsync(cancellationToken);
            if (revisionAttempt == 0
                && entryResponse.StatusCode == HttpStatusCode.Conflict
                && IsGroupRevisionConflict(detail))
            {
                logger.LogDebug(
                    "Selected friendly group {GroupId} changed during tactical-entry inspection; refreshing once.",
                    proxy.GroupId);
                continue;
            }
            logger.LogWarning(
                "Selected friendly group {GroupId} tactical entry returned {StatusCode}: {Detail}",
                proxy.GroupId,
                (int)entryResponse.StatusCode,
                detail);
            return;
        }
    }

    private static bool IsGroupRevisionConflict(string detail)
    {
        try
        {
            using var document = JsonDocument.Parse(detail);
            return document.RootElement.TryGetProperty("error", out var error)
                && string.Equals(
                    error.GetString(),
                    "group-revision-conflict",
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<bool> BeginSelectedLocalityHandoffAsync(
        OwnedSurfaceResourceStatePayload resources,
        SurfaceRenderProxyStatePayload selectedProxy,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        if (!selectedProxy.RequiresLocalityHandoff)
        {
            return false;
        }

        var selectedEntityId = BareCampaignEntityId(selectedProxy.EntityId)
            ?? throw new InvalidDataException("The selected projection has no campaign entity identity.");
        var activeOutsideSelection = resources.Entities
            .Where(item => string.Equals(item.LifecycleState, "active", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(BareCampaignEntityId(item.EntityId), selectedEntityId, StringComparison.Ordinal)
                && (!item.HasPosition
                    || GreatCircleDistanceMeters(
                        item.Latitude,
                        item.Longitude,
                        selectedProxy.Latitude,
                        selectedProxy.Longitude) > TacticalLocalityUnloadRadiusMeters))
            .OrderBy(item => item.AssignmentId, StringComparer.Ordinal)
            .ToArray();
        if (activeOutsideSelection.Length == 0)
        {
            return false;
        }

        using var client = CreateCampaignHttpClient(serviceToken);
        client.Timeout = TimeSpan.FromSeconds(5);
        foreach (var resource in activeOutsideSelection)
        {
            var handoffKey = $"{resource.AssignmentId}:{resource.AuthorityEpoch}";
            if (!_promotion.TryBeginLocalityHandoff(handoffKey))
            {
                continue;
            }

            using var response = await client.PostAsJsonAsync(
                "/control/quiesce",
                new
                {
                    campaignId = options.CampaignId,
                    assignmentId = resource.AssignmentId,
                    gameEntityId = resource.EntityId,
                    expectedGameRevision = resource.Revision,
                    idempotencyKey = $"map-locality-handoff-{NormalizeScopedAssignmentId(resource.AssignmentId)}-{resource.AuthorityEpoch}",
                },
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _promotion.AbandonLocalityHandoff(handoffKey);
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Could not quiesce tactical assignment {AssignmentId} before promoting campaign entity {EntityId}: {StatusCode} {Detail}",
                    resource.AssignmentId,
                    selectedEntityId,
                    (int)response.StatusCode,
                    detail);
                continue;
            }
            // The POST is idempotent. Release the in-flight latch once the
            // service has accepted it so a failed checkpoint/quiesce sequence
            // can be repaired by the next selection observation.
            _promotion.AbandonLocalityHandoff(handoffKey);
            logger.LogInformation(
                "Quiescing tactical assignment {AssignmentId} before promoting distant campaign entity {EntityId}.",
                resource.AssignmentId,
                selectedEntityId);
        }
        return true;
    }

    private async Task<bool> MaintainSelectedTacticalLocalityAsync(
        OwnedSurfaceResourceStatePayload resources,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var selected = resources.Entities
            .Where(item => string.Equals(
                    item.LifecycleState,
                    "active",
                    StringComparison.OrdinalIgnoreCase)
                && item.IsSelectedByPlayer
                && item.HasPosition)
            .OrderBy(item => item.AssignmentId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected is null)
        {
            return false;
        }

        var distant = resources.Entities
            .Where(item => string.Equals(
                    item.LifecycleState,
                    "active",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.EntityId, selected.EntityId, StringComparison.Ordinal)
                && (!item.HasPosition
                    || GreatCircleDistanceMeters(
                        selected.Latitude,
                        selected.Longitude,
                        item.Latitude,
                        item.Longitude) > TacticalLocalityUnloadRadiusMeters))
            .OrderBy(item => item.AssignmentId, StringComparer.Ordinal)
            .ToArray();
        if (distant.Length > 0)
        {
            using var client = CreateCampaignHttpClient(serviceToken);
            client.Timeout = TimeSpan.FromSeconds(5);
            foreach (var resource in distant)
            {
                var handoffKey = $"{resource.AssignmentId}:{resource.AuthorityEpoch}";
                if (!_promotion.TryBeginLocalityHandoff(handoffKey))
                {
                    continue;
                }
                try
                {
                    using var response = await client.PostAsJsonAsync(
                        "/control/quiesce",
                        new
                        {
                            campaignId = options.CampaignId,
                            assignmentId = resource.AssignmentId,
                            gameEntityId = resource.EntityId,
                            expectedGameRevision = resource.Revision,
                            idempotencyKey = $"locality-distance-{NormalizeScopedAssignmentId(resource.AssignmentId)}-{resource.AuthorityEpoch}",
                        },
                        JsonOptions,
                        cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                        logger.LogWarning(
                            "Could not quiesce distant tactical assignment {AssignmentId}: {StatusCode} {Detail}",
                            resource.AssignmentId,
                            (int)response.StatusCode,
                            detail);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Quiescing tactical assignment {AssignmentId} after it moved outside the selected unit's {RadiusMeters} m locality.",
                            resource.AssignmentId,
                            TacticalLocalityUnloadRadiusMeters);
                    }
                }
                finally
                {
                    _promotion.AbandonLocalityHandoff(handoffKey);
                }
            }
            return true;
        }
        return false;
    }

    private async Task ReconcileFriendlyRenderProxiesAsync(
        OwnedSurfaceResourceStatePayload resources,
        CapabilitiesPayload capabilities,
        string serviceToken,
        CancellationToken cancellationToken,
        bool force = false)
    {
        await _renderProjectionGate.WaitAsync(cancellationToken);
        try
        {
            await ReconcileFriendlyRenderProxiesCoreAsync(
                resources,
                capabilities,
                serviceToken,
                cancellationToken,
                force);
        }
        finally
        {
            _renderProjectionGate.Release();
        }
    }

    private async Task ReconcileFriendlyRenderProxiesCoreAsync(
        OwnedSurfaceResourceStatePayload resources,
        CapabilitiesPayload capabilities,
        string serviceToken,
        CancellationToken cancellationToken,
        bool force)
    {
        using var client = CreateCampaignHttpClient(serviceToken);
        client.Timeout = TimeSpan.FromSeconds(5);
        var requestDebugReveal = options.DebugRevealAllEnemies && !_debugRevealDenied;
        var debugQuery = requestDebugReveal
            ? "?debugRevealAllEnemies=true"
            : string.Empty;
        var projectionPath =
            $"/campaigns/{options.CampaignId}/tactical-workers/{options.ClientInstanceId}/projection";
        using var requestedResponse = await client.GetAsync(
            projectionPath + debugQuery,
            cancellationToken);
        HttpResponseMessage? fallbackResponse = null;
        if (requestDebugReveal
            && requestedResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            _debugRevealDenied = true;
            logger.LogWarning(
                "Campaign service denied debug enemy projection; continuing with ordinary tactical projections for this companion process");
            fallbackResponse = await client.GetAsync(projectionPath, cancellationToken);
        }
        using var fallbackResponseLease = fallbackResponse;
        var response = fallbackResponse ?? requestedResponse;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!string.Equals(
            root.GetProperty("campaignId").GetString(),
            options.CampaignId,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Render-proxy campaign view is bound to another campaign.");
        }

        var locallyTacticalEntities = new HashSet<string>(StringComparer.Ordinal);
        var activeLocalAssignmentIds = new HashSet<string>(StringComparer.Ordinal);
        var enabledLocalAssignmentIds = new HashSet<string>(StringComparer.Ordinal);
        var releasedLocalAssignmentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in root.GetProperty("assignments").EnumerateArray())
        {
            var lifecycle = assignment.GetProperty("lifecycle").GetString() ?? string.Empty;
            var role = assignment.GetProperty("role").GetString() ?? string.Empty;
            if (role is "Primary" or "Shadow")
            {
                var assignmentId = assignment.GetProperty("assignmentId").GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(assignmentId)
                    && !assignmentId.StartsWith("assignment:", StringComparison.Ordinal))
                {
                    assignmentId = $"assignment:{assignmentId}";
                }
                var entityId = assignment.GetProperty("entityId").GetString() ?? string.Empty;
                if (lifecycle == "Released")
                {
                    releasedLocalAssignmentIds.Add(assignmentId);
                }
                else
                {
                    activeLocalAssignmentIds.Add(assignmentId);
                    locallyTacticalEntities.Add(entityId);
                    if (lifecycle == "Active")
                    {
                        enabledLocalAssignmentIds.Add(assignmentId);
                    }
                }
            }
        }
        foreach (var resource in resources.Entities)
        {
            if (BareCampaignEntityId(resource.EntityId) is { } entityId)
            {
                if (activeLocalAssignmentIds.Contains(resource.AssignmentId))
                {
                    locallyTacticalEntities.Add(entityId);
                }
            }
        }
        resources = await ContainStaleOwnedResourcesAsync(
            resources,
            activeLocalAssignmentIds,
            enabledLocalAssignmentIds,
            releasedLocalAssignmentIds,
            capabilities,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(capabilities.PlayerTaskforceId))
        {
            return;
        }
        RecoverPendingPlayerFocus(
            resources,
            activeLocalAssignmentIds,
            cancellationToken);
        locallyTacticalEntities.Remove(string.Empty);

        if (!capabilities.SupportedCommands.Contains(
            GameCommandTypes.ReconcileSurfaceRenderProxies,
            StringComparer.Ordinal))
        {
            return;
        }

        ResetProjectionForGameSessionIfNeeded();

        var sourceSignature = string.Join(
            ":",
            game.SessionGeneration,
            root.GetProperty("head").GetInt64(),
            RenderProjectionResourceSignature(resources));
        if (!force && string.Equals(
                _lastAppliedRenderProjectionSignature,
                sourceSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        if (_pendingRenderProjectionSignature is not null)
        {
            var snapshotResult = await game.SendCommandAsync(
                GameCommandTypes.GetSurfaceRenderProxyState,
                "{}",
                CommandPriorities.Control,
                cancellationToken);
            if (!snapshotResult.Success)
            {
                return;
            }
            var runtimeSnapshot = JsonSerializer.Deserialize<SurfaceRenderProxySnapshotPayload>(
                snapshotResult.ResultJson,
                JsonOptions);
            if (runtimeSnapshot is null
                || runtimeSnapshot.AppliedProjectionGeneration < _pendingProjectionGeneration
                || runtimeSnapshot.AppliedProjectionGeneration == _pendingProjectionGeneration
                    && runtimeSnapshot.AppliedProjectionRevision < _pendingProjectionRevision)
            {
                return;
            }
            _lastAppliedRenderProjectionSignature = _pendingRenderProjectionSignature;
            _lastProjectionSpecs = _pendingProjectionSpecs;
            _pendingRenderProjectionSignature = null;
            _pendingProjectionSpecs = null;
        }

        var entities = root.GetProperty("friendlyEntities").EnumerateArray().ToArray();
        var contacts = root.TryGetProperty("contacts", out var contactElement)
            ? contactElement.EnumerateArray().ToArray()
            : [];
        var debugHostileEntities = root.TryGetProperty(
                "debugHostileEntities",
                out var debugHostileElement)
            ? debugHostileElement.EnumerateArray().ToArray()
            : [];

        SurfaceRenderProxySpecPayload[] proxies = string.IsNullOrWhiteSpace(
                root.GetProperty("coalitionId").GetString())
            ? Array.Empty<SurfaceRenderProxySpecPayload>()
            : entities
                .Where(entity => !string.Equals(
                        entity.GetProperty("lifecycle").GetString(),
                        "Destroyed",
                        StringComparison.Ordinal)
                    && !locallyTacticalEntities.Contains(
                        entity.GetProperty("entityId").GetString() ?? string.Empty))
                .Select(entity => CreateRenderProxySpec(entity, capabilities))
                .Where(spec => spec is not null)
                .Cast<SurfaceRenderProxySpecPayload>()
                .Concat((debugHostileEntities.Length > 0
                        ? debugHostileEntities
                            .Where(entity => !string.Equals(
                                entity.GetProperty("lifecycle").GetString(),
                                "Destroyed",
                                StringComparison.Ordinal))
                            .Select(CreateDebugHostileRenderProxySpec)
                        : contacts
                            .Where(contact => !contact.TryGetProperty("isLocallyTactical", out var local)
                                || !local.GetBoolean())
                            .Select(CreateContactRenderProxySpec))
                    .Where(spec => spec is not null)
                    .Cast<SurfaceRenderProxySpecPayload>())
                .OrderBy(spec => spec.EntityId, StringComparer.Ordinal)
                .ToArray();

        var desiredSpecs = proxies.ToDictionary(item => item.EntityId, StringComparer.Ordinal);
        var fullSnapshot = _projectionRequiresFullSnapshot || _lastProjectionSpecs is null;
        var upserts = fullSnapshot
            ? proxies
            : proxies.Where(spec => !_lastProjectionSpecs!.TryGetValue(spec.EntityId, out var prior)
                    || !string.Equals(
                        RenderProxySpecSignature(prior),
                        RenderProxySpecSignature(spec),
                        StringComparison.Ordinal))
                .ToArray();
        var removals = fullSnapshot
            ? Array.Empty<string>()
            : _lastProjectionSpecs!.Keys
                .Where(entityId => !desiredSpecs.ContainsKey(entityId))
                .OrderBy(entityId => entityId, StringComparer.Ordinal)
                .ToArray();
        if (!fullSnapshot && upserts.Length == 0 && removals.Length == 0)
        {
            _lastAppliedRenderProjectionSignature = sourceSignature;
            return;
        }

        await EnsureRenderProxyMutationsArmedAsync(capabilities, cancellationToken);
        var projectionPosition = NextProjectionPosition();
        var result = await RequireGameCommandAsync(
            GameCommandTypes.ReconcileSurfaceRenderProxies,
            JsonSerializer.Serialize(new ReconcileSurfaceRenderProxiesRequestPayload
            {
                ProjectionGeneration = projectionPosition.Generation,
                ProjectionRevision = projectionPosition.Revision,
                CampaignId = options.CampaignId,
                TaskforceId = capabilities.PlayerTaskforceId,
                IsFullSnapshot = fullSnapshot,
                Proxies = upserts,
                RemovedEntityIds = removals,
            }, JsonOptions),
            cancellationToken,
            CommandPriorities.Bulk);
        var reconciled = JsonSerializer.Deserialize<ReconcileSurfaceRenderProxiesResultPayload>(
            result.ResultJson,
            JsonOptions) ?? throw new InvalidDataException("Render-proxy result was empty.");
        if (reconciled.ResyncRequired)
        {
            _lastAppliedRenderProjectionSignature = null;
            _pendingRenderProjectionSignature = null;
            _lastProjectionSpecs = null;
            _pendingProjectionSpecs = null;
            _projectionRequiresFullSnapshot = true;
            AdvanceProjectionGenerationPast(reconciled.AcceptedProjectionGeneration);
        }
        else if (string.Equals(reconciled.Outcome, "accepted", StringComparison.Ordinal))
        {
            _pendingRenderProjectionSignature = sourceSignature;
            _pendingProjectionSpecs = desiredSpecs;
            _pendingProjectionGeneration = projectionPosition.Generation;
            _pendingProjectionRevision = projectionPosition.Revision;
            _projectionRequiresFullSnapshot = false;
        }
        logger.LogDebug(
            "Runtime projection outcome {Outcome}; accepted {AcceptedGeneration}/{AcceptedRevision}, applied {AppliedGeneration}/{AppliedRevision}, {VisibleCount} visible proxies.",
            reconciled.Outcome,
            reconciled.AcceptedProjectionGeneration,
            reconciled.AcceptedProjectionRevision,
            reconciled.AppliedProjectionGeneration,
            reconciled.AppliedProjectionRevision,
            reconciled.Proxies.Count(item => item.IsVisible));
    }

    private static string RenderProxySpecSignature(SurfaceRenderProxySpecPayload spec) =>
        JsonSerializer.Serialize(spec, JsonOptions);

    private (long Generation, long Revision) NextProjectionPosition()
    {
        lock (_projectionGate)
        {
            return (_projectionGeneration, ++_projectionRevision);
        }
    }

    private void ResetProjectionForGameSessionIfNeeded()
    {
        lock (_projectionGate)
        {
            var gameSessionGeneration = game.SessionGeneration;
            if (_projectionGameSessionGeneration == gameSessionGeneration)
            {
                return;
            }
            _projectionGameSessionGeneration = gameSessionGeneration;
            _projectionGeneration = Math.Max(
                _projectionGeneration + 1,
                gameSessionGeneration);
            _projectionRevision = 0;
            _lastAppliedRenderProjectionSignature = null;
            _pendingRenderProjectionSignature = null;
            _lastProjectionSpecs = null;
            _pendingProjectionSpecs = null;
            _projectionRequiresFullSnapshot = true;
        }
    }

    private static string RenderProjectionResourceSignature(
        OwnedSurfaceResourceStatePayload resources) => string.Join(
            "\n",
            resources.Entities
                .OrderBy(item => item.EntityId, StringComparer.Ordinal)
                .ThenBy(item => item.AssignmentId, StringComparer.Ordinal)
                .Select(item => string.Join(
                    ":",
                    item.EntityId,
                    item.AssignmentId,
                    item.AuthorityEpoch,
                    item.Revision,
                    item.LifecycleState,
                    item.GameUniqueId)));

    private void AdvanceProjectionGenerationPast(long acceptedGeneration)
    {
        lock (_projectionGate)
        {
            _projectionGeneration = Math.Max(
                _projectionGeneration + 1,
                acceptedGeneration + 1);
            _projectionRevision = 0;
        }
    }

    private async Task RecoverMissingActiveAssignmentsFromProjectionAsync(
        OwnedSurfaceResourceStatePayload resources,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        using var client = CreateCampaignHttpClient(serviceToken);
        client.Timeout = TimeSpan.FromSeconds(5);
        using var response = await client.GetAsync(
            $"/campaigns/{options.CampaignId}/tactical-workers/{options.ClientInstanceId}/projection",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!string.Equals(
            root.GetProperty("campaignId").GetString(),
            options.CampaignId,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Tactical recovery view is bound to another campaign.");
        }

        var activeAssignmentEntities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in root.GetProperty("assignments").EnumerateArray())
        {
            var lifecycle = assignment.GetProperty("lifecycle").GetString() ?? string.Empty;
            var role = assignment.GetProperty("role").GetString() ?? string.Empty;
            if (lifecycle != "Active" || role is not ("Primary" or "Shadow"))
            {
                continue;
            }
            var assignmentId = assignment.GetProperty("assignmentId").GetString() ?? string.Empty;
            if (!assignmentId.StartsWith("assignment:", StringComparison.Ordinal))
            {
                assignmentId = $"assignment:{assignmentId}";
            }
            activeAssignmentEntities[assignmentId] =
                assignment.GetProperty("entityId").GetString() ?? string.Empty;
        }

        await RecoverMissingActiveAssignmentsAsync(
            resources,
            activeAssignmentEntities,
            serviceToken,
            cancellationToken);
    }

    private async Task RecoverMissingActiveAssignmentsAsync(
        OwnedSurfaceResourceStatePayload resources,
        IReadOnlyDictionary<string, string> activeAssignmentEntities,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var materializedAssignmentIds = resources.Entities
            .Select(item => item.AssignmentId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);
        var currentGameSessionId = CurrentGameSessionId();
        var missingAssignmentIds = activeAssignmentEntities.Keys
            .Where(assignmentId => !materializedAssignmentIds.Contains(assignmentId))
            .Where(assignmentId => stateStore.TryReadMaterializedGameSession(assignmentId) is { } priorSessionId
                && !string.Equals(priorSessionId, currentGameSessionId, StringComparison.Ordinal))
            .OrderBy(assignmentId => assignmentId, StringComparer.Ordinal)
            .ToArray();
        if (missingAssignmentIds.Length == 0)
        {
            return;
        }

        var controlledAssignmentIds = new List<string>();
        foreach (var assignmentId in missingAssignmentIds)
        {
            try
            {
                if (stateStore.ReadAssignment(assignmentId).FocusAfterActivation)
                {
                    controlledAssignmentIds.Add(assignmentId);
                }
            }
            catch (InvalidDataException)
            {
                // A server-side recovery remains safe without the optional local focus hint.
            }
        }

        using var client = CreateCampaignHttpClient(serviceToken);
        client.Timeout = TimeSpan.FromSeconds(10);
        using var response = await client.PostAsJsonAsync(
            $"/control/campaigns/{options.CampaignId}/tactical-workers/{options.ClientInstanceId}/recover-missing",
            new
            {
                missingAssignmentIds,
                controlledAssignmentIds,
            },
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Tactical assignment recovery returned {(int)response.StatusCode} ({response.StatusCode}): {detail}",
                inner: null,
                response.StatusCode);
        }
        logger.LogWarning(
            "Reported {Count} active campaign assignments missing from the local game; stale authority was released and replacement scheduling requested.",
            missingAssignmentIds.Length);
    }

    private async Task RecordMaterializedGameSessionsAsync(
        OwnedSurfaceResourceStatePayload resources,
        CancellationToken cancellationToken)
    {
        var gameSessionId = CurrentGameSessionId();
        foreach (var assignmentId in resources.Entities
            .Select(item => item.AssignmentId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal))
        {
            if (!string.Equals(
                stateStore.TryReadMaterializedGameSession(assignmentId),
                gameSessionId,
                StringComparison.Ordinal))
            {
                await stateStore.SaveMaterializedGameSessionAsync(
                    assignmentId,
                    gameSessionId,
                    cancellationToken);
            }
        }
    }

    private async Task<OwnedSurfaceResourceStatePayload> ContainStaleOwnedResourcesAsync(
        OwnedSurfaceResourceStatePayload resources,
        IReadOnlySet<string> activeLocalAssignmentIds,
        IReadOnlySet<string> enabledLocalAssignmentIds,
        IReadOnlySet<string> releasedLocalAssignmentIds,
        CapabilitiesPayload capabilities,
        CancellationToken cancellationToken)
    {
        var resumableCandidates = resources.Entities
            .Where(resource => BareCampaignEntityId(resource.EntityId) is not null
                && string.Equals(
                    resource.LifecycleState,
                    "suspended-retained",
                    StringComparison.Ordinal)
                && enabledLocalAssignmentIds.Contains(resource.AssignmentId)
                && resource.HasCheckpoint
                && !resource.RecoveryRequired)
            .OrderBy(resource => resource.EntityId, StringComparer.Ordinal)
            .ToArray();
        var now = DateTimeOffset.UtcNow;
        var candidateKeys = resumableCandidates
            .Select(AuthorityRestorationObservationKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _authorityRestorationObservations.Keys
            .Where(key => !candidateKeys.Contains(key))
            .ToArray())
        {
            _authorityRestorationObservations.Remove(staleKey);
        }
        foreach (var candidate in resumableCandidates)
        {
            _authorityRestorationObservations.TryAdd(
                AuthorityRestorationObservationKey(candidate),
                now);
        }
        var resumableResources = resumableCandidates
            .Where(resource => now - _authorityRestorationObservations[
                AuthorityRestorationObservationKey(resource)] >= AuthorityRestorationStabilityDelay)
            .ToArray();
        if (resumableResources.Length > 0)
        {
            await EnsureRenderProxyMutationsArmedAsync(capabilities, cancellationToken);
            await RunAtPausedTacticalSafePointAsync(
                async () =>
                {
                    foreach (var resource in resumableResources)
                    {
                        var result = await RequireGameCommandAsync(
                            GameCommandTypes.SetOwnedSurfaceSuspension,
                            JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
                            {
                                Context = Context(
                                    resource.AssignmentId,
                                    resource.AuthorityEpoch,
                                    resource.Revision,
                                    $"restore-active-authority-{resource.AuthorityEpoch}-{resource.Revision}"),
                                EntityId = resource.EntityId,
                                IsSuspended = false,
                            }, JsonOptions),
                            cancellationToken);
                        var enabled = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                            result.ResultJson,
                            JsonOptions) ?? throw new InvalidDataException(
                                "Active-authority restoration result was empty.");
                        await stateStore.SaveGameRevisionAsync(
                            resource.AssignmentId,
                            enabled.Revision,
                            cancellationToken);
                        _authorityRestorationObservations.Remove(
                            AuthorityRestorationObservationKey(resource));
                    }
                    return true;
                },
                "active owned-resource authority restoration",
                cancellationToken);
            resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        }

        var staleActiveResources = resources.Entities
            .Where(resource => BareCampaignEntityId(resource.EntityId) is not null
                && string.Equals(
                    resource.LifecycleState,
                    "active",
                    StringComparison.Ordinal)
                && !activeLocalAssignmentIds.Contains(resource.AssignmentId))
            .OrderBy(resource => resource.EntityId, StringComparer.Ordinal)
            .ToArray();
        if (staleActiveResources.Length > 0)
        {
            await EnsureRenderProxyMutationsArmedAsync(capabilities, cancellationToken);
            await RunAtPausedTacticalSafePointAsync(
                async () =>
                {
                    foreach (var resource in staleActiveResources)
                    {
                        if (!resource.MaterializationRollbackPending)
                        {
                            await RequireGameCommandAsync(
                                GameCommandTypes.CaptureOwnedSurfaceCheckpoint,
                                JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                                {
                                    Context = Context(
                                        resource.AssignmentId,
                                        resource.AuthorityEpoch,
                                        resource.Revision,
                                        $"stale-authority-checkpoint-{resource.AuthorityEpoch}-{resource.Revision}"),
                                    EntityId = resource.EntityId,
                                }, JsonOptions),
                                cancellationToken);
                        }
                        var result = await RequireGameCommandAsync(
                            GameCommandTypes.SetOwnedSurfaceSuspension,
                            JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
                            {
                                Context = Context(
                                    resource.AssignmentId,
                                    resource.AuthorityEpoch,
                                    resource.Revision,
                                    $"stale-authority-containment-{resource.AuthorityEpoch}-{resource.Revision}"),
                                EntityId = resource.EntityId,
                                IsSuspended = true,
                            }, JsonOptions),
                            cancellationToken);
                        var suspension = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                            result.ResultJson,
                            JsonOptions) ?? throw new InvalidDataException(
                                "Stale owned-surface containment result was empty.");
                        await stateStore.SaveGameRevisionAsync(
                            resource.AssignmentId,
                            suspension.Revision,
                            cancellationToken);
                    }
                    return true;
                },
                "stale owned-surface authority containment",
                cancellationToken);
            logger.LogWarning(
                "Suspended {Count} locally active owned records whose campaign assignments are no longer authoritative.",
                staleActiveResources.Length);
            resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        }

        var incompleteReleasedResources = resources.Entities
            .Where(resource => BareCampaignEntityId(resource.EntityId) is not null
                && releasedLocalAssignmentIds.Contains(resource.AssignmentId)
                && resource.MaterializationRollbackPending
                && resource.LifecycleState is "suspended-retained" or "recovery-required-suspended")
            .OrderBy(resource => resource.EntityId, StringComparer.Ordinal)
            .ToArray();
        var detachedUncheckpointedResources = resources.Entities
            .Where(resource => BareCampaignEntityId(resource.EntityId) is not null
                && releasedLocalAssignmentIds.Contains(resource.AssignmentId)
                && string.Equals(
                    resource.LifecycleState,
                    "detached-after-scene-loss",
                    StringComparison.Ordinal)
                && !resource.HasCheckpoint)
            .OrderBy(resource => resource.EntityId, StringComparer.Ordinal)
            .ToArray();
        if (incompleteReleasedResources.Length > 0
            || detachedUncheckpointedResources.Length > 0)
        {
            var canRollbackIncomplete = capabilities.SupportedCommands.Contains(
                GameCommandTypes.RollbackIncompleteOwnedMaterialization,
                StringComparer.Ordinal);
            var canReleaseDetached = capabilities.SupportedCommands.Contains(
                GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery,
                StringComparer.Ordinal);
            var cleanedIncompleteCount = canRollbackIncomplete
                ? incompleteReleasedResources.Length
                : 0;
            var cleanedDetachedCount = canReleaseDetached
                ? detachedUncheckpointedResources.Length
                : 0;
            if (cleanedIncompleteCount > 0 || cleanedDetachedCount > 0)
            {
                await EnsureRenderProxyMutationsArmedAsync(capabilities, cancellationToken);
                if (canReleaseDetached)
                {
                    foreach (var resource in detachedUncheckpointedResources)
                    {
                        await RequireGameCommandAsync(
                            GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery,
                            JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                            {
                                Context = Context(
                                    resource.AssignmentId,
                                    resource.AuthorityEpoch,
                                    resource.Revision,
                                    $"release-detached-{resource.AuthorityEpoch}-{resource.Revision}"),
                                EntityId = resource.EntityId,
                            }, JsonOptions),
                            cancellationToken);
                    }
                }

                if (canRollbackIncomplete && incompleteReleasedResources.Length > 0)
                {
                    await RunAtPausedTacticalSafePointAsync(
                        async () =>
                        {
                            foreach (var resource in incompleteReleasedResources)
                            {
                                await RequireGameCommandAsync(
                                    GameCommandTypes.RollbackIncompleteOwnedMaterialization,
                                    JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                                    {
                                        Context = Context(
                                            resource.AssignmentId,
                                            resource.AuthorityEpoch,
                                            resource.Revision,
                                            $"rollback-released-incomplete-{resource.AuthorityEpoch}-{resource.Revision}"),
                                        EntityId = resource.EntityId,
                                    }, JsonOptions),
                                    cancellationToken);
                            }
                            return true;
                        },
                        "released incomplete-resource cleanup",
                        cancellationToken);
                }
                logger.LogInformation(
                    "Cleaned up {RollbackCount} incomplete and {DetachedCount} detached uncheckpointed resources after positive release receipts.",
                    cleanedIncompleteCount,
                    cleanedDetachedCount);
                resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
            }
        }

        if (!capabilities.SupportedCommands.Contains(
                GameCommandTypes.ReleaseCommittedOwnedResource,
                StringComparer.Ordinal))
        {
            return resources;
        }
        var committedResources = resources.Entities
            .Where(resource => BareCampaignEntityId(resource.EntityId) is not null
                && releasedLocalAssignmentIds.Contains(resource.AssignmentId)
                && !resource.RecoveryRequired
                && (string.Equals(
                        resource.LifecycleState,
                        "hosted-on-board",
                        StringComparison.Ordinal)
                    || string.Equals(
                            resource.LifecycleState,
                            "suspended-retained",
                            StringComparison.Ordinal)
                        && resource.HasCheckpoint
                    || string.Equals(
                            resource.LifecycleState,
                            "detached-after-scene-loss",
                            StringComparison.Ordinal)
                        && resource.HasCheckpoint))
            .OrderBy(resource => resource.EntityId, StringComparer.Ordinal)
            .ToArray();
        if (committedResources.Length == 0)
        {
            return resources;
        }

        await EnsureRenderProxyMutationsArmedAsync(capabilities, cancellationToken);
        var detachedCommittedResources = committedResources
            .Where(resource => string.Equals(
                resource.LifecycleState,
                "detached-after-scene-loss",
                StringComparison.Ordinal))
            .ToArray();
        foreach (var resource in detachedCommittedResources)
        {
            await RequireGameCommandAsync(
                GameCommandTypes.ReleaseCommittedOwnedResource,
                JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                {
                    Context = Context(
                        resource.AssignmentId,
                        resource.AuthorityEpoch,
                        resource.Revision,
                        $"release-committed-{resource.AuthorityEpoch}-{resource.Revision}"),
                    EntityId = resource.EntityId,
                }, JsonOptions),
                cancellationToken);
        }

        var sceneBoundCommittedResources = committedResources
            .Where(resource => !string.Equals(
                resource.LifecycleState,
                "detached-after-scene-loss",
                StringComparison.Ordinal))
            .ToArray();
        if (sceneBoundCommittedResources.Length > 0)
        {
            await RunAtPausedTacticalSafePointAsync(
                async () =>
                {
                    foreach (var resource in sceneBoundCommittedResources)
                    {
                        await RequireGameCommandAsync(
                            GameCommandTypes.ReleaseCommittedOwnedResource,
                            JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                            {
                                Context = Context(
                                    resource.AssignmentId,
                                    resource.AuthorityEpoch,
                                    resource.Revision,
                                    $"release-committed-{resource.AuthorityEpoch}-{resource.Revision}"),
                                EntityId = resource.EntityId,
                            }, JsonOptions),
                            cancellationToken);
                    }
                    return true;
                },
                "committed owned-resource reclamation",
                cancellationToken);
        }
        logger.LogInformation(
            "Reclaimed {Count} committed owned tactical resources after campaign authority was released.",
            committedResources.Length);
        return await ReadOwnedSurfaceResourcesAsync(cancellationToken);
    }

    private static string AuthorityRestorationObservationKey(
        OwnedSurfaceResourceEntryPayload resource) => string.Join(
            ":",
            resource.EntityId,
            resource.AssignmentId,
            resource.AuthorityEpoch,
            resource.Revision);

    private static SurfaceRenderProxySpecPayload? CreateRenderProxySpec(
        JsonElement entity,
        CapabilitiesPayload capabilities)
    {
        var unitTypeId = entity.GetProperty("catalogUnitId").GetString() ?? string.Empty;
        var loadoutId = entity.GetProperty("catalogLoadoutId").GetString() ?? string.Empty;
        var profile = ResolveRenderProxyProfile(
            capabilities.OwnedSurfaceMaterializationProfiles,
            unitTypeId,
            loadoutId);
        var landProfile = profile is null
            ? ResolveLandRenderProxyProfile(
                capabilities.OwnedLandMaterializationProfiles,
                unitTypeId,
                loadoutId)
            : null;
        var entityId = entity.GetProperty("entityId").GetString() ?? string.Empty;
        return Guid.TryParseExact(entityId, "N", out _)
            ? new SurfaceRenderProxySpecPayload
            {
                EntityId = $"unit:crimean-tom:{entityId}",
                Name = entity.GetProperty("name").GetString() ?? entityId,
                Relationship = "Friendly",
                Classification = entity.GetProperty("simulationDomain").GetString() ?? string.Empty,
                Lifecycle = entity.GetProperty("lifecycle").GetString() ?? string.Empty,
                SimulationDomain = entity.GetProperty("simulationDomain").GetString() ?? string.Empty,
                GroupId = entity.TryGetProperty("groupId", out var groupId)
                    ? groupId.GetString() ?? string.Empty
                    : string.Empty,
                GroupName = entity.TryGetProperty("groupName", out var groupName)
                    ? groupName.GetString() ?? string.Empty
                    : string.Empty,
                GroupRevision = entity.TryGetProperty("groupRevision", out var groupRevision)
                    ? groupRevision.GetInt64()
                    : 0,
                UnitTypeId = unitTypeId,
                VariantId = profile?.VariantId ?? landProfile?.VariantId ?? "Default",
                LoadoutId = profile?.LoadoutId ?? landProfile?.LoadoutId ?? loadoutId,
                Latitude = entity.GetProperty("latitudeMicrodegrees").GetInt32() / 1_000_000d,
                Longitude = entity.GetProperty("longitudeMicrodegrees").GetInt32() / 1_000_000d,
                HeadingDegrees = entity.GetProperty("headingMilliDegrees").GetInt32() / 1_000d,
                CampaignTelegraph = entity.GetProperty("telegraph").GetInt32(),
            }
            : null;
    }

    private static OwnedSurfaceMaterializationProfilePayload? ResolveRenderProxyProfile(
        IReadOnlyList<OwnedSurfaceMaterializationProfilePayload> profiles,
        string unitTypeId,
        string loadoutId)
    {
        var exact = profiles.FirstOrDefault(item =>
            string.Equals(item.UnitTypeId, unitTypeId, StringComparison.Ordinal)
            && string.Equals(item.LoadoutId, loadoutId, StringComparison.Ordinal));
        if (exact is not null
            || (!string.IsNullOrWhiteSpace(loadoutId)
                && !string.Equals(loadoutId, "Default", StringComparison.Ordinal)))
        {
            return exact;
        }

        return profiles.FirstOrDefault(item => string.Equals(
            item.UnitTypeId,
            unitTypeId,
            StringComparison.Ordinal));
    }

    private static OwnedLandMaterializationProfilePayload? ResolveLandRenderProxyProfile(
        IReadOnlyList<OwnedLandMaterializationProfilePayload> profiles,
        string unitTypeId,
        string loadoutId)
    {
        var exact = profiles.FirstOrDefault(item =>
            string.Equals(item.UnitTypeId, unitTypeId, StringComparison.Ordinal)
            && string.Equals(item.LoadoutId, loadoutId, StringComparison.Ordinal));
        if (exact is not null
            || (!string.IsNullOrWhiteSpace(loadoutId)
                && !string.Equals(loadoutId, "Default", StringComparison.Ordinal)))
        {
            return exact;
        }

        return profiles.FirstOrDefault(item => string.Equals(
            item.UnitTypeId,
            unitTypeId,
            StringComparison.Ordinal));
    }

    private static SurfaceRenderProxySpecPayload? CreateContactRenderProxySpec(JsonElement contact)
    {
        const string trackPrefix = "track:";
        var trackId = contact.GetProperty("trackId").GetString() ?? string.Empty;
        if (!trackId.StartsWith(trackPrefix, StringComparison.Ordinal)
            || trackId.Length <= trackPrefix.Length)
        {
            return null;
        }
        var classification = contact.GetProperty("classification").GetString() ?? "Unknown";
        var confidence = contact.GetProperty("confidence").GetString() ?? "Unknown";
        return new SurfaceRenderProxySpecPayload
        {
            EntityId = $"contact:crimean-tom:{trackId[trackPrefix.Length..]}",
            Name = $"{classification} contact ({confidence})",
            Relationship = "HostileContact",
            Classification = classification,
            Confidence = confidence,
            UncertaintyRadiusMeters = contact.GetProperty("uncertaintyRadiusMeters").GetInt32(),
            Lifecycle = "Detected",
            SimulationDomain = classification,
            Latitude = contact.GetProperty("latitudeMicrodegrees").GetInt32() / 1_000_000d,
            Longitude = contact.GetProperty("longitudeMicrodegrees").GetInt32() / 1_000_000d,
        };
    }

    private static SurfaceRenderProxySpecPayload? CreateDebugHostileRenderProxySpec(JsonElement entity)
    {
        var entityId = entity.GetProperty("entityId").GetString() ?? string.Empty;
        if (!Guid.TryParseExact(entityId, "N", out _))
        {
            return null;
        }
        var simulationDomain = entity.GetProperty("simulationDomain").GetString() ?? "Unknown";
        return new SurfaceRenderProxySpecPayload
        {
            EntityId = $"contact:crimean-tom:{entityId}",
            Name = entity.GetProperty("name").GetString() ?? entityId,
            Relationship = "HostileContact",
            Classification = simulationDomain,
            Confidence = "DebugExact",
            UncertaintyRadiusMeters = 0,
            Lifecycle = entity.GetProperty("lifecycle").GetString() ?? "Unknown",
            SimulationDomain = simulationDomain,
            UnitTypeId = entity.GetProperty("catalogUnitId").GetString() ?? string.Empty,
            Latitude = entity.GetProperty("latitudeMicrodegrees").GetInt32() / 1_000_000d,
            Longitude = entity.GetProperty("longitudeMicrodegrees").GetInt32() / 1_000_000d,
            HeadingDegrees = entity.GetProperty("headingMilliDegrees").GetInt32() / 1_000d,
        };
    }

    private string? BareCampaignEntityId(string entityId) =>
        CampaignEntityRef.TryParseCanonical(options.CampaignId, entityId, out var entity)
            ? entity.EntityId.ToString("N")
            : null;

    private async Task EnsureRenderProxyMutationsArmedAsync(
        CapabilitiesPayload capabilities,
        CancellationToken cancellationToken) =>
        await EnsureMutationModeArmedAsync(capabilities, 30_000, cancellationToken);

    private async Task EnsureMutationModeArmedAsync(
        CapabilitiesPayload capabilities,
        long minimumRemainingMilliseconds,
        CancellationToken cancellationToken)
    {
        var sessionId = CurrentGameSessionId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var advertisedExpiry = capabilities.MutationModeExpiresAtUnixMilliseconds ?? 0;
        if (capabilities.MutatingCommandsEnabled
            && advertisedExpiry > now + minimumRemainingMilliseconds)
        {
            _armedMutationSessionId = sessionId;
            _armedMutationExpiresAtUnixMilliseconds = advertisedExpiry;
            return;
        }
        if (string.Equals(_armedMutationSessionId, sessionId, StringComparison.Ordinal)
            && _armedMutationExpiresAtUnixMilliseconds > now + minimumRemainingMilliseconds)
        {
            return;
        }

        var armResult = await RequireGameCommandAsync(
            GameCommandTypes.ArmOwnedMutationMode,
            JsonSerializer.Serialize(new ArmOwnedMutationModeRequestPayload
            {
                SessionId = sessionId,
                RuntimeFingerprintSha256 = capabilities.RuntimeFingerprint.FingerprintSha256 ?? string.Empty,
                Confirmation = "enable-owned-mutations",
                DurationSeconds = 300,
            }, JsonOptions),
            cancellationToken);
        var armed = JsonSerializer.Deserialize<ArmOwnedMutationModeResultPayload>(
            armResult.ResultJson,
            JsonOptions) ?? throw new InvalidDataException("Mutation arming result was empty.");
        if (!string.Equals(armed.SessionId, sessionId, StringComparison.Ordinal)
            || armed.ExpiresAtUnixMilliseconds is not long expiresAtUnixMilliseconds)
        {
            throw new InvalidDataException("Mutation arming did not bind to the current game session.");
        }
        _armedMutationSessionId = sessionId;
        _armedMutationExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
    }

    private async Task<CapabilitiesPayload> ReadCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var result = await game.SendCommandAsync(
            GameCommandTypes.GetCapabilities,
            "{}",
            CommandPriorities.Control,
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Game capabilities unavailable: {result.ErrorCode}.");
        }
        return JsonSerializer.Deserialize<CapabilitiesPayload>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Game capabilities were empty.");
    }

    private async Task<IReadOnlyList<TacticalCouplingRelationship>> ReadCouplingRelationshipsAsync(
        CancellationToken cancellationToken)
    {
        var result = await game.SendCommandAsync(
            GameCommandTypes.GetTacticalCouplingState,
            "{}",
            CommandPriorities.Control,
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Game coupling state unavailable: {result.ErrorCode}.");
        }
        var state = JsonSerializer.Deserialize<TacticalCouplingStatePayload>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Game coupling state was empty.");
        var weapons = state.ActiveWeapons.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var relationships = new List<TacticalCouplingRelationship>();
        foreach (var weapon in state.ActiveWeapons
            .Where(item => item.IsLaunched && !item.IsDestroyed)
            .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var participants = new HashSet<string>(StringComparer.Ordinal);
            ResolveCampaignParticipants(
                weapon.LaunchPlatformId,
                weapons,
                participants,
                new HashSet<string>(StringComparer.Ordinal));
            ResolveCampaignParticipants(
                weapon.IntendedTargetId,
                weapons,
                participants,
                new HashSet<string>(StringComparer.Ordinal));
            var ordered = participants.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 1)
            {
                relationships.Add(Relationship(weapon, ordered[0], ordered[0]));
                continue;
            }
            for (var left = 0; left < ordered.Length; left++)
            {
                for (var right = left + 1; right < ordered.Length; right++)
                {
                    relationships.Add(Relationship(weapon, ordered[left], ordered[right]));
                }
            }
        }
        foreach (var observation in state.Relationships
            .Where(item => !string.IsNullOrWhiteSpace(item.Kind))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal))
        {
            var sources = new HashSet<string>(StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            ResolveCampaignParticipants(
                observation.SourceObjectId,
                weapons,
                sources,
                new HashSet<string>(StringComparer.Ordinal));
            ResolveCampaignParticipants(
                observation.TargetObjectId,
                weapons,
                targets,
                new HashSet<string>(StringComparer.Ordinal));
            foreach (var source in sources.OrderBy(item => item, StringComparer.Ordinal))
            {
                foreach (var target in targets.OrderBy(item => item, StringComparer.Ordinal))
                {
                    relationships.Add(Relationship(observation, source, target));
                }
            }
        }
        return relationships
            .GroupBy(item => string.Join('\n',
                item.Kind,
                item.SourceEntityId,
                item.TargetEntityId,
                item.EvidenceId,
                item.GuidanceType,
                item.UnsafeToSplit))
            .Select(group => group.First())
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.SourceEntityId, StringComparer.Ordinal)
            .ThenBy(item => item.TargetEntityId, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .Take(4_096)
            .ToArray();
    }

    private async Task<OwnedSurfaceResourceStatePayload> ReadOwnedSurfaceResourcesAsync(
        CancellationToken cancellationToken)
    {
        var result = await game.SendCommandAsync(
            GameCommandTypes.GetOwnedSurfaceResourceState,
            "{}",
            CommandPriorities.Control,
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Game owned-resource state unavailable: {result.ErrorCode}.");
        }
        return JsonSerializer.Deserialize<OwnedSurfaceResourceStatePayload>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Game owned-resource state was empty.");
    }

    private static TacticalCouplingRelationship Relationship(
        WeaponSnapshotPayload weapon,
        string sourceEntityId,
        string targetEntityId) => new()
    {
        Kind = "active-weapon",
        SourceEntityId = sourceEntityId,
        TargetEntityId = targetEntityId,
        EvidenceId = weapon.Id,
        GuidanceType = weapon.GuidanceType,
        UnsafeToSplit = true,
    };

    private static TacticalCouplingRelationship Relationship(
        TacticalCouplingObservationPayload observation,
        string sourceEntityId,
        string targetEntityId) => new()
    {
        Kind = observation.Kind,
        SourceEntityId = sourceEntityId,
        TargetEntityId = targetEntityId,
        EvidenceId = observation.EvidenceId,
        GuidanceType = observation.Detail,
        UnsafeToSplit = observation.UnsafeToSplit,
    };

    private void ResolveCampaignParticipants(
        string? objectId,
        IReadOnlyDictionary<string, WeaponSnapshotPayload> weapons,
        ISet<string> participants,
        ISet<string> visitedWeapons)
    {
        if (CampaignEntityId(objectId) is { } entityId)
        {
            participants.Add(entityId);
            return;
        }
        if (string.IsNullOrWhiteSpace(objectId)
            || !weapons.TryGetValue(objectId, out var weapon)
            || !visitedWeapons.Add(objectId))
        {
            return;
        }
        ResolveCampaignParticipants(weapon.LaunchPlatformId, weapons, participants, visitedWeapons);
        ResolveCampaignParticipants(weapon.IntendedTargetId, weapons, participants, visitedWeapons);
    }

    private string? CampaignEntityId(string? stableUnitId) =>
        stableUnitId is not null
        && CampaignEntityRef.TryParseStableUnitId(options.CampaignId, stableUnitId, out var entity)
            ? entity.EntityId.ToString("N")
            : null;

    private async Task<AssignmentPreparation> PrepareAssignmentAsync(
        TacticalAssignment assignment,
        CapabilitiesPayload capabilities,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var pauseGateHeld = false;
        var resumeAfterPreparation = false;
        try
        {
            var isShadow = assignment.Role == AssignmentRole.Shadow;
            if (!assignment.GloballyPaused
                || assignment.Role is not (AssignmentRole.Primary or AssignmentRole.Shadow)
                || !MatchesRequirement(assignment.RequiredRuntimeFingerprintSha256, capabilities.RuntimeFingerprint.FingerprintSha256)
                || !MatchesRequirement(assignment.RequiredCatalogFingerprintSha256, capabilities.RuntimeFingerprint.CatalogSha256))
            {
                throw new InvalidOperationException("Assignment pause, role, runtime, or catalog requirements are incompatible.");
            }
            await stateStore.SaveAssignmentAsync(assignment, cancellationToken);
            try
            {
                await ReconcileFriendlyRenderProxiesAsync(
                    await ReadOwnedSurfaceResourcesAsync(cancellationToken),
                    capabilities,
                    serviceToken,
                    cancellationToken,
                    force: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Could not pre-empt a presentation proxy before preparing tactical assignment {AssignmentId}; assignment preparation will continue.",
                    assignment.AssignmentId);
            }
            var sourceCheckpoint = await DownloadCheckpointAsync(
                assignment,
                serviceToken,
                cancellationToken);
            var materializationTaskforceId = await ResolveMaterializationTaskforceIdAsync(
                assignment,
                capabilities,
                cancellationToken);

            // Projection reconciliation can contain stale owned resources at a
            // paused safe point. Complete it before taking the pause gate so
            // the projection and pause locks always have one acquisition order.
            await _tacticalPauseGate.WaitAsync(cancellationToken);
            pauseGateHeld = true;
            var inspectionResult = await RequireGameCommandAsync(
                GameCommandTypes.GetUiInspection,
                "{}",
                cancellationToken: cancellationToken);
            var inspection = JsonSerializer.Deserialize<UiInspectionPayload>(
                inspectionResult.ResultJson,
                JsonOptions) ?? throw new InvalidDataException(
                    "Assignment preparation UI inspection was empty.");
            resumeAfterPreparation = !inspection.IsPaused;
            if (resumeAfterPreparation)
            {
                await RequireGameCommandAsync(
                    GameCommandTypes.SetExperimentalPauseState,
                    JsonSerializer.Serialize(
                        new ExperimentalPauseRequestPayload { IsPaused = true },
                        JsonOptions),
                    cancellationToken);
            }
            await EnsureMutationModeArmedAsync(capabilities, 30_000, cancellationToken);
            if (!string.IsNullOrWhiteSpace(assignment.HostEntityId))
            {
                if (isShadow)
                {
                    throw new InvalidOperationException(
                        "Hosted-aircraft shadow preparation is not supported until native recovery is portable.");
                }
                var hostResources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
                var host = hostResources.Entities.FirstOrDefault(item =>
                    string.Equals(item.EntityId, assignment.HostEntityId, StringComparison.Ordinal)
                    && string.Equals(item.LifecycleState, "active", StringComparison.OrdinalIgnoreCase)
                    && item.NativeInitialized);
                if (host is null)
                {
                    throw new InvalidOperationException(
                        "The campaign aircraft's native host must be materialized before its onboard inventory is registered.");
                }

                var hostedResult = await RequireGameCommandAsync(
                    GameCommandTypes.RegisterOwnedHostedAircraft,
                    JsonSerializer.Serialize(new RegisterOwnedHostedAircraftRequestPayload
                    {
                        Context = Context(
                            assignment.AssignmentId,
                            assignment.AuthorityEpoch,
                            assignment.ExpectedEntityRevision,
                            assignment.IdempotencyKey),
                        EntityId = assignment.EntityId,
                        DisplayName = assignment.DisplayName,
                        HostEntityId = assignment.HostEntityId,
                        UnitTypeId = assignment.UnitTypeId,
                        VariantId = assignment.VariantId,
                        LoadoutId = assignment.LoadoutId,
                        FuelFractionMilliunits = assignment.InitialFuelMilliunits,
                    }, JsonOptions),
                    cancellationToken);
                var hosted = JsonSerializer.Deserialize<RegisterOwnedHostedAircraftResultPayload>(
                    hostedResult.ResultJson,
                    JsonOptions) ?? throw new InvalidDataException(
                        "Hosted-aircraft registration result was empty.");
                var hostedStateHash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(hostedResult.ResultJson))).ToLowerInvariant();
                await stateStore.SaveGameRevisionAsync(
                    assignment.AssignmentId,
                    hosted.Revision,
                    cancellationToken);
                await stateStore.SaveMaterializedGameSessionAsync(
                    assignment.AssignmentId,
                    CurrentGameSessionId(),
                    cancellationToken);
                return new AssignmentPreparation(
                    new AssignmentPrepared
                    {
                        AssignmentId = assignment.AssignmentId,
                        EntityId = assignment.EntityId,
                        AuthorityEpoch = assignment.AuthorityEpoch,
                        RealizedRevision = hosted.Revision,
                        CanonicalStateSha256 = hostedStateHash,
                        Ready = true,
                    },
                    null);
            }
            if (string.IsNullOrWhiteSpace(assignment.HostEntityId))
            {
                await EnsureTacticalOriginNearAssignmentAsync(assignment, cancellationToken);
            }
            if (IsVerifiedLandProfile(assignment, capabilities))
            {
                await EnsureLandTerrainReadyAsync(assignment, cancellationToken);
            }
            var result = await RequireGameCommandAsync(
                GameCommandTypes.MaterializeOwnedSurfaceUnit,
                JsonSerializer.Serialize(new MaterializeOwnedSurfaceUnitRequestPayload
                {
                    Context = Context(assignment.AssignmentId, assignment.AuthorityEpoch,
                        assignment.ExpectedEntityRevision, assignment.IdempotencyKey),
                    EntityId = assignment.EntityId,
                    DisplayName = assignment.DisplayName,
                    UnitTypeId = assignment.UnitTypeId,
                    VariantId = assignment.VariantId,
                    LoadoutId = assignment.LoadoutId,
                    TaskforceId = materializationTaskforceId,
                    Relationship = assignment.Relationship == TacticalRelationship.Hostile
                        ? "Hostile"
                        : "Friendly",
                    Latitude = assignment.LatitudeMicrodegrees / 1_000_000d,
                    Longitude = assignment.LongitudeMicrodegrees / 1_000_000d,
                    AltitudeFeet = assignment.AltitudeFeet,
                    HeadingDegrees = assignment.HeadingMillidegrees / 1_000d,
                    Telegraph = assignment.Telegraph,
                    FuelFractionMilliunits = assignment.InitialFuelMilliunits,
                    DamageFractionMilliunits = assignment.InitialDamageMilliunits,
                    Magazines = assignment.Magazines.Count == 0
                        ? null
                        : assignment.Magazines
                            .Select(magazine => new AmmunitionStoreSnapshotPayload
                            {
                                AmmunitionId = magazine.AmmunitionId,
                                Quantity = magazine.Rounds,
                            })
                            .ToArray(),
                    SourceCheckpoint = sourceCheckpoint,
                }, JsonOptions),
                cancellationToken);
            var materialized = JsonSerializer.Deserialize<MaterializeOwnedSurfaceUnitResultPayload>(result.ResultJson, JsonOptions)
                ?? throw new InvalidDataException("Materialization result was empty.");
            try
            {
                await EnsureOwnedUnitTacticallyReadyAsync(
                    assignment,
                    materialized.Revision,
                    cancellationToken);
            }
            catch (Exception readinessException) when (readinessException is not OperationCanceledException)
            {
                var projectionRestored = false;
                try
                {
                    projectionRestored = await RollbackIncompleteMaterializationAsync(
                        assignment,
                        materialized.Revision,
                        capabilities,
                        serviceToken,
                        cancellationToken);
                }
                catch (Exception rollbackException) when (rollbackException is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        readinessException.Message
                        + " The incomplete native object could not be rolled back: "
                        + rollbackException.Message,
                        new AggregateException(readinessException, rollbackException));
                }
                throw new InvalidOperationException(
                    readinessException.Message
                    + (projectionRestored
                        ? " The incomplete native object was rolled back and its campaign projection was restored."
                        : " The incomplete native object was rolled back; the next projection cycle must restore its map symbol."),
                    readinessException);
            }
            await stateStore.SaveMaterializedGameSessionAsync(
                assignment.AssignmentId,
                CurrentGameSessionId(),
                cancellationToken);

            var checkpointResult = await RequireGameCommandAsync(
                GameCommandTypes.CaptureOwnedSurfaceCheckpoint,
                JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                {
                    Context = Context(assignment.AssignmentId, assignment.AuthorityEpoch,
                        materialized.Revision, $"{assignment.IdempotencyKey}-checkpoint"),
                    EntityId = assignment.EntityId,
                }, JsonOptions),
                cancellationToken);
            var checkpointBytes = Encoding.UTF8.GetBytes(checkpointResult.ResultJson);
            var checkpointHash = Convert.ToHexString(SHA256.HashData(checkpointBytes)).ToLowerInvariant();
            await UploadCheckpointAsync(checkpointHash, checkpointBytes, serviceToken, cancellationToken);
            var checkpoint = JsonSerializer.Deserialize<OwnedSurfaceCheckpointPayload>(checkpointResult.ResultJson, JsonOptions)
                ?? throw new InvalidDataException("Checkpoint result was empty.");
            _lastPublishedTacticalState[StateSyncKey(assignment.AssignmentId, assignment.AuthorityEpoch)] =
                checkpoint.CheckpointSha256;

            var realizedRevision = materialized.Revision;
            if (isShadow)
            {
                var suspensionResult = await RequireGameCommandAsync(
                    GameCommandTypes.SetOwnedSurfaceSuspension,
                    JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
                    {
                        Context = Context(assignment.AssignmentId, assignment.AuthorityEpoch,
                            materialized.Revision, $"{assignment.IdempotencyKey}-suspend-prepared-shadow"),
                        EntityId = assignment.EntityId,
                        IsSuspended = true,
                    }, JsonOptions),
                    cancellationToken);
                var suspension = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                    suspensionResult.ResultJson,
                    JsonOptions) ?? throw new InvalidDataException("Shadow suspension result was empty.");
                realizedRevision = suspension.Revision;
            }
            await stateStore.SaveGameRevisionAsync(
                assignment.AssignmentId,
                realizedRevision,
                cancellationToken);
            return new AssignmentPreparation(
                new AssignmentPrepared
                {
                    AssignmentId = assignment.AssignmentId,
                    EntityId = assignment.EntityId,
                    AuthorityEpoch = assignment.AuthorityEpoch,
                    RealizedRevision = realizedRevision,
                    CanonicalStateSha256 = checkpoint.CheckpointSha256,
                    Ready = true,
                },
                new CheckpointPublished
                {
                    AssignmentId = assignment.AssignmentId,
                    EntityId = assignment.EntityId,
                    AuthorityEpoch = assignment.AuthorityEpoch,
                    EntityRevision = materialized.Revision,
                    CampaignCommitPosition = assignment.HandoffCommitPosition,
                    CheckpointSha256 = checkpointHash,
                    CheckpointLength = checkpointBytes.Length,
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Tactical assignment {AssignmentId} epoch {AuthorityEpoch} for {EntityId} was rejected during preparation.",
                assignment.AssignmentId,
                assignment.AuthorityEpoch,
                assignment.EntityId);
            return new AssignmentPreparation(
                new AssignmentPrepared
                {
                    AssignmentId = assignment.AssignmentId,
                    EntityId = assignment.EntityId,
                    AuthorityEpoch = assignment.AuthorityEpoch,
                    Ready = false,
                    ErrorCode = "assignment-preparation-failed",
                    ErrorMessage = exception.Message,
                },
                null);
        }
        finally
        {
            if (resumeAfterPreparation)
            {
                try
                {
                    await RequireGameCommandAsync(
                        GameCommandTypes.SetExperimentalPauseState,
                        JsonSerializer.Serialize(
                            new ExperimentalPauseRequestPayload { IsPaused = false },
                            JsonOptions),
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Could not restore running tactical time after assignment preparation {AssignmentId}.",
                        assignment.AssignmentId);
                }
            }
            if (pauseGateHeld)
            {
                _tacticalPauseGate.Release();
            }
        }
    }

    private async Task<string> ResolveMaterializationTaskforceIdAsync(
        TacticalAssignment assignment,
        CapabilitiesPayload capabilities,
        CancellationToken cancellationToken)
    {
        if (assignment.Relationship != TacticalRelationship.Hostile)
        {
            if (string.IsNullOrWhiteSpace(assignment.TaskforceId))
            {
                throw new InvalidOperationException("A friendly tactical assignment requires the player task force.");
            }
            return assignment.TaskforceId;
        }

        var result = await RequireGameCommandAsync(
            GameCommandTypes.GetWorldSnapshot,
            "{}",
            cancellationToken);
        var world = JsonSerializer.Deserialize<WorldSnapshotPayload>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Sea Power world inspection was empty.");
        return SelectOpposingTaskforce(world, capabilities.PlayerTaskforceId).Id;
    }

    internal static TaskforceSnapshotPayload SelectOpposingTaskforce(
        WorldSnapshotPayload world,
        string playerTaskforceId)
    {
        var player = world.Taskforces.SingleOrDefault(item =>
            string.Equals(item.Id, playerTaskforceId, StringComparison.Ordinal));
        if (player is null || string.IsNullOrWhiteSpace(player.Side))
        {
            throw new InvalidOperationException("Sea Power did not report the current player task-force side.");
        }
        var opposing = world.Taskforces
            .Where(item => !item.IsPlayerControlled
                && !string.IsNullOrWhiteSpace(item.Id)
                && !string.IsNullOrWhiteSpace(item.Side)
                && !string.Equals(item.Side, "Neutral", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Side, player.Side, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return opposing ?? throw new InvalidOperationException(
            "The tactical mission has no authored opposing task force for a campaign hostile.");
    }

    private async Task<TacticalEventProposal> ApplyCommandAsync(
        OrderedTacticalCommand command,
        CancellationToken cancellationToken)
    {
        var assignment = stateStore.ReadAssignment(command.AssignmentId);
        if (assignment.Role == AssignmentRole.Shadow
            && !stateStore.HasActivationGrant(command.AssignmentId, command.AuthorityEpoch))
        {
            throw new InvalidDataException("A Shadow assignment cannot publish before a post-commit activation grant.");
        }
        var result = await RunAtPausedTacticalSafePointAsync(
            () => RequireGameCommandAsync(
                GameCommandTypes.UpdateOwnedSurfaceUnit,
                JsonSerializer.Serialize(new UpdateOwnedSurfaceUnitRequestPayload
                {
                    Context = Context(command.AssignmentId, command.AuthorityEpoch,
                        command.ExpectedEntityRevision, command.IdempotencyKey),
                    EntityId = command.EntityId,
                    Latitude = command.LatitudeMicrodegrees / 1_000_000d,
                    Longitude = command.LongitudeMicrodegrees / 1_000_000d,
                    HeadingDegrees = command.HeadingMillidegrees / 1_000d,
                    Telegraph = command.Telegraph,
                }, JsonOptions),
                cancellationToken),
            $"tactical command {command.CommandId}",
            cancellationToken);
        var update = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Tactical update result was empty.");
        await stateStore.SaveGameRevisionAsync(command.AssignmentId, update.Revision, cancellationToken);
        return new TacticalEventProposal
        {
            EventId = DeterministicId(command.CommandId, command.IdempotencyKey),
            IdempotencyKey = command.IdempotencyKey,
            AssignmentId = command.AssignmentId,
            EntityId = command.EntityId,
            AuthorityEpoch = command.AuthorityEpoch,
            ExpectedEntityRevision = update.Revision - 1,
            EffectiveTimeQuanta = command.EffectiveTimeQuanta,
            Kind = TacticalEventKind.Movement,
            LatitudeMicrodegrees = checked((int)Math.Round((update.Unit.Latitude ?? 0) * 1_000_000)),
            LongitudeMicrodegrees = checked((int)Math.Round((update.Unit.Longitude ?? 0) * 1_000_000)),
            HeadingMillidegrees = QuantizeHeadingMilliDegrees(update.Unit.HeadingDegrees ?? 0),
            Telegraph = update.Unit.Telegraph,
            Destroyed = update.Unit.IsDestroyed,
        };
    }

    private async Task<IReadOnlyList<TacticalEventProposal>> CaptureTacticalStateChangesAsync(
        OwnedSurfaceResourceStatePayload resources,
        CancellationToken cancellationToken)
    {
        var proposals = new List<TacticalEventProposal>();
        foreach (var owned in resources.Entities
            .Where(item => string.Equals(item.LifecycleState, "active", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.AssignmentId, StringComparer.Ordinal))
        {
            try
            {
                var assignment = stateStore.ReadAssignment(owned.AssignmentId);
                var mayPublish = assignment.Role == AssignmentRole.Primary
                    || assignment.Role == AssignmentRole.Shadow
                        && stateStore.HasActivationGrant(assignment.AssignmentId, assignment.AuthorityEpoch);
                if (!mayPublish
                    || assignment.AuthorityEpoch != owned.AuthorityEpoch
                    || !string.Equals(assignment.CampaignId, options.CampaignId, StringComparison.Ordinal)
                    || !string.Equals(assignment.EntityId, owned.EntityId, StringComparison.Ordinal))
                {
                    continue;
                }

                var expectedGameRevision = stateStore.ReadGameRevision(assignment.AssignmentId);
                if (owned.Revision != expectedGameRevision)
                {
                    logger.LogDebug(
                        "Skipping tactical state sync for {AssignmentId}: runtime revision {RuntimeRevision} does not match durable game revision {DurableRevision}.",
                        assignment.AssignmentId,
                        owned.Revision,
                        expectedGameRevision);
                    continue;
                }

                var readKey = $"state-sync-read-{DeterministicId(
                    assignment.AssignmentId,
                    $"{assignment.AuthorityEpoch}:{expectedGameRevision}")}";
                var result = await game.SendCommandAsync(
                    GameCommandTypes.CaptureOwnedSurfaceCheckpoint,
                    JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                    {
                        Context = Context(
                            assignment.AssignmentId,
                            assignment.AuthorityEpoch,
                            expectedGameRevision,
                            readKey),
                        EntityId = assignment.EntityId,
                    }, JsonOptions),
                    CommandPriorities.Control,
                    cancellationToken);
                if (!result.Success)
                {
                    logger.LogDebug(
                        "Tactical state sync capture for {AssignmentId} was unavailable: {ErrorCode}.",
                        assignment.AssignmentId,
                        result.ErrorCode);
                    continue;
                }

                var checkpoint = JsonSerializer.Deserialize<OwnedSurfaceCheckpointPayload>(
                    result.ResultJson,
                    JsonOptions) ?? throw new InvalidDataException("Tactical state sync checkpoint was empty.");
                var unit = checkpoint.Unit;
                if (unit.Latitude is null || unit.Longitude is null || unit.HeadingDegrees is null)
                {
                    logger.LogDebug(
                        "Tactical state sync for {AssignmentId} had incomplete kinematics.",
                        assignment.AssignmentId);
                    continue;
                }

                var syncKey = StateSyncKey(assignment.AssignmentId, assignment.AuthorityEpoch);
                if (_lastPublishedTacticalState.TryGetValue(syncKey, out var prior)
                    && string.Equals(prior, checkpoint.CheckpointSha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var idempotencyKey = $"tactical-sync-{DeterministicId(
                    assignment.AssignmentId,
                    $"{assignment.AuthorityEpoch}:{checkpoint.CheckpointSha256}")}";
                proposals.Add(new TacticalEventProposal
                {
                    EventId = DeterministicId(assignment.AssignmentId, idempotencyKey),
                    IdempotencyKey = idempotencyKey,
                    AssignmentId = assignment.AssignmentId,
                    EntityId = assignment.EntityId,
                    AuthorityEpoch = assignment.AuthorityEpoch,
                    ExpectedEntityRevision = expectedGameRevision,
                    EffectiveTimeQuanta = assignment.HandoffTimeQuanta,
                    Kind = TacticalEventKind.StateSync,
                    LatitudeMicrodegrees = checked((int)Math.Round(unit.Latitude.Value * 1_000_000)),
                    LongitudeMicrodegrees = checked((int)Math.Round(unit.Longitude.Value * 1_000_000)),
                    HeadingMillidegrees = QuantizeHeadingMilliDegrees(unit.HeadingDegrees.Value),
                    Telegraph = unit.Telegraph,
                    FuelMilliunits = ResolveFuelResource(
                        assignment.InitialFuelMilliunits,
                        unit.FuelFractionMilliunits),
                    StoresMilliunits = ScaleResource(
                        assignment.InitialStoresMilliunits,
                        unit.StoresFractionMilliunits),
                    DamageMilliunits = ApplyDamageBaseline(
                        assignment.InitialDamageMilliunits,
                        unit.DamageFractionMilliunits,
                        unit.IsDestroyed),
                    Destroyed = unit.IsDestroyed,
                    CheckpointSha256 = checkpoint.CheckpointSha256,
                    Magazines =
                    {
                        CampaignMagazineReadback(assignment, unit.Magazines),
                    },
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(
                    exception,
                    "Tactical state sync skipped owned assignment {AssignmentId}.",
                    owned.AssignmentId);
            }
        }

        return proposals;
    }

    private async Task<FencePreparation> ApplyFenceAsync(
        AuthorityFence fence,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        var detachedWithCheckpoint = resources.Entities.Any(resource =>
            string.Equals(resource.EntityId, fence.EntityId, StringComparison.Ordinal)
            && string.Equals(resource.AssignmentId, fence.AssignmentId, StringComparison.Ordinal)
            && resource.AuthorityEpoch == fence.FencedAuthorityEpoch
            && string.Equals(
                resource.LifecycleState,
                "detached-after-scene-loss",
                StringComparison.Ordinal)
            && resource.HasCheckpoint
            && !resource.RecoveryRequired);
        if (detachedWithCheckpoint)
        {
            await _tacticalPauseGate.WaitAsync(cancellationToken);
            try
            {
                return await ApplyFenceAtPausedSafePointAsync(
                    fence,
                    serviceToken,
                    cancellationToken);
            }
            finally
            {
                _tacticalPauseGate.Release();
            }
        }

        return await RunAtPausedTacticalSafePointAsync(
            () => ApplyFenceAtPausedSafePointAsync(
                fence,
                serviceToken,
                cancellationToken),
            $"authority fence {fence.AssignmentId}",
            cancellationToken);
    }

    private async Task<FencePreparation> ApplyFenceAtPausedSafePointAsync(
        AuthorityFence fence,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var assignment = stateStore.ReadAssignment(fence.AssignmentId);
        if (assignment.AuthorityEpoch != fence.FencedAuthorityEpoch
            || !string.Equals(assignment.EntityId, fence.EntityId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The authority fence does not match the durable assignment baseline.");
        }
        var capabilities = await ReadCapabilitiesAsync(cancellationToken);
        await EnsureMutationModeArmedAsync(capabilities, 30_000, cancellationToken);
        var durableExpectedGameRevision = fence.ResolveExpectedRevisionFromWorker
            ? stateStore.ReadGameRevision(fence.AssignmentId)
            : fence.ExpectedEntityRevision;
        var resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        var retainedResource = resources.Entities.SingleOrDefault(resource =>
            string.Equals(resource.EntityId, fence.EntityId, StringComparison.Ordinal)
            && string.Equals(resource.AssignmentId, fence.AssignmentId, StringComparison.Ordinal)
            && resource.AuthorityEpoch == fence.FencedAuthorityEpoch);
        var expectedGameRevision = fence.ResolveExpectedRevisionFromWorker
            && retainedResource is not null
            ? string.Equals(
                    retainedResource.LifecycleState,
                    "suspended-retained",
                    StringComparison.Ordinal)
                && retainedResource.HasCheckpoint
                && !retainedResource.RecoveryRequired
                    ? retainedResource.Revision - 1
                    : retainedResource.Revision
            : durableExpectedGameRevision;
        var alreadySuspended = retainedResource is not null
            && retainedResource.LifecycleState is "suspended-retained"
                or "detached-after-scene-loss"
            && retainedResource.HasCheckpoint
            && !retainedResource.RecoveryRequired
            && retainedResource.Revision == expectedGameRevision
                + (retainedResource.LifecycleState == "suspended-retained" ? 1 : 0);
        var checkpointCommandRevision = alreadySuspended
            ? retainedResource!.Revision
            : expectedGameRevision;
        GameCommandResult checkpointResult;
        try
        {
            checkpointResult = await RequireGameCommandAsync(
                GameCommandTypes.CaptureOwnedSurfaceCheckpoint,
                JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
                {
                    Context = Context(fence.AssignmentId, fence.FencedAuthorityEpoch,
                        checkpointCommandRevision, $"{fence.IdempotencyKey}-checkpoint"),
                    EntityId = fence.EntityId,
                }, JsonOptions),
                cancellationToken);
        }
        catch (InvalidOperationException exception) when (
            !string.IsNullOrWhiteSpace(assignment.HostEntityId)
            && exception.Message.Contains(
                "No bridge-owned surface record matches the requested entity.",
                StringComparison.Ordinal))
        {
            return await BuildUnsupportedHostedAircraftFenceAsync(
                fence,
                assignment,
                expectedGameRevision,
                serviceToken,
                cancellationToken);
        }
        var checkpointBytes = Encoding.UTF8.GetBytes(checkpointResult.ResultJson);
        var checkpointHash = Convert.ToHexString(SHA256.HashData(checkpointBytes)).ToLowerInvariant();
        await UploadCheckpointAsync(checkpointHash, checkpointBytes, serviceToken, cancellationToken);
        var checkpoint = JsonSerializer.Deserialize<OwnedSurfaceCheckpointPayload>(
            checkpointResult.ResultJson,
            JsonOptions) ?? throw new InvalidDataException("Final tactical checkpoint was empty.");
        if (checkpoint.Revision != expectedGameRevision)
        {
            throw new InvalidDataException(
                $"The final tactical checkpoint revision {checkpoint.Revision} does not match "
                + $"the fenced revision {expectedGameRevision}.");
        }
        var unit = checkpoint.Unit;
        if (unit.Latitude is null || unit.Longitude is null || unit.HeadingDegrees is null)
        {
            throw new InvalidDataException("The final tactical checkpoint has incomplete kinematics.");
        }

        var suspensionRevision = retainedResource?.Revision ?? expectedGameRevision;
        if (!alreadySuspended)
        {
            try
            {
                var result = await RequireGameCommandAsync(
                    GameCommandTypes.SetOwnedSurfaceSuspension,
                    JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
                    {
                        Context = Context(fence.AssignmentId, fence.FencedAuthorityEpoch,
                            expectedGameRevision, fence.IdempotencyKey),
                        EntityId = fence.EntityId,
                        IsSuspended = true,
                    }, JsonOptions),
                    cancellationToken);
                var suspension = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                    result.ResultJson,
                    JsonOptions) ?? throw new InvalidDataException("Tactical fence result was empty.");
                suspensionRevision = suspension.Revision;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("stale-revision", StringComparison.Ordinal))
            {
                var refreshedResources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
                var concurrentResource = refreshedResources.Entities.SingleOrDefault(resource =>
                    string.Equals(resource.EntityId, fence.EntityId, StringComparison.Ordinal)
                    && string.Equals(resource.AssignmentId, fence.AssignmentId, StringComparison.Ordinal)
                    && resource.AuthorityEpoch == fence.FencedAuthorityEpoch);
                if (concurrentResource is not null
                    && string.Equals(
                        concurrentResource.LifecycleState,
                        "suspended-retained",
                        StringComparison.Ordinal)
                    && concurrentResource.HasCheckpoint
                    && !concurrentResource.RecoveryRequired
                    && concurrentResource.Revision == expectedGameRevision + 1)
                {
                    suspensionRevision = concurrentResource.Revision;
                }
                else if (concurrentResource is not null
                    && string.Equals(
                        concurrentResource.LifecycleState,
                        "active",
                        StringComparison.Ordinal)
                    && concurrentResource.HasCheckpoint
                    && !concurrentResource.RecoveryRequired
                    && concurrentResource.Revision == expectedGameRevision)
                {
                    var retryResult = await RequireGameCommandAsync(
                        GameCommandTypes.SetOwnedSurfaceSuspension,
                        JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
                        {
                            Context = Context(fence.AssignmentId, fence.FencedAuthorityEpoch,
                                expectedGameRevision, fence.IdempotencyKey),
                            EntityId = fence.EntityId,
                            IsSuspended = true,
                        }, JsonOptions),
                        cancellationToken);
                    var retrySuspension = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                        retryResult.ResultJson,
                        JsonOptions) ?? throw new InvalidDataException(
                            "Retried tactical fence result was empty.");
                    suspensionRevision = retrySuspension.Revision;
                }
                else
                {
                    var observed = string.Join(
                        "; ",
                        refreshedResources.Entities.Select(resource => string.Join(
                            ",",
                            resource.EntityId,
                            resource.AssignmentId,
                            resource.AuthorityEpoch,
                            resource.Revision,
                            resource.LifecycleState,
                            resource.HasCheckpoint,
                            resource.RecoveryRequired)));
                    throw new InvalidOperationException(
                        $"A fence suspension raced with an incompatible owned-resource state: {observed}",
                        exception);
                }
            }
        }
        await stateStore.SaveGameRevisionAsync(
            fence.AssignmentId,
            suspensionRevision,
            cancellationToken);
        return new FencePreparation(
            new TacticalEventProposal
            {
                EventId = DeterministicId(fence.AssignmentId, fence.IdempotencyKey),
                IdempotencyKey = fence.IdempotencyKey,
                AssignmentId = fence.AssignmentId,
                EntityId = fence.EntityId,
                AuthorityEpoch = fence.FencedAuthorityEpoch,
                ExpectedEntityRevision = checkpoint.Revision,
                EffectiveTimeQuanta = fence.EffectiveTimeQuanta,
                Kind = TacticalEventKind.Checkpoint,
                LatitudeMicrodegrees = checked((int)Math.Round(unit.Latitude.Value * 1_000_000)),
                LongitudeMicrodegrees = checked((int)Math.Round(unit.Longitude.Value * 1_000_000)),
                HeadingMillidegrees = QuantizeHeadingMilliDegrees(unit.HeadingDegrees.Value),
                Telegraph = unit.Telegraph,
                FuelMilliunits = ResolveFuelResource(
                    assignment.InitialFuelMilliunits,
                    unit.FuelFractionMilliunits),
                StoresMilliunits = ScaleResource(
                    assignment.InitialStoresMilliunits,
                    unit.StoresFractionMilliunits),
                DamageMilliunits = ApplyDamageBaseline(
                    assignment.InitialDamageMilliunits,
                    unit.DamageFractionMilliunits,
                    unit.IsDestroyed),
                Destroyed = unit.IsDestroyed,
                CheckpointSha256 = checkpointHash,
                Magazines =
                {
                    CampaignMagazineReadback(assignment, unit.Magazines),
                },
            },
            new CheckpointPublished
            {
                AssignmentId = fence.AssignmentId,
                EntityId = fence.EntityId,
                AuthorityEpoch = fence.FencedAuthorityEpoch,
                EntityRevision = expectedGameRevision,
                CheckpointSha256 = checkpointHash,
                CheckpointLength = checkpointBytes.Length,
            });
    }

    private async Task<FencePreparation> BuildUnsupportedHostedAircraftFenceAsync(
        AuthorityFence fence,
        TacticalAssignment assignment,
        long expectedGameRevision,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var checkpointBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            kind = "hosted-aircraft-final-state-unsupported",
            assignmentId = fence.AssignmentId,
            entityId = fence.EntityId,
            hostEntityId = assignment.HostEntityId,
            authorityEpoch = fence.FencedAuthorityEpoch,
            revision = expectedGameRevision,
            capturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            unsupportedFields = new[]
            {
                "hosted-aircraft-final-state",
                "hosted-aircraft-recovery",
            },
        }, JsonOptions);
        var checkpointHash = Convert.ToHexString(SHA256.HashData(checkpointBytes)).ToLowerInvariant();
        await UploadCheckpointAsync(checkpointHash, checkpointBytes, serviceToken, cancellationToken);
        return new FencePreparation(
            new TacticalEventProposal
            {
                EventId = DeterministicId(fence.AssignmentId, fence.IdempotencyKey),
                IdempotencyKey = fence.IdempotencyKey,
                AssignmentId = fence.AssignmentId,
                EntityId = fence.EntityId,
                AuthorityEpoch = fence.FencedAuthorityEpoch,
                ExpectedEntityRevision = expectedGameRevision,
                EffectiveTimeQuanta = fence.EffectiveTimeQuanta,
                Kind = TacticalEventKind.Checkpoint,
                LatitudeMicrodegrees = assignment.LatitudeMicrodegrees,
                LongitudeMicrodegrees = assignment.LongitudeMicrodegrees,
                HeadingMillidegrees = assignment.HeadingMillidegrees,
                Telegraph = assignment.Telegraph,
                FuelMilliunits = assignment.InitialFuelMilliunits,
                StoresMilliunits = assignment.InitialStoresMilliunits,
                DamageMilliunits = assignment.InitialDamageMilliunits,
                CheckpointSha256 = checkpointHash,
            },
            new CheckpointPublished
            {
                AssignmentId = fence.AssignmentId,
                EntityId = fence.EntityId,
                AuthorityEpoch = fence.FencedAuthorityEpoch,
                EntityRevision = expectedGameRevision,
                CheckpointSha256 = checkpointHash,
                CheckpointLength = checkpointBytes.Length,
            });
    }

    private async Task<FinalCheckpointStaged> StageFinalCheckpointAsync(
        StageFinalCheckpoint request,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var assignment = stateStore.ReadAssignment(request.DestinationAssignmentId);
            if (assignment.Role != AssignmentRole.Shadow
                || assignment.AuthorityEpoch != request.DestinationAuthorityEpoch
                || !string.Equals(assignment.EntityId, request.EntityId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Final-checkpoint staging requires the durable Shadow assignment.");
            }
            if (stateStore.HasActivationGrant(request.DestinationAssignmentId, request.DestinationAuthorityEpoch))
            {
                throw new InvalidDataException("A granted destination cannot restage a pre-commit checkpoint.");
            }

            var sourceCheckpoint = await DownloadCheckpointAsync(
                request.CheckpointSha256,
                request.CheckpointUri,
                serviceToken,
                cancellationToken)
                ?? throw new InvalidDataException("The source final checkpoint reference is missing.");
            var expectedGameRevision = stateStore.ReadGameRevision(request.DestinationAssignmentId);
            var stagingContext = Context(
                request.DestinationAssignmentId,
                request.DestinationAuthorityEpoch,
                expectedGameRevision,
                request.IdempotencyKey);
            OwnedMutationContracts.ValidateOwnedLifecycleIdentifiers(stagingContext, request.EntityId);
            var stagedResult = await RequireGameCommandAsync(
                GameCommandTypes.StageOwnedSurfaceCheckpoint,
                JsonSerializer.Serialize(new StageOwnedSurfaceCheckpointRequestPayload
                {
                    Context = stagingContext,
                    EntityId = request.EntityId,
                    SourceCheckpoint = sourceCheckpoint,
                }, JsonOptions),
                cancellationToken);
            var staged = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                stagedResult.ResultJson,
                JsonOptions) ?? throw new InvalidDataException("Checkpoint staging result was empty.");
            if (!staged.Unit.IsDisabled)
            {
                var resuspendContext = Context(
                    request.DestinationAssignmentId,
                    request.DestinationAuthorityEpoch,
                    staged.Revision,
                    $"{request.IdempotencyKey}-keep-suspended");
                OwnedMutationContracts.ValidateOwnedLifecycleIdentifiers(
                    resuspendContext,
                    request.EntityId);
                var resuspend = await RequireGameCommandAsync(
                    GameCommandTypes.SetOwnedSurfaceSuspension,
                    JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
                    {
                        Context = resuspendContext,
                        EntityId = request.EntityId,
                        IsSuspended = true,
                    }, JsonOptions),
                    cancellationToken);
                var suspension = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                    resuspend.ResultJson,
                    JsonOptions) ?? throw new InvalidDataException("Post-stage suspension result was empty.");
                staged = suspension;
            }

            await stateStore.SaveGameRevisionAsync(
                request.DestinationAssignmentId,
                staged.Revision,
                cancellationToken);
            await stateStore.SaveStagedCheckpointAsync(
                request.DestinationAssignmentId,
                request.DestinationAuthorityEpoch,
                request.CheckpointSha256,
                cancellationToken);
            await stateStore.SaveStagedIdempotencyKeyAsync(
                request.DestinationAssignmentId,
                request.DestinationAuthorityEpoch,
                request.IdempotencyKey,
                cancellationToken);
            return new FinalCheckpointStaged
            {
                AssignmentId = request.DestinationAssignmentId,
                EntityId = request.EntityId,
                AuthorityEpoch = request.DestinationAuthorityEpoch,
                RealizedRevision = staged.Revision,
                StagedCheckpointSha256 = request.CheckpointSha256,
                Ready = true,
                IdempotencyKey = request.IdempotencyKey,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Final checkpoint staging failed for assignment {AssignmentId} epoch {AuthorityEpoch}.",
                request.DestinationAssignmentId,
                request.DestinationAuthorityEpoch);
            return new FinalCheckpointStaged
            {
                AssignmentId = request.DestinationAssignmentId,
                EntityId = request.EntityId,
                AuthorityEpoch = request.DestinationAuthorityEpoch,
                StagedCheckpointSha256 = request.CheckpointSha256,
                Ready = false,
                ErrorCode = "final-checkpoint-staging-failed",
                ErrorMessage = exception.Message,
                IdempotencyKey = request.IdempotencyKey,
            };
        }
    }

    private async Task ApplyActivationGrantAsync(
        ActivationGrant grant,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var focusAssignment = await RunAtPausedTacticalSafePointAsync(
            async () =>
            {
                return await ApplyActivationGrantAtSafePointAsync(
                    grant,
                    serviceToken,
                    cancellationToken);
            },
            $"activation grant {grant.AssignmentId}",
            cancellationToken);
        if (focusAssignment is not null)
        {
            await FocusAfterActivationAsync(focusAssignment, cancellationToken);
        }
    }

    private async Task<TacticalAssignment?> ApplyActivationGrantAtSafePointAsync(
        ActivationGrant grant,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        if (stateStore.HasSupersededActivationGrant(grant.AssignmentId, grant.AuthorityEpoch))
        {
            logger.LogInformation(
                "Activation grant for assignment {AssignmentId} epoch {AuthorityEpoch} remains superseded ({Reason}).",
                grant.AssignmentId,
                grant.AuthorityEpoch,
                stateStore.TryReadSupersededActivationGrantReason(grant.AssignmentId, grant.AuthorityEpoch)
                    ?? "campaign-assignment-superseded");
            return null;
        }

        if (stateStore.HasActivationGrant(grant.AssignmentId, grant.AuthorityEpoch))
        {
            var grantedAssignment = stateStore.ReadAssignment(grant.AssignmentId);
            var grantedResources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
            if (grantedResources.Entities.Any(item =>
                string.Equals(item.AssignmentId, grant.AssignmentId, StringComparison.Ordinal)
                && item.AuthorityEpoch == grant.AuthorityEpoch
                && string.Equals(item.EntityId, grant.EntityId, StringComparison.Ordinal)
                && string.Equals(item.LifecycleState, "active", StringComparison.OrdinalIgnoreCase)))
            {
                await stateStore.SaveMaterializedGameSessionAsync(
                    grant.AssignmentId,
                    CurrentGameSessionId(),
                    cancellationToken);
                return grantedAssignment;
            }
        }

        CampaignAssignmentLookup lookup;
        try
        {
            lookup = await TryResolveLiveCampaignAssignmentAsync(
                grant,
                serviceToken,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Campaign view was unreachable while resolving activation grant for assignment {AssignmentId} epoch {AuthorityEpoch}.",
                grant.AssignmentId,
                grant.AuthorityEpoch);
            return null;
        }

        if (lookup.Status != CampaignViewStatus.Ready)
        {
            logger.LogInformation(
                "Campaign view was not authoritative for activation grant {AssignmentId} epoch {AuthorityEpoch} ({Status}, viewCommit={ViewCommit}, migrationCommit={MigrationCommit}).",
                grant.AssignmentId,
                grant.AuthorityEpoch,
                lookup.Status,
                lookup.ViewCommitPosition,
                grant.MigrationCommitPosition);
            return null;
        }

        var liveAssignment = lookup.Assignment;
        if (liveAssignment is null
            || !string.Equals(liveAssignment.Role, "Primary", StringComparison.Ordinal)
            || !string.Equals(liveAssignment.Lifecycle, "Active", StringComparison.Ordinal)
            || liveAssignment.AuthorityEpoch != grant.AuthorityEpoch)
        {
            var reason = liveAssignment is null
                ? "campaign-assignment-missing"
                : liveAssignment.Lifecycle is "Released"
                    ? "campaign-assignment-released"
                    : liveAssignment.AuthorityEpoch != grant.AuthorityEpoch
                        ? "campaign-assignment-epoch-mismatch"
                        : "campaign-assignment-not-active-primary";
            await stateStore.SaveSupersededActivationGrantAsync(
                grant.AssignmentId,
                grant.AuthorityEpoch,
                reason,
                cancellationToken);
            logger.LogInformation(
                "Activation grant for assignment {AssignmentId} epoch {AuthorityEpoch} was superseded ({Reason}).",
                grant.AssignmentId,
                grant.AuthorityEpoch,
                reason);
            return null;
        }

        var assignment = stateStore.ReadAssignment(grant.AssignmentId);
        if (assignment.Role != AssignmentRole.Shadow
            || assignment.AuthorityEpoch != grant.AuthorityEpoch
            || !string.Equals(assignment.EntityId, grant.EntityId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Activation grant does not match the durable Shadow assignment.");
        }
        var stagedHash = stateStore.ReadStagedCheckpoint(grant.AssignmentId, grant.AuthorityEpoch);
        if (!string.Equals(stagedHash, grant.StagedCheckpointSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Activation grant checkpoint hash does not match the staged destination.");
        }
        if (stateStore.ReadCursor() < grant.MigrationCommitPosition)
        {
            throw new InvalidDataException("Activation grant arrived before the durable migration commit cursor.");
        }
        if (!stateStore.HasCommittedMigration(
            grant.MigrationCommitPosition,
            grant.AssignmentId,
            grant.AuthorityEpoch))
        {
            throw new InvalidDataException("Activation grant is not backed by a committed tactical-island migration.");
        }

        await EnsureMissionSceneMaterializationReadyAsync(cancellationToken);
        var capabilities = await ReadCapabilitiesAsync(cancellationToken);
        if (!MatchesRequirement(
                assignment.RequiredRuntimeFingerprintSha256,
                capabilities.RuntimeFingerprint.FingerprintSha256)
            || !MatchesRequirement(
                assignment.RequiredCatalogFingerprintSha256,
                capabilities.RuntimeFingerprint.CatalogSha256))
        {
            throw new InvalidOperationException(
                "Activation recovery requires the current runtime fingerprint and catalog profile.");
        }

        await EnsureOwnedMutationsArmedAsync(capabilities, cancellationToken);

        var expectedGameRevision = stateStore.ReadGameRevision(grant.AssignmentId);
        if (await TryConvergeActivationFromObservedMaterializationAsync(
                grant,
                assignment,
                expectedGameRevision,
                cancellationToken))
        {
            return assignment;
        }

        var resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        var ownedRecord = resources.Entities.FirstOrDefault(item =>
            string.Equals(item.EntityId, assignment.EntityId, StringComparison.Ordinal));
        if (ownedRecord is not null)
        {
            if (!string.Equals(ownedRecord.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)
                || ownedRecord.AuthorityEpoch != grant.AuthorityEpoch)
            {
                throw new InvalidDataException(
                    "Activation recovery found a conflicting owned surface record for the destination entity.");
            }

            if (string.Equals(ownedRecord.LifecycleState, "active", StringComparison.OrdinalIgnoreCase))
            {
                if (ownedRecord.Revision < expectedGameRevision)
                {
                    throw new InvalidDataException(
                        "Activation recovery found an active owned record below the durable expected revision.");
                }

                await stateStore.SaveGameRevisionAsync(
                    grant.AssignmentId,
                    ownedRecord.Revision,
                    cancellationToken);
                await stateStore.SaveActivationGrantAsync(
                    grant.AssignmentId,
                    grant.AuthorityEpoch,
                    grant.MigrationCommitPosition,
                    cancellationToken);
                await stateStore.SaveMaterializedGameSessionAsync(
                    grant.AssignmentId,
                    CurrentGameSessionId(),
                    cancellationToken);
                return assignment;
            }

            if (!string.Equals(ownedRecord.LifecycleState, "suspended-retained", StringComparison.OrdinalIgnoreCase)
                || ownedRecord.RecoveryRequired)
            {
                throw new InvalidDataException(
                    "Activation recovery requires an exact suspended retained owned record or an empty runtime.");
            }

            if (ownedRecord.Revision != expectedGameRevision)
            {
                throw new InvalidDataException(
                    "Activation recovery found a suspended owned record with a stale durable revision.");
            }

            var enabled = await ActivateSuspendedOwnedRecordAsync(
                grant,
                assignment,
                expectedGameRevision,
                cancellationToken);
            await stateStore.SaveGameRevisionAsync(grant.AssignmentId, enabled.Revision, cancellationToken);
            await stateStore.SaveActivationGrantAsync(
                grant.AssignmentId,
                grant.AuthorityEpoch,
                grant.MigrationCommitPosition,
                cancellationToken);
            await stateStore.SaveMaterializedGameSessionAsync(
                grant.AssignmentId,
                CurrentGameSessionId(),
                cancellationToken);
            return assignment;
        }

        var restagedRevision = await RestageActivationCheckpointAsync(
            grant,
            assignment,
            expectedGameRevision,
            serviceToken,
            cancellationToken);
        var activated = await ActivateSuspendedOwnedRecordAsync(
            grant,
            assignment,
            restagedRevision,
            cancellationToken);
        await stateStore.SaveGameRevisionAsync(grant.AssignmentId, activated.Revision, cancellationToken);
        await stateStore.SaveActivationGrantAsync(
            grant.AssignmentId,
            grant.AuthorityEpoch,
            grant.MigrationCommitPosition,
            cancellationToken);
        await stateStore.SaveMaterializedGameSessionAsync(
            grant.AssignmentId,
            CurrentGameSessionId(),
            cancellationToken);
        return assignment;
    }

    private async Task FocusAfterActivationAsync(
        TacticalAssignment assignment,
        CancellationToken cancellationToken)
    {
        if (!assignment.FocusAfterActivation
            || stateStore.HasCompletedPlayerFocus(
                assignment.AssignmentId,
                assignment.AuthorityEpoch))
        {
            return;
        }

        string? errorCode = null;
        string? errorMessage = null;
        for (var attempt = 1; attempt <= PlayerFocusAttempts; attempt++)
        {
            var result = await game.SendCommandAsync(
                GameCommandTypes.FocusCameraOnUnit,
                JsonSerializer.Serialize(new FocusCameraOnUnitRequestPayload
                {
                    UnitId = $"campaign-unit:{assignment.EntityId}",
                }, JsonOptions),
                CommandPriorities.Control,
                cancellationToken);
            if (result.Success)
            {
                var focus = JsonSerializer.Deserialize<UiAutomationActionResultPayload>(
                    result.ResultJson,
                    JsonOptions);
                if (!string.Equals(focus?.Outcome, "completed", StringComparison.Ordinal))
                {
                    errorCode = focus?.Outcome ?? "focus-pending";
                    errorMessage = focus?.Postcondition
                        ?? "The runtime accepted focus but has not attached the camera yet.";
                    if (attempt < PlayerFocusAttempts)
                    {
                        await Task.Delay(PlayerFocusRetryInterval, cancellationToken);
                    }
                    continue;
                }
                await stateStore.SaveCompletedPlayerFocusAsync(
                    assignment.AssignmentId,
                    assignment.AuthorityEpoch,
                    cancellationToken);
                logger.LogInformation(
                    "Focused the player camera on activated campaign entity {EntityId} after {AttemptCount} attempt(s).",
                    assignment.EntityId,
                    attempt);
                return;
            }

            errorCode = result.ErrorCode;
            errorMessage = result.ErrorMessage;
            if (string.Equals(
                    errorCode,
                    ProtocolErrorCodes.LocalityRebaseRequired,
                    StringComparison.Ordinal))
            {
                break;
            }
            if (attempt < PlayerFocusAttempts)
            {
                await Task.Delay(PlayerFocusRetryInterval, cancellationToken);
            }
        }

        logger.LogWarning(
            "Player-requested camera focus failed after activating {EntityId}: {ErrorCode}: {ErrorMessage}",
            assignment.EntityId,
            errorCode,
            errorMessage);
    }

    private void ObserveAssignmentPreparationForPlayerFocus(
        TacticalAssignment assignment,
        bool prepared,
        CancellationToken cancellationToken)
    {
        long generation;
        lock (_playerFocusGate)
        {
            if (prepared && assignment.FocusAfterActivation)
            {
                _pendingPlayerFocus = assignment.Clone();
            }
            if (_pendingPlayerFocus is null)
            {
                return;
            }
            generation = ++_playerFocusGeneration;
        }

        _ = FocusAfterAssignmentBatchSettlesAsync(generation, cancellationToken);
    }

    private void RecoverPendingPlayerFocus(
        OwnedSurfaceResourceStatePayload resources,
        IReadOnlySet<string> activeLocalAssignmentIds,
        CancellationToken cancellationToken)
    {
        var activeResources = resources.Entities
            .Where(resource => string.Equals(
                    resource.LifecycleState,
                    "active",
                    StringComparison.OrdinalIgnoreCase)
                && activeLocalAssignmentIds.Contains(resource.AssignmentId))
            .ToArray();
        foreach (var assignment in stateStore.ReadAssignments()
            .Where(item => item.FocusAfterActivation
                && !stateStore.HasCompletedPlayerFocus(item.AssignmentId, item.AuthorityEpoch)))
        {
            if (!activeResources.Any(resource =>
                    string.Equals(resource.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)
                    && string.Equals(resource.EntityId, assignment.EntityId, StringComparison.Ordinal)
                    && resource.AuthorityEpoch == assignment.AuthorityEpoch))
            {
                continue;
            }
            ObserveAssignmentPreparationForPlayerFocus(
                assignment,
                prepared: true,
                cancellationToken);
        }
    }

    private async Task FocusAfterAssignmentBatchSettlesAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PlayerFocusQuietPeriod, cancellationToken);
            TacticalAssignment? assignment;
            lock (_playerFocusGate)
            {
                if (generation != _playerFocusGeneration || _pendingPlayerFocus is null)
                {
                    return;
                }
                assignment = _pendingPlayerFocus;
                _pendingPlayerFocus = null;
            }

            // Initial Primary handoffs become authoritative when the service accepts
            // AssignmentPrepared; unlike migrations, they do not receive an
            // ActivationGrant callback. Wait for the preparation batch to go quiet,
            // then focus the requested unit without blocking later assignment frames.
            await FocusAfterActivationAsync(assignment, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Player-requested camera focus failed after the assignment batch settled.");
        }
    }

    private async Task EnsureMissionSceneMaterializationReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MaterializationReadinessTimeout;
        while (true)
        {
            var result = await game.SendCommandAsync(
                GameCommandTypes.GetSceneState,
                "{}",
                CommandPriorities.Control,
                cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Mission scene readiness is unavailable: {result.ErrorCode}: {result.ErrorMessage}");
            }

            var scene = JsonSerializer.Deserialize<SceneStatePayload>(result.ResultJson, JsonOptions)
                ?? throw new InvalidDataException("Mission scene readiness state was empty.");
            if (scene.SceneLoaded
                && scene.GameMainAvailable
                && scene.ObjectsManagerAvailable
                && scene.TaskforceManagerAvailable)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "Mission scene is not materialization-ready for committed migration activation recovery.");
            }

            await Task.Delay(MaterializationReadinessPollInterval, cancellationToken);
        }
    }

    private async Task EnsureOwnedMutationsArmedAsync(
        CapabilitiesPayload capabilities,
        CancellationToken cancellationToken) =>
        await EnsureMutationModeArmedAsync(capabilities, 30_000, cancellationToken);

    private async Task<long> RestageActivationCheckpointAsync(
        ActivationGrant grant,
        TacticalAssignment assignment,
        long expectedGameRevision,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var stagingIdempotencyKey = ResolveStagingIdempotencyKey(grant, assignment);
        var sourceCheckpoint = await DownloadCheckpointAsync(
            grant.StagedCheckpointSha256,
            $"/content/{grant.StagedCheckpointSha256}",
            serviceToken,
            cancellationToken)
            ?? throw new InvalidDataException("The committed migration checkpoint content is missing.");
        var stagingContext = Context(
            assignment.AssignmentId,
            grant.AuthorityEpoch,
            expectedGameRevision,
            stagingIdempotencyKey);
        OwnedMutationContracts.ValidateOwnedLifecycleIdentifiers(stagingContext, assignment.EntityId);
        var stagedResult = await RequireGameCommandAsync(
            GameCommandTypes.StageOwnedSurfaceCheckpoint,
            JsonSerializer.Serialize(new StageOwnedSurfaceCheckpointRequestPayload
            {
                Context = stagingContext,
                EntityId = assignment.EntityId,
                SourceCheckpoint = sourceCheckpoint,
            }, JsonOptions),
            cancellationToken);
        var staged = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
            stagedResult.ResultJson,
            JsonOptions) ?? throw new InvalidDataException("Activation recovery checkpoint staging result was empty.");
        if (!staged.Unit.IsDisabled)
        {
            var resuspendContext = Context(
                assignment.AssignmentId,
                grant.AuthorityEpoch,
                staged.Revision,
                $"{stagingIdempotencyKey}-keep-suspended");
            OwnedMutationContracts.ValidateOwnedLifecycleIdentifiers(
                resuspendContext,
                assignment.EntityId);
            var resuspend = await RequireGameCommandAsync(
                GameCommandTypes.SetOwnedSurfaceSuspension,
                JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
                {
                    Context = resuspendContext,
                    EntityId = assignment.EntityId,
                    IsSuspended = true,
                }, JsonOptions),
                cancellationToken);
            staged = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
                resuspend.ResultJson,
                JsonOptions) ?? throw new InvalidDataException("Activation recovery post-stage suspension result was empty.");
        }

        if (!string.Equals(staged.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)
            || staged.AuthorityEpoch != grant.AuthorityEpoch
            || !string.Equals(staged.Unit.EntityId, assignment.EntityId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Activation recovery staging result does not match the granted assignment identity.");
        }

        await stateStore.SaveGameRevisionAsync(
            grant.AssignmentId,
            staged.Revision,
            cancellationToken);
        await stateStore.SaveStagedCheckpointAsync(
            grant.AssignmentId,
            grant.AuthorityEpoch,
            grant.StagedCheckpointSha256,
            cancellationToken);
        await stateStore.SaveStagedIdempotencyKeyAsync(
            grant.AssignmentId,
            grant.AuthorityEpoch,
            stagingIdempotencyKey,
            cancellationToken);
        return staged.Revision;
    }

    private async Task<OwnedSurfaceLifecycleResultPayload> ActivateSuspendedOwnedRecordAsync(
        ActivationGrant grant,
        TacticalAssignment assignment,
        long expectedGameRevision,
        CancellationToken cancellationToken)
    {
        var activationIdempotencyKey = ResolveActivationIdempotencyKey(grant, assignment);
        var payload = OwnedMutationContracts.BuildSetOwnedSurfaceSuspensionRequest(
            Context(
                assignment.AssignmentId,
                grant.AuthorityEpoch,
                expectedGameRevision,
                activationIdempotencyKey),
            assignment.EntityId,
            isSuspended: false);
        OwnedMutationContracts.ValidateOwnedLifecycleIdentifiers(payload.Context, payload.EntityId);
        var result = await RequireGameCommandAsync(
            GameCommandTypes.SetOwnedSurfaceSuspension,
            JsonSerializer.Serialize(payload, JsonOptions),
            cancellationToken);
        var enabled = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
            result.ResultJson,
            JsonOptions) ?? throw new InvalidDataException("Activation unsuspend result was empty.");
        ValidateActivationUnsuspendResult(enabled, grant, assignment);
        return enabled;
    }

    private async Task<bool> TryConvergeActivationFromObservedMaterializationAsync(
        ActivationGrant grant,
        TacticalAssignment assignment,
        long expectedGameRevision,
        CancellationToken cancellationToken)
    {
        var resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
        var materialized = resources.Entities.FirstOrDefault(item =>
            string.Equals(item.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)
            && string.Equals(item.EntityId, assignment.EntityId, StringComparison.Ordinal)
            && item.AuthorityEpoch == grant.AuthorityEpoch
            && item.Revision >= expectedGameRevision
            && string.Equals(item.LifecycleState, "active", StringComparison.OrdinalIgnoreCase));
        if (materialized is null)
        {
            return false;
        }

        await stateStore.SaveGameRevisionAsync(grant.AssignmentId, materialized.Revision, cancellationToken);
        await stateStore.SaveActivationGrantAsync(
            grant.AssignmentId,
            grant.AuthorityEpoch,
            grant.MigrationCommitPosition,
            cancellationToken);
        return true;
    }

    private static void ValidateActivationUnsuspendResult(
        OwnedSurfaceLifecycleResultPayload enabled,
        ActivationGrant grant,
        TacticalAssignment assignment)
    {
        if (!string.Equals(enabled.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(enabled.AssignmentId, grant.AssignmentId, StringComparison.Ordinal)
            || enabled.AuthorityEpoch != grant.AuthorityEpoch
            || enabled.Revision < 1)
        {
            throw new InvalidDataException("Activation unsuspend result does not match the granted assignment identity.");
        }
    }

    private string ResolveActivationIdempotencyKey(ActivationGrant grant, TacticalAssignment assignment)
    {
        if (!string.IsNullOrWhiteSpace(grant.IdempotencyKey))
        {
            try
            {
                return OwnedMutationContracts.ResolveActivationIdempotencyKey(
                    grant.IdempotencyKey,
                    assignment.EntityId);
            }
            catch (InvalidDataException)
            {
            }
        }

        var stagedIdempotency = stateStore.TryReadStagedIdempotencyKey(
            grant.AssignmentId,
            grant.AuthorityEpoch);
        if (!string.IsNullOrWhiteSpace(stagedIdempotency))
        {
            return OwnedMutationContracts.ResolveActivationIdempotencyKey(
                stagedIdempotency,
                assignment.EntityId);
        }

        throw new InvalidDataException(
            "Activation grant idempotency could not be resolved from the grant or durable staging state.");
    }

    private string ResolveStagingIdempotencyKey(ActivationGrant grant, TacticalAssignment assignment)
    {
        var stagedIdempotency = stateStore.TryReadStagedIdempotencyKey(
            grant.AssignmentId,
            grant.AuthorityEpoch);
        if (!string.IsNullOrWhiteSpace(stagedIdempotency))
        {
            return stagedIdempotency;
        }

        if (!string.IsNullOrWhiteSpace(grant.IdempotencyKey))
        {
            return OwnedMutationContracts.ResolveStagingIdempotencyKey(
                grant.IdempotencyKey,
                assignment.EntityId);
        }

        throw new InvalidDataException(
            "Activation recovery staging idempotency could not be resolved from the grant or durable staging state.");
    }

    private async Task<GameCommandResult> RequireGameCommandAsync(
        string type,
        string json,
        CancellationToken cancellationToken,
        int priority = CommandPriorities.Control)
    {
        var result = await game.SendCommandAsync(type, json, priority, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Game command {type} failed: {result.ErrorCode}: {result.ErrorMessage}");
        }
        return result;
    }

    private async Task<CampaignAssignmentLookup> TryResolveLiveCampaignAssignmentAsync(
        ActivationGrant grant,
        string token,
        CancellationToken cancellationToken)
    {
        using var client = CreateCampaignHttpClient(token);
        client.Timeout = TimeSpan.FromSeconds(5);
        using var response = await client.GetAsync($"/campaigns/{options.CampaignId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var viewCommitPosition = document.RootElement.TryGetProperty("commitPosition", out var commitPosition)
            && commitPosition.TryGetInt64(out var parsedCommit)
            ? parsedCommit
            : 0L;
        if (viewCommitPosition < grant.MigrationCommitPosition)
        {
            return new CampaignAssignmentLookup(CampaignViewStatus.Stale, viewCommitPosition, Assignment: null);
        }

        var grantAssignmentId = NormalizeScopedAssignmentId(grant.AssignmentId);
        foreach (var item in document.RootElement.GetProperty("assignments").EnumerateArray())
        {
            var assignmentId = item.GetProperty("assignmentId").GetString() ?? string.Empty;
            if (!string.Equals(assignmentId, grantAssignmentId, StringComparison.Ordinal))
            {
                continue;
            }

            return new CampaignAssignmentLookup(
                CampaignViewStatus.Ready,
                viewCommitPosition,
                new CampaignAssignmentView(
                    assignmentId,
                    item.GetProperty("role").GetString() ?? string.Empty,
                    item.GetProperty("lifecycle").GetString() ?? string.Empty,
                    item.GetProperty("authorityEpoch").GetInt64()));
        }

        return new CampaignAssignmentLookup(CampaignViewStatus.Ready, viewCommitPosition, Assignment: null);
    }

    private static string NormalizeScopedAssignmentId(string assignmentId) =>
        assignmentId.StartsWith("assignment:", StringComparison.Ordinal)
            ? assignmentId["assignment:".Length..]
            : assignmentId;

    private enum CampaignViewStatus
    {
        Stale,
        Ready,
    }

    private readonly record struct CampaignAssignmentLookup(
        CampaignViewStatus Status,
        long ViewCommitPosition,
        CampaignAssignmentView? Assignment);

    private sealed record CampaignAssignmentView(
        string AssignmentId,
        string Role,
        string Lifecycle,
        long AuthorityEpoch);

    private async Task UploadCheckpointAsync(
        string sha256,
        byte[] content,
        string token,
        CancellationToken cancellationToken)
    {
        using var client = CreateCampaignHttpClient(token);
        using var response = await client.PutAsync(
            $"/content/{sha256}",
            new ByteArrayContent(content),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<OwnedSurfaceCheckpointPayload?> DownloadCheckpointAsync(
        TacticalAssignment assignment,
        string token,
        CancellationToken cancellationToken) =>
        await DownloadCheckpointAsync(
            assignment.CheckpointSha256,
            assignment.CheckpointUri,
            token,
            cancellationToken);

    private async Task<OwnedSurfaceCheckpointPayload?> DownloadCheckpointAsync(
        string checkpointSha256,
        string checkpointUri,
        string token,
        CancellationToken cancellationToken)
    {
        var hasHash = !string.IsNullOrWhiteSpace(checkpointSha256);
        var hasUri = !string.IsNullOrWhiteSpace(checkpointUri);
        if (!hasHash && !hasUri)
        {
            return null;
        }
        if (!hasHash || !hasUri
            || checkpointSha256.Length != 64
            || !checkpointSha256.All(Uri.IsHexDigit)
            || !string.Equals(
                checkpointUri,
                $"/content/{checkpointSha256}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The tactical assignment checkpoint reference is invalid.");
        }
        using var client = CreateCampaignHttpClient(token);
        var content = await client.GetByteArrayAsync(checkpointUri, cancellationToken);
        var actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualHash, checkpointSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The tactical assignment checkpoint content hash is invalid.");
        }
        return JsonSerializer.Deserialize<OwnedSurfaceCheckpointPayload>(content, JsonOptions)
            ?? throw new InvalidDataException("The tactical assignment checkpoint was empty.");
    }

    private HttpClient CreateCampaignHttpClient(string token)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }, disposeHandler: true)
        {
            BaseAddress = options.HttpUri,
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private OwnedMutationContextPayload Context(string assignment, long epoch, long revision, string idempotency) => new()
    {
        SessionId = CurrentGameSessionId(),
        AssignmentId = assignment,
        AuthorityEpoch = epoch,
        ExpectedRevision = revision,
        IdempotencyKey = idempotency,
    };

    private static bool MatchesRequirement(string required, string? actual) =>
        string.IsNullOrEmpty(required) || string.Equals(required, actual, StringComparison.Ordinal);

    private static bool IsVerifiedLandProfile(
        TacticalAssignment assignment,
        CapabilitiesPayload capabilities) =>
        capabilities.OwnedLandMaterializationProfiles.Any(profile =>
            string.Equals(profile.UnitTypeId, assignment.UnitTypeId, StringComparison.Ordinal)
            && string.Equals(profile.VariantId, assignment.VariantId, StringComparison.Ordinal)
            && string.Equals(profile.LoadoutId, assignment.LoadoutId, StringComparison.Ordinal));

    private async Task EnsureLandTerrainReadyAsync(
        TacticalAssignment assignment,
        CancellationToken cancellationToken)
    {
        var latitude = assignment.LatitudeMicrodegrees / 1_000_000d;
        var longitude = assignment.LongitudeMicrodegrees / 1_000_000d;
        var probePayload = JsonSerializer.Serialize(new TerrainPlacementProbeRequestPayload
        {
            Latitude = latitude,
            Longitude = longitude,
        }, JsonOptions);
        var initial = await RequireGameCommandAsync(
            GameCommandTypes.ProbeTerrainPlacement,
            probePayload,
            cancellationToken: cancellationToken);
        var terrain = JsonSerializer.Deserialize<TerrainPlacementProbeResultPayload>(
            initial.ResultJson,
            JsonOptions) ?? throw new InvalidDataException("The terrain placement probe was empty.");
        if (terrain.PlacementReady)
        {
            return;
        }

        // The hostless-assignment path has already relocated the floating origin.
        // Relocation starts asynchronous terrain streaming, during which Sea Power
        // deliberately rejects another prepare-location command. Wait for that
        // first relocation to finalize the target tile instead of relocating twice.
        var deadline = DateTimeOffset.UtcNow + TerrainStreamingTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TerrainStreamingPollInterval, cancellationToken);
            var result = await RequireGameCommandAsync(
                GameCommandTypes.ProbeTerrainPlacement,
                probePayload,
                cancellationToken: cancellationToken);
            terrain = JsonSerializer.Deserialize<TerrainPlacementProbeResultPayload>(
                result.ResultJson,
                JsonOptions) ?? throw new InvalidDataException("The terrain placement probe was empty.");
            if (terrain.PlacementReady)
            {
                return;
            }
            if (terrain.InitialTerrainLoadingDone
                && terrain.TileFinalized
                && !terrain.IsLand)
            {
                throw new InvalidOperationException(
                    $"Ground assignment {assignment.EntityId} resolves to water after terrain streaming.");
            }
        }
        throw new TimeoutException(
            $"Ground assignment {assignment.EntityId} terrain did not finalize within "
            + $"{TerrainStreamingTimeout.TotalSeconds:F0} seconds (last state: {terrain.Postcondition}).");
    }

    private async Task EnsureOwnedUnitTacticallyReadyAsync(
        TacticalAssignment assignment,
        long minimumRevision,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + NativeUnitReadinessTimeout;
        OwnedSurfaceResourceEntryPayload? lastObserved = null;
        while (true)
        {
            var resources = await ReadOwnedSurfaceResourcesAsync(cancellationToken);
            lastObserved = resources.Entities.FirstOrDefault(item =>
                string.Equals(item.EntityId, assignment.EntityId, StringComparison.Ordinal)
                && string.Equals(item.AssignmentId, assignment.AssignmentId, StringComparison.Ordinal)
                && item.AuthorityEpoch == assignment.AuthorityEpoch
                && item.Revision >= minimumRevision);
            var requiresPlayerOrders = assignment.Relationship != TacticalRelationship.Hostile;
            var identityMatches = lastObserved is not null
                && string.Equals(lastObserved.UnitTypeId, assignment.UnitTypeId, StringComparison.Ordinal)
                && string.Equals(lastObserved.VariantId, assignment.VariantId, StringComparison.Ordinal)
                && string.Equals(lastObserved.LoadoutId, assignment.LoadoutId, StringComparison.Ordinal);
            var requiresStores = assignment.CheckpointSha256.Length == 0
                && assignment.InitialStoresMilliunits > 0;
            if (lastObserved is not null
                && string.Equals(lastObserved.LifecycleState, "active", StringComparison.OrdinalIgnoreCase)
                && lastObserved.NativeInitialized
                && identityMatches
                && (!requiresStores || lastObserved.NativeStoresInitialized)
                && (!requiresPlayerOrders
                    || (lastObserved.AcceptsPlayerOrders && lastObserved.HasNativeControl))
                && (assignment.AltitudeFeet <= 0 || lastObserved.IsAircraftInFlight))
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var observed = lastObserved is null
                    ? "owned record not visible"
                    : string.Join(
                        ", ",
                        $"lifecycle={lastObserved.LifecycleState}",
                        $"nativeInitialized={lastObserved.NativeInitialized}",
                        $"acceptsPlayerOrders={lastObserved.AcceptsPlayerOrders}",
                        $"hasNativeControl={lastObserved.HasNativeControl}",
                        $"storesInitialized={lastObserved.NativeStoresInitialized}",
                        $"inFlight={lastObserved.IsAircraftInFlight}",
                        $"realizedProfile={lastObserved.UnitTypeId}/{lastObserved.VariantId}/{lastObserved.LoadoutId}",
                        $"revision={lastObserved.Revision}");
                throw new InvalidOperationException(
                    $"Sea Power did not finish initializing tactical unit '{assignment.EntityId}' "
                    + $"within {NativeUnitReadinessTimeout.TotalSeconds:0} seconds ({observed}).");
            }

            await Task.Delay(NativeUnitReadinessPollInterval, cancellationToken);
        }
    }

    private async Task<bool> RollbackIncompleteMaterializationAsync(
        TacticalAssignment assignment,
        long materializedRevision,
        CapabilitiesPayload capabilities,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var rollbackResult = await RequireGameCommandAsync(
            GameCommandTypes.RollbackIncompleteOwnedMaterialization,
            JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
            {
                Context = Context(
                    assignment.AssignmentId,
                    assignment.AuthorityEpoch,
                    materializedRevision,
                    $"{assignment.IdempotencyKey}-rollback-incomplete"),
                EntityId = assignment.EntityId,
            }, JsonOptions),
            cancellationToken);
        var rollback = JsonSerializer.Deserialize<RollbackIncompleteOwnedMaterializationResultPayload>(
            rollbackResult.ResultJson,
            JsonOptions) ?? throw new InvalidDataException(
                "Incomplete materialization rollback result was empty.");
        if (!string.Equals(rollback.Outcome, "rolled-back", StringComparison.Ordinal)
            || !string.Equals(rollback.EntityId, assignment.EntityId, StringComparison.Ordinal)
            || !string.Equals(
                rollback.AssignmentId,
                assignment.AssignmentId,
                StringComparison.Ordinal)
            || rollback.AuthorityEpoch != assignment.AuthorityEpoch
            || rollback.Revision != materializedRevision)
        {
            throw new InvalidDataException(
                "Incomplete materialization rollback did not confirm the requested authority fence.");
        }

        try
        {
            await ReconcileFriendlyRenderProxiesAsync(
                await ReadOwnedSurfaceResourcesAsync(cancellationToken),
                capabilities,
                serviceToken,
                cancellationToken,
                force: true);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Native materialization rollback succeeded for {EntityId}, but immediate projection restoration failed. The next projection cycle will retry it.",
                assignment.EntityId);
            return false;
        }
    }

    private async Task EnsureTacticalOriginNearAssignmentAsync(
        TacticalAssignment assignment,
        CancellationToken cancellationToken)
    {
        var result = await RequireGameCommandAsync(
            GameCommandTypes.PrepareTacticalLocation,
            JsonSerializer.Serialize(new PrepareTacticalLocationRequestPayload
            {
                SessionId = CurrentGameSessionId(),
                Latitude = assignment.LatitudeMicrodegrees / 1_000_000d,
                Longitude = assignment.LongitudeMicrodegrees / 1_000_000d,
            }, JsonOptions),
            cancellationToken);
        _ = JsonSerializer.Deserialize<PrepareTacticalLocationResultPayload>(
            result.ResultJson,
            JsonOptions) ?? throw new InvalidDataException(
                "Tactical-origin preparation returned no state.");
    }

    private static long ScaleResource(long baseline, long? fractionMilliunits) => checked(
        baseline * Math.Clamp(fractionMilliunits ?? 1_000_000, 0, 1_000_000) / 1_000_000);

    private static IEnumerable<MagazineStock> CampaignMagazineReadback(
        TacticalAssignment assignment,
        AmmunitionStoreSnapshotPayload[]? captured)
    {
        if (assignment.Magazines.Count == 0 || captured is null || captured.Length == 0)
        {
            yield break;
        }

        var tracked = assignment.Magazines
            .Select(magazine => magazine.AmmunitionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var magazine in captured.Where(item => tracked.Contains(item.AmmunitionId)))
        {
            yield return new MagazineStock
            {
                AmmunitionId = magazine.AmmunitionId,
                Rounds = magazine.Quantity,
            };
        }
    }

    private static long ResolveFuelResource(long baseline, long? nativeFractionMilliunits) =>
        Math.Clamp(nativeFractionMilliunits ?? baseline, 0, 1_000_000);

    private static int QuantizeHeadingMilliDegrees(double headingDegrees)
    {
        if (!double.IsFinite(headingDegrees))
        {
            throw new InvalidDataException("Sea Power returned a non-finite tactical heading.");
        }

        var normalized = headingDegrees % 360d;
        if (normalized < 0)
        {
            normalized += 360d;
        }
        var quantized = checked((int)Math.Round(normalized * 1_000d));
        return quantized == 360_000 ? 0 : quantized;
    }

    private static double GreatCircleDistanceMeters(
        double originLatitudeDegrees,
        double originLongitudeDegrees,
        double destinationLatitudeDegrees,
        double destinationLongitudeDegrees)
    {
        var originLatitude = originLatitudeDegrees * Math.PI / 180d;
        var destinationLatitude = destinationLatitudeDegrees * Math.PI / 180d;
        var deltaLatitude = destinationLatitude - originLatitude;
        var deltaLongitude = (destinationLongitudeDegrees - originLongitudeDegrees)
            * Math.PI / 180d;
        var sinLatitude = Math.Sin(deltaLatitude / 2d);
        var sinLongitude = Math.Sin(deltaLongitude / 2d);
        var haversine = sinLatitude * sinLatitude
            + Math.Cos(originLatitude) * Math.Cos(destinationLatitude)
            * sinLongitude * sinLongitude;
        var angularDistance = 2d * Math.Atan2(
            Math.Sqrt(Math.Clamp(haversine, 0d, 1d)),
            Math.Sqrt(Math.Max(0d, 1d - haversine)));
        return MeanEarthRadiusMeters * angularDistance;
    }

    private static long ApplyDamageBaseline(long baseline, long? observed, bool destroyed) =>
        destroyed
            ? 1_000_000
            : Math.Clamp(
                baseline + Math.Clamp(observed ?? 0, 0, 1_000_000),
                0,
                1_000_000);

    private static string DeterministicId(string identity, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }

    private async Task<T> RunAtPausedTacticalSafePointAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        await _tacticalPauseGate.WaitAsync(cancellationToken);
        try
        {
            return await RunAtPausedTacticalSafePointCoreAsync(
                operation,
                operationName,
                cancellationToken);
        }
        finally
        {
            _tacticalPauseGate.Release();
        }
    }

    private async Task<T> RunAtPausedTacticalSafePointCoreAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        var inspectionResult = await RequireGameCommandAsync(
            GameCommandTypes.GetUiInspection,
            "{}",
            cancellationToken);
        var inspection = JsonSerializer.Deserialize<UiInspectionPayload>(
            inspectionResult.ResultJson,
            JsonOptions) ?? throw new InvalidDataException(
                $"Tactical UI inspection was empty before {operationName}.");
        var resumeAfterOperation = !inspection.IsPaused;
        if (resumeAfterOperation)
        {
            await RequireGameCommandAsync(
                GameCommandTypes.SetExperimentalPauseState,
                JsonSerializer.Serialize(
                    new ExperimentalPauseRequestPayload { IsPaused = true },
                    JsonOptions),
                cancellationToken);
        }

        try
        {
            return await operation();
        }
        finally
        {
            if (resumeAfterOperation)
            {
                try
                {
                    await RequireGameCommandAsync(
                        GameCommandTypes.SetExperimentalPauseState,
                        JsonSerializer.Serialize(
                            new ExperimentalPauseRequestPayload { IsPaused = false },
                            JsonOptions),
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Could not restore running tactical time after {OperationName}.",
                        operationName);
                }
            }
        }
    }

    private static string StateSyncKey(string assignmentId, long authorityEpoch) =>
        $"{assignmentId}:{authorityEpoch}";

    private static string CheckpointSpoolKey(CheckpointPublished checkpoint) =>
        $"checkpoint-{DeterministicId(
            checkpoint.AssignmentId,
            $"{checkpoint.AuthorityEpoch}:{checkpoint.EntityRevision}:{checkpoint.CheckpointSha256}")}";

    private static string StagedCheckpointSpoolKey(FinalCheckpointStaged staged) =>
        $"staged-{DeterministicId(
            staged.AssignmentId,
            $"{staged.AuthorityEpoch}:{staged.StagedCheckpointSha256}:{staged.Ready}")}";

    private string CurrentGameSessionId() => game.SessionId
        ?? throw new InvalidOperationException("The game session ended during campaign work.");

    private sealed record AssignmentPreparation(AssignmentPrepared Prepared, CheckpointPublished? Checkpoint);

    private sealed record FencePreparation(TacticalEventProposal Proposal, CheckpointPublished Checkpoint);

    private sealed class GameSessionAuthorityDrain : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _drainRequested;

        public void RequestDrain() => Interlocked.Exchange(ref _drainRequested, 1);

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            if (Volatile.Read(ref _drainRequested) == 0)
            {
                return;
            }

            _gate.Release();
            throw new OperationCanceledException(
                "The Sea Power session ended before new campaign authority work could start.",
                cancellationToken);
        }

        public Task<bool> WaitForDrainAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            _gate.WaitAsync(timeout, cancellationToken);

        public void Exit() => _gate.Release();

        public void ReleaseDrain() => _gate.Release();

        public void Dispose() => _gate.Dispose();
    }
}
