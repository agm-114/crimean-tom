using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    private static FleetClashMatch GenerateRouteMatch(
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

        StringBuilder mission = new();
        mission.AppendLine("[Debug]");
        mission.AppendLine("AllowAIRadarRoutine=True");
        mission.AppendLine();
        mission.AppendLine("[Language_en]");
        if (useLauncherMetadata)
        {
            mission.AppendLine("Name=Generated Route Operation");
            mission.AppendLine("Description=Configure an escort or interdiction mission with a protected group on the move.");
        }
        else
        {
            mission.Append("Name=Generated ").Append(variantName).Append(" - ")
                .Append(options.RoutePerspective).Append(" - ").AppendLine(size.ToString());
            mission.Append("Description=").Append(match.Theater).Append(": ")
                .Append(escortPerspective ? "escort " : "interdict ")
                .Append(variantName.ToLowerInvariant()).Append(". ")
                .Append(escortPerspective
                    ? $"Get {route.RequiredArrivals} of {route.ProtectedUnits.Count} protected ships through. "
                    : $"Stop {route.ProtectedUnits.Count - route.RequiredArrivals + 1} protected ships. ")
                .Append("Generation seed ")
                .Append(parameters.Seed.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        }
        AppendRouteMessagesAndObjectiveText(mission, match, route, variantName);
        AppendCrimeanTomSettings(mission, alliedReferences, spectator: false);
        AppendEnvironment(mission, match, parameters);
        AppendRouteMission(mission, match, size, playerTaskforce, parameters, route);
        AppendFleet(
            mission,
            route.FirstDeployment,
            useLauncherMetadata && playerTaskforce == "Taskforce1");
        AppendProtectedGroup(mission, route.ProtectedUnits);
        AppendFleet(
            mission,
            route.SecondDeployment,
            useLauncherMetadata && playerTaskforce == "Taskforce2");
        AppendCivilianTraffic(mission, parameters);
        AppendRouteTriggers(mission, route, playerTaskforce);
        AppendRouteObjectives(mission);
        AppendRetreatAnchors(mission, match);
        return mission.ToString();
    }

    private static void AppendRouteMessagesAndObjectiveText(
        StringBuilder mission,
        FleetClashMatch match,
        RouteOperationPlan route,
        string variantName)
    {
        int lossThreshold = route.ProtectedUnits.Count - route.RequiredArrivals + 1;
        mission.Append("Taskforce1StartMessage=ESCORT OPERATION|").Append(match.FirstName)
            .Append(" escorts are covering a ").Append(variantName.ToLowerInvariant())
            .Append(" group. Keep formation and get at least ").Append(route.RequiredArrivals)
            .AppendLine(" protected ships into the marked destination area.|Take station");
        mission.Append("Taskforce2StartMessage=INTERDICTION OPERATION|").Append(match.SecondName)
            .Append(" forces must stop the ").Append(variantName.ToLowerInvariant())
            .Append(" group. Destroy at least ").Append(lossThreshold)
            .AppendLine(" protected ships before the remainder reaches safety.|Begin search");
        mission.AppendLine("Taskforce1RouteVictoryMessage=ROUTE COMPLETED|Enough protected ships reached the destination. The route is secure.|End mission");
        mission.AppendLine("Taskforce2RouteVictoryMessage=ROUTE DENIED|The protected group can no longer complete its mission.|End mission");
        mission.AppendLine("Taskforce1RouteDefeatMessage=ROUTE DENIED|Too many protected ships were lost. Withdraw the remaining escorts.|End mission");
        mission.AppendLine("Taskforce2RouteDefeatMessage=TARGETS ESCAPED|Enough protected ships reached safety. Break contact and withdraw.|End mission");
        mission.Append("Objective_Taskforce1DeliverProtectedGroup=Get at least ")
            .Append(route.RequiredArrivals).AppendLine(" protected ships to the destination");
        mission.Append("Objective_Taskforce2StopProtectedGroup=Destroy at least ")
            .Append(lossThreshold).AppendLine(" protected ships");
        mission.AppendLine();
    }

    private static void AppendRouteMission(
        StringBuilder mission,
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
        mission.AppendLine("[Mission]");
        mission.Append("Difficulty=").AppendLine(size switch
        {
            FleetClashSize.Small => "2",
            FleetClashSize.Medium => "3",
            FleetClashSize.Large => "4",
            _ => "3",
        });
        mission.Append("PlayerTaskforce=").AppendLine(playerTaskforce);
        mission.Append("EnemyTaskforce=").AppendLine(
            playerTaskforce == "Taskforce1" ? "Taskforce2" : "Taskforce1");
        mission.Append("Taskforce1_Nation=").AppendLine(match.First.PrimaryNation);
        mission.Append("Taskforce2_Nation=").AppendLine(match.Second.PrimaryNation);
        mission.AppendLine("AllowMoraleToAffectAI=True");
        mission.Append("NumberOfTaskforce1Vessels=").AppendLine(
            (route.FirstDeployment.Units.Count(unit => !unit.Ship.IsSubmarine) +
             route.ProtectedUnits.Count).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Vessels=").AppendLine(
            route.SecondDeployment.Units.Count(unit => !unit.Ship.IsSubmarine)
                .ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce1Submarines=").AppendLine(
            route.FirstDeployment.Units.Count(unit => unit.Ship.IsSubmarine)
                .ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Submarines=").AppendLine(
            route.SecondDeployment.Units.Count(unit => unit.Ship.IsSubmarine)
                .ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce1Aircraft=").AppendLine(
            route.FirstDeployment.Aircraft.Count(unit => !unit.AirPlan.IsHelicopter)
                .ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Aircraft=").AppendLine(
            route.SecondDeployment.Aircraft.Count(unit => !unit.AirPlan.IsHelicopter)
                .ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce1Helicopters=").AppendLine(
            route.FirstDeployment.Aircraft.Count(unit => unit.AirPlan.IsHelicopter)
                .ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Helicopters=").AppendLine(
            route.SecondDeployment.Aircraft.Count(unit => unit.AirPlan.IsHelicopter)
                .ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfNeutralVessels=").AppendLine(
            parameters.NeutralVesselCount.ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfNeutralAircraft=").AppendLine(
            parameters.NeutralAircraftCount.ToString(CultureInfo.InvariantCulture));
        AppendFormations(mission, "Taskforce1", firstFormations);
        AppendFormations(mission, "Taskforce2", route.SecondDeployment.Formations);
        mission.AppendLine("NumberOfTriggers=3");
        mission.AppendLine();
    }

    private static void AppendProtectedGroup(
        StringBuilder mission,
        IReadOnlyList<RouteProtectedUnit> units)
    {
        foreach (RouteProtectedUnit unit in units)
        {
            mission.Append('[').Append(unit.Reference).AppendLine("]");
            mission.Append("Type=").AppendLine(unit.UnitType);
            mission.Append("VariantReference=").AppendLine(unit.VariantReference);
            if (!string.IsNullOrWhiteSpace(unit.Nation))
            {
                mission.Append("Nation=").AppendLine(unit.Nation);
            }
            if (unit.StationRole != "Default")
            {
                mission.Append("StationRole=").AppendLine(unit.StationRole);
            }
            mission.AppendLine("CrewSkill=Trained");
            mission.Append("RelativePositionInNM=")
                .Append(unit.X.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(",0,")
                .AppendLine(unit.Z.ToString("0.##", CultureInfo.InvariantCulture));
            mission.AppendLine("Telegraph=2");
            mission.AppendLine("Heading=90");
            if (unit.Waypoints is not null)
            {
                mission.Append("Waypoints=").AppendLine(unit.Waypoints);
            }
            mission.AppendLine();
        }
    }

    private static void AppendRouteTriggers(
        StringBuilder mission,
        RouteOperationPlan route,
        string playerTaskforce)
    {
        string references = string.Join(',', route.ProtectedUnits.Select(unit => unit.Reference));
        int lossThreshold = route.ProtectedUnits.Count - route.RequiredArrivals + 1;
        mission.AppendLine("[Trigger1]");
        mission.AppendLine("Name=Issue route-operation orders");
        mission.AppendLine("Condition_Condition1_Type=OnMissionStart");
        mission.AppendLine("ConditionsCompleted=<Condition1>");
        mission.AppendLine("Action_Taskforce1_Message=Taskforce1StartMessage");
        mission.AppendLine("Action_Taskforce2_Message=Taskforce2StartMessage");
        mission.AppendLine();

        mission.AppendLine("[Trigger2]");
        mission.AppendLine("Name=Protected group reaches destination");
        mission.AppendLine("Condition_Condition1_Type=UnitsInTheArea");
        mission.Append("Condition_Condition1_PositionNM=")
            .Append(route.DestinationX.ToString("0.##", CultureInfo.InvariantCulture))
            .AppendLine(",0,0");
        mission.AppendLine("Condition_Condition1_AreaRadiusNM=6");
        mission.Append("Condition_Condition1_AreaDisplaySide=")
            .AppendLine(playerTaskforce == "Taskforce1" ? "Blue" : "Red");
        mission.Append("Condition_Condition1_Units=").AppendLine(references);
        mission.Append("Condition_Condition1_MinimumUnits=").AppendLine(
            route.RequiredArrivals.ToString(CultureInfo.InvariantCulture));
        mission.AppendLine("ConditionsCompleted=<Condition1>");
        mission.AppendLine("Action_Taskforce1_Message=Taskforce1RouteVictoryMessage");
        mission.AppendLine("Action_Taskforce2_Message=Taskforce2RouteDefeatMessage");
        mission.AppendLine("Action_Victory=Taskforce1");
        mission.AppendLine("Action_ObjectivesCompleted=Taskforce1DeliverProtectedGroup");
        mission.AppendLine("Action_ObjectivesFailed=Taskforce2StopProtectedGroup");
        mission.AppendLine("Action_EndMission=True");
        mission.AppendLine();

        mission.AppendLine("[Trigger3]");
        mission.AppendLine("Name=Protected group can no longer complete route");
        mission.AppendLine("Condition_Condition1_Type=UnitDestroyed");
        mission.Append("Condition_Condition1_Units=").AppendLine(references);
        mission.Append("Condition_Condition1_MinimumUnits=").AppendLine(
            lossThreshold.ToString(CultureInfo.InvariantCulture));
        mission.AppendLine("ConditionsCompleted=<Condition1>");
        mission.AppendLine("Action_Taskforce2_Message=Taskforce2RouteVictoryMessage");
        mission.AppendLine("Action_Taskforce1_Message=Taskforce1RouteDefeatMessage");
        mission.AppendLine("Action_Victory=Taskforce2");
        mission.AppendLine("Action_ObjectivesCompleted=Taskforce2StopProtectedGroup");
        mission.AppendLine("Action_ObjectivesFailed=Taskforce1DeliverProtectedGroup");
        mission.AppendLine("Action_EndMission=True");
        mission.AppendLine();
    }

    private static void AppendRouteObjectives(StringBuilder mission)
    {
        mission.AppendLine("[Taskforce1_Objectives]");
        mission.AppendLine("Taskforce1DeliverProtectedGroup=50,-50,,Main");
        mission.AppendLine();
        mission.AppendLine("[Taskforce2_Objectives]");
        mission.AppendLine("Taskforce2StopProtectedGroup=50,-50,,Main");
        mission.AppendLine();
    }

    private static string GetRouteVariantName(RouteOperationVariant variant) => variant switch
    {
        RouteOperationVariant.SupplyGroup => "Supply group",
        RouteOperationVariant.MinesweepingGroup => "Minesweeping group",
        _ => "Civilian shipping",
    };

    private sealed record RouteOperationPlan(
        FleetDeployment FirstDeployment,
        FleetDeployment SecondDeployment,
        IReadOnlyList<RouteProtectedUnit> ProtectedUnits,
        int RequiredArrivals,
        float DestinationX);

    private sealed record RouteProtectedUnit(
        string Reference,
        string UnitType,
        string VariantReference,
        string? Nation,
        float X,
        float Z,
        string? Waypoints,
        string StationRole);

    private sealed record RouteProtectedUnitOption(
        string UnitType,
        string VariantReference,
        string? Nation);
}
