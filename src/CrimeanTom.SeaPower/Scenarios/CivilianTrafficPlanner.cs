namespace CrimeanTom.SeaPower;

internal static class CivilianTrafficPlanner
{
    private static readonly string[] Merchants =
        ["civ_ms_bulk", "civ_ms_car_carrier_a", "civ_ms_freighter_a", "civ_ms_mairangi_bay", "civ_ms_ritina"];
    private static readonly string[] FishingBoats = ["civ_fv_fishingboat_a", "civ_fv_dhow"];
    // Route-length limits are generation rules, not an aircraft performance database.
    private static readonly (string Type, double MaxRouteNm)[] Airliners =
        [("civ_c340", 1000), ("civ_il-18", 2500), ("civ_tu-154", 2500),
            ("civ_707", 6000), ("civ_dc-10", 6000), ("civ_il-62", 6000)];
    private static readonly Dictionary<(ChartPosition, ChartPosition), bool> safeLegs = [];

    internal static IReadOnlyList<CivilianTrafficUnit> Generate(int seed, string region, ChartPosition center,
        float separationNm, int vesselCount, int aircraftCount)
    {
        List<CivilianTrafficUnit> traffic = [];
        double radius = Math.Max(90, separationNm * 0.75);
        if (vesselCount > 0) AddVessels(traffic, new(seed ^ 0x43A9E27), region, center, radius, vesselCount);
        // Independent streams: switching one traffic toggle never reshuffles the other traffic type.
        if (aircraftCount > 0) AddAirliners(traffic, new(seed ^ 0x416972), center, radius * 1.5, aircraftCount);
        return traffic;
    }

    private static void AddVessels(List<CivilianTrafficUnit> traffic, Random random, string region,
        ChartPosition center, double radius, int count)
    {
        var routes = ShippingRoutes.RegionalRoutes(region)
            .Select(route => new CivilianTrafficRoute(route.Origin, route.Destination, route.Points)).ToArray();
        var windows = routes.SelectMany(route => route.Near(center, radius)).ToArray();
        if (windows.Length == 0) windows = routes.SelectMany(route => route.Near(center, radius * 2)).ToArray();
        int merchantTarget = (int)Math.Ceiling(count * 0.8);
        for (int attempt = 0; attempt < count * 12 && traffic.Count < merchantTarget && windows.Length > 0; attempt++)
        {
            var window = windows[random.Next(windows.Length)];
            var (start, onward) = window.Sample(random);
            if (!SafeRoute(window)) continue;
            var course = SpreadMerchantCourse(start, onward, random);
            if (course is null) continue;
            Add(traffic, Merchants[random.Next(Merchants.Length)], course.Value.Start, course.Value.Onward, center,
                $"{window.Route.Origin} to {window.Route.Destination}");
        }
        // Fishing remains local. Do not replace a missing shipping lane with a fleet of arbitrary merchants.
        int fishingTarget = Math.Min(count - merchantTarget, traffic.Count / 3);
        int target = traffic.Count + fishingTarget;
        for (int attempt = 0; attempt < 80 && traffic.Count < target; attempt++)
        {
            double angle = random.NextDouble() * Math.PI * 2, distance = 8 + random.NextDouble() * 45;
            ChartPosition start = center.Offset(new((float)(Math.Cos(angle) * distance), 0, (float)(Math.Sin(angle) * distance)));
            ChartPosition next = start.Offset(new((float)(random.NextDouble() * 16 - 8), 0, (float)(random.NextDouble() * 16 - 8)));
            if (start.DistanceTo(next) < 3 || !ScenarioTerrain.IsNavigable(start, next, 8)) continue;
            Add(traffic, FishingBoats[random.Next(FishingBoats.Length)], start, [next, start], center, "Local fishing grounds");
        }
    }

