using SeaPower;
using UnityEngine;

namespace Amphibs;

internal class AmphibiousProfile(string id, string source, string name, float speed, bool weaponsAfloat,
    float waterlineOffset, float length, float wakeScale)
{
    internal string Id { get; } = id;
    internal string Source { get; } = source;
    internal string Name { get; } = name;
    internal float Speed { get; } = speed;
    internal bool WeaponsAfloat { get; } = weaponsAfloat;
    internal float WaterlineOffset => Value("WaterlineOffsetMeters");
    internal float Length { get; } = length;
    internal float WakeScale => Value("WakeScale");
    internal float Value(string key) => WaterEffectSettings.IsPerUnit(key) ? Effects[key] : WaterEffectSettings.Shared[key];
    internal void Set(string key, float value)
    {
        if (WaterEffectSettings.IsPerUnit(key)) Effects[key] = value;
        else WaterEffectSettings.Shared[key] = value;
    }
    internal Dictionary<string, float> Effects { get; } = new()
    {
        ["WaterlineOffsetMeters"] = waterlineOffset, ["WakeScale"] = wakeScale,
    };
}

internal static class AmphibiousCatalog
{
    internal static bool ShowTuningPanel { get; private set; }
    internal static readonly Dictionary<string, AmphibiousProfile> Profiles = new(StringComparer.OrdinalIgnoreCase);
    internal static bool Load()
    {
        try
        {
            Profiles.Clear();
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "amphibious-variants.ini");
            var ini = Utils.openIniFile(path, logErrors: false, useCaching: false)
                ?? throw new IOException($"Missing {path}");
            string overridesPath = Path.Combine(Path.GetDirectoryName(path)!, "water-effects.ini");
            var overrides = File.Exists(overridesPath) ? Utils.openIniFile(overridesPath, logErrors: false, useCaching: false) : null;
            ShowTuningPanel = overrides?.readValue("WakeCoefficients", "ShowTuningPanel", false)
                ?? ini.readValue("WakeCoefficients", "ShowTuningPanel", false);
            WaterEffectSettings.Shared.Clear();
            foreach (var setting in WaterEffectSettings.All.Where(setting => !WaterEffectSettings.IsPerUnit(setting.Key)))
            {
                float value = ini.readValue("WakeCoefficients", setting.Key, setting.Default);
                value = overrides?.readValue("WakeCoefficients", setting.Key, value) ?? value;
                if (!float.IsFinite(value) || value < setting.Min || value > setting.Max)
                    throw new FormatException($"Invalid shared {setting.Key}");
                WaterEffectSettings.Shared[setting.Key] = value;
            }
            foreach (string id in ini.getNamesOfAllSections())
            {
                if (id == "WakeCoefficients") continue;
                string status = ini.readValue(id, "Status", "");
                if (status == "Conditional") continue;
                string source = ini.readValue(id, "SourceUnit", "");
                float speed = ini.readValue(id, "WaterSpeedKnots", 0f);
                float offset = ini.readValue(id, "WaterlineOffsetMeters", 0f);
                float length = ini.readValue(id, "LengthMeters", 0f);
                float wakeScale = ini.readValue(id, "WakeScale", 0.3f);
                if (status != "Candidate" || !id.StartsWith("amphibs_", StringComparison.Ordinal)
                    || !source.StartsWith("land_units/", StringComparison.Ordinal)
                    || source.Count(c => c == '/') != 1 || source.Contains("..") || source.Contains('\\')
                    || id.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-')
                    || !float.IsFinite(speed) || speed <= 0 || speed > 20
                    || !float.IsFinite(offset) || Math.Abs(offset) > 5
                    || !float.IsFinite(length) || length <= 0 || length > 30
                    || !float.IsFinite(wakeScale) || wakeScale < 0 || wakeScale > 2)
                    throw new FormatException($"Invalid amphibious profile {id}");
                Profiles.Add(id, new(id, source.Substring("land_units/".Length),
                    ini.readValue(id, "DisplayName", id), speed, ini.readValue(id, "WeaponsAfloat", false),
                    offset, length, wakeScale));
                foreach (var setting in WaterEffectSettings.All)
                {
                    if (!WaterEffectSettings.IsPerUnit(setting.Key)) continue;
                    float initial = Profiles[id].Effects.TryGetValue(setting.Key, out float current) ? current : setting.Default;
                    if (setting.Key == "MaxEffectSpeed") initial = speed;
                    float value = ini.readValue(id, setting.Key, initial);
                    value = overrides?.readValue(id, setting.Key, value) ?? value;
                    if (!float.IsFinite(value) || value < setting.Min || value > setting.Max)
                        throw new FormatException($"Invalid {setting.Key} in {id}");
                    Profiles[id].Effects[setting.Key] = value;
                }
            }
            return Profiles.Count > 0;
        }
        catch (Exception error)
        {
            Profiles.Clear();
            Debug.LogError($"[Amphibs] Configuration rejected: {error.Message}");
            return false;
        }
    }

    internal static bool TryGet(ObjectBase unit, out AmphibiousProfile profile) =>
        Profiles.TryGetValue(unit?._obp?._objectIniName ?? "", out profile);
}
