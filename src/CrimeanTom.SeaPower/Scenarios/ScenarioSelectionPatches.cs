using System;
using System.Reflection;

using HarmonyLib;

using SeapowerUI;

namespace CrimeanTom.SeaPower;

[HarmonyPatch(typeof(ScenarioMenuView), MethodType.Constructor)]
internal static class ScenarioSelectionPatchInstaller
{
    private static bool _installed;

    private static void Postfix()
    {
        if (_installed)
        {
            return;
        }

        Type? viewModelType = AccessTools.TypeByName("SeapowerUI.ViewModels.ScenarioMenuViewModel");
        MethodInfo? briefingMethod = viewModelType is null
            ? null
            : AccessTools.Method(viewModelType, "TryLoadBriefing");
        MethodInfo? postfix = AccessTools.Method(
            typeof(ScenarioSelectionPatchInstaller),
            nameof(AfterBriefingLoaded));
        if (briefingMethod is null || postfix is null)
        {
            return;
        }

        new Harmony("crimean-tom.operations.scenario-selection").Patch(
            briefingMethod,
            postfix: new HarmonyMethod(postfix));
        _installed = true;
    }

    private static void AfterBriefingLoaded(object selectedMission)
    {
        PropertyInfo? fileNameProperty = AccessTools.Property(selectedMission.GetType(), "FileName");
        string? scenarioPath = fileNameProperty?.GetValue(selectedMission) as string;
        if (ScenarioDebugGenerator.IsLauncherPath(scenarioPath))
        {
            AccessTools.Property(selectedMission.GetType(), "MissionNation")?
                .SetValue(selectedMission, "Unknown");
            ScenarioSetupWindow.Show(scenarioPath!);
        }
    }
}
