namespace NuclearOption.ScenarioEditor.Core;

public interface IJsonData<T> where T : IJsonData<T>
{
    static abstract T Parse(string json);
    string Serialize();
}

public class JsonDatabase<T>(string fileName, string? path = null) where T : IJsonData<T>, new()
{
    private string? _loadedText;
    private bool _loaded;
    public string Path { get; } = path ?? Locate(fileName);
    public bool HasChanged => !_loaded || ReadText() != _loadedText;

    public T Load()
    {
        var text = ReadText();
        var plan = text is null ? new T() : T.Parse(text);
        _loadedText = text;
        _loaded = true;
        return plan;
    }

    public void Save(T plan)
    {
        if (!_loaded) throw new InvalidOperationException($"{fileName} did not load. Fix the JSON before saving.");
        var current = ReadText();
        if (current != _loadedText)
            throw new IOException($"{fileName} changed outside the editor. Wait for it to reload before trying again.");
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

    private static string Locate(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "tools", "NuclearOption.ScenarioEditor", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
    }
}
