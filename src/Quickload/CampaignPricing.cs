using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;
using UIElement = Noesis.UIElement;

namespace Quickload;

[HarmonyPatch]
internal static class CampaignPricing
{
    internal const string WaitMessage = "Point-based campaigns will be available when Quickload finishes calculating point values.";
    private static readonly Type rules = typeof(CampaignMenuViewModel).Assembly.GetType("SeapowerUI.ViewModels.LinearCampaignTaskForceModeRules", true)!;
    private static readonly Func<string, string> resolveCampaign = AccessTools.MethodDelegate<Func<string, string>>(
        AccessTools.Method(rules, "ResolveAuthoredCampaignPath"));
    private static readonly Func<IniHandler, TaskForceUnitAcquisitionRule> defaultRule =
        AccessTools.MethodDelegate<Func<IniHandler, TaskForceUnitAcquisitionRule>>(AccessTools.Method(rules, "ReadDefaultUnitAcquisitionRule"));
    private delegate bool ParseRule(string value, out TaskForceUnitAcquisitionRule rule);
    private static readonly ParseRule parseRule = AccessTools.MethodDelegate<ParseRule>(AccessTools.Method(rules, "TryParseUnitAcquisitionRule"));
    private static readonly Action<CampaignMenuViewModel, string> changed =
        AccessTools.MethodDelegate<Action<CampaignMenuViewModel, string>>(AccessTools.Method(typeof(CampaignMenuViewModel), "OnPropertyChanged"));
    private static readonly Action<UIElement, string, string> showLoadError =
        AccessTools.MethodDelegate<Action<UIElement, string, string>>(AccessTools.Method(typeof(CampaignMenuViewModel), "ShowCampaignLoadError"));
    private delegate bool ContinueSave(string lastSavePath, out string saveType, out string savePath);
    private static readonly ContinueSave continueSave =
        AccessTools.MethodDelegate<ContinueSave>(AccessTools.Method(typeof(MainMenuViewModel), "TryGetContinueSave"));
    private static readonly Action<MainMenuViewModel, string> mainMenuChanged =
        AccessTools.MethodDelegate<Action<MainMenuViewModel, string>>(AccessTools.Method(typeof(MainMenuViewModel), "NotifyPropertyChanged"));

    private static bool SelectedCampaignNeedsPrices(CampaignMenuViewModel menu)
    {
        if (!DeferredPointValues.Running || menu.SelectedMission is not Mission mission) return false;
        var authored = Utils.openIniFile(mission.FileName, logErrors: false);
        return HasTaskForceMode(authored) && (menu.HasTaskForceDifficultyPresets
            ? menu.UsesPointEconomy
            : defaultRule(authored) == TaskForceUnitAcquisitionRule.PointsBased);
    }

    private static bool HasTaskForceMode(IniHandler? campaign) =>
        campaign?.readValue("TaskForceMode", "Enabled", false, performLogging: false) == true;

    private static bool CampaignSaveNeedsPrices(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Utils.FileExists(path)) return false;
        var save = Utils.openIniFile(path, logErrors: false, useCaching: false);
        if (save?.doesKeyExist("File", "Version") != true) return false;
        string authoredPath = resolveCampaign(path);
        if (string.IsNullOrWhiteSpace(authoredPath) || !HasTaskForceMode(Utils.openIniFile(authoredPath, logErrors: false)))
            return false;
        string value = save.readValue("PersistentData", "TaskForceUnitAcquisitionRule", "", performLogging: false);
        return !parseRule(value, out var rule) || rule == TaskForceUnitAcquisitionRule.PointsBased;
    }

    internal static bool MissionSaveNeedsPrices(string missionPath)
    {
        if (!string.Equals(Path.GetExtension(missionPath), ".sav", StringComparison.OrdinalIgnoreCase)) return false;
        var mission = Utils.openIniFile(missionPath, logErrors: false, useCaching: false);
        return mission != null && CampaignSaveNeedsPrices(SaveLoadManager.GetCampaignSaveFilePath(mission));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MainMenuViewModel), nameof(MainMenuViewModel.CanContinue), MethodType.Getter)]
    private static void GateContinue(ref bool __result)
    {
        if (!__result || !DeferredPointValues.Running ||
            !continueSave(Path.Combine(Application.persistentDataPath, "last_save"), out string type, out string path)) return;
        __result = type == "LinearCampaign" ? !CampaignSaveNeedsPrices(path) : !MissionSaveNeedsPrices(path);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(CampaignMenuViewModel), nameof(CampaignMenuViewModel.CanPlaySelectedCampaign), MethodType.Getter)]
    private static void GatePlayButton(CampaignMenuViewModel __instance, ref bool __result) =>
        __result = __result && !SelectedCampaignNeedsPrices(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(CampaignMenuViewModel), "PlaySelectedCampaign")]
    private static bool GatePlayCommand(CampaignMenuViewModel __instance) => !SelectedCampaignNeedsPrices(__instance);

    [HarmonyPostfix, HarmonyPatch(typeof(CampaignMenuViewModel), nameof(CampaignMenuViewModel.SelectedUnitAcquisitionRule), MethodType.Setter)]
    private static void AcquisitionRuleChanged(CampaignMenuViewModel __instance) =>
        changed(__instance, nameof(CampaignMenuViewModel.CanPlaySelectedCampaign));

    internal static void Refresh()
    {
        if (Singleton<MenuSystemViewModel>.Instance.CurrentWindow is CampaignMenuView { DataContext: CampaignMenuViewModel menu })
            AcquisitionRuleChanged(menu);
        if (MainMenuView.Instance is { DataContext: MainMenuViewModel mainMenu })
            mainMenuChanged(mainMenu, nameof(MainMenuViewModel.CanContinue));
    }

    [HarmonyPatch]
    private static class SavedCampaign
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(PointValueCache).Assembly.GetType("SeaPower.LinearCampaign", true)!, "LoadLinearCampaign");

        private static bool Prefix(string filePath)
        {
            if (!DeferredPointValues.Running || !CampaignSaveNeedsPrices(filePath)) return true;
            if (Singleton<MenuSystemViewModel>.Instance.CurrentWindow is UIElement view)
                showLoadError(view, filePath, WaitMessage);
            return false;
        }
    }
}
