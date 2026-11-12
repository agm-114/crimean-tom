using HarmonyLib;
using SeaPower;

namespace Amphibs;

[HarmonyPatch]
internal static class AmphibiousVariants
{
    internal static void RegisterLoadedVariants()
    {
        if (!Singleton<UnitMetadataProvider>.InstanceExists()) return;
        UnitMetadataProvider catalog = Singleton<UnitMetadataProvider>.Instance;
        if (catalog.UnitData == null || catalog.UnitIdMap == null) return;

        // AnchorChain can start after the game's initial metadata pass.
        const string folder = "land_units";
        int nextId = catalog.UnitIdMap.Keys.DefaultIfEmpty(-1).Max() + 1;
        var variants = AmphibiousCatalog.Profiles.Values
            .Where(profile => catalog.UnitData.ContainsKey((folder, profile.Source)) && !catalog.UnitData.ContainsKey((folder, profile.Id)))
            .Select(profile => (profile.Id, Metadata: SimpleObjectParameters.LoadSimpleParameters(profile.Id, folder, 0)))
            .ToArray();
        if (variants.Length == 0) return;
        if (nextId + variants.Length > ClassInfoUtils.BitCapacity)
            throw new InvalidOperationException("The unit catalog has no room for Amphibs variants.");

        // The encyclopedia can still be enumerating the original dictionaries.
        var unitData = new Dictionary<(string, string), (int, SimpleObjectParameters)>(catalog.UnitData);
        var unitIds = new Dictionary<int, SimpleObjectParameters>(catalog.UnitIdMap);
        foreach (var (id, metadata) in variants)
        {
            unitData.Add((folder, id), (nextId, metadata));
            unitIds.Add(nextId++, metadata);
        }
        AccessTools.PropertySetter(typeof(UnitMetadataProvider), nameof(UnitMetadataProvider.UnitData)).Invoke(catalog, [unitData]);
        AccessTools.PropertySetter(typeof(UnitMetadataProvider), nameof(UnitMetadataProvider.UnitIdMap)).Invoke(catalog, [unitIds]);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(Utils), nameof(Utils.openIniFile), [typeof(string), typeof(string), typeof(bool), typeof(bool)])]
    private static void ResolveUnitFile(string rootPath, ref string name)
    {
        if (rootPath.Trim('/') != "land_units") return;
        string file = name.TrimStart('/');
        string suffix = file.EndsWith("_variants.ini", StringComparison.Ordinal) ? "_variants.ini" : ".ini";
        if (!file.EndsWith(suffix, StringComparison.Ordinal)) return;
        string id = file.Substring(0, file.Length - suffix.Length);
        if (AmphibiousCatalog.Profiles.TryGetValue(id, out var profile)) name = "/" + profile.Source + suffix;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(UnitMetadataProvider), "GetIniNames")]
    private static void ListVariants(string folder, ref IEnumerable<string> __result)
    {
        if (folder != "land_units") return;
        var originals = __result.ToArray();
        __result = originals.Concat(AmphibiousCatalog.Profiles.Values
            .Where(profile => originals.Contains(profile.Source)).Select(profile => profile.Id)).Distinct();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static void ReadMetadata(ref string objectIniName, string objectIniFolder, out AmphibiousProfile? __state)
    {
        __state = null;
        if (objectIniFolder != "land_units" || !AmphibiousCatalog.Profiles.TryGetValue(objectIniName, out var profile)) return;
        __state = profile;
        objectIniName = profile.Source;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static void NameMetadata(SimpleObjectParameters __result, AmphibiousProfile? __state)
    {
        if (__state == null || __result == null) return;
        __result._objectIniName = __state.Id;
        __result.DefaultName = __state.Name;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LandUnitLoader), nameof(LandUnitLoader.loadLandUnitFromInternalResources))]
    private static void LoadSource(ref string iniFileName, out AmphibiousProfile? __state)
    {
        __state = null;
        if (!AmphibiousCatalog.Profiles.TryGetValue(iniFileName, out var profile)) return;
        __state = profile;
        iniFileName = profile.Source;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LandUnitLoader), nameof(LandUnitLoader.loadLandUnitFromInternalResources))]
    private static void RestoreIdentity(LandUnitParameters lp, AmphibiousProfile? __state)
    {
        if (__state == null || lp._baseObject is not LandUnit unit) return;
        lp._objectIniName = __state.Id;
        unit._performAutoFlatten = false;
        unit._maskVegetation = false;
        unit.gameObject.AddComponent<AmphibiousWake>().Initialize(unit, __state, lp);
    }
}
