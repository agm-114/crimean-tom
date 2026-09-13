using SeaPower;
using UnityEngine;

namespace Airdrop;

internal class CargoDefinition(string reference, int count, int mass)
{
    internal string Reference { get; } = reference;
    internal int Count { get; } = count;
    internal int Mass { get; } = mass;
    internal bool Available => Singleton<UnitMetadataProvider>.InstanceExists()
        && Singleton<UnitMetadataProvider>.Instance.UnitData.ContainsKey(("land_units", Reference));
    internal string Name => Available
        ? Singleton<UnitMetadataProvider>.Instance.UnitData[("land_units", Reference)].Item2.DefaultName : Reference;
}

internal class AircraftManifest(int capacity, int minAltitude, int maxAltitude, int minSpeed, int maxSpeed, int interval, List<CargoDefinition> cargo)
{
    internal int Capacity { get; } = capacity;
    internal int MinAltitude { get; } = minAltitude;
    internal int MaxAltitude { get; } = maxAltitude;
    internal int MinSpeed { get; } = minSpeed;
    internal int MaxSpeed { get; } = maxSpeed;
    internal int Interval { get; } = interval;
    internal List<CargoDefinition> Cargo { get; } = cargo;
}

internal static class CargoCatalog
{
    internal static readonly Dictionary<string, AircraftManifest> Manifests = new(StringComparer.OrdinalIgnoreCase);
    internal static bool Load()
    {
        try
        {
            Manifests.Clear();
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "airdrop.ini");
            var ini = Utils.openIniFile(path, logErrors: false, useCaching: false) ?? throw new IOException($"Missing {path}");
            foreach (string section in ini.getNamesOfAllSections())
            {
                int capacity = ini.readValue(section, "CapacityKg", 27000);
                int minAltitude = ini.readValue(section, "MinAltitudeMeters", 150);
                int maxAltitude = ini.readValue(section, "MaxAltitudeMeters", 600);
                int minSpeed = ini.readValue(section, "MinSpeedKnots", 120);
                int maxSpeed = ini.readValue(section, "MaxSpeedKnots", 200);
                int interval = ini.readValue(section, "IntervalSeconds", 5);
                if (capacity is < 1 or > 200000 || minAltitude < 50 || maxAltitude < minAltitude || maxAltitude > 3000
                    || minSpeed < 60 || maxSpeed < minSpeed || maxSpeed > 300 || interval is < 1 or > 300)
                    throw new FormatException($"Invalid limits in {section}");
                List<CargoDefinition> cargo = [];
                foreach (var entry in ini.GetSectionKeyValues(section))
                {
                    if (entry.Key is "CapacityKg" or "MinAltitudeMeters" or "MaxAltitudeMeters" or "MinSpeedKnots" or "MaxSpeedKnots" or "IntervalSeconds") continue;
                    string[] key = entry.Key.Split('/');
                    string[] values = entry.Value.Split(',');
                    if (key.Length != 2 || key[0] != "land_units" || key[1].Length == 0
                        || key[1].Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-'))
                        || values.Length != 2 || !int.TryParse(values[0], out int count) || count is < 1 or > 64
                        || !int.TryParse(values[1], out int mass) || mass < 1 || mass > capacity)
                        throw new FormatException($"Invalid cargo entry {section}/{entry.Key}");
                    cargo.Add(new(key[1], count, mass));
                }
                if (cargo.Count == 0 || cargo.Sum(item => item.Count) > 64 || cargo.Sum(item => item.Count * item.Mass) > capacity)
                    throw new FormatException($"Manifest exceeds capacity in {section}");
                Manifests.Add(section, new(capacity, minAltitude, maxAltitude, minSpeed, maxSpeed, interval, cargo));
            }
            return Manifests.Count > 0;
        }
        catch (Exception error)
        {
            Manifests.Clear();
            Debug.LogError($"[Airdrop] Configuration rejected: {error.Message}");
            return false;
        }
    }
}

internal class CargoInventory(AircraftManifest definition)
{
    internal AircraftManifest Definition { get; } = definition;
    internal Dictionary<string, int> Aboard { get; } = definition.Cargo.ToDictionary(item => item.Reference, item => item.Count);
    internal Dictionary<string, int> Released { get; } = definition.Cargo.ToDictionary(item => item.Reference, _ => 0);
    internal CargoDefinition? ArmedCargo { get; set; }
    internal int Remaining { get; set; }
    internal float LastUpdate { get; set; } = GameTime.missionSessionTime;
    internal float Elapsed { get; set; }
    internal string Message { get; set; } = "";
    internal int Mass => Definition.Cargo.Sum(item => Aboard[item.Reference] * item.Mass);
    internal void Arm(CargoDefinition cargo, int quantity)
    {
        ArmedCargo = cargo;
        Remaining = Math.Clamp(quantity, 0, Aboard[cargo.Reference]);
        Elapsed = Definition.Interval;
        Message = "";
    }
    internal void Disarm() { ArmedCargo = null; Remaining = 0; Elapsed = 0; }
}
