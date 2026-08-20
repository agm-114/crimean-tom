namespace CrimeanTom.Protocol;

public sealed class WireEnvelope
{
    public int ProtocolVersion { get; set; } = ProtocolConstants.Version;

    public string Kind { get; set; } = string.Empty;

    public string MessageId { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public string? SessionId { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public long? DeadlineUnixMilliseconds { get; set; }

    public int Priority { get; set; }
}