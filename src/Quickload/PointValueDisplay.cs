using System.Globalization;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeapowerUI;
using Task = System.Threading.Tasks.Task;

namespace Quickload;

[HarmonyPatch]
internal static class PointValueDisplay
{
    private static readonly Action<MuseumEntry, string> notify = AccessTools.MethodDelegate<Action<MuseumEntry, string>>(
        AccessTools.Method(typeof(MuseumEntry), "NotifyPropertyChanged"));
    private static readonly HashSet<MuseumObjectEntry> visible = [];
    private static Dictionary<string, int> previous = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, int> current = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, float> previousAmmo = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, float> currentAmmo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<MuseumWeaponEntry> visibleAmmo = [];
    private static string? path;

    internal static void Start(string? profile)
    {
        path = Path.Combine(StartupPrefetch.DirectoryPath, "display-points-" + (profile ?? "native") + ".json");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 8 * 1024 * 1024)
            {
                var saved = JObject.Parse(File.ReadAllText(path));
                previous = saved["Units"]?.ToObject<Dictionary<string, int>>() ?? previous;
                previousAmmo = saved["Ammo"]?.ToObject<Dictionary<string, float>>() ?? previousAmmo;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            UnityEngine.Debug.LogWarning($"[Quickload] Last point-value display unavailable: {error.Message}");
        }
    }

    internal static void Reset()
    {
        visible.Clear();
        previous.Clear();
        current.Clear();
        previousAmmo.Clear();
        currentAmmo.Clear();
        visibleAmmo.Clear();
        path = null;
    }

    internal static void Publish(Dictionary<(string, string), (int, SimpleObjectParameters)> units)
    {
        current = units.Where(pair => pair.Value.Item2?._ini != null && !pair.Value.Item2.IsLoadoutUnit &&
                pair.Key.Item1 != "biologic")
            .ToDictionary(pair => pair.Key.Item1 + "/" + pair.Key.Item2, pair => pair.Value.Item2._taskForceCost,
                StringComparer.OrdinalIgnoreCase);
        foreach (string file in Singleton<FileManager>.Instance.GetFiles("ammunition", "*.ini"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (PointValueCache.TryGetAmmoPrice(name, out var detail)) currentAmmo[name] = detail.PerRound;
        }
        foreach (var entry in visible.ToArray()) notify(entry, nameof(MuseumObjectEntry.DetailFacts));
        visible.Clear();
        foreach (var entry in visibleAmmo.ToArray()) notify(entry, nameof(MuseumWeaponEntry.AmmoPointsCost));
        visibleAmmo.Clear();
        if (path is not { } target) return;
        string content = JObject.FromObject(new { Units = current, Ammo = currentAmmo }).ToString(Newtonsoft.Json.Formatting.None);
        _ = Task.Run(() =>
        {
            try { PointValueProfiles.Write(target, content); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                UnityEngine.Debug.LogWarning($"[Quickload] Could not save point-value display: {error.Message}");
            }
        });
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MuseumObjectEntry), nameof(MuseumObjectEntry.DetailFacts), MethodType.Getter)]
    private static void AddUnitPrice(MuseumObjectEntry __instance, ref List<MuseumDetailFact> __result)
    {
        if (path == null || __instance.IsLoadoutUnit) return;
        string? folder = __instance switch
        {
            MuseumSurfaceEntry or MuseumSubSurfaceEntry => "vessels",
            MuseumAircraftEntry => "aircraft",
            MuseumLandEntry => "land_units",
            _ => null,
        };
        if (folder == null) return;
        string key = folder + "/" + __instance.IniRef;
        string value;
        if (current.TryGetValue(key, out int price)) value = price.ToString("N0", CultureInfo.CurrentCulture);
        else if (DeferredPointValues.Running)
        {
            visible.Add(__instance);
            value = previous.TryGetValue(key, out price) ? $"{price:N0} · Updating…" : "Updating…";
        }
        else return;
        __result = [.. __result, new() { Label = "Point value", Value = value }];
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MuseumWeaponEntry), nameof(MuseumWeaponEntry.AmmoPointsCost), MethodType.Getter)]
    private static void ShowAmmoPrice(MuseumWeaponEntry __instance, ref string __result)
    {
        if (path == null || StartupLoading.DeferredEncyclopedia || __instance.IniRef == null) return;
        if (currentAmmo.TryGetValue(__instance.IniRef, out float value)) __result = value.ToString("N2", CultureInfo.CurrentCulture);
        else if (DeferredPointValues.Running)
        {
            visibleAmmo.Add(__instance);
            __result = previousAmmo.TryGetValue(__instance.IniRef, out value) ? $"{value:N2} · Updating…" : "Updating…";
        }
    }
}
