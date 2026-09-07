using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static class ScenarioUnitCatalog
{
    private static IReadOnlyList<ScenarioSubmarineOption>? _submarines;
    private static readonly Dictionary<string, float[]> Speeds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> PursuitCraft = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsPursuitCraft(string unitType)
    {
        if (PursuitCraft.TryGetValue(unitType, out bool small)) return small;
        string path = Path.Combine(Application.streamingAssetsPath, "original", "vessels", unitType + ".ini");
        bool Within(string section, string key, float maximum) => File.Exists(path) &&
            float.TryParse(ReadIniValue(path, section, key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) && value > 0 && value <= maximum;
        return PursuitCraft[unitType] = Within("General", "Length", 60) && Within("Physics", "Displacement", 500)
            && NavalWeaponReach.Read(unitType) <= 12;
    }

    internal static float SpeedAtTelegraph(string unitType, int telegraph)
    {
        if (!Speeds.TryGetValue(unitType, out float[] speeds))
        {
            string path = new[] { "vessels", "submarines" }.Select(folder => Path.Combine(
                Application.streamingAssetsPath, "original", folder, unitType + ".ini")).FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException($"Cannot read operating speed for {unitType}.");
            string[] authored = ReadIniValue(path, "Physics", "TelegraphVelocities").Split(',');
            speeds = new float[7];
            bool valid = authored.Length >= 7;
            for (int index = 0; valid && index < 7; index++)
                valid = float.TryParse(authored[index], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out speeds[index]);
            if (!valid)
            {
                float maximum = float.TryParse(ReadIniValue(path, "Physics", "MaxForwardVelocity"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
                    ? Math.Max(1, value) : 32;
                for (int index = 2; index < 7; index++) speeds[index] = (index - 1) * maximum / 5;
            }
            speeds[1] = 0;
            Speeds[unitType] = speeds;
        }
        return speeds[Math.Clamp(telegraph, 0, 5) + 1];
    }
    private static readonly Dictionary<string, CarrierAirPackage?> CarrierAirPackages =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<ScenarioSubmarineOption> GetSubmarines(
        IEnumerable<string> nations)
    {
        HashSet<string> selected = new(nations, StringComparer.OrdinalIgnoreCase);
        return GetAllSubmarines()
            .Where(option => selected.Contains(option.Nation))
            .ToArray();
    }

    internal static CarrierAirPackage? GetCarrierAirPackage(string carrierType)
    {
        if (CarrierAirPackages.TryGetValue(carrierType, out CarrierAirPackage? cached))
        {
            return cached;
        }

        CarrierAirPackage? package = ReadCarrierAirPackage(carrierType);
        CarrierAirPackages[carrierType] = package;
        return package;
    }

    private static IReadOnlyList<ScenarioSubmarineOption> GetAllSubmarines()
    {
        if (_submarines is not null)
        {
            return _submarines;
        }

        string directory = Path.Combine(Application.streamingAssetsPath, "original", "vessels");
        List<ScenarioSubmarineOption> output = [];
        foreach (string variantsPath in Directory.EnumerateFiles(directory, "*_variants.ini"))
        {
            string unitType = Path.GetFileNameWithoutExtension(variantsPath)[..^"_variants".Length];
            string unitPath = Path.Combine(directory, $"{unitType}.ini");
            if (unitType.Contains("_ssbn_", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(unitPath) ||
                !ReadIniValue(unitPath, "General", "UnitType")
                    .Equals("Submarine", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string defaultNation = string.Empty;
            foreach ((string section, IReadOnlyDictionary<string, string> values) in ReadSections(variantsPath))
            {
                if (section.Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    if (values.TryGetValue("Nation", out string nation))
                    {
                        defaultNation = nation;
                    }
                }
                else if (section.StartsWith("Variant", StringComparison.OrdinalIgnoreCase))
                {
                    string nation = values.TryGetValue("Nation", out string overrideNation)
                        ? overrideNation
                        : defaultNation;
                    if (!string.IsNullOrWhiteSpace(nation))
                    {
                        output.Add(new ScenarioSubmarineOption(unitType, section, nation));
                    }
                }
            }
        }

        _submarines = output;
        return output;
    }

    private static CarrierAirPackage? ReadCarrierAirPackage(string carrierType)
    {
        string root = Path.Combine(Application.streamingAssetsPath, "original");
        string carrierPath = Path.Combine(root, "vessels", $"{carrierType}.ini");
        if (!File.Exists(carrierPath))
        {
            return null;
        }

        List<(string Aircraft, string Squadron, int Available)> airGroup = [];
        foreach ((string section, IReadOnlyDictionary<string, string> values) in ReadSections(carrierPath))
        {
            if (!section.Equals("AirGroup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach ((string aircraft, string definition) in values)
            {
                string[] firstSquadron = definition.Split('|')[0].Split(',');
                string squadron = firstSquadron[0].Trim();
                int available = firstSquadron.Length > 1 &&
                    int.TryParse(firstSquadron[1].Trim(), out int parsedCount)
                        ? parsedCount
                        : 0;
                airGroup.Add((aircraft, squadron, available));
            }
        }

        List<CarrierAirPlan> capCandidates = [];
        List<CarrierAirPlan> aewCandidates = [];
        List<CarrierAirPlan> aswCandidates = [];
        foreach ((string aircraft, string squadron, int available) in airGroup)
        {
            string aircraftPath = Path.Combine(root, "aircraft", $"{aircraft}.ini");
            if (!File.Exists(aircraftPath))
            {
                continue;
            }

            bool isHelicopter = ReadIniValue(aircraftPath, "General", "UnitType")
                .Equals("Helicopter", StringComparison.OrdinalIgnoreCase);
            string[] loadouts = ReadFirstIniValue(aircraftPath, "AvailableLoadouts")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(loadout => loadout.Trim())
                .ToArray();
            string? capLoadout = loadouts.FirstOrDefault(candidate =>
                !isHelicopter && candidate.StartsWith("AirToAir", StringComparison.OrdinalIgnoreCase));
            if (capLoadout is not null)
            {
                capCandidates.Add(new CarrierAirPlan(
                    aircraft,
                    squadron,
                    capLoadout,
                    "Patrol",
                    isHelicopter,
                    Math.Max(0, available),
                    null));
            }

            string? aewLoadout = loadouts.FirstOrDefault(candidate =>
                candidate.Equals("AEW", StringComparison.OrdinalIgnoreCase));
            if (aewLoadout is not null)
            {
                aewCandidates.Add(new CarrierAirPlan(
                    aircraft,
                    squadron,
                    aewLoadout,
                    "Patrol",
                    isHelicopter,
                    Math.Max(0, available),
                    null));
            }

            string? aswLoadout = loadouts.FirstOrDefault(candidate =>
                isHelicopter && candidate.Equals("ASWHunter", StringComparison.OrdinalIgnoreCase)) ??
                loadouts.FirstOrDefault(candidate =>
                    isHelicopter && candidate.Equals("ASWHunterLight", StringComparison.OrdinalIgnoreCase)) ??
                loadouts.FirstOrDefault(candidate =>
                    isHelicopter && candidate.StartsWith("ASW", StringComparison.OrdinalIgnoreCase));
            if (aswLoadout is not null)
            {
                aswCandidates.Add(new CarrierAirPlan(
                    aircraft,
                    squadron,
                    aswLoadout,
                    "AntiSub",
                    isHelicopter,
                    Math.Max(0, available),
                    ReadSonobuoyAmmunition(aircraftPath, aswLoadout, 1985)));
            }
        }

        CarrierAirPlan? cap = capCandidates
            .OrderByDescending(candidate => candidate.AvailableCount)
            .FirstOrDefault();
        CarrierAirPlan? aew = aewCandidates
            .OrderByDescending(candidate => candidate.AvailableCount)
            .FirstOrDefault();
        CarrierAirPlan? asw = aswCandidates
            .OrderByDescending(candidate => candidate.SonobuoyAmmunition is not null)
            .ThenByDescending(candidate => candidate.AvailableCount)
            .FirstOrDefault();
        return cap is null && aew is null && asw is null
            ? null
            : new CarrierAirPackage(cap, aew, asw);
    }

    private static string? ReadSonobuoyAmmunition(
        string aircraftPath,
        string loadout,
        int scenarioYear)
    {
        IReadOnlyList<(string Section, IReadOnlyDictionary<string, string> Values)> sections =
            ReadSections(aircraftPath).ToArray();
        string suffix = loadout.Equals("Default", StringComparison.OrdinalIgnoreCase)
            ? "Default"
            : loadout;
        foreach ((string section, IReadOnlyDictionary<string, string> values) in sections)
        {
            if (!section.StartsWith("WeaponSystem", StringComparison.OrdinalIgnoreCase) ||
                !section.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach ((string key, string value) in values)
            {
                if (!key.StartsWith("Station", StringComparison.OrdinalIgnoreCase) ||
                    !value.Contains("Sonobuoy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string ammunition = value.Split('|')[0].Trim();
                if (!ammunition.StartsWith("DateBased_", StringComparison.OrdinalIgnoreCase))
                {
                    return ammunition;
                }

                string substitutions = ReadFirstIniValue(aircraftPath, ammunition);
                string? datedAmmunition = substitutions
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(entry => entry.Split(','))
                    .Where(parts => parts.Length >= 2 && int.TryParse(parts[0], out int year) && year <= scenarioYear)
                    .OrderByDescending(parts => int.Parse(parts[0]))
                    .Select(parts => parts[1].Trim())
                    .FirstOrDefault();
                return datedAmmunition;
            }
        }

        return null;
    }

    private static string ReadIniValue(string path, string wantedSection, string wantedKey)
    {
        foreach ((string section, IReadOnlyDictionary<string, string> values) in ReadSections(path))
        {
            if (section.Equals(wantedSection, StringComparison.OrdinalIgnoreCase) &&
                values.TryGetValue(wantedKey, out string value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadFirstIniValue(string path, string wantedKey)
    {
        foreach ((_, IReadOnlyDictionary<string, string> values) in ReadSections(path))
        {
            if (values.TryGetValue(wantedKey, out string value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    internal static IEnumerable<(string Section, IReadOnlyDictionary<string, string> Values)>
        ReadSections(string path)
    {
        string section = string.Empty;
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = StripComment(rawLine).Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section.Length > 0)
                {
                    yield return (section, values);
                }

                section = line[1..^1].Trim();
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals > 0)
            {
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        if (section.Length > 0)
        {
            yield return (section, values);
        }
    }

    private static string StripComment(string line)
    {
        int slash = line.IndexOf("//", StringComparison.Ordinal);
        int hash = line.IndexOf('#');
        int comment = slash < 0 ? hash : hash < 0 ? slash : Math.Min(slash, hash);
        return comment < 0 ? line : line[..comment];
    }
}

internal record ScenarioSubmarineOption(
    string UnitType,
    string VariantReference,
    string Nation);

internal record CarrierAirPlan(
    string AircraftType,
    string SquadronReference,
    string LoadoutVariant,
    string MissionType,
    bool IsHelicopter,
    int AvailableCount,
    string? SonobuoyAmmunition);

internal record CarrierAirPackage(
    CarrierAirPlan? CombatAirPatrol,
    CarrierAirPlan? AirborneEarlyWarning,
    CarrierAirPlan? AswPatrol);
