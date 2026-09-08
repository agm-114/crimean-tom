namespace CrimeanTom.SeaPower;

internal static class FleetOrderOfBattlePlanner
{
    internal static FleetDeployment Plan(
        string taskforce,
        FleetClashForce force,
        float baseX,
        float heading,
        float baseZ,
        float engagementSeparationNm,
        int controlledCount,
        string controlledLabel,
        string delegatedLabel,
        int seed)
    {
        List<IndexedShip> indexed = MissionUnitReferences.NumberHulls(taskforce, force.Ships, ship => ship.IsSubmarine)
            .Select((entry, index) => new IndexedShip(index, entry.Unit, entry.Reference)).ToList();

        int split = Math.Clamp(controlledCount, 0, indexed.Count);
        List<FleetGroupPlan> groups = [];
        AddGroups(groups, indexed.Take(split).ToList(), controlledLabel);
        AddGroups(groups, indexed.Skip(split).ToList(), delegatedLabel);

        float direction = heading > 0f ? 1f : -1f;
        List<DeployedFleetUnit> units = [];
        List<FleetFormationPlan> formations = [];
        List<DeployedAircraft> aircraft = [];
        EmbarkedAirDeployment airDeployment = new(
            taskforce, direction, heading, engagementSeparationNm, aircraft, formations,
            new Random(seed ^ (taskforce == "Taskforce1" ? 0x48454C31 : 0x48454C32)));
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            FleetGroupPlan group = groups[groupIndex];
            (float longitudinal, float lateral) = GetGroupOffset(groupIndex, group, engagementSeparationNm);
            float groupX = baseX - (direction * longitudinal);
            float groupZ = baseZ + lateral;
            for (int memberIndex = 0; memberIndex < group.Members.Count; memberIndex++)
            {
                IndexedShip member = group.Members[memberIndex];
                (float memberLongitudinal, float memberLateral) =
                    GetMemberOffset(memberIndex, group.Kind);
                units.Add(new DeployedFleetUnit(
                    member.SourceIndex,
                    member.Ship,
                    member.Reference,
                    groupX - (direction * memberLongitudinal),
                    groupZ + memberLateral,
                    memberIndex == 0
                        ? BuildRoute(
                            groupX,
                            baseX,
                            groupZ,
                            direction,
                            groupIndex,
                            group.Kind,
                            engagementSeparationNm)
                        : null,
                    ResolveMissionType(group.Kind),
                    ResolveStationRole(group, memberIndex),
                    member.Ship.IsSubmarine ? 1 : 3,
                    group.Kind == FleetGroupKind.Carrier
                        ? NormalizeHeading(heading + 180f)
                        : heading));
            }

            formations.Add(new FleetFormationPlan(
                group.Members.Select(member => member.Reference).ToArray(),
                group.Name,
                GetFormationPattern(group.Kind),
                GetFormationSpacing(group.Kind)));

            IndexedShip? carrier = group.Members.FirstOrDefault(member => member.Ship.IsCarrier);
            CarrierAirPackage? airPackage = carrier is null
                ? null
                : ScenarioUnitCatalog.GetCarrierAirPackage(carrier.Ship.UnitType);
            if (carrier is not null && airPackage is not null)
            {
                airDeployment.Add(new(carrier, group.Name, groupX, groupZ), airPackage);
            }
            if (airPackage?.AswPatrol is not { IsHelicopter: true, AvailableCount: > 0 } &&
                group.Kind != FleetGroupKind.SubmarinePatrol)
            {
                foreach (IndexedShip host in group.Members.Where(member => !member.Ship.IsCarrier)
                    .OrderByDescending(member => member.Ship.AswCapability))
                {
                    CarrierAirPlan? patrol = ScenarioUnitCatalog.GetCarrierAirPackage(host.Ship.UnitType)?.AswPatrol;
                    if (patrol is not { IsHelicopter: true, AvailableCount: > 0 }) continue;
                    DeployedFleetUnit position = units.First(unit => unit.SourceIndex == host.SourceIndex);
                    airDeployment.AddAswPatrol(new(host, group.Name, position.X, position.Z), patrol);
                    break;
                }
            }
        }

