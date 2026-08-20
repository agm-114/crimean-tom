using System.Text.Json;

using CrimeanTom.Protocol;

namespace SeaPowerRuntimeBridge;

internal sealed class ExecutionThreadGuardSelfTest
{
    public const string Pending = "pending";
    public const string Passed = "passed";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private int _started;
    private string _result = Pending;

    public string Result => Volatile.Read(ref _result);

    public void Start(
        int initializedMainThreadId,
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() => Run(initializedMainThreadId, fileFingerprintProvider));
    }

    private void Run(
        int initializedMainThreadId,
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        var result = "failed:worker-exception";

        try
        {
            var command = new WireEnvelope
            {
                Kind = MessageKinds.Command,
                MessageId = $"execution-thread-self-test-{Guid.NewGuid():N}",
                SessionId = "execution-thread-self-test",
                MessageType = GameCommandTypes.Ping,
                PayloadJson = "{}",
                DeadlineUnixMilliseconds = DateTimeOffset.UtcNow
                    .AddSeconds(5)
                    .ToUnixTimeMilliseconds(),
                Priority = CommandPriorities.Control,
            };
            var response = GameCommandDispatcher.Execute(
                command,
                initializedMainThreadId,
                fileFingerprintProvider,
                Pending);

            if (!string.Equals(response.Kind, MessageKinds.Response, StringComparison.Ordinal)
                || !string.Equals(response.CorrelationId, command.MessageId, StringComparison.Ordinal)
                || !string.Equals(response.SessionId, command.SessionId, StringComparison.Ordinal))
            {
                result = "failed:invalid-response-envelope";
            }
            else
            {
                var payload = JsonSerializer.Deserialize<CommandResponsePayload>(
                    response.PayloadJson,
                    JsonOptions);
                result = payload switch
                {
                    null => "failed:invalid-response-payload",
                    { Success: true } => "failed:unexpected-success",
                    { ErrorCode: ProtocolErrorCodes.WrongExecutionThread } => Passed,
                    _ => "failed:unexpected-error-code",
                };
            }
        }
        catch
        {
            // The capability payload intentionally exposes no exception details.
        }

        Volatile.Write(ref _result, result);
    }
}
