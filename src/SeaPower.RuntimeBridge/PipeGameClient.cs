using System.IO.Pipes;
using System.Text.Json;

using CrimeanTom.Protocol;

namespace SeaPowerRuntimeBridge;

internal sealed class PipeGameClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GameHelloPayload _hello;
    private readonly string _pipeName;
    private readonly Func<WireEnvelope, PriorityEnqueueResult> _receiveCommand;
    private readonly Func<string, bool> _cancelCommand;
    private readonly Action<string> _endSession;
    private readonly Action<string, bool> _log;
    private readonly BoundedPriorityEnvelopeQueue _outbound = new(
        ProtocolConstants.MaximumGameOutboundQueue);
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeClientStream? _activePipe;
    private string? _activeSessionId;
    private Task? _backgroundTask;

    public PipeGameClient(
        GameHelloPayload hello,
        string pipeName,
        Func<WireEnvelope, PriorityEnqueueResult> receiveCommand,
        Func<string, bool> cancelCommand,
        Action<string> endSession,
        Action<string, bool> log)
    {
        _hello = hello;
        _pipeName = pipeName;
        _receiveCommand = receiveCommand;
        _cancelCommand = cancelCommand;
        _endSession = endSession;
        _log = log;
    }

    public void Start()
    {
        _backgroundTask ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    public bool Send(WireEnvelope message)
    {
        if (_outbound.TryEnqueue(message) == PriorityEnqueueResult.Enqueued)
        {
            return true;
        }

        _log("[Sea Power Runtime Bridge] outbound pipe queue is full; reconnecting instead of dropping a response", true);
        _activePipe?.Dispose();
        return false;
    }

    public bool SendEvent(string eventType, string payloadJson, int priority)
    {
        var sessionId = _activeSessionId;
        if (string.IsNullOrEmpty(sessionId) || !CommandPriorities.IsDefined(priority))
        {
            return false;
        }
        return Send(new WireEnvelope
        {
            Kind = MessageKinds.Event,
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            MessageType = eventType,
            PayloadJson = payloadJson,
            Priority = priority,
        });
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _activePipe?.Dispose();

        try
        {
            _backgroundTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Unity is tearing down; the cancellation path is best effort.
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string? sessionId = null;
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                _activePipe = pipe;
                await pipe.ConnectAsync(2_000, stoppingToken).ConfigureAwait(false);

                sessionId = await PerformHandshakeAsync(pipe, stoppingToken).ConfigureAwait(false);
                _activeSessionId = sessionId;
                _log($"[Sea Power Runtime Bridge] companion accepted pipe session {sessionId}", false);

                using var sessionStopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var readTask = ReadLoopAsync(pipe, sessionId, sessionStopping.Token);
                var writeTask = WriteLoopAsync(pipe, sessionId, sessionStopping.Token);
                var completedTask = await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);

                sessionStopping.Cancel();
                pipe.Dispose();
                try
                {
                    await Task.WhenAll(readTask, writeTask).ConfigureAwait(false);
                }
                catch
                {
                    // Surface the task that ended the session. Cancellation or
                    // disposal of the other task is expected during reconnect.
                    await completedTask.ConfigureAwait(false);
                    throw;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _log($"[Sea Power Runtime Bridge] companion pipe unavailable: {exception}", true);
            }
            finally
            {
                _activePipe = null;
                _activeSessionId = null;
                if (sessionId is not null)
                {
                    var droppedResponses = _outbound.RemoveSession(sessionId);
                    _endSession(sessionId);
                    if (droppedResponses > 0)
                    {
                        _log(
                            $"[Sea Power Runtime Bridge] dropped {droppedResponses} stale responses from ended session {sessionId}",
                            true);
                    }
                }
            }

            try
            {
                await Task.Delay(1_000, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<string> PerformHandshakeAsync(
        NamedPipeClientStream pipe,
        CancellationToken stoppingToken)
    {
        var helloMessageId = Guid.NewGuid().ToString("N");
        await WriteEnvelopeAsync(pipe, new WireEnvelope
        {
            Kind = MessageKinds.Hello,
            MessageId = helloMessageId,
            MessageType = "game",
            PayloadJson = JsonSerializer.Serialize(_hello, JsonOptions),
        }, stoppingToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(ProtocolConstants.HandshakeTimeoutMilliseconds);
        var response = await ReadEnvelopeAsync(pipe, timeout.Token).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The companion closed the pipe during handshake.");

        if (response.ProtocolVersion != ProtocolConstants.Version
            || response.CorrelationId != helloMessageId
            || (response.Kind != MessageKinds.HelloAccepted
                && response.Kind != MessageKinds.HelloRejected))
        {
            throw new InvalidDataException("The companion returned an invalid handshake response.");
        }

        var payload = JsonSerializer.Deserialize<HandshakeResponsePayload>(response.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("The companion returned an empty handshake response.");
        if (!payload.Accepted || response.Kind == MessageKinds.HelloRejected)
        {
            throw new InvalidDataException(
                $"Companion rejected the handshake ({payload.ErrorCode ?? "unknown"}): "
                + (payload.ErrorMessage ?? "no reason supplied"));
        }

        if (string.IsNullOrWhiteSpace(payload.SessionId)
            || payload.SessionId != response.SessionId
            || payload.ProtocolVersion != ProtocolConstants.Version
            || payload.MaximumFrameBytes != ProtocolConstants.MaximumFrameBytes)
        {
            throw new InvalidDataException("The companion negotiated unsupported session parameters.");
        }

        return payload.SessionId;
    }

    private async Task ReadLoopAsync(
        NamedPipeClientStream pipe,
        string sessionId,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var envelope = await ReadEnvelopeAsync(pipe, stoppingToken).ConfigureAwait(false);
            if (envelope is null)
            {
                throw new EndOfStreamException("The companion closed the pipe.");
            }

            if (envelope.ProtocolVersion != ProtocolConstants.Version
                || envelope.SessionId != sessionId)
            {
                throw new InvalidDataException("The companion sent an invalid command envelope.");
            }

            if (string.Equals(envelope.Kind, MessageKinds.CancelCommand, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(envelope.MessageId)
                    || string.IsNullOrWhiteSpace(envelope.CorrelationId)
                    || !string.Equals(
                        envelope.MessageType,
                        MessageKinds.CancelCommand,
                        StringComparison.Ordinal)
                    || envelope.Priority != CommandPriorities.Control
                    || envelope.DeadlineUnixMilliseconds is null)
                {
                    throw new InvalidDataException("The companion sent an invalid cancel-command frame.");
                }

                CancelCommandPayload? cancellation;
                try
                {
                    cancellation = JsonSerializer.Deserialize<CancelCommandPayload>(
                        envelope.PayloadJson,
                        JsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        "The companion sent an invalid cancel-command payload.",
                        exception);
                }

                if (cancellation is null || string.IsNullOrWhiteSpace(cancellation.Reason))
                {
                    throw new InvalidDataException(
                        "The companion sent an empty cancel-command payload.");
                }

                if (envelope.DeadlineUnixMilliseconds
                    <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    continue;
                }

                _cancelCommand(envelope.CorrelationId);
                continue;
            }

            if (!string.Equals(envelope.Kind, MessageKinds.Command, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The companion sent an unsupported pipe message kind.");
            }

            var enqueueResult = _receiveCommand(envelope);
            if (enqueueResult != PriorityEnqueueResult.Enqueued)
            {
                var (code, message) = enqueueResult switch
                {
                    PriorityEnqueueResult.Full => (
                        ProtocolErrorCodes.QueueFull,
                        "The Unity command queue is full; retry after the game catches up."),
                    PriorityEnqueueResult.InvalidPriority => (
                        ProtocolErrorCodes.InvalidMessage,
                        "The command priority is not supported."),
                    PriorityEnqueueResult.DuplicateMessageId => (
                        ProtocolErrorCodes.InvalidMessage,
                        "The command message ID is already queued."),
                    _ => (
                        ProtocolErrorCodes.InvalidMessage,
                        "The command envelope is invalid."),
                };
                Send(GameCommandDispatcher.Reject(envelope, code, message));
            }
        }
    }

    private async Task WriteLoopAsync(
        NamedPipeClientStream pipe,
        string sessionId,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_outbound.TryDequeue(out var message))
            {
                if (message.SessionId == sessionId)
                {
                    await WriteEnvelopeAsync(pipe, message, stoppingToken).ConfigureAwait(false);
                }
            }
            else
            {
                await Task.Delay(25, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<WireEnvelope?> ReadEnvelopeAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var json = await PipeFrameCodec.ReadAsync(
            pipe,
            ProtocolConstants.MaximumFrameBytes,
            cancellationToken).ConfigureAwait(false);
        return json is null
            ? null
            : JsonSerializer.Deserialize<WireEnvelope>(json, JsonOptions);
    }

    private static Task WriteEnvelopeAsync(
        Stream pipe,
        WireEnvelope message,
        CancellationToken cancellationToken)
    {
        return PipeFrameCodec.WriteAsync(
            pipe,
            JsonSerializer.Serialize(message, JsonOptions),
            ProtocolConstants.MaximumFrameBytes,
            cancellationToken);
    }
}
