using System.Globalization;
using SeaPower;
using UnityEngine;

namespace Deckpark;

internal class DeckSlot(int number, Vector3 offset, float heading = 0)
{
    internal int Number { get; } = number;
    internal Vector3 Offset { get; set; } = offset;
    internal float Heading { get; set; } = heading;
    internal bool Used { get; set; }
}

internal static class DeckCatalog
{
    internal static readonly List<string> Units = [];
    private static readonly Dictionary<string, List<DeckSlot>> Profiles = new(StringComparer.OrdinalIgnoreCase);
    internal static void Load()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "deckpark.ini");
        var ini = Utils.openIniFile(path, logErrors: false, useCaching: false) ?? throw new IOException($"Missing {path}");
        Units.Clear();
        Profiles.Clear();
        foreach (string section in ini.getNamesOfAllSections())
        {
            var values = ini.GetSectionKeyValues(section);
            if (section == "GroundUnits")
            {
                foreach (var entry in values)
                    if (entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) Units.Add(entry.Key);
                continue;
            }
            List<DeckSlot> slots = [];
            foreach (var entry in values)
            {
                if (!int.TryParse(entry.Key, out int number) || number is < 1 or > 8 || slots.Any(slot => slot.Number == number))
                    throw new FormatException($"Invalid slot in {section}: {entry.Key}");
                float[] v = entry.Value.Split(',').Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length != 4 || v.Any(value => !float.IsFinite(value)) || Math.Abs(v[0]) > 100 || Math.Abs(v[2]) > 250 || v[1] is < 0 or > 80)
                    throw new FormatException($"Invalid deck position in {section}/{entry.Key}");
                slots.Add(new(number, new(v[0], v[1], v[2]), v[3]));
            }
            if (slots.Count == 0) throw new FormatException($"No deck slots in {section}");
            Profiles.Add(section, slots.OrderBy(slot => slot.Number).ToList());
        }
        if (Units.Count is < 1 or > 64) throw new FormatException("Choose 1-64 ground-unit catalogue IDs.");
    }

    internal static List<DeckSlot> Slots(Vessel ship) => Profiles.TryGetValue(ship._obp._objectIniName, out var slots)
        ? slots.Select(slot => new DeckSlot(slot.Number, slot.Offset, slot.Heading)).ToList()
        : [new(1, new(0, 10, ship._obp._length * 0.25f)), new(2, new(0, 10, -ship._obp._length * 0.25f), 180)];
    internal static bool Available(string unit) => Singleton<UnitMetadataProvider>.InstanceExists()
        && Singleton<UnitMetadataProvider>.Instance.UnitData.ContainsKey(("land_units", unit));
    internal static string Name(string unit) => Available(unit)
        ? Singleton<UnitMetadataProvider>.Instance.UnitData[("land_units", unit)].Item2.DefaultName : unit;
}
