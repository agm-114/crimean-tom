using System;
using System.Collections.Generic;
using System.Linq;

using Noesis;

using SeaPower;

using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

internal static partial class TaskForceNationRules
{
    private const string CoalitionArtPath = "campaigns/crimean-tom-operations/art/coalitions";

    private static readonly CoalitionDefinition[] Coalitions =
    [
        new(
            "Coalition_NATO",
            "NATO",
            "Grand alliance",
            $"{CoalitionArtPath}/nato.png",
            ["Belgium", "Canada", "Denmark", "France", "Germany", "Greece", "Iceland", "Italy", "Netherlands", "Norway", "Portugal", "Spain", "Switzerland", "Turkey", "UK"]),
        new(
            "Coalition_WarsawPact",
            "Warsaw Pact",
            "Grand alliance",
            $"{CoalitionArtPath}/warsaw-pact.png",
            ["Bulgaria", "Cuba", "Czech", "DDR", "Hungary", "Poland", "Romania", "Vietnam"]),
        new(
            "Coalition_ArabLeague",
            "Arab League",
            "Regional coalition",
            $"{CoalitionArtPath}/arab-league.png",
            ["Algeria", "Egypt", "Iran", "Iraq", "Jordan", "Kuwait", "Libya", "Morocco", "Qatar", "Saudi", "South_Yemen", "Sudan", "Syria", "Tunisia"]),
        new(
            "Coalition_BlueDragons",
            "Blue Dragons",
            "Pacific defense coalition",
            $"{CoalitionArtPath}/blue-dragons.png",
            ["Indonesia", "Japan", "Malaysia", "Philippines", "RoC", "South_Korea", "South_Vietnam", "Thailand"]),
        new(
            "Coalition_Commonwealth",
            "Commonwealth",
            "Regional coalition",
            $"{CoalitionArtPath}/commonwealth.png",
            ["Australia", "Canada", "India", "Malaysia", "NewZealand", "Pakistan", "UK"]),
        new(
            "Coalition_EasternMediterranean",
            "Eastern Mediterranean",
            "Regional coalition",
            string.Empty,
            ["Greece", "Israel", "Turkey"],
            "Israel"),
        new(
            "Coalition_Eurocorps",
            "Eurocorps",
            "Regional coalition",
            $"{CoalitionArtPath}/eurocorps.png",
            ["Belgium", "Canada", "Denmark", "France", "Germany", "Greece", "Norway", "Portugal", "Spain", "Sweden", "Switzerland", "UK"]),
        new(
            "Coalition_UnifiedGermany",
            "Unified Germany",
            "Unified nation",
            string.Empty,
            ["DDR", "Germany"],
            "DDR"),
        new(
            "Coalition_UnifiedVietnam",
            "Unified Vietnam",
            "Unified nation",
            string.Empty,
            ["South_Vietnam", "Vietnam"],
            "Vietnam"),
        new(
            "Coalition_RedDragons",
            "Red Dragons",
            "Regional coalition",
            $"{CoalitionArtPath}/red-dragons.png",
            ["China", "North_Korea", "Vietnam"]),
        new(
            "Coalition_Scandinavia",
            "Scandinavia",
            "Regional coalition",
            $"{CoalitionArtPath}/scandinavia.png",
            ["Denmark", "Norway", "Sweden"]),
        new(
            "Coalition_SouthAmerica",
            "South America",
            "Regional coalition",
            string.Empty,
            ["Argentina", "Brazil", "Chile", "Ecuador", "Mexico", "South_Africa"],
            "Brazil"),
    ];

    internal static IEnumerable<LinearCampaignTaskForceCommanderNation> GetCoalitionOptions() =>
        Coalitions.Select(coalition => new LinearCampaignTaskForceCommanderNation(
            coalition.Id,
            coalition.DisplayName,
            $"Member fleets: {string.Join(", ", coalition.MemberNations.Select(ToDisplayName))}",
            coalition.EmblemResourcePath,
            isDefault: false));

    internal static IReadOnlyCollection<CoalitionDefinition> GetCoalitions() => Coalitions;

    internal static bool IsCoalition(string? selection) => GetCoalition(selection) is not null;

    internal static bool ShouldHideStandaloneNation(string? nation) =>
        !string.IsNullOrWhiteSpace(nation) &&
        !IsCoalition(nation) &&
        TaskForceRosterGenerator.GetShipVariantCount(nation) == 0 &&
        TaskForceRosterGenerator.HasRosterUnits(nation) &&
        Coalitions.Any(coalition => coalition.MemberNations.Contains(nation));

    internal static IReadOnlyCollection<string> GetCoalitionMemberNations(string? selection) =>
        GetCoalition(selection) is CoalitionDefinition coalition
            ? coalition.MemberNations
            : Array.Empty<string>();

    internal static string GetCoalitionDisplayName(string? selection) =>
        GetCoalition(selection)?.DisplayName ?? string.Empty;

    internal static int GetCoalitionShipVariantCount(string? selection) =>
        IsCoalition(selection) ? GetRosterStrength(selection!).VariantCount : 0;

    internal static IEnumerable<LinearCampaignTaskForceBuilderNationFilter> GetCoalitionFilterTabs(
        string? selectedFilter) =>
        Coalitions
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
        string flagNation = GetCoalition(selection)?.FlagNation ?? string.Empty;
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
        CoalitionDefinition? firstCoalition = GetCoalition(first);
        if (firstCoalition is not null)
        {
            return firstCoalition.MemberNations.Contains(second ?? string.Empty);
        }

        CoalitionDefinition? secondCoalition = GetCoalition(second);
        if (secondCoalition is not null)
        {
            return secondCoalition.MemberNations.Contains(first ?? string.Empty);
        }

        return string.Equals(first, second, comparison);
    }

    private static CoalitionDefinition? GetCoalition(string? selection) =>
        Coalitions.FirstOrDefault(coalition =>
            string.Equals(coalition.Id, selection, StringComparison.OrdinalIgnoreCase));

    private static string ToDisplayName(string nation) => nation.Replace('_', ' ');

    internal sealed class CoalitionDefinition
    {
        internal CoalitionDefinition(
            string id,
            string displayName,
            string category,
            string emblemResourcePath,
            IEnumerable<string> memberNations,
            string flagNation = "")
        {
            Id = id;
            DisplayName = displayName;
            Category = category;
            EmblemResourcePath = emblemResourcePath;
            FlagNation = flagNation;
            MemberNations = new HashSet<string>(memberNations, StringComparer.OrdinalIgnoreCase);
        }

        internal string Id { get; }

        internal string DisplayName { get; }

        internal string Category { get; }

        internal string EmblemResourcePath { get; }

        internal string FlagNation { get; }

        internal HashSet<string> MemberNations { get; }
    }
}
