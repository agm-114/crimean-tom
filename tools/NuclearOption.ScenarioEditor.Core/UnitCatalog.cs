using System.Globalization;
using System.Text.Json;

namespace NuclearOption.ScenarioEditor.Core;

public record UnitType(string Id, string Name, string Category, bool Disabled = false, bool EventOnly = false, long? Cost = null, string Code = "", long? MaxCost = null)
{
    public string Display => Name == Id ? Id : $"{Name} [{Id}]";
    public string CostDisplay => Cost is { } low && MaxCost is { } high && high != low
        ? FormattableString.Invariant($"${low / 1_000_000m:0.###}m - ${high / 1_000_000m:0.###}m")
        : Cost?.ToString("C0", CultureInfo.GetCultureInfo("en-US")) ?? "Unknown";
    public bool IsReplacement => !Disabled && !EventOnly && Category is "Vehicle" or "Aircraft" or "Ship" or "Resource";
}

public static class UnitCatalog
{
    private static readonly Dictionary<string, UnitType> s_units = Load();
    private static Dictionary<string, MergedUnit> s_merged = new(StringComparer.Ordinal);
    private static Dictionary<string, string> s_members = new(StringComparer.Ordinal);

    public static IReadOnlyCollection<UnitType> RawUnits => s_units.Values;
    public static IReadOnlyCollection<UnitType> All => s_units.Values.Where(u => !s_members.ContainsKey(u.Id))
        .Concat(s_merged.Keys.Select(Get)).ToList();
    public static UnitType GetRaw(string id) => s_units.GetValueOrDefault(id) ?? new(id, id, "Unknown");
    public static string CanonicalId(string id) => s_members.GetValueOrDefault(id) ?? id;
    public static bool IsMerged(string id) => s_merged.ContainsKey(CanonicalId(id));

    public static UnitType Get(string id)
    {
        id = CanonicalId(id);
        if (!s_merged.TryGetValue(id, out var merged)) return GetRaw(id);
        var variants = new[] { GetRaw(merged.BDF), GetRaw(merged.PALA) };
        var costs = variants.Where(u => u.Cost.HasValue).Select(u => u.Cost!.Value).ToList();
        return new(id, merged.Name, merged.Category, variants.All(u => u.Disabled), variants.All(u => u.EventOnly),
            costs.Count > 0 ? costs.Min() : null, merged.Code, costs.Count > 0 ? costs.Max() : null);
    }

    public static void SetMergedUnits(MergedUnits data)
    {
        data.Validate();
        var merged = data.Units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var members = data.Units.SelectMany(u => new[] { (Variant: u.BDF, u.Id), (Variant: u.PALA, u.Id) })
            .ToDictionary(x => x.Variant, x => x.Id, StringComparer.Ordinal);
        s_merged = merged;
        s_members = members;
    }

    public static string ReplacementFor(string id, string faction)
    {
        id = CanonicalId(id);
        if (s_merged.TryGetValue(id, out var merged)) return merged.VariantFor(faction);
        if (id.StartsWith("merged:", StringComparison.Ordinal)) throw new ArgumentException($"Unknown merged unit '{id}'.");
        return id;
    }

    public static string CostDetails(string id) => s_merged.TryGetValue(CanonicalId(id), out var merged)
        ? $"BDF: {GetRaw(merged.BDF).CostDisplay}\nPALA: {GetRaw(merged.PALA).CostDisplay}" : Get(id).CostDisplay;
    public static string Name(string id) => id.Length == 0 ? "No production" : Get(id).Name;
    public static string Display(string id) => id.Length == 0 ? "No production" : Get(id).Display;

    public static IReadOnlyList<UnitType> Options(IEnumerable<string> ids) => ids.Select(CanonicalId).Distinct(StringComparer.Ordinal)
        .Select(Get).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Id, StringComparer.Ordinal).ToList();

    public static string ResolveId(string text) => CanonicalId(Resolve(text, RawUnits.Concat(s_merged.Keys.Select(Get))));
    public static string ResolveRawId(string text) => Resolve(text, RawUnits);

    private static string Resolve(string text, IEnumerable<UnitType> choices)
    {
        if (s_units.ContainsKey(text) || s_merged.ContainsKey(text)) return text;
        var matches = choices.Where(u => u.Display.Equals(text, StringComparison.OrdinalIgnoreCase) ||
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
