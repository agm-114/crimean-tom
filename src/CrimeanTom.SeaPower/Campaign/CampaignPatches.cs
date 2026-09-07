using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

[HarmonyPatch]
internal static class CampaignLatestUnitStatePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager"),
        "FindLatestCanonicalOrNativeUnitStatePrefix");

    private static bool Prefix(IniHandler stateIni, string instanceId, ref string __result)
    {
        if (!CampaignOperationsState.IsActiveCampaign) return true;
        string prefix = CampaignStateData.UnitPrefix(instanceId);
        if (!stateIni.doesSectionExist(prefix)) return true;
        __result = prefix;
        return false;
    }
}

[HarmonyPatch]
internal static class CampaignDebriefRecordedPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceDebriefViewModel"),
        "SaveDebriefSnapshot");

    private static void Prefix(MissionDebriefViewModel debrief)
    {
        if (CampaignOperationsState.IsActiveCampaign)
            CampaignOperationsState.DebriefResult = debrief.MissionResult.ToString();
    }

    private static void Postfix(object mission) =>
        CampaignOperationsState.RecordBattle(mission);

    private static void Finalizer() => CampaignOperationsState.DebriefResult = null;
}

[HarmonyPatch]
internal static class CampaignOfferSelectionPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(LinearCampaignMapViewModel), "SetSelectedMission");
    private static void Prefix(object mission) => CampaignContractOffers.Describe(mission);
}

// Native completion must still accept defeat so the game records losses and debriefs.
// Only the economics use a victory gate.
[HarmonyPatch]
internal static class CampaignContractPaymentPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceDebriefViewModel"),
        "ReadMissionIntRule");
    private static void Postfix(object mission, string key, ref int __result)
    {
        if (!CampaignOperationsState.IsActiveCampaign ||
            key is not ("TaskForceModeCompletionPoints" or "TaskForceModeCompletionCapPoints")) return;
        int index = NativeCampaignAccess.GetMissionIndex(mission);
        if (index is < 1 or > 4) return;
        var offer = CampaignContractOffers.Get(index);
        __result = !CampaignOperationsState.BattleWon(index) ? 0 :
            key == "TaskForceModeCompletionPoints" ? offer.Payment : offer.CapReward;
    }
}

[HarmonyPatch]
internal static class CampaignNoFallbackRewardPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(LinearCampaignMapViewModel), "ApplyTaskForceCompletionRewards");
    private static bool Prefix() => !CampaignOperationsState.IsActiveCampaign;
}

// The native post-state writer also pays rewards before the debrief exists.
// Gate the shared payment method so that path cannot pay the descriptor's fallback amounts.
[HarmonyPatch]
internal static class CampaignRewardApplicationPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager"),
        "ApplyCompletionRewards");
    private static void Prefix(IniHandler stateIni, int missionIndex, ref int pointReward, ref int capReward)
    {
        if (!CampaignOperationsState.IsActiveCampaign || missionIndex is < 1 or > 4) return;
        string result = CampaignOperationsState.DebriefResult ?? stateIni.readValue(
            $"Mission_{missionIndex}_TaskForceDebrief", "MissionResult", string.Empty, performLogging: false);
        var offer = CampaignContractOffers.Get(missionIndex);
        bool victory = CampaignContractRules.IsVictory(result);
        pointReward = victory ? offer.Payment : 0;
        capReward = victory ? offer.CapReward : 0;
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceBuilderViewModel), "get_CanAddSelectedItem")]
internal static class CampaignHullPurchaseAvailabilityPatch
{
    private static void Postfix(LinearCampaignTaskForceBuilderViewModel __instance, ref bool __result)
    {
        if (CampaignOperationsState.InitialPurchasingClosed && __instance.SelectedRosterItem?.IsAirUnit != true)
            __result = false;
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceBuilderViewModel), "AddSelectedItem")]
internal static class CampaignHullPurchasePatch
{
    private static bool Prefix(LinearCampaignTaskForceBuilderViewModel __instance) =>
        !CampaignOperationsState.InitialPurchasingClosed || __instance.SelectedRosterItem?.IsAirUnit == true;
}

[HarmonyPatch(typeof(LinearCampaignTaskForceBuilderViewModel), "get_CanConfirmPurchase")]
internal static class CampaignPendingHullPurchasePatch
{
    private static void Postfix(LinearCampaignTaskForceBuilderViewModel __instance, ref bool __result)
    {
        if (CampaignOperationsState.InitialPurchasingClosed && __instance.PendingPurchaseItems.Any(item => !item.IsAirUnit))
            __result = false;
    }
}

[HarmonyPatch]
internal static class CampaignDebriefClosePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceDebriefViewModel"),
        "Close");

    private static void Postfix()
    {
        if (CampaignOperationsState.HasPendingBattle)
        {
            CampaignManagementWindow.Show();
        }
    }
}

[HarmonyPatch]
internal static class CampaignPendingManagementLaunchPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeaPower.LinearCampaignMission"),
        "LaunchMission");

    private static bool Prefix(object __instance)
    {
        if (CampaignOperationsState.IsActiveCampaign && !CampaignOperationsState.HasPendingBattle)
        {
            var roster = NativeCampaignAccess.ReadCurrentSnapshot();
            if (roster.Items.Any(item => !item.IsAirUnit) &&
                !CampaignOperationsState.FilterFatiguedUnits(roster).Items.Any(item => !item.IsAirUnit))
            {
                CampaignManagementWindow.Show();
                return false;
            }
        }
        if (!CampaignOperationsState.HasPendingBattle)
        {
            return !CampaignOperationsState.IsActiveCampaign || CampaignPreparationWindow.ConfirmLaunch(__instance);
        }

        CampaignManagementWindow.Show();
        return false;
    }
}

[HarmonyPatch]
internal static class CampaignFatigueLaunchSnapshotPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager"),
        "PrepareLaunchSnapshot",
        [typeof(int), typeof(LinearCampaignTaskForceSnapshot), typeof(bool)]);

    private static void Prefix(
        ref LinearCampaignTaskForceSnapshot launchSnapshot,
        bool isReplay)
    {
        if (!isReplay)
        {
            launchSnapshot = CampaignOperationsState.FilterFatiguedUnits(launchSnapshot);
        }
    }
}
