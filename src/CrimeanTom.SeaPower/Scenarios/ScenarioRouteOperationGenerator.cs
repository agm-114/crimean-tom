using System.Globalization;
using System.Text;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private static readonly string[] CivilianShippingTypes =
    [
        "civ_ms_bulk",
        "civ_ms_freighter_a",
        "civ_ms_car_carrier_a",
        "civ_ms_mairangi_bay",
        "civ_ms_ritina",
        "civ_ms_roro_a",
    ];

    private static readonly string[] SupplyGroupTypes =
    [
        "usn_ao_t2",
        "civ_ms_sealift_pacific",
        "civ_ms_roro_b",
        "civ_ms_c7s68",
        "civ_ms_murom",
        "civ_ms_andizhan",
    ];

    private static readonly string[] MinesweepingGroupTypes =
    [
        "usn_mso_aggressive",
        "usn_msc_adjutant",
        "knm_msc_sauda",
        "plan_msc_type_010",
    ];

    private static void GenerateRouteOperationEntry(
        string scenarioPath,
        FleetClashGenerationOptions options,
        bool useLauncherMetadata)
    {
        int seed = CreateSeed();
        System.Random random = new(seed);
        FleetClashSize size = options.Size ?? (FleetClashSize)random.Next(3);
        string playerTaskforce = options.RoutePerspective == RouteOperationPerspective.Escort
            ? "Taskforce1"
            : "Taskforce2";
        FleetClashMatch match = GenerateRouteMatch(random, size, options.Region);
        match = ApplyEnemyMultiplier(
            match,
            random,
            size,
            playerTaskforce,
            options.EnemySizeMultiplier,
            excludeCarriers: true);
        int playerControlledCount = GetPlayerControlledCount(
            match,
            playerTaskforce,
            options.PlayerControlPercentage);
        match = ReorderPlayerForce(
            match,
            playerTaskforce,
            playerControlledCount,
            options.ControlPriority);
        ScenarioParameters parameters = ScenarioParameters.Create(random, seed, size, options, match);
        RouteOperationPlan route = CreateRouteOperationPlan(
            random,
            match,
            parameters,
            size,
            playerTaskforce,
            playerControlledCount,
            options.RouteVariant);

        File.WriteAllText(
            scenarioPath,
            NormalizeLineEndings(BuildRouteOperation(
                match,
                parameters,
                size,
                playerTaskforce,
                options,
                route,
                useLauncherMetadata)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Debug.Log(
            $"[Crimean Tom Operations] generated {size} " +
            $"{GetRouteVariantName(options.RouteVariant).ToLowerInvariant()} " +
            $"route operation seed {seed} in {match.Theater}; " +
            $"perspective {options.RoutePerspective}, protected ships {route.ProtectedUnits.Count}, " +
            $"required arrivals {route.RequiredArrivals}, force cost " +
            $"{match.First.PointCost}/{match.Second.PointCost}, sea state {parameters.SeaState}");
    }

    internal static FleetClashMatch GenerateRouteMatch(
        System.Random random,
        FleetClashSize size,
        string? region)
    {
        FleetClashMatch? best = null;
        int bestCarrierCount = int.MaxValue;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            FleetClashMatch candidate = FleetClashBalancer.Generate(random, size, region);
            int carrierCount = candidate.First.Ships.Count(ship => ship.IsCarrier) +
                candidate.Second.Ships.Count(ship => ship.IsCarrier);
            if (carrierCount == 0)
            {
                return candidate;
            }

            if (carrierCount < bestCarrierCount ||
                carrierCount == bestCarrierCount &&
                (best is null || candidate.BalanceError < best.BalanceError))
            {
                best = candidate;
                bestCarrierCount = carrierCount;
            }
        }

        return best ?? throw new InvalidOperationException(
            "Could not generate forces for a route operation.");
    }

    private static RouteOperationPlan CreateRouteOperationPlan(
        System.Random random,
        FleetClashMatch match,
        ScenarioParameters parameters,
        FleetClashSize size,
        string playerTaskforce,
        int playerControlledCount,
        RouteOperationVariant variant)
    {
        float routeLength = size switch
        {
            FleetClashSize.Small => 48f,
            FleetClashSize.Medium => 62f,
            FleetClashSize.Large => 78f,
            _ => 62f,
        };
        float routeStart = -routeLength / 2f;
        float routeEnd = routeLength / 2f;
        float interceptX = routeLength * 0.12f;
        float attackLane = random.Next(2) == 0 ? 1f : -1f;

        FleetDeployment firstDeployment = FleetOrderOfBattlePlanner.Plan(
            "Taskforce1",
            match.First,
            routeStart - 7f,
            90f,
            parameters.LateralOffsetNm,
            routeLength,
            playerTaskforce == "Taskforce1" ? playerControlledCount : match.First.Ships.Count,
            playerTaskforce == "Taskforce1" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce1" ? "Friendly AI" : "Enemy");
        firstDeployment = RetaskRouteFleet(
            firstDeployment,
            routeEnd,
            90f,
            isDefender: true,
            attackLane: 0f);

        FleetDeployment secondDeployment = FleetOrderOfBattlePlanner.Plan(
            "Taskforce2",
            match.Second,
            interceptX + 20f,
            -90f,
            attackLane * 22f,
            routeLength,
            playerTaskforce == "Taskforce2" ? playerControlledCount : match.Second.Ships.Count,
            playerTaskforce == "Taskforce2" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce2" ? "Friendly AI" : "Enemy");
        secondDeployment = RetaskRouteFleet(
            secondDeployment,
            interceptX,
            -90f,
            isDefender: false,
            attackLane);

        int protectedCount = GetProtectedGroupSize(size, variant);
        int vesselIndexOffset = firstDeployment.Units.Count(unit => !unit.Ship.IsSubmarine);
        IReadOnlyList<RouteProtectedUnit> protectedUnits = CreateProtectedUnits(
            random,
            match.First,
            variant,
            protectedCount,
            vesselIndexOffset,
            routeStart,
            routeEnd);
        int requiredArrivals = Math.Max(1, (int)Math.Ceiling(protectedCount * (2d / 3d)));
        return new RouteOperationPlan(
            firstDeployment,
            secondDeployment,
            protectedUnits,
            requiredArrivals,
            routeEnd);
    }

    private static FleetDeployment RetaskRouteFleet(
        FleetDeployment deployment,
        float destinationX,
        float heading,
        bool isDefender,
        float attackLane)
    {
        Dictionary<string, int> groupByReference = [];
        for (int groupIndex = 0; groupIndex < deployment.Formations.Count; groupIndex++)
        {
            foreach (string reference in deployment.Formations[groupIndex].References)
            {
                groupByReference[reference] = groupIndex;
            }
        }

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
            string height = unit.Ship.IsSubmarine ? "shallow" : "0";
            string? waypoints = leaders.Contains(unit.Reference) && !unit.Ship.IsCarrier
                ? isDefender
                    ? FormattableString.Invariant(
                        $"0,{height},{lane:0.##}|{destinationX:0.##},{height},{lane:0.##}/SetWeaponStatus,Free")
                    : FormattableString.Invariant(
                        $"{destinationX + 8f:0.##},{height},{lane:0.##}|{destinationX:0.##},{height},{lane:0.##}/SetWeaponStatus,Free")
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

    private static IReadOnlyList<RouteProtectedUnit> CreateProtectedUnits(
        System.Random random,
        FleetClashForce escortForce,
        RouteOperationVariant variant,
        int count,
        int vesselIndexOffset,
        float routeStart,
        float routeEnd)
    {
        RouteProtectedUnitOption[] available = GetProtectedUnitOptions(escortForce, variant, random)
            .OrderBy(_ => random.Next())
            .ToArray();
        if (available.Length == 0)
        {
            throw new InvalidOperationException(
                $"No installed vessels support the {GetRouteVariantName(variant)} variant.");
        }

        List<RouteProtectedUnit> units = [];
        for (int index = 0; index < count; index++)
        {
            float longitudinal = (index / 2) * -1.5f;
            float lateral = index == 0 ? 0f : (index % 2 == 0 ? 1f : -1f) * 1.4f;
            units.Add(new RouteProtectedUnit(
                $"Taskforce1Vessel{vesselIndexOffset + index + 1}",
                available[index % available.Length].UnitType,
                available[index % available.Length].VariantReference,
                available[index % available.Length].Nation,
                routeStart + longitudinal,
                lateral,
                index == 0
                    ? FormattableString.Invariant($"0,0,0|{routeEnd:0.##},0,0")
                    : null,
                index == 0 ? "Core" : "Default"));
        }

        return units;
    }

    private static bool UnitTypeExists(string unitType) => File.Exists(Path.Combine(
        Application.streamingAssetsPath,
        "original",
        "vessels",
        $"{unitType}.ini"));

    private static IReadOnlyList<RouteProtectedUnitOption> GetProtectedUnitOptions(
        FleetClashForce escortForce,
        RouteOperationVariant variant,
        System.Random random)
    {
        if (variant != RouteOperationVariant.MinesweepingGroup)
        {
            IEnumerable<string> types = variant == RouteOperationVariant.SupplyGroup
                ? SupplyGroupTypes
                : CivilianShippingTypes;
            return types
                .Where(UnitTypeExists)
                .Select(type => new RouteProtectedUnitOption(type, "Random", null))
                .ToArray();
        }

        string[] escortNations = escortForce.Ships
            .Select(ship => ship.Nation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RouteProtectedUnitOption[] regional = GetMinesweeperOptions(escortNations, random);
        if (regional.Length > 0)
        {
            return regional;
        }

        RouteProtectedUnitOption[] alliedFallback = GetMinesweeperOptions(
            ["US", "Norway"],
            random);
        if (alliedFallback.Length > 0)
        {
            return alliedFallback;
        }

        return MinesweepingGroupTypes
            .Where(UnitTypeExists)
            .Select(type => new RouteProtectedUnitOption(type, "Random", null))
            .ToArray();
    }

    private static RouteProtectedUnitOption[] GetMinesweeperOptions(
        IEnumerable<string> nations,
        System.Random random) =>
        TaskForceRosterGenerator.GetVesselOptions(nations)
            .Where(option => IsMinesweeper(option.UnitType))
            .GroupBy(
                option => (option.UnitType, option.Nation),
                option => option,
                (key, variants) =>
                {
                    TaskForceRosterGenerator.RosterUnitOption[] choices = variants.ToArray();
                    TaskForceRosterGenerator.RosterUnitOption selected =
                        choices[random.Next(choices.Length)];
                    return new RouteProtectedUnitOption(
                        key.UnitType,
                        selected.VariantReference,
                        key.Nation);
                })
            .ToArray();

    private static bool IsMinesweeper(string unitType) =>
        unitType.Contains("_msc_", StringComparison.OrdinalIgnoreCase) ||
        unitType.Contains("_mso_", StringComparison.OrdinalIgnoreCase);

    private static int GetProtectedGroupSize(
        FleetClashSize size,
        RouteOperationVariant variant)
    {
        int count = size switch
        {
            FleetClashSize.Small => 3,
            FleetClashSize.Medium => 4,
            FleetClashSize.Large => 6,
            _ => 4,
        };
        return variant == RouteOperationVariant.MinesweepingGroup
            ? Math.Max(2, count - 1)
            : count;
    }

    private static string BuildRouteOperation(
        FleetClashMatch match,
        ScenarioParameters parameters,
        FleetClashSize size,
        string playerTaskforce,
        FleetClashGenerationOptions options,
        RouteOperationPlan route,
        bool useLauncherMetadata)
    {
        string variantName = GetRouteVariantName(options.RouteVariant);
        bool escortPerspective = options.RoutePerspective == RouteOperationPerspective.Escort;
        FleetDeployment playerDeployment = playerTaskforce == "Taskforce1"
            ? route.FirstDeployment
            : route.SecondDeployment;
        IEnumerable<string> alliedReferences = playerDeployment.AlliedReferences;
        if (escortPerspective)
        {
            alliedReferences = alliedReferences.Concat(
                route.ProtectedUnits.Select(unit => unit.Reference));
        }

        MissionWriter mission = new();
        mission.Section("Debug");
        mission.Write("AllowAIRadarRoutine", true);

        mission.Section("Language_en");
        if (useLauncherMetadata)
        {
            mission.Write("Name", "Generated Route Operation");
            mission.Write("Description", "Configure an escort or interdiction mission with a protected group on the move.");
        }
        else
        {
            mission.Write("Name", $"Generated {variantName} - {options.RoutePerspective} - {size}");
            mission.Write("Description", $"{match.Theater}: {(escortPerspective ? "escort " : "interdict ")}{variantName.ToLowerInvariant()}. {(escortPerspective
                    ? $"Get {route.RequiredArrivals} of {route.ProtectedUnits.Count} protected ships through. "
                    : $"Stop {route.ProtectedUnits.Count - route.RequiredArrivals + 1} protected ships. ")}Generation seed {parameters.Seed}.");
        }
        AppendRouteMessagesAndObjectiveText(mission, match, route, variantName);
        AppendCrimeanTomSettings(mission, alliedReferences, spectator: false);
        AppendEnvironment(mission, match, parameters);
        AppendRouteMission(mission, match, size, playerTaskforce, parameters, route);
        route.FirstDeployment.WriteTo(mission, useLauncherMetadata && playerTaskforce == "Taskforce1");
        foreach (RouteProtectedUnit unit in route.ProtectedUnits) unit.WriteTo(mission);
        route.SecondDeployment.WriteTo(mission, useLauncherMetadata && playerTaskforce == "Taskforce2");
        AppendCivilianTraffic(mission, parameters);
        AppendRouteTriggers(mission, route, playerTaskforce);
        AppendOpposedObjectives(mission, "Taskforce1DeliverProtectedGroup", "Taskforce2StopProtectedGroup");
        AppendRetreatAnchors(mission, match);
        return mission.ToString();
    }

    private static void AppendRouteMessagesAndObjectiveText(
        MissionWriter mission,
        FleetClashMatch match,
        RouteOperationPlan route,
        string variantName)
    {
        int lossThreshold = route.ProtectedUnits.Count - route.RequiredArrivals + 1;
        mission.Write("Taskforce1StartMessage", $"ESCORT OPERATION|{match.FirstName} escorts are covering a {variantName.ToLowerInvariant()} group. Keep formation and get at least {route.RequiredArrivals} protected ships into the marked destination area.|Take station");
        mission.Write("Taskforce2StartMessage", $"INTERDICTION OPERATION|{match.SecondName} forces must stop the {variantName.ToLowerInvariant()} group. Destroy at least {lossThreshold} protected ships before the remainder reaches safety.|Begin search");
        mission.Write("Taskforce1RouteVictoryMessage", "ROUTE COMPLETED|Enough protected ships reached the destination. The route is secure.|End mission");
        mission.Write("Taskforce2RouteVictoryMessage", "ROUTE DENIED|The protected group can no longer complete its mission.|End mission");
        mission.Write("Taskforce1RouteDefeatMessage", "ROUTE DENIED|Too many protected ships were lost. Withdraw the remaining escorts.|End mission");
        mission.Write("Taskforce2RouteDefeatMessage", "TARGETS ESCAPED|Enough protected ships reached safety. Break contact and withdraw.|End mission");
        mission.Write("Objective_Taskforce1DeliverProtectedGroup", $"Get at least {route.RequiredArrivals} protected ships to the destination");
        mission.Write("Objective_Taskforce2StopProtectedGroup", $"Destroy at least {lossThreshold} protected ships");
    }

    private static void AppendRouteMission(
        MissionWriter mission,
        FleetClashMatch match,
        FleetClashSize size,
        string playerTaskforce,
        ScenarioParameters parameters,
        RouteOperationPlan route)
    {
        IReadOnlyList<FleetFormationPlan> firstFormations = route.FirstDeployment.Formations
            .Concat(
            [
                new FleetFormationPlan(
                    route.ProtectedUnits.Select(unit => unit.Reference).ToArray(),
                    "Protected Route Group",
                    "Box",
                    1.5f),
            ])
            .ToArray();
        AppendMission(mission, match, size, playerTaskforce, parameters,
            route.FirstDeployment, route.SecondDeployment,
            additionalFirstVessels: route.ProtectedUnits.Count,
            firstFormations: firstFormations);
    }

    private static void AppendRouteTriggers(
        MissionWriter mission,
        RouteOperationPlan route,
        string playerTaskforce)
    {
        string references = string.Join(',', route.ProtectedUnits.Select(unit => unit.Reference));
        int lossThreshold = route.ProtectedUnits.Count - route.RequiredArrivals + 1;
        AppendStartTrigger(mission, "Issue route-operation orders");

        mission.Section("Trigger2");
        mission.Write("Name", "Protected group reaches destination");
        mission.Write("Condition_Condition1_Type", "UnitsInTheArea");
        mission.WritePosition("Condition_Condition1_PositionNM", route.DestinationX, 0, 0);
        mission.Write("Condition_Condition1_AreaRadiusNM", 6);
        mission.Write("Condition_Condition1_AreaDisplaySide", playerTaskforce == "Taskforce1" ? "Blue" : "Red");
        mission.Write("Condition_Condition1_Units", references);
        mission.Write("Condition_Condition1_MinimumUnits", route.RequiredArrivals);
        mission.Write("ConditionsCompleted", "<Condition1>");
        mission.Write("Action_Taskforce1_Message", "Taskforce1RouteVictoryMessage");
        mission.Write("Action_Taskforce2_Message", "Taskforce2RouteDefeatMessage");
        mission.Write("Action_Victory", "Taskforce1");
        mission.Write("Action_ObjectivesCompleted", "Taskforce1DeliverProtectedGroup");
        mission.Write("Action_ObjectivesFailed", "Taskforce2StopProtectedGroup");
        mission.Write("Action_EndMission", true);

        mission.Section("Trigger3");
        mission.Write("Name", "Protected group can no longer complete route");
        mission.Write("Condition_Condition1_Type", "UnitDestroyed");
        mission.Write("Condition_Condition1_Units", references);
        mission.Write("Condition_Condition1_MinimumUnits", lossThreshold);
        mission.Write("ConditionsCompleted", "<Condition1>");
        mission.Write("Action_Taskforce2_Message", "Taskforce2RouteVictoryMessage");
        mission.Write("Action_Taskforce1_Message", "Taskforce1RouteDefeatMessage");
        mission.Write("Action_Victory", "Taskforce2");
        mission.Write("Action_ObjectivesCompleted", "Taskforce2StopProtectedGroup");
        mission.Write("Action_ObjectivesFailed", "Taskforce1DeliverProtectedGroup");
        mission.Write("Action_EndMission", true);
    }

    private static string GetRouteVariantName(RouteOperationVariant variant) => variant switch
    {
        RouteOperationVariant.SupplyGroup => "Supply group",
        RouteOperationVariant.MinesweepingGroup => "Minesweeping group",
        _ => "Civilian shipping",
    };

    private record RouteOperationPlan(
        FleetDeployment FirstDeployment,
        FleetDeployment SecondDeployment,
        IReadOnlyList<RouteProtectedUnit> ProtectedUnits,
        int RequiredArrivals,
        float DestinationX);

    private record RouteProtectedUnit(
        string Reference,
        string UnitType,
        string VariantReference,
        string? Nation,
        float X,
        float Z,
        string? Waypoints,
        string StationRole)
    {
        internal void WriteTo(MissionWriter mission)
        {
            mission.Section(Reference);
            mission.Write("Type", UnitType);
            mission.Write("VariantReference", VariantReference);
            if (!string.IsNullOrWhiteSpace(Nation)) mission.Write("Nation", Nation);
            if (StationRole != "Default") mission.Write("StationRole", StationRole);
            mission.Write("CrewSkill", "Trained");
            mission.WritePosition("RelativePositionInNM", X, 0, Z);
            mission.Write("Telegraph", 2);
            mission.Write("Heading", 90);
            if (Waypoints is not null) mission.Write("Waypoints", Waypoints);
        }
    }

    private record RouteProtectedUnitOption(
        string UnitType,
        string VariantReference,
        string? Nation);
}