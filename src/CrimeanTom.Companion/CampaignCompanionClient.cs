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
            },
        });

        foreach (var staged in stateStore.ReadOutbound())
        {
            await SendAsync(staged.Frame, staged.Key);
        }

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
                    var preparation = await PrepareAssignmentAsync(frame.Assignment, capabilities, token, cancellationToken);
                    if (preparation.Checkpoint is { } checkpoint)
                    {
                        await SendAsync(
                            new CompanionFrame { CheckpointPublished = checkpoint },
                            $"checkpoint-{checkpoint.AssignmentId}-{checkpoint.EntityRevision}");
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
                    var proposal = await ApplyFenceAsync(frame.Fence, cancellationToken);
                    await SendAsync(
                        new CompanionFrame { TacticalEvent = proposal },
                        $"event-{proposal.EventId}");
                    break;
                }
                case ServiceFrame.PayloadOneofCase.Acknowledgement:
                    if (pending.TryRemove(frame.Acknowledgement.CompanionSequence, out var key)
                        && frame.Acknowledgement.DurablyAccepted)
                    {
                        stateStore.AcknowledgeOutbound(key);
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
            Destroyed = update.Unit.IsDestroyed,
        };
    }

    private async Task<TacticalEventProposal> ApplyFenceAsync(AuthorityFence fence, CancellationToken cancellationToken)
    {
        var result = await RequireGameCommandAsync(
            GameCommandTypes.SetOwnedSurfaceSuspension,
            JsonSerializer.Serialize(new SetOwnedSurfaceSuspensionRequestPayload
            {
                Context = Context(fence.AssignmentId, fence.FencedAuthorityEpoch,
                    fence.ExpectedEntityRevision, fence.IdempotencyKey),
                EntityId = fence.EntityId,
                IsSuspended = true,
            }, JsonOptions),
            cancellationToken);
        var suspension = JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Tactical fence result was empty.");
        return new TacticalEventProposal
        {
            EventId = DeterministicId(fence.AssignmentId, fence.IdempotencyKey),
            IdempotencyKey = fence.IdempotencyKey,
            AssignmentId = fence.AssignmentId,
            EntityId = fence.EntityId,
            AuthorityEpoch = fence.FencedAuthorityEpoch,
            ExpectedEntityRevision = suspension.Revision - 1,
            EffectiveTimeQuanta = fence.EffectiveTimeQuanta,
            Kind = TacticalEventKind.Checkpoint,
        };
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

    private static string DeterministicId(string identity, string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}:{idempotencyKey}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }

    private string CurrentGameSessionId() => game.SessionId
        ?? throw new InvalidOperationException("The game session ended during campaign work.");

    private sealed record AssignmentPreparation(AssignmentPrepared Prepared, CheckpointPublished? Checkpoint);
}
