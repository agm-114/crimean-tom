using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace SeaPower.GameBridge;

// Optional integration: do not load Keel or require its DLL in non-Keel installs.
internal static class KeelInterop
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type? api;
    private static Type? hostType;
    private static MonoBehaviour? host;

    internal static KeelState Read()
    {
        api ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "SeaPowerKeel")
            ?.GetType("SeaPowerKeel.Keel");
        if (api == null) return new(false, null, null, null, false, null);
        if (api.GetMethod("GetUiState", Any)?.Invoke(null, null) is { } state)
            return new(true, ReadProperty<string>(state, "Version"), ReadProperty<bool?>(state, "StartupComplete"),
                ReadProperty<bool?>(state, "ReportOpen"), api.GetMethod("SetReportOpen", Any) != null,
                ReadProperty<string>(state, "WaitingFor"));
        hostType ??= api.Assembly.GetType("SeaPowerKeel.KeelHost");
        if (host == null && hostType != null) host = Resources.FindObjectsOfTypeAll(hostType).OfType<MonoBehaviour>().FirstOrDefault();
        string? version = api.GetField("Version", Any)?.GetValue(null)?.ToString();
        bool? complete = host == null ? null : hostType?.GetField("done", Any)?.GetValue(host) as bool?;
        bool? open = host == null ? null : hostType?.GetField("showWindow", Any)?.GetValue(host) as bool?;
        bool supported = complete.HasValue && open.HasValue && hostType?.GetMethod("ToggleWindow", Any) != null;
        string? waiting = api.Assembly.GetType("SeaPowerKeel.Loading.LoadingWindow")?.GetField("WaitingFor", Any)?.GetValue(null) as string;
        return new(true, version, complete, open, supported, waiting);
    }

    private static T? ReadProperty<T>(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source) is T value ? value : default;

    internal static void EnsureReady()
    {
        var state = Read();
        if (!state.Present) return;
        if (!state.Supported) throw new InvalidOperationException("Keel is present but its UI state cannot be checked. Use keel status and logs before interacting.");
        if (state.StartupComplete != true) throw new InvalidOperationException("R.A.T.S. Keel is still starting mods. Use keel status to check progress.");
        if (state.ReportOpen == true) throw new InvalidOperationException("R.A.T.S. Keel's report covers the game UI. Use keel close before interacting.");
    }

    internal static object Execute(string operation)
    {
        var state = Read();
        if (operation is "" or "status") return new { keel = state };
        if (!state.Present) throw new InvalidOperationException("R.A.T.S. Keel is not running in this game.");
        if (operation == "report")
        {
            if (api?.GetMethod("GetReport", Any) is { } report) return new { keel = state, text = report.Invoke(null, null) as string };
            string? path = hostType?.GetField("reportPath", Any)?.GetValue(host) as string;
            return new { keel = state, path, text = path != null && File.Exists(path) ? File.ReadAllText(path) : null };
        }
        if (operation is not ("open" or "close")) throw new ArgumentException("Use keel status, report, open or close.");
        if (!state.Supported) throw new InvalidOperationException("This Keel version does not expose the expected report controls.");
        if (api?.GetMethod("SetReportOpen", Any) is { } setReport)
        {
            setReport.Invoke(null, [operation == "open"]);
            return new { ok = true, action = "keel", operation, keel = Read() };
        }
        if (host == null) throw new InvalidOperationException("Keel's report host is unavailable.");
        if (state.ReportOpen != (operation == "open"))
            hostType!.GetMethod("ToggleWindow", Any)!.Invoke(host, null);
        return new { ok = true, action = "keel", operation, keel = Read() };
    }
}

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal record KeelState(bool Present, string? Version, bool? StartupComplete, bool? ReportOpen, bool Supported, string? WaitingFor);
