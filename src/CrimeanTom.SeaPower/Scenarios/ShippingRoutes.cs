using System.Globalization;

namespace CrimeanTom.SeaPower;

internal enum RouteEncounterKind { Random, Departure, MidCrossing, PortApproach, CoastalTransit, Chokepoint }

internal record ChartPosition(double Latitude, double Longitude)
{
    // Sea Power's mission grid uses 60 NM per degree on both axes, without a cosine correction.
    internal MissionPosition RelativeTo(ChartPosition center) => new(
        (float)((Longitude - center.Longitude) * 60), 0,
        (float)((Latitude - center.Latitude) * 60));
    internal ChartPosition Offset(MissionPosition position) => new(Latitude + position.Z / 60,
        Longitude + position.X / 60);
    internal double DistanceTo(ChartPosition other)
    {
        MissionPosition delta = other.RelativeTo(this);
        return Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
    }
    internal ChartPosition Towards(ChartPosition other, double fraction) =>
        new(Latitude + (other.Latitude - Latitude) * fraction, Longitude + (other.Longitude - Longitude) * fraction);
    internal string ToMissionValue() => FormattableString.Invariant($"{Latitude:0.######},{Longitude:0.######}");
}

internal record ShippingPort(string Name, string Nation, ChartPosition Position);

// Port names resolve against the installed campaign data. Points are offshore approaches, not docks.
internal record ShippingRoute(string Region, string Origin, string Destination, IReadOnlyList<ChartPosition> Points,
    IReadOnlyList<int>? ChokepointTurns = null, IReadOnlyList<string>? LegRegions = null,
    bool OriginInRegion = true, bool DestinationInRegion = true)
{
    private bool navigabilityChecked;
    internal double LengthNm => Enumerable.Range(1, Points.Count - 1).Sum(index => Points[index - 1].DistanceTo(Points[index]));

    internal IReadOnlyList<(double Start, double End)> Windows(RouteEncounterKind kind)
    {
        List<(double Start, double End)> spans = [];
        double distance = 0;
        for (int index = 1; index < Points.Count; index++)
        {
            double next = distance + Points[index - 1].DistanceTo(Points[index]);
            if (LegRegions is null || LegRegions[index - 1] == Region)
            {
                if (spans.Count > 0 && Math.Abs(spans[^1].End - distance) < 0.01)
                    spans[^1] = (spans[^1].Start, next);
                else spans.Add((distance, next));
            }
            distance = next;
        }
        return spans.Where(span => span.End - span.Start >= 65 && kind switch
        {
            RouteEncounterKind.Departure => OriginInRegion && span.Start < 0.01,
            RouteEncounterKind.PortApproach => DestinationInRegion && Math.Abs(span.End - distance) < 0.01,
            RouteEncounterKind.CoastalTransit => OriginInRegion && span.Start < 0.01 ||
                DestinationInRegion && Math.Abs(span.End - distance) < 0.01,
            RouteEncounterKind.Chokepoint => ChokepointTurns?.Any(turn => DistanceAt(turn) >= span.Start && DistanceAt(turn) <= span.End) == true,
            _ => true,
        }).ToArray();
    }

    private double DistanceAt(int turn) => Enumerable.Range(1, turn).Sum(index => Points[index - 1].DistanceTo(Points[index]));

    internal ChartPosition At(double distance)
    {
        for (int index = 1; index < Points.Count; index++)
        {
            double length = Points[index - 1].DistanceTo(Points[index]);
            if (distance <= length) return Points[index - 1].Towards(Points[index], Math.Clamp(distance / length, 0, 1));
            distance -= length;
        }
        return Points[^1];
    }

    internal RouteEncounter Encounter(int seed, FleetClashSize size, RouteEncounterKind requested)
    {
        if (!navigabilityChecked)
        {
            for (int index = 1; index < Points.Count; index++)
                if (!ScenarioTerrain.IsNavigable(Points[index - 1], Points[index]))
                    throw new InvalidOperationException($"The {Origin} to {Destination} approach needs a water-depth review.");
            navigabilityChecked = true;
        }
        Random random = new(seed ^ 0x526F7574);
        if (requested == RouteEncounterKind.Random) throw new ArgumentException("Choose the encounter phase before the ports.", nameof(requested));
        RouteEncounterKind kind = requested;
        var windows = Windows(kind);
        if (windows.Count == 0) throw new InvalidOperationException($"The {Origin} to {Destination} route has no {kind} window in {Region}.");
        var window = windows[random.Next(windows.Count)];
        double length = Math.Min(window.End - window.Start, size switch { FleetClashSize.Small => 65, FleetClashSize.Medium => 85, _ => 110 });
        double latest = window.End - length;
        int[] turns = ChokepointTurns?.Where(turn => DistanceAt(turn) >= window.Start && DistanceAt(turn) <= window.End).ToArray() ?? [];
        double start = kind switch
        {
            // Leave navigable outbound water behind the convoy for pursuing forces.
            RouteEncounterKind.Departure => Math.Min(window.Start + 24, latest),
            RouteEncounterKind.PortApproach => latest,
            RouteEncounterKind.CoastalTransit => window.Start < 0.01 && OriginInRegion &&
                (!DestinationInRegion || random.Next(2) == 0) ? Math.Min(window.Start + 45, latest) : Math.Max(window.Start, latest - 20),
            RouteEncounterKind.Chokepoint => Math.Clamp(DistanceAt(turns[random.Next(turns.Length)]) - length * 0.55, window.Start, latest),
            _ => window.Start + (latest - window.Start) * (0.2 + random.NextDouble() * 0.5),
        };
        ChartPosition center = At(start);
        MissionPosition bearing = At(start + Math.Min(5, length)).RelativeTo(center);
        float heading = (float)(Math.Atan2(bearing.X, bearing.Z) * 180 / Math.PI);
        List<MissionWaypoint> waypoints = [];
        double traveled = 0;
        // Include the local exit explicitly even when it lies between authored waypoints.
        List<(double Distance, ChartPosition Position)> onward = [(start + length, At(start + length))];
        for (int index = 1; index < Points.Count; index++)
        {
            traveled += Points[index - 1].DistanceTo(Points[index]);
            if (traveled > start + 0.1) onward.Add((traveled, Points[index]));
        }
        onward.OrderBy(point => point.Distance).ForEach(point => waypoints.Add(new(point.Position.RelativeTo(center))));
        return new(this, kind, center, heading, (float)length, At(start + length).RelativeTo(center), waypoints, start);
    }
}

