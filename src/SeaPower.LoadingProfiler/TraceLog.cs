using Newtonsoft.Json;

namespace SeaPower.LoadingProfiler;

internal static class TraceLog
{
    private static StreamWriter? output;
    internal static bool Active => output != null;
    internal static void Open() => output = TraceDirectory.Open("loading.jsonl");
    internal static void Write(string kind, object? data = null)
    {
        if (output == null) return;
        try
        {
            output.WriteLine(JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, seconds = TraceDirectory.Seconds, kind, data }));
        }
        catch (IOException) { Close(); }
        catch (UnauthorizedAccessException) { Close(); }
    }
    internal static void Close()
    {
        var writer = output;
        output = null;
        try { writer?.Dispose(); }
        catch (IOException) { }
    }
}
