namespace Quickload;

internal static class StartupSegments
{
    internal static async Task<Dictionary<string, T>> Read<T>(StartupArchives source, string kind, int format,
        Func<Stream, Dictionary<string, T>?> read)
    {
        var segments = await Task.WhenAll(source.SegmentLayers.Select(layer =>
            source.ReadSegment($"{layer}/{kind}", format, read))).ConfigureAwait(false);
        var records = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
            if (segment != null)
                foreach (var (key, value) in segment) records[key] = value;
        return records;
    }

    internal static void Save<T>(StartupArchives source, string kind, int format, IEnumerable<KeyValuePair<string, T>> records,
        Func<KeyValuePair<string, T>, string?> layer, Func<Stream, Dictionary<string, T>?> read, Action<Stream, Dictionary<string, T>> write)
    {
        foreach (var segment in records.GroupBy(layer))
        {
            if (segment.Key == null) continue;
            source.SaveSegment($"{segment.Key}/{kind}", format,
                segment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), read, write);
        }
    }
}
