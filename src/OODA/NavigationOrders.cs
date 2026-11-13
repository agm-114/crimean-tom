using SeaPower;
using CrimeanTom.SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal record ChartDestination(string Id, string Label, GeoPosition Position, string? GroupId = null);

internal static class NavigationOrders
{
    internal static string StageSubmarine(Submarine sub, Taskforce side, Vehicle contact, bool observe)
    {
        if (!PlayerOrders.CanStageSubmarine(sub, side) || !PlayerOrders.CanStageOnContact(contact)
            || contact.PositionEstimate?.Item1 is not { } fix) return "Rejected · no available submarine or live surface plot";
        double standoff = PlayerOrders.SubmarineTorpedoStandoffNm(sub);
        if (standoff <= 0) return "Rejected · no stocked anti-surface torpedoes";
        GeoPosition origin = sub.getGeoPosition();
        double distance = GeoPosition.HaversineDistanceNmi(origin, fix);
        if (distance <= standoff + 0.25) return "Kept · already near torpedo range";
        double speed = PlayerOrders.SubmarineStagingSpeed(sub);
        PlayerPicture.SurfaceMotion? motion = PlayerPicture.ObservedSurfaceMotion(contact);
        if (PlayerPicture.Intercept(origin, fix, motion, speed, standoff * 0.95, horizonHours: 1) is not { } intercept)
            return motion == null ? "Rejected · torpedo standoff is beyond a one-hour transit at the available speed"
                : "Rejected · the plotted course does not give a torpedo intercept within one hour at the available speed";
        var route = sub.GetAllWaypointsGeoPositions(getRelative: false);
        if (route.Count > 0)
        {
            double hours = GeoPosition.HaversineDistanceNmi(origin, route[^1].wpGP) / speed;
            GeoPosition atArrival = motion?.Project(fix, hours) ?? fix;
            if (hours <= 1 && GeoPosition.HaversineDistanceNmi(route[^1].wpGP, atArrival) <= standoff)
                return "Kept · current route reaches the projected torpedo envelope";
        }

        ChartPosition start = new(origin._latitude, origin._longitude);
        GeoPosition projected = intercept.Target;
        double projectedRange = GeoPosition.HaversineDistanceNmi(origin, projected);
        ChartPosition target = new(projected._latitude, projected._longitude);
        ChartPosition end = RoutePreview.Between(start, target, Math.Max(0, 1 - standoff * 0.95 / projectedRange));
        float depthMeters = Math.Max(20, (float)(-origin._height / Utils.heightInMeterToUnity(1)) + 10);
        if (end.Longitude is < -180 or > 180 || start.DistanceTo(end) > 150 || !ScenarioTerrain.IsNavigable(start, end, depthMeters))
            return "Rejected · direct staging leg crosses shallow water or exceeds chart limits";
        GeoPosition point = new() { _latitude = end.Latitude, _longitude = end.Longitude, _height = origin._height };
        string basis = motion == null ? "last plotted position, course unknown" : $"{motion.Course:0}° at {motion.Speed:0.#} kn from {motion.Source}";
        string detail = $"{intercept.Hours * 60:0.#} min to {standoff:0.#} nm torpedo standoff from {PlayerPicture.ContactLabel(contact)} · {basis}";
        if (observe) return $"Suggested submarine staging · {detail}";
        var result = PlayerOrders.ReplaceSubmarineWaypoint(sub, side, point);
        return $"{result.Disposition} · {(result.Disposition == WaypointDisposition.Applied ? detail : result.Detail)}";
    }

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

    internal static string SetWaypoint(Vessel ship, Taskforce side, ChartDestination destination, bool observe = false)
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
        if (observe) return $"Suggested waypoint · {destination.Label}";

