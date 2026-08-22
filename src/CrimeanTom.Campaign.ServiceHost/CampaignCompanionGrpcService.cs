using System.Security.Cryptography;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.Transport.V1;

using Google.Protobuf;

using Grpc.Core;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignCompanionGrpcService(
    CampaignServiceToken token,
    CampaignSessionRegistry sessions,
    CampaignInboundSpool inboundSpool,
    CampaignCoordinator coordinator,
    SqliteCampaignJournal journal,
    ILogger<CampaignCompanionGrpcService> logger)
    : CampaignCompanionTransport.CampaignCompanionTransportBase
{
    public override async Task Connect(
        IAsyncStreamReader<CompanionFrame> requestStream,
        IServerStreamWriter<ServiceFrame> responseStream,
        ServerCallContext context)
    {
        RequireAuthentication(context, token);
        if (!await requestStream.MoveNext(context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A hello frame is required."));
        }

        var first = requestStream.Current;
        if (first.Sequence != 1 || first.PayloadCase != CompanionFrame.PayloadOneofCase.Hello)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first frame must be sequence-1 hello."));
        }
        var hello = first.Hello;
        if (hello.ProtocolVersion != 1
            || !Guid.TryParseExact(hello.ClientInstanceId, "N", out _)
            || !Guid.TryParseExact(hello.CampaignId, "N", out var campaignGuid)
            || hello.ReconnectCommitPosition < 0
            || !CampaignWorkerKind.TryValidate(hello.WorkerKind, out var workerKind))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The companion hello is incompatible."));
        }

        if (CampaignWorkerKind.IsOperational(workerKind))
        {
            if (!string.IsNullOrWhiteSpace(hello.RuntimeFingerprintSha256)
                || !string.IsNullOrWhiteSpace(hello.PlayerTaskforceId)
                || !string.IsNullOrWhiteSpace(hello.GameSessionId)
                || hello.MaterializationProfiles.Count > 0)
            {
                throw new RpcException(new Status(
                    StatusCode.FailedPrecondition,
                    "Operational workers must not advertise a Sea Power runtime, task force, or materialization profile."));
            }

            if (hello.TacticalEntityCapacity is < 1 or > 10_000
                || hello.TacticalReserveCapacity < 0
                || hello.TacticalReserveCapacity > hello.TacticalEntityCapacity)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Operational worker capacity is invalid."));
            }
        }

        var campaignId = new CampaignId(campaignGuid);
        var currentHead = await journal.ReadHeadAsync(campaignId, context.CancellationToken);
        if (hello.ReconnectCommitPosition > currentHead.Value)
        {
            throw new RpcException(new Status(StatusCode.OutOfRange, "Reconnect cursor is ahead of the durable campaign head."));
        }

        var session = sessions.Open(hello);
        try
        {
            await responseStream.WriteAsync(new ServiceFrame
            {
                Sequence = session.NextSequence(),
                Welcome = new ServiceWelcome
                {
                    ProtocolVersion = 1,
                    ServiceSessionId = session.SessionId,
                    CampaignId = hello.CampaignId,
                    CurrentCommitPosition = currentHead.Value,
                    ResumedAfterCommitPosition = hello.ReconnectCommitPosition,
                    IssuedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });

            var events = await journal.ReadEventsAsync(
                campaignId,
                new CommitPosition(hello.ReconnectCommitPosition),
                context.CancellationToken);
            foreach (var campaignEvent in events.Where(item => item.CommitPosition.Value <= currentHead.Value))
            {
                var json = CampaignContractJson.Serialize(campaignEvent);
                await responseStream.WriteAsync(new ServiceFrame
                {
                    Sequence = session.NextSequence(),
                    CommittedEvent = new CrimeanTom.Campaign.Transport.V1.CommittedCampaignEvent
                    {
                        CommitPosition = campaignEvent.CommitPosition.Value,
                        EventId = campaignEvent.EventId.ToString(),
                        EventJson = ByteString.CopyFrom(json),
                        EventSha256 = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant(),
                    },
                });
            }

            var incoming = ReadIncomingAsync(requestStream, session, context.CancellationToken);
            await foreach (var frame in session.Outgoing.ReadAllAsync(context.CancellationToken))
            {
                frame.Sequence = session.NextSequence();
                await responseStream.WriteAsync(frame);
            }
            await incoming;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await inboundSpool.AbandonSessionAsync(session);
            }
            finally
            {
                sessions.Close(session);
                logger.LogInformation(
                    "Campaign companion session {SessionId} for client {ClientId} closed at cursor {Cursor}.",
                    session.SessionId,
                    hello.ClientInstanceId,
                    session.Snapshot().LastAppliedCommitPosition);
            }
        }
    }

    private async Task ReadIncomingAsync(
        IAsyncStreamReader<CompanionFrame> stream,
        CampaignCompanionSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            ulong previousSequence = 1;
            while (await stream.MoveNext(cancellationToken))
            {
                var frame = stream.Current;
                if (frame.Sequence != previousSequence + 1 || frame.PayloadCase == CompanionFrame.PayloadOneofCase.Hello)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Companion frame sequence is invalid."));
                }
                previousSequence = frame.Sequence;
                if (frame.PayloadCase != CompanionFrame.PayloadOneofCase.Heartbeat)
                {
                    var receipt = await inboundSpool.AcceptAsync(session, frame, cancellationToken);
                    if (!receipt.AlreadyProcessed)
                    {
                        await coordinator.ProcessCompanionFrameAsync(session.Hello, frame, cancellationToken);
                    }
                    await inboundSpool.CompleteAsync(receipt, cancellationToken);
                }
                session.Observe(frame);
                if (!session.TryQueue(new ServiceFrame
                {
                    Acknowledgement = new ServiceAcknowledgement
                    {
                        CompanionSequence = frame.Sequence,
                        DurablyAccepted = true,
                    },
                }))
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "The service control queue is full."));
                }
            }
        }
        finally
        {
            session.Complete("client-stream-complete");
        }
    }

    private static void RequireAuthentication(ServerCallContext context, CampaignServiceToken token)
    {
        var value = context.RequestHeaders.FirstOrDefault(item => item.Key == "authorization")?.Value;
        const string prefix = "Bearer ";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || !token.IsMatch(value[prefix.Length..]))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A valid campaign-service bearer token is required."));
        }
    }
}
