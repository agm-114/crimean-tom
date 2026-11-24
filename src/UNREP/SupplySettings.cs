using SeaPower;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UNREP;

internal static class SupplySettings
{
    internal static bool AllowConcurrentUnrepAndVertrep { get; private set; } = true;
    public class OperationsOptions
    {
        public bool AllowConcurrentUnrepAndVertrep { get; set; } = true;
    }
    internal static double RangeNm { get; private set; }
    internal static double MaxSpeed { get; private set; }
    internal static double KgPerSecond { get; private set; }
    internal static double ItemsPerSecond { get; private set; }
    internal static double PointsPerSecond { get; private set; }
    internal static double UnrepSpeed { get; private set; }
    internal static double StationNm { get; private set; }
    internal static double RendezvousNm { get; private set; }
    internal static double BreakawayNm { get; private set; }
    internal static double StationToleranceNm { get; private set; }
    internal static double HeadingTolerance { get; private set; }
    internal static double RelativeSpeedTolerance { get; private set; }
    internal static double SlingCapacityKg { get; private set; }
    internal static double HoverToleranceNm { get; private set; }

    internal static void Load()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "supplies.ini");
        string optionsPath = Path.ChangeExtension(path, ".yml");
        AllowConcurrentUnrepAndVertrep = !File.Exists(optionsPath) || (new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance).Build()
            .Deserialize<OperationsOptions>(File.ReadAllText(optionsPath)) ?? new()).AllowConcurrentUnrepAndVertrep;
        var ini = Utils.openIniFile(path, logErrors: false, useCaching: false) ?? throw new IOException($"Missing {path}");
        RangeNm = Read("Transfer", "RangeNm", 0.2, 0.01, 2);
        MaxSpeed = Read("Transfer", "MaxSpeedKnots", 12, 0, 30);
        KgPerSecond = Read("Transfer", "KgPerSecond", 20, 0.01, 10000);
        ItemsPerSecond = Read("Transfer", "ItemsPerSecond", 0.1, 0.001, 1000);
        PointsPerSecond = Read("Transfer", "AmmoPointsPerSecond", 1, 0.001, 10000);
        UnrepSpeed = Read("Transfer", "UnrepSpeedKnots", 8, 1, MaxSpeed);
        StationNm = Read("Transfer", "StationNm", 0.1, 0.05, RangeNm);
        RendezvousNm = Read("Transfer", "RendezvousNm", 0.6, 0.3, 5);
        BreakawayNm = Read("Transfer", "BreakawayNm", 0.5, StationNm * 2, 5);
        StationToleranceNm = Read("Transfer", "StationToleranceNm", 0.03, 0.005, StationNm / 2);
        HeadingTolerance = Read("Transfer", "HeadingToleranceDegrees", 10, 1, 30);
        RelativeSpeedTolerance = Read("Transfer", "RelativeSpeedToleranceKnots", 1, 0.1, 3);
        SlingCapacityKg = Read("Helicopter", "SlingCapacityKg", 500, 1, 10000);
        HoverToleranceNm = Read("Helicopter", "HoverToleranceNm", 0.05, 0.01, 0.1);
        SupplyTuning.Register(ini);

        double Read(string section, string key, double fallback, double min, double max) =>
            SupplyTuning.ReadNumber(ini, section, key, max, min) ?? fallback;
    }
}
