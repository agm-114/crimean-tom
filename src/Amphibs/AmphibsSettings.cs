using Noesis;
using SeaPower;
using Settings;
using Path = System.IO.Path;
using static UI.NativeUi;

namespace Amphibs;

internal static class AmphibsSettings
{
    internal const string Wake = "WakeCoefficients";
    private const string ProfileSection = "Amphibs";
    private const string TuningPanel = "ShowTuningPanel";
    private const string DefaultUnits = "DefaultUnits";
    private const string ExtendExistingUnits = "ExtendExistingUnits";
    private const string CreateVariants = "CreateVariants";
    private static readonly string ModDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
    internal static readonly WaterEffectSetting SwimSpeed = new("WaterSpeedKnots", "Swimming speed (knots)", 0, 0.1f, 20);
    internal static readonly WaterEffectSetting HullLength = new("LengthMeters", "Hull length (m)", 0, 0.1f, 30);
    internal static readonly WaterEffectSetting AfloatWeapons = new("WeaponsAfloat", "Allow firing afloat", 0, 0, 1);
    private static readonly WaterEffectSetting[] UnitFields =
        [SwimSpeed, HullLength, AfloatWeapons, .. WaterEffectSettings.All.Where(field => WaterEffectSettings.IsPerUnit(field.Key))];
    private static readonly WaterEffectSetting[] WakeFields = WaterEffectSettings.All
        .Where(field => !WaterEffectSettings.IsPerUnit(field.Key)).ToArray();
    private static readonly List<string> Vehicles = [];

    internal static ModSettings Settings { get; private set; } = null!;
    internal static bool ExtendStockUnits { get; private set; }
    internal static bool UseVariants { get; private set; }
    internal static IReadOnlyList<string> DefaultVehicles => Vehicles;
    internal static bool ShowTuningPanel => !Settings.UseDefaults && Settings.Current.Get(Wake, TuningPanel) != 0;

    internal static void Register()
    {
        var defaults = new SettingsValues();
        defaults.Set(DefaultUnits, ExtendExistingUnits, 1);
        defaults.Set(DefaultUnits, CreateVariants, 0);
        var ini = Utils.openIniFile(Path.Combine(ModDirectory, "amphibious-variants.ini"), logErrors: false, useCaching: false);
        foreach (var field in WakeFields) defaults.Set(Wake, field.Key, field.ValidOrDefault(ini?.readValue(Wake, field.Key, field.Default) ?? field.Default));
        defaults.Set(Wake, TuningPanel, ini?.readValue(Wake, TuningPanel, false) == true ? 1 : 0);
        string extensions = Path.Combine(ModDirectory, "unit_extensions");
        if (Directory.Exists(extensions))
        {
            foreach (string file in Directory.EnumerateFiles(extensions, "*.ini").OrderBy(file => file, StringComparer.Ordinal))
            {
                IniHandler.ReadModifierDirectives(file, out var targets, out _);
                foreach (string target in targets.Where(target => target.StartsWith("land_units/", StringComparison.OrdinalIgnoreCase)))
                {
                    string id = Path.GetFileNameWithoutExtension(target);
                    if (Vehicles.Contains(id) || !ReadUnit(defaults, id, Utils.openIniFile("/land_units", $"/{id}.ini", logErrors: false))) continue;
                    Vehicles.Add(id);
                }
            }
        }
        Settings = new("Amphibs", defaults, ReadLegacy);
        ExtendStockUnits = Settings.Current.Get(DefaultUnits, ExtendExistingUnits, 1) != 0;
        UseVariants = Settings.Current.Get(DefaultUnits, CreateVariants) != 0;
        GameplaySettings.Register("Amphibs", Settings, Build);
    }

    internal static bool ReadUnit(SettingsValues values, string id, IniHandler? ini)
    {
        if (ini?.doesSectionExist(ProfileSection) != true) return false;
        float speed = ini.readValue(ProfileSection, SwimSpeed.Key, 0f);
        float length = ini.readValue(ProfileSection, HullLength.Key, 0f);
        if (SwimSpeed.ValidOrDefault(speed) == 0 || HullLength.ValidOrDefault(length) == 0) return false;
        foreach (var field in UnitFields)
        {
            float value = field == AfloatWeapons
                ? (ini.readValue(ProfileSection, field.Key, false) ? 1 : 0)
                : ini.readValue(ProfileSection, field.Key, field.Default);
            values.Set(id, field.Key, field.ValidOrDefault(value));
        }
        return true;
    }

