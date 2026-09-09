using System.Globalization;

namespace FlightDeckTweaks.Preparation;

internal class ServiceRates(string path)
{
    private readonly Dictionary<string, Dictionary<string, double>> tables = new(StringComparer.OrdinalIgnoreCase);

    internal void Read()
    {
        if (!File.Exists(path)) return;
        string section = "Defaults";
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Split(';')[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            int equals = line.IndexOf('=');
            if (equals < 1) continue;
            if (!double.TryParse(line[(equals + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 86400)
                throw new FormatException($"Invalid preparation rate: {section}/{line[..equals]}");
            Table(section)[line[..equals].Trim()] = value;
        }
        if (Get("Defaults", "UnloadMultiplier", 0.35) >= 1)
            throw new FormatException("UnloadMultiplier must be between zero and one");
    }

    internal double Get(string section, string key, double fallback)
    {
        var table = Table(section);
        if (!table.TryGetValue(key, out double value)) table[key] = value = fallback;
        return value;
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[] { "; Seconds of crew work before the game's flight-deck timing multiplier.",
            "; Weapon rates are per round/item. Aircraft refuel rates are empty-to-full internal fuel.",
            "; Generated starting values are gameplay tuning, not measured servicing times." }
            .Concat(tables.SelectMany(table => new[] { "", $"[{table.Key}]" }.Concat(table.Value.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}={pair.Value.ToString("R", CultureInfo.InvariantCulture)}")))));
    }

    private Dictionary<string, double> Table(string section)
    {
        if (!tables.TryGetValue(section, out var table)) tables[section] = table = new(StringComparer.OrdinalIgnoreCase);
        return table;
    }
}
