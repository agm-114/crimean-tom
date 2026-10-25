namespace NuclearOption.ScenarioEditor.Core;

public class OverrideFile(string? path = null)
{
    private string? _loadedText;
    private bool _loaded;
    public string Path { get; } = path ?? Locate();
    public bool HasChanged => !_loaded || ReadText() != _loadedText;

    public OverridePlan Load()
    {
        var text = ReadText();
        var plan = text is null ? new OverridePlan() : OverridePlan.Parse(text);
        _loadedText = text;
        _loaded = true;
        return plan;
    }

    public void Save(OverridePlan plan)
    {
        if (!_loaded) throw new InvalidOperationException("Overrides did not load. Fix overrides.json and reload it before saving.");
        var current = ReadText();
        if (current != _loadedText)
            throw new IOException("overrides.json changed outside the editor. Reload it before saving to avoid losing those edits.");
        var text = plan.Serialize();
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, Path, true);
            _loadedText = text;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string? ReadText() => File.Exists(Path) ? File.ReadAllText(Path) : null;

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "tools", "NuclearOption.ScenarioEditor", "overrides.json");
            if (File.Exists(candidate)) return candidate;
        }
        return System.IO.Path.Combine(AppContext.BaseDirectory, "overrides.json");
    }
}
