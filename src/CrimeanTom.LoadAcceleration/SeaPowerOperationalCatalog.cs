using System.Globalization;
using System.Text.RegularExpressions;

namespace CrimeanTom.LoadAcceleration;

public sealed record SeaPowerCatalogWeapon(
    string AmmunitionId,
    string TargetType,
    long MaximumRangeMeters);

public sealed class SeaPowerOperationalCatalog
{
    private static readonly string[] UnitFolders = ["vessels", "aircraft", "land_units"];
    private readonly string _originalRoot;
    private readonly object _sync = new();
    private readonly Dictionary<(string Unit, string Loadout), IReadOnlyList<SeaPowerCatalogWeapon>> _cache = new();

    public SeaPowerOperationalCatalog(string originalStreamingAssetsRoot)
    {
        _originalRoot = Path.GetFullPath(originalStreamingAssetsRoot);
        if (!Directory.Exists(Path.Combine(_originalRoot, "ammunition")))
        {
            throw new DirectoryNotFoundException($"Sea Power original catalog was not found beneath {_originalRoot}.");
        }
    }

    public static SeaPowerOperationalCatalog? TryCreateFromEnvironment()
    {
        var gameDirectory = Environment.GetEnvironmentVariable("SEA_POWER_DIR");
        var candidates = new[]
        {
            gameDirectory,
            @"C:\Program Files (x86)\Steam\steamapps\common\Sea Power",
            @"C:\Program Files\Steam\steamapps\common\Sea Power",
        };
        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var original = Path.Combine(candidate!, "Sea Power_Data", "StreamingAssets", "original");
            if (Directory.Exists(Path.Combine(original, "ammunition")))
            {
                return new SeaPowerOperationalCatalog(original);
            }
        }
        return null;
    }

    public IReadOnlyList<SeaPowerCatalogWeapon> Resolve(
        string unitTypeId,
        string loadoutId = "Default",
        int scenarioYear = 1989)
    {
        var key = (unitTypeId.ToLowerInvariant(), loadoutId.ToLowerInvariant());
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var unitPath = UnitFolders
            .Select(folder => Path.Combine(_originalRoot, folder, $"{unitTypeId}.ini"))
            .FirstOrDefault(File.Exists);
        if (unitPath is null)
        {
            lock (_sync)
            {
                return _cache[key] = [];
            }
        }

        var unit = IniDocument.Load(unitPath);
        var ammunitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemCount = unit.Integer("WeaponSystems", "NumberOfWeaponSystems");
        for (var index = 1; index <= systemCount; index++)
        {
            var baseSection = $"WeaponSystem{index}";
            var type = unit.Value(baseSection, "Type");
            if (string.Equals(type, "Hardpoint", StringComparison.OrdinalIgnoreCase))
            {
                var loadoutSection = unit.HasSection(baseSection + loadoutId)
                    ? baseSection + loadoutId
                    : baseSection + "Default";
                foreach (var station in unit.Section(loadoutSection)
                    .Where(item => Regex.IsMatch(item.Key, "^Station[0-9]+$", RegexOptions.IgnoreCase)))
                {
                    AddResolvedAmmunition(ammunitionIds, station.Value, unit, scenarioYear);
                }
                continue;
            }

            AddResolvedAmmunition(ammunitionIds, unit.Value(baseSection, "Ammunition"), unit, scenarioYear);
            var magazine = unit.Value(baseSection, "AssociatedMagazine");
            var ammunitionCount = unit.Integer(magazine, "NumberOfAmmunitionTypes");
            for (var ammunitionIndex = 1; ammunitionIndex <= ammunitionCount; ammunitionIndex++)
            {
                if (unit.Integer(magazine, $"Ammunition{ammunitionIndex}_Count") != 0)
                {
                    AddResolvedAmmunition(
                        ammunitionIds,
                        unit.Value(magazine, $"Ammunition{ammunitionIndex}"),
                        unit,
                        scenarioYear);
                }
            }
        }

        var result = ammunitionIds
            .SelectMany(ResolveAmmunition)
            .GroupBy(item => (item.AmmunitionId, item.TargetType), new WeaponKeyComparer())
            .Select(group => group.OrderByDescending(item => item.MaximumRangeMeters).First())
            .OrderBy(item => item.AmmunitionId, StringComparer.Ordinal)
            .ThenBy(item => item.TargetType, StringComparer.Ordinal)
            .ToArray();
        lock (_sync)
        {
            return _cache[key] = result;
        }
    }

    private IEnumerable<SeaPowerCatalogWeapon> ResolveAmmunition(string ammunitionId)
    {
        var path = Path.Combine(_originalRoot, "ammunition", $"{ammunitionId}.ini");
        if (!File.Exists(path))
        {
            yield break;
        }
        var ammunition = IniDocument.Load(path);
        if (string.Equals(ammunition.Value("General", "Type"), "Container", StringComparison.OrdinalIgnoreCase))
        {
            var contained = ResolveAmmunitionId(ammunition.Value("Ammunition", "Ammunition"));
            if (contained.Length == 0)
            {
                yield break;
            }
            foreach (var item in ResolveAmmunition(contained))
            {
                yield return item;
            }
            yield break;
        }

        var maximumRangeMeters = RangeMeters(ammunition);
        if (maximumRangeMeters <= 0)
        {
            yield break;
        }
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTarget(targets, ammunition.Value("General", "TargetType"));
        AddTarget(targets, ammunition.Value("General", "SecondaryTargetType"));
        if (!string.IsNullOrWhiteSpace(ammunition.Value("General", "LandAttackCapability")))
        {
            targets.Add("LandFormation");
        }
        foreach (var target in targets.OrderBy(value => value, StringComparer.Ordinal))
        {
            yield return new SeaPowerCatalogWeapon(ammunitionId, target, maximumRangeMeters);
        }
    }

    private static long RangeMeters(IniDocument ammunition)
    {
        var maximumLaunchRange = ammunition.Number("Guidance", "MaxLaunchRange");
        if (maximumLaunchRange > 0)
        {
            return checked((long)Math.Round(maximumLaunchRange * 1_852d, MidpointRounding.AwayFromZero));
        }
        var projectileRange = ammunition.Number("Physics", "MaxRange");
        if (projectileRange > 0)
        {
            return checked((long)Math.Round(projectileRange, MidpointRounding.AwayFromZero));
        }
        var torpedoRange = ammunition.Numbers("Guidance", "SpeedSettingRanges").DefaultIfEmpty().Max();
        if (torpedoRange > 0)
        {
            return checked((long)Math.Round(torpedoRange * 1_852d, MidpointRounding.AwayFromZero));
        }
        return 0;
    }

    private static void AddTarget(ISet<string> targets, string raw)
    {
        var target = raw.Trim().ToUpperInvariant() switch
        {
            "AAW" => "Air",
            "ASUW" => "Surface",
            "ASW" => "Subsurface",
            _ => string.Empty,
        };
        if (target.Length > 0)
        {
            targets.Add(target);
        }
    }

    private void AddResolvedAmmunition(
        ISet<string> destination,
        string raw,
        IniDocument unit,
        int scenarioYear)
    {
        var resolved = ResolveAmmunitionId(raw, unit, scenarioYear);
        if (resolved.Length > 0)
        {
            destination.Add(resolved);
        }
    }

    private string ResolveAmmunitionId(
        string raw,
        IniDocument? unit = null,
        int scenarioYear = 1989)
    {
        var attachmentFree = raw.Split('|')[0].Trim();
        if (unit is not null && attachmentFree.StartsWith("DateBased_", StringComparison.OrdinalIgnoreCase))
        {
            attachmentFree = ResolveDateBasedAmmunition(
                unit.Value("WeaponSystems", attachmentFree),
                scenarioYear);
        }
        if (File.Exists(Path.Combine(_originalRoot, "ammunition", $"{attachmentFree}.ini")))
        {
            return attachmentFree;
        }
        return Regex.Matches(attachmentFree, "[A-Za-z][A-Za-z0-9_.-]+")
            .Select(match => match.Value)
            .FirstOrDefault(candidate => File.Exists(Path.Combine(_originalRoot, "ammunition", $"{candidate}.ini")))
            ?? string.Empty;
    }

    private static string ResolveDateBasedAmmunition(string raw, int scenarioYear)
    {
        var selected = string.Empty;
        foreach (var rawEntry in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = rawEntry.Trim();
            var parts = entry.Split(new[] { ',' }, 2, StringSplitOptions.None)
                .Select(part => part.Trim())
                .ToArray();
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var effectiveYear)
                && effectiveYear <= scenarioYear)
            {
                selected = parts[1];
            }
        }
        return selected;
    }

    private sealed class WeaponKeyComparer : IEqualityComparer<(string AmmunitionId, string TargetType)>
    {
        public bool Equals((string AmmunitionId, string TargetType) left, (string AmmunitionId, string TargetType) right) =>
            string.Equals(left.AmmunitionId, right.AmmunitionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.TargetType, right.TargetType, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string AmmunitionId, string TargetType) value) =>
            HashCode.Combine(value.AmmunitionId.ToUpperInvariant(), value.TargetType.ToUpperInvariant());
    }

    private sealed class IniDocument
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections =
            new(StringComparer.OrdinalIgnoreCase);

        public static IniDocument Load(string path)
        {
            var result = new IniDocument();
            Dictionary<string, string>? section = null;
            foreach (var sourceLine in File.ReadLines(path))
            {
                var line = sourceLine.Trim();
                if (line.Length == 0 || line[0] is '#' or ';')
                {
                    continue;
                }
                if (line.StartsWith('[') && line.IndexOf(']') is var sectionEnd && sectionEnd > 1)
                {
                    var name = line[1..sectionEnd].Trim();
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result._sections[name] = section;
                    continue;
                }
                var separator = line.IndexOf('=');
                if (section is null || separator <= 0)
                {
                    continue;
                }
                var value = line[(separator + 1)..];
                var comment = value.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    value = value[..comment];
                }
                section[line[..separator].Trim()] = value.Trim();
            }
            return result;
        }

        public bool HasSection(string name) => _sections.ContainsKey(name);

        public IReadOnlyDictionary<string, string> Section(string name) =>
            _sections.TryGetValue(name, out var section) ? section : Empty;

        public string Value(string section, string key) =>
            _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value)
                ? value : string.Empty;

        public int Integer(string section, string key) =>
            int.TryParse(Value(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : 0;

        public double Number(string section, string key) =>
            double.TryParse(Value(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value : 0d;

        public IEnumerable<double> Numbers(string section, string key) =>
            Value(section, key)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : 0d);

        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
