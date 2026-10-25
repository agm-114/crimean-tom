using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuclearOption.ScenarioEditor.Core;

public record UnitUsage(string Type, int Ground, int Aircraft, int Ships, int Factories, int Reserves)
{
    public string Name => UnitCatalog.Name(Type);
    public long? Cost => UnitCatalog.Get(Type).Cost;
    public string CostDisplay => UnitCatalog.Get(Type).CostDisplay;
    public int Units => Ground + Aircraft + Ships;
    public int Total => Units + Factories + Reserves;

    public bool MatchesCategory(string category) => UnitCatalog.Get(Type).Category is { } known && known != "Unknown"
        ? known == category
        : category switch { "Vehicle" => Ground > 0, "Aircraft" => Aircraft > 0, "Ship" => Ships > 0, _ => false };
}

public record Edit(string Location, string Before, string After)
{
    public string BeforeDisplay => UnitCatalog.Name(Before);
    public string AfterDisplay => UnitCatalog.Name(After);
}

public class Factory(JsonObject node)
{
    public string Name => node.Text("UniqueName");
    public string Faction => node.Text("faction");
    public string Building => node.Text("type");
    public string BuildingName => UnitCatalog.Name(Building);
    public string Product => Options.Text("productionType");
    public string ProductName => UnitCatalog.Name(Product);
    public double Seconds => Options["productionTime"]?.GetValue<double>() ?? 0;
    internal JsonObject Options => (JsonObject)node["factoryOptions"]!;
}

public class MissionDocument
{
    private readonly Stack<(string Json, int EditCount)> _undo = new();
    private readonly List<Edit> _edits = [];
    private JsonObject _root;
    private string _saved;

    public MissionDocument(string json)
    {
        _root = Parse(json);
        _saved = Serialize();
    }

    public IReadOnlyList<Edit> Edits => _edits;
    public bool CanUndo => _undo.Count > 0;
    public bool IsDirty => Serialize() != _saved;
    public IReadOnlyList<Factory> Factories => _root.Objects("buildings")
        .Where(b => b["factoryOptions"] is JsonObject &&
            (b.Text("type").StartsWith("factory", StringComparison.OrdinalIgnoreCase) ||
             b["factoryOptions"].Text("productionType").Length > 0))
        .Select(b => new Factory(b)).ToList();

    public IReadOnlyList<UnitUsage> Usage => Slots().GroupBy(s => s.Type, StringComparer.Ordinal)
        .Select(g => new UnitUsage(g.Key, g.Count(s => s.Kind == "Ground"),
            g.Count(s => s.Kind == "Aircraft"), g.Count(s => s.Kind == "Ships"),
            g.Count(s => s.Kind == "Factories"), g.Count(s => s.Kind == "Reserves")))
        .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<Edit> PreviewReplacement(string before, string after)
    {
        ValidateType(after);
        return before == after ? [] : Slots().Where(s => s.Type == before)
            .Select(s => new Edit(s.Location, before, after)).ToList();
    }

    public int ReplaceGlobally(string before, string after)
    {
        var edits = PreviewReplacement(before, after);
        if (edits.Count == 0) return 0;
        Remember();
        foreach (var slot in Slots().Where(s => s.Type == before).ToList())
            slot.Node[slot.Key] = after;
        _edits.AddRange(edits);
        return edits.Count;
    }

    public void Undo()
    {
        if (_undo.TryPop(out var previous))
        {
            _root = Parse(previous.Json);
            _edits.RemoveRange(previous.EditCount, _edits.Count - previous.EditCount);
        }
    }

    public string Serialize() => _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    public void MarkSaved() => _saved = Serialize();

    private void Remember() => _undo.Push((Serialize(), _edits.Count));

    private IEnumerable<UnitSlot> Slots()
    {
        foreach (var (array, kind) in new[] { ("vehicles", "Ground"), ("aircraft", "Aircraft"), ("ships", "Ships") })
            foreach (var node in _root.Objects(array))
                if (node.Text("type") is { Length: > 0 })
                    yield return new(node, "type", kind, $"{kind} / {node.Text("faction")} / {node.Text("UniqueName")}");
        foreach (var factory in Factories)
            if (factory.Product.Length > 0)
                yield return new(factory.Options, "productionType", "Factories", $"Factory / {factory.Faction} / {factory.Name}");
        foreach (var faction in _root.Objects("factions"))
            foreach (var supply in faction.Objects("supplies"))
                if (supply.Text("UnitType") is { Length: > 0 })
                    yield return new(supply, "UnitType", "Reserves", $"Reserve / {faction.Text("factionName")} / {supply.Text("UnitType")}");
    }

    private static JsonObject Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Expected a mission JSON object.");
        if (root["missionSettings"] is not JsonObject || root["JsonVersion"] is not JsonValue)
            throw new JsonException("This file is not a Nuclear Option mission. Choose the scenario JSON, not meta.json.");
        foreach (var array in new[] { "vehicles", "aircraft", "ships", "buildings", "factions" })
            if (root[array] is not null && root[array] is not JsonArray)
                throw new JsonException($"Mission field '{array}' must be an array.");
        return root;
    }

    private static void ValidateType(string type)
    {
        if (string.IsNullOrWhiteSpace(type) || type != type.Trim() || type.Any(char.IsControl))
            throw new ArgumentException("Enter an exact unit type ID without leading spaces or control characters.");
    }

    private record UnitSlot(JsonObject Node, string Key, string Kind, string Location)
    {
        public string Type => Node.Text(Key);
    }
}

internal static class JsonFields
{
    public static string Text(this JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
    public static IEnumerable<JsonObject> Objects(this JsonNode node, string key) =>
        (node[key] as JsonArray)?.OfType<JsonObject>() ?? [];
}
