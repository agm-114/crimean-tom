namespace CrimeanTom.SeaPower;

internal enum NearbyPresence { Off, On, Random }
internal enum NearbySize { Small, Medium, Large, Random }

internal record NearbySideSettings(NearbyPresence Naval = NearbyPresence.Off,
    NearbyPresence Coastal = NearbyPresence.Off, NearbySize Size = NearbySize.Small)
{
    internal int Budget(int playerPoints, Random random) => (int)(playerPoints * ((Size == NearbySize.Random
        ? (NearbySize)ScenarioRandomization.PickIndex(random, ScenarioRandomization.NearbySizeWeights) : Size) switch
    {
        NearbySize.Small => 0.10,
        NearbySize.Medium => 0.25,
        NearbySize.Large => 0.40,
        _ => throw new ArgumentOutOfRangeException(nameof(Size)),
    }));
    internal static bool Enabled(NearbyPresence presence, Random random) =>
        presence == NearbyPresence.On || presence == NearbyPresence.Random && random.NextDouble() < ScenarioRandomization.PresenceChance;
}

internal record ScenarioActivitySettings(NearbySideSettings Friendly, NearbySideSettings Enemy, bool SecondaryEncounters = false)
{
    internal static ScenarioActivitySettings Off { get; } = new(new(), new());
    internal static ScenarioActivitySettings Campaign { get; } = new(
        new(NearbyPresence.Random, NearbyPresence.Random, NearbySize.Random),
        new(NearbyPresence.Random, NearbyPresence.Random, NearbySize.Random), true);
}
