using System.Globalization;
using System.Text;
using SeaPower;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private static string BuildPassageOperation(
        FleetClashMatch match,
        ScenarioParameters parameters,
        FleetClashSize size,
        int playerUnitCount,
        FleetClashGenerationOptions options)
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
        float attackLane = parameters.LateralOffsetNm < 0f ? -1f : 1f;

        FleetDeployment passageForce = FleetOrderOfBattlePlanner.Plan(
            "Taskforce1",
            match.First,
            routeStart - 7f,
            90f,
            parameters.LateralOffsetNm,
            routeLength,
            playerUnitCount,
            "Player",
            "Friendly AI");
        passageForce = RetaskRouteFleet(
            passageForce,
            routeEnd,
            90f,
            isDefender: true,
            attackLane: 0f);

        FleetDeployment blockingForce = FleetOrderOfBattlePlanner.Plan(
            "Taskforce2",
            match.Second,
            interceptX + 20f,
            -90f,
            attackLane * 22f,
            routeLength,
            match.Second.Ships.Count,
            "Enemy",
            "Enemy");
        blockingForce = RetaskRouteFleet(
            blockingForce,
            interceptX,
            -90f,
            isDefender: false,
            attackLane);

        string[] passageTargets = CampaignContractRules.PassageTargets(
            passageForce.Units.Select(unit => (unit.Reference, unit.Ship.PointCost)));
        int requiredArrivals = passageTargets.Length;
        int lossThreshold = 1;
        string targetNames = string.Join(", ", passageForce.Units
            .Where(unit => passageTargets.Contains(unit.Reference))
            .Select(unit => unit.Ship.UnitType.Replace('_', ' ')));
        StringBuilder mission = new();
        mission.AppendLine("[Debug]");
        mission.AppendLine("AllowAIRadarRoutine=True");
        mission.AppendLine();
        mission.AppendLine("[Language_en]");
        mission.Append("Name=Generated Passage Operation - ").AppendLine(size.ToString());
        mission.Append("Description=").Append(match.Theater)
            .Append(": take the persistent task force through a defended passage. Get ")
            .Append(requiredArrivals).Append(" designated high-value ships through; generation seed ")
            .Append(parameters.Seed.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        mission.Append("Taskforce1StartMessage=PASSAGE OPERATION|")
            .Append(match.FirstName).Append(" must break through the defended waterway. Get at least ")
            .Append(requiredArrivals).Append(" designated ships into the marked exit area. All must survive: ")
            .Append(targetNames).AppendLine(".|Make passage");
        mission.Append("Taskforce2StartMessage=BLOCKADE OPERATION|")
            .Append(match.SecondName).Append(" must prevent the enemy task force from escaping. Stop at least ")
            .Append(lossThreshold).AppendLine(" designated high-value ship before the group reaches the exit.|Hold the passage");
        mission.AppendLine("Taskforce1PassageVictoryMessage=PASSAGE FORCED|Enough of the task force reached open water. The route is open.|End mission");
        mission.AppendLine("Taskforce2PassageVictoryMessage=PASSAGE DENIED|The enemy can no longer force the passage.|End mission");
        mission.AppendLine("Taskforce1PassageDefeatMessage=PASSAGE DENIED|Too much of the task force was lost. Withdraw the survivors.|End mission");
        mission.AppendLine("Taskforce2PassageDefeatMessage=BLOCKADE BROKEN|Enough enemy ships escaped through the passage.|End mission");
        mission.Append("Objective_Taskforce1ForcePassage=Get at least ")
            .Append(requiredArrivals).Append(" designated ships to the exit area: ").AppendLine(targetNames);
        mission.Append("Objective_Taskforce2DenyPassage=Stop at least ")
            .Append(lossThreshold).AppendLine(" designated high-value ship");
        mission.AppendLine();

        AppendCrimeanTomSettings(mission, passageForce.AlliedReferences, spectator: false);
        mission.AppendLine("PassageContract=True");
        mission.Append("PassageAllUnits=").AppendJoin(',', passageForce.Units.Select(unit => unit.Reference)).AppendLine();
        mission.Append("PassageDestinationX=")
            .AppendLine(routeEnd.ToString("0.##", CultureInfo.InvariantCulture));
        mission.AppendLine();
        AppendEnvironment(mission, match, parameters);
        AppendMission(mission, match, size, "Taskforce1", parameters, passageForce, blockingForce);
        AppendFleet(mission, passageForce, maskFirstNation: false);
        AppendFleet(mission, blockingForce, maskFirstNation: false);
        AppendCivilianTraffic(mission, parameters);
        AppendPassageTriggers(
            mission,
            routeEnd,
            requiredArrivals,
            lossThreshold,
            passageTargets);
        AppendOpposedObjectives(mission, "Taskforce1ForcePassage", "Taskforce2DenyPassage");
        AppendRetreatAnchors(mission, match);
        return mission.ToString();
    }

    private static void AppendPassageTriggers(
        StringBuilder mission,
        float destinationX,
        int requiredArrivals,
        int lossThreshold,
        IEnumerable<string> passageUnitReferences)
    {
        string joinedReferences = string.Join(',', passageUnitReferences);
        AppendStartTrigger(mission, "Issue passage orders");

        mission.AppendLine("[Trigger2]");
        mission.AppendLine("Name=Task force reaches the passage exit");
        mission.AppendLine("Condition_Condition1_Type=UnitsInTheArea");
        mission.Append("Condition_Condition1_PositionNM=")
            .Append(destinationX.ToString("0.##", CultureInfo.InvariantCulture))
            .AppendLine(",0,0");
        mission.AppendLine("Condition_Condition1_AreaRadiusNM=6");
        mission.AppendLine("Condition_Condition1_AreaDisplaySide=Blue");
        mission.Append("Condition_Condition1_Units=").AppendLine(joinedReferences);
        mission.Append("Condition_Condition1_MinimumUnits=")
            .AppendLine(requiredArrivals.ToString(CultureInfo.InvariantCulture));
        mission.AppendLine("ConditionsCompleted=<Condition1>");
        mission.AppendLine("Action_Taskforce1_Message=Taskforce1PassageVictoryMessage");
        mission.AppendLine("Action_Taskforce2_Message=Taskforce2PassageDefeatMessage");
        mission.AppendLine("Action_Victory=Taskforce1");
        mission.AppendLine("Action_ObjectivesCompleted=Taskforce1ForcePassage");
        mission.AppendLine("Action_ObjectivesFailed=Taskforce2DenyPassage");
        mission.AppendLine("Action_EndMission=True");
        mission.AppendLine();

        mission.AppendLine("[Trigger3]");
        mission.AppendLine("Name=Task force can no longer force the passage");
        mission.AppendLine("Condition_Condition1_Type=UnitDestroyed");
        mission.Append("Condition_Condition1_Units=").AppendLine(joinedReferences);
        mission.Append("Condition_Condition1_MinimumUnits=")
            .AppendLine(lossThreshold.ToString(CultureInfo.InvariantCulture));
        mission.AppendLine("ConditionsCompleted=<Condition1>");
        mission.AppendLine("Action_Taskforce2_Message=Taskforce2PassageVictoryMessage");
        mission.AppendLine("Action_Taskforce1_Message=Taskforce1PassageDefeatMessage");
        mission.AppendLine("Action_Victory=Taskforce2");
        mission.AppendLine("Action_ObjectivesCompleted=Taskforce2DenyPassage");
        mission.AppendLine("Action_ObjectivesFailed=Taskforce1ForcePassage");
        mission.AppendLine("Action_EndMission=True");
        mission.AppendLine();
    }

    internal static void FinalizeCampaignContract(string generatedMissionPath)
    {
        try
        {
            IniHandler missionIni = Utils.openIniFile(
                generatedMissionPath,
                logErrors: true,
                useCaching: false);
            if (!missionIni.readValue("CrimeanTom", "PassageContract", false))
            {
                return;
            }

            string[] references = missionIni.readValue(
                    "Trigger2",
                    "Condition_Condition1_Units",
                    string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(reference => reference.Trim())
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (references.Length == 0)
            {
                throw new InvalidOperationException(
                    "The generated passage mission contains no passage-unit references.");
            }

            string[] missingReferences = references
                .Where(reference => !missionIni.doesSectionExist(reference))
                .ToArray();
            if (missingReferences.Length > 0)
            {
                throw new InvalidOperationException(
                    $"The generated passage mission is missing referenced units: " +
                    string.Join(", ", missingReferences));
            }

            float destinationX = missionIni.readValue(
                "CrimeanTom",
                "PassageDestinationX",
                31f);
            // Every escort receives the passage route, not just the objective ships.
            references = missionIni.readValue("CrimeanTom", "PassageAllUnits", string.Join(',', references))
                .Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (string reference in references)
            {
                string position = missionIni.readValue(
                    reference,
                    "RelativePositionInNM",
                    "0,0,0",
                    performLogging: false);
                string[] coordinates = position.Split(',');
                string depth = coordinates.Length > 1 ? coordinates[1].Trim() : "0";
                string lane = coordinates.Length > 2 ? coordinates[2].Trim() : "0";
                missionIni.writeValue(reference, "Heading", 90);
                missionIni.writeValue(reference, "Telegraph", 2);
                missionIni.writeValue(
                    reference,
                    "Waypoints",
                    FormattableString.Invariant(
                        $"{destinationX:0.##},{depth},0/SetWeaponStatus,Free"));
            }

            missionIni.saveFile(overwrite: true);
            int requiredArrivals = missionIni.readValue(
                "Trigger2",
                "Condition_Condition1_MinimumUnits",
                0);
            Debug.Log(
                $"[Crimean Tom Operations] finalized passage contract with {references.Length} " +
                $"campaign units; {requiredArrivals} must reach the exit");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[Crimean Tom Operations] could not finalize campaign contract " +
                $"'{generatedMissionPath}': {exception}");
            throw;
        }
    }

}