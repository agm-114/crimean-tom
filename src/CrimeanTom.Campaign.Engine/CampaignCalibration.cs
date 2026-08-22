using System.Globalization;
using System.Text.RegularExpressions;

using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.Engine;

public sealed record CampaignCalibrationReport(
    int SchemaVersion,
    bool InEnvelope,
    int MaximumTacticalCompressionTimes,
    int HealthyTicksPerWallSecond,
    int TargetTicksPerWallSecond,
    int ObservedWeaponExpenditureCount,
    int ObservedRangeViolationCount,
    int MaximumRangeRatioMilliunits,
    int EnvelopeMaximumRangeRatioMilliunits,
    int ObservedFencedIslandCount,
    int DemotedFencedIslandCount,
    IReadOnlyList<string> Disagreements);

public static class CampaignCalibration
{
    public const int SchemaVersion = 1;
    public const int EnvelopeMaximumRangeRatioMilliunits = 1_000;
    public const int MaximumTacticalCompressionTimes = 10;
    public const int HealthyTicksPerWallSecond = 30;

    private static readonly Regex WeaponRangeDetail = new(
        @"employment at (?<distance>\d+) m \(catalog max (?<range>\d+) m\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static CampaignCalibrationReport Evaluate(
        IEnumerable<OperationalOutcome> outcomes,
        int targetTicksPerWallSecond,
        int observedFencedIslandCount,
        int demotedFencedIslandCount)
    {
        var disagreements = new List<string>();
        var expenditures = 0;
        var violations = 0;
        var maximumRatio = 0;
        foreach (var outcome in outcomes
            .Where(item => item.Kind == OperationalOutcomeKind.WeaponExpenditure)
            .OrderBy(item => item.OutcomeId, StringComparer.Ordinal))
        {
            expenditures++;
            var match = WeaponRangeDetail.Match(outcome.Detail ?? string.Empty);
            if (!match.Success
                || !long.TryParse(match.Groups["distance"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var distance)
                || !long.TryParse(match.Groups["range"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var range)
                || range <= 0)
            {
                violations++;
                disagreements.Add($"weapon-range-unparseable:{outcome.OutcomeId}");
                continue;
            }

            var ratio = distance > long.MaxValue / 1_000L
                ? int.MaxValue
                : CeilingRatioMilliunits(distance, range);
            maximumRatio = Math.Max(maximumRatio, ratio);
            if (distance > range)
            {
                violations++;
                disagreements.Add($"weapon-range-exceeds-catalog:{outcome.OutcomeId}:{ratio}");
            }
        }

        var maximumTicks = checked(HealthyTicksPerWallSecond * MaximumTacticalCompressionTimes);
        if (targetTicksPerWallSecond < 0 || targetTicksPerWallSecond > maximumTicks)
        {
            disagreements.Add($"tactical-compression-exceeds-{MaximumTacticalCompressionTimes}x:{targetTicksPerWallSecond}");
        }

        if (demotedFencedIslandCount > 0)
        {
            disagreements.Add($"fenced-island-demoted:{demotedFencedIslandCount}");
        }

        return new CampaignCalibrationReport(
            SchemaVersion,
            disagreements.Count == 0,
            MaximumTacticalCompressionTimes,
            HealthyTicksPerWallSecond,
            targetTicksPerWallSecond,
            expenditures,
            violations,
            maximumRatio,
            EnvelopeMaximumRangeRatioMilliunits,
            Math.Max(0, observedFencedIslandCount),
            Math.Max(0, demotedFencedIslandCount),
            disagreements);
    }

    private static int CeilingRatioMilliunits(long distance, long range)
    {
        var scaledDistance = distance * 1_000L;
        var quotient = Math.DivRem(scaledDistance, range, out var remainder);
        if (remainder > 0)
        {
            quotient++;
        }
        return (int)Math.Min(int.MaxValue, quotient);
    }
}
