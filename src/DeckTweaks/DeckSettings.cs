using Noesis;
using Settings;
using Path = System.IO.Path;
using static UI.NativeUi;

namespace DeckTweaks;

internal static class DeckSettings
{
    private const string General = "Defaults";
    internal const double Maximum = 86400;
    private static readonly (string Key, string Label, double Minimum)[] Fields =
    [
        ("MountSeconds", "Base mounting time (s)", 0.001),
        ("MountSecondsPerKg", "Mounting time per kg (s)", 0),
        ("WeaponTimeMultiplier", "Weapon time multiplier", 0.001),
        ("RefuelFixedSeconds", "Base refueling time (s)", 0.001),
        ("RefuelSecondsPerKg", "Refueling time per kg (s)", 0),
        ("UnloadMultiplier", "Unloading time multiplier", 0.001),
    ];
    internal static ModSettings Settings { get; private set; } = null!;

    internal static void Register()
    {
        using var resource = typeof(Plugin).Assembly.GetManifestResourceStream("rearm-times.ini")!;
        using var reader = new StreamReader(resource);
        Settings = new("DeckTweaks", SettingsValues.Read(reader), ReadLegacy);
        GameplaySettings.Register("Deck Tweaks", Settings, Build);
    }

    internal static double Rate(string section, string key, double fallback) =>
        Rate(Settings.Current, section, key, fallback);

    private static double Rate(SettingsValues values, string section, string key, double fallback)
    {
        double value = values.Get(section, key, fallback);
        bool allowZero = section == General && (key is "MountSecondsPerKg" or "RefuelSecondsPerKg");
        return value <= Maximum && (value > 0 || value == 0 && allowZero) ? value : fallback;
    }

    private static SettingsValues ReadLegacy()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "rearm-times.ini");
        if (!File.Exists(path)) return new();
        using var reader = File.OpenText(path);
        return SettingsValues.Read(reader);
    }

    private static void Build(StackPanel panel, SettingsDraft draft)
    {
        foreach (var (key, label, minimum) in Fields)
        {
            double value = Rate(draft.Values, General, key, Settings.Defaults.Get(General, key));
            panel.Children.Add(FormRow(label,
                draft.NumberEditor(General, key, label, Math.Max(minimum, value), minimum, Maximum), 225));
        }
        DeckOverrideEditor.AddSections(panel, draft);
    }
}
