using SeaPower;
using System.Globalization;

namespace UNREP;

internal static class SupplySettings
{
    private static IniHandler ini = null!;
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
    internal static double SlingHandlingSeconds { get; private set; }
    internal static double HoverToleranceNm { get; private set; }

    internal static void Load()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "supplies.ini");
        ini = Utils.openIniFile(path, logErrors: false, useCaching: false) ?? throw new IOException($"Missing {path}");
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
        SlingHandlingSeconds = Read("Helicopter", "HandlingSeconds", 30, 1, 600);
        HoverToleranceNm = Read("Helicopter", "HoverToleranceNm", 0.05, 0.01, 0.1);
        foreach (string section in ini.getNamesOfAllSections().Where(section => section != "Transfer"))
        foreach (string stock in new[] { "Fuel", "Food", "Water", "SpareParts" })
        foreach (string suffix in new[] { "", "Capacity" })
        {
            Read(section, stock + suffix + "BaseKg", 0, 0, 1e9);
            Read(section, stock + suffix + "KgPerTonne", 0, 0, 1e6);
        }
    }

    internal static double Amount(Vessel ship, string key)
    {
        double displacement = (ship._obp as VesselParameters)?._displacement ?? 0;
        if (double.IsNaN(displacement) || double.IsInfinity(displacement)) displacement = 0;
        double baseKg = Value(ship, key + "BaseKg", 1e9);
        double perTonne = Value(ship, key + "KgPerTonne", 1e6);
        return Math.Clamp(baseKg + perTonne * Math.Max(0, displacement), 0, 1e9);
    }

    private static double Value(Vessel ship, string key, double maximum) =>
        Read(ship._obp._objectIniName, key, Read("Defaults", key, 0, 0, maximum), 0, maximum);

    private static double Read(string section, string key, double fallback, double min, double max)
    {
        if (!ini.Data.TryGetValue(section, out var values) || !values.TryGetValue(key, out string raw)) return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
            throw new FormatException($"Invalid {section}/{key} in supplies.ini");
        return value;
    }
}
