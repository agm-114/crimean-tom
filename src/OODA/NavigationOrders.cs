using SeaPower;
using CrimeanTom.SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal record ChartDestination(string Id, string Label, GeoPosition Position, string? GroupId = null);

internal static class NavigationOrders
{
    internal static ChartDestination[] Destinations(Vessel[] ships, ChartDestination? chartPoint) => ships.Where(PlayerPicture.CanNavigate)
        .SelectMany(ship => ship.GetAllWaypointsGeoPositions(getRelative: false).Take(1)
            .Select(waypoint => new ChartDestination($"route-{PlayerPicture.Id(ship)}", $"{ship.getName()} · current waypoint", waypoint.wpGP, PlayerPicture.Id(ship))))
        .Concat(chartPoint is null ? Array.Empty<ChartDestination>() : [chartPoint]).ToArray();

    internal static string SetWaypoint(Vessel ship, Taskforce side, ChartDestination destination)
    {
        if (!PlayerPicture.CanCommand(ship, side) || !PlayerPicture.CanNavigate(ship)) return "Rejected · ship is no longer commandable";
        if (destination.GroupId != null && destination.GroupId != PlayerPicture.Id(ship)) return "Rejected · waypoint belongs to another group";
        if (PlayerOrders.HasWaypoint(ship, destination.Position)) return "Kept · waypoint already active";
        if (ship.CurrentOrder.Value.OrderType != Order.Type.None || ship._patrolZone != null)
            return "Rejected · ship has a separate active order";
        GeoPosition from = ship.getGeoPosition();
        GeoPosition to = destination.Position;
        ChartPosition start = new(from._latitude, from._longitude);
        ChartPosition end = new(to._latitude, to._longitude);
        if (start.DistanceTo(end) > 150 || !ScenarioTerrain.IsNavigable(start, end, depth: 12))
            return "Rejected · direct waypoint leg is too long or crosses shallow water";

        var result = PlayerOrders.ReplaceWaypoint(ship, side, to);
        return $"{result.Disposition} · {result.Detail}";
    }
}
