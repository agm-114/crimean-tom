using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SeaPower;
using Seapower;
using Debug = UnityEngine.Debug;
using Task = System.Threading.Tasks.Task;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaCache
{
    private const int Format = 3;
    private const string FileName = "encyclopedia.json.gz";
    private static readonly Type manager = EncyclopediaLoading.ManagerType;
    private static readonly MethodInfo setUnitsLoaded = AccessTools.PropertySetter(manager, "UnitsLoaded");
    private static readonly FieldInfo unitsLoadedEvent = AccessTools.Field(manager, "UnitsLoadedEvent");
    // Native UI callers still get the shared game object when they open missile details.
    private static readonly ConditionalWeakTable<MuseumWeaponEntry, string> ammunition = new();
    private static Task<Snapshot?>? prefetch;
    private static Task<StartupArchives.Decoded<Snapshot>?>? early;
    private static Snapshot? restored;
    private static Snapshot? captured;
    private static StartupArchives? archives;
    private static bool refreshedRadars;

    internal static bool HasSnapshot => restored != null;

    internal static void DecodeAhead() =>
        early = StartupArchives.DecodeDuringStartup(FileName, Format, Read);

    internal static void Start(StartupArchives source)
    {
        archives = source;
        prefetch = source.Read(FileName, Format, Read, decoded: Interlocked.Exchange(ref early, null));
    }

    internal static void Finish()
    {
        EncyclopediaWeapons.Reset();
        EncyclopediaUnitCalculations.Finish();
        prefetch = null;
        restored = null;
        captured = null;
        archives = null;
        refreshedRadars = false;
    }

    internal static IEnumerator Wrap(string part, object manager, object[] arguments, IEnumerator native)
    {
        if (!StartupLoading.DeferredEncyclopedia || archives == null)
        {
            yield return native;
            yield break;
        }

        if (part == "sensors" && prefetch is { } read)
        {
            prefetch = null;
            while (!read.IsCompleted) yield return null;
            restored = read.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? read.Result : null;
            if (restored is { } cache && !cache.CanRestore)
                restored = null;
            if (restored == null && EncyclopediaSensors.Available && ResolvedSensors.Delta is { } delta &&
                ResolvedSensors.CanReuseEncyclopedia(delta))
            {
                var radarRead = archives.Read(FileName, Format, Read, sensorIndependent: true);
                while (!radarRead.IsCompleted) yield return null;
                if (radarRead is { IsCompletedSuccessfully: true, Result: { CanRestore: true } radar } &&
                    radar.SensorStamp == delta.PreviousStamp &&
                    delta.Names.All(name => radar.Sensors.Any(sensor => sensor.IniRef == name && sensor is MuseumRadarEntry)))
                {
                    if (EncyclopediaSensors.TryRefreshRadars(manager, arguments[0], native, radar.Sensors, delta.Names))
                    {
                        radar.SensorGroups = Panel(arguments[0]).ToArray();
                        radar.SensorStamp = ResolvedSensors.Stamp;
                        restored = radar;
                        refreshedRadars = true;
                        Debug.Log($"[Quickload] Rebuilt {delta.Names.Count} Encyclopedia radar entries and reused the weapons and units.");
                        yield return null;
                        yield break;
                    }
                    native = EncyclopediaSensors.Load(manager, arguments[0]);
                }
            }
            if (restored == null)
            {
                EncyclopediaWeapons.Start(archives);
                EncyclopediaUnitCalculations.Start(archives);
                captured = new();
            }
        }

        if (restored is { } snapshot)
        {
            (native as IDisposable)?.Dispose();
            Restore(part, manager, arguments, snapshot);
            if (part == "units" && refreshedRadars && archives != null)
                archives.Save(FileName, Format, snapshot, Write);
            Debug.Log($"[Quickload] Encyclopedia {part} restored from snapshot.");
            yield break;
        }

        yield return native;
        if (captured is { } output) Capture(part, manager, arguments, output);
    }

    private static void Capture(string part, object instance, object[] arguments, Snapshot snapshot)
    {
        switch (part)
        {
            case "sensors":
                snapshot.Sensors = Entries<MuseumSensorEntry>(instance, "SensorMuseumEntries").ToArray();
                snapshot.SensorGroups = Panel(arguments[0]).ToArray();
                break;
            case "weapons":
                var weapons = Entries<MuseumWeaponEntry>(instance, "WeaponMuseumEntries");
                snapshot.BaseWeaponCount = weapons.Count;
                snapshot.Missiles = weapons.Where(entry => entry.Type == Ammunition.Type.Missile).ToArray();
                snapshot.WeaponGroups = Panel(arguments[0]).ToArray();
                break;
            case "units":
                if (!(bool)AccessTools.PropertyGetter(manager, "UnitsLoaded").Invoke(instance, null)) break;
                snapshot.Weapons = Entries<MuseumWeaponEntry>(instance, "WeaponMuseumEntries").ToArray();
                snapshot.Units = Entries<MuseumObjectEntry>(instance, "MuseumObjectEntries").ToArray();
                snapshot.Guns = Entries<MuseumGunEntry>(instance, "GunMuseumEntries").ToArray();
                snapshot.Launchers = Entries<MuseumLauncherEntry>(instance, "LauncherMuseumEntries").ToArray();
                snapshot.Panels = arguments.Select(panel => Panel(panel).ToArray()).ToArray();
                snapshot.NatoUnitNames = OptionsManager.NatoUnitNamesEnabled;
                snapshot.NatoWeaponNames = OptionsManager.NatoWeaponNamesEnabled;
                snapshot.Culture = System.Globalization.CultureInfo.CurrentCulture.Name;
                snapshot.SensorStamp = ResolvedSensors.Stamp;
                captured = null;
                if (archives is { } source) source.Save(FileName, Format, snapshot, Write);
                archives?.Save(FileName, Format, snapshot, Write, sensorIndependent: true);
                break;
        }
    }

    private static void Restore(string part, object instance, object[] arguments, Snapshot snapshot)
    {
        switch (part)
        {
            case "sensors":
                Replace(Entries<MuseumSensorEntry>(instance, "SensorMuseumEntries"), snapshot.Sensors);
                Replace(Panel(arguments[0]), snapshot.SensorGroups);
                break;
            case "weapons":
                foreach (var missile in snapshot.Missiles) ammunition.Add(missile, missile.IniRef);
                Replace(Entries<MuseumWeaponEntry>(instance, "WeaponMuseumEntries"), snapshot.Weapons.Take(snapshot.BaseWeaponCount));
                Replace(Panel(arguments[0]), snapshot.WeaponGroups);
                break;
            case "units":
                var weapons = Entries<MuseumWeaponEntry>(instance, "WeaponMuseumEntries");
                foreach (var entry in snapshot.Weapons.Skip(snapshot.BaseWeaponCount)) weapons.Add(entry);
                Replace(Entries<MuseumObjectEntry>(instance, "MuseumObjectEntries"), snapshot.Units);
                Replace(Entries<MuseumGunEntry>(instance, "GunMuseumEntries"), snapshot.Guns);
                Replace(Entries<MuseumLauncherEntry>(instance, "LauncherMuseumEntries"), snapshot.Launchers);
                for (int i = 0; i < arguments.Length; i++) Replace(Panel(arguments[i]), snapshot.Panels[i]);
                setUnitsLoaded.Invoke(instance, [true]);
                (unitsLoadedEvent.GetValue(instance) as Action)?.Invoke();
                break;
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MuseumWeaponEntry), nameof(MuseumWeaponEntry.Ap), MethodType.Getter)]
    private static void ResolveAmmunition(MuseumWeaponEntry __instance, ref AmmunitionParameters? __result)
    {
        if (__result != null || !ammunition.TryGetValue(__instance, out string name)) return;
        __result = __instance.Ap = AmmunitionParameters.GetShared(name);
        ammunition.Remove(__instance);
    }

    private static ObservableCollection<T> Entries<T>(object instance, string property) =>
        (ObservableCollection<T>)AccessTools.PropertyGetter(manager, property).Invoke(instance, null);
    private static ObservableCollection<MuseumEntry> Panel(object value) => (ObservableCollection<MuseumEntry>)value;

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> entries)
    {
        collection.Clear();
        foreach (var entry in entries) collection.Add(entry);
    }

    private static JsonSerializer CreateSerializer() => JsonSerializer.Create(settings);
    private static Snapshot? Read(Stream stream) => StartupArchives.ReadJson<Snapshot>(stream, CreateSerializer());
    private static void Write(Stream stream, Snapshot snapshot) => StartupArchives.WriteJson(stream, snapshot, CreateSerializer());

    private static readonly JsonSerializerSettings settings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        SerializationBinder = new MuseumTypes(),
        PreserveReferencesHandling = PreserveReferencesHandling.Objects,
        ContractResolver = new MuseumMembers(),
        DateParseHandling = DateParseHandling.None,
        Converters = { new LengthConverter(), new SpeedConverter(), new UnitWorkerOutput.GuidanceConverter() },
    };

    internal static JsonSerializer ReferenceSerializer() => CreateSerializer();

    internal static void BindMissile(MuseumWeaponEntry entry)
    {
        if (entry.Type == Ammunition.Type.Missile && entry.Ap == null)
            ammunition.GetValue(entry, item => item.IniRef);
    }

    private class Snapshot
    {
        [JsonRequired] public MuseumSensorEntry[] Sensors { get; set; } = [];
        [JsonRequired] public MuseumEntry[] SensorGroups { get; set; } = [];
        [JsonRequired] public MuseumWeaponEntry[] Weapons { get; set; } = [];
        [JsonRequired] public MuseumWeaponEntry[] Missiles { get; set; } = [];
        [JsonRequired] public MuseumEntry[] WeaponGroups { get; set; } = [];
        [JsonRequired] public int BaseWeaponCount { get; set; }
        [JsonRequired] public MuseumObjectEntry[] Units { get; set; } = [];
        [JsonRequired] public MuseumGunEntry[] Guns { get; set; } = [];
        [JsonRequired] public MuseumLauncherEntry[] Launchers { get; set; } = [];
        [JsonRequired] public MuseumEntry[][] Panels { get; set; } = [];
        [JsonRequired] public bool NatoUnitNames { get; set; }
        [JsonRequired] public bool NatoWeaponNames { get; set; }
        [JsonRequired] public string Culture { get; set; } = "";
        public string SensorStamp { get; set; } = "";

        internal bool CanRestore => Panels.Length == 8 && BaseWeaponCount >= 0 && BaseWeaponCount <= Weapons.Length
            && NatoUnitNames == OptionsManager.NatoUnitNamesEnabled
            && NatoWeaponNames == OptionsManager.NatoWeaponNamesEnabled
            && Culture == System.Globalization.CultureInfo.CurrentCulture.Name;
    }

    private class MuseumTypes : ISerializationBinder
    {
        private static readonly Dictionary<string, Type> types = new[]
        {
            typeof(MuseumSurfaceEntry), typeof(MuseumSubSurfaceEntry), typeof(MuseumAircraftEntry),
            typeof(MuseumLandEntry), typeof(MuseumBiologicEntry), typeof(MuseumWeaponEntry), typeof(MuseumGunEntry),
            typeof(MuseumLauncherEntry), typeof(MuseumRadarEntry), typeof(MuseumSonarEntry), typeof(MuseumEcmEntry),
            typeof(MuseumEsmEntry), typeof(MuseumVisualEntry), typeof(MuseumMadEntry), typeof(MuseumLaserDesignatorEntry)
        }.ToDictionary(type => type.Name);

        public Type BindToType(string? assemblyName, string typeName) =>
            types.TryGetValue(typeName, out var type) ? type : throw new JsonSerializationException($"Unexpected museum type {typeName}.");

        public void BindToName(Type serializedType, out string? assemblyName, out string typeName)
        {
            assemblyName = null;
            typeName = serializedType.Name;
            if (!types.ContainsKey(typeName)) throw new JsonSerializationException($"Unexpected museum type {serializedType}.");
        }
    }

    private class MuseumMembers : DefaultContractResolver
    {
        private static readonly FieldInfo description = AccessTools.Field(typeof(MuseumEntry), "description");
        private static readonly FieldInfo ammoPoints = AccessTools.Field(typeof(MuseumWeaponEntry), "<AmmoPointsCost>k__BackingField");
        private static readonly HashSet<string> ignored =
        [
            nameof(MuseumEntry.IsExpanded), nameof(MuseumEntry.IsSelected), nameof(MuseumEntry.FilteredMuseumEntries),
            nameof(MuseumWeaponEntry.Ap), nameof(MuseumObjectEntry.ActiveWeaponSystemLoadout), nameof(MuseumObjectEntry.ActiveSensorsToRemove)
        ];

        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization serialization)
        {
            var properties = base.CreateProperties(type, serialization);
            if (!typeof(MuseumEntry).IsAssignableFrom(type) && type != typeof(MuseumWeaponSystemEntry)
                && type != typeof(MuseumFireControlPart) && type != typeof(MuseumHitChanceEntry)) return properties;
            foreach (var property in properties)
            {
                if (property.UnderlyingName == nameof(MuseumEntry.Description))
                    property.ValueProvider = new ReflectionValueProvider(description);
                else if (property.DeclaringType == typeof(MuseumWeaponEntry) && property.UnderlyingName == nameof(MuseumWeaponEntry.AmmoPointsCost))
                    property.ValueProvider = new ReflectionValueProvider(ammoPoints);
            }
            return properties.Where(property => !ignored.Contains(property.UnderlyingName!)
                && (property.Writable || property.PropertyType == typeof(HashSet<string>))).ToArray();
        }

        protected override JsonArrayContract CreateArrayContract(Type type)
        {
            var contract = base.CreateArrayContract(type);
            if (!type.IsArray && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) contract.IsReference = true;
            return contract;
        }
    }

    private class LengthConverter : JsonConverter<Length>
    {
        public override void WriteJson(JsonWriter writer, Length value, JsonSerializer serializer) => writer.WriteValue(value.InMetres);
        public override Length ReadJson(JsonReader reader, Type type, Length existing, bool hasExisting, JsonSerializer serializer) =>
            Length.FromMetres(serializer.Deserialize<float>(reader));
    }

    private class SpeedConverter : JsonConverter<Speed>
    {
        public override void WriteJson(JsonWriter writer, Speed value, JsonSerializer serializer) => writer.WriteValue(value.InMetresPerSecond);
        public override Speed ReadJson(JsonReader reader, Type type, Speed existing, bool hasExisting, JsonSerializer serializer) =>
            Speed.FromMetresPerSecond(serializer.Deserialize<float>(reader));
    }
}
