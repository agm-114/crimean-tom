using System.Globalization;
using System.Text.RegularExpressions;

namespace Amphibs;

internal class WaterEffectSetting(string key, string label, float initial, float min, float max)
{
    internal string Key { get; } = key;
    internal string Label { get; } = label;
    internal float Default { get; } = initial;
    internal float Min { get; } = min;
    internal float Max { get; } = max;
    internal bool IsToggle => Key.EndsWith("Enabled", StringComparison.Ordinal);
    internal string Group => Key switch
    {
        "WaterlineOffsetMeters" or "BoxScaleX" or "BoxScaleY" or "BoxScaleZ" => "Vehicle box",
        "WakeScale" or "SprayScale" or "SpraySpeed" or "MinEffectSpeed" or "MaxEffectSpeed" => "General",
        "WakeWidth" or "WakeLength" or "WakePosition" => "Surface footprint",
        "DisplacementEnabled" or "WaveHeightCoefficient" => "Displacement",
        "RipplesEnabled" or "NormalStrength" => "Ripples",
        "SurfaceFoamEnabled" or "SurfaceFoam" => "Surface foam",
        "BowPosition" => "Bow spray",
        "SternPosition" => "Propwash",
        _ when Key.StartsWith("HullFoam", StringComparison.Ordinal) => "Hull foam trail",
        _ when Key.StartsWith("SternFoam", StringComparison.Ordinal) => "Stern foam trail",
        _ when Key.StartsWith("BowSpray", StringComparison.Ordinal) => "Bow spray",
        _ when Key.StartsWith("Propwash", StringComparison.Ordinal) => "Propwash",
        _ => "Legacy",
    };
}

internal static class WaterEffectSettings
{
    internal static readonly Dictionary<string, float> Shared = new();
    internal static bool IsPerUnit(string key) => key == "WaterlineOffsetMeters" || key.StartsWith("BoxScale", StringComparison.Ordinal);
    internal static readonly WaterEffectSetting[] All =
    [
        new("DisplacementEnabled", "Water displacement", 1, 0, 1),
        new("RipplesEnabled", "Water ripples", 1, 0, 1),
        new("SurfaceFoamEnabled", "Surface / bow foam", 0, 0, 1),
        new("HullFoamEnabled", "Hull foam trail", 0, 0, 1),
        new("SternFoamEnabled", "Stern foam trail", 0, 0, 1),
        new("BowSprayEnabled", "Bow spray", 1, 0, 1),
        new("PropwashEnabled", "Propwash spray", 1, 0, 1),
        new("WaterlineOffsetMeters", "Waterline offset (m)", 0, -5, 1),
        new("BoxScaleX", "Box width multiplier", 1, 0.1f, 3),
        new("BoxScaleY", "Box height multiplier", 1, 0.1f, 3),
        new("BoxScaleZ", "Box length multiplier", 1, 0.1f, 3),
        new("WakeScale", "Overall wake scale", 1, 0, 2),
        new("BowPosition", "Bow position / half-length (1 = bow)", 1f, 0.5f, 2),
        new("SternPosition", "Stern position (hull lengths)", -0.45f, -1, 0),
        new("SprayScale", "Spray size", 1.1f, 0, 2),
        new("SpraySpeed", "Spray velocity multiplier", 0.4f, 0, 2),
        new("BowSpray", "Bow spray multiplier", 1, 0, 2),
        new("Propwash", "Propwash multiplier", 1, 0, 2),
        new("HullFoam", "Hull foam opacity", 0.8f, 0, 2),
        new("WakeWidth", "Surface width / box width", 6f, 0.1f, 8),
        new("WakeLength", "Surface length (hull lengths)", 3.9f, 0.1f, 12),
        new("WakePosition", "Surface position (hull lengths)", -0.35f, -4, 2),
        new("WaveHeightCoefficient", "Wave height / box height", 0.05f, 0, 0.5f),
        new("NormalStrength", "Water ripple strength", 3, 0, 10),
        new("SurfaceFoam", "Surface foam opacity", 1, 0, 2),
        new("MinEffectSpeed", "Minimum effect speed / swim speed", 0, 0, 1),
        new("MaxEffectSpeed", "Full effect speed / swim speed", 1, 0.1f, 3),
        new("BowSprayY", "Vertical offset / box height", 0.02f, -3, 3),
        new("BowSpraySpeed", "Particle velocity multiplier", 1, 0, 3),
        new("PropwashY", "Vertical offset / box height", 0.02f, -3, 3),
        new("PropwashSpeed", "Particle velocity multiplier", 1, 0, 3),
        .. Trail("HullFoam", 0.55f),
        .. Trail("SternFoam", -0.45f),
    ];

    private static IEnumerable<WaterEffectSetting> Trail(string prefix, float position)
    {
        yield return new(prefix + "Position", "Forward offset (hull lengths)", position, -2, 2);
        yield return new(prefix + "Y", "Vertical offset / box height", 0, -3, 3);
        yield return new(prefix + "Size", "Size / box width", 3f, 0.1f, 4);
        yield return new(prefix + "Duration", "Lifetime (seconds)", 18, 1, 60);
        yield return new(prefix + "Spacing", "Spacing / box length", 0.85f, 0.1f, 4);
        yield return new(prefix + "Expansion", "Expansion / box width per second", 0.05f, 0, 1);
        yield return new(prefix + "Momentum", "Momentum / box length per second", 0, -2, 2);
        yield return new(prefix + "Spin", "Spin (degrees/s)", 0, -90, 90);
        yield return new(prefix + "Jitter", "Jitter", 0.1f, 0, 1);
        if (prefix == "SternFoam") yield return new(prefix + "Opacity", "Opacity", 1.2f, 0, 2);
    }

    internal static void Save(AmphibiousProfile profile)
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "water-effects.ini");
        string original = File.Exists(path) ? File.ReadAllText(path) : "; Saved Amphibs water-effect overrides. Builds preserve this file.\r\n";
        foreach (string sectionName in new[] { "WakeCoefficients", profile.Id })
        {
        string pattern = @"(?ms)^\[" + Regex.Escape(sectionName) + @"\][^\r\n]*\r?\n.*?(?=^\[|\z)";
        if (!Regex.IsMatch(original, pattern)) original = original.TrimEnd() + "\r\n\r\n[" + sectionName + "]\r\n";
        original = Regex.Replace(original, pattern, match =>
        {
            string section = match.Value.TrimEnd();
            foreach (var setting in All)
            {
                if (IsPerUnit(setting.Key) != (sectionName == profile.Id)) continue;
                string line = setting.Key + "=" + profile.Value(setting.Key).ToString("0.###", CultureInfo.InvariantCulture);
                string key = @"(?m)^" + Regex.Escape(setting.Key) + @"=[^\r\n]*";
                section = Regex.IsMatch(section, key) ? Regex.Replace(section, key, line) : section + "\r\n" + line;
            }
            return section + "\r\n\r\n";
        });
        }
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, original);
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }
}
