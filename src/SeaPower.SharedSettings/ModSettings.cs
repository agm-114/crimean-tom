using UnityEngine;

namespace SeaPower.SharedSettings;

internal class ModSettings
{
    private const string ModeSection = "Settings";
    private const string DefaultKey = "UseModDefaults";
    private readonly string path;
    private SettingsValues custom;
    internal SettingsValues Defaults { get; }
    internal bool UseDefaults { get; private set; }
    internal event Action? Changed;
    internal SettingsValues Current => UseDefaults ? Defaults : custom;

    internal ModSettings(string fileName, SettingsValues defaults, Func<SettingsValues>? legacy = null)
    {
        path = Path.Combine(Application.persistentDataPath, "CrimeanTom", "Settings", fileName + ".ini");
        Defaults = defaults;
        custom = defaults.Copy();
        UseDefaults = true;
        try
        {
            if (File.Exists(path))
            {
                using var reader = File.OpenText(path);
                var saved = SettingsValues.Read(reader);
                custom.Overlay(saved, replaceSections: true);
                UseDefaults = saved.Get(ModeSection, DefaultKey, 1) != 0;
            }
            else if (legacy != null) custom.Overlay(legacy());
        }
        catch (IOException error) { Debug.LogWarning($"[{fileName}] Could not read settings: {error.Message}"); }
        catch (UnauthorizedAccessException error) { Debug.LogWarning($"[{fileName}] Could not read settings: {error.Message}"); }
    }

    internal SettingsValues Edit() => custom.Copy();

    internal void Apply(bool useDefaults, SettingsValues values)
    {
        var saved = values.Copy();
        saved.Set(ModeSection, DefaultKey, useDefaults ? 1 : 0);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllLines(temporary, saved.Lines());
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
        custom = saved;
        UseDefaults = useDefaults;
        Changed?.Invoke();
    }
}