internal record RouteEncounter(ShippingRoute Route, RouteEncounterKind Kind, ChartPosition Center, float Heading,
    float LengthNm, MissionPosition Exit, IReadOnlyList<MissionWaypoint> Onward, double StartDistance = 0)
{
    internal string Summary => $"{Route.Origin} to {Route.Destination}, {Kind}";
    internal MissionPosition At(float distance) => Route.At(StartDistance + distance).RelativeTo(Center);

    internal MissionPosition Orient(MissionPosition position)
    {
        double angle = (90 - Heading) * Math.PI / 180;
        return position with
        {
            X = (float)(position.X * Math.Cos(angle) - position.Z * Math.Sin(angle)),
            Z = (float)(position.X * Math.Sin(angle) + position.Z * Math.Cos(angle)),
        };
    }

    internal FleetDeployment OrientFleet(FleetDeployment fleet) => fleet with
    {
        Units = fleet.Units.Select(unit =>
        {
            MissionPosition position = Orient(new(unit.X, 0, unit.Z));
            return unit with { X = position.X, Z = position.Z, Heading = unit.Heading + Heading - 90,
                Waypoints = unit.Waypoints?.Select(OrientWaypoint).ToArray() };
        }).ToArray(),
        Aircraft = fleet.Aircraft.Select(unit =>
        {
            MissionPosition position = Orient(new(unit.X, unit.Altitude, unit.Z));
            return unit with { X = position.X, Z = position.Z, Heading = unit.Heading + Heading - 90,
                Waypoints = unit.Waypoints?.Select(OrientWaypoint).ToArray() };
        }).ToArray(),
    };

    private MissionWaypoint OrientWaypoint(MissionWaypoint waypoint) => waypoint with
    {
        Position = Orient(waypoint.Position),
        Order = waypoint.Order is AttackAtWaypointOrder attack ? attack with { Target = Orient(attack.Target) } : waypoint.Order,
    };

    internal void WriteChart(MissionWriter mission, FleetDeployment raiders)
    {
        const float radius = 12;
        List<MissionPosition> sectors = ScenarioChart.GroupReports(raiders, Center);
        foreach (float fraction in new[] { 0.3f, 0.5f, 0.65f, 0.8f, 0.95f, 0.1f })
        foreach (float offset in new[] { -24f, 24f, -44f, 44f, -64f, 64f })
        {
            if (sectors.Count == 6) break;
            MissionPosition along = At(LengthNm * fraction);
            MissionPosition lateral = Orient(new(0, 0, offset));
            MissionPosition candidate = new(along.X + lateral.X, 0, along.Z + lateral.Z);
            if (sectors.All(sector => Math.Pow(sector.X - candidate.X, 2) + Math.Pow(sector.Z - candidate.Z, 2) >= 32 * 32) &&
                ScenarioTerrain.HeightMeters(Center.Offset(candidate)) <= -20)
                sectors.Add(candidate);
        }
        int rotation = (int)Math.Abs(StartDistance) % Math.Max(1, sectors.Count);
        sectors = sectors.Skip(rotation).Concat(sectors.Take(rotation)).ToList();
        mission.Section("MapSymbols");
        string[] symbols = Enumerable.Range(1, 8).Where(index => index > 3 || index <= sectors.Count)
            .Concat(Enumerable.Range(9, Math.Max(0, sectors.Count - 3))).Select(index => $"RouteChart{index}").ToArray();
        mission.Write("NumberOfSymbols", symbols.Length);
        symbols.ForEach((symbol, index) => mission.Write($"Symbol{index + 1}", symbol));
        mission.Set("Language_en", "RouteChart7Label_en", $"ORIGIN: {Route.Origin}");
        mission.Set("Language_en", "RouteChart8Label_en", $"DESTINATION: {Route.Destination}");
        for (int index = 1; index <= 8; index++)
        {
            if (index == 6 || index <= 3 && index > sectors.Count) continue;
            mission.Section($"RouteChart{index}");
            mission.Write("Kind", index >= 4 ? "Label" : "Circle");
            mission.Write("LabelKey", $"RouteChart{index}Label");
            mission.Write("Color", index >= 4 ? "Blue" : "Red");
            ChartPosition position = index switch
            {
                8 => Route.Points[^1],
                7 => Route.Points[0],
                5 => Center,
                4 => Center.Offset(Exit),
                _ => Center.Offset(sectors[index - 1]),
            };
            mission.Write("GeoPoint", position.ToMissionValue());
            mission.Write("RadiusNm", radius);
            mission.Write("BorderColor", index >= 4 ? "Blue" : "Red");
            mission.Write("BorderWidth", 1.5);
            mission.Write("FillOpacity", 0.04);
            mission.Write("BorderOpacity", 0.6);
            mission.Write("FillStyle", "Fill");
            mission.Write("FontSize", 12);
            mission.Write("VisibleIn", "CampaignMap,BriefingMap,Mission");
        }
        mission.Section("RouteChart6");
        // Native ThreatArrow symbols use MapPolyline: an open, unfilled line through the authored bends.
        mission.Write("Kind", "ThreatArrow");
        mission.Write("Color", "Blue");
        mission.Write("Side", "Friendly");
        mission.Write("BorderColor", "Blue");
        mission.Write("BorderWidth", 2);
        mission.Write("VisibleIn", "CampaignMap,BriefingMap,Mission");
        mission.Write("GeoPoint", Route.Points[^1].ToMissionValue());
        MissionPosition finalLeg = Route.Points[^1].RelativeTo(Route.Points[^2]);
        mission.Write("Bearing", Math.Atan2(finalLeg.X, finalLeg.Z) * 180 / Math.PI);
        mission.Write("NumberOfGeoPoints", Route.Points.Count);
        Route.Points.ForEach((point, index) => mission.Write($"GeoPoint{index + 1}", point.ToMissionValue()));
        for (int index = 3; index < sectors.Count; index++)
        {
            string symbol = $"RouteChart{index + 6}";
            mission.Set("Language_en", symbol + "Label_en", $"Search {(char)('A' + index)}");
            mission.Section(symbol);
            mission.Write("Kind", "Circle");
            mission.Write("LabelKey", symbol + "Label");
            mission.Write("GeoPoint", Center.Offset(sectors[index]).ToMissionValue());
            mission.Write("RadiusNm", radius);
            mission.Write("Color", "Red");
            mission.Write("BorderColor", "Red");
            mission.Write("BorderWidth", 1.5);
            mission.Write("FillOpacity", 0.04);
            mission.Write("BorderOpacity", 0.6);
            mission.Write("FillStyle", "Fill");
            mission.Write("FontSize", 12);
            mission.Write("VisibleIn", "CampaignMap,BriefingMap,Mission");
        }
        ScenarioChart.WriteViewport(mission, Center, sectors, Route.Points, radius);
    }
}

