namespace CrimeanTom.SeaPower;

// Shared offshore waypoints connect regional routes. Unconnected or shallow-water links are never used.
internal class ShippingRouteGraph
{
    private record Leg(ChartPosition End, string Region);
    private readonly Dictionary<ChartPosition, List<Leg>> links = [];
    private readonly Dictionary<string, ChartPosition> endpoints = [];
    private readonly Dictionary<string, HashSet<string>> portRegions = [];
    private readonly HashSet<ChartPosition> chokepoints = [];
    private readonly List<ShippingRoute> paths = [];

    internal ShippingRouteGraph(IReadOnlyList<ShippingRoute> routes)
    {
        foreach (ShippingRoute route in routes)
        {
            AddPort(route.Origin, route.Points[0], route.Region);
            AddPort(route.Destination, route.Points[^1], route.Region);
            route.ChokepointTurns?.ForEach(turn => chokepoints.Add(route.Points[turn]));
            AddLane(route.Region, route.Points);
        }
        foreach (var lane in Connections)
        {
            bool navigable = Enumerable.Range(1, lane.Points.Length - 1)
                .All(index => ScenarioTerrain.IsNavigable(lane.Points[index - 1], lane.Points[index]));
            if (navigable) AddLane(lane.Region, lane.Points);
            else UnityEngine.Debug.Log($"[Crimean Tom Operations] skipping shallow shipping connection in {lane.Region}.");
        }
        foreach (var origin in endpoints) BuildPaths(origin.Key, origin.Value);
    }

    internal IEnumerable<ShippingRoute> InRegion(string region) => paths
        .Where(path => path.LegRegions!.Contains(region))
        .Select(path => path with { Region = region, OriginInRegion = portRegions[path.Origin].Contains(region),
            DestinationInRegion = portRegions[path.Destination].Contains(region) });

    private void AddPort(string name, ChartPosition position, string region)
    {
        if (!ShippingRoutes.Ports.ContainsKey(name)) return;
        endpoints[name] = position;
        if (!portRegions.TryGetValue(name, out var regions)) portRegions[name] = regions = [];
        regions.Add(region);
    }

    private void AddLane(string region, IReadOnlyList<ChartPosition> points)
    {
        foreach (ChartPosition point in points)
            if (!links.ContainsKey(point)) links[point] = [];
        for (int index = 1; index < points.Count; index++)
        {
            links[points[index - 1]].Add(new(points[index], region));
            links[points[index]].Add(new(points[index - 1], region));
        }
    }

    private void BuildPaths(string origin, ChartPosition start)
    {
        Dictionary<ChartPosition, double> distances = new() { [start] = 0 };
        Dictionary<ChartPosition, (ChartPosition Previous, string Region)> previous = [];
        HashSet<ChartPosition> remaining = [.. links.Keys];
        while (remaining.Count > 0)
        {
            ChartPosition current = remaining.OrderBy(point => distances.TryGetValue(point, out double distance) ? distance : double.MaxValue).First();
            if (!distances.TryGetValue(current, out double traveled)) break;
            remaining.Remove(current);
            foreach (Leg leg in links[current])
            {
                double distance = traveled + current.DistanceTo(leg.End);
                if (!remaining.Contains(leg.End) || distances.TryGetValue(leg.End, out double known) && known <= distance) continue;
                distances[leg.End] = distance;
                previous[leg.End] = (current, leg.Region);
            }
        }
        foreach (var destination in endpoints)
        {
            if (destination.Key == origin || !previous.ContainsKey(destination.Value)) continue;
            List<ChartPosition> points = [destination.Value];
            List<string> regions = [];
            ChartPosition cursor = destination.Value;
            while (cursor != start)
            {
                var step = previous[cursor];
                points.Add(cursor = step.Previous);
                regions.Add(step.Region);
            }
            points.Reverse();
            regions.Reverse();
            paths.Add(new(string.Empty, origin, destination.Key, points,
                Enumerable.Range(0, points.Count).Where(index => chokepoints.Contains(points[index])).ToArray(), regions));
        }
    }

    private static readonly (string Region, ChartPosition[] Points)[] Connections =
    [
        ("North Atlantic", [new(62, -20), new(61, -10), new(61.5, -4), new(61.5, 1)]),
        ("North Sea", [new(61.5, 1), new(57.8, 3), new(54.2, 5.5)]),
        ("North Sea", [new(54.2, 5.5), new(57.8, 6), new(58.1, 10), new(57.7, 11.2)]),
        ("Baltic Sea", [new(57.7, 11.2), new(56.5, 11.6),
            new(56.1, 11.1), new(55.75, 10.875), new(55.075, 11), new(54.825, 10.975), new(54.8, 10.975),
            new(54.725, 11.05), new(54.575, 11.525), new(54.525, 12.35), new(54.6, 12.5), new(54.8, 14), new(54.9, 16), new(55, 19)]),
        ("North Atlantic", [new(55, -38), new(47, -20), new(37, -12), new(35.9, -7), new(35.96, -5.65), new(36, -4.5), new(37, 3)]),
        ("Western Mediterranean", [new(39.3, 11.5), new(37.8, 11.5), new(36.5, 13), new(35, 16), new(34, 22)]),
        ("Eastern Mediterranean", [new(34, 22), new(33, 27), new(32.3, 30.5)]),
        ("Arabian Sea", [new(19, 69), new(10, 73), new(5, 78), new(5, 85), new(6, 95), new(6.5, 96), new(5, 99)]),
        ("South China Sea", [new(5, 99), new(3, 100.5),
            new(1.7, 102.9), new(1.15, 103.6), new(1.16, 103.83), new(1.4, 104.55)]),
    ];
}
