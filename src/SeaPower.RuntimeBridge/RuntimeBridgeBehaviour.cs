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
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        RuntimeCampaignMapSelectionAdapter.Install();
        RuntimePositionWatch.Install();
        _fileFingerprintProvider = RuntimeFileFingerprintProvider.Start();
        _executionThreadGuardSelfTest.Start(_mainThreadId, _fileFingerprintProvider);

        var assembly = typeof(Plugin).Assembly;
        var instance = RuntimeInstanceConfiguration.Current;
        var hello = new GameHelloPayload
        {
            ClientInstanceId = Guid.NewGuid().ToString("N"),
            RuntimeInstanceId = instance.InstanceId,
            GamePipeName = instance.PipeName,
            PersistentDataPath = Application.persistentDataPath,
            ExperimentalSaveDirectory = instance.ExperimentalSaveDirectory,
            PluginVersion = assembly.GetName().Version?.ToString() ?? "unknown",
            ProcessId = Process.GetCurrentProcess().Id,
            GameVersion = Application.version,
            InitialScene = SceneManager.GetActiveScene().name ?? string.Empty,
            ColliderCacheDirectory = RuntimeColliderCatalog.CurrentRoot(),
        };

        _pipeClient = new PipeGameClient(
            hello,
            instance.PipeName,
            QueueCommand,
            CancelCommand,
            EndSession,
            QueueLogMessage);
        RuntimeEventPublisher.Bind(_pipeClient.SendEvent);
        _pipeClient.Start();
        UnityEngine.Debug.Log(
            $"[Sea Power Runtime Bridge] game-control bridge started for instance {instance.InstanceId} on pipe {instance.PipeName}");
    }

    private void Update()
    {
        RuntimeCampaignMapSelectionAdapter.TryInstallViewportHook();
        RuntimeVisualRenderProxyAdapter.Tick();
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
            if (GameCommandDispatcher.TakeEndOfFrameAction() is { } endOfFrameAction)
            {
                StartCoroutine(RunEndOfFrameAction(endOfFrameAction));
            }
            processed++;
        }
    }

    private static IEnumerator RunDeferredAction(Action action)
    {
        yield return new WaitForSecondsRealtime(0.25f);
        action();
    }

    private static IEnumerator RunEndOfFrameAction(Action action)
    {
        yield return new WaitForEndOfFrame();
        action();
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        RuntimePositionWatch.Reset();
        RuntimeEventPublisher.Unbind();
        _pipeClient?.Dispose();
        _pipeClient = null;
    }

    private static void OnActiveSceneChanged(Scene previous, Scene current)
    {
        RuntimeVisualRenderProxyAdapter.ReleaseScenePresentation();
        RuntimeCampaignMapSelectionAdapter.ResetSceneViewport();
        RuntimePositionWatch.Reset();
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
        RuntimeOwnedMutationAdapter.EndSession(sessionId);
        RuntimeOwnedLandMutationAdapter.EndSession(sessionId);
        RuntimeVisualRenderProxyAdapter.ClearAll();
        RuntimeCampaignMapSelectionAdapter.ResetSceneViewport();
        RuntimePositionWatch.Reset();
        var removed = _commands.RemoveSession(sessionId);
        if (removed > 0)
        {
            QueueLogMessage(
                $"[Sea Power Runtime Bridge] removed {removed} queued commands from ended session {sessionId}",
                true);
        }
    }
}
