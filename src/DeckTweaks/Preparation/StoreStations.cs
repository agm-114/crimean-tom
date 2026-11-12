namespace DeckTweaks.Preparation;

internal readonly struct StoreStation(string reference, int system, int number, int capacity)
{
    internal string Reference { get; } = reference;
    internal (int System, int Number) Position { get; } = (system, number);
    internal int Capacity { get; } = capacity;
}

internal class StoreStations(IEnumerable<StoreStation> selected, IEnumerable<StoreStation> available)
{
    private readonly ILookup<string, StoreStation> target = selected.ToLookup(station => station.Reference, StringComparer.OrdinalIgnoreCase);
    private readonly ILookup<string, StoreStation> possible = available.ToLookup(station => station.Reference, StringComparer.OrdinalIgnoreCase);

    internal HashSet<(int System, int Number)>? BlockedBy(IEnumerable<string> references)
    {
        HashSet<(int System, int Number)> blocked = [];
        foreach (string reference in references)
        {
            // Recovery and saves record counts, so any station that can hold this store may be occupied.
            if (!possible.Contains(reference)) return null;
            blocked.UnionWith(possible[reference].Select(station => station.Position));
        }
        return blocked;
    }

    internal int FreeCapacity(string reference, HashSet<(int System, int Number)> blocked) =>
        target[reference].Where(station => !blocked.Contains(station.Position)).Sum(station => station.Capacity);
}
