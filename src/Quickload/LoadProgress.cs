using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SeaPower;
using UnityEngine;

namespace Quickload;

internal class LoadProgress
{
    private readonly string[] stages;
    private readonly string path;
    private readonly double[] previous;
    private readonly double[] measured;
    private readonly Stopwatch clock = new();
    private int index;
    private bool started;

    internal LoadProgress(string[] stages)
    {
        this.stages = stages;
        measured = new double[stages.Length];
        var files = Singleton<FileManager>.Instance.Directories.Where(directory => directory.IsChecked)
            .Select(directory => directory.DirectoryInfo.FullName);
        string context = string.Join("\n", new[] { Application.version, Singleton<LanguageResourceHandler>.Instance.Language }
            .Concat(files).Concat(stages));
        using var hash = SHA256.Create();
        string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(context))).Replace("-", "");
        path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SeaPowerQuickload", $"startup-{key}.txt");
        previous = ReadHistory();
    }

    internal void Start()
    {
        started = true;
        clock.Restart();
    }

    internal void SetRunning(bool running)
    {
        if (!started) return;
        if (running) clock.Start();
        else clock.Stop();
    }

    internal void Finish()
    {
        clock.Stop();
        measured[index++] = clock.Elapsed.TotalSeconds;
        clock.Reset();
        started = false;
    }

    internal int StageCount => stages.Length;

    internal (string Tooltip, float Seconds, float Fraction, bool Indeterminate) ReadStage(int stage)
    {
        bool active = started && stage == index;
        double? elapsed = stage < index ? measured[stage] : active ? clock.Elapsed.TotalSeconds : null;
        double? last = previous.Length == stages.Length ? previous[stage] : null;
        string tooltip = $"{stages[stage]}\n{(stage < index ? "Completed in " : "Elapsed ")}";
        tooltip = elapsed is double time ? tooltip + $"{time:F2} s" : $"{stages[stage]}\nWaiting";
        if (last is double lastDuration) tooltip += $"\nLast run {lastDuration:F2} s";
        double duration = stage < index ? measured[stage] : Math.Max(elapsed ?? 0, last ?? 0);
        float fraction = stage < index ? 1 : active && duration > 0 ? (float)Math.Min(.99, elapsed!.Value / duration) : 0;
        return (tooltip, (float)duration, fraction, active && last == null);
    }

    internal (string Timing, string Stage) Read(bool failed)
    {
        double elapsed = clock.Elapsed.TotalSeconds;
        double total = measured.Sum() + elapsed;
        string stage = index < stages.Length ? $"Stage {index + 1}/{stages.Length} · {stages[index]} · {Seconds(elapsed)}" : "";
        string timing = $"Elapsed {Seconds(total)}";
        if (failed) return (timing, stage);
        if (previous.Length == 0) return ($"{timing} · ETA after first complete run", stage);

        double remaining = previous.Skip(index + 1).Sum();
        if (index < previous.Length) remaining += Math.Max(0, previous[index] - elapsed);
        string estimate = remaining <= 0 && index < stages.Length ? "Past estimate" : $"~{Seconds(remaining)} left";
        double lastRun = previous.Sum();
        // Keep progress moving forward even if an individual stage exceeds its last duration.
        double done = previous.Take(index).Sum() + (index < previous.Length ? Math.Min(elapsed, previous[index] * 0.99) : 0);
        float percent = (float)Math.Clamp(100 * done / lastRun, 0, 100);
        return ($"{Math.Floor(percent):0}% · {estimate} · {timing} · Last run {Seconds(lastRun)}", stage);
    }

    internal void Save()
    {
        if (index != stages.Length) return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(temporary, measured.Select(seconds => seconds.ToString("R", CultureInfo.InvariantCulture)));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            UnityEngine.Debug.LogWarning($"[Quickload] Could not save startup timings: {error.Message}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private double[] ReadHistory()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return [];
            string[] lines = File.ReadAllLines(path);
            if (lines.Length != stages.Length) return [];
            var result = new double[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                if (!double.TryParse(lines[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])
                    || !double.IsFinite(result[i]) || result[i] < 0 || result[i] > 86400) return [];
            return result.Sum() > 0 ? result : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string Seconds(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }
}
