namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private record RouteCorridor(float StartX, float DestinationX, FleetDeployment First, FleetDeployment Second);

    // Callers choose the attack lane so passage geometry never consumes route-operation RNG.
    private static RouteCorridor CreateRouteCorridor(
        FleetClashMatch match, FleetClashSize size, float lateralOffset, float attackLane,
        string playerTaskforce, int playerControlledCount, int seed, float? routeLength = null)
    {
        float length = routeLength ?? (size switch
        {
            FleetClashSize.Small => 48f,
            FleetClashSize.Medium => 62f,
            FleetClashSize.Large => 78f,
            _ => 62f,
        });
        float start = routeLength.HasValue ? 0 : -length / 2f;
        float destination = start + length;
        float intercept = length * 0.12f;

        FleetDeployment first = FleetOrderOfBattlePlanner.Plan(
            "Taskforce1", match.First, start + 4f, 90f, lateralOffset, length,
            playerTaskforce == "Taskforce1" ? playerControlledCount : match.First.Ships.Count,
            playerTaskforce == "Taskforce1" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce1" ? "Friendly AI" : "Enemy", seed);
        first = RetaskRouteFleet(first, destination, 90f, isDefender: true, attackLane: 0f);

        FleetDeployment second = FleetOrderOfBattlePlanner.Plan(
            "Taskforce2", match.Second, intercept + 20f, -90f, attackLane * 22f, length,
            playerTaskforce == "Taskforce2" ? playerControlledCount : match.Second.Ships.Count,
            playerTaskforce == "Taskforce2" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce2" ? "Friendly AI" : "Enemy", seed);
        second = RetaskRouteFleet(second, intercept, -90f, isDefender: false, attackLane);

        return new(start, destination, first, second);
    }

    private static FleetDeployment RetaskRouteFleet(
        FleetDeployment deployment,
        float destinationX,
        float heading,
        bool isDefender,
        float attackLane)
    {
        Dictionary<string, int> groupByReference = [];
        deployment.Formations.ForEach((formation, groupIndex) =>
            formation.References.ForEach(reference => groupByReference[reference] = groupIndex));

        HashSet<string> leaders = deployment.Formations
            .Where(formation => formation.References.Count > 0)
            .Select(formation => formation.References[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DeployedFleetUnit[] units = deployment.Units.Select(unit =>
        {
            int groupIndex = groupByReference.TryGetValue(unit.Reference, out int found)
                ? found
                : 0;
            float lane = isDefender
                ? unit.Z * 0.45f
                : attackLane * Math.Max(2f, 7f - (groupIndex * 2f));
            string? depth = unit.Ship.IsSubmarine ? "shallow" : null;
            IReadOnlyList<MissionWaypoint>? waypoints = leaders.Contains(unit.Reference) && !unit.Ship.IsCarrier
                ?
                [
                    new(new(isDefender ? Math.Min(destinationX - 4, unit.X + 8) : destinationX + 8f, 0, lane, depth)),
                    new(new(destinationX, 0, isDefender ? 0f : lane, depth), WaypointOrder.WeaponsFree),
                ]
                : unit.Waypoints;
            return unit with
            {
                Waypoints = waypoints,
                Telegraph = isDefender && !unit.Ship.IsSubmarine ? 2 : unit.Telegraph,
                Heading = unit.Ship.IsCarrier ? unit.Heading : heading,
            };
        }).ToArray();

        return new FleetDeployment(
            units,
            deployment.Formations,
            deployment.Aircraft,
            deployment.ControlledCount);
    }
}
