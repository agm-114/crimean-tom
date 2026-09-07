using System.Globalization;
using HarmonyLib;
using SeaPower;

namespace CrimeanTom.SeaPower;

internal static partial class TaskForceNationRules
{
    private const float MinimumDiscount = 0.05f;
    private const float MaximumMultiClassDiscount = 0.70f;
    private const float MaximumDiscount = 0.75f;

    private static IReadOnlyDictionary<string, NationTier>? _tierCache;

    private static bool _discountsLogged;

    internal static NationTier GetTier(string? nation)
    {
        EnsureTierCache();
        string selection = nation?.Trim() ?? string.Empty;
        return _tierCache!.TryGetValue(selection, out NationTier tier)
            ? tier
            : CreateTier(TaskForceRosterGenerator.RosterStrength.Empty, isCoalition: false, 0d);
    }

    internal static void InvalidateRosterStrengthCache()
    {
        _tierCache = null;
        _discountsLogged = false;
    }

    internal static void LogRosterDiscounts()
    {
        if (_discountsLogged)
        {
            return;
        }

        EnsureTierCache();
        string summary = string.Join(
            ", ",
            _tierCache!
                .OrderBy(pair => pair.Value.Discount)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={(int)Math.Round(pair.Value.Discount * 100f)}%"));
        UnityEngine.Debug.Log($"[Crimean Tom Operations] calculated roster discounts: {summary}");
        _discountsLogged = true;
    }

    internal static float GetCampaignDiscount(
        IDictionary<string, string> persistentData,
        string? selection,
        string? unitNation)
    {
        if (!SelectionIncludesNation(selection, unitNation))
        {
            return 0f;
        }

        return persistentData.TryGetValue(SavedDiscountKey, out string savedValue) &&
            float.TryParse(savedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float savedDiscount)
                ? Math.Clamp(savedDiscount, MinimumDiscount, MaximumDiscount)
                : GetTier(selection).Discount;
    }

    internal static void SaveCampaignDiscount(
        IDictionary<string, string> persistentData,
        string selection)
    {
        persistentData[SavedDiscountKey] = GetTier(selection).Discount.ToString(
            "R",
            CultureInfo.InvariantCulture);
    }

    private static void EnsureTierCache()
    {
        if (_tierCache is not null)
        {
            return;
        }

        string[] selections = TaskForceRosterGenerator.GetKnownNations()
            .Concat(FactionCatalog.Coalitions.Select(coalition => coalition.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Dictionary<string, TaskForceRosterGenerator.RosterStrength> strengths =
            selections.ToDictionary(
                selection => selection,
                GetRosterStrength,
                StringComparer.OrdinalIgnoreCase);
        double[] logarithmicScores = strengths.Values
            .Where(strength => strength.Score > 0d && strength.ClassCount > 1)
            .Select(strength => Math.Log(1d + strength.Score))
            .OrderBy(score => score)
            .ToArray();

        _tierCache = strengths.ToDictionary(
            pair => pair.Key,
            pair => CreateTier(
                pair.Value,
                FactionCatalog.IsCoalition(pair.Key),
                GetStrengthPercentile(pair.Value, logarithmicScores)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static TaskForceRosterGenerator.RosterStrength GetRosterStrength(string selection)
    {
        FactionDefinition? faction = FactionCatalog.Get(selection);
        IEnumerable<string> nations = faction is null
            ? [selection]
            : faction.MemberNations;
        return TaskForceRosterGenerator.GetRosterStrength(nations);
    }

    private static NationTier CreateTier(
        TaskForceRosterGenerator.RosterStrength strength,
        bool isCoalition,
        double strengthPercentile)
    {
        bool isSingleClassFleet = strength.ClassCount <= 1;
        double rawDiscount = isSingleClassFleet
            ? MaximumDiscount
            : MaximumMultiClassDiscount -
                ((MaximumMultiClassDiscount - MinimumDiscount) * strengthPercentile);
        float discount = (float)(Math.Round(rawDiscount * 100d) / 100d);
        string label = discount switch
        {
            <= 0.10f => "Fleet power",
            <= 0.25f => "Established navy",
            <= 0.45f => "Regional navy",
            <= 0.65f => "Limited navy",
            _ => strength.ClassCount > 0 ? "Fleet detachment" : "Observer nation",
        };

        return new NationTier(
            label,
            discount,
            isRare: !isCoalition && strength.ClassCount < 3,
            strength.ClassCount,
            strength.VariantCount,
            strength.RoleCount,
            strength.Score);
    }

    private static double GetStrengthPercentile(
        TaskForceRosterGenerator.RosterStrength strength,
        IReadOnlyList<double> orderedScores)
    {
        if (strength.Score <= 0d || strength.ClassCount <= 1)
        {
            return 0d;
        }

        if (orderedScores.Count <= 1)
        {
            return 1d;
        }

        double score = Math.Log(1d + strength.Score);
        int lowerCount = orderedScores.Count(candidate => candidate < score);
        int equalCount = orderedScores.Count(candidate => Math.Abs(candidate - score) < 0.000001d);
        double middleRank = lowerCount + ((Math.Max(1, equalCount) - 1) / 2d);
        return Math.Clamp(middleRank / (orderedScores.Count - 1d), 0d, 1d);
    }

    private static bool SelectionIncludesNation(string? selection, string? unitNation)
    {
        FactionDefinition? coalition = FactionCatalog.GetCoalition(selection);
        return coalition is not null
            ? coalition.MemberNations.Contains(unitNation ?? string.Empty)
            : string.Equals(
                selection?.Trim() ?? string.Empty,
                unitNation?.Trim() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
    }

    internal class NationTier(
        string label,
        float discount,
        bool isRare,
        int classCount,
        int variantCount,
        int roleCount,
        double strengthScore)
    {
        internal string Label { get; } = label;
        internal float Discount { get; } = discount;
        internal bool IsRare { get; } = isRare;
        internal int ClassCount { get; } = classCount;
        internal int VariantCount { get; } = variantCount;
        internal int RoleCount { get; } = roleCount;
        internal double StrengthScore { get; } = strengthScore;
    }
}

[HarmonyPatch(typeof(UnitMetadataProvider), nameof(UnitMetadataProvider.Start))]
internal static class RosterCostInitializationPatch
{
    private static void Postfix()
    {
        if (TaskForceRosterGenerator.RefreshBaseCosts())
        {
            TaskForceNationRules.LogRosterDiscounts();
        }
    }
}