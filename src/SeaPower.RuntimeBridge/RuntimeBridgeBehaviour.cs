using System.Diagnostics;
using System.Collections;

using CrimeanTom.Protocol;

using UnityEngine;
using UnityEngine.SceneManagement;

namespace SeaPowerRuntimeBridge;

public sealed class RuntimeBridgeBehaviour : MonoBehaviour
{
    private const int MaximumCommandsPerFrame = 32;
    private const int MaximumLogMessagesPerFrame = 16;

    private readonly BoundedPriorityEnvelopeQueue _commands = new(
        ProtocolConstants.MaximumGameCommandQueue);
    private readonly BoundedConcurrentQueue<(string Message, bool IsWarning)> _logMessages = new(128);
    private readonly ExecutionThreadGuardSelfTest _executionThreadGuardSelfTest = new();
    private PipeGameClient? _pipeClient;
    private RuntimeFileFingerprintProvider? _fileFingerprintProvider;
    private int _mainThreadId;

    private void Awake()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        _fileFingerprintProvider = RuntimeFileFingerprintProvider.Start();
        _executionThreadGuardSelfTest.Start(_mainThreadId, _fileFingerprintProvider);

        var assembly = typeof(Plugin).Assembly;
        var hello = new GameHelloPayload
        {
            ClientInstanceId = Guid.NewGuid().ToString("N"),
            PluginVersion = assembly.GetName().Version?.ToString() ?? "unknown",
            ProcessId = Process.GetCurrentProcess().Id,
            GameVersion = Application.version,
            InitialScene = SceneManager.GetActiveScene().name ?? string.Empty,
        };

        _pipeClient = new PipeGameClient(
            hello,
            QueueCommand,
            CancelCommand,
            EndSession,
            QueueLogMessage);
        _pipeClient.Start();
        UnityEngine.Debug.Log("[Sea Power Runtime Bridge] game-control bridge started");
    }

    private void Update()
    {
        var logged = 0;
        while (logged < MaximumLogMessagesPerFrame && _logMessages.TryDequeue(out var message))
        {
            if (message.IsWarning)
            {
                UnityEngine.Debug.LogWarning(message.Message);
            }
            else
            {
                UnityEngine.Debug.Log(message.Message);
            }

            logged++;
        }

        var processed = 0;
        while (processed < MaximumCommandsPerFrame && _commands.TryDequeue(out var command))
        {
            var response = GameCommandDispatcher.Execute(
                command,
                _mainThreadId,
                _fileFingerprintProvider ?? RuntimeFileFingerprintProvider.Start(),
                _executionThreadGuardSelfTest.Result);
            _pipeClient?.Send(response);
            if (GameCommandDispatcher.TakeDeferredAction() is { } deferredAction)
            {
                StartCoroutine(RunDeferredAction(deferredAction));
            }
            processed++;
        }
    }

    private static IEnumerator RunDeferredAction(Action action)
    {
        yield return new WaitForSecondsRealtime(0.25f);
        action();
    }

    private void OnDestroy()
    {
        _pipeClient?.Dispose();
        _pipeClient = null;
    }

    private void QueueLogMessage(string message, bool isWarning)
    {
        _logMessages.TryEnqueue((message, isWarning));
    }

    private PriorityEnqueueResult QueueCommand(WireEnvelope command)
    {
        return _commands.TryEnqueue(command);
    }

    private bool CancelCommand(string messageId)
    {
        return _commands.TryCancel(messageId);
    }

    private void EndSession(string sessionId)
    {
        var removed = _commands.RemoveSession(sessionId);
        if (removed > 0)
        {
            QueueLogMessage(
                $"[Sea Power Runtime Bridge] removed {removed} queued commands from ended session {sessionId}",
                true);
        }
    }
}
