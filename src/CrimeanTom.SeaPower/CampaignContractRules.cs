using System;
using System.Collections.Generic;
using System.Linq;

namespace CrimeanTom.SeaPower;

// Campaign economics, independent of Unity and the native UI.
internal static class CampaignContractRules
{
    internal static bool IsVictory(string? result) =>
        result?.EndsWith("Victory", StringComparison.OrdinalIgnoreCase) == true;

    internal static int OfferSeed(int campaignSeed, int turn, int mission) =>
        unchecked(campaignSeed + turn * 7919 + mission * 104729);

    // Each board has at least one small, medium, and large job.
    internal static int OfferSize(int turn, int mission) => (mission + turn - 2) % 3;

    internal static int Payment(int size, int enemyPoints, int mission) =>
        new[] { 25, 40, 60 }[size] + (int)Math.Ceiling(enemyPoints * 0.15d) +
        (mission == 4 ? 10 : mission == 1 ? 0 : 5);

    internal static int SalvageAward(bool victory, int payment) =>
        victory ? Math.Max(5, (int)Math.Ceiling(payment * 0.5d)) : 0;

    internal static int MarketPrice(int fullPrice, float discount) =>
        Math.Max(1, (int)Math.Floor(fullPrice * (1f - Math.Clamp(discount, 0f, 0.75f))));

    // Important passage ships represent at least 60% of deployed fleet value.
    internal static string[] PassageTargets(IEnumerable<(string Reference, int Value)> units)
    {
        var ordered = units.OrderByDescending(unit => unit.Value)
            .ThenBy(unit => unit.Reference, StringComparer.Ordinal).ToArray();
        int target = (int)Math.Ceiling(ordered.Sum(unit => Math.Max(1, unit.Value)) * 0.6d);
        int value = 0;
        return ordered.TakeWhile(unit =>
        {
            if (value >= target) return false;
            value += Math.Max(1, unit.Value);
            return true;
        }).Select(unit => unit.Reference).ToArray();
    }
}
