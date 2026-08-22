using System.Collections.Concurrent;

using CrimeanTom.Campaign.Transport.V1;

using Grpc.Core;
using Grpc.Net.Client;

namespace CrimeanTom.Campaign.OperationalWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = OperationalWorkerOptions.FromEnvironment(args);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await RunAsync(options, shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static async Task RunAsync(OperationalWorkerOptions options, CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(options.TokenFile, cancellationToken)).Trim();
        long observedCommitPosition = 0;
        var assignments = new ConcurrentDictionary<string, OperationalAssignment>(StringComparer.Ordinal);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                observedCommitPosition = await ConnectOnceAsync(
                    options,
                    token,
                    observedCommitPosition,
                    assignments,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await File.AppendAllTextAsync(
                    Path.Combine(options.RecordDirectory, "worker.stderr.log"),
                    $"{DateTimeOffset.UtcNow:o} {exception}{Environment.NewLine}",
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static async Task<long> ConnectOnceAsync(
        OperationalWorkerOptions options,
        string token,
        long reconnectCommitPosition,
        ConcurrentDictionary<string, OperationalAssignment> assignments,
        CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(options.GrpcUri, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false },
        });
        var client = new CampaignCompanionTransport.CampaignCompanionTransportClient(channel);
        var headers = new Metadata { { "authorization", $"Bearer {token}" } };
        using var call = client.Connect(headers, cancellationToken: cancellationToken);
        var writeGate = new SemaphoreSlim(1, 1);
        ulong nextSequence = 1;
        var observedCommitPosition = reconnectCommitPosition;
        await call.RequestStream.WriteAsync(new CompanionFrame
        {
            Sequence = nextSequence,
            Hello = new CompanionHello
            {
                ProtocolVersion = 1,
                ClientInstanceId = options.ClientInstanceId,
                CampaignId = options.CampaignId,
                ReconnectCommitPosition = reconnectCommitPosition,
                CatalogFingerprintSha256 = options.CatalogFingerprintSha256,
                WorkerKind = CampaignWorkerKind.Operational,
                TacticalEntityCapacity = options.Capacity,
                TacticalReserveCapacity = options.Reserve,
                Health = "Healthy",
                Eligibility = "Eligible",
            },
        });

        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;
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

                        heartbeatTask ??= RunHeartbeatAsync();
                        break;
                    case ServiceFrame.PayloadOneofCase.CommittedEvent:
                        observedCommitPosition = Math.Max(
                            observedCommitPosition,
                            frame.CommittedEvent.CommitPosition);
                        break;
                    case ServiceFrame.PayloadOneofCase.Assignment:
                        await HandleAssignmentAsync(frame.Assignment);
                        await SendAcknowledgementAsync(frame.Sequence, observedCommitPosition);
                        break;
                    case ServiceFrame.PayloadOneofCase.Fence:
                        assignments.TryRemove(frame.Fence.AssignmentId, out _);
                        await SendAcknowledgementAsync(frame.Sequence, observedCommitPosition);
                        break;
                    case ServiceFrame.PayloadOneofCase.Command:
                    case ServiceFrame.PayloadOneofCase.ActivationGrant:
                    case ServiceFrame.PayloadOneofCase.StageFinalCheckpoint:
                        await SendPreparedAsync(
                            frame.PayloadCase == ServiceFrame.PayloadOneofCase.Command
                                ? frame.Command.AssignmentId
                                : frame.PayloadCase == ServiceFrame.PayloadOneofCase.ActivationGrant
                                    ? frame.ActivationGrant.AssignmentId
                                    : frame.StageFinalCheckpoint.DestinationAssignmentId,
                            frame.PayloadCase == ServiceFrame.PayloadOneofCase.Command
                                ? frame.Command.EntityId
                                : frame.PayloadCase == ServiceFrame.PayloadOneofCase.ActivationGrant
                                    ? frame.ActivationGrant.EntityId
                                    : frame.StageFinalCheckpoint.EntityId,
                            frame.PayloadCase == ServiceFrame.PayloadOneofCase.Command
                                ? frame.Command.AuthorityEpoch
                                : frame.PayloadCase == ServiceFrame.PayloadOneofCase.ActivationGrant
                                    ? frame.ActivationGrant.AuthorityEpoch
                                    : frame.StageFinalCheckpoint.DestinationAuthorityEpoch,
                            ready: false,
                            "operational-worker-rejects-tactical");
                        await SendAcknowledgementAsync(frame.Sequence, observedCommitPosition);
                        break;
                    case ServiceFrame.PayloadOneofCase.Acknowledgement:
                    case ServiceFrame.PayloadOneofCase.Heartbeat:
                    case ServiceFrame.PayloadOneofCase.Error:
                        break;
                }
            }
        }
        finally
        {
            heartbeatCancellation.Cancel();
            if (heartbeatTask is not null)
            {
                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
                {
                }
            }
        }

        return observedCommitPosition;

        async Task HandleAssignmentAsync(TacticalAssignment assignment)
        {
            var tactical = !string.IsNullOrWhiteSpace(assignment.RequiredRuntimeFingerprintSha256)
                || !string.IsNullOrWhiteSpace(assignment.TaskforceId);
            if (tactical)
            {
                await SendPreparedAsync(
                    assignment.AssignmentId,
                    assignment.EntityId,
                    assignment.AuthorityEpoch,
                    ready: false,
                    "operational-worker-rejects-tactical");
                return;
            }

            assignments[assignment.AssignmentId] = new OperationalAssignment(
                assignment.AssignmentId,
                assignment.EntityId,
                assignment.AuthorityEpoch);
            await SendPreparedAsync(
                assignment.AssignmentId,
                assignment.EntityId,
                assignment.AuthorityEpoch,
                ready: true,
                errorCode: string.Empty);
        }

        async Task SendPreparedAsync(
            string assignmentId,
            string entityId,
            long authorityEpoch,
            bool ready,
            string errorCode)
        {
            await writeGate.WaitAsync(cancellationToken);
            try
            {
                await call.RequestStream.WriteAsync(new CompanionFrame
                {
                    Sequence = ++nextSequence,
                    AssignmentPrepared = new AssignmentPrepared
                    {
                        AssignmentId = assignmentId,
                        EntityId = entityId,
                        AuthorityEpoch = authorityEpoch,
                        Ready = ready,
                        ErrorCode = errorCode,
                        ErrorMessage = ready ? string.Empty : "Track A workers cannot materialize Sea Power tactical islands.",
                    },
                });
            }
            finally
            {
                writeGate.Release();
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

        async Task RunHeartbeatAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), heartbeatCancellation.Token);
            while (!heartbeatCancellation.IsCancellationRequested)
            {
                var owned = assignments.Count;
                await writeGate.WaitAsync(heartbeatCancellation.Token);
                try
                {
                    await call.RequestStream.WriteAsync(new CompanionFrame
                    {
                        Sequence = ++nextSequence,
                        Heartbeat = new CompanionHeartbeat
                        {
                            ObservedCommitPosition = observedCommitPosition,
                            SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            CatalogFingerprintSha256 = options.CatalogFingerprintSha256,
                            WorkerKind = CampaignWorkerKind.Operational,
                            OwnedMaterializedEntityCount = owned,
                            OwnedActiveEntityCount = owned,
                            TacticalEntityCapacity = options.Capacity,
                            TacticalReserveCapacity = options.Reserve,
                            Health = "Healthy",
                            Eligibility = "Eligible",
                        },
                    });
                }
                finally
                {
                    writeGate.Release();
                }

                await Task.Delay(TimeSpan.FromSeconds(1), heartbeatCancellation.Token);
            }
        }
    }
}