        var result = PlayerOrders.ReplaceWaypoint(ship, side, to);
        return $"{result.Disposition} · {result.Detail}";
    }

    internal static string SetSurfaceRoute(Vessel ship, Taskforce side, GeoPosition[] route, SurfaceDefense defense, bool observe)
    {
        if (!PlayerPicture.CanCommand(ship, side) || !PlayerPicture.CanNavigate(ship)
            || ship.CurrentOrder.Value?.OrderType != Order.Type.None || ship._patrolZone != null)
            return "Rejected · ship has a separate active order or cannot navigate independently";
        GeoPosition origin = ship.getGeoPosition();
        ChartPosition start = new(origin._latitude, origin._longitude);
        double distance = 0;
        foreach (GeoPosition point in route)
        {
            ChartPosition end = new(point._latitude, point._longitude);
            distance += start.DistanceTo(end);
            if (distance > 300 || !ScenarioTerrain.IsNavigable(start, end, depth: 12))
                return "Rejected · route crosses shallow water or exceeds chart limits";
            if (defense.ApproachBlocker(side, start, end, allowWithdrawal: true) is { } blocker)
                return $"Rejected · route crosses the threat envelope of {blocker}";
            start = end;
        }
        string detail = $"{route.Length} {(route.Length == 1 ? "waypoint" : "waypoints")} · {distance:0.#} nm to {start.ToMissionValue()}";
        if (observe) return $"Suggested surface route · {detail}";
        var result = PlayerOrders.ReplaceSurfaceRoute(ship, side, route);
        return $"{result.Disposition} · {(result.Disposition == WaypointDisposition.Applied ? detail : result.Detail)}";
    }

    internal static string Approach(Vessel ship, Taskforce side, Vehicle contact, SurfaceDefense defense,
        double? standoffNm, bool observe)
    {
        if (!PlayerPicture.CanNavigate(ship) || contact.BaseObject is not (LandUnit or Vessel)
            || contact.PositionEstimate?.Item1 is not { } position) return "Rejected · no independent ship or live surface or ground position";
        if (standoffNm.HasValue && (!double.IsFinite(standoffNm.Value) || standoffNm.Value is < 8 or > 70))
            return "Rejected · support standoff must be 8 to 70 nm";
        double range = standoffNm ?? (contact.BaseObject is LandUnit target ? PlayerOrders.ShoreGunRangeNm(ship, target) : 0);
        if (!double.IsFinite(range) || range <= 0) return "Rejected · specify a support standoff or use a stocked shore-capable gun";
        GeoPosition origin = ship.getGeoPosition();
        ChartPosition start = new(origin._latitude, origin._longitude);
        ChartPosition shore = new(position._latitude, position._longitude);
        double distance = start.DistanceTo(shore);
        if (distance <= range) return standoffNm.HasValue ? "Kept · already within support standoff" : "Kept · already in nominal shore gun range";
        double desiredRange = standoffNm.HasValue ? range : 0.85 * range;
        if (contact.BaseObject is Vessel && PlayerPicture.ObservedSurfaceMotion(contact) is { } motion)
        {
            double speed = ship.SpeedCommand.Value?.CommandSpeedInKnots ?? ship._velocityInKnots;
            if (PlayerPicture.Intercept(origin, position, motion, speed, desiredRange, horizonHours: 2) is not { } intercept)
                return "Rejected · no projected support standoff within two hours at current speed";
            shore = new(intercept.Target._latitude, intercept.Target._longitude);
            distance = GeoPosition.HaversineDistanceNmi(origin, intercept.Target);
        }
        ChartPosition firingPoint = RoutePreview.Between(start, shore, Math.Max(0, 1 - desiredRange / distance));
        if (defense.ApproachBlocker(side, start, firingPoint) is { } blocker)
            return $"Rejected · approach crosses the threat envelope of {blocker}";
        string positionType = standoffNm.HasValue ? "Support position" : "Gun position";
        ChartDestination destination = new("approach", $"{positionType} for {PlayerPicture.ContactLabel(contact)}",
            new GeoPosition { _latitude = firingPoint.Latitude, _longitude = firingPoint.Longitude }, PlayerPicture.Id(ship));
        return SetWaypoint(ship, side, destination, observe);
    }
}
