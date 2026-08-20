using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

public sealed class GameCommandResult
{
    public bool Success { get; init; }

    public int HttpStatusCode { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorId { get; init; }

    public string ResultJson { get; init; } = "{}";

    public static GameCommandResult Disconnected()
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status503ServiceUnavailable,
            ErrorCode = "game-disconnected",
            ErrorMessage = "Sea Power is not connected to the companion pipe.",
        };
    }

    public static GameCommandResult TimedOut()
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status504GatewayTimeout,
            ErrorCode = "game-command-timeout",
            ErrorMessage = "Sea Power did not respond before the command timeout.",
        };
    }

    public static GameCommandResult TransportFailure()
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status502BadGateway,
            ErrorCode = "game-transport-failure",
            ErrorMessage = "The command could not be transmitted to Sea Power.",
        };
    }

    public static GameCommandResult Cancelled()
    {
        return new GameCommandResult
        {
            HttpStatusCode = 499,
            ErrorCode = "request-cancelled",
            ErrorMessage = "The caller cancelled the game command.",
        };
    }

    public static GameCommandResult Busy()
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status429TooManyRequests,
            ErrorCode = ProtocolErrorCodes.QueueFull,
            ErrorMessage = "The companion command queue is full; retry later.",
        };
    }

    public static GameCommandResult InvalidFrame(string message)
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status413PayloadTooLarge,
            ErrorCode = "invalid-frame",
            ErrorMessage = message,
        };
    }

    public static GameCommandResult InvalidPriority(int priority)
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status400BadRequest,
            ErrorCode = ProtocolErrorCodes.InvalidMessage,
            ErrorMessage = $"Command priority '{priority}' is not supported.",
        };
    }

    public static GameCommandResult Unsupported(string commandType)
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status404NotFound,
            ErrorCode = ProtocolErrorCodes.UnsupportedCommand,
            ErrorMessage = $"Command '{commandType}' is not exposed by the companion.",
        };
    }

    public static GameCommandResult Failed(string? code, string? message, string? errorId)
    {
        return new GameCommandResult
        {
            HttpStatusCode = StatusCodes.Status422UnprocessableEntity,
            ErrorCode = code ?? "game-command-failed",
            ErrorMessage = message ?? "Sea Power rejected the command.",
            ErrorId = errorId,
        };
    }

    public static GameCommandResult Completed(string resultJson)
    {
        return new GameCommandResult
        {
            Success = true,
            HttpStatusCode = StatusCodes.Status200OK,
            ResultJson = resultJson,
        };
    }
}
