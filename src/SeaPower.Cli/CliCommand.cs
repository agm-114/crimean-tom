using System.Globalization;
using System.Text.Json;

namespace SeaPower.Cli;

internal record CliCommand(UiRequest Request, int Seconds, int TimeoutSeconds, SnapshotQuery Query, string? Input)
{
    internal const string Help = """
        Sea Power CLI

        seapower-cli inspect
        seapower-cli update
        seapower-cli units
        seapower-cli units-update
        seapower-cli query --input FILE --query JSONPATH
        seapower-cli query --input FILE --where EXPRESSION [--order-by EXPRESSION] [--select EXPRESSION]
        seapower-cli units --query-file QUERY.yaml [--format json]
        seapower-cli units --preset moving-units --take 10
        seapower-cli presets
        seapower-cli status [--format yaml|json]
        seapower-cli click --snapshot TOKEN --id ID [--confirm]
        seapower-cli select --snapshot TOKEN --id ID [--index N]
        seapower-cli expand|open --snapshot TOKEN --id ID
        seapower-cli set-text --snapshot TOKEN --id ID --text TEXT
        seapower-cli wait-for --text TEXT [--timeout SECONDS]
        seapower-cli wait-sim [--seconds 60] [--timeout SECONDS]
        seapower-cli pause|resume
        seapower-cli speed 10
        seapower-cli set-speed --speed 10

        All commands default to YAML. Use --format json for JSON; --json is an alias.
        --timeout uses wall-clock seconds. Errors use the requested format on stderr.
        update and units-update compare against their last dump or update, saved
        across CLI invocations. With no matching baseline they return a full dump.
        units exposes actual simulator state, including undetected enemy units.
        inspect, update, units, units-update and wait-for also accept queries:
          --query JSONPATH                 Select from the entire response.
          --where EXPRESSION               Filter rows with Dynamic LINQ.
          --order-by EXPRESSION            Sort rows, or projected group summaries.
          --select EXPRESSION              Project fields, e.g. new(Name, SpeedKnots).
          --group-by EXPRESSION            Group rows; select can use Key, Count(), Sum(...).
          --having EXPRESSION              Filter groups before select.
          --distinct                      Remove duplicate results after select.
          --skip N --take N                Page results after sorting/projection.
          --count                         Return the count after all query operations.
          --params JSON_ARRAY             Bind scalar values as @0, @1, etc.
          --query-file FILE               Read a YAML/JSON query object or .jsonpath text.
          --preset NAME                   Use a built-in query; presets lists them.
        Inline options override file/preset options. Files and presets cannot be combined.
        JSONPath and LINQ options cannot be combined. Queries never filter the stored baseline.
        On deltas, LINQ queries added and updated rows separately; removed IDs remain unfiltered.
        query reads a saved JSON/YAML dump without contacting the game or changing baselines.
        Default timeout: 12 seconds; wait-for: 10; wait-sim: twice its duration,
        bounded to 30 seconds through 24 hours. Ctrl+C cancels the CLI, not actions
        already accepted by the game. The CLI never launches or enables the game.
        """;