internal static class ShippingRoutes
{
    private static IReadOnlyDictionary<string, ShippingPort>? ports;
    private static readonly ShippingRoute[] Routes =
    [
        new("North Atlantic", "Port Halifax", "Port Reykjavik",
            [new(44.4, -63.3), new(43.5, -59), new(47, -49), new(55, -38), new(61, -27), new(63.7, -23.5), new(64.3, -23), new(64.3, -22.5), new(64.18, -22.2)]),
        new("Norwegian Sea", "Port Reykjavik", "Port Narvik",
            [new(64.18, -22.2), new(64.3, -22.5), new(64.3, -23), new(63.5, -24), new(62, -20), new(64, -5), new(66, 5), new(67, 10), new(67.5, 12.5), new(68.1, 15.2), new(68.3, 16.1)], [10]),
        new("Baltic Sea", "Port Gdansk", "Port Klaipeda",
            [new(54.65, 18.9), new(55, 19), new(55.6, 19.5), new(55.75, 20.5), new(55.75, 20.95)]),
        new("Eastern Mediterranean", "Port Alexandria", "Port Haifa",
            [new(31.5, 29.8), new(32.3, 30.5), new(33, 32), new(33.1, 34), new(32.9, 34.8)]),
        new("Arabian Sea", "Karachi", "Mumbai",
            [new(24.5, 66.7), new(22, 66), new(19, 69), new(18.5, 72.2), new(18.75, 72.6)]),
        new("South China Sea", "Singapore", "Manila",
            [new(1.25, 104.2), new(1.4, 104.55), new(3, 105.5), new(7, 110), new(12, 116), new(14.2, 119.8), new(14.25, 120.4), new(14.3, 120.7), new(14.5, 120.75)], [1, 7]),
        new("North Sea", "Port Rotterdam", "Port Hamburg",
            [new(52, 3.8), new(53, 3.7), new(54.2, 5.5), new(54.1, 7.3), new(53.95, 8.2)], [4]),
        new("Western Mediterranean", "Port Algiers", "Port Naples",
            [new(37, 3), new(38, 5), new(38.2, 7.5), new(38.4, 9.5), new(39.3, 11.5), new(40.3, 13.3), new(40.6, 14)]),
    ];
    private static ShippingRouteGraph? graph;
    private static readonly Dictionary<string, ShippingRoute[]> regionalRoutes = [];
    internal static ShippingRoute[] RegionalRoutes(string region)
    {
        if (!regionalRoutes.TryGetValue(region, out var routes))
            regionalRoutes[region] = routes = (graph ??= new(Routes)).InRegion(region).ToArray();
        return routes;
    }

