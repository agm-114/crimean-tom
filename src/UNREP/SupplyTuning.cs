using System.Globalization;
using Noesis;
using SeaPower;
using Settings;
using static UI.NativeUi;

namespace UNREP;

internal static class SupplyTuning
{
    private const string UnitSection = "UNREP";
    private const string ReservesSection = "Reserves";
    private const string PropulsionSection = "Propulsion";
    private const string StopWhenOutOfFuelKey = "StopWhenOutOfFuel";
    private const string EstimatesSection = "ShipEstimates";
    private const string EstimatesEnabled = "Enabled";
    private const double MaxQuantity = 1e9;
    private const double MaxPerTonne = 1e6;
    private const double MaxMultiplier = 100;
    private static IniHandler installed = null!;
    private static ModSettings settings = null!;
    private static readonly (string Key, string Label)[] OptionalReserves =
        [("Water", "Fresh water"), ("Food", "Food"), ("SpareParts", "Spare parts"), ("AviationFuel", "Aviation fuel")];
    private static readonly Dictionary<string, Formula> Formulas = new()
    {
        ["Fuel"] = new("Initial fuel", 1000, 45),
        ["FuelCapacity"] = new("Fuel capacity", 2000, 90),
        ["AviationFuel"] = new("Initial aviation fuel", 0, 4),
        ["AviationFuelCapacity"] = new("Aviation fuel capacity", 0, 8),
        ["Food"] = new("Initial food", 100, 3),
        ["FoodCapacity"] = new("Food capacity", 200, 6),
        ["Water"] = new("Initial fresh water", 100, 3),
        ["WaterCapacity"] = new("Fresh water capacity", 200, 6),
        ["SpareParts"] = new("Initial spare parts", 50, 1),
        ["SparePartsCapacity"] = new("Spare parts capacity", 100, 2),
        ["FuelStandard"] = new("Fuel use at Ahead Standard", 0, 0.65, hourly: true, legacyPrefix: "FuelFull"),
        ["FoodUse"] = new("Food use", 0, 0.006, hourly: true),
        ["WaterProduction"] = new("Fresh water production", 0, 0.04, hourly: true),
        ["SparePartsUse"] = new("Spare parts use", 0, 0.001, hourly: true),
        ["SparePartsPerDCTeam"] = new("Extra parts use per active DC team", 100, 0, hourly: true)
    };

    internal static void Register(IniHandler ini)
    {
        installed = ini;
        var defaults = new SettingsValues();
        foreach (var (key, _) in OptionalReserves)
            defaults.Set(ReservesSection, key, ini.readValue(ReservesSection, key, true) ? 1 : 0);
        defaults.Set(PropulsionSection, StopWhenOutOfFuelKey,
            ini.readValue(PropulsionSection, StopWhenOutOfFuelKey, true) ? 1 : 0);
        defaults.Set(EstimatesSection, EstimatesEnabled, ini.readValue(EstimatesSection, EstimatesEnabled, true) ? 1 : 0);
        foreach (var (prefix, formula) in Formulas)
        {
            foreach (var (suffix, fallback, max, _) in formula.Coefficients())
                defaults.Set(formula.Section, prefix + suffix,
                    formula.Read(ini, formula.Section, prefix, suffix, max) ?? fallback);
        }
        FuelTelegraph.ReadDefaults(ini, defaults);
        settings = new("UNREP", defaults);
        FuelTelegraph.Apply(settings.Current);
        settings.Changed += () => FuelTelegraph.Apply(settings.Current);
        GameplaySettings.Register("UNREP", settings, Build);
    }

    internal static double Value(Vessel ship, string prefix)
    {
        var formula = Formulas[prefix];
        var unit = ship.VP;
        if (UnitValue(formula.AmountSuffix, MaxQuantity) is { } absolute) return absolute;

        double baseAmount = Global(formula.BaseSuffix, formula.Base, MaxQuantity);
        double perTonne = Global(formula.PerTonneSuffix, formula.PerTonne, MaxPerTonne);
        if (UsesEstimates(settings.Current) && baseAmount == formula.Base && perTonne == formula.PerTonne
            && (prefix is "Fuel" or "FuelCapacity" or "AviationFuel" or "AviationFuelCapacity" or "Water" or "WaterCapacity")
            && ReserveEstimates.For(prefix, unit._displacement,
                unit.Simple?._ini?.readValue("AI", "Role", "") ?? "",
                unit.Simple?._ini?.readValue("MainPowerSystem", "Type", "") ?? "") is { } estimate)
            (baseAmount, perTonne) = estimate;

        baseAmount = UnitValue(formula.BaseSuffix, MaxQuantity) ?? baseAmount;
        perTonne = UnitValue(formula.PerTonneSuffix, MaxPerTonne) ?? perTonne;
        double multiplier = UnitValue("Multiplier", MaxMultiplier) ?? Global("Multiplier", 1, MaxMultiplier);
        return Scale(baseAmount, perTonne, unit._displacement, multiplier);

        double? UnitValue(string suffix, double max) =>
            formula.Read(installed, unit._objectIniName, prefix, suffix, max, strict: false)
            ?? formula.Read(unit.Simple?._ini, UnitSection, prefix, suffix, max, strict: false);

        double Global(string suffix, double fallback, double max) =>
            ConfigValue(settings.Current, formula.Section, prefix + suffix, fallback, max);
    }

