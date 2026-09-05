using System.Globalization;
using System.Text;

using UnityEngine;

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
        int firstLossesToBreak = (int)Math.Ceiling(match.First.Ships.Count * 0.75d);
        int secondLossesToBreak = (int)Math.Ceiling(match.Second.Ships.Count * 0.75d);
        FleetDeployment firstDeployment = FleetOrderOfBattlePlanner.Plan(
            "Taskforce1",
            match.First,
            -parameters.SeparationNm / 2f,
            90f,
            parameters.LateralOffsetNm,
            parameters.SeparationNm,
            playerTaskforce == "Taskforce1" ? playerControlledCount : match.First.Ships.Count,
            playerTaskforce == "Taskforce1" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce1" ? "Friendly AI" : "Enemy");
        FleetDeployment secondDeployment = FleetOrderOfBattlePlanner.Plan(
            "Taskforce2",
            match.Second,
            parameters.SeparationNm / 2f,
            -90f,
            -parameters.LateralOffsetNm,
            parameters.SeparationNm,
            playerTaskforce == "Taskforce2" ? playerControlledCount : match.Second.Ships.Count,
            playerTaskforce == "Taskforce2" ? "Player" : "Enemy",
            playerTaskforce == "Taskforce2" ? "Friendly AI" : "Enemy");
        FleetDeployment playerDeployment = playerTaskforce == "Taskforce1"
            ? firstDeployment
            : secondDeployment;
        StringBuilder mission = new();

        mission.AppendLine("[Debug]");
        mission.AppendLine("AllowAIRadarRoutine=True");
        if (options.Spectator)
        {
            mission.AppendLine("ShowUnidentifiedUnitsIn3D=True");
        }
        mission.AppendLine();
        mission.AppendLine("[Language_en]");
        if (useLauncherMetadata)
        {
            mission.AppendLine(options.Spectator
                ? "Name=Generated Fleet Clash - Spectator"
                : "Name=Generated Fleet Clash");
            mission.AppendLine(options.Spectator
                ? "Description=Configure a fresh fleet clash and watch both AI forces from a Blue observer side."
                : "Description=Select this entry to configure and generate a fresh fleet clash.");
        }
        else
        {
            mission.Append("Name=Generated Fleet Clash - ").Append(size).Append(" - ")
                .Append(options.Spectator
                    ? "Spectator"
                    : playerIsFirst ? "Blue Command" : "Red Command").AppendLine();
            mission.Append("Description=").Append(match.Theater).Append(options.Spectator
                    ? ": observe the "
                    : ": command the ")
                .Append(playerName).Append(" against the ").Append(enemyName).Append(". ")
                .Append("Estimated points ").Append(match.First.PointCost).Append(" vs ")
                .Append(match.Second.PointCost).Append("; generation seed ")
                .Append(parameters.Seed.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        }
        AppendMessagesAndObjectiveText(mission, match, firstLossesToBreak, secondLossesToBreak);
        AppendCrimeanTomSettings(
            mission,
            playerDeployment,
            options.Spectator);
        AppendEnvironment(mission, match, parameters);
        AppendMission(
            mission,
            match,
            size,
            playerTaskforce,
            parameters,
            firstDeployment,
            secondDeployment);
        AppendFleet(
            mission,
            firstDeployment,
            useLauncherMetadata && playerTaskforce == "Taskforce1");
        AppendFleet(
            mission,
            secondDeployment,
            useLauncherMetadata && playerTaskforce == "Taskforce2");
        AppendCivilianTraffic(mission, parameters);
        AppendTriggers(
            mission,
            firstDeployment.Units.Select(unit => unit.Reference),
            secondDeployment.Units.Select(unit => unit.Reference),
            firstLossesToBreak,
            secondLossesToBreak);
        AppendObjectives(mission);
        AppendRetreatAnchors(mission, match);
        return mission.ToString();
    }

    private static void AppendMessagesAndObjectiveText(
        StringBuilder mission,
        FleetClashMatch match,
        int firstLossesToBreak,
        int secondLossesToBreak)
    {
        mission.Append("Taskforce1StartMessage=FLEET CLASH|").Append(match.FirstName)
            .Append(" forces are engaging the ").Append(match.SecondName)
            .AppendLine(". Break the enemy formation while preserving a combat-capable ship.|Commence action");
        mission.Append("Taskforce2StartMessage=FLEET CLASH|").Append(match.SecondName)
            .Append(" forces are engaging the ").Append(match.FirstName)
            .AppendLine(". Break the enemy formation while preserving a combat-capable ship.|Commence action");
        mission.AppendLine("Taskforce1VictoryMessage=VICTORY|The opposing surface group has lost combat cohesion. Disengage and await further orders.|End mission");
        mission.AppendLine("Taskforce2VictoryMessage=VICTORY|The opposing surface group has lost combat cohesion. Disengage and await further orders.|End mission");
        mission.AppendLine("Taskforce1DefeatMessage=DEFEAT|Your surface group has lost combat cohesion. Surviving units are ordered to withdraw.|End mission");
        mission.AppendLine("Taskforce2DefeatMessage=DEFEAT|Your surface group has lost combat cohesion. Surviving units are ordered to withdraw.|End mission");
        mission.Append("Objective_Taskforce1BreakEnemy=Destroy at least ").Append(secondLossesToBreak).AppendLine(" enemy ships");
        mission.AppendLine("Objective_Taskforce1PreserveForce=Keep at least one ship combat-capable");
        mission.Append("Objective_Taskforce2BreakEnemy=Destroy at least ").Append(firstLossesToBreak).AppendLine(" enemy ships");
        mission.AppendLine("Objective_Taskforce2PreserveForce=Keep at least one ship combat-capable");
        mission.AppendLine();
    }

    private static void AppendCrimeanTomSettings(
        StringBuilder mission,
        FleetDeployment playerDeployment,
        bool spectator) =>
        AppendCrimeanTomSettings(mission, playerDeployment.AlliedReferences, spectator);

    private static void AppendCrimeanTomSettings(
        StringBuilder mission,
        IEnumerable<string> alliedReferences,
        bool spectator)
    {
        mission.AppendLine("[CrimeanTom]");
        mission.Append("Mode=").AppendLine(spectator ? "Spectator" : "Command");
        mission.Append("AlliedAIUnits=").AppendJoin(',', alliedReferences).AppendLine();
        mission.Append("GodVision=").AppendLine(spectator ? "True" : "False");
        mission.AppendLine();
    }

    private static void AppendEnvironment(
        StringBuilder mission,
        FleetClashMatch match,
        ScenarioParameters parameters)
    {
        mission.AppendLine("[Environment]");
        mission.AppendLine("Date=1985,6,15");
        mission.Append("Time=").Append(parameters.Hour).Append(',')
            .AppendLine(parameters.Minute.ToString(CultureInfo.InvariantCulture));
        mission.Append("SeaState=").AppendLine(parameters.SeaState.ToString(CultureInfo.InvariantCulture));
        mission.Append("Clouds=").AppendLine(parameters.Clouds);
        mission.Append("WindDirection=").AppendLine(parameters.WindDirection);
        mission.Append("MapCenterLatitude=").AppendLine(match.Latitude.ToString("0.####", CultureInfo.InvariantCulture));
        mission.Append("MapCenterLongitude=").AppendLine(match.Longitude.ToString("0.####", CultureInfo.InvariantCulture));
        mission.AppendLine("LoadBackgroundData=False");
        mission.AppendLine();
    }

    private static void AppendMission(
        StringBuilder mission,
        FleetClashMatch match,
        FleetClashSize size,
        string playerTaskforce,
        ScenarioParameters parameters,
        FleetDeployment firstDeployment,
        FleetDeployment secondDeployment)
    {
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
        mission.Append("NumberOfTaskforce1Vessels=").AppendLine(firstDeployment.Units.Count(unit => !unit.Ship.IsSubmarine).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Vessels=").AppendLine(secondDeployment.Units.Count(unit => !unit.Ship.IsSubmarine).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce1Submarines=").AppendLine(firstDeployment.Units.Count(unit => unit.Ship.IsSubmarine).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Submarines=").AppendLine(secondDeployment.Units.Count(unit => unit.Ship.IsSubmarine).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce1Aircraft=").AppendLine(firstDeployment.Aircraft.Count(unit => !unit.AirPlan.IsHelicopter).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Aircraft=").AppendLine(secondDeployment.Aircraft.Count(unit => !unit.AirPlan.IsHelicopter).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce1Helicopters=").AppendLine(firstDeployment.Aircraft.Count(unit => unit.AirPlan.IsHelicopter).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfTaskforce2Helicopters=").AppendLine(secondDeployment.Aircraft.Count(unit => unit.AirPlan.IsHelicopter).ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfNeutralVessels=").AppendLine(parameters.NeutralVesselCount.ToString(CultureInfo.InvariantCulture));
        mission.Append("NumberOfNeutralAircraft=").AppendLine(parameters.NeutralAircraftCount.ToString(CultureInfo.InvariantCulture));
        AppendFormations(mission, "Taskforce1", firstDeployment.Formations);
        AppendFormations(mission, "Taskforce2", secondDeployment.Formations);
        mission.AppendLine("NumberOfTriggers=3");
        mission.AppendLine();
    }

    private static void AppendFormations(
        StringBuilder mission,
        string taskforce,
        IReadOnlyList<FleetFormationPlan> formations)
    {
        mission.Append(taskforce).Append("_NumberOfFormations=")
            .AppendLine(formations.Count.ToString(CultureInfo.InvariantCulture));
        for (int index = 0; index < formations.Count; index++)
        {
            FleetFormationPlan formation = formations[index];
            mission.Append(taskforce).Append("_Formation").Append(index + 1).Append('=')
                .AppendJoin(',', formation.References)
                .Append('|').Append(formation.Name)
                .Append('|').Append(formation.Pattern)
                .Append('|').Append(formation.Spacing.ToString("0.##", CultureInfo.InvariantCulture))
                .AppendLine("|OverrideSpawnPositions");
        }
    }

    private static void AppendFleet(
        StringBuilder mission,
        FleetDeployment deployment,
        bool maskFirstNation)
    {
        foreach (DeployedFleetUnit unit in deployment.Units)
        {
            FleetClashShip ship = unit.Ship;
            mission.Append('[').Append(unit.Reference).AppendLine("]");
            mission.Append("Type=").AppendLine(ship.UnitType);
            mission.Append("VariantReference=").AppendLine(ship.VariantReference);
            mission.Append("Nation=").AppendLine(maskFirstNation && unit.SourceIndex == 0 ? "Unknown" : ship.Nation);
            if (unit.StationRole != "Default")
            {
                mission.Append("StationRole=").AppendLine(unit.StationRole);
            }

            mission.Append("MissionType=").AppendLine(unit.MissionType);
            mission.AppendLine("WeaponStatus=Free");
            mission.AppendLine("RadarsActive=False");
            mission.AppendLine("ActiveSonarsEnabled=False");
            if (ship.IsSubmarine)
            {
                mission.AppendLine("TowedArrayDeployed=True");
            }
            mission.AppendLine("CrewSkill=Trained");
            mission.AppendLine("Morale=3");
            mission.AppendLine("RetreatAfterWeaponsExpended=True");
            mission.Append("RelativePositionInNM=")
                .Append(unit.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(ship.IsSubmarine ? "shallow" : "0").Append(',')
                .AppendLine(unit.Z.ToString("0.##", CultureInfo.InvariantCulture));
            mission.Append("Telegraph=").AppendLine(
                unit.Telegraph.ToString(CultureInfo.InvariantCulture));
            mission.Append("Heading=").AppendLine(unit.Heading.ToString("0", CultureInfo.InvariantCulture));
            if (unit.Waypoints is not null)
            {
                mission.Append("Waypoints=").AppendLine(unit.Waypoints);
            }

            mission.AppendLine();
        }

        foreach (DeployedAircraft aircraft in deployment.Aircraft)
        {
            mission.Append('[').Append(aircraft.Reference).AppendLine("]");
            mission.Append("Type=").AppendLine(aircraft.AirPlan.AircraftType);
            mission.Append("SquadronReference=").AppendLine(aircraft.AirPlan.SquadronReference);
            mission.Append("LoadoutVariant=").AppendLine(aircraft.AirPlan.LoadoutVariant);
            mission.Append("MissionType=").AppendLine(aircraft.AirPlan.MissionType);
            mission.Append("StationRole=").AppendLine(aircraft.StationRole);
            mission.AppendLine("WeaponStatus=Free");
            mission.Append("RadarsActive=").AppendLine(
                aircraft.StationRole == "AEW" || !aircraft.AirPlan.IsHelicopter
                    ? "True"
                    : "False");
            mission.AppendLine("CrewSkill=Trained");
            mission.Append("RelativePositionInNM=")
                .Append(aircraft.X.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(aircraft.Altitude.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(aircraft.Z.ToString("0.##", CultureInfo.InvariantCulture));
            mission.Append("Telegraph=").AppendLine(aircraft.StationRole == "AEW" ? "2" : "3");
            mission.Append("Heading=").AppendLine(aircraft.Heading.ToString("0", CultureInfo.InvariantCulture));
            if (aircraft.Waypoints is not null)
            {
                mission.Append("Waypoints=").AppendLine(aircraft.Waypoints);
            }
            mission.Append("HomeBase=").AppendLine(aircraft.HomeBaseReference);
            mission.AppendLine();
        }
    }

    private static void AppendCivilianTraffic(
        StringBuilder mission,
        ScenarioParameters parameters)
    {
        System.Random random = new(parameters.Seed ^ 0x43A9E27);
        string[] vesselTypes =
        [
            "civ_ms_bulk",
            "civ_ms_car_carrier_a",
            "civ_ms_freighter_a",
            "civ_ms_mairangi_bay",
            "civ_ms_ritina",
            "civ_fv_fishingboat_a",
            "civ_fv_dhow",
        ];
        for (int index = 0; index < parameters.NeutralVesselCount; index++)
        {
            float x = RandomRange(random, -parameters.SeparationNm * 0.35f, parameters.SeparationNm * 0.35f);
            float z = RandomRange(random, 12f, 32f) * (index % 2 == 0 ? 1f : -1f);
            float heading = index % 2 == 0 ? 80f : -100f;
            mission.Append("[NeutralVessel").Append(index + 1).AppendLine("]");
            mission.Append("Type=").AppendLine(vesselTypes[random.Next(vesselTypes.Length)]);
            mission.AppendLine("VariantReference=Random");
            mission.AppendLine("CrewSkill=Trained");
            mission.Append("RelativePositionInNM=")
                .Append(x.ToString("0.##", CultureInfo.InvariantCulture)).Append(",0,")
                .AppendLine(z.ToString("0.##", CultureInfo.InvariantCulture));
            mission.AppendLine("Telegraph=2");
            mission.Append("Heading=").AppendLine(heading.ToString("0", CultureInfo.InvariantCulture));
            mission.Append("Waypoints=")
                .Append((-x).ToString("0.##", CultureInfo.InvariantCulture)).Append(",0,")
                .AppendLine(z.ToString("0.##", CultureInfo.InvariantCulture));
            mission.AppendLine();
        }

        string[] aircraftTypes =
        [
            "civ_707",
            "civ_c340",
            "civ_dc-10",
            "civ_il-18",
            "civ_il-62",
            "civ_tu-154",
        ];
        for (int index = 0; index < parameters.NeutralAircraftCount; index++)
        {
            float x = parameters.SeparationNm * (index % 2 == 0 ? -1.1f : 1.1f);
            float z = RandomRange(random, -24f, 24f);
            int altitude = random.Next(2, 8) * 5000;
            float destinationX = -x;
            float heading = x < 0f ? 90f : -90f;
            mission.Append("[NeutralAircraft").Append(index + 1).AppendLine("]");
            mission.Append("Type=").AppendLine(aircraftTypes[random.Next(aircraftTypes.Length)]);
            mission.AppendLine("SquadronReference=Random");
            mission.AppendLine("CrewSkill=Trained");
            mission.Append("RelativePositionInNM=")
                .Append(x.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(altitude).Append(',')
                .AppendLine(z.ToString("0.##", CultureInfo.InvariantCulture));
            mission.AppendLine("Telegraph=3");
            mission.Append("Heading=").AppendLine(heading.ToString("0", CultureInfo.InvariantCulture));
            mission.Append("Waypoints=")
                .Append(destinationX.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(altitude).Append(',')
                .AppendLine(z.ToString("0.##", CultureInfo.InvariantCulture));
            mission.AppendLine();
        }
    }

    private static void AppendTriggers(
        StringBuilder mission,
        IEnumerable<string> firstReferences,
        IEnumerable<string> secondReferences,
        int firstLossesToBreak,
        int secondLossesToBreak)
    {
        mission.AppendLine("[Trigger1]");
        mission.AppendLine("Name=Start message");
        mission.AppendLine("Condition_Condition1_Type=OnMissionStart");
        mission.AppendLine("ConditionsCompleted=<Condition1>");
        mission.AppendLine("Action_Taskforce1_Message=Taskforce1StartMessage");
        mission.AppendLine("Action_Taskforce2_Message=Taskforce2StartMessage");
        mission.AppendLine();
        AppendOutcomeTrigger(mission, 2, "Taskforce1", firstReferences, firstLossesToBreak);
        AppendOutcomeTrigger(mission, 3, "Taskforce2", secondReferences, secondLossesToBreak);
    }

    private static void AppendOutcomeTrigger(
        StringBuilder mission,
        int triggerNumber,
        string losingTaskforce,
        IEnumerable<string> unitReferences,
        int lossesToBreak)
    {
        string winningTaskforce = losingTaskforce == "Taskforce1" ? "Taskforce2" : "Taskforce1";
        mission.Append("[Trigger").Append(triggerNumber).AppendLine("]");
        mission.Append("Name=").Append(losingTaskforce).AppendLine(" loses combat cohesion");
        mission.AppendLine("Condition_Condition1_Type=UnitDestroyed");
        mission.Append("Condition_Condition1_Units=")
            .AppendJoin(',', unitReferences).AppendLine();
        mission.Append("Condition_Condition1_MinimumUnits=").AppendLine(lossesToBreak.ToString(CultureInfo.InvariantCulture));
        mission.AppendLine("ConditionsCompleted=<Condition1>");
        mission.Append("Action_").Append(losingTaskforce).Append("_Message=")
            .Append(losingTaskforce).AppendLine("DefeatMessage");
        mission.Append("Action_").Append(winningTaskforce).Append("_Message=")
            .Append(winningTaskforce).AppendLine("VictoryMessage");
        mission.Append("Action_Victory=").AppendLine(winningTaskforce);
        mission.Append("Action_ObjectivesFailed=").Append(losingTaskforce)
            .Append("BreakEnemy,").Append(losingTaskforce).AppendLine("PreserveForce");
        mission.Append("Action_ObjectivesCompleted=").Append(winningTaskforce)
            .Append("BreakEnemy,").Append(winningTaskforce).AppendLine("PreserveForce");
        mission.AppendLine("Action_EndMission=True");
        mission.AppendLine();
    }

    private static void AppendObjectives(StringBuilder mission)
    {
        mission.AppendLine("[Taskforce1_Objectives]");
        mission.AppendLine("Taskforce1BreakEnemy=25,-25,,Main");
        mission.AppendLine("Taskforce1PreserveForce=10,-10,");
        mission.AppendLine();
        mission.AppendLine("[Taskforce2_Objectives]");
        mission.AppendLine("Taskforce2BreakEnemy=25,-25,,Main");
        mission.AppendLine("Taskforce2PreserveForce=10,-10,");
        mission.AppendLine();
    }

    private static void AppendRetreatAnchors(StringBuilder mission, FleetClashMatch match)
    {
        mission.AppendLine("[GameplayAnchors]");
        mission.AppendLine("NumberOfAnchors=2");
        mission.AppendLine("Anchor1=RetreatPoint_Blue_1");
        mission.AppendLine("Anchor2=RetreatPoint_Red_1");
        mission.AppendLine();
        AppendRetreatAnchor(mission, "RetreatPoint_Blue_1", "Blue", match.Latitude, match.Longitude - 3f);
        AppendRetreatAnchor(mission, "RetreatPoint_Red_1", "Red", match.Latitude, match.Longitude + 3f);
    }

    private static void AppendRetreatAnchor(
        StringBuilder mission,
        string name,
        string side,
        float latitude,
        float longitude)
    {
        mission.Append('[').Append(name).AppendLine("]");
        mission.AppendLine("Type=GameplayAnchor");
        mission.Append("Side=").AppendLine(side);
        mission.Append("GeoPoint=").Append(latitude.ToString("0.####", CultureInfo.InvariantCulture))
            .Append(',').AppendLine(longitude.ToString("0.####", CultureInfo.InvariantCulture));
        mission.AppendLine("AltitudeM=0");
        mission.AppendLine("RetreatPoint=True");
        mission.AppendLine();
    }
}