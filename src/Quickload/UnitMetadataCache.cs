using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using HarmonyLib;
using Newtonsoft.Json;
using SeaPower;
using Unity.Entities;
using Debug = UnityEngine.Debug;
using System.Threading.Tasks;

namespace Quickload;

[HarmonyPatch]
internal static class UnitMetadataCache
{
    private const int Format = 5;
    private const string FileName = "unit-worker-output.json.gz";
    private static Session? session;
    private static Task<CacheFile?>? validation;

    internal static void StartValidation(StartupArchives archives) =>
        validation = archives.Read(FileName, Format, stream => StartupArchives.ReadJson<CacheFile>(stream, UnitWorkerOutput.CreateSerializer()));

    internal static IEnumerator OpenForStartup()
    {
        var task = validation;
        validation = null;
        if (task == null) yield break;
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted)
        {
            Debug.LogWarning($"[Quickload] Unit metadata cache unavailable: {task.Exception?.GetBaseException().Message}");
            yield break;
        }
        var prepared = new Session(task.Result);
        var sensors = Singleton<SensorMetadataProvider>.Instance;
        prepared.SensorNames = sensors.Sensors.ToDictionary(pair => pair.Value, pair => pair.Key);
        foreach (var record in prepared.SignatureRecords)
            prepared.Signatures[record.Id] = sensors.GetSignatureId(record.Key, record.Family, record.Category);
        session = prepared;
        Debug.Log($"[Quickload] Unit metadata cache {(session.Entries.Count > 0 ? "hit" : "miss")}: waited {clock.Elapsed.TotalSeconds:F2}s");
    }

    internal static void Finish(StartupArchives? archives, bool loaded)
    {
        var completed = session;
        session = null;
        if (completed == null || archives == null || !loaded) return;
        Debug.Log($"[Quickload] Unit metadata cache restored {completed.Restored} units.");
        if (completed.Added.IsEmpty || completed.Restored > 0) return;
        var snapshot = completed.BuildCache();
        archives.Save(FileName, Format, snapshot, (stream, data) => StartupArchives.WriteJson(stream, data, UnitWorkerOutput.CreateSerializer()));
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static bool Restore(string objectIniName, string objectIniFolder, int objectNumber, ref SimpleObjectParameters __result)
    {
        if (objectNumber != 0 || !SimpleObjectParameters.DeferSensorEntityCreation || session is not { } active) return true;
        string key = objectIniFolder + "/" + objectIniName;
        if (!active.Entries.TryRemove(key, out var entry)) return true;
        try
        {
            active.RestoreAmmunition();
            __result = entry.Restore(objectIniFolder, objectIniName, Singleton<SensorMetadataProvider>.Instance,
                active.Signatures, active.Ammunition);
            Interlocked.Increment(ref active.Restored);
            return false;
        }
        catch (Exception error) when (error is JsonException or IOException or KeyNotFoundException or ArgumentException or NullReferenceException)
        {
            Debug.LogWarning($"[Quickload] Rebuilding cached unit {key}: {error.Message}");
            return true;
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static void Capture(int objectNumber, SimpleObjectParameters __result, bool __runOriginal)
    {
        if (objectNumber != 0 || !SimpleObjectParameters.DeferSensorEntityCreation || !__runOriginal
            || __result?._ini == null || session is not { } active) return;
        try
        {
            active.Added[__result._objectIniFolder + "/" + __result._objectIniName] =
                UnitWorkerOutput.Capture(__result, active.SensorNames);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException)
        {
            Debug.LogWarning($"[Quickload] Could not cache unit {__result._objectIniName}: {error.Message}");
        }
    }

    private class Session(CacheFile? snapshot)
    {
        internal readonly ConcurrentDictionary<string, UnitWorkerOutput> Entries = snapshot?.Units ?? new(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<string, UnitWorkerOutput> Added = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, AmmunitionSimpleParameters> Ammunition = snapshot?.Ammunition ?? new(StringComparer.Ordinal);
        internal readonly SignatureRecord[] SignatureRecords = snapshot?.Signatures ?? [];
        internal Dictionary<int, int> Signatures = [];
        internal Dictionary<Entity, string> SensorNames = [];
        internal int Restored;
        private readonly object ammoLock = new();
        private volatile bool ammunitionRestored;

        internal void RestoreAmmunition()
        {
            if (ammunitionRestored) return;
            lock (ammoLock)
            {
                if (ammunitionRestored) return;
                var live = Singleton<UnitMetadataProvider>.Instance.AmmunitionData;
                foreach (var pair in Ammunition) live.TryAdd(pair.Key, pair.Value);
                ammunitionRestored = true;
            }
        }

        internal CacheFile BuildCache()
        {
            var sensor = Singleton<SensorMetadataProvider>.Instance;
            return new()
            {
                Units = Added,
                Ammunition = new(Singleton<UnitMetadataProvider>.Instance.AmmunitionData),
                Signatures = sensor.SignatureIdMap.Select(pair => new SignatureRecord(pair.Key, pair.Value,
                    sensor.FamilyMap[pair.Value], sensor.CategoryMap[pair.Value])).ToArray(),
            };
        }
    }

    private class CacheFile
    {
        [JsonProperty(Required = Required.Always)] public ConcurrentDictionary<string, UnitWorkerOutput> Units = new(StringComparer.Ordinal);
        [JsonProperty(Required = Required.Always)] public Dictionary<string, AmmunitionSimpleParameters> Ammunition = [];
        [JsonProperty(Required = Required.Always)] public SignatureRecord[] Signatures = [];
    }

    private record SignatureRecord(int Id, string Key, string? Family, string? Category);
}
