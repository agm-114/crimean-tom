using SeaPower;

namespace OODA;

internal enum ReinforcementKind { Surface, Submarine, Aircraft, Helicopter, Land, Carrier }

internal class ReinforcementCatalog
{
    internal record Model(string Type, ReinforcementKind Kind, string Family, string[] Loadouts, bool Port);

    private readonly Dictionary<ReinforcementKind, Model[][]> families;

    internal ReinforcementCatalog()
    {
        var files = Singleton<FileManager>.Instance;
        List<(Model Model, string[] AirGroup)> entries = [];
        foreach (string folder in new[] { "vessels", "aircraft", "land_units" })
        foreach (string type in files.GetFiles(folder, "*.ini").Select(Path.GetFileNameWithoutExtension)
                     .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (type.StartsWith("civ_", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("_variants", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("_squadrons", StringComparison.OrdinalIgnoreCase))
                continue;

            IniHandler ini = Utils.openVirtualIniFile(folder, type, folder);
            if (ini is null || !Enum.TryParse(Read(ini, "General", "UnitType"), true, out ObjectBase.ObjectType unitType)) continue;
            if (Read(ini, "General", "HideIn").Split(',').Any(place => place.Trim().Equals("MissionEditor", StringComparison.OrdinalIgnoreCase)))
                continue;
            string roles = Read(ini, "General", "Role") + "," + Read(ini, "AI", "Role");
            if (roles.Split(',').Any(role => role.Trim() is "Deco" or "SeaMine" or "Civilian")) continue;
            ReinforcementKind? kind = unitType switch
            {
                ObjectBase.ObjectType.Vessel => ReinforcementKind.Surface,
                ObjectBase.ObjectType.Submarine => ReinforcementKind.Submarine,
                ObjectBase.ObjectType.Aircraft => ReinforcementKind.Aircraft,
                ObjectBase.ObjectType.Helicopter => ReinforcementKind.Helicopter,
                ObjectBase.ObjectType.LandUnit => ReinforcementKind.Land,
                _ => null,
            };
            if (kind is null) continue;

            string family = Read(ini, "General", "EncyclopediaTag");
            if (family.Length == 0) family = type[(type.IndexOf('_') + 1)..];
            string[] loadouts = Read(ini, "WeaponSystems", "AvailableLoadouts")
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(loadout => loadout.Trim()).ToArray();
            string[] airGroup = ini.doesSectionExist("AirGroup") && ini.doesSectionExist("FlightDeck")
                ? ini.GetAllKeysOfSection("AirGroup").ToArray() : [];
            bool port = Read(ini, "General", "LandUnitSubType").Equals("Port", StringComparison.OrdinalIgnoreCase);
            entries.Add((new(type, kind.Value, family, loadouts, port), airGroup));
        }

        var aircraft = entries.Where(entry => entry.Model.Kind == ReinforcementKind.Aircraft)
            .Select(entry => entry.Model.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        families = entries.Select(entry => entry.Model.Kind == ReinforcementKind.Surface && entry.AirGroup.Any(aircraft.Contains)
                ? entry.Model with { Kind = ReinforcementKind.Carrier } : entry.Model)
            .GroupBy(model => model.Kind).ToDictionary(group => group.Key,
                group => group.GroupBy(model => model.Family, StringComparer.OrdinalIgnoreCase).Select(family => family.ToArray()).ToArray());
        UnityEngine.Debug.Log($"[OODA] Catalog families. {string.Join(", ", families.Select(group => $"{group.Key} {group.Value.Length}"))}.");
    }

    internal IEnumerable<ReinforcementKind> Kinds => families.Keys.Where(kind => kind != ReinforcementKind.Carrier);

    internal Model Pick(ReinforcementKind kind, Random random)
    {
        Model[][] pool = families[kind];
        Model[] family = pool[random.Next(pool.Length)];
        return family[random.Next(family.Length)];
    }

    private static string Read(IniHandler ini, string section, string key) =>
        ini.readValue(section, key, string.Empty, performLogging: false).Trim();
}