internal sealed record OperationalAssignment(string AssignmentId, string EntityId, long AuthorityEpoch);

internal sealed record OperationalWorkerOptions(
    string CampaignId,
    Uri GrpcUri,
    string TokenFile,
    string ClientInstanceId,
    string RecordDirectory,
    string CatalogFingerprintSha256,
    int Capacity,
    int Reserve)
{
    public static OperationalWorkerOptions FromEnvironment(string[] args)
    {
        var campaignId = Require("CRIMEAN_TOM_CAMPAIGN_ID");
        var grpcUrl = Require("CRIMEAN_TOM_CAMPAIGN_GRPC_URL");
        if (!Uri.TryCreate(grpcUrl, UriKind.Absolute, out var grpcUri))
        {
            throw new InvalidOperationException("CRIMEAN_TOM_CAMPAIGN_GRPC_URL must be an absolute URI.");
        }

        var tokenFile = Require("CRIMEAN_TOM_CAMPAIGN_TOKEN_FILE");
        var clientInstanceId = Require("CRIMEAN_TOM_CLIENT_INSTANCE_ID");
        var recordDirectory = Require("CRIMEAN_TOM_SIM_WORKER_RECORD_DIRECTORY");
        Directory.CreateDirectory(recordDirectory);
        _ = args;
        return new(
            campaignId,
            grpcUri,
            tokenFile,
            clientInstanceId,
            recordDirectory,
            Environment.GetEnvironmentVariable("CRIMEAN_TOM_CATALOG_FINGERPRINT") ?? string.Empty,
            ParseCapacity("CRIMEAN_TOM_SIM_WORKER_CAPACITY", 8),
            ParseCapacity("CRIMEAN_TOM_SIM_WORKER_RESERVE", 0));
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }

        return value.Trim();
    }

    private static int ParseCapacity(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var parsed) || parsed < 0 || parsed > 10_000)
        {
            throw new InvalidOperationException($"{name} must be an integer between 0 and 10000.");
        }

        return parsed;
    }
}
