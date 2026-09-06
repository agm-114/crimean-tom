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
        string delegatedLabel)
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
        int aircraftNumber = 0;
        int helicopterNumber = 0;
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            FleetGroupPlan group = groups[groupIndex];
            (float longitudinal, float lateral) = GetGroupOffset(groupIndex, group.Kind);
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
                AddCombatAirPatrol(
                    taskforce,
                    group,
                    carrier,
                    airPackage.CombatAirPatrol,
                    groupX,
                    groupZ,
                    direction,
                    heading,
                    engagementSeparationNm,
                    aircraft,
                    formations,
                    ref aircraftNumber);
                AddAirborneEarlyWarning(
                    taskforce,
                    carrier,
                    airPackage.AirborneEarlyWarning,
                    groupX,
                    groupZ,
                    direction,
                    heading,
                    engagementSeparationNm,
                    aircraft,
                    ref aircraftNumber,
                    ref helicopterNumber);
                AddAswPatrol(
                    taskforce,
                    group,
                    carrier,
                    airPackage.AswPatrol,
                    groupX,
                    groupZ,
                    direction,
                    heading,
                    engagementSeparationNm,
                    aircraft,
                    ref helicopterNumber);
            }
        }

        return new FleetDeployment(units, formations, aircraft, split);
    }

    private static void AddCombatAirPatrol(
        string taskforce,
        FleetGroupPlan group,
        IndexedShip carrier,
        CarrierAirPlan? plan,
        float groupX,
        float groupZ,
        float direction,
        float heading,
        float engagementSeparationNm,
        ICollection<DeployedAircraft> aircraft,
        ICollection<FleetFormationPlan> formations,
        ref int aircraftNumber)
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

                string reference = $"{taskforce}Aircraft{++aircraftNumber}";
                pair.Add(reference);
                aircraft.Add(new DeployedAircraft(
                    carrier.SourceIndex,
                    reference,
                    carrier.Reference,
                    plan,
                    groupX + (direction * (8f + pairIndex)),
                    groupZ + (pairIndex == 0 ? 6f : -6f) + (memberIndex * 0.1f),
                    28000f,
                    memberIndex == 0
                        ? BuildAirPatrolRoute(
                            groupX,
                            groupZ,
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
                    $"{group.Name} CAP {(char)('A' + pairIndex)}",
                    "Vic",
                    0.1f));
            }
        }
    }

    private static void AddAirborneEarlyWarning(
        string taskforce,
        IndexedShip carrier,
        CarrierAirPlan? plan,
        float groupX,
        float groupZ,
        float direction,
        float heading,
        float engagementSeparationNm,
        ICollection<DeployedAircraft> aircraft,
        ref int aircraftNumber,
        ref int helicopterNumber)
    {
        if (plan is null || plan.AvailableCount < 1)
        {
            return;
        }

        string unitKind = plan.IsHelicopter ? "Helicopter" : "Aircraft";
        int number = plan.IsHelicopter ? ++helicopterNumber : ++aircraftNumber;
        float altitude = plan.IsHelicopter ? 10000f : 30000f;
        aircraft.Add(new DeployedAircraft(
            carrier.SourceIndex,
            $"{taskforce}{unitKind}{number}",
            carrier.Reference,
            plan,
            groupX + (direction * 16f),
            groupZ,
            altitude,
            BuildAewOrbitRoute(
                groupX,
                groupZ,
                direction,
                altitude,
                engagementSeparationNm),
            heading,
            "AEW"));
    }

    private static void AddAswPatrol(
        string taskforce,
        FleetGroupPlan group,
        IndexedShip carrier,
        CarrierAirPlan? plan,
        float groupX,
        float groupZ,
        float direction,
        float heading,
        float engagementSeparationNm,
        ICollection<DeployedAircraft> aircraft,
        ref int helicopterNumber)
    {
        if (plan is null || !plan.IsHelicopter)
        {
            return;
        }

        int count = Math.Min(2, plan.AvailableCount);
        for (int index = 0; index < count; index++)
        {
            string reference = $"{taskforce}Helicopter{++helicopterNumber}";
            float lane = index == 0 ? 1f : -1f;
            aircraft.Add(new DeployedAircraft(
                carrier.SourceIndex,
                reference,
                carrier.Reference,
                plan,
                groupX + (direction * 6f),
                groupZ + (lane * 8f),
                200f,
                BuildSonobuoyBarrierRoute(
                    groupX,
                    groupZ,
                    direction,
                    lane,
                    200f,
                    plan.SonobuoyAmmunition,
                    engagementSeparationNm),
                heading,
                "HeloASW"));
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
            int take = Math.Min(2, submarines.Count);
            List<IndexedShip> members = submarines
                .OrderByDescending(ship => ship.Ship.SurfaceCapability + ship.Ship.AswCapability)
                .Take(take)
                .ToList();
            foreach (IndexedShip member in members)
            {
                submarines.Remove(member);
            }

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
        FleetGroupKind kind)
    {
        float[] lateral = [0f, 9f, -9f, 16f, -16f, 24f, -24f];
        float offset = lateral[index % lateral.Length];
        float trail = (index / lateral.Length) * 8f;
        if (kind == FleetGroupKind.Carrier)
        {
            trail += 5f;
        }
        else if (kind == FleetGroupKind.SubmarinePatrol)
        {
            trail -= 10f;
            offset *= 1.35f;
        }

        return (trail, offset);
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
            1 => (spacing, -spacing),
            2 => (spacing, spacing),
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
        return
        [
            new(new(groupX + (direction * firstLeg), 0, groupZ + lateralDrift, depth)),
            new(new(-baseX, 0, groupZ + (lateralDrift * 0.5f), depth), WaypointOrder.WeaponsFree),
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
        float innerStation = Math.Clamp(engagementSeparationNm * 0.22f, 18f, 36f);
        float outerStation = Math.Clamp(engagementSeparationNm * 0.42f, 35f, 68f);
        float returnStation = Math.Clamp(engagementSeparationNm * 0.13f, 8f, 22f);
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
        float innerStation = Math.Clamp(engagementSeparationNm * 0.28f, 16f, 48f);
        float outerStation = Math.Clamp(engagementSeparationNm * 0.36f, 24f, 62f);
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