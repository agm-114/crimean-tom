using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPower;
using UnityEngine;

namespace Quickload;

[HarmonyPatch]
internal static class PointValueProfiles
{
    private static string? directory;
    private static string? root;
    private static string? cachePath;
    private static StartupArchives? source;
    private static string? sensorSeed;

    internal static void Start(StartupArchives archives)
    {
        root = Path.Combine(Application.persistentDataPath, "cache", "quickload-pointvalues");
        directory = Path.Combine(root, archives.Profile);
        source = archives;
        cachePath = null;
        sensorSeed = null;
    }

    internal static void Stop()
    {
        directory = null;
        root = null;
        cachePath = null;
        source = null;
        sensorSeed = null;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PointValueCache), "CachePath", MethodType.Getter)]
    private static void SelectProfile(ref string __result)
    {
        if (cachePath != null)
        {
            __result = cachePath;
            return;
        }
        if (directory == null || root == null || source?.Inputs is not { IsCompletedSuccessfully: true } inputs) return;
        var tag = inputs.Result;
        string shared = tag.SharedUnitInputs;
        string nativeHash = PointValueCache.ComputeGlobalHash();
        string file = shared + "-" + nativeHash + ".json";
        string path = Path.Combine(directory, file);
        try
        {
            if (!File.Exists(path) && Directory.Exists(root))
            {
                string? compatible = Directory.EnumerateDirectories(root)
                    .Select(profile => Path.Combine(profile, file)).FirstOrDefault(File.Exists);
                if (compatible != null)
                {
                    Directory.CreateDirectory(directory);
                    File.Copy(compatible, path);
                }
            }
            if (!File.Exists(path) && source.IsVanilla)
            {
                string bundled = Path.Combine(source.VanillaBundle, "point-values-" + file);
                if (File.Exists(bundled) && new FileInfo(bundled).Length <= 16 * 1024 * 1024)
                {
                    Directory.CreateDirectory(directory);
                    File.Copy(bundled, path);
                }
            }
            if (tag.SensorIndependent != null && ResolvedSensors.Stamp.Length > 0)
            {
                string seedDirectory = Path.Combine(root, "sensor-inputs");
                sensorSeed = Path.Combine(seedDirectory, SeedName(tag.SensorIndependent, ResolvedSensors.Stamp));
                if (!File.Exists(path) && ResolvedSensors.Delta is { } change)
                {
                    string previous = Path.Combine(seedDirectory, SeedName(tag.SensorIndependent, change.PreviousStamp));
                    if (File.Exists(previous) && !ResolvedSensors.HasExternalRadarReferences(change))
                        SeedRadarChange(previous, path, nativeHash, change);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.LogWarning($"[Quickload] Point-value profile seed unavailable: {error.Message}");
        }
        source = null;
        __result = cachePath = path;
    }

    private static string SeedName(string context, string sensors) => StartupArchives.Digest(context + sensors) + ".json";

    private static void SeedRadarChange(string previous, string target, string nativeHash, ResolvedSensors.Change change)
    {
        var cache = JObject.Parse(File.ReadAllText(previous));
        if (cache.Value<int>("Version") != 3 || cache["Units"] is not JObject units || cache["AmmoPrices"] is not JObject prices) return;
        var affected = ResolvedSensors.AffectedUnits(change);
        var invalid = units.Properties().Where(unit => affected.Contains(unit.Name)).ToArray();
        foreach (var unit in invalid) unit.Remove();
        // Native code still compares each unit hash when applying the seeded records.
        cache["GlobalHash"] = nativeHash;
        Write(target, cache.ToString(Formatting.None));
        Debug.Log($"[Quickload] Seeded {units.Count} unit scores and {prices.Count} ammo prices; {invalid.Length} radar-dependent unit scores invalidated.");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PointValueCache), nameof(PointValueCache.Flush))]
    private static void SaveSensorSeed()
    {
        if (sensorSeed == null || cachePath == null || !File.Exists(cachePath)) return;
        try
        {
            if (!File.Exists(sensorSeed) || File.GetLastWriteTimeUtc(cachePath) > File.GetLastWriteTimeUtc(sensorSeed))
                Write(sensorSeed, File.ReadAllText(cachePath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.LogWarning($"[Quickload] Could not save point-value seed: {error.Message}");
        }
    }

    internal static void ExportVanilla(string folder)
    {
        if (cachePath == null || !File.Exists(cachePath)) return;
        Directory.CreateDirectory(folder);
        File.Copy(cachePath, Path.Combine(folder, "point-values-" + Path.GetFileName(cachePath)), overwrite: true);
    }

    internal static void Write(string path, string data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, data);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }
}
