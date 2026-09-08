using System.Text.RegularExpressions;

namespace CrimeanTom.SeaPower;

internal enum ScenarioSpecialMode { None, NoMissiles }

internal static class ScenarioSpecialModes
{
    private static readonly Dictionary<(string Type, string? Loadout), bool> MissilePlatforms = [];
    private static readonly Dictionary<string, bool> AntiShipMissiles = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Allows(this ScenarioSpecialMode mode, string unitType, string? loadout = null) =>
        mode != ScenarioSpecialMode.NoMissiles || !HasAntiShipMissiles(unitType, loadout);

    private static bool HasAntiShipMissiles(string unitType, string? loadout)
    {
        var key = (unitType, loadout);
        if (MissilePlatforms.TryGetValue(key, out bool found)) return found;
        string root = Path.Combine(UnityEngine.Application.streamingAssetsPath, "original");
        string? path = new[] { "vessels", "submarines", "land_units", "aircraft" }
            .Select(folder => Path.Combine(root, folder, unitType + ".ini")).FirstOrDefault(File.Exists);
        if (path is null) return MissilePlatforms[key] = true;
        var sections = ScenarioUnitCatalog.ReadSections(path).ToArray();
        var values = sections.SelectMany(section => section.Values).ToArray();
        var payload = loadout is null ? values : sections.Where(section =>
                section.Section.StartsWith("WeaponSystem", StringComparison.OrdinalIgnoreCase) &&
                section.Section.EndsWith(loadout, StringComparison.OrdinalIgnoreCase))
            .SelectMany(section => section.Values).ToArray();
        return MissilePlatforms[key] = payload.Where(pair => Regex.IsMatch(pair.Key,
                loadout is null ? @"^Ammunition\d*$" : @"^Station\d+$", RegexOptions.IgnoreCase))
            .SelectMany(pair => Resolve(pair.Value.Split('|')[0].Trim())).Any(IsAntiShipMissile);

        IEnumerable<string> Resolve(string reference) => reference.StartsWith("DateBased_", StringComparison.OrdinalIgnoreCase)
            ? (values.FirstOrDefault(pair => pair.Key.Equals(reference, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty)
                .Split('|').Select(entry => entry.Split(',').Last().Trim())
            : [reference];

        bool IsAntiShipMissile(string ammunition)
        {
            if (AntiShipMissiles.TryGetValue(ammunition, out bool missile)) return missile;
            string ammoPath = Path.Combine(root, "ammunition", ammunition + ".ini");
            if (!File.Exists(ammoPath)) return AntiShipMissiles[ammunition] = true;
            var general = ScenarioUnitCatalog.ReadSections(ammoPath)
                .FirstOrDefault(section => section.Section.Equals("General", StringComparison.OrdinalIgnoreCase)).Values;
            return AntiShipMissiles[ammunition] = general is null ||
                general.GetValueOrDefault("Type", "").Equals("Missile", StringComparison.OrdinalIgnoreCase) &&
                general.GetValueOrDefault("TargetType", "").Split(',')
                    .Any(target => target.Trim().Equals("ASuW", StringComparison.OrdinalIgnoreCase));
        }
    }
}
