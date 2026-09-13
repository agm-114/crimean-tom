using System.Globalization;
using SeaPower;

namespace Spacewar;

internal class Satellite(string name, string role, Orbit orbit)
{
    internal string Name { get; } = name;
    internal string Role { get; } = role;
    internal Orbit Orbit { get; } = orbit;
}

internal static class SatelliteCatalog
{
    internal static List<Satellite> Load()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "spacewar.ini");
        var ini = Utils.openIniFile(path, logErrors: false, useCaching: false) ?? throw new IOException($"Missing {path}");
        List<Satellite> satellites = [];
        foreach (string section in ini.getNamesOfAllSections())
        {
            if (satellites.Count >= 64) throw new FormatException("Spacewar supports at most 64 satellites.");
            var values = ini.GetSectionKeyValues(section);
            double Read(string key, double fallback) => values.TryGetValue(key, out string raw)
                ? double.Parse(raw, CultureInfo.InvariantCulture) : fallback;
            string role = values.TryGetValue("Role", out string label) ? label : "Satellite";
            satellites.Add(new(section, role, new(Read("PerigeeKm", 500), Read("ApogeeKm", 500),
                Read("InclinationDeg", 0), Read("AscendingNodeDeg", 0), Read("PeriapsisArgumentDeg", 0), Read("MeanAnomalyDeg", 0))));
        }
        if (satellites.Count == 0) throw new FormatException("Spacewar needs at least one satellite profile.");
        return satellites;
    }
}
