using System.Globalization;
using System.Text;

namespace CrimeanTom.SeaPower;

// In-memory output only. Existing mission loading and editing still use Sea Power's IniHandler.
internal class MissionWriter
{
    private readonly StringBuilder text = new();

    internal void Section(string name)
    {
        if (text.Length > 0) text.AppendLine();
        text.Append('[').Append(name).AppendLine("]");
    }

    internal void Write(string key, object? value, string? format = null)
    {
        string formatted = Format(value, format).Replace("\n", "\\n").Replace("\r", "\\r");
        text.Append(key).Append('=').AppendLine(formatted);
    }

    internal void Write(string key, FormattableString value) =>
        Write(key, value.ToString(CultureInfo.InvariantCulture));

    internal void WriteList(string key, IEnumerable<string> values) =>
        Write(key, string.Join(',', values));

    internal void WritePosition(string key, MissionPosition position) =>
        Write(key, position.ToMissionValue());

    internal void WriteWaypoints(IEnumerable<MissionWaypoint> waypoints, bool loop = false) =>
        Write("Waypoints", MissionWaypoint.FormatRoute(waypoints) + (loop ? "|Loop" : string.Empty));

    public override string ToString() => text.ToString();

    private static string Format(object? value, string? format) => value switch
    {
        IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? string.Empty,
    };
}