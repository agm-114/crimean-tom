using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SeaPower;
using Unity.Entities;
using UnityEngine;

namespace Quickload;

internal class UnitWorkerOutput
{
    private static readonly Type[] unitTypes =
    [
        typeof(SimpleObjectParameters), typeof(SimpleVesselParameters), typeof(SimpleSubmarineParameters),
        typeof(SimpleAircraftParameters), typeof(SimpleHelicopterParameters), typeof(SimpleLandUnitParameters),
        typeof(SimpleBiologicParameters)
    ];
    private static readonly FieldInfo pendingSensors = AccessTools.Field(typeof(SimpleObjectParameters), "_pendingSensorEntities");
    private static readonly FieldInfo pendingSignature = AccessTools.Field(typeof(SimpleObjectParameters), "_pendingSignature");
    private static readonly JsonSerializerSettings settings = new()
    {
        ContractResolver = new DataMembers(), ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
        TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
        Converters = { new UnitVariantConverter(), new GuidanceConverter() }
    };
    [ThreadStatic] private static JsonSerializer? serializer;
    private static JsonSerializer Serializer => serializer ??= JsonSerializer.Create(settings);

    [JsonProperty(Required = Required.Always)] public JObject Data = new();
    private SimpleObjectParameters? prepared;
    [JsonProperty(Required = Required.Always)] public string[] SensorNames = [];
    [JsonProperty(Required = Required.Always)] public int[] SignatureIds = [];
    [JsonProperty(Required = Required.Always)] public Dictionary<string, UnitLoadout> Loadouts = [];
    [JsonProperty(Required = Required.Always)] public AmmunitionReference[] Ammunition = [];
    public int Type;
    public bool HasVariants;
    public bool HasSquadrons;
    public KeyframeData[]? MachCurve;

    internal static UnitWorkerOutput Capture(SimpleObjectParameters unit, IReadOnlyDictionary<Entity, string> sensorNames)
    {
        var sig = (DetectedSignature)pendingSignature.GetValue(unit);
        var ids = new List<int>();
        for (int i = 0; i < sig.Bits.Length * 64; i++)
            if (sig.HasBit(i)) ids.Add(i);
        return new()
        {
            Type = Array.IndexOf(unitTypes, unit.GetType()),
            Data = JObject.FromObject(unit, Serializer),
            SensorNames = ((List<Entity>)pendingSensors.GetValue(unit)).Select(entity => sensorNames[entity]).ToArray(),
            SignatureIds = ids.ToArray(),
            HasVariants = unit._iniObjVariants != null,
            HasSquadrons = unit._iniSquadrons != null,
            MachCurve = unit._machLimitCurve?.keys.Select(key => new KeyframeData(key.time, key.value, key.inTangent,
                key.outTangent, key.inWeight, key.outWeight, key.weightedMode)).ToArray(),
            Loadouts = unit._loadouts.ToDictionary(pair => pair.Key, pair => new UnitLoadout(pair.Value)),
            Ammunition = unit.OnboardAmmunition.Select(item => new AmmunitionReference(item.Ammunition.Name,
                item.MagazineReferences, item.WeaponSystemsReferences)).ToArray(),
        };
    }

    internal SimpleObjectParameters Restore(string folder, string name, SensorMetadataProvider sensors,
        IReadOnlyDictionary<int, int> signatures, IReadOnlyDictionary<string, AmmunitionSimpleParameters> ammunition)
    {
        var unit = prepared ?? ReadUnit();
        if (unit._objectIniName != name || unit._objectIniFolder != folder)
            throw new JsonException("Unit identity changed.");
        string root = "/" + folder;
        unit._ini = OpenIni("");
        if (unit._ini == null) throw new IOException("Unit INI disappeared.");
        if (HasVariants) unit._iniObjVariants = OpenIni("_variants") ?? throw new IOException("Variants INI disappeared.");
        if (HasSquadrons) unit._iniSquadrons = OpenIni("_squadrons") ?? throw new IOException("Squadrons INI disappeared.");
        unit._machLimitCurve = MachCurve == null ? null : new AnimationCurve(MachCurve.Select(key => key.Restore()).ToArray());
        unit._taskForceLoadoutCosts = new(unit._taskForceLoadoutCosts, StringComparer.OrdinalIgnoreCase);
        unit._taskForceAmmoCostShares = new(unit._taskForceAmmoCostShares, StringComparer.OrdinalIgnoreCase);
        unit.SystemData = [];
        pendingSensors.SetValue(unit, SensorNames.Select(sensor => sensors.Sensors[sensor]).ToList());
        DetectedSignature signature = default;
        foreach (int id in SignatureIds) signature.SetBit(signatures[id]);
        pendingSignature.SetValue(unit, signature);
        foreach (var pair in Loadouts) unit._loadouts.Add(pair.Key, pair.Value.Restore(unit._ini));
        foreach (var item in Ammunition)
            unit.OnboardAmmunition.Add(new()
            {
                Ammunition = ammunition[item.Name],
                MagazineReferences = item.Magazines,
                WeaponSystemsReferences = item.Weapons,
            });
        return unit;

        IniHandler? OpenIni(string suffix) => Utils.openIniFile(root, "/" + name + suffix + ".ini", logErrors: false);
    }

