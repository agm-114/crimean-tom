using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

public static class GamePipeConnectionStates
{
    public const string Starting = "starting";
    public const string Waiting = "waiting-for-game";
    public const string Handshaking = "handshaking";
    public const string Connected = "connected";
    public const string Stopping = "stopping";
}

public sealed class GamePipeBroker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<GamePipeBroker> _logger;
    private readonly string _pipeName;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _transportWriteTimeout;
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new();
    private readonly Channel<GameRuntimeEvent> _runtimeEvents = Channel.CreateBounded<GameRuntimeEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly SemaphoreSlim _pendingSlots;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _waitingControlWriters;
    private int _waitingBulkWriters;
    private int _consecutiveControlWrites;
    private const int MaximumConsecutiveControlWrites = 8;
    private static readonly TimeSpan MaximumBulkPriorityDelay = TimeSpan.FromMilliseconds(25);
    private readonly object _sessionLock = new();
    private GameSession? _session;
    private string _connectionState = GamePipeConnectionStates.Starting;
    private long _sessionGeneration;
    private long _lastConnectionTransitionUnixMilliseconds =
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private bool _stopping;

    public GamePipeBroker(ILogger<GamePipeBroker> logger)
        : this(logger, new GamePipeBrokerOptions())
    {
    }

    public GamePipeBroker(ILogger<GamePipeBroker> logger, string pipeName)
        : this(logger, new GamePipeBrokerOptions
        {
            PipeName = pipeName,
        })
    {
    }

    public GamePipeBroker(
        ILogger<GamePipeBroker> logger,
        GamePipeBrokerOptions options)
    {
        _logger = logger;
        ArgumentNullException.ThrowIfNull(options);
        _pipeName = string.IsNullOrWhiteSpace(options.PipeName)
            ? throw new ArgumentException("A pipe name is required.", nameof(options))
            : options.PipeName;
        _commandTimeout = RequirePositiveTimeout(
            options.CommandTimeout,
            nameof(options.CommandTimeout));
        _transportWriteTimeout = RequirePositiveTimeout(
            options.TransportWriteTimeout,
            nameof(options.TransportWriteTimeout));
        if (options.MaximumPendingCommands <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The maximum pending command count must be positive.");
        }

        _pendingSlots = new SemaphoreSlim(
            options.MaximumPendingCommands,
            options.MaximumPendingCommands);
    }

    public bool IsGameConnected
    {
        get
        {
            lock (_sessionLock)
            {
                return _session is not null;
            }
        }
    }

    public string? SessionId
    {
        get
        {
            lock (_sessionLock)
            {
                return _session?.SessionId;
            }
        }
    }

    public GameHelloPayload? GameHello
    {
        get
        {
            lock (_sessionLock)
            {
                return _session?.Hello;
            }
        }
    }

    public string ConnectionState
    {
        get
        {
            lock (_sessionLock)
            {
                return _connectionState;
            }
        }
    }

    public long SessionGeneration
    {
        get
        {
            lock (_sessionLock)
            {
                return _sessionGeneration;
            }
        }
    }

    public long LastConnectionTransitionUnixMilliseconds
    {
        get
        {
            lock (_sessionLock)
            {
                return _lastConnectionTransitionUnixMilliseconds;
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sessionLock)
        {
            _stopping = true;
            SetConnectionStateLocked(GamePipeConnectionStates.Stopping);
        }

        return base.StopAsync(cancellationToken);
    }

    public async Task<GameCommandResult> SendCommandAsync(
        string commandType,
        string payloadJson,
        int priority,
        CancellationToken cancellationToken)
    {
        if (!GameCommandTypes.AllCommands.Contains(commandType, StringComparer.Ordinal))
        {
            return GameCommandResult.Unsupported(commandType);
        }

        if (!CommandPriorities.IsDefined(priority))
        {
            return GameCommandResult.InvalidPriority(priority);
        }

        GameSession? session;
        lock (_sessionLock)
        {
            session = _session;
        }

        if (session is null)
        {
            return GameCommandResult.Disconnected();
        }

        bool slotAcquired;
        try
        {
            slotAcquired = await _pendingSlots.WaitAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return GameCommandResult.Cancelled();
        }

        if (!slotAcquired)
        {
            return GameCommandResult.Busy();
        }

        var commandStarted = DateTimeOffset.UtcNow;
        using var deadlineCancellation = new CancellationTokenSource(_commandTimeout);
        using var commandLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);

        var messageId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<CommandResponsePayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingCommand(session.SessionId, completion);

        if (!_pending.TryAdd(messageId, pending))
        {
            _pendingSlots.Release();
            throw new InvalidOperationException($"Duplicate command ID '{messageId}'.");
        }

        try
        {
            var deadline = commandStarted.Add(_commandTimeout);
            var command = new WireEnvelope
            {
                Kind = MessageKinds.Command,
                MessageId = messageId,
                SessionId = session.SessionId,
                MessageType = commandType,
                PayloadJson = payloadJson,
                DeadlineUnixMilliseconds = deadline.ToUnixTimeMilliseconds(),
                Priority = priority,
            };

            var commandSent = false;

            try
            {
                await AcquireWriteLockAsync(priority, commandLifetime.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return GameCommandResult.Cancelled();
            }
            catch (OperationCanceledException)
            {
                return GameCommandResult.TimedOut();
            }

            try
            {
                lock (_sessionLock)
                {
                    if (!ReferenceEquals(session, _session))
                    {
                        return GameCommandResult.Disconnected();
                    }
                }

                using var writeTimeout = new CancellationTokenSource(_transportWriteTimeout);
                using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    commandLifetime.Token,
                    writeTimeout.Token);
                try
                {
                    await WriteEnvelopeAsync(session.Pipe, command, writeCancellation.Token);
                    commandSent = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    session.Pipe.Dispose();
                    return GameCommandResult.Cancelled();
                }
                catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
                {
                    session.Pipe.Dispose();
                    return GameCommandResult.TimedOut();
                }
                catch (OperationCanceledException)
                {
                    session.Pipe.Dispose();
                    return GameCommandResult.TransportFailure();
                }
                catch (Exception exception) when (
                    exception is IOException or ObjectDisposedException)
                {
                    session.Pipe.Dispose();
                    return GameCommandResult.TransportFailure();
                }
            }
            finally
            {
                _writeLock.Release();
            }

            CommandResponsePayload response;
            try
            {
                response = await completion.Task.WaitAsync(commandLifetime.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (commandSent)
                {
                    await TrySendCancellationAsync(session, messageId, "caller-cancelled");
                }

                return GameCommandResult.Cancelled();
            }
            catch (OperationCanceledException)
            {
                if (commandSent)
                {
                    await TrySendCancellationAsync(session, messageId, "deadline-elapsed");
                }

                return GameCommandResult.TimedOut();
            }

            return response.Success
                ? GameCommandResult.Completed(response.ResultJson)
                : GameCommandResult.Failed(response.ErrorCode, response.ErrorMessage, response.ErrorId);
        }
        catch (InvalidDataException exception)
        {
            return GameCommandResult.InvalidFrame(exception.Message);
        }
        catch (IOException)
        {
            return GameCommandResult.Disconnected();
        }
        finally
        {
            _pending.TryRemove(messageId, out _);
            _pendingSlots.Release();
        }
    }

    public ValueTask<GameRuntimeEvent> ReadRuntimeEventAsync(CancellationToken cancellationToken) =>
        _runtimeEvents.Reader.ReadAsync(cancellationToken);

    private async Task AcquireWriteLockAsync(int priority, CancellationToken cancellationToken)
    {
        var isControl = priority == CommandPriorities.Control;
        var priorityWait = System.Diagnostics.Stopwatch.StartNew();
        if (isControl)
        {
            Interlocked.Increment(ref _waitingControlWriters);
        }
        else
        {
            Interlocked.Increment(ref _waitingBulkWriters);
        }

        try
        {
            while (true)
            {
                var controlMustYield = isControl
                    && Volatile.Read(ref _waitingBulkWriters) > 0
                    && Volatile.Read(ref _consecutiveControlWrites)
                        >= MaximumConsecutiveControlWrites;
                var bulkMustYield = !isControl
                    && Volatile.Read(ref _waitingControlWriters) > 0
                    && Volatile.Read(ref _consecutiveControlWrites)
                        < MaximumConsecutiveControlWrites
                    && priorityWait.Elapsed < MaximumBulkPriorityDelay;
                if (controlMustYield || bulkMustYield)
                {
                    await Task.Delay(1, cancellationToken);
                    continue;
                }

                await _writeLock.WaitAsync(cancellationToken);
                controlMustYield = isControl
                    && Volatile.Read(ref _waitingBulkWriters) > 0
                    && Volatile.Read(ref _consecutiveControlWrites)
                        >= MaximumConsecutiveControlWrites;
                bulkMustYield = !isControl
                    && Volatile.Read(ref _waitingControlWriters) > 0
                    && Volatile.Read(ref _consecutiveControlWrites)
                        < MaximumConsecutiveControlWrites
                    && priorityWait.Elapsed < MaximumBulkPriorityDelay;
                if (!controlMustYield && !bulkMustYield)
                {
                    if (isControl)
                    {
                        Interlocked.Increment(ref _consecutiveControlWrites);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _consecutiveControlWrites, 0);
                    }
                    return;
                }

                _writeLock.Release();
                await Task.Delay(1, cancellationToken);
            }
        }
        finally
        {
            if (isControl)
            {
                Interlocked.Decrement(ref _waitingControlWriters);
            }
            else
            {
                Interlocked.Decrement(ref _waitingBulkWriters);
            }
        }
    }

    private static TimeSpan RequirePositiveTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Timeouts must be positive.");
        }

        return value;
    }

    private async Task TrySendCancellationAsync(
        GameSession session,
        string commandMessageId,
        string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var lockAcquired = false;

        try
        {
            await AcquireWriteLockAsync(CommandPriorities.Control, timeout.Token);
            lockAcquired = true;

            lock (_sessionLock)
            {
                if (!ReferenceEquals(session, _session))
                {
                    return;
                }
            }

            await WriteEnvelopeAsync(session.Pipe, new WireEnvelope
            {
                Kind = MessageKinds.CancelCommand,
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = commandMessageId,
                SessionId = session.SessionId,
                MessageType = MessageKinds.CancelCommand,
                PayloadJson = JsonSerializer.Serialize(new CancelCommandPayload
                {
                    Reason = reason,
                }, JsonOptions),
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow
                    .AddMilliseconds(500)
                    .ToUnixTimeMilliseconds(),
                Priority = CommandPriorities.Control,
            }, timeout.Token);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogDebug(
                exception,
                "Cancellation send timed out for game command {CommandMessageId}",
                commandMessageId);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException)
        {
            session.Pipe.Dispose();
            _logger.LogDebug(
                exception,
                "Could not transmit cancellation for game command {CommandMessageId}",
                commandMessageId);
        }
        finally
        {
            if (lockAcquired)
            {
                _writeLock.Release();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SetConnectionState(GamePipeConnectionStates.Waiting);
            GameSession? session = null;
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                _logger.LogInformation("Waiting for Sea Power on pipe {PipeName}", _pipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
                SetConnectionState(GamePipeConnectionStates.Handshaking);
                session = await AcceptSessionAsync(pipe, stoppingToken);
                if (session is not null)
                {
                    await RunSessionAsync(session, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Game pipe session failed");
            }
            finally
            {
                DisconnectSession(session);
            }
        }
    }

    private async Task<GameSession?> AcceptSessionAsync(
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(ProtocolConstants.HandshakeTimeoutMilliseconds);

        var helloEnvelope = await ReadEnvelopeAsync(pipe, timeout.Token);
        if (helloEnvelope is null)
        {
            return null;
        }

        if (helloEnvelope.ProtocolVersion != ProtocolConstants.Version)
        {
            await RejectHandshakeAsync(
                pipe,
                helloEnvelope,
                ProtocolErrorCodes.ProtocolVersionMismatch,
                $"Companion supports protocol {ProtocolConstants.Version}, received {helloEnvelope.ProtocolVersion}.",
                stoppingToken);
            return null;
        }

        if (!string.Equals(helloEnvelope.Kind, MessageKinds.Hello, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(helloEnvelope.MessageId))
        {
            await RejectHandshakeAsync(
                pipe,
                helloEnvelope,
                ProtocolErrorCodes.InvalidMessage,
                "The first frame must be a hello envelope with a message ID.",
                stoppingToken);
            return null;
        }

        GameHelloPayload? hello;
        try
        {
            hello = JsonSerializer.Deserialize<GameHelloPayload>(helloEnvelope.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            hello = null;
        }

        if (hello is null
            || string.IsNullOrWhiteSpace(hello.ClientInstanceId)
            || hello.ProcessId <= 0
            || string.IsNullOrWhiteSpace(hello.PluginVersion))
        {
            await RejectHandshakeAsync(
                pipe,
                helloEnvelope,
                ProtocolErrorCodes.InvalidMessage,
                "The hello payload is missing required game metadata.",
                stoppingToken);
            return null;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var accepted = CreateHandshakeResponse(
            helloEnvelope,
            MessageKinds.HelloAccepted,
            new HandshakeResponsePayload
            {
                Accepted = true,
                SessionId = sessionId,
                ProtocolVersion = ProtocolConstants.Version,
                MaximumFrameBytes = ProtocolConstants.MaximumFrameBytes,
            },
            sessionId);
        await WriteEnvelopeAsync(pipe, accepted, stoppingToken);

        var session = new GameSession(pipe, sessionId, hello);
        lock (_sessionLock)
        {
            if (_stopping)
            {
                pipe.Dispose();
                return null;
            }

            _session = session;
            _sessionGeneration++;
            SetConnectionStateLocked(GamePipeConnectionStates.Connected);
        }

        _logger.LogInformation(
            "Sea Power connected: session {SessionId}, process {ProcessId}, game {GameVersion}, plugin {PluginVersion}",
            sessionId,
            hello.ProcessId,
            hello.GameVersion,
            hello.PluginVersion);
        return session;
    }

    private async Task RunSessionAsync(GameSession session, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && session.Pipe.IsConnected)
        {
            var envelope = await ReadEnvelopeAsync(session.Pipe, stoppingToken);
            if (envelope is null)
            {
                break;
            }

            if (envelope.ProtocolVersion != ProtocolConstants.Version
                || envelope.SessionId != session.SessionId)
            {
                throw new InvalidDataException("Sea Power returned an invalid session envelope.");
            }

            if (string.Equals(envelope.Kind, MessageKinds.Event, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(envelope.MessageId)
                    || string.IsNullOrWhiteSpace(envelope.MessageType))
                {
                    throw new InvalidDataException("Sea Power returned an invalid event envelope.");
                }
                _runtimeEvents.Writer.TryWrite(new GameRuntimeEvent(
                    envelope.MessageType,
                    envelope.PayloadJson,
                    envelope.MessageId,
                    session.SessionId));
                continue;
            }

            if (!string.Equals(envelope.Kind, MessageKinds.Response, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(envelope.CorrelationId))
            {
                throw new InvalidDataException("Sea Power returned an invalid response envelope.");
            }

            var response = JsonSerializer.Deserialize<CommandResponsePayload>(
                envelope.PayloadJson,
                JsonOptions)
                ?? throw new InvalidDataException("Sea Power returned an empty command response.");
            if (_pending.TryGetValue(envelope.CorrelationId, out var pending)
                && pending.SessionId == session.SessionId)
            {
                pending.Completion.TrySetResult(response);
            }
        }
    }

    private async Task RejectHandshakeAsync(
        NamedPipeServerStream pipe,
        WireEnvelope helloEnvelope,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Rejected Sea Power pipe handshake: {ErrorCode}", errorCode);
        var rejected = CreateHandshakeResponse(
            helloEnvelope,
            MessageKinds.HelloRejected,
            new HandshakeResponsePayload
            {
                Accepted = false,
                ProtocolVersion = ProtocolConstants.Version,
                MaximumFrameBytes = ProtocolConstants.MaximumFrameBytes,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
            },
            sessionId: null);
        await WriteEnvelopeAsync(pipe, rejected, cancellationToken);
    }

    private static WireEnvelope CreateHandshakeResponse(
        WireEnvelope helloEnvelope,
        string kind,
        HandshakeResponsePayload payload,
        string? sessionId)
    {
        return new WireEnvelope
        {
            Kind = kind,
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = helloEnvelope.MessageId,
            SessionId = sessionId,
            MessageType = "game",
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
        };
    }

    private static async Task<WireEnvelope?> ReadEnvelopeAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var json = await PipeFrameCodec.ReadAsync(
            pipe,
            ProtocolConstants.MaximumFrameBytes,
            cancellationToken);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WireEnvelope>(json, JsonOptions)
                ?? throw new InvalidDataException("Pipe frame did not contain an envelope.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Pipe frame contained invalid JSON.", exception);
        }
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

    private void DisconnectSession(GameSession? session)
    {
        if (session is null)
        {
            return;
        }

        var disconnected = false;
        lock (_sessionLock)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
                SetConnectionStateLocked(
                    _stopping
                        ? GamePipeConnectionStates.Stopping
                        : GamePipeConnectionStates.Waiting);
                disconnected = true;
            }
        }

        if (!disconnected)
        {
            return;
        }

        foreach (var pending in _pending.Values.Where(
            item => item.SessionId == session.SessionId))
        {
            pending.Completion.TrySetException(
                new IOException("Sea Power disconnected from the companion pipe."));
        }

        _logger.LogInformation(
            "Sea Power disconnected from pipe session {SessionId}",
            session.SessionId);
    }

    private void SetConnectionState(string state)
    {
        lock (_sessionLock)
        {
            if (_stopping && state != GamePipeConnectionStates.Stopping)
            {
                return;
            }

            SetConnectionStateLocked(state);
        }
    }

    private void SetConnectionStateLocked(string state)
    {
        if (string.Equals(_connectionState, state, StringComparison.Ordinal))
        {
            return;
        }

        _connectionState = state;
        _lastConnectionTransitionUnixMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private sealed record GameSession(
        NamedPipeServerStream Pipe,
        string SessionId,
        GameHelloPayload Hello);

    private sealed record PendingCommand(
        string SessionId,
        TaskCompletionSource<CommandResponsePayload> Completion);
}

public sealed record GameRuntimeEvent(
    string EventType,
    string PayloadJson,
    string MessageId,
    string SessionId);