    private static (ChartPosition Start, ChartPosition[] Onward)? SpreadMerchantCourse(
        ChartPosition start, ChartPosition[] onward, Random random)
    {
        double length = start.DistanceTo(onward[0]);
        MissionPosition direction = onward[0].RelativeTo(start);
        double magnitude = Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        if (magnitude < 0.01) return null;
        // Independent lanes and rejoin distances, not a second parallel line shared by every merchant.
        double lane = (random.Next(2) == 0 ? -1 : 1) * Math.Min(length * 0.15, 0.3 + random.NextDouble() * 2.7);
        double firstDistance = Math.Min(length * 0.4, 12 + random.NextDouble() * 16);
        double rejoinDistance = Math.Min(length * 0.85, firstDistance + 20 + random.NextDouble() * 30);
        ChartPosition first = start.Towards(onward[0], firstDistance / length);
        ChartPosition rejoin = start.Towards(onward[0], rejoinDistance / length);
        double drift = 0.6 + random.NextDouble() * 0.6;
        foreach (double scale in new[] { 1d, 0.5, 0.2 })
        {
            ChartPosition displacedStart = Offset(start, lane * scale);
            ChartPosition displacedFirst = Offset(first, lane * scale * drift);
            if (!ScenarioTerrain.IsNavigable(displacedStart, displacedFirst) ||
                !ScenarioTerrain.IsNavigable(displacedFirst, rejoin)) continue;
            // Everything beyond the rejoin remains on the already validated shipping route.
            return (displacedStart, [displacedFirst, rejoin, .. onward]);
        }
        return null;

        ChartPosition Offset(ChartPosition point, double distance) => point.Offset(new(
            (float)(-direction.Z / magnitude * distance), 0, (float)(direction.X / magnitude * distance)));
    }

    private static void AddAirliners(List<CivilianTrafficUnit> traffic, Random random, ChartPosition center,
        double radius, int count)
    {
        // Spawn/despawn outside the initial traffic area while retaining the airport-to-airport course.
        var routes = AirlinerRoutes.Routes.SelectMany(route => route.Crossings(center, radius + 30)
            .Select(points => route with { Points = points })).ToArray();
        var windows = routes.SelectMany(route => route.Near(center, radius)).ToArray();
        int added = 0;
        for (int attempt = 0; attempt < count * 12 && added < count && windows.Length > 0; attempt++)
        {
            var window = windows[random.Next(windows.Length)];
            var (start, onward) = window.Sample(random);
            var types = Airliners.Where(type => type.MaxRouteNm >= window.Route.DistanceNm).ToArray();
            if (types.Length == 0) continue;
            var airliner = types[random.Next(types.Length)];
            bool eastbound = onward[0].Longitude > start.Longitude;
            int altitude = (airliner.Type is "civ_c340" or "civ_il-18" ? 19000 : 29000) +
                random.Next(3) * 2000 + (eastbound ? 0 : 1000);
            if (!Add(traffic, airliner.Type, start, onward, center,
                $"{window.Route.Origin} to {window.Route.Destination}", altitude)) continue;
            traffic[^1] = traffic[^1] with
            {
                AirTrafficRoute = window.Route.Points.Select(point =>
                    new MissionWaypoint(point.RelativeTo(center) with { Altitude = altitude })).ToArray(),
            };
            added++;
        }
    }

    private static bool SafeRoute(TrafficRouteWindow window)
    {
        var points = window.Route.Points;
        for (int index = window.Leg; index < points.Count; index++)
        {
            var leg = (points[index - 1], points[index]);
            if (!safeLegs.TryGetValue(leg, out bool safe))
            {
                safe = ScenarioTerrain.IsNavigable(leg.Item1, leg.Item2);
                safeLegs[leg] = safeLegs[(leg.Item2, leg.Item1)] = safe;
            }
            if (!safe) return false;
        }
        return true;
    }

    private static bool Add(List<CivilianTrafficUnit> traffic, string type, ChartPosition start,
        IReadOnlyList<ChartPosition> onward, ChartPosition center, string journey, int altitude = 0)
    {
        bool aircraft = altitude > 0;
        MissionPosition position = start.RelativeTo(center) with { Altitude = altitude };
        if (traffic.Any(unit => unit.IsAircraft == aircraft && Math.Abs(unit.Position.Altitude - altitude) < 1000 &&
            Math.Pow(unit.Position.X - position.X, 2) + Math.Pow(unit.Position.Z - position.Z, 2) < (aircraft ? 25 : 1))) return false;
        int index = traffic.Count(unit => unit.IsAircraft == aircraft) + 1;
        traffic.Add(new($"Neutral{(aircraft ? "Aircraft" : "Vessel")}{index}", type, position,
            onward.Select(point => new MissionWaypoint(point.RelativeTo(center) with { Altitude = altitude })).ToArray(), journey, aircraft));
        return true;
    }
}
