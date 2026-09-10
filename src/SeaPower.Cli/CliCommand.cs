using System.Globalization;
using System.Text.Json;

namespace SeaPower.Cli;

internal record CliCommand(GameRequest Request, int Seconds, int TimeoutSeconds, SnapshotQuery Query, string? Input)
{
    internal string? GameDirectory { get; init; }
    internal string? LogFile { get; init; }
    internal string? RequirePattern { get; init; }
    internal string? Scenario { get; init; }
    internal int TailLines { get; init; } = 200;
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
        seapower-cli game-status [--game-dir PATH] [--log-file PATH]
        seapower-cli unity-explorer [status|install|enable|disable] [--confirm] [--game-dir PATH]
        seapower-cli logs [--tail 200] [--game-dir PATH] [--log-file PATH]
        seapower-cli launch --confirm [--require-pattern REGEX] [--timeout 60] [--game-dir PATH]
        seapower-cli launch --confirm --scenario FILE.ini [--timeout 120]
        seapower-cli load-scenario --scenario FILE.ini --confirm
        seapower-cli cancel-scenario --confirm
        seapower-cli menu [PAGE] [--confirm]
        seapower-cli encyclopedia --unit INI_NAME [--variant 0] --confirm [--timeout 120]
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
        game-status and logs work without the bridge or a running game. status reads the mission clock.
        --game-dir overrides SEA_POWER_DIR or the default Steam installation.
        --log-file overrides Player.log; logs also tails BepInEx/LogOutput.log when present.
        launch opens Steam app 1286220 and requires a running process plus fresh Player.log content.
        It refuses an existing game and only matches --require-pattern in new log content.
        --scenario uses an existing INI path, absolute or relative to the current directory.
        It requires an enabled, updated game bridge and submits the native load once the main menu is ready.
        Success means load-requested, not tactical readiness; generated launchers can open their setup menu.
        Launch timeout/cancellation does not close the game. Check game-status before retrying.
        load-scenario works in an already running game, from the main or Scenarios menu.
        cancel-scenario abandons a loaded standalone mission without saving. It refuses campaigns and editor sessions.
        menu without PAGE lists pages and the current window. Pages:
          main, scenarios, campaigns, instant-action, editor, encyclopedia,
          options, mods, manual, continue, exit.
        Navigation starts from the main menu; menu main uses the current page's native Back command.
        encyclopedia, continue, manual and exit require --confirm. Disabled native options stay disabled.
        encyclopedia --unit uses a unit INI name, not a display name; --variant is zero-based.
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
        Default timeout: 12 seconds; encyclopedia: 120; launch: 60 or 120 with --scenario; wait-for: 10; wait-sim: twice its duration,
        bounded to 30 seconds through 24 hours. Ctrl+C cancels the CLI, not actions
        already accepted by the game. Only launch --confirm starts the game.
        unity-explorer manages the BepInEx 5 Mono plugin without the bridge. Changes require --confirm and a closed game.
        install downloads pinned 4.9.0, verifies SHA-256 and installs disabled. It never overwrites an existing installation.
        """;

    internal static CliCommand Parse(string[] args)
    {
        string action = args.Length == 0 ? "inspect" : args[0].ToLowerInvariant();
        if (action == "speed") action = "set-speed";
        string[] actionOptions = action switch
        {
            "inspect" or "update" or "units" or "units-update" or "status" or "pause" or "resume" or "presets" => [],
            "game-status" => ["--game-dir", "--log-file"],
            "unity-explorer" => ["--game-dir", "--confirm", "--operation"],
            "logs" => ["--game-dir", "--log-file", "--tail"],
            "launch" => ["--game-dir", "--log-file", "--require-pattern", "--confirm", "--scenario"],
            "load-scenario" => ["--scenario", "--confirm"],
            "cancel-scenario" => ["--confirm"],
            "menu" => ["--page", "--confirm"],
            "encyclopedia" => ["--unit", "--variant", "--confirm"],
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
            if (action is "set-speed" or "menu" or "unity-explorer" && index == 1 && !key.StartsWith("--"))
            {
                value = key;
                key = action switch { "menu" => "--page", "unity-explorer" => "--operation", _ => "--speed" };
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
        string? scenario = options.ContainsKey("--scenario") ? Path.GetFullPath(Required("--scenario")) : null;
        if (action == "load-scenario" && scenario == null) Required("--scenario");
        if (scenario != null && (!Path.GetExtension(scenario).Equals(".ini", StringComparison.OrdinalIgnoreCase) || !File.Exists(scenario)))
            throw new ArgumentException("--scenario requires an existing mission .ini file.");
        string page = options.ContainsKey("--page") ? Required("--page").ToLowerInvariant() : "";
        string operation = options.ContainsKey("--operation") ? Required("--operation").ToLowerInvariant() : "status";
        if (action == "unity-explorer" && operation is not ("status" or "install" or "enable" or "disable"))
            throw new ArgumentException("UnityExplorer operation must be status, install, enable or disable.");
        if (action == "menu" && page.Length > 0 && !new[] { "main", "scenarios", "campaigns", "instant-action", "editor", "encyclopedia", "options", "mods", "manual", "continue", "exit" }.Contains(page))
            throw new ArgumentException($"Unknown menu page '{page}'. Use --help.");
        int defaultTimeout = action switch { "wait-sim" => Math.Clamp(seconds * 2, 30, 86400), "wait-for" => 10, "encyclopedia" or "unity-explorer" => 120, "launch" => scenario == null ? 60 : 120, _ => 12 };
        if ((action is "launch" or "load-scenario" or "cancel-scenario" or "encyclopedia"
            || action == "unity-explorer" && operation != "status"
            || action == "menu" && page is "encyclopedia" or "continue" or "manual" or "exit") && !options.ContainsKey("--confirm"))
            throw new ArgumentException($"{action} requires --confirm.");
        string? pattern = options.ContainsKey("--require-pattern") ? Required("--require-pattern") : null;
        if (pattern?.Length > 4096) throw new ArgumentException("--require-pattern must not exceed 4096 characters.");
        bool controlAction = action is "click" or "select" or "expand" or "open" or "set-text";
        string snapshot = controlAction ? Required("--snapshot") : "";
        if (controlAction) Required("--id");
        string text = action switch
        {
            "set-text" or "wait-for" => Required("--text", action == "set-text"),
            "unity-explorer" => operation,
            "load-scenario" => scenario!, "menu" => page, "encyclopedia" => Required("--unit"), _ => "",
        };
        float speed = 1;
        if (action == "set-speed" && (!float.TryParse(Required("--speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out speed)
            || !new float[] { 0.1f, 0.2f, 0.5f, 1, 2, 3, 5, 10, 20, 30, 50, 100 }.Contains(speed)))
            throw new ArgumentException("Speed must be a native step between 0.1 and 100.");
        return new(new(action)
        {
            Snapshot = snapshot, Id = Number("--id", 0, 1, int.MaxValue),
            Index = action == "encyclopedia" ? Number("--variant", 0, 0, int.MaxValue) : Number("--index", -1, 0, int.MaxValue),
            Text = text, Confirm = options.ContainsKey("--confirm"), Speed = speed,
        }, seconds, Number("--timeout", defaultTimeout, 1, 86400), query, input)
        {
            GameDirectory = options.ContainsKey("--game-dir") ? Required("--game-dir") : null,
            LogFile = options.ContainsKey("--log-file") ? Required("--log-file") : null,
            RequirePattern = pattern, Scenario = scenario, TailLines = Number("--tail", 200, 1, 5000),
        };

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
