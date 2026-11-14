using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json;
using SeaPower;
using Unity.Entities;
using Debug = UnityEngine.Debug;
using System.Threading.Tasks;
using BackgroundTask = System.Threading.Tasks.Task;

namespace Quickload;

[HarmonyPatch]
internal static class UnitMetadataCache
{
    private const int Format = 5;
    private const string FileName = "unit-worker-output.json.gz";
    private static Session? session;
    private static Task<CacheFile?>? validation;

    internal static void StartValidation(StartupArchives archives) =>
        validation = archives.Read(FileName, Format, Read);

    private static CacheFile? Read(Stream stream)
    {
        var cached = StartupArchives.ReadJson<CacheFile>(stream, UnitWorkerOutput.CreateSerializer());
        if (cached == null) return null;
        foreach (var entry in cached.Units.Values) entry.Prepare();
        return cached;
    }

    internal static IEnumerator OpenForStartup()
    {
        var task = validation;
        validation = null;
        if (task == null) yield break;
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted)
        {
            Debug.LogWarning($"[Quickload] Unit metadata cache unavailable: {task.Exception?.GetBaseException()}");
        }
        var prepared = new Session(task.Status == TaskStatus.RanToCompletion ? task.Result : null);
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

    [HarmonyPatch]
    private static class UnitDispatch
    {
        private static readonly Type iterator = typeof(UnitMetadataProvider).GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name.StartsWith("<<OnStart>g__LoadUnitMetadata|", StringComparison.Ordinal));

        private static MethodBase TargetMethod() => AccessTools.Method(iterator, "MoveNext");

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ReplaceUnitDispatch(IEnumerable<CodeInstruction> code)
        {
            var instructions = code.ToList();
            var dispatch = instructions.Where(instruction => instruction.operand is MethodInfo method &&
                method.DeclaringType == typeof(BackgroundTask) && method.Name == nameof(BackgroundTask.Run) &&
                method.ReturnType == typeof(Task<ParallelLoopResult>)).ToArray();
            if (dispatch.Length != 2) return instructions;
            dispatch[0].operand = AccessTools.Method(typeof(UnitDispatch), nameof(RunUnitWorker));
            dispatch[1].operand = AccessTools.Method(typeof(UnitDispatch), nameof(RunAmmoWorker));
            return instructions;
        }

        private static Task<ParallelLoopResult> RunAmmoWorker(Func<ParallelLoopResult> native)
        {
            if (session is { Restored: > 0 } && native.Target is { } closure &&
                AccessTools.Field(closure.GetType(), "ammoNames")?.GetValue(closure) is List<string> { Count: 0 })
                return BackgroundTask.FromResult(default(ParallelLoopResult));
            return BackgroundTask.Run(native);
        }

        private static Task<ParallelLoopResult> RunUnitWorker(Func<ParallelLoopResult> native)
        {
            if (session is not { } active || active.Entries.IsEmpty || native.Target is not { } closure ||
                AccessTools.Field(closure.GetType(), "workItems")?.GetValue(closure) is not List<(string Folder, string Name)> items ||
                AccessTools.Field(closure.GetType(), "results")?.GetValue(closure) is not SimpleObjectParameters[] results ||
                results.Length != items.Count || active.Entries.Count != items.Count ||
                items.Any(item => !active.Entries.ContainsKey(item.Folder + "/" + item.Name)))
                return BackgroundTask.Run(native);

            var sensors = Singleton<SensorMetadataProvider>.Instance;
            var ammunition = Singleton<UnitMetadataProvider>.Instance.AmmunitionData;
            return BackgroundTask.Run(() =>
            {
                try
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        var (folder, name) = items[i];
                        var entry = active.Entries[folder + "/" + name];
                        results[i] = entry.Restore(folder, name, sensors, active.Signatures, active.Ammunition);
                    }
                }
                catch (Exception error) when (error is JsonException or IOException or KeyNotFoundException or ArgumentException or NullReferenceException)
                {
                    Debug.LogWarning($"[Quickload] Rebuilding cached unit batch: {error.Message}");
                    Array.Clear(results, 0, results.Length);
                    return native();
                }

                foreach (var pair in active.Ammunition) ammunition.TryAdd(pair.Key, pair.Value);
                active.Restored = results.Length;
                active.Entries.Clear();
                Debug.Log($"[Quickload] Supplied {results.Length:N0} cached records without the native unit worker pool.");
                return default;
            });
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
