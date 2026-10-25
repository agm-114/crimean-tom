using System.Globalization;
using System.Text.Json;

namespace NuclearOption.ScenarioEditor.Core;

public record UnitType(string Id, string Name, string Category, bool Disabled = false, bool EventOnly = false, long? Cost = null, string Code = "")
{
    public string Display => Name == Id ? Id : $"{Name} [{Id}]";
    public string CostDisplay => Cost?.ToString("C0", CultureInfo.GetCultureInfo("en-US")) ?? "Unknown";
    public bool IsReplacement => !Disabled && !EventOnly && Category is "Vehicle" or "Aircraft" or "Ship" or "Resource";
}

public static class UnitCatalog
{
    private static readonly Dictionary<string, UnitType> s_units = Load();

    public static IReadOnlyCollection<UnitType> All => s_units.Values;
    public static UnitType Get(string id) => s_units.GetValueOrDefault(id) ?? new(id, id, "Unknown");
    public static string Name(string id) => id.Length == 0 ? "No production" : Get(id).Name;
    public static string Display(string id) => id.Length == 0 ? "No production" : Get(id).Display;

    public static IReadOnlyList<UnitType> Options(IEnumerable<string> ids) => ids.Distinct(StringComparer.Ordinal)
        .Select(Get).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Id, StringComparer.Ordinal).ToList();

    public static string ResolveId(string text)
    {
        if (s_units.ContainsKey(text)) return text;
        var matches = s_units.Values.Where(u => u.Display.Equals(text, StringComparison.OrdinalIgnoreCase) ||
            u.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            0 => text,
            1 => matches[0].Id,
            _ => throw new ArgumentException("Several units share that name. Choose an entry that includes its ID.")
        };
    }

    private static Dictionary<string, UnitType> Load()
    {
        using var stream = typeof(UnitCatalog).Assembly.GetManifestResourceStream("NuclearOption.ScenarioEditor.Core.data.json")
            ?? throw new InvalidOperationException("The built-in unit catalogue is missing.");
        var entries = JsonSerializer.Deserialize<UnitType[]>(stream) ?? [];
        return entries.ToDictionary(u => u.Id, StringComparer.Ordinal);
    }
}
