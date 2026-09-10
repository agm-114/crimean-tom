using System.Diagnostics;

namespace SeaPower.LoadingProfiler;

internal static class TraceDirectory
{
    internal static readonly Process Game = Process.GetCurrentProcess();
    internal static readonly DateTime ProcessStartUtc = Game.StartTime.ToUniversalTime();
    private static readonly DateTime ClockStartUtc = DateTime.UtcNow;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    internal static double Seconds => (ClockStartUtc - ProcessStartUtc).TotalSeconds + Clock.Elapsed.TotalSeconds;
    internal static string Path => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "SeaPowerLoadingProfiler", $"{ProcessStartUtc:yyyyMMdd-HHmmss}-{Game.Id}");

    internal static StreamWriter Open(string filename)
    {
        Directory.CreateDirectory(Path);
        return new(System.IO.Path.Combine(Path, filename), append: true) { AutoFlush = true };
    }
}