    internal static CliCommand Parse(string[] args)
    {
        string action = args.Length == 0 ? "inspect" : args[0].ToLowerInvariant();
        if (action == "speed") action = "set-speed";
        string[] actionOptions = action switch
        {
            "inspect" or "update" or "units" or "units-update" or "status" or "pause" or "resume" or "presets" => [],
            "click" => ["--snapshot", "--id", "--confirm"],
            "select" => ["--snapshot", "--id", "--index"],
            "expand" or "open" => ["--snapshot", "--id"],
            "set-text" => ["--snapshot", "--id", "--text"],
            "wait-for" => ["--text"],
            "query" => ["--input"],
            "wait-sim" => ["--seconds"],
            "set-speed" => ["--speed"],
            _ => throw new ArgumentException($"Unknown command '{action}'. Use --help."),
        };
        string[] queryOptions = ["--query", "--where", "--order-by", "--select", "--group-by", "--having", "--distinct", "--count", "--skip", "--take", "--params"];
        HashSet<string> allowed = ["--json", "--format", "--timeout", .. actionOptions];
        if (action is "inspect" or "update" or "units" or "units-update" or "wait-for" or "query")
            allowed.UnionWith(["--query-file", "--preset", .. queryOptions]);
        Dictionary<string, string> options = [];
        for (int index = 1; index < args.Length; index++)
        {
            string key = args[index];
            string value;
            if (action == "set-speed" && index == 1 && !key.StartsWith("--"))
            {
                value = key;
                key = "--speed";
            }
            else
            {
                if (!allowed.Contains(key)) throw new ArgumentException($"Unknown option '{key}' for {action}.");
                if (key is "--confirm" or "--json" or "--distinct" or "--count") value = "true";
                else if (++index < args.Length && !args[index].StartsWith("--")) value = args[index];
                else throw new ArgumentException($"{key} requires a value.");
            }
            if (!options.TryAdd(key, value)) throw new ArgumentException($"Duplicate option '{key}'.");
        }

        if (options.ContainsKey("--query-file") && options.ContainsKey("--preset"))
            throw new ArgumentException("Use --query-file or --preset, not both.");
        SnapshotQuery basis = options.TryGetValue("--query-file", out string? queryFile) ? SnapshotQuery.Read(queryFile)
            : options.TryGetValue("--preset", out string? preset) ? QueryPresets.Get(preset) : new();
        SnapshotQuery query = basis with
        {
            JsonPath = options.GetValueOrDefault("--query") ?? basis.JsonPath, Where = options.GetValueOrDefault("--where") ?? basis.Where,
            OrderBy = options.GetValueOrDefault("--order-by") ?? basis.OrderBy, Select = options.GetValueOrDefault("--select") ?? basis.Select,
            GroupBy = options.GetValueOrDefault("--group-by") ?? basis.GroupBy, Having = options.GetValueOrDefault("--having") ?? basis.Having,
            Distinct = options.ContainsKey("--distinct") || basis.Distinct, Count = options.ContainsKey("--count") || basis.Count,
            Skip = options.ContainsKey("--skip") ? Number("--skip", 0, 0, 1000000) : basis.Skip,
            Take = options.ContainsKey("--take") ? Number("--take", 0, 0, 1000000) : basis.Take,
            Parameters = options.TryGetValue("--params", out string? parameters)
                ? JsonSerializer.Deserialize<JsonElement[]>(parameters) ?? throw new ArgumentException("--params requires a JSON array.") : basis.Parameters,
        };
        query.Validate();
        if (action == "query" && !query.Active) throw new ArgumentException("query requires --query or a Dynamic LINQ option.");
        string? input = action == "query" ? Required("--input") : null;
        int seconds = Number("--seconds", 60, 1, 86400);
        int defaultTimeout = action switch { "wait-sim" => Math.Clamp(seconds * 2, 30, 86400), "wait-for" => 10, _ => 12 };
        bool controlAction = action is "click" or "select" or "expand" or "open" or "set-text";
        string snapshot = controlAction ? Required("--snapshot") : "";
        if (controlAction) Required("--id");
        string text = action is "set-text" or "wait-for" ? Required("--text", action == "set-text") : "";
        float speed = 1;
        if (action == "set-speed" && (!float.TryParse(Required("--speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out speed)
            || !new float[] { 0.1f, 0.2f, 0.5f, 1, 2, 3, 5, 10, 20, 30, 50, 100 }.Contains(speed)))
            throw new ArgumentException("Speed must be a native step between 0.1 and 100.");
        return new(new(action)
        {
            Snapshot = snapshot, Id = Number("--id", 0, 1, int.MaxValue), Index = Number("--index", -1, 0, int.MaxValue),
            Text = text, Confirm = options.ContainsKey("--confirm"), Speed = speed,
        }, seconds, Number("--timeout", defaultTimeout, 1, 86400), query, input);

        string Required(string key, bool allowEmpty = false) => options.TryGetValue(key, out string? value) && (allowEmpty || !string.IsNullOrWhiteSpace(value))
            ? value : throw new ArgumentException($"{action} requires {key}.");

        int Number(string key, int fallback, int minimum, int maximum)
        {
            if (!options.TryGetValue(key, out string? value)) return fallback;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= minimum && number <= maximum
                ? number : throw new ArgumentException($"{key} must be between {minimum} and {maximum}.");
        }
    }
}
