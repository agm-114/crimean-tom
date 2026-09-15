using System.Globalization;
using SeaPower;

namespace Spacewar;

internal static class SpacecraftCatalog
{
    internal static List<Spacecraft> Units { get; private set; } = [];

    internal static void Load()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "spacewar.ini");
        var ini = Utils.openIniFile(path, logErrors: false, useCaching: false) ?? throw new IOException($"Missing {path}");
        List<Spacecraft> spacecraft = [];
        foreach (string section in ini.getNamesOfAllSections())
        {
            if (spacecraft.Count >= 64) throw new FormatException("Spacewar supports at most 64 spacecraft.");
            var values = ini.GetSectionKeyValues(section);
            double Read(string key, double fallback) => values.TryGetValue(key, out string raw)
                ? double.Parse(raw, CultureInfo.InvariantCulture) : fallback;
            string role = values.TryGetValue("Role", out string label) ? label : "Satellite";
            spacecraft.Add(new(section, role, new(Read("PerigeeKm", 500), Read("ApogeeKm", 500),
                Read("InclinationDeg", 0), Read("AscendingNodeDeg", 0), Read("PeriapsisArgumentDeg", 0), Read("MeanAnomalyDeg", 0))));
        }
        if (spacecraft.Count == 0) throw new FormatException("Spacewar needs at least one spacecraft profile.");
        Units = spacecraft;
    }
}
