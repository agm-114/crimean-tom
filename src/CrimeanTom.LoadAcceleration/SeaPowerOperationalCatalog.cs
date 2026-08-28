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
    long MagazineRounds = 0,
    /// <summary>
    /// The mounts that can employ this ammunition, in the order the unit
    /// declares them. Each cycles on its own, so they are kept separate rather
    /// than collapsed into a count.
    /// </summary>
    IReadOnlyList<SeaPowerCatalogMount>? Mounts = null)
{
    public IReadOnlyList<SeaPowerCatalogMount> Mounts { get; init; } =
        Mounts ?? Array.Empty<SeaPowerCatalogMount>();
}

/// <summary>
/// One weapon mount, identified by the section the unit declares it in.
///
/// Sea Power reloads each mount independently, so two torpedo tubes sharing a
/// magazine can each launch and then cycle out of phase with one another. The
/// catalog therefore reports mounts individually instead of reporting how many
/// there are: a count cannot say which mount is still reloading.
/// </summary>
public sealed record SeaPowerCatalogMount(
    /// <summary>
    /// The unit's own weapon-system section name, such as
    /// <c>WeaponSystem3</c>. Unique within a unit and stable across loads,
    /// which is what lets readiness be tracked per mount.
    /// </summary>
    string MountId,
    /// <summary>
    /// Seconds this mount needs before it can fire again, from its
    /// <c>ReloadTime</c>. Zero means the catalog declares none, so the server
    /// does not gate re-engagement on reloading.
    /// </summary>
    long ReloadSeconds = 0,
    /// <summary>
    /// Rounds one engagement consumes: <c>SalvoFireAmount</c> for launchers, or
    /// one second of sustained fire at the mount's <c>FireRate</c> for guns and
    /// CIWS. Zero means the catalog declares neither and the server falls back
    /// to a single round.
    /// </summary>
    int SalvoRounds = 0,
    /// <summary>
    /// Missiles this mount's own fire-control sensors can guide at once, summed
    /// from each associated sensor's <c>WeaponChannels</c>. Zero means the
    /// catalog declares no guidance channel, as for a mount with no associated
    /// fire-control sensor.
    /// </summary>
    int GuidanceChannels = 0);

/// <summary>
/// One radar a unit carries, resolved through the unit's own sensor section
/// into the shared sensor definitions.
///
/// Only radars are reported. Sea Power gives every radar a declared
/// <c>MaxRange</c>, but a sonar's range emerges from an acoustic propagation
/// model whose declared <c>ActiveRange</c> is a nominal bound rather than a
/// detection distance, and a visual sensor carries a multiplier rather than a
/// range at all. Reporting those from this reader would put a number on them
/// that the catalog does not actually make.
/// </summary>
public sealed record SeaPowerCatalogSensor(
    /// <summary>
    /// The unit's own sensor-system section name, such as
    /// <c>SensorSystem2</c>. Unique within a unit.
    /// </summary>
    string SensorId,
    /// <summary>Shared sensor definition the unit's section names.</summary>
    string SystemName,
    long MinimumRangeMeters,
    long MaximumRangeMeters,
    bool DetectsAir,
    bool DetectsSurface,
    bool DetectsLand,
    int TargetChannels,
    bool HasDataLink);

public sealed class SeaPowerOperationalCatalog
{
    private static readonly string[] UnitFolders = ["vessels", "aircraft", "land_units"];
    private readonly string _originalRoot;
    private readonly object _sync = new();
    private readonly Dictionary<(string Unit, string Loadout), IReadOnlyList<SeaPowerCatalogWeapon>> _cache = new();
    private readonly Dictionary<string, IReadOnlyList<SeaPowerCatalogSensor>> _sensorCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<IniDocument> _weaponSystems;
    private readonly Lazy<IniDocument> _sensors;

    /// <summary>
    /// Shared weapon-system definitions. Launcher capacity lives here rather
    /// than in the unit file: a unit names a system and a container count, and
    /// this file says how many rounds one container of that system holds.
    /// </summary>
    private IniDocument WeaponSystems => _weaponSystems.Value;

