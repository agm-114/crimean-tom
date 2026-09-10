using System.Linq.Dynamic.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SeaPower.Cli;

internal record SnapshotQuery
{
    public string? JsonPath { get; init; }
    public string? Where { get; init; }
    public string? OrderBy { get; init; }
    public string? Select { get; init; }
    public string? GroupBy { get; init; }
    public string? Having { get; init; }
    public bool Distinct { get; init; }
    public bool Count { get; init; }
    public int? Skip { get; init; }
    public int? Take { get; init; }
    public JsonElement[]? Parameters { get; init; }
    internal static readonly JsonSerializerOptions JsonOptions = new(WireJson.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly ParsingConfig Config = new()
    {
        AllowNewToEvaluateAnyType = false,
        ResolveTypesBySimpleName = false,
        SupportCastingToFullyQualifiedTypeAsString = false,
        AllowEqualsAndToStringMethodsOnObject = false,
        RestrictOrderByToPropertyOrField = true,
    };
    private bool Linq => Where != null || OrderBy != null || Select != null || GroupBy != null || Having != null
        || Distinct || Count || Skip != null || Take != null || Parameters != null;
    internal bool Active => JsonPath != null || Linq;

    internal static SnapshotQuery Read(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new ArgumentException("Query files must not exceed 64 KB.");
        string text = File.ReadAllText(path);
        SnapshotQuery query = Path.GetExtension(path).Equals(".jsonpath", StringComparison.OrdinalIgnoreCase)
            ? new() { JsonPath = text.Trim() }
            : SnapshotData.Parse(text, path).Deserialize<SnapshotQuery>(JsonOptions)
                ?? throw new InvalidDataException("Query file must contain a query object.");
        query.Validate();
        if (!query.Active) throw new ArgumentException("Query file contains no query operations.");
        return query;
    }

    internal void Validate()
    {
        if (JsonPath != null && Linq)
            throw new ArgumentException("Use --query for JSONPath or --where/--order-by/--select for Dynamic LINQ, not both in one command.");
        foreach (string? expression in new[] { JsonPath, Where, OrderBy, Select, GroupBy, Having })
            if (expression != null && (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096))
                throw new ArgumentException("Query expressions must contain 1 to 4096 characters.");
        if (Having != null && GroupBy == null) throw new ArgumentException("--having requires --group-by.");
        if (Skip is < 0 or > 1000000 || Take is < 0 or > 1000000)
            throw new ArgumentException("skip and take must be between 0 and 1000000.");
        if (Parameters?.Any(value => value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) == true)
            throw new ArgumentException("Query parameters must be strings, numbers, booleans or null.");
        foreach (string field in OrderBy?.Split(',') ?? [])
            if (!Regex.IsMatch(field.Trim(), @"\A[A-Za-z_]\w*(\.\w+)*(\s+(asc|ascending|desc|descending))?\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                throw new ArgumentException("--order-by accepts comma-separated property paths with optional asc or desc, not method calls.");
    }

    internal JsonElement Apply(JsonElement snapshot)
    {
        if (!Active) return snapshot;
        JsonObject output = JsonNode.Parse(snapshot.GetRawText()) as JsonObject
            ?? throw new InvalidDataException("Queries require a snapshot object.");
        JsonNode? results;
        if (JsonPath != null)
        {
            using StringReader text = new(snapshot.GetRawText());
            using Newtonsoft.Json.JsonTextReader reader = new(text) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
            JToken document = JToken.Load(reader);
            JArray matches = new(document.SelectTokens(JsonPath));
            results = JsonNode.Parse(matches.ToString(Newtonsoft.Json.Formatting.None));
        }
        else
        {
            string? kind = output["kind"]?.GetValue<string>();
            kind ??= output.ContainsKey("units") ? "units" : output.ContainsKey("controls") ? "controls" : null;
            if (kind is not ("units" or "controls")) throw new InvalidDataException("Dynamic LINQ requires a units or UI controls snapshot.");
            results = output["mode"]?.GetValue<string>() == "delta"
                ? new JsonObject
                {
                    ["added"] = Rows(snapshot.GetProperty("added"), kind),
                    ["updated"] = Rows(snapshot.GetProperty("updated"), kind),
                    ["removed"] = output["removed"]?.DeepClone(),
                }
                : Rows(snapshot.GetProperty(kind), kind);
        }

        foreach (string field in new[] { "controls", "units", "added", "updated", "removed" }) output.Remove(field);
        output["sourceMode"] = output["mode"]?.DeepClone();
        output["mode"] = "query";
        output["query"] = JsonSerializer.SerializeToNode(this, JsonOptions);
        output["results"] = results;
        return JsonSerializer.SerializeToElement(output, WireJson.Options);
    }

    private JsonNode? Rows(JsonElement rows, string kind) => kind == "units" ? Rows<UnitState>(rows) : Rows<UiControl>(rows);

    private JsonNode? Rows<T>(JsonElement rows)
    {
        object[] parameters = Parameters?.Select(value => SnapshotData.ToObject(value)!).ToArray() ?? [];
        IQueryable query = (rows.Deserialize<T[]>(WireJson.Options)
            ?? throw new InvalidDataException("Snapshot has no rows.")).AsQueryable();
        if (Where != null) query = query.Where(Config, Where, parameters);
        if (GroupBy != null)
        {
            query = query.GroupBy(Config, GroupBy, parameters);
            if (Having != null) query = query.Where(Config, Having, parameters);
            query = query.Select(Config, Select ?? "new(Key, Count() as Count)", parameters);
            if (OrderBy != null) query = query.OrderBy(Config, OrderBy);
        }
        else
        {
            if (OrderBy != null) query = query.OrderBy(Config, OrderBy);
            if (Select != null) query = query.Select(Config, Select, parameters);
        }
        if (Distinct) query = query.Distinct();
        if (Skip is { } skip) query = query.Skip(skip);
        if (Take is { } take) query = query.Take(take);
        if (Count) return JsonValue.Create(query.Count());
        if (GroupBy != null || Select != null)
            return JsonSerializer.SerializeToNode(query.Cast<object>().ToArray(), WireJson.Options);
        // Keep fields from newer bridge builds when no projection was requested.
        Dictionary<int, JsonElement> originals = rows.EnumerateArray().ToDictionary(row => row.GetProperty("id").GetInt32());
        return JsonSerializer.SerializeToNode(query.Select(Config, "Id").Cast<int>().Select(id => originals[id]).ToArray(), WireJson.Options);
    }
}
