namespace CrimeanTom.SeaPower;

internal enum RouteAttackMode { Pursuit, Intercept, Flank, SubmarineAmbush }

internal record RouteAttackGroup(string Name, RouteAttackMode Mode, float SpeedKnots, float ConvoySpeedKnots,
    float InitialRangeNm, float CatchUpHours)
{
    internal void WriteTo(MissionWriter mission, int index)
    {
        mission.Write($"AttackGroup{index}", FormattableString.Invariant(
            $"{Name}|{Mode}|{SpeedKnots:0.##}kt|convoy {ConvoySpeedKnots:0.##}kt|{InitialRangeNm:0.##}nm|closing {CatchUpHours:0.##}h"));
    }
}

internal static class RouteInterceptionPlanner
{
    internal static RouteAttackMode ModeFor(float groupSpeed, float convoySpeed, bool submarine, bool pursuitCraft = false) =>
        submarine ? RouteAttackMode.SubmarineAmbush : pursuitCraft && groupSpeed >= convoySpeed + 8 ? RouteAttackMode.Pursuit :
        groupSpeed <= convoySpeed - 2 ? RouteAttackMode.Intercept : RouteAttackMode.Flank;

    internal static (FleetDeployment Fleet, IReadOnlyList<RouteAttackGroup> Groups) Place(
        FleetDeployment fleet, RouteEncounter voyage, float convoySpeed, IReadOnlyList<MissionPosition>? protectedPositions = null)
    {
        Dictionary<string, DeployedFleetUnit> units = fleet.Units.ToDictionary(unit => unit.Reference);
        Dictionary<int, MissionPosition> aircraftOffsets = [];
        Dictionary<string, IReadOnlyList<string>> formationOrders = [];
        List<RouteAttackGroup> reports = [];
        List<MissionPosition> occupied = [];
        protectedPositions ??= [new(0, 0, 0)];
        foreach (FleetFormationPlan formation in fleet.Formations)
        {
            DeployedFleetUnit[] members = formation.References.Where(units.ContainsKey).Select(reference => units[reference]).ToArray();
            if (members.Length == 0) continue;
            bool submarine = members.All(unit => unit.Ship.IsSubmarine);
            members = members.OrderBy(unit => ScenarioUnitCatalog.SpeedAtTelegraph(unit.Ship.UnitType, submarine ? 2 : 4)).ToArray();
            formationOrders[formation.Name] = members.Select(unit => unit.Reference).ToArray();
            float speed = members.Min(unit => ScenarioUnitCatalog.SpeedAtTelegraph(unit.Ship.UnitType, submarine ? 2 : 4));
            RouteAttackMode mode = ModeFor(speed, convoySpeed, submarine,
                members.All(unit => ScenarioUnitCatalog.IsPursuitCraft(unit.Ship.UnitType)));
            // Weapon reach is not a spawn exclusion radius. Long-range missiles otherwise push
            // raiders hundreds of miles away. Native release triggers provide the opening delay.
            float minimumRange = submarine ? 20 : mode == RouteAttackMode.Pursuit ? 8 : 26;
            float closingSpeed = Math.Max(0, speed - convoySpeed);
            // Leave at least half the local route for combat after a stern chase closes.
            float gap = Math.Max(minimumRange + 2, Math.Min(16, closingSpeed * voyage.LengthNm / Math.Max(1, convoySpeed) * 0.2f));
            DeployedFleetUnit leader = members[0];
            bool placed = false;
            for (int attempt = 0; attempt < 96; attempt++)
            {
                // A boat that cannot make a safe chase gets an ahead/flank station, never a point-blank start.
                if (attempt == 48 && mode == RouteAttackMode.Pursuit)
                {
                    mode = RouteAttackMode.Flank;
                    minimumRange = 26;
                }
                float selectedAlong = mode == RouteAttackMode.Pursuit ? -gap - attempt / 8 * 2 :
                    voyage.LengthNm * (0.36f + (reports.Count + attempt / 12) % 4 * 0.07f);
                MissionPosition anchor = Along(voyage, selectedAlong);
                MissionPosition next = Along(voyage, selectedAlong + 1);
                float direction = (float)Math.Atan2(next.X - anchor.X, next.Z - anchor.Z);
                float lateralBase = submarine || mode == RouteAttackMode.Pursuit ? 2 : 6;
                float lateral = (attempt % 2 == 0 ? 1 : -1) * (lateralBase + attempt % 12 / 2 * 2);
                MissionPosition start = anchor with { X = anchor.X + lateral * (float)Math.Cos(direction),
                    Z = anchor.Z - lateral * (float)Math.Sin(direction) };
                MissionWaypoint[] route = BuildRoute(voyage, mode, selectedAlong, submarine);
                float catchUp = mode == RouteAttackMode.Pursuit ?
                    (Math.Max(0, RouteLength(start, route) - voyage.LengthNm * 0.65f) + (members.Length - 1) * 0.75f) / Math.Max(1, closingSpeed) : 0;
                if (mode == RouteAttackMode.Pursuit && catchUp > voyage.LengthNm / Math.Max(1, convoySpeed) * 0.55f) continue;
                float releaseHours = RouteAmbushTrigger.MaximumDelaySeconds / 3600f;
                // Every group must reach its attack lane before the convoy passes it. Do not assume
                // a theoretical missile shot can rescue an unreachable interception.
                if (mode != RouteAttackMode.Pursuit && releaseHours + Distance(start, route[0].Position) / Math.Max(1, speed)
                    > selectedAlong / Math.Max(1, convoySpeed) * 0.9f) continue;
                if (mode == RouteAttackMode.Pursuit && catchUp + releaseHours > voyage.LengthNm / Math.Max(1, convoySpeed) * 0.55f) continue;
                float heading = (float)(Math.Atan2(route[0].Position.X - start.X, route[0].Position.Z - start.Z) * 180 / Math.PI);
                float formationDirection = heading * (float)Math.PI / 180;
                DeployedFleetUnit[] shifted = members.Select((unit, index) => unit with
                {
                    X = start.X - (float)Math.Sin(formationDirection) * index * 0.75f,
                    Z = start.Z - (float)Math.Cos(formationDirection) * index * 0.75f,
                    Heading = heading,
                    Telegraph = submarine ? 2 : 4,
                    Waypoints = unit.Reference == leader.Reference ? route : null,
                    FollowReference = null,
                }).ToArray();
                if (shifted.Any(unit => occupied.Any(position => Distance(new(unit.X, 0, unit.Z), position) < 2) ||
                    protectedPositions.Any(position => Distance(new(unit.X, 0, unit.Z), position) < minimumRange))) continue;
                if (!ScenarioTerrain.CanPlace(voyage, shifted)) continue;
                // Validate follower paths at their formation offsets too.
                if (shifted.Skip(1).Any(unit => !ScenarioTerrain.CanPlace(voyage, [unit with
                {
                    Waypoints = route.Select(waypoint => waypoint with { Position = waypoint.Position with
                    { X = waypoint.Position.X + unit.X - start.X, Z = waypoint.Position.Z + unit.Z - start.Z } }).ToArray(),
                }]))) continue;
                foreach (DeployedFleetUnit unit in shifted)
                {
                    DeployedFleetUnit original = units[unit.Reference];
                    aircraftOffsets[unit.SourceIndex] = new(unit.X - original.X, 0, unit.Z - original.Z);
                    units[unit.Reference] = unit;
                    occupied.Add(new(unit.X, 0, unit.Z));
                }
                reports.Add(new(formation.Name, mode, speed, convoySpeed, Distance(start, new(0, 0, 0)), catchUp));
                placed = true;
                break;
            }
            if (!placed) throw new InvalidOperationException($"No safe {mode} position for {formation.Name}. Try another encounter location or a smaller force.");
        }
        return (fleet with
        {
            Units = fleet.Units.Select(unit => units[unit.Reference]).ToArray(),
            Formations = fleet.Formations.Select(formation => formationOrders.TryGetValue(formation.Name, out var references)
                ? formation with { References = references, Pattern = "LineAstern", Spacing = 0.75f } : formation).ToArray(),
            Aircraft = fleet.Aircraft.Select(aircraft => aircraftOffsets.TryGetValue(aircraft.ParentSourceIndex, out MissionPosition offset)
                ? aircraft with { X = aircraft.X + offset.X, Z = aircraft.Z + offset.Z,
                    Waypoints = aircraft.Waypoints?.Select(waypoint => waypoint with
                    {
                        Position = waypoint.Position with { X = waypoint.Position.X + offset.X, Z = waypoint.Position.Z + offset.Z },
                        Order = waypoint.Order is AttackAtWaypointOrder attack ? attack with
                        { Target = attack.Target with { X = attack.Target.X + offset.X, Z = attack.Target.Z + offset.Z } } : waypoint.Order,
                    }).ToArray() } : aircraft).ToArray(),
        }, reports);
    }

