using System.Globalization;
using SeaPower;
using static DeckTweaks.DeckSettings;

namespace DeckTweaks.Preparation;

internal class WeaponService(double mountSeconds, double removeSeconds, string category, double ammoPoints, bool countermeasure)
{
    internal double MountSeconds { get; } = mountSeconds;
    internal double RemoveSeconds { get; } = removeSeconds;
    internal string Category { get; } = category;
    internal double AmmoPoints { get; } = ammoPoints;
    internal bool Countermeasure { get; } = countermeasure;
}

internal static class PreparationCatalog
{
    private static readonly Dictionary<string, WeaponService> Weapons = new(StringComparer.OrdinalIgnoreCase);
    private static IniHandler? ammunitionNames;
    private static string? nameLanguage;

    internal static string WeaponName(string reference)
    {
        string language = Singleton<LanguageResourceHandler>.Instance.Language;
        if (ammunitionNames == null || nameLanguage != language)
        {
            ammunitionNames = LocalizationLoader.OpenVirtualLocalizationFile($"language_{language}", "ammunition_names", "ammunition_names");
            nameLanguage = language;
        }
        string fallback = reference.Equals("usn_pod_aww-9", StringComparison.OrdinalIgnoreCase) ? "AN/AWW-9 Data Link Pod" : reference;
        if (ammunitionNames == null) return fallback;
        string entry = Value(ammunitionNames, "AmmunitionNames", reference);
        string[] names = LocalizationLoader.ApplyNatoWeaponName(ammunitionNames, reference,
            (entry.Length > 0 ? entry : fallback).Split(','));
        string name = names.Length > 0 && !string.IsNullOrWhiteSpace(names[0]) ? names[0].Trim() : fallback;
        return names.Length > 1 && !string.IsNullOrWhiteSpace(names[1]) ? $"{name} ({names[1].Trim()})" : name;
    }

    internal static void ClearWeaponRates() => Weapons.Clear();

    internal static WeaponService Weapon(string reference)
    {
        if (Weapons.TryGetValue(reference, out var weapon)) return weapon;
        IniHandler ini = Utils.openIniFile("/ammunition", $"/{reference}.ini");
        string type = Value(ini, "General", "Type").ToLowerInvariant();
        bool countermeasure = type is "chaff" or "flare";
        double defaultMount = type switch
        {
            "projectile" => 0.21,
            "chaff" or "flare" => 2.8,
            "sonobuoy" => 7,
            "bomb" => 14,
            "aerialrocket" => 2.8,
            "torpedo" => 560,
            "fueltank" => 28,
            "airdepthcharge" => 42,
            _ => Rate("Defaults", "MountSeconds", 60),
        };
        defaultMount += Number(ini, "General", "Mass", 0) * Rate("Defaults", "MountSecondsPerKg", 0.1);
        double mount = Rate("WeaponMountSeconds", reference, defaultMount);
        double remove = Rate("WeaponRemoveSeconds", reference, mount * Rate("Defaults", "UnloadMultiplier", 1.1));
        double multiplier = Rate("Defaults", "WeaponTimeMultiplier", 1);
        return Weapons[reference] = new(mount * multiplier, remove * multiplier,
            Value(ini, "General", "SupplyCategory"), Number(ini, "General", "AmmoPoints", 0), countermeasure);
    }

