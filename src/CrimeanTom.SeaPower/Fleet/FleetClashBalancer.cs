namespace CrimeanTom.SeaPower;

internal static class FleetClashBalancer
{
    private const int CandidateFleetCount = 72;
    private const int VariedMatchCount = 10;

    internal static FleetClashMatch Generate(
        System.Random random,
        FleetClashSize size,
        string? region = null)
    {
        FleetClashTheater theater = FleetClashTheaterCatalog.Pick(
            random,
            FleetClashForceGenerator.IsViable,
            region);
        FleetCandidate[] firstFleets = FleetClashForceGenerator.GenerateCandidates(
            theater.First,
            size,
            CandidateFleetCount,
            random).ToArray();
        FleetCandidate[] secondFleets = FleetClashForceGenerator.GenerateCandidates(
            theater.Second,
            size,
            CandidateFleetCount,
            random).ToArray();
        (FleetCandidate First, FleetCandidate Second, double Error)[] matches =
            (from first in firstFleets
             from second in secondFleets
             let error = FleetClashBalanceEvaluator.Calculate(first, second)
             orderby error
             select (first, second, error))
            .Take(VariedMatchCount)
            .ToArray();
        (FleetCandidate First, FleetCandidate Second, double Error) match =
            matches[random.Next(matches.Length)];

        return new FleetClashMatch(
            theater.Name,
            theater.Latitude,
            theater.Longitude,
            theater.First.DisplayName,
            theater.Second.DisplayName,
            FleetClashForceGenerator.Finalize(match.First, random),
            FleetClashForceGenerator.Finalize(match.Second, random),
            match.Error);
    }

    internal static int GetShipCount(FleetClashSize size) => size switch
    {
        FleetClashSize.Small => 3,
        FleetClashSize.Medium => 5,
        FleetClashSize.Large => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    internal static float GetMinimumSeparation(FleetClashSize size) => size switch
    {
        FleetClashSize.Small => 36f,
        FleetClashSize.Medium => 46f,
        FleetClashSize.Large => 58f,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    internal static float GetMaximumSeparation(FleetClashSize size) => size switch
    {
        FleetClashSize.Small => 48f,
        FleetClashSize.Medium => 60f,
        FleetClashSize.Large => 74f,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}