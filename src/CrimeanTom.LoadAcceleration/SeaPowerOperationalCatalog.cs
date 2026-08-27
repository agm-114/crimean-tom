using System.Globalization;
using System.Text.RegularExpressions;

namespace CrimeanTom.LoadAcceleration;

public sealed record SeaPowerCatalogWeapon(
    string AmmunitionId,
    string TargetType,
    long MinimumRangeMeters,
    long MaximumRangeMeters,
    string OrdnanceType,
    int GuidanceType,
    int MidCourseCorrection,
    long SpeedMetersPerSecond,
    long SeekerRangeMeters,
    int LaunchReliabilityMilliUnits,
    int KillProbabilityMilliUnits,
    int WarheadPower,
    /// <summary>
    /// Rounds of this ammunition the unit and loadout carry, counted with Sea
    /// Power's own loadout rule. Zero means the game's rule declares no
    /// countable stock for this ammunition on this platform, which is not the
    /// same as an empty magazine: a gun or CIWS mount without an associated
    /// magazine carries rounds that the catalog never enumerates. Treat zero as
    /// "not magazine-limited by the catalog" rather than "out of ammunition".
    /// </summary>
    long MagazineRounds = 0);

public sealed class SeaPowerOperationalCatalog
{
    private static readonly string[] UnitFolders = ["vessels", "aircraft", "land_units"];
    private readonly string _originalRoot;
    private readonly object _sync = new();
    private readonly Dictionary<(string Unit, string Loadout), IReadOnlyList<SeaPowerCatalogWeapon>> _cache = new();
    private readonly Lazy<IniDocument> _weaponSystems;

    /// <summary>
    /// Shared weapon-system definitions. Launcher capacity lives here rather
    /// than in the unit file: a unit names a system and a container count, and
    /// this file says how many rounds one container of that system holds.
    /// </summary>
    private IniDocument WeaponSystems => _weaponSystems.Value;

    public SeaPowerOperationalCatalog(string originalStreamingAssetsRoot)
    {
        _originalRoot = Path.GetFullPath(originalStreamingAssetsRoot);
        if (!Directory.Exists(Path.Combine(_originalRoot, "ammunition")))
        {
            throw new DirectoryNotFoundException($"Sea Power original catalog was not found beneath {_originalRoot}.");
        }
        _weaponSystems = new Lazy<IniDocument>(() =>
            IniDocument.TryLoad(Path.Combine(_originalRoot, "systems", "weapons.ini"))
                ?? IniDocument.None);
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

        // Rounds are counted with Sea Power's own loadout rule
        // (SimpleObjectParameters.ReadAmmoFromWeaponSystem and
        // ReadAmmoFromHardpointSystem) so the server starts an engagement with
        // the same magazine contents the game would give the same unit and
        // loadout. Keys use the game's ammunition names.
        var rounds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var processedMagazines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                ReadHardpointStations(
                    unit, baseSection, loadoutSection, scenarioYear, ammunitionIds, rounds);
                continue;
            }

            AddResolvedAmmunition(ammunitionIds, unit.Value(baseSection, "Ammunition"), unit, scenarioYear);
            var magazine = unit.Value(baseSection, "AssociatedMagazine");
            if (magazine.Length > 0)
            {
                // Two mounts can share one magazine. Sea Power counts such a
                // magazine once, so a twin torpedo installation does not report
                // double its real stock.
                if (!processedMagazines.Add(magazine))
                {
                    continue;
                }
                var ammunitionCount = unit.Integer(magazine, "NumberOfAmmunitionTypes");
                for (var ammunitionIndex = 1; ammunitionIndex <= ammunitionCount; ammunitionIndex++)
                {
                    var declared = unit.Integer(magazine, $"Ammunition{ammunitionIndex}_Count");
                    var resolved = AddResolvedAmmunition(
                        ammunitionIds,
                        unit.Value(magazine, $"Ammunition{ammunitionIndex}"),
                        unit,
                        scenarioYear,
                        include: declared != 0);
                    AddRounds(rounds, resolved, declared);
                }
                continue;
            }

            // A gun or CIWS mount without a magazine has no catalog-countable
            // stock. Sea Power skips it here, so the server records no rounds
            // rather than inventing a number.
            if (string.Equals(type, "Gun", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "CIWS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var systemName = unit.Value(baseSection, "SystemName");
            var containers = unit.IntegerOrDefault(baseSection, "NumberOfContainers", 1);
            var attachments = WeaponSystems.IntegerOrDefault(systemName, "NumberOfAttachments", 1);
            AddRounds(
                rounds,
                ResolveAmmunitionId(unit.Value(baseSection, "Ammunition"), unit, scenarioYear),
                checked(containers * attachments));
        }

        var result = ammunitionIds
            .SelectMany(ResolveAmmunition)
            .GroupBy(item => (item.AmmunitionId, item.TargetType), new WeaponKeyComparer())
            .Select(group => group.OrderByDescending(item => item.MaximumRangeMeters).First())
            .Select(item => item with
            {
                MagazineRounds = rounds.TryGetValue(item.AmmunitionId, out var stock) ? stock : 0,
            })
            .OrderBy(item => item.AmmunitionId, StringComparer.Ordinal)
            .ThenBy(item => item.TargetType, StringComparer.Ordinal)
            .ToArray();
        lock (_sync)
        {
            return _cache[key] = result;
        }
    }

