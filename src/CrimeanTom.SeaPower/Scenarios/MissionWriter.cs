using System.Globalization;
using System.Text;

namespace CrimeanTom.SeaPower;

// In-memory output only. Existing mission loading and editing still use Sea Power's IniHandler.
internal class MissionWriter
{
    private readonly Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
    private string current = string.Empty;

    internal void Section(string name)
    {
        current = name;
        if (!sections.ContainsKey(name)) sections[name] = new(StringComparer.OrdinalIgnoreCase);
    }

    internal void Write(string key, object? value, string? format = null)
    {
        string formatted = Format(value, format).Replace("\n", "\\n").Replace("\r", "\\r");
        sections[current][key] = formatted;
    }

    internal void Write(string key, FormattableString value) =>
        Write(key, value.ToString(CultureInfo.InvariantCulture));

    internal void WriteList(string key, IEnumerable<string> values) =>
        Write(key, string.Join(',', values));

    internal void WritePosition(string key, MissionPosition position) =>
        Write(key, position.ToMissionValue());

    internal void WriteWaypoints(IEnumerable<MissionWaypoint> waypoints, bool loop = false) =>
        Write("Waypoints", MissionWaypoint.FormatRoute(waypoints) + (loop ? "|Loop" : string.Empty));

    internal string Read(string section, string key, string fallback = "") =>
        sections.TryGetValue(section, out var values) && values.TryGetValue(key, out string value) ? value : fallback;

    internal void Set(string section, string key, object value)
    {
        string previous = current;
        Section(section);
        Write(key, value);
        current = previous;
    }

    public override string ToString()
    {
        StringBuilder text = new();
        foreach (var section in sections)
        {
            if (text.Length > 0) text.AppendLine();
            text.Append('[').Append(section.Key).AppendLine("]");
            section.Value.ForEach(pair => text.Append(pair.Key).Append('=').AppendLine(pair.Value));
        }
        return text.ToString();
    }

    private static string Format(object? value, string? format) => value switch
    {
        IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? string.Empty,
    };
}
