using System.Globalization;

namespace NuclearOption.ScenarioEditor.Core;

public record ProductionRate(IReadOnlyList<double> Intervals)
{
    public int Factories => Intervals.Count;
    public bool HasInvalidInterval => Intervals.Any(seconds => !double.IsFinite(seconds) || seconds <= 0);
    public double? UnitsPerHour => HasInvalidInterval ? null : Intervals.Sum(seconds => 3600 / seconds);
    public double? SecondsPerUnit => UnitsPerHour is > 0 and var rate ? 3600 / rate : null;
    public string SecondsDisplay => HasInvalidInterval ? "Invalid interval" : SecondsPerUnit is { } seconds
        ? seconds.ToString("0.##", CultureInfo.CurrentCulture) : "No production";
    public string FactoryIntervalsDisplay => Factories == 0 ? "No factories produce this unit." :
        string.Join("\n", Intervals.Order().GroupBy(seconds => seconds)
            .Select(g => $"{g.Count()} {(g.Count() == 1 ? "factory" : "factories")} at {g.Key:0.##} seconds per unit"));
}

public record UnitProduction(string Type, ProductionRate Before, ProductionRate After)
{
    public string Name => UnitCatalog.Name(Type);
    public bool HasFinalProduction => After.UnitsPerHour is > 0;
    public bool IsChanged => Before.HasInvalidInterval != After.HasInvalidInterval ||
        Math.Abs((Before.UnitsPerHour ?? 0) - (After.UnitsPerHour ?? 0)) >
        1e-9 * Math.Max(1, Math.Max(Before.UnitsPerHour ?? 0, After.UnitsPerHour ?? 0));

    public static IReadOnlyList<UnitProduction> Compare(MissionDocument source, MissionDocument result, string? faction = null)
    {
        var before = source.GetUnitProduction(faction);
        var after = result.GetUnitProduction(faction);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Select(type => new UnitProduction(type, before.GetValueOrDefault(type) ?? new([]), after.GetValueOrDefault(type) ?? new([])))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
