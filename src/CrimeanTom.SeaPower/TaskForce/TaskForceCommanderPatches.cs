using System.Collections.ObjectModel;
using System.Reflection;
using HarmonyLib;
using Noesis;
using SeaPower;
using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

[HarmonyPatch]
internal static class CommanderNationOptionsPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceCommanderManager"),
        "ReadCommanderNationOptions");

    private static void Postfix(
        IniHandler campaignIni,
        ref ReadOnlyCollection<LinearCampaignTaskForceCommanderNation> __result)
    {
        if (!TaskForceNationRules.IsOurCampaign(campaignIni))
        {
            return;
        }

        TaskForceRosterGenerator.RefreshBaseCosts();
        TaskForceNationRules.LogRosterDiscounts();
        bool showRare = TaskForceNationRules.ShowRareNations(campaignIni);
        List<LinearCampaignTaskForceCommanderNation> options = __result
            .Where(option =>
                !TaskForceNationRules.ShouldHideStandaloneNation(option.Nation) &&
                (showRare || !TaskForceNationRules.GetTier(option.Nation).IsRare))
            .Concat(TaskForceNationRules.GetCoalitionOptions())
            .Select(option =>
            {
                TaskForceNationRules.NationTier tier = TaskForceNationRules.GetTier(option.Nation);
                int percent = (int)Math.Round(tier.Discount * 100f);
                return new LinearCampaignTaskForceCommanderNation(
                    option.Nation,
                    $"{option.DisplayName} | {percent}%",
                    $"{tier.Label} | {tier.ClassCount} classes",
                    option.EmblemResourcePath,
                    option.IsDefault);
            })
            .OrderBy(option => TaskForceNationRules.GetTier(option.Nation).Discount)
            .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        __result = new ReadOnlyCollection<LinearCampaignTaskForceCommanderNation>(options);
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceCommanderNation), "get_Flag")]
internal static class CoalitionMissingFlagPatch
{
    private static bool Prefix(
        LinearCampaignTaskForceCommanderNation __instance,
        ref TextureSource __result)
    {
        if (!TaskForceNationRules.IsCoalition(__instance.Nation))
        {
            return true;
        }

        __result = TaskForceNationRules.ResolveCoalitionFlag(__instance.Nation)!;
        return false;
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceServiceRecordViewModel), "get_ServiceRecordWindowWidth")]
internal static class ServiceRecordWindowWidthPatch
{
    private static void Postfix(IniHandler ____campaignIni, ref float __result)
    {
        if (TaskForceNationRules.IsOurCampaign(____campaignIni))
        {
            __result = 1040f;
        }
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceServiceRecordViewModel), "get_ServiceRecordWindowHeight")]
internal static class ServiceRecordWindowHeightPatch
{
    private static void Postfix(IniHandler ____campaignIni, ref float __result)
    {
        if (TaskForceNationRules.IsOurCampaign(____campaignIni))
        {
            __result = Math.Max(__result, 760f);
        }
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceServiceRecordViewModel), "get_ServiceRecordWindowMinHeight")]
internal static class ServiceRecordWindowMinHeightPatch
{
    private static void Postfix(IniHandler ____campaignIni, ref float __result)
    {
        if (TaskForceNationRules.IsOurCampaign(____campaignIni))
        {
            __result = Math.Max(__result, 700f);
        }
    }
}

[HarmonyPatch]
internal static class CommanderRankFallbackPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceCommanderManager"),
        "ReadRanksForNation");

    private static void Postfix(
        IniHandler campaignIni,
        string nation,
        ref ReadOnlyCollection<LinearCampaignTaskForceCommanderRank> __result)
    {
        if (!TaskForceNationRules.IsOurCampaign(campaignIni) || __result.Count > 0)
        {
            return;
        }

        __result = new ReadOnlyCollection<LinearCampaignTaskForceCommanderRank>(
            new List<LinearCampaignTaskForceCommanderRank>
            {
                new(nation, "Commander", "CDR", "OF-5", 6, string.Empty),
            });
    }
}

[HarmonyPatch]
internal static class CommanderDiscountPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceCommanderManager"),
        "TryGetSameNationDiscount");

    private static bool Prefix(
        IniHandler campaignIni,
        IDictionary<string, string> persistentData,
        string unitNation,
        ref float discount,
        ref bool __result)
    {
        if (!TaskForceNationRules.IsOurCampaign(campaignIni))
        {
            return true;
        }

        discount = 0f;
        if (persistentData is null ||
            !persistentData.TryGetValue("TaskForceCommanderCommitted", out string committedValue) ||
            !bool.TryParse(committedValue, out bool committed) ||
            !committed)
        {
            __result = false;
            return false;
        }

        persistentData.TryGetValue("TaskForceCommanderNationOverride", out string discountNation);
        if (string.IsNullOrWhiteSpace(discountNation))
        {
            persistentData.TryGetValue("TaskForceCommanderNation", out discountNation);
        }

        discount = TaskForceNationRules.GetCampaignDiscount(
            persistentData,
            discountNation,
            unitNation);
        __result = discount > 0f;
        return false;
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceServiceRecordViewModel), "get_SameNationDiscountSummary")]
internal static class CommanderDiscountSummaryPatch
{
    private static void Postfix(
        LinearCampaignTaskForceServiceRecordViewModel __instance,
        IniHandler ____campaignIni,
        ref string __result)
    {
        if (!TaskForceNationRules.IsOurCampaign(____campaignIni))
        {
            return;
        }

        string nation = __instance.SelectedNationName;
        if (string.IsNullOrWhiteSpace(nation))
        {
            return;
        }

        TaskForceNationRules.NationTier tier = TaskForceNationRules.GetTier(nation);
        int percent = (int)Math.Round(tier.Discount * 100f);
        __result = TaskForceNationRules.IsCoalition(nation)
            ? $"{tier.Label}: {percent}% member discount. {tier.ClassCount} classes, {tier.VariantCount} variants."
            : $"{tier.Label}: {percent}% national discount. {tier.ClassCount} classes, {tier.VariantCount} variants.";
    }
}

[HarmonyPatch]
internal static class CommanderDiscountPersistencePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceCommanderManager"),
        "CommitCommander",
        [
            typeof(IniHandler),
            typeof(IDictionary<string, string>),
            typeof(string),
            typeof(string),
            typeof(string),
        ]);

    private static void Postfix(
        IniHandler campaignIni,
        IDictionary<string, string> persistentData,
        string nation,
        bool __result)
    {
        if (__result && TaskForceNationRules.IsOurCampaign(campaignIni))
        {
            TaskForceNationRules.SaveCampaignDiscount(persistentData, nation);
        }
    }
}

[HarmonyPatch]
internal static class CampaignRuleSeedPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceDifficultyRules"),
        "SeedPersistentDataForNewCampaign");

    private static void Postfix(string campaignPath, IDictionary<string, string> persistentData)
    {
        if (!TaskForceNationRules.IsOurCampaignPath(campaignPath))
        {
            return;
        }

        bool showRare = persistentData.TryGetValue("TaskForceRule_UnrestrictedRoster", out string value) &&
            bool.TryParse(value, out bool selected) &&
            selected;

        persistentData[TaskForceNationRules.ShowRareNationsKey] = showRare.ToString();
        persistentData.Remove(TaskForceNationRules.SavedDiscountKey);
        TaskForceNationRules.SetShowRareNationsForSession(showRare);
        persistentData["TaskForceRule_UnrestrictedRoster"] = bool.FalseString;
        persistentData["TaskForceRule_LimitUnitDates"] = bool.FalseString;
    }
}