    /// <summary>
    /// Counts one hardpoint system's stations the way Sea Power's
    /// <c>ReadAmmoFromHardpointSystem</c> does. A bare station carries one
    /// round. A station naming an attachment group carries one round per
    /// position in that group, which is how a single sonobuoy station holds a
    /// full rack. A station carrying a container contributes the container's
    /// own contents instead of the container itself.
    /// </summary>
    private void ReadHardpointStations(
        IniDocument unit,
        string systemSection,
        string loadoutSection,
        int scenarioYear,
        ISet<string> ammunitionIds,
        IDictionary<string, long> rounds)
    {
        var stationCount = unit.Integer(systemSection, "NumberOfStations");
        for (var index = 1; index <= stationCount; index++)
        {
            var raw = unit.Value(loadoutSection, $"Station{index}");
            if (raw.Length == 0)
            {
                continue;
            }

            var parts = raw.Split('|');
            var resolved = AddResolvedAmmunition(ammunitionIds, raw, unit, scenarioYear);
            if (resolved.Length == 0)
            {
                continue;
            }

            var positions = 1L;
            if (parts.Length > 1)
            {
                var group = unit.Value(systemSection, parts[1].Trim() + "Positions");
                positions = group.Length == 0
                    ? 1L
                    : group.Split('|', StringSplitOptions.RemoveEmptyEntries).Length;
            }

            var ammunition = IniDocument.TryLoad(
                Path.Combine(_originalRoot, "ammunition", $"{resolved}.ini"));
            if (ammunition is null)
            {
                continue;
            }
            if (string.Equals(
                ammunition.Value("General", "Type"), "Container", StringComparison.OrdinalIgnoreCase))
            {
                if (!ammunition.HasSection("Ammunition"))
                {
                    continue;
                }
                AddRounds(
                    rounds,
                    ammunition.Value("Ammunition", "Ammunition"),
                    checked(ammunition.IntegerOrDefault("Ammunition", "NumberOfAttachments", 1) * positions));
                continue;
            }

            AddRounds(rounds, resolved, positions);
        }
    }

    /// <summary>
    /// Accumulates rounds the way Sea Power's <c>AddAmmo</c> does, including
    /// its rule that a count below one contributes nothing.
    /// </summary>
    private static void AddRounds(IDictionary<string, long> rounds, string ammunitionId, long count)
    {
        if (ammunitionId.Length == 0 || count < 1)
        {
            return;
        }
        rounds[ammunitionId] = rounds.TryGetValue(ammunitionId, out var existing)
            ? checked(existing + count)
            : count;
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
            yield return new SeaPowerCatalogWeapon(
                ammunitionId,
                target,
                MinimumRangeMeters(ammunition),
                maximumRangeMeters,
                ammunition.Value("General", "Type"),
                ammunition.Integer("Guidance", "GuidanceType"),
                ammunition.Integer("Guidance", "MidCourseCorrection"),
                SpeedMetersPerSecond(ammunition),
                SeekerRangeMeters(ammunition),
                ProbabilityMilliUnits(ammunition, "Guidance", "LaunchReliability", percentage: true, fallback: 1_000),
                ProbabilityMilliUnits(ammunition, "WarheadData", "KillProbability", percentage: false, fallback: 1_000),
                ammunition.Integer("WarheadData", "Power"));
        }
    }

    private static long MinimumRangeMeters(IniDocument ammunition)
    {
        var nauticalMiles = ammunition.Number("Guidance", "MinLaunchRange");
        return nauticalMiles <= 0
            ? 0
            : checked((long)Math.Round(nauticalMiles * 1_852d, MidpointRounding.AwayFromZero));
    }

    private static long SpeedMetersPerSecond(IniDocument ammunition)
    {
        var metersPerSecond = ammunition.Number("Physics", "MuzzleVelocity");
        if (metersPerSecond > 0)
        {
            return checked((long)Math.Round(metersPerSecond, MidpointRounding.AwayFromZero));
        }
        var knots = ammunition.Number("Guidance", "MaxVelocity");
        if (knots <= 0)
        {
            knots = ammunition.Numbers("Guidance", "SpeedSettings").DefaultIfEmpty().Max();
        }
        if (knots <= 0)
        {
            knots = ammunition.Number("Guidance", "TerminalVelocity");
        }
        return knots <= 0
            ? 0
            : checked((long)Math.Round(knots * 0.514444d, MidpointRounding.AwayFromZero));
    }

    private static long SeekerRangeMeters(IniDocument ammunition)
    {
        var active = ammunition.Number("Guidance", "SeekerActiveRange");
        var passive = ammunition.Number("Guidance", "SeekerPassiveRange");
        var nauticalMiles = Math.Max(active, passive);
        return nauticalMiles <= 0
            ? 0
            : checked((long)Math.Round(nauticalMiles * 1_852d, MidpointRounding.AwayFromZero));
    }

    private static int ProbabilityMilliUnits(
        IniDocument ammunition,
        string section,
        string key,
        bool percentage,
        int fallback)
    {
        var raw = ammunition.Value(section, key);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }
        var normalized = percentage ? value / 100d : value;
        return Math.Clamp(
            checked((int)Math.Round(normalized * 1_000d, MidpointRounding.AwayFromZero)),
            0,
            1_000);
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

    private string AddResolvedAmmunition(
        ISet<string> destination,
        string raw,
        IniDocument unit,
        int scenarioYear,
        bool include = true)
    {
        var resolved = ResolveAmmunitionId(raw, unit, scenarioYear);
        if (resolved.Length > 0 && include)
        {
            destination.Add(resolved);
        }
        return resolved;
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

        public static readonly IniDocument None = new();

        public static IniDocument? TryLoad(string path) =>
            File.Exists(path) ? Load(path) : null;

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

        public int IntegerOrDefault(string section, string key, int fallback) =>
            int.TryParse(Value(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

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
