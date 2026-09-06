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
        RouteCorridor corridor = CreateRouteCorridor(
            match, size, parameters.LateralOffsetNm, parameters.LateralOffsetNm < 0f ? -1f : 1f,
            "Taskforce1", playerUnitCount);
        FleetDeployment passageForce = corridor.First;
        FleetDeployment blockingForce = corridor.Second;

        string[] passageTargets = CampaignContractRules.PassageTargets(
            passageForce.Units.Select(unit => (unit.Reference, unit.Ship.PointCost)));
        int requiredArrivals = passageTargets.Length;
        int lossThreshold = 1;
        string targetNames = string.Join(", ", passageForce.Units
            .Where(unit => passageTargets.Contains(unit.Reference))
            .Select(unit => unit.Ship.UnitType.Replace('_', ' ')));
        MissionWriter mission = new();
        mission.Section("Debug");
        mission.Write("AllowAIRadarRoutine", true);

        mission.Section("Language_en");
        mission.Write("Name", $"Generated Passage Operation - {size}");
        mission.Write("Description", $"{match.Theater}: take the persistent task force through a defended passage. Get {requiredArrivals} designated high-value ships through; generation seed {parameters.Seed}.");
        mission.Write("Taskforce1StartMessage", $"PASSAGE OPERATION|{match.FirstName} must break through the defended waterway. Get at least {requiredArrivals} designated ships into the marked exit area. All must survive: {targetNames}.|Make passage");
        mission.Write("Taskforce2StartMessage", $"BLOCKADE OPERATION|{match.SecondName} must prevent the enemy task force from escaping. Stop at least {lossThreshold} designated high-value ship before the group reaches the exit.|Hold the passage");
        mission.Write("Taskforce1PassageVictoryMessage", "PASSAGE FORCED|Enough of the task force reached open water. The route is open.|End mission");
        mission.Write("Taskforce2PassageVictoryMessage", "PASSAGE DENIED|The enemy can no longer force the passage.|End mission");
        mission.Write("Taskforce1PassageDefeatMessage", "PASSAGE DENIED|Too much of the task force was lost. Withdraw the survivors.|End mission");
        mission.Write("Taskforce2PassageDefeatMessage", "BLOCKADE BROKEN|Enough enemy ships escaped through the passage.|End mission");
        mission.Write("Objective_Taskforce1ForcePassage", $"Get at least {requiredArrivals} designated ships to the exit area: {targetNames}");
        mission.Write("Objective_Taskforce2DenyPassage", $"Stop at least {lossThreshold} designated high-value ship");

        AppendCrimeanTomSettings(mission, passageForce.AlliedReferences, spectator: false);
        mission.Write("PassageContract", true);
        mission.WriteList("PassageAllUnits", passageForce.Units.Select(unit => unit.Reference));
        mission.Write("PassageDestinationX", corridor.DestinationX, "0.##");

        AppendEnvironment(mission, match, parameters);
        AppendMission(mission, match, size, "Taskforce1", parameters, passageForce, blockingForce);
        passageForce.WriteTo(mission, maskFirstNation: false);
        blockingForce.WriteTo(mission, maskFirstNation: false);
        AppendCivilianTraffic(mission, parameters);
        AppendPassageTriggers(
            mission,
            corridor.DestinationX,
            requiredArrivals,
            lossThreshold,
            passageTargets);
        AppendOpposedObjectives(mission, "Taskforce1ForcePassage", "Taskforce2DenyPassage");
        AppendRetreatAnchors(mission, match);
        return mission.ToString();
    }

    private static void AppendPassageTriggers(
        MissionWriter mission,
        float destinationX,
        int requiredArrivals,
        int lossThreshold,
        IEnumerable<string> passageUnitReferences)
    {
        string[] references = passageUnitReferences.ToArray();
        AppendStartTrigger(mission, "Issue passage orders");

        new MissionTrigger(2, "Task force reaches the passage exit",
            new(references, requiredArrivals, new(new(destinationX, 0, 0), 6, "Blue")),
            new("Taskforce1", "Taskforce1PassageVictoryMessage", "Taskforce2PassageDefeatMessage",
                ["Taskforce1ForcePassage"], ["Taskforce2DenyPassage"])).WriteTo(mission);

        new MissionTrigger(3, "Task force can no longer force the passage",
            new(references, lossThreshold),
            new("Taskforce2", "Taskforce2PassageVictoryMessage", "Taskforce1PassageDefeatMessage",
                ["Taskforce2DenyPassage"], ["Taskforce1ForcePassage"])).WriteTo(mission);
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
                missionIni.writeValue(reference, "Heading", 90);
                missionIni.writeValue(reference, "Telegraph", 2);
                missionIni.writeValue(
                    reference,
                    "Waypoints",
                    new MissionWaypoint(new(destinationX, 0, 0, depth), WaypointOrder.WeaponsFree).ToMissionValue());
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