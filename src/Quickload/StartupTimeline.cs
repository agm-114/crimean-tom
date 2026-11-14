using System.Diagnostics;
using System.Reflection;

namespace Quickload;

internal static class StartupTimeline
{
    private static readonly DateTime launched = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static readonly double offset = (DateTime.UtcNow - launched).TotalSeconds;
    private static readonly List<Phase> completed = [];
    private static double stageStarted;
    private static string current = "Waiting for startup checks";
    private static Action? handOff;

    internal static double Elapsed => offset + clock.Elapsed.TotalSeconds;
    internal static string Current => current;
    internal static int Count => completed.Count + 1;

    internal static void Initialize()
    {
        var keel = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "SeaPowerKeel")
            ?.GetType("SeaPowerKeel.Keel");
        handOff = keel?.GetMethod("HandOffStartupWindow", BindingFlags.Public | BindingFlags.Static)
            ?.CreateDelegate(typeof(Action)) as Action;
        if (keel?.GetMethod("GetStartupTimings")?.Invoke(null, null) is double[] { Length: >= 2 } timings &&
            timings[0] > 0 && timings[1] >= timings[0] && timings[1] <= Elapsed)
        {
            Advance("Game startup and splashes", timings[0], "Engine and loader startup · internal timings unavailable");
            Advance("Keel mod discovery and checks", timings[1]);
            if (timings.Length >= 3 && timings[2] >= timings[1] && timings[2] <= Elapsed)
                Advance("Quickload patch setup", timings[2]);
        }
        Advance("Waiting for startup checks", Elapsed, stageStarted == 0
            ? "Startup before Quickload · internal timings unavailable" : null);
    }

    internal static void Advance(string next) => Advance(next, Elapsed);

    private static void Advance(string next, double now, string? previous = null)
    {
        if (now > stageStarted) completed.Add(new(previous ?? current, now - stageStarted));
        stageStarted = now;
        current = next;
    }

    internal static Phase[] Finish()
    {
        Advance("Loading game data");
        return completed.ToArray();
    }

    internal static void Reset()
    {
        if (completed.Count == 0 || current == "Waiting for startup checks") return;
        completed.Clear();
        stageStarted = Elapsed;
        current = "Waiting for startup checks";
    }

    internal static void HandOffWindow()
    {
        handOff?.Invoke();
        handOff = null;
    }

    internal static (string Tooltip, float Seconds, float Fraction, bool Indeterminate) ReadStage(int index)
    {
        if (index < completed.Count)
        {
            var phase = completed[index];
            return ($"{phase.Name}\nCompleted in {phase.Seconds:F2} s", (float)phase.Seconds, 1, false);
        }
        double seconds = Elapsed - stageStarted;
        return ($"{current}\nElapsed {seconds:F2} s", (float)seconds, 0, true);
    }

    internal record Phase(string Name, double Seconds);
}
