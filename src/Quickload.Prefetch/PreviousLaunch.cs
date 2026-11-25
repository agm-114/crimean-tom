using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Quickload;

// The splash can read this index before the game's live FileManager is available.
public class PreviousLaunch
{
    private static readonly double offset = ProcessAge();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static string? indexPath;
    private static bool remembered;
    private static PreviousLaunch? current;
    private readonly string[] names;
    private readonly double[] durations;
    private readonly double[] ends;
    private record Stage(int Index, double Since);
    private Stage stage = new(0, 0);

    public static PreviousLaunch? Current => Volatile.Read(ref current);
    public static double Elapsed => offset + clock.Elapsed.TotalSeconds;
    public int Count => names.Length;
    public double TotalSeconds => ends[^1];

    private static double ProcessAge()
    {
        using var process = Process.GetCurrentProcess();
        return (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds;
    }

    private PreviousLaunch(string[] names, double[] durations)
    {
        this.names = names;
        this.durations = durations;
        ends = new double[names.Length];
        double total = 0;
        for (int i = 0; i < ends.Length; i++) ends[i] = total += durations[i];
    }

    internal static void Open(string assets, string[] mods, bool cacheEnabled)
    {
        if (indexPath != null) return;
        try
        {
            string game = Path.Combine(assets, "..", "Managed", "Seapower-Scripts.dll");
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..", "LocalLow", "Triassic Games", "Sea Power", "usersettings.ini");
            string? language = File.Exists(settings) ? File.ReadLines(settings).FirstOrDefault(line => line.StartsWith("Language=", StringComparison.OrdinalIgnoreCase)) : null;
            string context = $"{File.GetLastWriteTimeUtc(game).Ticks}\n{language}\n{cacheEnabled}\n" + string.Join("\n", mods);
            using var hash = SHA256.Create();
            string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(context))).Replace("-", "");
            indexPath = Path.Combine(StartupPrefetch.DirectoryPath, $"splash-{key}.txt");
            if (!File.Exists(indexPath) || new FileInfo(indexPath).Length > 8192) return;
            string[] lines = File.ReadAllLines(indexPath);
            if (lines.Length < 2 || lines[0].Length != 64 || !lines[0].All(Uri.IsHexDigit)) return;
            string path = Path.Combine(StartupPrefetch.DirectoryPath, $"startup-{lines[0]}.txt");
            var durations = ReadDurations(path, lines.Length - 1);
            if (durations.Length != 0) Volatile.Write(ref current, new PreviousLaunch(lines.Skip(1).ToArray(), durations));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static double[] ReadDurations(string path, int count)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return [];
            string[] lines = File.ReadAllLines(path);
            if (lines.Length != count) return [];
            var result = new double[count];
            for (int i = 0; i < count; i++)
                if (!double.TryParse(lines[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])
                    || !double.IsFinite(result[i]) || result[i] < 0 || result[i] > 86400) return [];
            return result.Sum() > 0 ? result : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    public static void Remember(string key, string[] names)
    {
        if (indexPath == null || remembered) return;
        string temporary = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, new[] { key }.Concat(names));
            if (File.Exists(indexPath)) File.Replace(temporary, indexPath, null);
            else File.Move(temporary, indexPath);
            remembered = true;
        }
        finally { File.Delete(temporary); }
    }

    public void Enter(string name, double since)
    {
        int index = Array.IndexOf(names, name, stage.Index);
        if (index >= 0) Volatile.Write(ref stage, new(index, since));
    }

    public (string Tooltip, float Seconds, float Fraction, bool Indeterminate) ReadStage(int index)
    {
        var active = Volatile.Read(ref stage);
        double fraction = index < active.Index ? 1 : index > active.Index ? 0
            : Math.Clamp((Elapsed - active.Since) / Math.Max(.001, durations[index]), 0, .99);
        return ($"{names[index]}\nLast run {durations[index]:F2} s", (float)durations[index], (float)fraction, false);
    }

    public string Estimate
    {
        get
        {
            var active = Volatile.Read(ref stage);
            double elapsed = Math.Max(0, Elapsed - active.Since);
            double left = ends[^1] - ends[active.Index] + Math.Max(0, durations[active.Index] - elapsed);
            return left > 0 ? $"~{FormatTime(left)} left" : "Past estimate";
        }
    }

    public static string FormatTime(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
    }
}
