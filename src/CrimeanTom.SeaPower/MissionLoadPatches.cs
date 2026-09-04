using HarmonyLib;

using SeaPower;

namespace CrimeanTom.SeaPower;

[HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoLoad))]
internal static class GeneratedScenarioLoadPatch
{
    private static bool _allowNextLoad;

    private static bool Prefix()
    {
        GeneratedScenarioRuntime.RestoreDebugState();

        if (_allowNextLoad)
        {
            _allowNextLoad = false;
            return true;
        }

        if (!ScenarioDebugGenerator.IsLauncherPath(Globals.currentMissionFilePath))
        {
            return true;
        }

        ScenarioSetupWindow.Show(Globals.currentMissionFilePath);
        return false;
    }

    internal static void LoadGeneratedScenario()
    {
        _allowNextLoad = true;
        MissionManager.DoLoad();
    }
}
