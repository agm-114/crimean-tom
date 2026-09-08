namespace CrimeanTom.SeaPower;

internal enum NearbyPresence { Off, On, Random }
internal enum NearbySize { Small, Medium, Large, Random }

internal record NearbySideSettings(NearbyPresence Naval = NearbyPresence.Off,
    NearbyPresence Coastal = NearbyPresence.Off, NearbySize Size = NearbySize.Small,
    NearbyPresence Airfields = NearbyPresence.On, int AircraftChance = 35)
{
    internal int Budget(int forcePoints, Random random)
    {
        NearbySize size = Size == NearbySize.Random
            ? (NearbySize)ScenarioRandomization.PickIndex(random, ScenarioRandomization.NearbySizeWeights) : Size;
        var (share, minimum) = size switch
        {
            NearbySize.Small => (0.10, 8), NearbySize.Medium => (0.25, 16), NearbySize.Large => (0.40, 24),
            _ => throw new ArgumentOutOfRangeException(nameof(Size)),
        };
        return Math.Min(Math.Max(0, forcePoints), Math.Max(minimum, (int)(forcePoints * share)));
    }
    internal int AirBudget(int forcePoints) => Airfields == NearbyPresence.Off && AircraftChance == 0 ? 0 :
        Math.Min(Math.Max(0, forcePoints), Math.Max(10, (int)(forcePoints * 0.25)));
    internal static bool Enabled(NearbyPresence presence, Random random) =>
        presence == NearbyPresence.On || presence == NearbyPresence.Random && random.NextDouble() < ScenarioRandomization.PresenceChance;
}

internal record ScenarioActivitySettings(NearbySideSettings Friendly, NearbySideSettings Enemy, bool SecondaryEncounters = false)
{
    internal static ScenarioActivitySettings Off { get; } = new(new(Airfields: NearbyPresence.Off, AircraftChance: 0),
        new(Airfields: NearbyPresence.Off, AircraftChance: 0));
    internal static ScenarioActivitySettings Default { get; } = new(
        new(NearbyPresence.On, NearbyPresence.On), new(Coastal: NearbyPresence.On), true);
    internal static ScenarioActivitySettings Campaign { get; } = new(
        new(NearbyPresence.Random, NearbyPresence.Random, NearbySize.Random),
        new(NearbyPresence.Random, NearbyPresence.Random, NearbySize.Random), true);
}
