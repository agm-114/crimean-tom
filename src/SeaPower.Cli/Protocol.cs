using System.Text.Json;

namespace SeaPower.Cli;

internal record UiRequest(string Action)
{
    public string Session { get; init; } = "";
    public string Request { get; init; } = "";
    public DateTime Expires { get; init; }
    public string Snapshot { get; init; } = "";
    public int Id { get; init; }
    public int Index { get; init; } = -1;
    public string Text { get; init; } = "";
    public bool Confirm { get; init; }
    public float Speed { get; init; } = 1;
}

// Only fields used by the CLI are decoded; output retains the complete game reply.
internal record UiReply(TimeStatus? Time, UiControl[]? Controls);
internal record UiControl(int Id, int Parent, int Order, string Type, string Name, string Text,
    bool Enabled, int? Selected, bool? Expanded, UiOption[]? Options)
{
    internal bool Matches(string value) => (Text?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
        || string.Equals(Name, value, StringComparison.OrdinalIgnoreCase);
}
internal record UiOption(int Index, string Text);
internal record UnitState(int Id, string Name, string Type, string UnitClass, string Variant, string Nation,
    string? Taskforce, string? Side, bool PlayerControlled, bool Destroyed, UnitPosition? Position,
    double? HeadingDegrees, double? SpeedKnots, string? Loadout, double? FuelKg);
internal record UnitPosition(double? Latitude, double? Longitude, double? AltitudeMeters);
internal record TimeStatus(bool Available, bool Blocked, bool Paused, float Speed, double? ElapsedSeconds, double? MissionStartSeconds)
{
    internal double RequireElapsed()
    {
        if (!Available || ElapsedSeconds == null || MissionStartSeconds == null)
            throw new InvalidOperationException("An active mission and a helper build with mission clock support are required.");
        return ElapsedSeconds.Value;
    }
}

internal static class WireJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static UiReply Read(JsonElement reply) => reply.Deserialize<UiReply>(Options)
        ?? throw new InvalidDataException("Empty helper reply.");
}
