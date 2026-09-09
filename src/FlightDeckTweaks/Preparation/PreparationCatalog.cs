using System.Globalization;
using SeaPower;

namespace FlightDeckTweaks.Preparation;

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
    private static readonly ServiceRates Rates = new(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "rearm-times.ini"));
    private static readonly Dictionary<string, WeaponService> Weapons = new(StringComparer.OrdinalIgnoreCase);
    private static bool initialized;

    internal static void Initialize()
    {
        if (initialized) return;
        Rates.Read();
        Rates.Get("Defaults", "MountSeconds", 120);
        Rates.Get("Defaults", "RefuelKgPerSecond", 4);
        foreach (string file in FileManager.Instance.GetFiles("ammunition", "*.ini")) Weapon(Path.GetFileNameWithoutExtension(file));
        foreach (string file in FileManager.Instance.GetFiles("aircraft", "*.ini"))
        {
            string reference = Path.GetFileNameWithoutExtension(file);
            IniHandler ini = Utils.openIniFile("/aircraft", $"/{reference}.ini");
            RefuelSeconds(reference, ini);
        }
        Rates.Save();
        initialized = true;
    }

    internal static WeaponService Weapon(string reference)
    {
        if (Weapons.TryGetValue(reference, out var weapon)) return weapon;
        IniHandler ini = Utils.openIniFile("/ammunition", $"/{reference}.ini");
        string type = Value(ini, "General", "Type").ToLowerInvariant();
        bool countermeasure = type is "chaff" or "flare";
        double defaultMount = type switch
        {
            "projectile" => 0.3,
            "chaff" or "flare" => 2,
            "sonobuoy" => 5,
            _ => Rates.Get("Defaults", "MountSeconds", 120),
        };
        double mount = Rates.Get("WeaponMountSeconds", reference, defaultMount);
        double remove = Rates.Get("WeaponRemoveSeconds", reference, mount * Rates.Get("Defaults", "UnloadMultiplier", 0.35));
        if (remove >= mount) throw new FormatException($"Removal must be faster than mounting: {reference}");
        return Weapons[reference] = new(mount, remove, Value(ini, "General", "SupplyCategory"), Number(ini, "General", "AmmoPoints", 0), countermeasure);
    }

    internal static PreparationLoadout Loadout(VehicleTypeOnBoard vehicle, Loadout selected)
    {
        Initialize();
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
        return new(RefuelSeconds(vehicle._fileName, ini), stores);
    }

    private static double RefuelSeconds(string reference, IniHandler? ini) => Rates.Get("AircraftRefuelSeconds", reference,
        Math.Max(1, Number(ini, "Physics", "MaxFuel", 2400) / Rates.Get("Defaults", "RefuelKgPerSecond", 4)));

    private static string Value(IniHandler? ini, string section, string key) =>
        ini != null && ini.Data.TryGetValue(section, out var values) && values.TryGetValue(key, out string value) ? value.Trim() : "";
    private static double Number(IniHandler? ini, string section, string key, double fallback) =>
        double.TryParse(Value(ini, section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 ? value : fallback;
}
