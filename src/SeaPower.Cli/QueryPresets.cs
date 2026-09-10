namespace SeaPower.Cli;

internal static class QueryPresets
{
    internal static readonly IReadOnlyDictionary<string, SnapshotQuery> All = new Dictionary<string, SnapshotQuery>(StringComparer.OrdinalIgnoreCase)
    {
        ["player-units"] = new() { Where = "PlayerControlled && !Destroyed" },
        ["moving-units"] = new() { Where = "SpeedKnots > 0 && !Destroyed", OrderBy = "SpeedKnots descending" },
        ["buttons"] = new() { Where = "Enabled && Type == \"Button\"", Select = "new(Id, Name, Text)" },
        ["fleet-summary"] = new()
        {
            GroupBy = "new(Nation, Type)", Select = "new(Key.Nation as Nation, Key.Type as Type, Count() as Units)",
            OrderBy = "Units descending, Nation, Type",
        },
    };

    internal static SnapshotQuery Get(string name) => All.TryGetValue(name, out SnapshotQuery? query) ? query
        : throw new ArgumentException($"Unknown query preset '{name}'. Run presets to list the built-in queries.");
}
