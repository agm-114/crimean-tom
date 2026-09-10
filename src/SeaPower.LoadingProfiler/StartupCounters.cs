using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace SeaPower.LoadingProfiler;

internal class StartupCounters : IDisposable
{
    private readonly List<(string Name, ProfilerRecorder Recorder)> counters = [];
    internal StartupCounters()
    {
        List<ProfilerRecorderHandle> available = [];
        ProfilerRecorderHandle.GetAvailable(available);
        foreach (var handle in available)
        {
            var description = ProfilerRecorderHandle.GetDescription(handle);
            TraceLog.Write("counter-available", new { name = description.Name, unit = description.UnitType.ToString() });
            if (description.UnitType.ToString() != "TimeNanoseconds") continue;
            ProfilerRecorder recorder = new(handle, 1024);
            if (!recorder.Valid) { recorder.Dispose(); continue; }
            recorder.Start();
            counters.Add((description.Name, recorder));
        }
    }
    internal void Capture()
    {
        foreach (var (name, recorder) in counters)
        {
            List<ProfilerRecorderSample> samples = [];
            recorder.CopyTo(samples);
            TraceLog.Write("early-counter", new { name, count = samples.Count, nanoseconds = samples.Sum(s => s.Value), last = recorder.LastValue });
        }
    }
    public void Dispose()
    {
        foreach (var (_, recorder) in counters) recorder.Dispose();
        counters.Clear();
    }
}
