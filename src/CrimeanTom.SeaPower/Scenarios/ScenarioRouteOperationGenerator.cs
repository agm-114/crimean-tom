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
        int seed = LastGenerationSeed = options.Seed ?? CreateSeed();
        System.Random random = new(seed);
        FleetClashSize size = options.Size ?? (FleetClashSize)random.Next(3);
        if (options.Spectator) options.RoutePerspective = RouteOperationPerspective.Escort;
        string playerTaskforce = options.RoutePerspective == RouteOperationPerspective.Escort
            ? "Taskforce1"
            : "Taskforce2";
        FleetClashMatch match = GenerateRouteMatch(random, size, options.Region, playerTaskforce, options.EnemyComposition);
        match = ApplyEnemyMultiplier(
            match,
            random,
            size,
            playerTaskforce,
            options.EnemySizeMultiplier,
            excludeCarriers: true, preserveComposition: options.EnemyComposition != FleetComposition.Random);
        (match, options.ChaseAllowance) = ApplyRouteChaseAllowance(match, seed, size, options.RouteVariant, options.RouteEncounter,
            generationOptions: options);
        int playerControlledCount = options.Spectator ? 0 : GetPlayerControlledCount(
            match,
            playerTaskforce,
            options.PlayerControlPercentage);
        match = ReorderPlayerForce(
            match,
            playerTaskforce,
            playerControlledCount,
            options.ControlPriority);
        ScenarioParameters parameters = ScenarioParameters.Create(new System.Random(seed), seed, size, options, match);
        RouteOperationPlan route = CreateRouteOperationPlan(
            random,
            match,
            parameters,
            size,
            playerTaskforce,
            playerControlledCount,
            options.RouteVariant,
            options.RouteEncounter);

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
        string? region,
        string? playerTaskforce = null,
        FleetComposition composition = FleetComposition.Random)
        => FleetClashBalancer.Generate(random, size, region, playerTaskforce, composition, excludeCarriers: true);

    private static RouteOperationPlan CreateRouteOperationPlan(
        System.Random random,
        FleetClashMatch match,
        ScenarioParameters parameters,
        FleetClashSize size,
        string playerTaskforce,
        int playerControlledCount,
        RouteOperationVariant variant,
        RouteEncounterKind encounterKind = RouteEncounterKind.Random,
        FleetClashForce? convoyRoster = null)
    {
        random = new System.Random(parameters.Seed ^ 0x524F5554);
        RouteEncounter voyage = ShippingRoutes.Pick(match.Theater, parameters.Seed, size, encounterKind);
        RouteCorridor corridor = CreateRouteCorridor(
            match, size, parameters.LateralOffsetNm, random.Next(2) == 0 ? 1f : -1f,
            playerTaskforce, playerControlledCount, voyage.LengthNm);

        int protectedCount = GetProtectedGroupSize(size, variant);
        int vesselIndexOffset = corridor.First.VesselCount;
        IReadOnlyList<RouteProtectedUnit> protectedUnits = CreateProtectedUnits(
            new System.Random(parameters.Seed ^ 0x434F4E56),
            convoyRoster ?? match.First,
            variant,
            protectedCount,
            vesselIndexOffset,
            corridor.StartX,
            corridor.DestinationX);
        // Surface leaders screen the moving convoy; submarines keep their search routes.
        FleetDeployment escort = corridor.First with
        {
            Units = corridor.First.Units.Select(unit =>
                !unit.Ship.IsSubmarine && !unit.Ship.IsCarrier && unit.Waypoints is not null
                    ? unit with { FollowReference = protectedUnits[0].Reference }
                    : unit).ToArray(),
        };
        protectedUnits = protectedUnits.Select(unit =>
        {
            MissionPosition position = voyage.Orient(new(unit.X, 0, unit.Z));
            return unit with { X = position.X, Z = position.Z, Heading = voyage.Heading,
                Waypoints = unit.Waypoints is null ? null : voyage.Onward };
        }).ToArray();
        float convoySpeed = protectedUnits.Min(unit => ScenarioUnitCatalog.SpeedAtTelegraph(unit.UnitType, 3));
        escort = ScenarioTerrain.PlaceFleet(voyage, voyage.OrientFleet(escort));
        var interception = RouteInterceptionPlanner.Place(voyage.OrientFleet(corridor.Second), voyage, convoySpeed,
            escort.Units.Select(unit => new MissionPosition(unit.X, 0, unit.Z))
                .Concat(protectedUnits.Select(unit => new MissionPosition(unit.X, 0, unit.Z))).ToArray());
        int requiredArrivals = Math.Max(1, (int)Math.Ceiling(protectedCount * (2d / 3d)));
        RouteOperationPlan plan = new(
            escort,
            interception.Fleet,
            protectedUnits,
            requiredArrivals,
            corridor.DestinationX,
            voyage,
            interception.Groups);
        foreach (RouteProtectedUnit unit in plan.ProtectedUnits)
            if (ScenarioTerrain.HeightMeters(voyage.Center.Offset(new(unit.X, 0, unit.Z))) > -20)
                throw new InvalidOperationException("The convoy does not fit this coastal approach. Choose a mid-crossing encounter or smaller force.");
        return plan;
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

        // The slowest selected ship leads, so formation speed agrees with interception planning.
        available = Enumerable.Range(0, count).Select(index => available[index % available.Length])
            .OrderBy(unit => ScenarioUnitCatalog.SpeedAtTelegraph(unit.UnitType, 3)).ToArray();
        List<RouteProtectedUnit> units = [];
        for (int index = 0; index < count; index++)
        {
            float longitudinal = index * -0.75f;
            units.Add(new RouteProtectedUnit(
                MissionUnitReferences.FormatHull("Taskforce1", vesselIndexOffset + index + 1),
                available[index % available.Length].UnitType,
                available[index % available.Length].VariantReference,
                available[index % available.Length].Nation,
                routeStart + longitudinal,
                0,
                index == 0
                    ? [new(new(0, 0, 0)), new(new(routeEnd, 0, 0))]
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
        if (route.Voyage is { } voyage)
            match = match with { Latitude = (float)voyage.Center.Latitude, Longitude = (float)voyage.Center.Longitude };
        string variantName = GetRouteVariantName(options.RouteVariant);
        bool escortPerspective = options.RoutePerspective == RouteOperationPerspective.Escort;
        bool controlProtectedShips = escortPerspective && !options.Spectator && options.Starting.ControlProtectedShips;
        route = options.Spectator ? route : playerTaskforce == "Taskforce1"
            ? route with { FirstDeployment = options.Starting.Apply(route.FirstDeployment) }
            : route with { SecondDeployment = options.Starting.Apply(route.SecondDeployment) };
        FleetDeployment playerDeployment = playerTaskforce == "Taskforce1"
            ? route.FirstDeployment
            : route.SecondDeployment;
        IEnumerable<string> alliedReferences = playerDeployment.AlliedReferences;
        if (escortPerspective && !controlProtectedShips)
        {
            alliedReferences = alliedReferences.Concat(
                route.ProtectedUnits.Select(unit => unit.Reference));
        }
        if (controlProtectedShips && !options.Starting.PreloadOrders)
            route = route with { ProtectedUnits = route.ProtectedUnits.Select(unit => unit with { Waypoints = null, Telegraph = 0 }).ToArray() };

        MissionWriter mission = new();
        mission.Section("Debug");
        mission.Write("AllowAIRadarRoutine", true);
        if (options.Spectator) mission.Write("ShowUnidentifiedUnitsIn3D", true);

        mission.Section("Language_en");
        if (useLauncherMetadata)
        {
            mission.Write("Name", options.Spectator ? "Generated Route Operation Observer" : "Generated Route Operation");
            mission.Write("Description", options.Spectator ? "Watch an AI escort and interdiction battle from a Blue observer side."
                : "Configure an escort or interdiction mission with a protected group on the move.");
        }
        else
        {
            mission.Write("Name", $"Generated {variantName} - {(options.Spectator ? "Observer" : options.RoutePerspective)} - {size}");
            mission.Write("Description", $"{match.Theater}: {(escortPerspective ? "escort " : "interdict ")}{variantName.ToLowerInvariant()}.\n{(escortPerspective
                    ? $"Get {route.RequiredArrivals} of {route.ProtectedUnits.Count} protected ships through."
                    : $"Stop {route.ProtectedUnits.Count - route.RequiredArrivals + 1} protected ships.")}");
        }
        AppendRouteMessagesAndObjectiveText(mission, match, route, variantName);
        if (options.Spectator)
            mission.Write("Taskforce1StartMessage", $"ROUTE OBSERVER|Both forces operate under AI control. Blue escorts must get {route.RequiredArrivals} protected ships to EXIT. Search A/B/C are uncertain interdiction reports. Full visibility is for your display only; neither AI receives the observer's enemy truth.|Observe battle");
        for (int index = 1; index <= 3; index++)
            mission.Write($"RouteChart{index}Label_en", $"Search {(char)('A' + index - 1)}");
        mission.Write("RouteChart4Label_en", "EXIT");
        mission.Write("RouteChart5Label_en", "CONVOY START");
        mission.Write("MissionBriefingGenerateMapFromSymbols", true);
        AppendCrimeanTomSettings(mission, alliedReferences, options.Spectator);
        options.Starting.WriteTo(mission);
        mission.Write("PlayerControlsProtectedShips", controlProtectedShips);
        mission.Write("FirstForcePoints", match.First.PointCost);
        mission.Write("SecondForcePoints", match.Second.PointCost);
        mission.Write("EnemyBudgetMultiplier", options.EnemySizeMultiplier);
        mission.Write("ChaseAllowance", options.ChaseAllowance);
        mission.Write("EnemyComposition", GetComposition(playerTaskforce == "Taskforce1" ? match.Second : match.First));
        route.AttackGroups?.ForEach((group, index) => group.WriteTo(mission, index + 1));
        if (route.Voyage is { } selected)
        {
            mission.Write("ShippingRoute", selected.Summary);
            mission.Write("RouteLengthNm", selected.LengthNm);
            mission.Write("GenerationSeed", parameters.Seed);
        }
        AppendEnvironment(mission, match, parameters);
        AppendRouteMission(mission, match, size, playerTaskforce, parameters, route);
        route.FirstDeployment.WriteTo(mission, useLauncherMetadata && playerTaskforce == "Taskforce1");
        route.ProtectedUnits.ForEach(unit => unit.WriteTo(mission));
        route.SecondDeployment.WriteTo(mission, useLauncherMetadata && playerTaskforce == "Taskforce2");
        AppendCivilianTraffic(mission, parameters, match);
        AppendRouteTriggers(mission, route, playerTaskforce);
        AppendOpposedObjectives(mission, "Taskforce1DeliverProtectedGroup", "Taskforce2StopProtectedGroup");
        AppendRetreatAnchors(mission, match);
        route.Voyage?.WriteChart(mission, route.SecondDeployment);
        AppendActivity(mission, match, parameters, options, playerTaskforce);
        if (route.Voyage is { } ambushRoute)
            RouteAmbushTrigger.WriteTo(mission, route.SecondDeployment, ambushRoute,
                route.ProtectedUnits.Select(unit => unit.Reference).ToArray(),
                route.FirstDeployment.Units.Select(NavalWeaponEnvelope.From)
                    .Concat(route.ProtectedUnits.Select(unit => new NavalWeaponEnvelope(unit.UnitType, unit.X, unit.Z))).ToArray(),
                parameters.Seed, playerTaskforce == "Taskforce2", options.Spectator);
        ScenarioTerrain.ValidateNavalSpawns(mission);
        return mission.ToString();
    }

    private static void AppendRouteMessagesAndObjectiveText(
        MissionWriter mission,
        FleetClashMatch match,
        RouteOperationPlan route,
        string variantName)
    {
        int lossThreshold = route.ProtectedUnits.Count - route.RequiredArrivals + 1;
        string journey = route.Voyage is { } voyage ? $" Shipping route: {voyage.Summary}. Clear the marked local passage area; onward orders continue toward port." : string.Empty;
        mission.Write("Taskforce1StartMessage", $"ESCORT OPERATION|{match.FirstName} escorts are covering a {variantName.ToLowerInvariant()} group.{journey} Get at least {route.RequiredArrivals} protected ships to EXIT. Search A/B/C mark possible interdiction forces, not confirmed contacts. Keep the screen together rather than chase every contact. If the convoy lead is lost, assign the screen to a surviving protected ship.|Take station");
        mission.Write("Taskforce2StartMessage", $"INTERDICTION OPERATION|{match.SecondName} forces must stop the {variantName.ToLowerInvariant()} group.{journey} Small, fast attack craft may approach from astern; larger surface groups intercept ahead or from the flanks. Submarines hold separate ambush stations. Destroy at least {lossThreshold} protected ships before the remainder reaches safety.|Begin search");
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
                    "LineAstern",
                    0.75f),
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
        string[] references = route.ProtectedUnits.Select(unit => unit.Reference).ToArray();
        int lossThreshold = route.ProtectedUnits.Count - route.RequiredArrivals + 1;
        AppendStartTrigger(mission, "Issue route-operation orders");

        new MissionTrigger(2, "Protected group reaches destination",
            new(references, route.RequiredArrivals,
                new(route.Voyage?.Exit ?? new(route.DestinationX, 0, 0), 6, playerTaskforce == "Taskforce1" ? "Blue" : "Red")),
            new("Taskforce1", "Taskforce1RouteVictoryMessage", "Taskforce2RouteDefeatMessage",
                ["Taskforce1DeliverProtectedGroup"], ["Taskforce2StopProtectedGroup"])).WriteTo(mission);

        new MissionTrigger(3, "Protected group can no longer complete route",
            new(references, lossThreshold),
            new("Taskforce2", "Taskforce2RouteVictoryMessage", "Taskforce1RouteDefeatMessage",
                ["Taskforce2StopProtectedGroup"], ["Taskforce1DeliverProtectedGroup"])).WriteTo(mission);
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
        float DestinationX,
        RouteEncounter? Voyage = null,
        IReadOnlyList<RouteAttackGroup>? AttackGroups = null);

    private record RouteProtectedUnit(
        string Reference,
        string UnitType,
        string VariantReference,
        string? Nation,
        float X,
        float Z,
        IReadOnlyList<MissionWaypoint>? Waypoints,
        string StationRole,
        float Heading = 90,
        int Telegraph = 3)
    {
        internal void WriteTo(MissionWriter mission)
        {
            mission.Section(Reference);
            mission.Write("Type", UnitType);
            mission.Write("VariantReference", VariantReference);
            if (!string.IsNullOrWhiteSpace(Nation)) mission.Write("Nation", Nation);
            if (StationRole != "Default") mission.Write("StationRole", StationRole);
            mission.Write("CrewSkill", "Trained");
            mission.WritePosition("RelativePositionInNM", new(X, 0, Z));
            mission.Write("Telegraph", Telegraph);
            mission.Write("Heading", Heading);
            if (Waypoints is not null) mission.WriteWaypoints(Waypoints);
        }
    }

    private record RouteProtectedUnitOption(
        string UnitType,
        string VariantReference,
        string? Nation);
}
