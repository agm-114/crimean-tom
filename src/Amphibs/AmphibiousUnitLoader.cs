using HarmonyLib;
using SeaPower;

namespace Amphibs;

[HarmonyPatch(typeof(LandUnitLoader), nameof(LandUnitLoader.loadLandUnitFromInternalResources))]
internal static class AmphibiousUnitLoader
{
    [HarmonyPrefix]
    private static void LoadVariantSource(ref string iniFileName, out string? __state)
    {
        __state = null;
        if (AmphibiousVariants.SourceOf(iniFileName) is not { } source) return;
        __state = iniFileName;
        iniFileName = source;
    }

    [HarmonyPostfix]
    private static void AttachWaterEffects(LandUnitParameters lp, string? __state)
    {
        if (lp._baseObject is not LandUnit unit) return;
        if (__state != null) lp._objectIniName = __state;
        if (!AmphibiousCatalog.TryLoadProfile(lp, out var profile)) return;
        unit._performAutoFlatten = false;
        unit._maskVegetation = false;
        unit.gameObject.AddComponent<AmphibiousWake>().Initialize(unit, profile, lp);
    }
}
