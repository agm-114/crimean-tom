using System.Collections;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using SeaPower;
using Unity.Collections;
using Unity.Entities;
using Debug = UnityEngine.Debug;
using Task = System.Threading.Tasks.Task;
using IniData = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>;

namespace Quickload;

[HarmonyPatch]
internal static class ResolvedSensors
{
    private const string FileName = "resolved-sensors.json.gz";
    private const int Format = 4;
    private static Task<Snapshot?>? pending;
    private static Task<Snapshot?>? exact;
    private static readonly Dictionary<string, Sensor> sensors = new(StringComparer.Ordinal);
    private static bool loading;
    private static int reused;
    private static HashSet<string> changedSources = new(StringComparer.OrdinalIgnoreCase);
    private static StartupArchives? activeArchives;
    private static bool sourceOrderChanged;
    private static bool? externalReferences;

    internal static Change? Delta { get; private set; }
    internal static string Stamp { get; private set; } = "";

    internal static void Begin(StartupArchives archives)
    {
        pending = archives.Read(FileName, Format, Read, sensorIndependent: true);
        exact = archives.Read(FileName, Format, Read);
    }

    internal static void Reset()
    {
        pending = null;
        exact = null;
        Delta = null;
        Stamp = "";
        sensors.Clear();
        changedSources.Clear();
        activeArchives = null;
        sourceOrderChanged = false;
        externalReferences = null;
        loading = false;
    }

