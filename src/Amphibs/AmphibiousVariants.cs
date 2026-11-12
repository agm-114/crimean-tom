using HarmonyLib;
using SeaPower;

namespace Amphibs;

[HarmonyPatch]
internal static class AmphibiousVariants
{
    private const string Folder = "land_units";
    private const string Prefix = "amphibs_";

    internal static bool IsDefaultVehicle(string id) =>
        AmphibsSettings.DefaultVehicles.Contains(id, StringComparer.OrdinalIgnoreCase);

    internal static string? SourceOf(string id) =>
        AmphibsSettings.UseVariants && id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && IsDefaultVehicle(id[Prefix.Length..]) ? id[Prefix.Length..] : null;

    internal static string Name(string original) => $"{original} (Amphibious)";

    internal static void RegisterLoadedVariants()
    {
        if (!AmphibsSettings.UseVariants || !Singleton<UnitMetadataProvider>.InstanceExists()) return;
        var catalog = Singleton<UnitMetadataProvider>.Instance;
        if (catalog.UnitData == null || catalog.UnitIdMap == null) return;

        // AnchorChain can start after the game's initial metadata pass.
        var ids = AmphibsSettings.DefaultVehicles
            .Where(source => catalog.UnitData.ContainsKey((Folder, source)))
            .Select(source => Prefix + source)
            .Where(id => !catalog.UnitData.ContainsKey((Folder, id)))
            .ToArray();
        if (ids.Length == 0) return;
        int nextId = catalog.UnitIdMap.Keys.DefaultIfEmpty(-1).Max() + 1;
        if (nextId + ids.Length > ClassInfoUtils.BitCapacity)
            throw new InvalidOperationException("The unit catalog has no room for Amphibs variants.");

        // The encyclopedia can still be enumerating the original dictionaries.
        var unitData = new Dictionary<(string, string), (int, SimpleObjectParameters)>(catalog.UnitData);
        var unitIds = new Dictionary<int, SimpleObjectParameters>(catalog.UnitIdMap);
        foreach (string id in ids)
        {
            var metadata = SimpleObjectParameters.LoadSimpleParameters(id, Folder, 0);
            unitData.Add((Folder, id), (nextId, metadata));
            unitIds.Add(nextId++, metadata);
        }
        AccessTools.PropertySetter(typeof(UnitMetadataProvider), nameof(UnitMetadataProvider.UnitData)).Invoke(catalog, [unitData]);
        AccessTools.PropertySetter(typeof(UnitMetadataProvider), nameof(UnitMetadataProvider.UnitIdMap)).Invoke(catalog, [unitIds]);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(Utils), nameof(Utils.openIniFile), [typeof(string), typeof(string), typeof(bool), typeof(bool)])]
    private static void ResolveUnitFile(string rootPath, ref string name)
    {
        if (!AmphibsSettings.UseVariants || rootPath.Trim('/') != Folder) return;
        string file = name.TrimStart('/');
        string suffix = file.EndsWith("_variants.ini", StringComparison.Ordinal) ? "_variants.ini" : ".ini";
        if (file.EndsWith(suffix, StringComparison.Ordinal) && SourceOf(file[..^suffix.Length]) is { } source)
            name = $"/{source}{suffix}";
    }

    [HarmonyPostfix, HarmonyPatch(typeof(UnitMetadataProvider), "GetIniNames")]
    private static void ListVariants(string folder, ref IEnumerable<string> __result)
    {
        if (!AmphibsSettings.UseVariants || folder != Folder) return;
        var originals = __result.ToArray();
        __result = originals.Concat(originals.Where(IsDefaultVehicle).Select(source => Prefix + source)).Distinct();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static void ReadMetadata(ref string objectIniName, string objectIniFolder, out string? __state)
    {
        __state = null;
        if (objectIniFolder != Folder || SourceOf(objectIniName) is not { } source) return;
        __state = objectIniName;
        objectIniName = source;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static void NameMetadata(SimpleObjectParameters __result, string? __state)
    {
        if (__state == null || __result == null) return;
        __result._objectIniName = __state;
        __result.DefaultName = Name(__result.DefaultName);
    }
}
