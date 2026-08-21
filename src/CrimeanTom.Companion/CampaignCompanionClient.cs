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
        using var channel = GrpcChannel.ForAddress(options.GrpcUri);
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
            if (!assignment.GloballyPaused
                || assignment.Role != AssignmentRole.Primary
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
            await stateStore.SaveGameRevisionAsync(
                assignment.AssignmentId,
                materialized.Revision,
                cancellationToken);
            return new AssignmentPreparation(
                new AssignmentPrepared
                {
                    AssignmentId = assignment.AssignmentId,
                    EntityId = assignment.EntityId,
                    AuthorityEpoch = assignment.AuthorityEpoch,
                    RealizedRevision = materialized.Revision,
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
                DamageMilliunits = Math.Clamp(unit.DamageFractionMilliunits ?? 0, 0, 1_000_000),
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

    private async Task UploadCheckpointAsync(
        string sha256,
        byte[] content,
        string token,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = options.HttpUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PutAsync(
            $"/content/{sha256}",
            new ByteArrayContent(content),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<OwnedSurfaceCheckpointPayload?> DownloadCheckpointAsync(
        TacticalAssignment assignment,
        string token,
        CancellationToken cancellationToken)
    {
        var hasHash = !string.IsNullOrWhiteSpace(assignment.CheckpointSha256);
        var hasUri = !string.IsNullOrWhiteSpace(assignment.CheckpointUri);
        if (!hasHash && !hasUri)
        {
            return null;
        }
        if (!hasHash || !hasUri
            || assignment.CheckpointSha256.Length != 64
            || !assignment.CheckpointSha256.All(Uri.IsHexDigit)
            || !string.Equals(
                assignment.CheckpointUri,
                $"/content/{assignment.CheckpointSha256}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The tactical assignment checkpoint reference is invalid.");
        }
        using var client = new HttpClient { BaseAddress = options.HttpUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var content = await client.GetByteArrayAsync(assignment.CheckpointUri, cancellationToken);
        var actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualHash, assignment.CheckpointSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The tactical assignment checkpoint content hash is invalid.");
        }
        return JsonSerializer.Deserialize<OwnedSurfaceCheckpointPayload>(content, JsonOptions)
            ?? throw new InvalidDataException("The tactical assignment checkpoint was empty.");
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

    private static string DeterministicId(string identity, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }

    private static string CheckpointSpoolKey(CheckpointPublished checkpoint) =>
        $"checkpoint-{DeterministicId(
            checkpoint.AssignmentId,
            $"{checkpoint.AuthorityEpoch}:{checkpoint.EntityRevision}:{checkpoint.CheckpointSha256}")}";

    private string CurrentGameSessionId() => game.SessionId
        ?? throw new InvalidOperationException("The game session ended during campaign work.");

    private sealed record AssignmentPreparation(AssignmentPrepared Prepared, CheckpointPublished? Checkpoint);

    private sealed record FencePreparation(TacticalEventProposal Proposal, CheckpointPublished Checkpoint);
}
