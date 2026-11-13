using SeaPower;
using CrimeanTom.SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal record ChartDestination(string Id, string Label, GeoPosition Position, string? GroupId = null);

internal static class NavigationOrders
{
    internal static ChartDestination[] Destinations(Taskforce side, Vessel[] ships, ChartDestination? chartPoint)
    {
        Vehicle[] protectedContacts = SurfaceDefense.ProtectedContacts(side).Take(6).ToArray();
        return ships.Where(PlayerPicture.CanNavigate).SelectMany(ship => ShipDestinations(ship, protectedContacts))
            .Concat(chartPoint is null ? [] : [chartPoint]).ToArray();
    }

    private static IEnumerable<ChartDestination> ShipDestinations(Vessel ship, Vehicle[] protectedContacts)
    {
        string shipId = PlayerPicture.Id(ship);
        foreach (var waypoint in ship.GetAllWaypointsGeoPositions(getRelative: false).Take(1))
            yield return new($"route-{shipId}", $"{ship.getName()} · current waypoint", waypoint.wpGP, shipId);

        GeoPosition origin = ship.getGeoPosition();
        ChartPosition start = new(origin._latitude, origin._longitude);
        foreach (Vehicle contact in protectedContacts)
        {
            if (contact.PositionEstimate?.Item1 is not { } estimate) continue;
            double distance = GeoPosition.HaversineDistanceNmi(origin, estimate);
            if (distance is < 3 or > 40) continue;
            ChartPosition shipPosition = new(estimate._latitude, estimate._longitude);
            ChartPosition screen = start.Towards(shipPosition, 1 - 2 / distance);
            if (ScenarioTerrain.IsNavigable(start, screen, depth: 12))
                yield return new($"screen-{shipId}-{PlayerPicture.Id(contact)}", $"Screen {PlayerPicture.ContactLabel(contact)}",
                    new GeoPosition { _latitude = screen.Latitude, _longitude = screen.Longitude }, shipId);
        }
    }

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
