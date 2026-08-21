using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    private string? _armedMutationSessionId;
    private long _armedMutationExpiresAtUnixMilliseconds;

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
                await ConnectOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(options.TokenFile, cancellationToken)).Trim();
        using var channel = GrpcChannel.ForAddress(options.GrpcUri, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
            DisposeHttpClient = true,
        });
        var client = new CampaignCompanionTransport.CampaignCompanionTransportClient(channel);
        var headers = new Metadata { { "authorization", $"Bearer {token}" } };
        using var call = client.Connect(headers, cancellationToken: cancellationToken);
        var writeGate = new SemaphoreSlim(1, 1);
        ulong nextSequence = 1;
        var pending = new ConcurrentDictionary<ulong, string>();
        var capabilities = await ReadCapabilitiesAsync(cancellationToken);
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
                TacticalEntityCapacity = options.TacticalEntityCapacity,
                TacticalReserveCapacity = options.TacticalReserveCapacity,
                MaterializationProfiles =
                {
                    capabilities.OwnedSurfaceMaterializationProfiles.Select(profile =>
                        new TacticalMaterializationProfile
                        {
                            UnitTypeId = profile.UnitTypeId,
                            VariantId = profile.VariantId,
                            LoadoutId = profile.LoadoutId,
                        }),
                },
            },
        });

        foreach (var staged in stateStore.ReadOutbound())
        {
            await SendAsync(staged.Frame, staged.Key);
        }

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeatAsync(heartbeatCancellation.Token);
        try
        {
            ulong previousServiceSequence = 0;
            await foreach (var frame in call.ResponseStream.ReadAllAsync(cancellationToken))
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
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask;
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
            await stateStore.StageOutboundAsync(spoolKey, outbound, cancellationToken);
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
            while (!heartbeatToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), heartbeatToken);
                try
                {
                    var current = await ReadCapabilitiesAsync(heartbeatToken);
                    var couplingRelationships = await ReadCouplingRelationshipsAsync(heartbeatToken);
                    var resources = await ReadOwnedSurfaceResourcesAsync(heartbeatToken);
                    await writeGate.WaitAsync(heartbeatToken);
                    try
                    {
                        await call.RequestStream.WriteAsync(new CompanionFrame
                        {
                            Sequence = ++nextSequence,
                            Heartbeat = new CompanionHeartbeat
                            {
                                ObservedCommitPosition = stateStore.ReadCursor(),
                                SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                GameSessionId = CurrentGameSessionId(),
                                RuntimeFingerprintSha256 = current.RuntimeFingerprint.FingerprintSha256 ?? string.Empty,
                                CatalogFingerprintSha256 = current.RuntimeFingerprint.CatalogSha256 ?? string.Empty,
                                PlayerTaskforceId = current.PlayerTaskforceId,
                                MaterializationProfiles =
                                {
                                    current.OwnedSurfaceMaterializationProfiles.Select(profile =>
                                        new TacticalMaterializationProfile
                                        {
                                            UnitTypeId = profile.UnitTypeId,
                                            VariantId = profile.VariantId,
                                            LoadoutId = profile.LoadoutId,
                                        }),
                                },
                                CouplingRelationships = { couplingRelationships },
                                OwnedMaterializedEntityCount = resources.RetainedConstructedObjectCount,
                                OwnedActiveEntityCount = resources.ActiveObjectCount,
                                OwnedRetainedSuspendedEntityCount = resources.SuspendedRetainedObjectCount,
                                OwnedRecoveryRequiredEntityCount = resources.RecoveryRequiredCount,
                                TacticalEntityCapacity = options.TacticalEntityCapacity,
                                TacticalReserveCapacity = options.TacticalReserveCapacity,
                            },
                        });
                    }
                    finally
                    {
                        writeGate.Release();
                    }
                }
                catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Campaign heartbeat capability refresh was skipped.");
                }
            }
        }
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

    private static void ResolveCampaignParticipants(
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

    private static string? CampaignEntityId(string? stableUnitId)
    {
        const string prefix = "campaign-unit:unit:crimean-tom:";
        if (stableUnitId is null || !stableUnitId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        return Guid.TryParseExact(stableUnitId[prefix.Length..], "N", out var parsed)
            ? parsed.ToString("N")
            : null;
    }

    private async Task<AssignmentPreparation> PrepareAssignmentAsync(
        TacticalAssignment assignment,
        CapabilitiesPayload capabilities,
        string serviceToken,
        CancellationToken cancellationToken)
    {
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
            var sourceCheckpoint = await DownloadCheckpointAsync(
                assignment,
                serviceToken,
                cancellationToken);

            await RequireGameCommandAsync(
                GameCommandTypes.SetExperimentalPauseState,
                JsonSerializer.Serialize(new ExperimentalPauseRequestPayload { IsPaused = true }, JsonOptions),
                cancellationToken);
            await RequireGameCommandAsync(
                GameCommandTypes.ArmOwnedMutationMode,
                JsonSerializer.Serialize(new ArmOwnedMutationModeRequestPayload
                {
                    SessionId = CurrentGameSessionId(),
                    RuntimeFingerprintSha256 = capabilities.RuntimeFingerprint.FingerprintSha256,
                    Confirmation = "enable-owned-mutations",
                    DurationSeconds = 300,
                }, JsonOptions),
                cancellationToken);
            var result = await RequireGameCommandAsync(
                GameCommandTypes.MaterializeOwnedSurfaceUnit,
                JsonSerializer.Serialize(new MaterializeOwnedSurfaceUnitRequestPayload
                {
                    Context = Context(assignment.AssignmentId, assignment.AuthorityEpoch,
                        assignment.ExpectedEntityRevision, assignment.IdempotencyKey),
                    EntityId = assignment.EntityId,
                    UnitTypeId = assignment.UnitTypeId,
                    VariantId = assignment.VariantId,
                    LoadoutId = assignment.LoadoutId,
                    TaskforceId = assignment.TaskforceId,
                    Latitude = assignment.LatitudeMicrodegrees / 1_000_000d,
                    Longitude = assignment.LongitudeMicrodegrees / 1_000_000d,
                    HeadingDegrees = assignment.HeadingMillidegrees / 1_000d,
                    Telegraph = assignment.Telegraph,
                    SourceCheckpoint = sourceCheckpoint,
                }, JsonOptions),
                cancellationToken);
            var materialized = JsonSerializer.Deserialize<MaterializeOwnedSurfaceUnitResultPayload>(result.ResultJson, JsonOptions)
                ?? throw new InvalidDataException("Materialization result was empty.");

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
        var result = await RequireGameCommandAsync(
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
            HeadingMillidegrees = checked((int)Math.Round((update.Unit.HeadingDegrees ?? 0) * 1_000)),
            Telegraph = update.Unit.Telegraph,
            Destroyed = update.Unit.IsDestroyed,
        };
    }

    private async Task<FencePreparation> ApplyFenceAsync(
        AuthorityFence fence,
        string serviceToken,
        CancellationToken cancellationToken)
    {
        var expectedGameRevision = fence.ResolveExpectedRevisionFromWorker
            ? stateStore.ReadGameRevision(fence.AssignmentId)
            : fence.ExpectedEntityRevision;
        var checkpointResult = await RequireGameCommandAsync(
            GameCommandTypes.CaptureOwnedSurfaceCheckpoint,
            JsonSerializer.Serialize(new OwnedSurfaceEntityRequestPayload
            {
                Context = Context(fence.AssignmentId, fence.FencedAuthorityEpoch,
                    expectedGameRevision, $"{fence.IdempotencyKey}-checkpoint"),
                EntityId = fence.EntityId,
            }, JsonOptions),
            cancellationToken);
        var checkpointBytes = Encoding.UTF8.GetBytes(checkpointResult.ResultJson);
        var checkpointHash = Convert.ToHexString(SHA256.HashData(checkpointBytes)).ToLowerInvariant();
        await UploadCheckpointAsync(checkpointHash, checkpointBytes, serviceToken, cancellationToken);
        var checkpoint = JsonSerializer.Deserialize<OwnedSurfaceCheckpointPayload>(
            checkpointResult.ResultJson,
            JsonOptions) ?? throw new InvalidDataException("Final tactical checkpoint was empty.");
        var assignment = stateStore.ReadAssignment(fence.AssignmentId);
        if (assignment.AuthorityEpoch != fence.FencedAuthorityEpoch
            || !string.Equals(assignment.EntityId, fence.EntityId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The authority fence does not match the durable assignment baseline.");
        }
        var unit = checkpoint.Unit;
        if (unit.Latitude is null || unit.Longitude is null || unit.HeadingDegrees is null)
        {
            throw new InvalidDataException("The final tactical checkpoint has incomplete kinematics.");
        }

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
        var suspension = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Tactical fence result was empty.");
        await stateStore.SaveGameRevisionAsync(fence.AssignmentId, suspension.Revision, cancellationToken);
        return new FencePreparation(
            new TacticalEventProposal
            {
                EventId = DeterministicId(fence.AssignmentId, fence.IdempotencyKey),
                IdempotencyKey = fence.IdempotencyKey,
                AssignmentId = fence.AssignmentId,
                EntityId = fence.EntityId,
                AuthorityEpoch = fence.FencedAuthorityEpoch,
                ExpectedEntityRevision = suspension.Revision - 1,
                EffectiveTimeQuanta = fence.EffectiveTimeQuanta,
                Kind = TacticalEventKind.Checkpoint,
                LatitudeMicrodegrees = checked((int)Math.Round(unit.Latitude.Value * 1_000_000)),
                LongitudeMicrodegrees = checked((int)Math.Round(unit.Longitude.Value * 1_000_000)),
                HeadingMillidegrees = checked((int)Math.Round(unit.HeadingDegrees.Value * 1_000)),
                Telegraph = unit.Telegraph,
                FuelMilliunits = ScaleResource(
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
        if (stateStore.HasSupersededActivationGrant(grant.AssignmentId, grant.AuthorityEpoch))
        {
            return;
        }

        if (stateStore.HasActivationGrant(grant.AssignmentId, grant.AuthorityEpoch))
        {
            return;
        }

        var liveAssignment = await TryResolveLiveCampaignAssignmentAsync(
            grant,
            serviceToken,
            cancellationToken);
        if (liveAssignment is null
            || !string.Equals(liveAssignment.Role, "Primary", StringComparison.Ordinal)
            || !string.Equals(liveAssignment.Lifecycle, "Active", StringComparison.Ordinal)
            || liveAssignment.AuthorityEpoch != grant.AuthorityEpoch)
        {
            var reason = liveAssignment is null
                ? "campaign-assignment-missing"
                : liveAssignment.Lifecycle is "Released"
                    ? "campaign-assignment-released"
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
            return;
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
            return;
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
                return;
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
            return;
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
        CancellationToken cancellationToken)
    {
        var sessionId = CurrentGameSessionId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (string.Equals(_armedMutationSessionId, sessionId, StringComparison.Ordinal)
            && _armedMutationExpiresAtUnixMilliseconds > now)
        {
            return;
        }

        await RequireGameCommandAsync(
            GameCommandTypes.SetExperimentalPauseState,
            JsonSerializer.Serialize(new ExperimentalPauseRequestPayload { IsPaused = true }, JsonOptions),
            cancellationToken);
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
            JsonOptions) ?? throw new InvalidDataException("Owned mutation arming result was empty.");
        if (!string.Equals(armed.SessionId, sessionId, StringComparison.Ordinal)
            || armed.ExpiresAtUnixMilliseconds is not long expiresAtUnixMilliseconds)
        {
            throw new InvalidDataException("Owned mutation arming did not bind to the current game session.");
        }

        _armedMutationSessionId = sessionId;
        _armedMutationExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
    }

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
        CancellationToken cancellationToken)
    {
        var result = await game.SendCommandAsync(type, json, CommandPriorities.Control, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Game command {type} failed: {result.ErrorCode}: {result.ErrorMessage}");
        }
        return result;
    }

    private async Task<CampaignAssignmentView?> TryResolveLiveCampaignAssignmentAsync(
        ActivationGrant grant,
        string token,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = options.HttpUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync($"/campaigns/{options.CampaignId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var grantAssignmentId = NormalizeScopedAssignmentId(grant.AssignmentId);
        foreach (var item in document.RootElement.GetProperty("assignments").EnumerateArray())
        {
            var assignmentId = item.GetProperty("assignmentId").GetString() ?? string.Empty;
            if (!string.Equals(assignmentId, grantAssignmentId, StringComparison.Ordinal))
            {
                continue;
            }

            return new CampaignAssignmentView(
                assignmentId,
                item.GetProperty("role").GetString() ?? string.Empty,
                item.GetProperty("lifecycle").GetString() ?? string.Empty,
                item.GetProperty("authorityEpoch").GetInt64(),
                item.TryGetProperty("workerClientInstanceId", out var worker)
                    ? worker.GetString() ?? string.Empty
                    : string.Empty);
        }

        return null;
    }

    private static string NormalizeScopedAssignmentId(string assignmentId) =>
        assignmentId.StartsWith("assignment:", StringComparison.Ordinal)
            ? assignmentId["assignment:".Length..]
            : assignmentId;

    private sealed record CampaignAssignmentView(
        string AssignmentId,
        string Role,
        string Lifecycle,
        long AuthorityEpoch,
        string WorkerClientInstanceId);

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

    private static long ScaleResource(long baseline, long? fractionMilliunits) => checked(
        baseline * Math.Clamp(fractionMilliunits ?? 1_000_000, 0, 1_000_000) / 1_000_000);

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
}