    internal static double CargoFuelCapacity(Vessel ship)
    {
        const string key = "CargoFuelCapacityKg";
        return ReadNumber(installed, ship.VP._objectIniName, key, MaxQuantity, strict: false)
            ?? ReadNumber(ship.VP.Simple?._ini, UnitSection, key, MaxQuantity, strict: false) ?? 0;
    }

    internal static double AviationFuelCapacity(Vessel ship) =>
        ship._obp._flightDeck?._vehiclesOnBoard.Count > 0 ? Value(ship, "AviationFuelCapacity")
        : ReadNumber(installed, ship.VP._objectIniName, "AviationFuelCapacityKg", MaxQuantity, strict: false)
            ?? ReadNumber(ship.VP.Simple?._ini, UnitSection, "AviationFuelCapacityKg", MaxQuantity, strict: false) ?? 0;

    internal static double Scale(double baseAmount, double perTonne, double displacement, double multiplier) =>
        Math.Clamp((baseAmount + perTonne * (double.IsFinite(displacement) ? Math.Max(0, displacement) : 0))
            * multiplier, 0, MaxQuantity);

    internal static bool ReserveEnabled(string key) => ReserveEnabled(settings.Current, key);
    internal static bool StopWhenOutOfFuel => CutsPropulsion(settings.Current);

    private static bool CutsPropulsion(SettingsValues values) => values.Get(PropulsionSection, StopWhenOutOfFuelKey,
        settings.Defaults.Get(PropulsionSection, StopWhenOutOfFuelKey, 1)) != 0;

    private static bool UsesEstimates(SettingsValues values) =>
        values.Get(EstimatesSection, EstimatesEnabled, settings.Defaults.Get(EstimatesSection, EstimatesEnabled, 1)) != 0;

    private static bool ReserveEnabled(SettingsValues values, string key) =>
        values.Get(ReservesSection, key, settings.Defaults.Get(ReservesSection, key, 1)) != 0;

    private static void Build(StackPanel panel, SettingsDraft draft)
    {
        panel.Children.Add(Section("Optional reserves", Stack(OptionalReserves.Select(reserve =>
            draft.Editor(Check(reserve.Label, ReserveEnabled(draft.Values, reserve.Key),
                enabled => draft.Set(ReservesSection, reserve.Key, enabled ? 1 : 0)))).ToArray())));
        panel.Children.Add(draft.Editor(Check("Stop propulsion when out of fuel", CutsPropulsion(draft.Values),
            enabled => draft.Set(PropulsionSection, StopWhenOutOfFuelKey, enabled ? 1 : 0))));
        FuelTelegraph.Build(panel, draft);
        panel.Children.Add(draft.Editor(Check("Estimate reserves by ship type", UsesEstimates(draft.Values),
            enabled => draft.Set(EstimatesSection, EstimatesEnabled, enabled ? 1 : 0))));
        panel.Children.Add(Text("Reserve amounts and base rates apply when a mission loads."));
        foreach (var (prefix, formula) in Formulas)
        {
            var fields = Stack(formula.Coefficients().Select(field =>
            {
                string key = prefix + field.Suffix;
                double value = ConfigValue(draft.Values, formula.Section, key, field.Default, field.Max);
                return FormRow(field.Label, draft.NumberEditor(formula.Section, key, field.Label, value, 0, field.Max), 230);
            }).ToArray());
            panel.Children.Add(Section(formula.Label, fields, expanded: false));
        }
    }

    internal static double? ReadNumber(IniHandler? ini, string section, string key, double max, double min = 0, bool strict = true)
    {
        if (ini == null || !ini.Data.TryGetValue(section, out var values) || !values.TryGetValue(key, out string raw))
            return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && InRange(value, min, max))
            return value;
        if (strict) throw new FormatException($"Invalid UNREP setting {section}/{key}. Expected {min}–{max}.");
        UnityEngine.Debug.LogWarning($"[UNREP] Ignoring invalid {section}/{key}. Expected {min}–{max}.");
        return null;
    }

    internal static double ConfigValue(SettingsValues values, string section, string key, double fallback, double max)
    {
        fallback = settings.Defaults.Get(section, key, fallback);
        double value = values.Get(section, key, fallback);
        return InRange(value, 0, max) ? value : fallback;
    }

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;

    private class Formula(string label, double baseAmount, double perTonne, bool hourly = false, string? legacyPrefix = null)
    {
        internal string Label { get; } = label;
        internal double Base { get; } = baseAmount;
        internal double PerTonne { get; } = perTonne;
        internal string Section => hourly ? "Consumption" : "Defaults";
        internal string AmountSuffix => hourly ? "KgPerHour" : "Kg";
        internal string BaseSuffix => "Base" + AmountSuffix;
        internal string PerTonneSuffix => hourly ? "KgPerTonnePerHour" : "KgPerTonne";

        internal (string Suffix, double Default, double Max, string Label)[] Coefficients() =>
        [
            (BaseSuffix, Base, MaxQuantity, hourly ? "Base (kg/hour)" : "Base (kg)"),
            (PerTonneSuffix, PerTonne, MaxPerTonne, hourly ? "Per tonne (kg/hour)" : "Per tonne (kg)"),
            ("Multiplier", 1, MaxMultiplier, "Multiplier (×)")
        ];

        internal double? Read(IniHandler? ini, string section, string prefix, string suffix, double max, bool strict = true) =>
            ReadNumber(ini, section, prefix + suffix, max, strict: strict)
            ?? (legacyPrefix == null ? null : ReadNumber(ini, section, legacyPrefix + suffix, max, strict: strict));
    }
}
