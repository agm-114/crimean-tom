using System.Globalization;

namespace SeaPower.SharedSettings;

internal class SettingsValues
{
    private readonly Dictionary<string, Dictionary<string, double>> sections = new(StringComparer.OrdinalIgnoreCase);

    internal double Get(string section, string key, double fallback = 0) =>
        sections.TryGetValue(section, out var values) && values.TryGetValue(key, out double value) ? value : fallback;

    internal void Set(string section, string key, double value)
    {
        if (!sections.TryGetValue(section, out var values)) sections.Add(section, values = new(StringComparer.OrdinalIgnoreCase));
        values[key] = value;
    }

    internal IEnumerable<KeyValuePair<string, double>> Entries(string section) =>
        sections.TryGetValue(section, out var values) ? values : [];

    internal IEnumerable<string> SectionNames => sections.Keys;

    internal bool Remove(string section, string key) => sections.TryGetValue(section, out var values) && values.Remove(key);

    internal SettingsValues Copy()
    {
        var copy = new SettingsValues();
        copy.Overlay(this, replaceSections: true);
        return copy;
    }

    internal void Overlay(SettingsValues overrides, bool replaceSections = false)
    {
        foreach (var section in overrides.sections)
        {
            if (replaceSections) sections[section.Key] = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in section.Value) Set(section.Key, entry.Key, entry.Value);
        }
    }

    internal IEnumerable<string> Lines() => sections.SelectMany(section =>
        new[] { $"[{section.Key}]" }.Concat(section.Value.Select(entry =>
            $"{entry.Key}={entry.Value.ToString("R", CultureInfo.InvariantCulture)}")).Append(""));

    internal static SettingsValues Read(TextReader reader)
    {
        var settings = new SettingsValues();
        string section = "";
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Split(';')[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Length > 0 && !settings.sections.ContainsKey(section))
                    settings.sections.Add(section, new(StringComparer.OrdinalIgnoreCase));
            }
            else if (section.Length > 0 && TryReadEntry(line, out string key, out double value))
                settings.Set(section, key, value);
        }
        return settings;
    }

    internal static bool TryReadEntry(string line, out string key, out double value)
    {
        int equals = line.IndexOf('=');
        key = equals > 0 ? line[..equals].Trim() : "";
        value = 0;
        if (key.Length == 0 || key.IndexOfAny(['[', ']', ';']) >= 0) return false;
        string text = line[(equals + 1)..].Trim();
        if (bool.TryParse(text, out bool enabled)) { value = enabled ? 1 : 0; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}
