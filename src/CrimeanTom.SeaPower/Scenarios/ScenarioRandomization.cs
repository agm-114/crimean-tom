namespace CrimeanTom.SeaPower;

internal static class ScenarioRandomization
{
    internal const double PresenceChance = 0.33;
    internal static readonly int[] SeaStateWeights = [64, 20, 8, 3, 1, 1];
    internal static readonly int[] CompositionWeights = [45, 25, 15, 15];
    internal static readonly int[] RouteAttackerCompositionWeights = [5, 20, 50, 25];
    internal static readonly int[] NearbySizeWeights = [60, 30, 10];

    internal static int PickIndex(Random random, IReadOnlyList<int> weights)
    {
        int roll = random.Next(weights.Sum());
        for (int index = 0; index < weights.Count; index++)
        {
            roll -= weights[index];
            if (roll < 0) return index;
        }
        throw new InvalidOperationException("Random choices require a positive total weight.");
    }

    internal static double Chance(IReadOnlyList<int> weights, int index) => (double)weights[index] / weights.Sum();
}
