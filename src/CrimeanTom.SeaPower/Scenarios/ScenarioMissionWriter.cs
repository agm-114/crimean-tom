namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private static string BuildFleetClash(
        FleetClashMatch match,
        ScenarioParameters parameters,
        FleetClashSize size,
        string playerTaskforce,
        int playerControlledCount,
        FleetClashGenerationOptions options,
        bool useLauncherMetadata)
    {
        bool playerIsFirst = playerTaskforce == "Taskforce1";
        string playerName = playerIsFirst ? match.FirstName : match.SecondName;
        string enemyName = playerIsFirst ? match.SecondName : match.FirstName;
        int firstLossesToBreak = GetLossesToBreak(match.First.Ships.Count);
        int secondLossesToBreak = GetLossesToBreak(match.Second.Ships.Count);
        FleetDeployment firstDeployment = FleetOrderOfBattlePlanner.Plan(
            "Taskforce1",
            match.First,
            -parameters.SeparationNm / 2f,
            90f,
            parameters.LateralOffsetNm,
            parameters.SeparationNm,
            playerTaskforce == "Taskforce1" ? playerControlledCount : match.First.Ships.Count,
            playerTaskforce == "Taskforce1" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce1" ? "Friendly AI" : "Enemy", parameters.Seed);
        FleetDeployment secondDeployment = FleetOrderOfBattlePlanner.Plan(
            "Taskforce2",
            match.Second,
            parameters.SeparationNm / 2f,
            -90f,
            -parameters.LateralOffsetNm,
            parameters.SeparationNm,
            playerTaskforce == "Taskforce2" ? playerControlledCount : match.Second.Ships.Count,
            playerTaskforce == "Taskforce2" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce2" ? "Friendly AI" : "Enemy", parameters.Seed);
        if (!options.Spectator)
        {
            if (playerIsFirst) firstDeployment = options.Starting.Apply(firstDeployment);
            else secondDeployment = options.Starting.Apply(secondDeployment);
        }
        FleetDeployment playerDeployment = playerTaskforce == "Taskforce1"
            ? firstDeployment
            : secondDeployment;
        MissionWriter mission = new();

        mission.Section("Debug");
        mission.Write("AllowAIRadarRoutine", true);
        if (options.Spectator)
        {
            mission.Write("ShowUnidentifiedUnitsIn3D", true);
        }

        mission.Section("Language_en");
        if (useLauncherMetadata)
        {
            mission.Write("Name", options.Spectator
                ? "Generated Fleet Clash - Spectator"
                : "Generated Fleet Clash");
            mission.Write("Description", options.Spectator
                ? "Configure a fresh fleet clash and watch both AI forces from a Blue observer side."
                : "Select this entry to configure and generate a fresh fleet clash.");
        }
        else
        {
            mission.Write("Name", $"Generated Fleet Clash - {size} - {(options.Spectator
                    ? "Spectator"
                    : playerIsFirst ? "Blue Command" : "Red Command")}");
            mission.Write("Description", $"{match.Theater}{(options.Spectator
                    ? ": observe the "
                    : ": command the ")}{playerName} against the {enemyName}.\nEstimated fleet strength: {match.First.PointCost} vs {match.Second.PointCost} points.");
        }
        AppendMessagesAndObjectiveText(mission, match, firstLossesToBreak, secondLossesToBreak);
        AppendCrimeanTomSettings(
            mission,
            playerDeployment,
            options.Spectator);
        options.Starting.WriteTo(mission);
        mission.Write("GenerationSeed", parameters.Seed);
        mission.Write("FirstForcePoints", match.First.PointCost);
        mission.Write("SecondForcePoints", match.Second.PointCost);
        mission.Write("EnemyBudgetMultiplier", options.EnemySizeMultiplier);
        mission.Write("EnemyComposition", GetComposition(playerIsFirst ? match.Second : match.First));
        AppendEnvironment(mission, match, parameters);
        AppendMission(
            mission,
            match,
            size,
            playerTaskforce,
            parameters,
            firstDeployment,
            secondDeployment);
        firstDeployment.WriteTo(mission, useLauncherMetadata && playerTaskforce == "Taskforce1");
        secondDeployment.WriteTo(mission, useLauncherMetadata && playerTaskforce == "Taskforce2");
        AppendCivilianTraffic(mission, parameters, match);
        AppendTriggers(
            mission,
            firstDeployment.Units.Select(unit => unit.Reference),
            secondDeployment.Units.Select(unit => unit.Reference),
            firstLossesToBreak,
            secondLossesToBreak);
        AppendObjectives(mission);
        AppendRetreatAnchors(mission, match);
        AppendActivity(mission, match, parameters, options, playerTaskforce);
        ScenarioOpeningOrders.WriteTo(mission, firstDeployment, secondDeployment, parameters.Seed, playerIsFirst, options.Spectator);
        ScenarioOpeningOrders.WriteTo(mission, secondDeployment, firstDeployment, parameters.Seed ^ 0x1020, !playerIsFirst, options.Spectator);
        ScenarioChart.WriteFleetClash(mission, match, parameters.Seed, parameters.SeparationNm, playerDeployment,
            playerIsFirst ? secondDeployment : firstDeployment);
        ScenarioTerrain.ValidateNavalSpawns(mission);
        return mission.ToString();
    }

    private static void AppendActivity(MissionWriter mission, FleetClashMatch match, ScenarioParameters parameters,
        FleetClashGenerationOptions options, string playerSide)
    {
        ScenarioActivityPlan plan = options.ActivityPlan ?? ScenarioActivityPlan.Generate(match, parameters.Seed,
            options.Activity, playerSide, (playerSide == "Taskforce1" ? match.First : match.Second).PointCost);
        plan.WriteTo(mission);
    }

    private static int GetLossesToBreak(int count) =>
        Math.Clamp((int)Math.Ceiling(count * 0.75d), 1, Math.Max(1, count - 1));

    private static void AppendMessagesAndObjectiveText(
        MissionWriter mission,
        FleetClashMatch match,
        int firstLossesToBreak,
        int secondLossesToBreak)
    {
        string carrierOrders = match.First.Ships.Concat(match.Second.Ships).Any(ship => ship.IsCarrier)
            ? " Carrier groups open the range while their aircraft cover the surface groups."
            : string.Empty;
        mission.Write("Taskforce1StartMessage", $"FLEET CLASH|{match.FirstName} forces are engaging the {match.SecondName}. Break the enemy force before your own losses reach the limit shown in the objectives.{carrierOrders}|Commence action");
        mission.Write("Taskforce2StartMessage", $"FLEET CLASH|{match.SecondName} forces are engaging the {match.FirstName}. Break the enemy force before your own losses reach the limit shown in the objectives.{carrierOrders}|Commence action");
        mission.Write("Taskforce1VictoryMessage", "VICTORY|The opposing force has lost combat cohesion. Disengage and await further orders.|End mission");
        mission.Write("Taskforce2VictoryMessage", "VICTORY|The opposing force has lost combat cohesion. Disengage and await further orders.|End mission");
        mission.Write("Taskforce1DefeatMessage", "DEFEAT|Your force has lost combat cohesion. Surviving units are ordered to withdraw.|End mission");
        mission.Write("Taskforce2DefeatMessage", "DEFEAT|Your force has lost combat cohesion. Surviving units are ordered to withdraw.|End mission");
        mission.Write("Objective_Taskforce1BreakEnemy", $"Destroy at least {secondLossesToBreak} enemy ships");
        mission.Write("Objective_Taskforce1PreserveForce", $"Lose fewer than {firstLossesToBreak} ships");
        mission.Write("Objective_Taskforce2BreakEnemy", $"Destroy at least {firstLossesToBreak} enemy ships");
        mission.Write("Objective_Taskforce2PreserveForce", $"Lose fewer than {secondLossesToBreak} ships");
    }

    private static void AppendCrimeanTomSettings(
        MissionWriter mission,
        FleetDeployment playerDeployment,
        bool spectator) =>
        AppendCrimeanTomSettings(mission, playerDeployment.AlliedReferences, spectator);

    private static void AppendCrimeanTomSettings(
        MissionWriter mission,
        IEnumerable<string> alliedReferences,
        bool spectator)
    {
        mission.Section("CrimeanTom");
        mission.Write("Mode", spectator ? "Spectator" : "Command");
        mission.WriteList("AlliedAIUnits", alliedReferences);
        mission.Write("GodVision", spectator);
    }

    private static void AppendEnvironment(
        MissionWriter mission,
        FleetClashMatch match,
        ScenarioParameters parameters)
    {
        mission.Section("Environment");
        mission.Write("Date", "1985,6,15");
        mission.Write("Time", $"{parameters.Hour},{parameters.Minute}");
        mission.Write("SeaState", parameters.SeaState);
        mission.Write("Clouds", parameters.Clouds);
        mission.Write("WindDirection", parameters.WindDirection);
        mission.Write("MapCenterLatitude", match.Latitude, "0.####");
        mission.Write("MapCenterLongitude", match.Longitude, "0.####");
        mission.Write("LoadBackgroundData", false);
    }

    private static void AppendMission(
        MissionWriter mission,
        FleetClashMatch match,
        FleetClashSize size,
        string playerTaskforce,
        ScenarioParameters parameters,
        FleetDeployment firstDeployment,
        FleetDeployment secondDeployment,
        int additionalFirstVessels = 0,
        IReadOnlyList<FleetFormationPlan>? firstFormations = null)
    {
        mission.Section("Mission");
        mission.Write("Difficulty", size switch
        {
            FleetClashSize.Small => 2,
            FleetClashSize.Medium => 3,
            FleetClashSize.Large => 4,
            _ => 3,
        });
        mission.Write("PlayerTaskforce", playerTaskforce);
        mission.Write("EnemyTaskforce", playerTaskforce == "Taskforce1" ? "Taskforce2" : "Taskforce1");
        mission.Write("Taskforce1_Nation", match.First.PrimaryNation);
        mission.Write("Taskforce2_Nation", match.Second.PrimaryNation);
        mission.Write("AllowMoraleToAffectAI", true);
        mission.Write("NumberOfTaskforce1Vessels", firstDeployment.VesselCount + additionalFirstVessels);
        mission.Write("NumberOfTaskforce2Vessels", secondDeployment.VesselCount);
        mission.Write("NumberOfTaskforce1Submarines", firstDeployment.SubmarineCount);
        mission.Write("NumberOfTaskforce2Submarines", secondDeployment.SubmarineCount);
        mission.Write("NumberOfTaskforce1Aircraft", firstDeployment.FixedWingCount);
        mission.Write("NumberOfTaskforce2Aircraft", secondDeployment.FixedWingCount);
        mission.Write("NumberOfTaskforce1Helicopters", firstDeployment.HelicopterCount);
        mission.Write("NumberOfTaskforce2Helicopters", secondDeployment.HelicopterCount);
        mission.Write("NumberOfNeutralVessels", parameters.NeutralVesselCount);
        mission.Write("NumberOfNeutralAircraft", parameters.NeutralAircraftCount);
        AppendFormations(mission, "Taskforce1", firstFormations ?? firstDeployment.Formations);
        AppendFormations(mission, "Taskforce2", secondDeployment.Formations);
        mission.Write("NumberOfTriggers", 3);
    }

    private static void AppendFormations(
        MissionWriter mission,
        string taskforce,
        IReadOnlyList<FleetFormationPlan> formations)
    {
        mission.Write($"{taskforce}_NumberOfFormations", formations.Count);
        formations.ForEach((formation, index) =>
            mission.Write($"{taskforce}_Formation{index + 1}", formation.ToMissionValue()));
    }

    private static void AppendCivilianTraffic(
        MissionWriter mission,
        ScenarioParameters parameters,
        FleetClashMatch match)
    {
        var traffic = CivilianTrafficPlanner.Generate(parameters.Seed, match.Theater,
            new(match.Latitude, match.Longitude), parameters.SeparationNm,
            parameters.NeutralVesselCount, parameters.NeutralAircraftCount);
        // Terrain and route coverage can reduce the requested counts. Never leave dangling neutral references.
        mission.Set("Mission", "NumberOfNeutralVessels", traffic.Count(unit => !unit.IsAircraft));
        mission.Set("Mission", "NumberOfNeutralAircraft", traffic.Count(unit => unit.IsAircraft));
        traffic.ForEach(unit => unit.WriteTo(mission));
        CivilianAirTraffic.WriteRoutes(mission, traffic, parameters.Seed);
        ScenarioWildlife.WriteTo(mission, parameters.Seed, new(match.Latitude, match.Longitude), parameters.SeparationNm);
    }

    private static void AppendStartTrigger(MissionWriter mission, string name)
    {
        mission.Section("Trigger1");
        mission.Write("Name", name);
        mission.Write("Condition_Condition1_Type", "OnMissionStart");
        mission.Write("ConditionsCompleted", "<Condition1>");
        mission.Write("Action_Taskforce1_Message", "Taskforce1StartMessage");
        mission.Write("Action_Taskforce2_Message", "Taskforce2StartMessage");
    }

    private static void AppendTriggers(
        MissionWriter mission,
        IEnumerable<string> firstReferences,
        IEnumerable<string> secondReferences,
        int firstLossesToBreak,
        int secondLossesToBreak)
    {
        AppendStartTrigger(mission, "Start message");
        AppendOutcomeTrigger(mission, 2, "Taskforce1", firstReferences, firstLossesToBreak);
        AppendOutcomeTrigger(mission, 3, "Taskforce2", secondReferences, secondLossesToBreak);
    }

    private static void AppendOutcomeTrigger(
        MissionWriter mission,
        int triggerNumber,
        string losingTaskforce,
        IEnumerable<string> unitReferences,
        int lossesToBreak)
    {
        string winningTaskforce = losingTaskforce == "Taskforce1" ? "Taskforce2" : "Taskforce1";
        new MissionTrigger(triggerNumber, $"{losingTaskforce} loses combat cohesion",
            new(unitReferences.ToArray(), lossesToBreak),
            new(winningTaskforce, $"{winningTaskforce}VictoryMessage", $"{losingTaskforce}DefeatMessage",
                [$"{winningTaskforce}BreakEnemy", $"{winningTaskforce}PreserveForce"],
                [$"{losingTaskforce}BreakEnemy", $"{losingTaskforce}PreserveForce"])).WriteTo(mission);
    }

    private static void AppendOpposedObjectives(MissionWriter mission, string firstObjective, string secondObjective)
    {
        mission.Section("Taskforce1_Objectives");
        mission.Write(firstObjective, "50,-50,,Main");

        mission.Section("Taskforce2_Objectives");
        mission.Write(secondObjective, "50,-50,,Main");
    }

    private static void AppendObjectives(MissionWriter mission)
    {
        mission.Section("Taskforce1_Objectives");
        mission.Write("Taskforce1BreakEnemy", "25,-25,,Main");
        mission.Write("Taskforce1PreserveForce", "10,-10,");

        mission.Section("Taskforce2_Objectives");
        mission.Write("Taskforce2BreakEnemy", "25,-25,,Main");
        mission.Write("Taskforce2PreserveForce", "10,-10,");
    }

    private static void AppendRetreatAnchors(MissionWriter mission, FleetClashMatch match)
    {
        mission.Section("GameplayAnchors");
        mission.Write("NumberOfAnchors", 2);
        mission.Write("Anchor1", "RetreatPoint_Blue_1");
        mission.Write("Anchor2", "RetreatPoint_Red_1");

        AppendRetreatAnchor(mission, "RetreatPoint_Blue_1", "Blue", match.Latitude, match.Longitude - 3f);
        AppendRetreatAnchor(mission, "RetreatPoint_Red_1", "Red", match.Latitude, match.Longitude + 3f);
    }

    private static void AppendRetreatAnchor(
        MissionWriter mission,
        string name,
        string side,
        float latitude,
        float longitude)
    {
        mission.Section(name);
        mission.Write("Type", "GameplayAnchor");
        mission.Write("Side", side);
        mission.Write("GeoPoint", $"{latitude:0.####},{longitude:0.####}");
        mission.Write("AltitudeM", 0);
        mission.Write("RetreatPoint", true);
    }
}