    internal static float Value(string section, WaterEffectSetting field, double fallback = double.NaN)
    {
        double defaultValue = double.IsNaN(fallback) ? Settings.Defaults.Get(section, field.Key, field.Default) : fallback;
        if (Settings.UseDefaults) return (float)defaultValue;
        return field.ValidOrDefault(Settings.Current.Get(section, field.Key, defaultValue), (float)defaultValue);
    }

    private static SettingsValues ReadLegacy()
    {
        string path = Path.Combine(ModDirectory, "water-effects.ini");
        var legacy = new SettingsValues();
        if (!File.Exists(path)) return legacy;
        using var reader = File.OpenText(path);
        var old = SettingsValues.Read(reader);
        foreach (string section in old.SectionNames.OrderByDescending(section => section.StartsWith("amphibs_", StringComparison.OrdinalIgnoreCase)))
        {
            string id = section.StartsWith("amphibs_", StringComparison.OrdinalIgnoreCase) ? section["amphibs_".Length..] : section;
            foreach (var entry in old.Entries(section)) legacy.Set(id, entry.Key, entry.Value);
        }
        return legacy;
    }

    private static void Build(StackPanel panel, SettingsDraft draft)
    {
        panel.Children.Add(Section("Default units (restart required)", Stack(
            draft.Editor(Check("Extend existing units", draft.Values.Get(DefaultUnits, ExtendExistingUnits, 1) != 0,
                value => draft.Set(DefaultUnits, ExtendExistingUnits, value ? 1 : 0))),
            draft.Editor(Check("Create variants", draft.Values.Get(DefaultUnits, CreateVariants) != 0,
                value => draft.Set(DefaultUnits, CreateVariants, value ? 1 : 0))))));
        panel.Children.Add(draft.Editor(Check("Show live water-effects panel", draft.Values.Get(Wake, TuningPanel) != 0,
            value => draft.Set(Wake, TuningPanel, value ? 1 : 0))));
        if (Vehicles.Count > 0)
        {
            var fields = new StackPanel();
            var choice = Choice(Vehicles, UnitName, changed: ShowVehicle);
            panel.Children.Add(Section("Vehicle settings", Stack(FormRow("Vehicle", choice, 165), fields)));
            ShowVehicle(Vehicles[0]);

            void ShowVehicle(string id)
            {
                fields.Children.Clear();
                foreach (var field in UnitFields) fields.Children.Add(Editor(draft, id, field));
            }
        }
        foreach (var group in WakeFields.GroupBy(field => field.Group))
        {
            var fields = Stack(group.OrderByDescending(field => field.IsToggle).Select(field => Editor(draft, Wake, field)).ToArray());
            panel.Children.Add(Section(group.Key, fields, expanded: false));
        }
    }

    private static FrameworkElement Editor(SettingsDraft draft, string section, WaterEffectSetting field)
    {
        double fallback = Settings.Defaults.Get(section, field.Key, field.Default);
        float value = field.ValidOrDefault(draft.Values.Get(section, field.Key, fallback), (float)fallback);
        if (field.IsToggle || field == AfloatWeapons)
            return draft.Editor(Check(field.Label, value != 0, enabled => draft.Set(section, field.Key, enabled ? 1 : 0)));

        // Convert the float limits to their decimal representation for display.
        var input = draft.NumberEditor(section, field.Key, field.Label,
            (double)(decimal)value, (double)(decimal)field.Min, (double)(decimal)field.Max);
        return FormRow(field.Label, input, 245);
    }

    private static string UnitName(string id) => Singleton<UnitMetadataProvider>.InstanceExists()
        && Singleton<UnitMetadataProvider>.Instance.UnitData is { } units && units.TryGetValue(("land_units", id), out var entry)
        ? entry.Item2.DefaultName : id;
}
