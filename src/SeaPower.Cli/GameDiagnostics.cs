using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SeaPower.Cli;

internal class GameDiagnostics(CliCommand command)
{
    private readonly string directory = ResolveDirectory(command.GameDirectory);
    internal static string ResolveDirectory(string? directory) => Path.GetFullPath(directory
        ?? Environment.GetEnvironmentVariable("SEA_POWER_DIR")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Sea Power");
    private readonly string playerLog = Path.GetFullPath(command.LogFile ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Triassic Games", "Sea Power", "Player.log"));
    private string Executable => Path.Combine(directory, "Sea Power.exe");
    private string BepInExLog => Path.Combine(directory, "BepInEx", "LogOutput.log");

    internal async Task<JsonElement> ExecuteAsync(CancellationToken cancellation) => command.Request.Action switch
    {
        "game-status" => JsonSerializer.SerializeToElement(new
        {
            gameDirectory = directory, executable = Executable, executableExists = File.Exists(Executable),
            processIds = ProcessIds(), playerLog = Describe(playerLog), bepInExLog = Describe(BepInExLog),
        }, WireJson.Options),
        "logs" => await LogsAsync(cancellation),
        "launch" => await LaunchAsync(cancellation),
        _ => throw new ArgumentException("Unknown game diagnostic command."),
    };

    private async Task<JsonElement> LogsAsync(CancellationToken cancellation)
    {
        if (!File.Exists(playerLog)) throw new FileNotFoundException($"Player.log was not found at '{playerLog}'.");
        List<object> logs = [];
        foreach (string path in new[] { playerLog, BepInExLog }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            using StreamReader reader = OpenLog(path);
            Queue<string> lines = new();
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > command.TailLines) lines.Dequeue();
            }
            logs.Add(new { file = Describe(path), lines = lines.ToArray() });
        }
        return JsonSerializer.SerializeToElement(new { logs }, WireJson.Options);
    }

    private async Task<JsonElement> LaunchAsync(CancellationToken cancellation)
    {
        if (!command.Request.Confirm) throw new ArgumentException("launch requires --confirm.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sea Power launch requires Windows and Steam.");
        if (!File.Exists(Executable)) throw new FileNotFoundException($"Sea Power was not found at '{directory}'. Use --game-dir or SEA_POWER_DIR.");
        if (ProcessIds().Length != 0) throw new InvalidOperationException("Sea Power is already running. launch will not stop or restart it.");
        if (command.Scenario != null)
        {
            string bridge = Path.Combine(directory, "Sea Power_Data", "StreamingAssets", "SeaPowerGameBridge");
            if (!File.Exists(Path.Combine(bridge, "SeaPower.GameBridge.dll")) || !File.Exists(Path.Combine(bridge, "enabled.txt")))
                throw new InvalidOperationException("--scenario requires SeaPower.GameBridge.dll and enabled.txt in StreamingAssets/SeaPowerGameBridge. Deploy and enable the bridge first.");
        }
        Regex? required = command.RequirePattern is { } pattern
            ? new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) : null;
        string before = File.Exists(playerLog) ? await ReadLogAsync(playerLog, cancellation) : "";
        cancellation.ThrowIfCancellationRequested();
        DateTime launchUtc = DateTime.UtcNow;
        using Process? steam = Process.Start(new ProcessStartInfo("steam://run/1286220") { UseShellExecute = true });
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int[] processes = ProcessIds();
            if (processes.Length != 0 && File.Exists(playerLog) && File.GetLastWriteTimeUtc(playerLog) >= launchUtc)
            {
                string current = await ReadLogAsync(playerLog, cancellation);
                string fresh = current.StartsWith(before, StringComparison.Ordinal) ? current[before.Length..] : current;
                if (!string.IsNullOrWhiteSpace(fresh) && (required == null || required.IsMatch(fresh)))
                {
                    JsonElement? scenario = command.Scenario is { } path
                        ? await new GameClient().LoadScenarioAsync(path, launchUtc, cancellation) : null;
                    return JsonSerializer.SerializeToElement(new
                    {
                        ok = true, action = "launch", launchUtc, processIds = processes,
                        playerLog = Describe(playerLog), requiredPattern = command.RequirePattern, scenario,
                    }, WireJson.Options);
                }
            }
            await Task.Delay(500, cancellation);
        }
    }

    internal static int[] ProcessIds()
    {
        List<int> ids = [];
        foreach (Process process in Process.GetProcessesByName("Sea Power"))
        {
            using (process)
                if (!process.HasExited) ids.Add(process.Id);
        }
        return ids.ToArray();
    }

    private static object Describe(string path)
    {
        FileInfo file = new(path);
        return new { path, exists = file.Exists, lastWriteUtc = file.Exists ? (DateTime?)file.LastWriteTimeUtc : null };
    }

    private static StreamReader OpenLog(string path) => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    private static async Task<string> ReadLogAsync(string path, CancellationToken cancellation)
    {
        using StreamReader reader = OpenLog(path);
        return await reader.ReadToEndAsync(cancellation);
    }
}
