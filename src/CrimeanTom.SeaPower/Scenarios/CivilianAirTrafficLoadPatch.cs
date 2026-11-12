using HarmonyLib;
using SeaPower;

namespace CrimeanTom.SeaPower;

[HarmonyPatch(typeof(SceneCreator), "LoadCivilianRoutes")]
internal static class CivilianAirTrafficLoadPatch
{
    private static void Postfix(IniHandler ini) => CivilianAirTraffic.RegisterInitialAircraft(ini);
}
