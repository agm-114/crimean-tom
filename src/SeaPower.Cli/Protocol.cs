using System.Text.Json;

namespace SeaPower.Cli;

internal record GameRequest(string Action)
{
    public string Session { get; init; } = "";
    public string Request { get; init; } = "";
    public DateTime Expires { get; init; }
    public string Snapshot { get; init; } = "";
    public int Id { get; init; }
    public int Depth { get; init; } = 8;
    public int Index { get; init; } = -1;
    public string Text { get; init; } = "";
    public bool Confirm { get; init; }
    public string ModDirectory { get; init; } = "";
    public string ChangeLog { get; init; } = "";
    public string Visibility { get; init; } = "";
    public ulong[] Dependencies { get; init; } = [];
    public string[] Tags { get; init; } = [];
    public float Speed { get; init; } = 1;
    public float Value { get; init; }
}

// Only fields used by the CLI are decoded; output retains the complete game reply.
internal record GameReply(TimeStatus? Time, UiControl[]? Controls);
internal record UiControl(int Id, int Parent, int Order, string Type, string Name, string Text,
    bool Enabled, int? Selected, bool? Expanded, UiOption[]? Options, bool? IsChecked = null)
{
    internal bool Matches(string value) => (Text?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
        || string.Equals(Name, value, StringComparison.OrdinalIgnoreCase);
}
internal record UiOption(int Index, string Text);
internal record UnitState(int Id, string Name, string Type, string UnitClass, string Variant, string Nation,
    string? Taskforce, string? Side, bool PlayerControlled, bool Destroyed, UnitPosition? Position,
    double? HeadingDegrees, double? SpeedKnots, string? Loadout, double? FuelKg,
    string? Order, string? FlightDeckState, string? Parent, UnitVector? WorldPosition,
    double? DemandedSpeedKnots, bool? RotorsOn, bool? Landing);
internal record UnitVector(double X, double Y, double Z);
internal record UnitPosition(double? Latitude, double? Longitude, double? AltitudeMeters);
internal record TimeStatus(bool Available, bool Blocked, bool Paused, float Speed, double? ElapsedSeconds, double? MissionStartSeconds)
{
    internal double RequireElapsed()
    {
        if (!Available || ElapsedSeconds == null || MissionStartSeconds == null)
            throw new InvalidOperationException("An active mission and a bridge build with mission clock support are required.");
        return ElapsedSeconds.Value;
    }
}

internal static class WireJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 256 };
    internal static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };
    internal static GameReply Read(JsonElement reply) => reply.Deserialize<GameReply>(Options)
        ?? throw new InvalidDataException("Empty bridge reply.");
}
