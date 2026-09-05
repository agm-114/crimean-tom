using System.Collections;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

// Sea Power's private campaign API. Resolve the singleton on each call because campaigns can change.
internal static class NativeCampaignAccess
{
    private static readonly Type? SaveManagerType = AccessTools.TypeByName(
        "SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager");
    private static readonly MethodInfo? ReadSnapshotMethod = AccessTools.Method(
        SaveManagerType, "ReadCurrentTaskForceSnapshot", Type.EmptyTypes);
    private static readonly MethodInfo? CommitPurchasesMethod = AccessTools.Method(
        SaveManagerType, "CommitBuilderPurchases");
    private static readonly MethodInfo? ApplyRewardsMethod = AccessTools.Method(
        SaveManagerType, "ApplyCompletionRewards");
    private static readonly MethodInfo? OpenStateMethod = AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceModeRules"), "OpenCampaignStateIni");
    private static readonly Type? CampaignType = AccessTools.TypeByName("SeaPower.LinearCampaign");
    private static readonly PropertyInfo? InstanceProperty = CampaignType is null
        ? null : AccessTools.Property(typeof(Singleton<>).MakeGenericType(CampaignType), "Instance");
    private static readonly FieldInfo? CampaignPathField = AccessTools.Field(CampaignType, "currentCampaignFilePath");
    private static readonly PropertyInfo? PersistentDataProperty = AccessTools.Property(CampaignType, "PersistentData");
    private static readonly PropertyInfo? MissionsProperty = AccessTools.Property(CampaignType, "Missions");
    private static readonly MethodInfo? SaveMethod = AccessTools.Method(CampaignType, "SaveCampaignPersistentData");

    internal static string? CurrentCampaignPath => GetCampaign() is { } campaign
        ? CampaignPathField?.GetValue(campaign) as string : null;
    internal static bool CanCommitPurchases => CommitPurchasesMethod is not null;

    private static object? GetCampaign() => InstanceProperty?.GetValue(null);

    internal static Dictionary<string, string>? GetPersistentData() => GetCampaign() is { } campaign
        ? PersistentDataProperty?.GetValue(campaign) as Dictionary<string, string> : null;

    internal static IEnumerable<object> GetMissions() => GetCampaign() is { } campaign &&
        MissionsProperty?.GetValue(campaign) is IEnumerable missions ? missions.Cast<object>() : [];

    internal static void SaveCampaign()
    {
        if (GetCampaign() is { } campaign) SaveMethod?.Invoke(campaign, null);
    }

    internal static IniHandler? OpenCampaignState() => OpenStateMethod?.Invoke(null, null) as IniHandler;

    internal static LinearCampaignTaskForceSnapshot ReadCurrentSnapshot() =>
        ReadSnapshotMethod?.Invoke(null, null) as LinearCampaignTaskForceSnapshot ?? LinearCampaignTaskForceSnapshot.Empty;

    internal static LinearCampaignTaskForceSnapshot? CommitPurchases(
        string taskForceName, LinearCampaignTaskForceBuilderRosterItem[] items, int remainingPoints, int capPoints) =>
        CommitPurchasesMethod?.Invoke(null, [taskForceName, items, remainingPoints, capPoints])
            as LinearCampaignTaskForceSnapshot;

    internal static void ApplyCompletionRewards(IniHandler state, int missionIndex, int payment, int capReward) =>
        ApplyRewardsMethod?.Invoke(null, [state, missionIndex, payment, capReward]);

    internal static int GetMissionIndex(object? mission) => mission is not null &&
        AccessTools.Field(mission.GetType(), "_index")?.GetValue(mission) is int index ? index : 0;

    internal static void SetMissionComplete(object mission, bool complete) =>
        AccessTools.Property(mission.GetType(), "IsComplete")?.SetValue(mission, complete);

    internal static void DescribeMission(object mission, string description, string resupplyRules, int difficulty)
    {
        if (AccessTools.Property(mission.GetType(), "LanguageDataItems")?.GetValue(mission) is IEnumerable languages)
        {
            foreach (object language in languages)
            {
                foreach (string property in new[] { "MissionIntro", "MissionDescription" })
                    AccessTools.Property(language.GetType(), property)?.SetValue(language, description);
                AccessTools.Property(language.GetType(), "MissionResupplyRules")?.SetValue(language, resupplyRules);
            }
        }
        AccessTools.Property(mission.GetType(), "MissionDifficulty")?.SetValue(mission, difficulty);
        AccessTools.Method(mission.GetType(), "NotifyPropertyChanged", [typeof(string)])?.Invoke(mission, ["SelectedLanguage"]);
    }
}