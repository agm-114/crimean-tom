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
    private const int Format = 6;
    private const string FileName = "unit-worker-output.json.gz";
    private const int SegmentFormat = 2;
    private static Session? session;
    private static Task<Prepared>? validation;
    private static Task<StartupArchives.Decoded<CacheFile>?>? early;

    internal static void DecodeAhead() =>
        early = StartupArchives.DecodeDuringStartup(FileName, Format, Read);

    internal static void StartValidation(StartupArchives archives) =>
        validation = BackgroundTask.Run(() => ReadStartup(archives));

    private static async Task<Prepared> ReadStartup(StartupArchives archives)
    {
        var full = await ReadUnits(archives, sensorIndependent: false).ConfigureAwait(false);
        if (full != null) return new(archives, full,
            archives.ExportRequested ? await archives.Inputs.ConfigureAwait(false) : null, null, false);
        var radarCompatible = await ReadUnits(archives, sensorIndependent: true).ConfigureAwait(false);
        if (radarCompatible != null) return new(archives, radarCompatible, null, null, true);
        var inputs = await archives.Inputs.ConfigureAwait(false);
        var segments = await StartupSegments.Read(archives, UnitSegmentFile(inputs), SegmentFormat, ReadSegment).ConfigureAwait(false);
        return new(archives, null, inputs, segments, false);
    }

    private static string UnitSegmentFile(StartupArchives.InputIndex inputs) => $"units-{inputs.SharedUnitInputs}.json.gz";
    private static Dictionary<string, Segment>? ReadSegment(Stream stream) =>
        StartupArchives.ReadJson<Dictionary<string, Segment>>(stream, UnitWorkerOutput.CreateSerializer());

    private static CacheFile? Read(Stream stream) =>
        StartupArchives.ReadJson<CacheFile>(stream, UnitWorkerOutput.CreateSerializer());

    private static async Task<CacheFile?> ReadUnits(StartupArchives archives, bool sensorIndependent)
    {
        var decoded = sensorIndependent ? null : Interlocked.Exchange(ref early, null);
        var snapshot = await archives.Read(FileName, Format, Read, sensorIndependent: sensorIndependent, decoded: decoded).ConfigureAwait(false);
        try
        {
            if (snapshot != null)
                foreach (var entry in snapshot.Units.Values) entry.Prepare();
            return snapshot;
        }
        catch (Exception error) when (StartupArchives.IsCacheError(error))
        {
            Debug.LogWarning($"[Quickload] Ignoring unit snapshot: {error.Message}");
            return null;
        }
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
        var candidate = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
        if (candidate is { SensorIndependent: true } && (ResolvedSensors.Delta is not { } delta ||
            candidate.Full?.SensorStamp != delta.PreviousStamp)) candidate = null;
        var prepared = new Session(candidate);
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
        if (completed.SensorIndependent && completed.Restored > 0 && completed.Original is { } compatible)
        {
            compatible.SensorStamp = ResolvedSensors.Stamp;
            archives.Save(FileName, Format, compatible, WriteCache);
            return;
        }
        var snapshot = completed.Full && completed.Restored > 0 ? completed.Original : completed.BuildCache();
        if (snapshot == null || snapshot.Units.IsEmpty) return;
        if (!completed.Full || completed.Restored == 0)
        {
            archives.Save(FileName, Format, snapshot, WriteCache);
            archives.Save(FileName, Format, snapshot, WriteCache, sensorIndependent: true);
        }
        else if (!archives.ExportRequested) return;
        if (completed.Stamps.IsEmpty && archives.ExportRequested && completed.Inputs is { } current)
            foreach (var pair in Singleton<UnitMetadataProvider>.Instance.UnitData)
                if (snapshot.Units.ContainsKey(pair.Key.Item1 + "/" + pair.Key.Item2) &&
                    UnitStamp.Create(archives, current, pair.Key.Item1, pair.Key.Item2) is { } stamp)
                    completed.Stamps[pair.Key.Item1 + "/" + pair.Key.Item2] = stamp;
        archives.Track(BackgroundTask.Run(async () =>
        {
            try
            {
                var inputs = await archives.Inputs.ConfigureAwait(false);
                var records = new Dictionary<string, Segment>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, output) in snapshot.Units)
                {
                    if (!completed.Stamps.TryGetValue(key, out var stamp)) continue;
                    records[stamp.Path] = new(stamp, output,
                        output.Ammunition.Select(reference => reference.Name).Distinct()
                            .ToDictionary(name => name, name => snapshot.Ammunition[name], StringComparer.Ordinal),
                        snapshot.Signatures.Where(record => output.SignatureIds.Contains(record.Id)).ToArray());
                }
                StartupSegments.Save(archives, UnitSegmentFile(inputs), SegmentFormat, records, pair => archives.LayerFor(archives.LocalPath(pair.Key)),
                    ReadSegment,
                    (stream, data) => StartupArchives.WriteJson(stream, data, UnitWorkerOutput.CreateSerializer()));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                Debug.LogWarning($"[Quickload] Could not write unit layer snapshots: {error.Message}");
            }
        }));
    }

    private static void WriteCache(Stream stream, CacheFile data) =>
        StartupArchives.WriteJson(stream, data, UnitWorkerOutput.CreateSerializer());

    [HarmonyPrefix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static bool RestoreSegment(string objectIniName, string objectIniFolder, int objectNumber, ref SimpleObjectParameters __result)
    {
        if (objectNumber != 0 || !SimpleObjectParameters.DeferSensorEntityCreation || session is not { Full: false } active ||
            active.Inputs == null || active.Segments == null || active.Segments.Count == 0) return true;
        var stamp = UnitStamp.Create(active.Archives!, active.Inputs, objectIniFolder, objectIniName);
        if (stamp == null || !active.Segments.TryGetValue(stamp.Path, out var segment) || segment.Stamp != stamp) return true;

        var sensors = Singleton<SensorMetadataProvider>.Instance;
        try
        {
            var prepared = segment.Output.Restore(objectIniFolder, objectIniName, sensors, segment.Signatures.ToDictionary(
                record => record.Id, record => sensors.GetSignatureId(record.Key, record.Family, record.Category)), segment.Ammunition);
            var output = UnitWorkerOutput.Capture(prepared, active.SensorNames);
            var live = Singleton<UnitMetadataProvider>.Instance.AmmunitionData;
            foreach (var pair in segment.Ammunition) live.TryAdd(pair.Key, pair.Value);
            foreach (var ammo in prepared.OnboardAmmunition) ammo.Ammunition = live[ammo.Ammunition.Name];
            __result = prepared;
            active.Stamps[objectIniFolder + "/" + objectIniName] = stamp;
            active.Added[objectIniFolder + "/" + objectIniName] = output;
            Interlocked.Increment(ref active.Restored);
            return false;
        }
        catch (Exception error) when (error is JsonException or IOException or KeyNotFoundException or ArgumentException or NullReferenceException)
        {
            Debug.LogWarning($"[Quickload] Rebuilding unit {objectIniName}: {error.Message}");
            return true;
        }
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
                if (!active.SensorIndependent && active.Archives?.ExportRequested != true) active.Entries.Clear();
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
            string key = __result._objectIniFolder + "/" + __result._objectIniName;
            active.Added[key] = UnitWorkerOutput.Capture(__result, active.SensorNames);
            if (active.Inputs is { } inputs &&
                UnitStamp.Create(active.Archives!, inputs, __result._objectIniFolder, __result._objectIniName) is { } stamp &&
                string.Equals(stamp.Path, active.Archives!.PortablePath(__result._ini.getFilePath()), StringComparison.OrdinalIgnoreCase))
                active.Stamps[key] = stamp;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException)
        {
            Debug.LogWarning($"[Quickload] Could not cache unit {__result._objectIniName}: {error.Message}");
        }
    }

    private class Session(Prepared? prepared)
    {
        internal readonly bool Full = prepared?.Full != null;
        internal readonly bool SensorIndependent = prepared?.SensorIndependent == true;
        internal readonly CacheFile? Original = prepared?.Full;
        internal readonly StartupArchives? Archives = prepared?.Archives;
        internal readonly StartupArchives.InputIndex? Inputs = prepared?.Inputs;
        internal readonly Dictionary<string, Segment>? Segments = prepared?.Segments;
        internal readonly ConcurrentDictionary<string, UnitStamp> Stamps = new(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<string, UnitWorkerOutput> Entries = prepared?.Full?.Units ?? new(StringComparer.Ordinal);
        internal readonly ConcurrentDictionary<string, UnitWorkerOutput> Added = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, AmmunitionSimpleParameters> Ammunition = prepared?.Full?.Ammunition ?? new(StringComparer.Ordinal);
        internal readonly SignatureRecord[] SignatureRecords = prepared?.Full?.Signatures ?? [];
        internal Dictionary<int, int> Signatures = [];
        internal Dictionary<Entity, string> SensorNames = [];
        internal int Restored;

        internal CacheFile BuildCache()
        {
            var sensor = Singleton<SensorMetadataProvider>.Instance;
            return new()
            {
                Units = Added,
                SensorStamp = ResolvedSensors.Stamp,
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
        [JsonProperty(Required = Required.Always)] public string SensorStamp = "";
    }

    private record SignatureRecord(int Id, string Key, string? Family, string? Category);
    private record Prepared(StartupArchives Archives, CacheFile? Full, StartupArchives.InputIndex? Inputs,
        Dictionary<string, Segment>? Segments, bool SensorIndependent);
    private record Segment(UnitStamp Stamp, UnitWorkerOutput Output, Dictionary<string, AmmunitionSimpleParameters> Ammunition,
        SignatureRecord[] Signatures);

    private record UnitStamp(string Path, string Digest)
    {
        internal static UnitStamp? Create(StartupArchives archives, StartupArchives.InputIndex inputs, string folder, string name)
        {
            var manager = Singleton<FileManager>.Instance;
            string? mainPath = null;
            var keys = new List<string> { inputs.SharedUnitInputs };
            foreach (string suffix in new[] { "", "_variants", "_squadrons" })
            {
                string path = manager.GetFile($"{folder}/{name}{suffix}.ini");
                if (path == null)
                {
                    if (suffix.Length == 0) return null;
                    if (IniHandler.GetCopySources(name).Count > 0) return null;
                    keys.Add("");
                    continue;
                }
                path = StartupArchives.Normalize(path);
                if (!inputs.Files.TryGetValue(path, out var file) || file.HasIncludes) return null;
                IniHandler.GetModifierFilePaths(path, out var extend, out var prepend);
                if (extend.Count > 0 || prepend.Count > 0) return null;
                string portable = archives.PortablePath(path);
                mainPath ??= portable;
                keys.Add(portable);
                keys.Add(file.Hash);
            }
            return new(mainPath!, StartupArchives.Digest(string.Join("\0", keys)));
        }
    }
}
