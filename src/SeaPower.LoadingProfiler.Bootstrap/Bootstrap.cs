using System.Reflection;
using BepInEx.Logging;
using Mono.Cecil;

namespace SeaPower.LoadingProfiler;

// Loaded before Unity. Keep this assembly free of Unity, game and JSON dependencies.
public class Bootstrap : ILogListener
{
    private static readonly object Gate = new();
    private static StreamWriter? output;
    private static Bootstrap? listener;
    private static Timer? stopTimer;
    public static IEnumerable<string> TargetDLLs => [];
    public static void Patch(AssemblyDefinition assembly) { }
    public static void Initialize()
    {
        try
        {
            output = TraceDirectory.Open("bootstrap.tsv");
            output.WriteLine("utc\tseconds\tcpuSeconds\tthread\tsource\tmessage");
            Write("bootstrap", "Timing listener started; no assembly edits");
            listener = new();
            BepInEx.Logging.Logger.Listeners.Add(listener);
            AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
            AppDomain.CurrentDomain.ProcessExit += ProcessExit;
            stopTimer = new(_ => Stop(), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        }
        catch (Exception error)
        {
            Stop();
            using var log = BepInEx.Logging.Logger.CreateLogSource("Loading Profiler Bootstrap");
            log.LogWarning($"Loading profiler bootstrap unavailable: {error.Message}");
        }
    }
    public static void Finish() => Write("patchers-finished", "");
    public void LogEvent(object sender, LogEventArgs e) => Write(e.Source.SourceName, e.Data?.ToString() ?? "");
    public void Dispose() => Stop();
    private static void AssemblyLoaded(object? sender, AssemblyLoadEventArgs e) => Write("assembly", e.LoadedAssembly.GetName().Name ?? "");
    private static void ProcessExit(object? sender, EventArgs e) => Stop();
    private static string Cell(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private static void Write(string source, string message)
    {
        lock (Gate)
        {
            if (output == null) return;
            try
            {
                output.WriteLine(FormattableString.Invariant($"{DateTime.UtcNow:O}\t{TraceDirectory.Seconds:F6}\t{TraceDirectory.Game.TotalProcessorTime.TotalSeconds:F6}\t{Thread.CurrentThread.ManagedThreadId}\t{Cell(source)}\t{Cell(message)}"));
            }
            catch (IOException) { CloseWriter(); }
            catch (UnauthorizedAccessException) { CloseWriter(); }
        }
    }
    private static void Stop()
    {
        // Remove the BepInEx listener outside Gate to avoid lock inversion with logging.
        var previous = Interlocked.Exchange(ref listener, null);
        if (previous != null) BepInEx.Logging.Logger.Listeners.Remove(previous);
        AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
        AppDomain.CurrentDomain.ProcessExit -= ProcessExit;
        Interlocked.Exchange(ref stopTimer, null)?.Dispose();
        lock (Gate) CloseWriter();
    }
    private static void CloseWriter()
    {
        var writer = output;
        output = null;
        try { writer?.Dispose(); }
        catch (IOException) { }
    }
}