    internal static IEnumerator Load(IEnumerator native, StartupArchives archives)
    {
        var task = pending;
        var exactTask = exact;
        pending = null;
        exact = null;
        while (task is { IsCompleted: false } || exactTask is { IsCompleted: false }) yield return null;
        var previous = task is { IsCompletedSuccessfully: true } ? task.Result : null;
        var exactMatch = exactTask is { IsCompletedSuccessfully: true } ? exactTask.Result : null;
        var library = Utils.openVirtualIniFile("systems", "sensors", "sensors");
        if (library?.Data == null)
        {
            yield return native;
            yield break;
        }

        var current = library.Data.ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value, pair.Value.Comparer), StringComparer.Ordinal);
        Stamp = Fingerprint(current);
        Delta = previous == null ? null : Change.Compare(previous.Sections, current);
        var sources = archives.Inputs.IsCompletedSuccessfully ? archives.Inputs.Result.SensorFiles : new(StringComparer.OrdinalIgnoreCase);
        activeArchives = archives;
        changedSources = previous == null ? new(StringComparer.OrdinalIgnoreCase)
            : new(previous.Sources.Keys.Concat(sources.Keys).Where(path =>
                previous.Sources.GetValueOrDefault(path) != sources.GetValueOrDefault(path)), StringComparer.OrdinalIgnoreCase);
        sourceOrderChanged = previous != null && !previous.Sources.Keys.Where(sources.ContainsKey)
            .SequenceEqual(sources.Keys.Where(previous.Sources.ContainsKey), StringComparer.OrdinalIgnoreCase);
        sensors.Clear();
        var reuse = exactMatch ?? previous;
        if (reuse != null)
            foreach (var (name, sensor) in reuse.Sensors)
                if (current.TryGetValue(name, out var resolved) && reuse.Sections.TryGetValue(name, out var old) &&
                    SameSection(old, resolved) && IniHandler.GetCopySources(name).Count == 0) sensors[name] = sensor;
        reused = 0;
        loading = true;
        try { yield return native; }
        finally { loading = false; }
        Debug.Log($"[Quickload] Restored {reused:N0} resolved sensors. Radar range changes: {Delta?.Names.Count.ToString() ?? "unavailable"}.");
        var snapshot = new Snapshot(current, new(sensors), sources);
        if (exactMatch == null) archives.Save(FileName, Format, snapshot, Write);
        if (previous == null || Delta == null)
            archives.Save(FileName, Format, snapshot, Write, sensorIndependent: true);
        sensors.Clear();
    }

    internal static bool CanReuseEncyclopedia(Change change)
    {
        if (sourceOrderChanged) return false;
        if (changedSources.Count == 0) return !HasExternalRadarReferences(change);
        if (change.Names.Count == 0 || changedSources.Count != 1) return false;
        string path = activeArchives?.LocalPath(changedSources.Single()) ?? changedSources.Single();
        if (!File.Exists(path)) return false;
        // A changed source can also change which mod the Encyclopedia credits.
        try
        {
            var sections = IniHandler.ReadOwnKeys(path, out bool inherits);
            return !inherits && sections is { Count: > 0 } && sections.Keys.All(change.Names.Contains) &&
                !HasExternalRadarReferences(change);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        "MakeRadar", "MakeSonar", "MakeECM", "MakeESM", "MakeVisual", "MakeMAD", "MakeLaser"
    }.Select(name => AccessTools.Method(typeof(SensorMetadataProvider), name));

    [HarmonyPrefix]
    private static bool Restore(SensorMetadataProvider __instance, ref EntityManager em, string __2, MethodBase __originalMethod, ref Entity __result)
    {
        if (!loading || !sensors.TryGetValue(__2, out var sensor) || sensor.Kind != __originalMethod.Name) return true;
        __result = sensor.Kind switch
        {
            "MakeRadar" when sensor.Radar is { } radar => Create(ref em, __2, "Radar", radar, true),
            "MakeSonar" when sensor.Sonar is { } sonar => Create(ref em, __2, "Sonar", sonar),
            "MakeECM" when sensor.Ecm is { } ecm => Create(ref em, __2, "ECM", ecm, true, false),
            "MakeESM" when sensor.Esm is { } esm => Create(ref em, __2, "ESM", esm),
            "MakeVisual" when sensor.Visual is { } visual => Create(ref em, __2, "Visual", visual),
            "MakeMAD" when sensor.Mad is { } mad => Create(ref em, __2, "MAD", mad),
            "MakeLaser" when sensor.Laser is { } laser => Create(ref em, __2, "Laser", laser),
            _ => Entity.Null,
        };
        if (__result == Entity.Null) return true;
        if (sensor.Signature)
        {
            int? signature = __instance.MakeSignatureForSystem(ref em, __result, __result);
            if (signature.HasValue) __instance.Signatures[signature.Value] = __result;
        }
        reused++;
        return false;
    }

    private static Entity Create<T>(ref EntityManager em, string name, string kind, T data, bool hasSignature = false, bool named = true)
        where T : unmanaged, IComponentData
    {
        var entity = hasSignature
            ? em.CreateEntity(typeof(SensorDataComponent), typeof(T), typeof(SignatureComponent), typeof(PersistComponent))
            : em.CreateEntity(typeof(SensorDataComponent), typeof(T), typeof(PersistComponent));
        em.SetName(entity, kind + "Params_" + name);
        if (named) em.SetComponentData(entity, new SensorDataComponent { Name = name });
        em.SetComponentData(entity, data);
        return entity;
    }

    [HarmonyPostfix]
    private static void Capture(ref EntityManager em, string __2, MethodBase __originalMethod, Entity __result, bool __runOriginal)
    {
        if (!loading || !__runOriginal || __result == Entity.Null) return;
        string kind = __originalMethod.Name;
        bool signature = em.HasComponent<SignatureComponent>(__result);
        sensors[__2] = kind switch
        {
            "MakeRadar" => new(kind, signature) { Radar = em.GetComponentData<SensorDataRadarComponent>(__result) },
            "MakeSonar" => new(kind, signature) { Sonar = em.GetComponentData<SensorDataSonarComponent>(__result) },
            "MakeECM" => new(kind, signature) { Ecm = em.GetComponentData<SensorDataECMComponent>(__result) },
            "MakeESM" => new(kind, signature) { Esm = em.GetComponentData<SensorDataESMComponent>(__result) },
            "MakeVisual" => new(kind, signature) { Visual = em.GetComponentData<SensorDataVisualComponent>(__result) },
            "MakeMAD" => new(kind, signature) { Mad = em.GetComponentData<SensorDataMADComponent>(__result) },
            _ => new(kind, signature) { Laser = em.GetComponentData<SensorDataLaserComponent>(__result) },
        };
    }

    private static string Fingerprint(IniData sections) => StartupArchives.Digest(
        JsonConvert.SerializeObject(sections.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new { pair.Key, Values = pair.Value.OrderBy(value => value.Key, StringComparer.Ordinal) })));

    internal static HashSet<string> AffectedUnits(Change change)
    {
        var units = Singleton<UnitMetadataProvider>.Instance.UnitData;
        if (change.Names.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        return units.Where(pair => UsesChangedRadar(pair.Value.Item2?._ini, change) ||
                UsesChangedRadar(pair.Value.Item2?._iniObjVariants, change) ||
                UsesChangedRadar(pair.Value.Item2?._iniSquadrons, change))
            .Select(pair => pair.Key.Item1 + "/" + pair.Key.Item2).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool HasExternalRadarReferences(Change change) =>
        externalReferences ??= ScanExternalRadarReferences(change);

    private static bool ScanExternalRadarReferences(Change change)
    {
        if (change.Names.Count == 0) return false;
        foreach (string library in new[] { "weapons", "combatsystems" })
        {
            var ini = Utils.openVirtualIniFile("systems", library, library);
            if (UsesChangedRadar(ini, change)) return true;
        }
        var files = Singleton<FileManager>.Instance;
        foreach (string virtualPath in files.GetFiles("ammunition", "*.ini"))
        {
            string? path = files.GetFile(virtualPath);
            if (path == null) return true;
            var ini = Utils.openIniFile(path, logErrors: false);
            if (ini?.Data == null || UsesChangedRadar(ini, change)) return true;
        }
        return false;
    }

    // Check all resolved values, including nation and loadout overrides and the
    // SystemName targets reached through weapon-system fire-control references.
    private static bool UsesChangedRadar(IniHandler? ini, Change change) =>
        ini?.Data is { } data && data.Any(section => change.Names.Contains(section.Key) ||
            section.Value.Any(pair => change.Names.Contains(pair.Key) ||
                change.Names.Any(name => pair.Value.Contains(name, StringComparison.OrdinalIgnoreCase))));

    private static bool SameSection(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(pair => pair.Key, StringComparer.Ordinal));

    private static JsonSerializer CreateSerializer() => JsonSerializer.Create(new()
    {
        Converters = { new VectorConverter(), new SignatureNameConverter() },
    });
    private static Snapshot? Read(Stream stream) => StartupArchives.ReadJson<Snapshot>(stream, CreateSerializer());
    private static void Write(Stream stream, Snapshot snapshot) =>
        StartupArchives.WriteJson(stream, snapshot, CreateSerializer());
    private record Snapshot(IniData Sections, Dictionary<string, Sensor> Sensors, Dictionary<string, string> Sources);
    private record Sensor(string Kind, bool Signature)
    {
        public SensorDataRadarComponent? Radar { get; init; }
        public SensorDataSonarComponent? Sonar { get; init; }
        public SensorDataECMComponent? Ecm { get; init; }
        public SensorDataESMComponent? Esm { get; init; }
        public SensorDataVisualComponent? Visual { get; init; }
        public SensorDataMADComponent? Mad { get; init; }
        public SensorDataLaserComponent? Laser { get; init; }
    }

    private class VectorConverter : JsonConverter<UnityEngine.Vector2>
    {
        public override void WriteJson(JsonWriter writer, UnityEngine.Vector2 value, JsonSerializer serializer) =>
            serializer.Serialize(writer, new[] { value.x, value.y });

        public override UnityEngine.Vector2 ReadJson(JsonReader reader, Type type, UnityEngine.Vector2 existing, bool hasExisting, JsonSerializer serializer)
        {
            var values = serializer.Deserialize<float[]>(reader);
            if (values is not { Length: 2 }) throw new JsonSerializationException("Expected two sensor vector coordinates.");
            return new(values[0], values[1]);
        }
    }

    private class SignatureNameConverter : JsonConverter<FixedString128Bytes>
    {
        public override void WriteJson(JsonWriter writer, FixedString128Bytes value, JsonSerializer serializer) =>
            writer.WriteValue(value.ToString());

        public override FixedString128Bytes ReadJson(JsonReader reader, Type type, FixedString128Bytes existing, bool hasExisting,
            JsonSerializer serializer) => reader.Value is string name
            ? new(name)
            : throw new JsonSerializationException("Expected a sensor signature name.");
    }

    internal record Change(HashSet<string> Names, string PreviousStamp)
    {
        internal static Change? Compare(IniData previous, IniData current)
        {
            if (!previous.Keys.SequenceEqual(current.Keys, StringComparer.Ordinal)) return null;
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, section) in current)
            {
                var old = previous[name];
                if (SameSection(old, section)) continue;
                if (!section.TryGetValue("Kind", out string kind) || kind != "Radar" ||
                    !old.Where(pair => !IsRange(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .SequenceEqual(section.Where(pair => !IsRange(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))) return null;
                if (IniHandler.GetCopySources(name).Count > 0 ||
                    !changed.Add(name)) return null;
            }
            if (changed.Count > 0 && current.Keys.Any(name => IniHandler.GetCopySources(name).Any(changed.Contains))) return null;
            return new(changed, Fingerprint(previous));
        }

        private static bool IsRange(string key) => key.Equals("MaxRange", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("MinRange", StringComparison.OrdinalIgnoreCase);
    }
}