    internal static PreparationLoadout Loadout(VehicleTypeOnBoard vehicle, Loadout selected)
    {
        int year = Singleton<SeaPower.Environment>.InstanceExists() ? Singleton<SeaPower.Environment>.Instance.Year : int.MaxValue;
        Loadout[] choices = vehicle.Loadouts.Where(loadout => loadout.ServiceEntryYear <= year).ToArray();
        HashSet<string> builtIns = new(StringComparer.OrdinalIgnoreCase);
        IniHandler ini = selected.OwnerIni;
        if (ini != null)
        {
            foreach (string section in ini.Data.Keys.Where(section => section.StartsWith("WeaponSystem", StringComparison.Ordinal)))
            {
                if (!int.TryParse(section.Substring("WeaponSystem".Length), out _)) continue;
                string type = Value(ini, section, "Type").ToLowerInvariant();
                if (type is not ("gun" or "ciws" or "chaff" or "flare")) continue;
                string magazine = Value(ini, section, "AssociatedMagazine");
                if (ini.Data.TryGetValue(magazine, out var values))
                    foreach (var entry in values.Where(entry => entry.Key.StartsWith("Ammunition", StringComparison.Ordinal)
                        && int.TryParse(entry.Key.Substring("Ammunition".Length), out _)))
                        builtIns.Add(Utils.ResolveDateBasedAmmo(ini, entry.Value));
                string direct = Value(ini, section, "Ammunition");
                if (direct.Length > 0) builtIns.Add(Utils.ResolveDateBasedAmmo(ini, direct));
            }
        }
        StoreRequirement[] stores = selected.AmmunitionAmounts.Where(pair => pair.Value > 0).Select(pair =>
        {
            WeaponService weapon = Weapon(pair.Key);
            int frequency = choices.Count(loadout => loadout.AmmunitionAmounts.ContainsKey(pair.Key));
            int priority = builtIns.Contains(pair.Key) || weapon.Countermeasure ? 0 : choices.Length + 1 - frequency;
            return new StoreRequirement(pair.Key, pair.Value, weapon.MountSeconds, weapon.RemoveSeconds, priority);
        }).ToArray();
        bool helicopter = vehicle._type == VehicleType.Helicopter;
        double capacity = Number(ini, helicopter ? "Physics" : "Performance", "MaxFuel", helicopter ? 1000 : 0);
        StoreStation[] stations = ReadStations(selected).ToArray();
        return new(RefuelSeconds(vehicle._fileName, capacity), stores, capacity,
            new(stations, stations.Concat(choices.Where(loadout => loadout != selected).SelectMany(ReadStations))), refuel: capacity > 0);
    }

    private static IEnumerable<StoreStation> ReadStations(Loadout loadout)
    {
        IniHandler? ini = loadout.OwnerIni;
        if (ini == null) yield break;
        int.TryParse(Value(ini, "WeaponSystems", "NumberOfWeaponSystems"), out int systems);
        for (int system = 1; system <= systems; system++)
        {
            string baseSection = $"WeaponSystem{system}";
            string section = baseSection + loadout.Name;
            if (!ini.doesSectionExist(section)) section = baseSection;
            int.TryParse(Value(ini, baseSection, "NumberOfStations"), out int stations);
            for (int number = 1; number <= stations; number++)
            {
                string[] entry = Value(ini, section, $"Station{number}").Split('|');
                if (entry[0].Length == 0) continue;
                string reference = Utils.ResolveDateBasedAmmo(ini, entry[0].Trim());
                if (string.IsNullOrEmpty(reference) || !loadout.AmmunitionAmounts.ContainsKey(reference)) continue;
                int capacity = entry.Length > 1 ? Value(ini, baseSection, entry[1].Trim() + "Positions").Split('|').Length : 1;
                yield return new(reference, system, number, capacity);
            }
        }
    }

    private static double RefuelSeconds(string reference, double capacity)
    {
        double fullTankSeconds = Rate("Defaults", "RefuelFixedSeconds", 300)
            + Rate("Defaults", "RefuelSecondsPerKg", 0.015) * capacity;
        return Rate("AircraftRefuelSeconds", reference, fullTankSeconds);
    }

    internal static double OperationTimeMultiplier(PendingLaunchTask task) =>
        Rate("AircraftOperationTimeMultipliers", task.VehicleType._fileName, 1)
        * Rate("NationOperationTimeMultipliers", task._flightDeck._baseObject.getNation(), 1);

    private static string Value(IniHandler? ini, string section, string key) =>
        ini != null && ini.Data.TryGetValue(section, out var values) && values.TryGetValue(key, out string value) ? value.Trim() : "";
    private static double Number(IniHandler? ini, string section, string key, double fallback) =>
        double.TryParse(Value(ini, section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) && value >= 0 ? value : fallback;
}
