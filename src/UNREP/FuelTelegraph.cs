using Noesis;
using SeaPower;
using Settings;
using static UI.NativeUi;

namespace UNREP;

internal static class FuelTelegraph
{
    private const string SectionName = "FuelTelegraph";
    private const double MaxPercent = 1000;
    // Sea Power's telegraph step starts at -1 for reverse. The speed array starts at zero.
    private static readonly (string Key, string Label, double Percent)[] Steps =
    [
        ("BackFullPercent", "Back Full", 150),
        ("AllStopPercent", "All Stop", 10),
        ("AheadOneThirdPercent", "Ahead 1/3", 30),
        ("AheadTwoThirdsPercent", "Ahead 2/3", 60),
        ("AheadStandardPercent", "Ahead Standard", 100),
        ("AheadFullPercent", "Ahead Full", 200),
        ("AheadFlankPercent", "Ahead Flank", 300)
    ];
    private static double[] loads = Steps.Select(step => step.Percent / 100).ToArray();

    internal static void ReadDefaults(IniHandler ini, SettingsValues defaults)
    {
        foreach (var (key, _, percent) in Steps)
            defaults.Set(SectionName, key, SupplyTuning.ReadNumber(ini, SectionName, key, MaxPercent) ?? percent);
    }

    internal static void Apply(SettingsValues values) =>
        loads = Steps.Select(step => SupplyTuning.ConfigValue(values, SectionName, step.Key, step.Percent, MaxPercent) / 100).ToArray();

    internal static void Build(StackPanel panel, SettingsDraft draft)
    {
        var fields = Stack(Steps.AsEnumerable().Reverse().Select(step =>
        {
            string label = $"{step.Label} (%)";
            double percent = SupplyTuning.ConfigValue(draft.Values, SectionName, step.Key, step.Percent, MaxPercent);
            return FormRow(label, draft.NumberEditor(SectionName, step.Key, label, percent, 0, MaxPercent), 230);
        }).ToArray());
        panel.Children.Add(Section("Fuel use by engine order", fields));
    }

    internal static double Load(Vessel ship)
    {
        var command = ship.SpeedCommand.Value;
        if (command is { Kind: SpeedKind.Telegraph, Telegraph: >= -1 and <= 5 })
            return loads[command.Telegraph + 1];

        return Interpolate(command?.GetDemandedSpeedInKnots(ship) ?? 0, ship.VP._telegraphVelocities, loads);
    }

    internal static double Interpolate(double speed, ReadOnlySpan<float> speeds, ReadOnlySpan<double> factors)
    {
        double stop = factors[1];
        if (!double.IsFinite(speed) || speed == 0 || speeds.Length < 2) return stop;
        if (speed < 0)
        {
            double reverse = Math.Abs(speeds[0]);
            return reverse > 0 ? stop + (factors[0] - stop) * Math.Min(-speed / reverse, 1) : stop;
        }

        double lowerSpeed = 0, lowerLoad = stop;
        for (int i = 2; i < Math.Min(speeds.Length, factors.Length); i++)
        {
            double upperSpeed = speeds[i];
            if (!double.IsFinite(upperSpeed) || upperSpeed <= lowerSpeed) continue;
            if (speed <= upperSpeed)
                return lowerLoad + (factors[i] - lowerLoad) * (speed - lowerSpeed) / (upperSpeed - lowerSpeed);
            lowerSpeed = upperSpeed;
            lowerLoad = factors[i];
        }
        return lowerLoad;
    }
}
