using Noesis;
using SeaPower;
using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

internal static partial class TaskForceNationRules
{
    internal static IEnumerable<LinearCampaignTaskForceCommanderNation> GetCoalitionOptions() =>
        FactionCatalog.Coalitions.Select(coalition => new LinearCampaignTaskForceCommanderNation(
            coalition.Id,
            coalition.DisplayName,
            $"Member fleets: {string.Join(", ", coalition.MemberNations.Select(ToDisplayName))}",
            coalition.EmblemResourcePath,
            isDefault: false));

    internal static bool ShouldHideStandaloneNation(string? nation) =>
        !string.IsNullOrWhiteSpace(nation) &&
        !FactionCatalog.IsCoalition(nation) &&
        TaskForceRosterGenerator.GetShipVariantCount(nation) == 0 &&
        TaskForceRosterGenerator.HasRosterUnits(nation) &&
        FactionCatalog.Coalitions.Any(coalition => coalition.MemberNations.Contains(nation));

    internal static int GetCoalitionShipVariantCount(string? selection) =>
        FactionCatalog.IsCoalition(selection) ? GetRosterStrength(selection!).VariantCount : 0;

    internal static IEnumerable<LinearCampaignTaskForceBuilderNationFilter> GetCoalitionFilterTabs(
        string? selectedFilter) =>
        FactionCatalog.Coalitions
            .OrderByDescending(coalition => coalition.MemberNations.Sum(TaskForceRosterGenerator.GetShipVariantCount))
            .ThenBy(coalition => coalition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(coalition =>
            {
                LinearCampaignTaskForceCommanderNation option = new(
                    coalition.Id,
                    coalition.DisplayName,
                    coalition.Category,
                    coalition.EmblemResourcePath,
                    isDefault: false);
                return new LinearCampaignTaskForceBuilderNationFilter(
                    coalition.Id,
                    option.Emblem ?? ResolveCoalitionFlag(coalition.Id),
                    string.Equals(coalition.Id, selectedFilter, StringComparison.OrdinalIgnoreCase));
            });

    internal static TextureSource? ResolveCoalitionFlag(string? selection)
    {
        string flagNation = FactionCatalog.GetCoalition(selection)?.FlagNation ?? string.Empty;
        return !string.IsNullOrWhiteSpace(flagNation) &&
            UI_Parameters.NationToFlagDictionary.TryGetValue(flagNation.ToLowerInvariant(), out TextureSource flag)
                ? flag
                : null;
    }

    internal static bool NationMatchesFilter(
        string? first,
        string? second,
        StringComparison comparison)
    {
        FactionDefinition? firstCoalition = FactionCatalog.GetCoalition(first);
        if (firstCoalition is not null)
        {
            return firstCoalition.MemberNations.Contains(second ?? string.Empty);
        }

        FactionDefinition? secondCoalition = FactionCatalog.GetCoalition(second);
        if (secondCoalition is not null)
        {
            return secondCoalition.MemberNations.Contains(first ?? string.Empty);
        }

        return string.Equals(first, second, comparison);
    }

    private static string ToDisplayName(string nation) => nation.Replace('_', ' ');
}