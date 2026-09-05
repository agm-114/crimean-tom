using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using HarmonyLib;

using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

[HarmonyPatch]
internal static class CoalitionBuilderNationFilterSortPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(LinearCampaignTaskForceBuilderViewModel),
        "RebuildNationFilters");

    private static void Prefix(string ____selectedNationFilter, out string __state) =>
        __state = ____selectedNationFilter;

    private static void Postfix(
        LinearCampaignTaskForceBuilderViewModel __instance,
        string __state,
        ref string ____selectedNationFilter)
    {
        string selectedFilter = TaskForceNationRules.IsCoalition(__state)
            ? __state
            : ____selectedNationFilter;
        if (TaskForceNationRules.IsCoalition(__state))
        {
            ____selectedNationFilter = __state;
        }

        List<LinearCampaignTaskForceBuilderNationFilter> fixedTabs = __instance.NationFilters
            .Where(filter => filter.IsAll || filter.IsDefault)
            .ToList();
        List<LinearCampaignTaskForceBuilderNationFilter> nationTabs = __instance.NationFilters
            .Where(filter =>
                !filter.IsAll &&
                !filter.IsDefault &&
                !TaskForceNationRules.ShouldHideStandaloneNation(filter.Nation))
            .OrderByDescending(filter => TaskForceRosterGenerator.GetShipVariantCount(filter.Nation))
            .ThenBy(filter => filter.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<LinearCampaignTaskForceBuilderNationFilter> coalitionTabs =
            TaskForceNationRules.GetCoalitionFilterTabs(selectedFilter).ToList();

        __instance.NationFilters.Clear();
        foreach (LinearCampaignTaskForceBuilderNationFilter filter in fixedTabs.Concat(coalitionTabs).Concat(nationTabs))
        {
            filter.IsSelected = string.Equals(filter.Nation, selectedFilter, StringComparison.OrdinalIgnoreCase);
            __instance.NationFilters.Add(filter);
        }
    }
}

[HarmonyPatch]
internal static class CoalitionBuilderRosterFilterPatch
{
    private static readonly MethodInfo StringEqualsMethod = AccessTools.Method(
        typeof(string),
        nameof(string.Equals),
        [typeof(string), typeof(string), typeof(StringComparison)]);

    private static readonly MethodInfo NationMatchesFilterMethod = AccessTools.Method(
        typeof(TaskForceNationRules),
        nameof(TaskForceNationRules.NationMatchesFilter));

    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(LinearCampaignTaskForceBuilderViewModel),
        "AddFilteredRosterGroups");

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, StringEqualsMethod))
            {
                instruction.operand = NationMatchesFilterMethod;
            }

            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceBuilderNationFilter), "get_DisplayName")]
internal static class CoalitionBuilderFilterDisplayNamePatch
{
    private static void Postfix(
        LinearCampaignTaskForceBuilderNationFilter __instance,
        ref string __result)
    {
        string displayName = TaskForceNationRules.GetCoalitionDisplayName(__instance.Nation);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            __result = displayName;
        }
    }
}

[HarmonyPatch(typeof(LinearCampaignTaskForceBuilderNationFilter), "get_Tooltip")]
internal static class CoalitionBuilderFilterTooltipPatch
{
    private static void Postfix(
        LinearCampaignTaskForceBuilderNationFilter __instance,
        ref string __result)
    {
        string displayName = TaskForceNationRules.GetCoalitionDisplayName(__instance.Nation);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            int count = TaskForceNationRules.GetCoalitionShipVariantCount(__instance.Nation);
            __result = $"Show {displayName} member units ({count} ship variants)";
        }
    }
}