    private static MissionWaypoint[] BuildRoute(RouteEncounter voyage, RouteAttackMode mode, float along, bool submarine)
    {
        if (submarine) return [new(voyage.At(along) with { Depth = "shallow" }, WaypointOrder.WeaponsFree),
            new(voyage.At(along + 4) with { Depth = "shallow" })];
        float join = mode == RouteAttackMode.Pursuit ? 0 : mode == RouteAttackMode.Intercept ? along : Math.Max(0, along - 4);
        List<MissionWaypoint> route = [];
        // Follow the actual route bends, rather than cutting directly across a headland to the exit.
        double traveled = 0;
        for (int index = 1; index < voyage.Route.Points.Count; index++)
        {
            traveled += voyage.Route.Points[index - 1].DistanceTo(voyage.Route.Points[index]);
            if (traveled > voyage.StartDistance + (mode == RouteAttackMode.Pursuit ? along : join) &&
                traveled < voyage.StartDistance + voyage.LengthNm * 0.65f)
                route.Add(new(voyage.Route.Points[index].RelativeTo(voyage.Center)));
        }
        if (mode != RouteAttackMode.Pursuit) route.Insert(0, new(voyage.At(join)));
        route.Add(new(voyage.At(voyage.LengthNm * 0.65f)));
        route[0] = route[0] with { Order = WaypointOrder.WeaponsFree };
        return route.ToArray();
    }

    private static MissionPosition Along(RouteEncounter voyage, float distance) => voyage.StartDistance + distance >= 0
        ? voyage.At(distance) : voyage.Orient(new(distance, 0, 0));
    private static float Distance(MissionPosition first, MissionPosition second) =>
        (float)Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Z - second.Z, 2));
    private static float RouteLength(MissionPosition start, IEnumerable<MissionWaypoint> route)
    {
        float length = 0;
        foreach (MissionWaypoint waypoint in route) { length += Distance(start, waypoint.Position); start = waypoint.Position; }
        return length;
    }
}