        return new FleetDeployment(units, formations, aircraft, split);
    }

    private record EmbarkedAirStation(IndexedShip Host, string GroupName, float X, float Z);

    // One builder per fleet side, so aircraft IDs keep increasing across carrier groups.
    private class EmbarkedAirDeployment(
        string taskforce, float direction, float heading, float engagementSeparationNm,
        ICollection<DeployedAircraft> aircraft, ICollection<FleetFormationPlan> formations, Random random)
    {
        private int _aircraftNumber;
        private int _helicopterNumber;

        internal void Add(EmbarkedAirStation station, CarrierAirPackage package)
        {
            AddCombatAirPatrol(station, package.CombatAirPatrol);
            AddAirborneEarlyWarning(station, package.AirborneEarlyWarning);
            AddAswPatrol(station, package.AswPatrol);
        }

        private void AddCombatAirPatrol(EmbarkedAirStation station, CarrierAirPlan? plan)
        {
            if (plan is null)
            {
                return;
            }

            int count = Math.Min(4, plan.AvailableCount);
            for (int pairIndex = 0; pairIndex < (count + 1) / 2; pairIndex++)
            {
                List<string> pair = [];
                for (int memberIndex = 0; memberIndex < 2; memberIndex++)
                {
                    int deployedIndex = (pairIndex * 2) + memberIndex;
                    if (deployedIndex >= count)
                    {
                        break;
                    }

                    string reference = $"{taskforce}Aircraft{++_aircraftNumber}";
                    pair.Add(reference);
                    aircraft.Add(new DeployedAircraft(
                        station.Host.SourceIndex,
                        reference,
                        station.Host.Reference,
                        plan,
                        station.X + (direction * (8f + pairIndex)),
                        station.Z + (pairIndex == 0 ? 6f : -6f) + (memberIndex * 0.1f),
                        28000f,
                        memberIndex == 0
                            ? BuildAirPatrolRoute(
                                station.X,
                                station.Z,
                                direction,
                                28000f,
                                pairIndex,
                                engagementSeparationNm)
                            : null,
                        heading,
                        "CAP"));
                }

                if (pair.Count > 1)
                {
                    formations.Add(new FleetFormationPlan(
                        pair,
                        $"{station.GroupName} CAP {(char)('A' + pairIndex)}",
                        "Vic",
                        0.1f));
                }
            }
        }

        private void AddAirborneEarlyWarning(EmbarkedAirStation station, CarrierAirPlan? plan)
        {
            if (plan is null || plan.AvailableCount < 1)
            {
                return;
            }

            string unitKind = plan.IsHelicopter ? "Helicopter" : "Aircraft";
            int number = plan.IsHelicopter ? ++_helicopterNumber : ++_aircraftNumber;
            float altitude = plan.IsHelicopter ? 10000f : 30000f;
            aircraft.Add(new DeployedAircraft(
                station.Host.SourceIndex,
                $"{taskforce}{unitKind}{number}",
                station.Host.Reference,
                plan,
                station.X - (direction * 8f),
                station.Z,
                altitude,
                BuildAewOrbitRoute(
                    station.X,
                    station.Z,
                    direction,
                    altitude,
                    engagementSeparationNm),
                heading,
                "AEW"));
        }

        internal void AddAswPatrol(EmbarkedAirStation station, CarrierAirPlan? plan)
        {
            if (plan is null || !plan.IsHelicopter)
            {
                return;
            }

            int count = Math.Min(2, plan.AvailableCount);
            for (int index = 0; index < count; index++)
            {
                CarrierAirPlan patrol = plan.RollPatrolLoadout(random);
                bool surfaceAttack = patrol.MissionType == "AntiSurface";
                string reference = $"{taskforce}Helicopter{++_helicopterNumber}";
                float lane = index == 0 ? 1f : -1f;
                aircraft.Add(new DeployedAircraft(
                    station.Host.SourceIndex,
                    reference,
                    station.Host.Reference,
                    patrol,
                    station.X + (direction * 6f),
                    station.Z + (lane * 8f),
                    200f,
                    surfaceAttack
                        ? BuildAirPatrolRoute(station.X, station.Z, direction, 200f, index, engagementSeparationNm)
                        : BuildSonobuoyBarrierRoute(station.X, station.Z, direction, lane, 200f,
                            patrol.SonobuoyAmmunition, engagementSeparationNm),
                    heading,
                    surfaceAttack ? "Default" : "HeloASW"));
            }
        }
    }

    private static void AddGroups(
        ICollection<FleetGroupPlan> output,
        List<IndexedShip> ships,
        string ownership)
    {
        if (ships.Count == 0)
        {
            return;
        }

        List<IndexedShip> submarines = ships.Where(ship => ship.Ship.IsSubmarine).ToList();
        List<IndexedShip> surfaceShips = ships.Where(ship => !ship.Ship.IsSubmarine).ToList();
        int sequence = 1;

        // Leave a close escort with the main body. Small craft can scout as a separate detachment
        // instead of inheriting a cruiser's station and engaging at exactly the same time.
        if (surfaceShips.Count >= 3)
        {
            float largest = surfaceShips.Max(ship => ScenarioUnitCatalog.DisplacementTons(ship.Ship.UnitType));
            int reserve = Math.Max(1, surfaceShips.Count(ship => ship.Ship.IsCarrier) * 3);
            var screen = surfaceShips.Where(ship => !ship.Ship.IsCarrier &&
                    ScenarioUnitCatalog.DisplacementTons(ship.Ship.UnitType) <= Math.Max(2500, largest * 0.45f))
                .OrderBy(ship => ScenarioUnitCatalog.DisplacementTons(ship.Ship.UnitType))
                .Take(Math.Clamp(surfaceShips.Count - reserve, 0, 2)).ToArray();
            if (screen.Length > 0)
            {
                screen.ForEach(ship => surfaceShips.Remove(ship));
                output.Add(new(screen, $"{ownership} Forward Screen {sequence++}", FleetGroupKind.Escort));
            }
        }

        while (surfaceShips.Any(ship => ship.Ship.IsCarrier))
        {
            IndexedShip carrier = surfaceShips.First(ship => ship.Ship.IsCarrier);
            surfaceShips.Remove(carrier);
            List<IndexedShip> members = [carrier];
            AddBest(members, surfaceShips, ship => ship.Ship.AirDefenseCapability);
            AddBest(members, surfaceShips, ship => ship.Ship.AswCapability);
            AddBest(members, surfaceShips, ship => ship.Ship.CombatValue);
            output.Add(new FleetGroupPlan(
                members,
                $"{ownership} Carrier Task Group {sequence++}",
                FleetGroupKind.Carrier));
        }

        while (surfaceShips.Count > 0)
        {
            int take = ChooseSurfaceGroupSize(surfaceShips.Count);
            IndexedShip leader = surfaceShips
                .OrderByDescending(ship => ship.Ship.Role == FleetRole.Command)
                .ThenByDescending(ship => ship.Ship.SurfaceCapability)
                .ThenByDescending(ship => ship.Ship.CombatValue)
                .First();
            surfaceShips.Remove(leader);
            List<IndexedShip> members = [leader];
            while (members.Count < take && surfaceShips.Count > 0)
            {
                IndexedShip escort = surfaceShips
                    .OrderByDescending(ship => GroupFit(members, ship))
                    .First();
                surfaceShips.Remove(escort);
                members.Add(escort);
            }

            FleetGroupKind kind = members.All(member =>
                member.Ship.Role is FleetRole.Escort or FleetRole.AntiSubmarine)
                    ? FleetGroupKind.Escort
                    : FleetGroupKind.SurfaceAction;
            output.Add(new FleetGroupPlan(
                members,
                $"{ownership} {(kind == FleetGroupKind.Escort ? "Escort Division" : "Surface Action Group")} {sequence++}",
                kind));
        }

        while (submarines.Count > 0)
        {
            int take = 1;
            List<IndexedShip> members = submarines
                .OrderByDescending(ship => ship.Ship.SurfaceCapability + ship.Ship.AswCapability)
                .Take(take)
                .ToList();
            members.ForEach(member => submarines.Remove(member));

            output.Add(new FleetGroupPlan(
                members,
                $"{ownership} Submarine Patrol {sequence++}",
                FleetGroupKind.SubmarinePatrol));
        }
    }

    private static void AddBest(
        ICollection<IndexedShip> destination,
        ICollection<IndexedShip> source,
        Func<IndexedShip, double> score)
    {
        if (source.Count == 0 || destination.Count >= 4)
        {
            return;
        }

        IndexedShip? best = source
            .Where(candidate => !candidate.Ship.IsCarrier)
            .OrderByDescending(score)
            .FirstOrDefault();
        if (best is null)
        {
            return;
        }

        source.Remove(best);
        destination.Add(best);
    }

    private static int ChooseSurfaceGroupSize(int remaining) => remaining switch
    {
        <= 4 => remaining,
        5 => 3,
        6 => 3,
        7 => 4,
        _ => 4,
    };

    private static double GroupFit(IEnumerable<IndexedShip> members, IndexedShip candidate)
    {
        bool hasAaw = members.Any(member => member.Ship.Role == FleetRole.AirDefense);
        bool hasAsw = members.Any(member => member.Ship.Role == FleetRole.AntiSubmarine);
        double specialty = !hasAaw && candidate.Ship.Role == FleetRole.AirDefense
            ? candidate.Ship.AirDefenseCapability * 3d
            : !hasAsw && candidate.Ship.Role == FleetRole.AntiSubmarine
                ? candidate.Ship.AswCapability * 3d
                : candidate.Ship.CombatValue;
        return specialty;
    }

    private static string ResolveMissionType(FleetGroupKind kind) => kind switch
    {
        FleetGroupKind.Carrier => "Patrol",
        FleetGroupKind.SubmarinePatrol => "Patrol",
        _ => "AntiSurface",
    };

    private static string ResolveStationRole(FleetGroupPlan group, int memberIndex)
    {
        if (group.Kind == FleetGroupKind.SubmarinePatrol)
        {
            return "Default";
        }

        if (memberIndex == 0)
        {
            return "Core";
        }

        FleetClashShip ship = group.Members[memberIndex].Ship;
        if (ship.StationRole is "AAW" or "ASW")
        {
            return ship.StationRole;
        }

        if (group.Kind == FleetGroupKind.Carrier)
        {
            if (ship.AirDefenseCapability > ship.AswCapability && ship.AirDefenseCapability > 0)
            {
                return "AAW";
            }

            if (ship.AswCapability > 0)
            {
                return "ASW";
            }
        }

        return "Default";
    }

    private static (float Longitudinal, float Lateral) GetGroupOffset(
        int index,
        FleetGroupPlan group,
        float separation)
    {
        float[] lateral = [0f, 16f, -16f, 28f, -28f, 40f, -40f];
        float offset = lateral[index % lateral.Length];
        float trail = GroupDepth(group.Members.Select(member => member.Ship), separation) + (index / lateral.Length) * 12f;
        if (group.Kind == FleetGroupKind.SubmarinePatrol)
        {
            offset *= 1.35f;
        }

        return (trail, offset);
    }

    internal static float GroupDepth(IEnumerable<FleetClashShip> ships, float separation)
    {
        var members = ships.ToArray();
        if (members.Any(ship => ship.IsCarrier)) return Math.Clamp(separation * 0.15f, 12, 24);
        if (members.All(ship => ship.IsSubmarine)) return -Math.Min(8, separation * 0.1f);
        float largest = members.Max(ship => ScenarioUnitCatalog.DisplacementTons(ship.UnitType));
        return largest >= 6000 ? Math.Clamp(separation * 0.12f, 8, 18) :
            largest <= 2500 ? -Math.Min(8, separation * 0.1f) : 0;
    }

    private static (float Longitudinal, float Lateral) GetMemberOffset(
        int index,
        FleetGroupKind kind)
    {
        if (index == 0)
        {
            return (0f, 0f);
        }

        float spacing = kind switch
        {
            FleetGroupKind.Carrier => 2.75f,
            FleetGroupKind.SubmarinePatrol => 5f,
            _ => 1.8f,
        };
        return index switch
        {
            1 => (-spacing, -spacing),
            2 => (-spacing, spacing),
            _ => (spacing * 2f, 0f),
        };
    }

    private static IReadOnlyList<MissionWaypoint> BuildRoute(
        float groupX,
        float baseX,
        float groupZ,
        float direction,
        int groupIndex,
        FleetGroupKind kind,
        float engagementSeparationNm)
    {
        if (kind == FleetGroupKind.Carrier)
        {
            float withdrawalDrift = ((groupIndex % 2 == 0) ? 1f : -1f) * 4f;
            float firstWithdrawal = Math.Clamp(engagementSeparationNm * 0.18f, 20f, 32f);
            float secondWithdrawal = Math.Clamp(engagementSeparationNm * 0.36f, 42f, 62f);
            return
            [
                new(new(groupX - (direction * firstWithdrawal), 0, groupZ + withdrawalDrift)),
                new(new(groupX - (direction * secondWithdrawal), 0, groupZ + (withdrawalDrift * 0.5f))),
            ];
        }

        float firstLeg = kind == FleetGroupKind.SubmarinePatrol
            ? Math.Clamp(engagementSeparationNm * 0.25f, 18f, 38f)
            : Math.Clamp(engagementSeparationNm * 0.28f, 24f, 45f);
        float lateralDrift = ((groupIndex % 2 == 0) ? 1f : -1f) *
            (kind == FleetGroupKind.SubmarinePatrol ? 8f : 4f);
        string? depth = kind == FleetGroupKind.SubmarinePatrol ? "shallow" : null;
        int assignment = groupIndex % 3;
        float searchX = groupX + direction * (assignment == 2 ? firstLeg * 0.2f : firstLeg * 0.65f);
        float searchZ = groupZ + lateralDrift * (assignment == 1 ? 3 : 1);
        return
        [
            new(new(searchX, 0, searchZ, depth)),
            new(new(searchX + direction * firstLeg * 0.4f, 0, searchZ + lateralDrift, depth)),
            new(new(-baseX * 0.55f, 0, searchZ, depth), WaypointOrder.WeaponsFree),
        ];
    }

    private static IReadOnlyList<MissionWaypoint> BuildAirPatrolRoute(
        float groupX,
        float groupZ,
        float direction,
        float altitude,
        int patrolIndex,
        float engagementSeparationNm)
    {
        float lane = patrolIndex == 0 ? 1f : -1f;
        float innerStation = Math.Clamp(engagementSeparationNm * 0.07f, 5f, 10f);
        float outerStation = Math.Clamp(engagementSeparationNm * 0.12f, 9f, 18f);
        float returnStation = -6;
        return
        [
            new(new(groupX + (direction * innerStation), altitude, groupZ + (lane * 12f))),
            new(new(groupX + (direction * outerStation), altitude, groupZ + (lane * 5f))),
            new(new(groupX + (direction * innerStation), altitude, groupZ - (lane * 7f))),
            new(new(groupX + (direction * returnStation), altitude, groupZ + (lane * 6f))),
        ];
    }

    private static IReadOnlyList<MissionWaypoint> BuildSonobuoyBarrierRoute(
        float groupX,
        float groupZ,
        float direction,
        float lane,
        float altitude,
        string? sonobuoyAmmunition,
        float engagementSeparationNm)
    {
        List<MissionWaypoint> waypoints = [];
        float start = Math.Clamp(engagementSeparationNm * 0.1f, 9f, 17f);
        float interval = Math.Clamp(engagementSeparationNm * 0.035f, 3.5f, 6f);
        for (int index = 0; index < 6; index++)
        {
            float x = groupX + (direction * (start + (index * interval)));
            float z = groupZ + (lane * (5f + ((index % 2) * 9f)));
            MissionPosition position = new(x, altitude, z);
            waypoints.Add(new(position, sonobuoyAmmunition is null
                ? null
                : new AttackAtWaypointOrder(sonobuoyAmmunition, position)));
        }

        return waypoints;
    }

    private static IReadOnlyList<MissionWaypoint> BuildAewOrbitRoute(
        float groupX,
        float groupZ,
        float direction,
        float altitude,
        float engagementSeparationNm)
    {
        float innerStation = -Math.Clamp(engagementSeparationNm * 0.06f, 6f, 12f);
        float outerStation = -Math.Clamp(engagementSeparationNm * 0.12f, 12f, 24f);
        return
        [
            new(new(groupX + (direction * innerStation), altitude, groupZ + 7f)),
            new(new(groupX + (direction * outerStation), altitude, groupZ + 3f)),
            new(new(groupX + (direction * outerStation), altitude, groupZ - 7f)),
            new(new(groupX + (direction * innerStation), altitude, groupZ - 3f)),
        ];
    }

    private static float NormalizeHeading(float heading)
    {
        while (heading > 180f)
        {
            heading -= 360f;
        }
        while (heading < -180f)
        {
            heading += 360f;
        }
        return heading;
    }

    private static string GetFormationPattern(FleetGroupKind kind) => kind switch
    {
        FleetGroupKind.Carrier => "Circle",
        FleetGroupKind.Escort => "LineAbreast",
        FleetGroupKind.SubmarinePatrol => "LineAbreast",
        _ => "Echelon",
    };

    private static float GetFormationSpacing(FleetGroupKind kind) => kind switch
    {
        FleetGroupKind.Carrier => 2.75f,
        FleetGroupKind.SubmarinePatrol => 5f,
        _ => 1.8f,
    };

    private record IndexedShip(int SourceIndex, FleetClashShip Ship, string Reference);

    private record FleetGroupPlan(
        IReadOnlyList<IndexedShip> Members,
        string Name,
        FleetGroupKind Kind);
}

internal enum FleetGroupKind
{
    Carrier,
    SurfaceAction,
    Escort,
    SubmarinePatrol,
}
