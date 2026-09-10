using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SeaPower;
using UnityEngine;

namespace SeaPower.StartupTweaks;

internal class LoadLane(string[] keys)
{
    internal string[] Keys { get; } = keys;
    internal Dictionary<string, double> Completed { get; } = [];
    internal string? Current { get; private set; }
    internal string File { get; set; } = "";
    internal bool Failed { get; set; }
    private readonly Stopwatch clock = new();
    internal double Elapsed => clock.Elapsed.TotalSeconds;
    internal bool Done => Completed.Count == Keys.Length && !Failed;

    internal void Begin(string key)
    {
        Current = key;
        File = "";
        clock.Restart();
    }
    internal void End(bool success = true)
    {
        clock.Stop();
        if (success && Current != null) Completed[Current] = Elapsed;
        else Failed = true;
        Current = null;
        File = "";
    }
    internal double? Remaining(IReadOnlyDictionary<string, double> previous)
    {
        double remaining = 0;
        foreach (string key in Keys.Where(key => !Completed.ContainsKey(key)))
        {
            if (!previous.TryGetValue(key, out double expected)) return null;
            remaining += Math.Max(0, expected - (key == Current ? Elapsed : 0));
        }
        return remaining;
    }
}

internal class LoadProgress
{
    internal LoadLane Data { get; }
    internal LoadLane Voice { get; }
    internal LoadLane Extras { get; }
    internal LoadLane Music { get; }
    internal LoadLane[] Lanes => [Data, Voice, Extras, Music];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<string, double> previous = [];
    private readonly string path;
    private bool saved;
    internal double Elapsed => clock.Elapsed.TotalSeconds;

    internal LoadProgress(string[] stages, string[] music)
    {
        Data = new(stages.Where(stage => stage is not ("VoiceMessage_LoadClips" or "WriteTacViewDatabase")).ToArray());
        Voice = new(stages.Where(stage => stage == "VoiceMessage_LoadClips").ToArray());
        Extras = new(stages.Where(stage => stage == "WriteTacViewDatabase").ToArray());
        Music = new(music);
        string context = string.Join("\n", new[] { "3", Application.version, Singleton<LanguageResourceHandler>.Instance.Language }
            .Concat(Singleton<FileManager>.Instance.Directories.Where(dir => dir.IsChecked).Select(dir => dir.DirectoryInfo.FullName))
            .Concat(stages).Concat(music));
        using var hash = SHA256.Create();
        string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(context))).Replace("-", "");
        path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SeaPowerStartupTweaks", key + ".json");
        try
        {
            if (System.IO.File.Exists(path) && new FileInfo(path).Length <= 65536)
                previous = JsonConvert.DeserializeObject<Dictionary<string, double>>(System.IO.File.ReadAllText(path)) ?? [];
            if (previous.Values.Any(value => double.IsNaN(value) || double.IsInfinity(value) || value < 0)) previous.Clear();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StartupTweaks.Log($"Timing history unavailable: {error.Message}");
        }
    }

    internal string Eta()
    {
        var lanes = Lanes;
        if (lanes.Any(item => item.Failed)) return "Loading failed";
        if (lanes.All(item => item.Done)) return "Ready";
        if (lanes.Any(item => item.Current is { } key && previous.TryGetValue(key, out double expected) && item.Elapsed >= expected))
            return "ETA exceeded";
        if (lanes.Any(item => item.Remaining(previous) == null)) return "ETA unavailable on first load";
        double remaining = Data.Remaining(previous)!.Value + Voice.Remaining(previous)!.Value
            + Math.Max(Extras.Remaining(previous)!.Value, Music.Remaining(previous)!.Value);
        return $"~{Seconds(remaining)} remaining";
    }

    internal double? Percent
    {
        get
        {
            if (Lanes.All(lane => lane.Done)) return 100;
            if (Lanes.SelectMany(lane => lane.Keys).Any(key => !previous.ContainsKey(key))) return null;
            double total = Lanes.SelectMany(lane => lane.Keys).Sum(key => previous[key]);
            double done = Lanes.Sum(lane => lane.Completed.Keys.Sum(key => previous[key])
                + (lane.Current is { } key ? Math.Min(lane.Elapsed, previous[key] * 0.99) : 0));
            return total > 0 ? Math.Min(99, 100 * done / total) : null;
        }
    }
    internal static string Seconds(double value) => value < 60 ? $"{value:0.0}s" : $"{(int)(value / 60)}m {value % 60:00}s";

    internal void SaveIfComplete()
    {
        if (saved || Lanes.Any(lane => !lane.Done)) return;
        saved = true;
        clock.Stop();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var times = Lanes.SelectMany(lane => lane.Completed).ToDictionary(pair => pair.Key, pair => pair.Value);
            System.IO.File.WriteAllText(temporary, JsonConvert.SerializeObject(times));
            if (System.IO.File.Exists(path)) System.IO.File.Replace(temporary, path, null);
            else System.IO.File.Move(temporary, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupTweaks.Log($"Could not save load timings: {error.Message}");
        }
        finally
        {
            try { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
