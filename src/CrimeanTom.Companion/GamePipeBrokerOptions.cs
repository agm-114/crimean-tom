using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

public sealed class GamePipeBrokerOptions
{
    public string PipeName { get; init; } = ProtocolConstants.GamePipeName;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMilliseconds(
        ProtocolConstants.DefaultCommandTimeoutMilliseconds);

    public TimeSpan TransportWriteTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public int MaximumPendingCommands { get; init; } =
        ProtocolConstants.MaximumPendingCommands;
}