    /// <summary>
    /// Shared sensor definitions. A mount's simultaneous guidance capacity
    /// lives here, on the fire-control sensor it is associated with, rather
    /// than on the launcher or the missile.
    /// </summary>
    private IniDocument Sensors => _sensors.Value;

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
        _sensors = new Lazy<IniDocument>(() =>
            IniDocument.TryLoad(Path.Combine(_originalRoot, "systems", "sensors.ini"))
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

    /// <summary>
    /// The radars a unit carries, with the ranges and roles Sea Power declares
    /// for them. A unit with no resolvable radar returns an empty list, which
    /// the caller must treat as "the catalog says nothing here" rather than as
    /// a blind platform.
    /// </summary>
    public IReadOnlyList<SeaPowerCatalogSensor> ResolveSensors(string unitTypeId)
    {
        lock (_sync)
        {
            if (_sensorCache.TryGetValue(unitTypeId, out var cached))
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
                return _sensorCache[unitTypeId] = [];
            }
        }

        var unit = IniDocument.Load(unitPath);
        var result = new List<SeaPowerCatalogSensor>();
        var count = unit.Integer("SensorSystems", "NumberOfSensorSystems");
        for (var index = 1; index <= count; index++)
        {
            var section = $"SensorSystem{index}";
            var systemName = unit.Value(section, "SystemName");
            if (systemName.Length == 0
                || !string.Equals(Sensors.Value(systemName, "Kind"), "Radar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Ranges are declared in kilometres, unlike weapon ranges, which
            // the game declares in nautical miles.
            var maximum = Sensors.NumberOrDefault(systemName, "MaxRange", 0d);
            if (maximum <= 0)
            {
                continue;
            }

            var role = Sensors.Value(systemName, "Role");
            var air = role.IndexOf("Air", StringComparison.OrdinalIgnoreCase) >= 0;
            var surface = role.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0;
            result.Add(new SeaPowerCatalogSensor(
                section,
                systemName,
                checked((long)Math.Round(Sensors.NumberOrDefault(systemName, "MinRange", 0d) * 1_000d)),
                checked((long)Math.Round(maximum * 1_000d)),
                air,
                surface,
                string.Equals(Sensors.Value(systemName, "CanDetectLandTargets"), "True", StringComparison.OrdinalIgnoreCase),
                Math.Max(0, Sensors.IntegerOrDefault(systemName, "TargetChannels", 0)),
                string.Equals(Sensors.Value(systemName, "HasDataLink"), "True", StringComparison.OrdinalIgnoreCase)));
        }

        lock (_sync)
        {
            return _sensorCache[unitTypeId] = result.ToArray();
        }
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

        // Which mounts can shoot each ammunition, and how fast. Reload time and
        // salvo size come from the weapon system rather than the ammunition,
        // because two ships carrying the same missile can launch it at very
        // different rates. Mounts stay separate so each can cycle on its own.
        var cadence = new Dictionary<string, List<SeaPowerCatalogMount>>(StringComparer.OrdinalIgnoreCase);
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
            var mount = ReadMountCadence(unit, baseSection, type);
            var magazine = unit.Value(baseSection, "AssociatedMagazine");
            if (magazine.Length > 0)
            {
                // Two mounts can share one magazine. Sea Power counts the
                // magazine once, so a twin torpedo installation does not report
                // double its real stock. Each mount still shoots on its own,
                // so cadence is recorded before the stock is skipped.
                var firstUse = processedMagazines.Add(magazine);
                var ammunitionCount = unit.Integer(magazine, "NumberOfAmmunitionTypes");
                for (var ammunitionIndex = 1; ammunitionIndex <= ammunitionCount; ammunitionIndex++)
                {
                    var declared = unit.Integer(magazine, $"Ammunition{ammunitionIndex}_Count");
                    var resolved = AddResolvedAmmunition(
                        ammunitionIds,
                        unit.Value(magazine, $"Ammunition{ammunitionIndex}"),
                        unit,
                        scenarioYear,
                        include: declared != 0 && firstUse);
                    AddMount(cadence, resolved, mount);
                    if (firstUse)
                    {
                        AddRounds(rounds, resolved, declared);
                    }
                }
                continue;
            }

            // A gun or CIWS mount without a magazine has no catalog-countable
            // stock. Sea Power skips it here, so the server records no rounds
            // rather than inventing a number.
            var systemAmmunition = ResolveAmmunitionId(
                unit.Value(baseSection, "Ammunition"), unit, scenarioYear);
            AddMount(cadence, systemAmmunition, mount);
            if (string.Equals(type, "Gun", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "CIWS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var systemName = unit.Value(baseSection, "SystemName");
            var containers = unit.IntegerOrDefault(baseSection, "NumberOfContainers", 1);
            var attachments = WeaponSystems.IntegerOrDefault(systemName, "NumberOfAttachments", 1);
            AddRounds(rounds, systemAmmunition, checked(containers * attachments));
        }

        var result = ammunitionIds
            .SelectMany(ResolveAmmunition)
            .GroupBy(item => (item.AmmunitionId, item.TargetType), new WeaponKeyComparer())
            .Select(group => group.OrderByDescending(item => item.MaximumRangeMeters).First())
            .Select(item => item with
            {
                MagazineRounds = rounds.TryGetValue(item.AmmunitionId, out var stock) ? stock : 0,
                Mounts = cadence.TryGetValue(item.AmmunitionId, out var mounts)
                    ? mounts.ToArray()
                    : Array.Empty<SeaPowerCatalogMount>(),
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
    /// Reads one mount's firing cadence. Sea Power takes reload time and fire
    /// rate from the shared weapon-system definition and lets a unit override
    /// either in its own weapon-system section, which is how the same launcher
    /// can cycle at different rates on different ships.
    /// </summary>
    private SeaPowerCatalogMount ReadMountCadence(IniDocument unit, string systemSection, string type)
    {
        var systemName = unit.Value(systemSection, "SystemName");
        var reload = unit.NumberOrDefault(
            systemSection,
            "ReloadTime",
            WeaponSystems.NumberOrDefault(systemName, "ReloadTime", 0d));

        // A gun does not reload as a unit; it sustains a rate of fire. One
        // engagement therefore consumes a second of that rate rather than a
        // salvo of discrete rounds.
        var isSustainedFire = string.Equals(type, "Gun", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "CIWS", StringComparison.OrdinalIgnoreCase);
        int salvo;
        if (isSustainedFire)
        {
            var roundsPerMinute = unit.NumberOrDefault(
                systemSection,
                "FireRate",
                WeaponSystems.NumberOrDefault(systemName, "FireRate", 0d));
            salvo = roundsPerMinute > 0
                ? Math.Max(1, checked((int)Math.Round(roundsPerMinute / 60d, MidpointRounding.AwayFromZero)))
                : 0;
        }
        else
        {
            salvo = Math.Max(0, WeaponSystems.IntegerOrDefault(systemName, "SalvoFireAmount", 0));
        }

        return new SeaPowerCatalogMount(
            systemSection,
            reload > 0 ? checked((long)Math.Round(reload, MidpointRounding.AwayFromZero)) : 0,
            salvo,
            ReadGuidanceChannels(unit, systemSection));
    }

    /// <summary>
    /// Sums the guidance channels of the fire-control sensors a mount is
    /// associated with. The unit names its own sensor sections, and each names
    /// a shared sensor definition that declares how many missiles it can guide
    /// at once.
    /// </summary>
    private int ReadGuidanceChannels(IniDocument unit, string systemSection)
    {
        var associated = unit.Value(systemSection, "AssociatedSensors");
        if (associated.Length == 0)
        {
            return 0;
        }

        var channels = 0;
        foreach (var reference in associated.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var sensorName = unit.Value(reference.Trim(), "SystemName");
            if (sensorName.Length == 0)
            {
                continue;
            }
            channels = checked(channels + Math.Max(0, Sensors.IntegerOrDefault(sensorName, "WeaponChannels", 0)));
        }
        return channels;
    }

    /// <summary>
    /// Records one more mount able to fire this ammunition, keeping declaration
    /// order so the same unit always resolves its mounts identically.
    /// </summary>
    private static void AddMount(
        IDictionary<string, List<SeaPowerCatalogMount>> cadence,
        string ammunitionId,
        SeaPowerCatalogMount mount)
    {
        if (ammunitionId.Length == 0)
        {
            return;
        }
        if (!cadence.TryGetValue(ammunitionId, out var existing))
        {
            cadence[ammunitionId] = existing = [];
        }
        // A mount that feeds several ammunition types walks this path once per
        // type, but it must appear once per type, not twice for any one of them.
        if (!existing.Any(item => string.Equals(item.MountId, mount.MountId, StringComparison.Ordinal)))
        {
            existing.Add(mount);
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

        public double NumberOrDefault(string section, string key, double fallback) =>
            double.TryParse(Value(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

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