    internal void Prepare()
    {
        // The native OnboardAircraftInfo constructor looks up aircraft in the live catalog.
        if (Data["_variants"] is JObject variants &&
            variants.Properties().Any(variant => variant.Value["Aircraft"] is JArray { Count: > 0 }))
            return;
        prepared = ReadUnit();
        Data = new();
    }

    private SimpleObjectParameters ReadUnit()
    {
        if (Type < 0 || Type >= unitTypes.Length) throw new JsonException("Unknown unit type.");
        return (SimpleObjectParameters)(Data.ToObject(unitTypes[Type], Serializer) ?? throw new JsonException("Missing unit data."));
    }

    internal static JsonSerializer CreateSerializer() => JsonSerializer.Create(settings);

    internal record AmmunitionReference(string Name, List<OnboardAmmunition.MagazineReference> Magazines,
        List<OnboardAmmunition.WeaponSystemReference> Weapons);

    internal record KeyframeData(float Time, float Value, float InTangent, float OutTangent,
        float InWeight, float OutWeight, WeightedMode Mode)
    {
        internal Keyframe Restore() => new(Time, Value, InTangent, OutTangent, InWeight, OutWeight) { weightedMode = Mode };
    }

    private class DataMembers : DefaultContractResolver
    {
        private static readonly HashSet<string> liveUnitMembers =
        [
            "_ini", "_iniObjVariants", "_iniSquadrons", "_machLimitCurve", "_loadouts", "SystemData", "_loadoutUnitBase"
        ];

        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization serialization)
        {
            var properties = base.CreateProperties(type, serialization);
            if (typeof(SimpleObjectParameters).IsAssignableFrom(type))
                return properties.Where(property => !liveUnitMembers.Contains(property.UnderlyingName!)
                    && property.Writable && property.UnderlyingName is not "DefaultName" and not "ClassName").ToArray();
            if (type == typeof(AmmunitionSimpleParameters))
                return properties.Where(property => property.UnderlyingName is not "DisplayedName" and not "DisplayedNickname").ToArray();
            return properties;
        }
    }

    private class UnitVariantConverter : JsonConverter<VariantInfo>
    {
        public override void WriteJson(JsonWriter writer, VariantInfo value, JsonSerializer json) =>
            json.Serialize(writer, new VariantData(value.Index, value.ObjectName, value.ShortName, value.NativeDefaultName,
                value.NativeClassName, value.NatoDefaultName, value.NatoClassName, value.TypeAbbr, value.TypeFullName,
                value.Nation, value.Description, value.ServiceEntryYear, value.ServiceExitYear, value.LoadoutVariant,
                value.OnboardAircraftInfos?.Select(item => new Aircraft(item.AircraftIniName, item.AircraftSquadron, item.Numbers)).ToArray()));

        public override VariantInfo ReadJson(JsonReader reader, Type objectType, VariantInfo existingValue,
            bool hasExistingValue, JsonSerializer json)
        {
            var value = json.Deserialize<VariantData>(reader) ?? throw new JsonException("Missing variant.");
            return new VariantInfo(value.Index, value.ObjectName, value.ShortName, value.NativeDefaultName,
                value.NativeClassName, value.NatoDefaultName, value.NatoClassName, value.TypeAbbr, value.TypeFullName,
                value.Nation, value.Description, value.ServiceEntryYear, value.ServiceExitYear,
                value.Aircraft?.Select(item => new OnboardAircraftInfo(item.Name, item.Squadron, item.Count)).ToList())
            { LoadoutVariant = value.LoadoutVariant };
        }
    }

    internal class GuidanceConverter : JsonConverter<AmmunitionParameters.MidCourseCorrection>
    {
        public override void WriteJson(JsonWriter writer, AmmunitionParameters.MidCourseCorrection? value, JsonSerializer json) =>
            writer.WriteValue(value?.Index);

        public override AmmunitionParameters.MidCourseCorrection? ReadJson(JsonReader reader, Type objectType,
            AmmunitionParameters.MidCourseCorrection? existingValue, bool hasExistingValue, JsonSerializer json) =>
            reader.TokenType == JsonToken.Null ? null : AmmunitionParameters.MidCourseCorrection.FromIndex(json.Deserialize<int>(reader));
    }

    private record VariantData(int Index, string ObjectName, string ShortName, string NativeDefaultName, string NativeClassName,
        string NatoDefaultName, string NatoClassName, string TypeAbbr, string TypeFullName, string Nation, string Description,
        int ServiceEntryYear, int ServiceExitYear, string LoadoutVariant, Aircraft[]? Aircraft);
    private record Aircraft(string Name, string Squadron, int Count);
}