    internal static RouteEncounter Pick(string region, int seed, FleetClashSize size, RouteEncounterKind kind)
    {
        Random random = new(seed ^ 0x506F7274);
        var routes = RegionalRoutes(region);
        // Region is already selected by the force generator. Roll phase before choosing either endpoint.
        if (kind == RouteEncounterKind.Random)
        {
            var phases = AvailablePhases(routes);
            if (phases.Length == 0) throw new InvalidOperationException($"No installed port route is configured for {region}.");
            kind = phases[random.Next(phases.Length)];
        }
        var available = routes.Where(route => route.Windows(kind).Count > 0).ToArray();
        if (available.Length == 0) throw new InvalidOperationException(kind == RouteEncounterKind.Chokepoint
            ? $"No chokepoint route is configured for {region}. Choose another encounter location or region."
            : $"No installed port route is configured for {region}.");
        // Each eligible ordered port pair appears once, regardless of how many regional legs it crosses.
        return available[random.Next(available.Length)].Encounter(seed, size, kind);
    }

    internal static double EncounterChance(RouteEncounterKind kind, IReadOnlyDictionary<string, double> regions)
    {
        double chance = 0;
        foreach (var region in regions)
        {
            var phases = AvailablePhases(RegionalRoutes(region.Key));
            if (phases.Contains(kind)) chance += region.Value / phases.Length;
        }
        return chance;
    }

    private static RouteEncounterKind[] AvailablePhases(ShippingRoute[] routes) =>
        Enum.GetValues(typeof(RouteEncounterKind)).Cast<RouteEncounterKind>()
            .Where(kind => kind != RouteEncounterKind.Random && routes.Any(route => route.Windows(kind).Count > 0)).ToArray();

    internal static IReadOnlyDictionary<string, ShippingPort> Ports => ports ??= LoadPorts();

    private static IReadOnlyDictionary<string, ShippingPort> LoadPorts()
    {
        Dictionary<string, ShippingPort> output = new(StringComparer.OrdinalIgnoreCase);
        string directory = Path.Combine(UnityEngine.Application.streamingAssetsPath, "original", "campaigns");
        foreach (string file in Directory.EnumerateFiles(directory, "*ports*.ini").OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var (section, values) in ScenarioUnitCatalog.ReadSections(file))
            {
                if (!values.TryGetValue("Location", out string location)) continue;
                string[] coordinates = location.Split(',');
                if (coordinates.Length < 2 || !double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) ||
                    !double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude)) continue;
                output[section] = new(section, values.TryGetValue("Nation", out string nation) ? nation : string.Empty, new(latitude, longitude));
            }
        }
        return output;
    }
}
