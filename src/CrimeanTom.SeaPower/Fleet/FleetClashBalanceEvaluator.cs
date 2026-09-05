namespace CrimeanTom.SeaPower;

internal static class FleetClashBalanceEvaluator
{
    internal static double Calculate(FleetCandidate first, FleetCandidate second)
    {
        double costDifference = RelativeDifference(first.PointCost, second.PointCost);
        double surfaceDifference = RelativeDifference(first.SurfaceCapability, second.SurfaceCapability);
        double airDefenseDifference = RelativeDifference(first.AirDefenseCapability, second.AirDefenseCapability);
        double aswDifference = RelativeDifference(first.AswCapability, second.AswCapability);
        double resilienceDifference = RelativeDifference(first.Resilience, second.Resilience);
        double limitPenalty =
            Math.Max(0d, costDifference - 0.12d) * 2d +
            Math.Max(0d, surfaceDifference - 0.20d) * 1.5d +
            Math.Max(0d, airDefenseDifference - 0.25d);
        return
            (costDifference * 0.45d) +
            (surfaceDifference * 0.25d) +
            (airDefenseDifference * 0.15d) +
            (aswDifference * 0.05d) +
            (resilienceDifference * 0.10d) +
            limitPenalty;
    }

    internal static double RelativeDifference(double first, double second)
    {
        double mean = Math.Max(1d, (first + second) / 2d);
        return Math.Abs(first - second) / mean;
    }
}