using System.IO;
using System.Text.Json;

namespace NuclearOption.ScenarioEditor;

public class EditorSettings
{
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NuclearOptionScenarioEditor", "settings.json");

    public string? LastSource { get; set; }
    public string UnitMode { get; set; } = "Vehicle";
    public Dictionary<string, string> Outputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static EditorSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(s_path)) ?? new();
            settings.Outputs = new(settings.Outputs ?? [], StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(s_path)!);
        var temporary = s_path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, s_path, true);
    }
}
