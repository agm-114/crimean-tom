using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeaPower.Cli;

internal class SnapshotCache(string path, string collection, FileStream gate) : IAsyncDisposable
{
    internal static async Task<SnapshotCache> OpenAsync(string collection, CancellationToken cancellation)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaPowerCli");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, collection + ".json");
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new(path, collection, new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) { await Task.Delay(100, cancellation); }
        }
    }

    internal async Task<JsonElement> UpdateAsync(JsonElement response, string session, bool delta, SnapshotQuery query, CancellationToken cancellation)
    {
        JsonObject current = JsonNode.Parse(response.GetRawText())!.AsObject();
        if (current["identityVersion"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Rebuild the game bridge and restart Sea Power to enable stable snapshot IDs.");
        if (current["truncated"]?.GetValue<bool>() == true)
            throw new InvalidDataException("UI inspection exceeded its node limit. The previous baseline was kept; close some windows and retry.");
        Dictionary<int, JsonNode> rows = Rows(current);
        JsonObject? previous = await ReadAsync(cancellation);
        bool reset = previous == null || previous["session"]?.GetValue<string>() != session
            || !JsonNode.DeepEquals(previous["identityVersion"], current["identityVersion"])
            || !SameMission(previous, current);
        current["session"] = session;
        current["kind"] = collection;
        current["mode"] = "full";
        current["reset"] = reset;
        JsonObject output = current;
        if (delta && !reset)
        {
            Dictionary<int, JsonNode> old = Rows(previous!);
            output = (JsonObject)current.DeepClone();
            output.Remove(collection);
            output["mode"] = "delta";
            output["baseSnapshot"] = previous!["snapshot"]!.DeepClone();
            output["added"] = new JsonArray(rows.Where(row => !old.ContainsKey(row.Key)).Select(row => row.Value.DeepClone()).ToArray());
            output["updated"] = new JsonArray(rows.Where(row => old.TryGetValue(row.Key, out JsonNode? value)
                && !JsonNode.DeepEquals(value, row.Value)).Select(row => row.Value.DeepClone()).ToArray());
            output["removed"] = JsonSerializer.SerializeToNode(old.Keys.Except(rows.Keys), WireJson.Options);
        }

        // Evaluate before saving: a failed query must not consume the next delta.
        JsonElement result = query.Apply(JsonSerializer.SerializeToElement(output, WireJson.Options));
        cancellation.ThrowIfCancellationRequested();
        string temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, current.ToJsonString(WireJson.Options), cancellation);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return result;
    }

    private async Task<JsonObject?> ReadAsync(CancellationToken cancellation)
    {
        try
        {
            JsonObject? saved = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellation)) as JsonObject;
            if (saved == null || !Guid.TryParseExact(saved["snapshot"]?.GetValue<string>(), "N", out _)
                || !Guid.TryParseExact(saved["session"]?.GetValue<string>(), "N", out _)
                || saved["identityVersion"]?.GetValue<int>() != 1) return null;
            if (saved["time"]?.Deserialize<TimeStatus>(WireJson.Options) == null) return null;
            Rows(saved);
            return saved;
        }
        catch (Exception exception) when (exception is FileNotFoundException or JsonException or InvalidOperationException
            or InvalidDataException or ArgumentException or FormatException) { return null; }
    }

    private Dictionary<int, JsonNode> Rows(JsonObject snapshot) => snapshot[collection] is JsonArray rows
        ? rows.ToDictionary(row => row?["id"]?.GetValue<int>() ?? throw new InvalidDataException("Snapshot row has no ID."), row => row!)
        : throw new InvalidDataException($"Helper did not return {collection}.");

    private static bool SameMission(JsonObject previous, JsonObject current)
    {
        TimeStatus? before = previous["time"]?.Deserialize<TimeStatus>(WireJson.Options);
        TimeStatus? now = current["time"]?.Deserialize<TimeStatus>(WireJson.Options);
        return before != null && now != null && before.Available == now.Available
            && before.MissionStartSeconds == now.MissionStartSeconds
            && (!now.Available || now.ElapsedSeconds >= before.ElapsedSeconds);
    }

    public ValueTask DisposeAsync() => gate.DisposeAsync();
}
