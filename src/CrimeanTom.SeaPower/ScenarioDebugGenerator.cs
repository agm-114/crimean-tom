using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static class ScenarioDebugGenerator
{
    private const string ScenarioFolderName = "Crimean Tom Generated";
    private const string CommandLauncherFileName = "Generated Fleet Clash.ini";
    private const string SpectatorLauncherFileName = "Generated Fleet Clash Spectator.ini";

    private static int _generationCounter;

    internal static void WriteDebugScenarios()
    {
        try
        {
            string directory = GetScenarioDirectory();
            Directory.CreateDirectory(directory);
            WriteIfChanged(Path.Combine(directory, "_info.ini"), BuildCategoryFile());
            DeleteObsoleteEntries(directory);
            GenerateEntry(
                GetCommandLauncherPath(),
                new FleetClashGenerationOptions
                {
                    SeaState = 1,
                },
                useLauncherMetadata: true);
            GenerateEntry(
                GetSpectatorLauncherPath(),
                new FleetClashGenerationOptions
                {
                    SeaState = 1,
                    Spectator = true,
                },
                useLauncherMetadata: true);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] could not generate scenario templates: {exception}");
        }
    }

    internal static bool IsLauncherPath(string? scenarioPath)
    {
        if (string.IsNullOrWhiteSpace(scenarioPath))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(scenarioPath);
        return string.Equals(
                fullPath,
                Path.GetFullPath(GetCommandLauncherPath()),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fullPath,
                Path.GetFullPath(GetSpectatorLauncherPath()),
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSpectatorLauncherPath(string? scenarioPath) =>
        !string.IsNullOrWhiteSpace(scenarioPath) &&
        string.Equals(
            Path.GetFullPath(scenarioPath),
            Path.GetFullPath(GetSpectatorLauncherPath()),
            StringComparison.OrdinalIgnoreCase);

    internal static bool GenerateScenario(
        string scenarioPath,
        FleetClashGenerationOptions options)
    {
        try
        {
            GenerateEntry(scenarioPath, options, useLauncherMetadata: false);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[Crimean Tom Operations] could not generate '{scenarioPath}': {exception}");
            return false;
        }
    }

    private static void GenerateEntry(
        string scenarioPath,
        FleetClashGenerationOptions options,
        bool useLauncherMetadata)
    {
        int seed = CreateSeed();
        System.Random random = new(seed);
        FleetClashSize size = options.Size ?? (FleetClashSize)random.Next(3);
        string playerTaskforce = options.Spectator
            ? "Taskforce1"
            : options.PlayerTaskforce ??
                (random.Next(2) == 0 ? "Taskforce1" : "Taskforce2");
        FleetClashMatch match = FleetClashBalancer.Generate(random, size, options.Region);
        match = ApplyEnemyMultiplier(match, random, size, playerTaskforce, options.EnemySizeMultiplier);
        int playerControlledCount = options.Spectator
            ? 0
            : GetPlayerControlledCount(match, playerTaskforce, options.PlayerControlPercentage);
        match = ReorderPlayerForce(match, playerTaskforce, playerControlledCount, options.ControlPriority);
        ScenarioParameters parameters = ScenarioParameters.Create(random, seed, size, options, match);
        File.WriteAllText(
            scenarioPath,
            NormalizeLineEndings(BuildFleetClash(
                match,
                parameters,
                size,
                playerTaskforce,
                playerControlledCount,
                options,
                useLauncherMetadata)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Debug.Log(
            $"[Crimean Tom Operations] generated {size} fleet clash seed {seed} in " +
            $"{match.Theater}: {match.FirstName} [{match.First.Summary}] versus " +
            $"{match.SecondName} [{match.Second.Summary}]; cost " +
            $"{match.First.PointCost}/{match.Second.PointCost}, ASuW " +
            $"{match.First.SurfaceCapability}/{match.Second.SurfaceCapability}, AAW " +
            $"{match.First.AirDefenseCapability}/{match.Second.AirDefenseCapability}; " +
            $"enemy multiplier {options.EnemySizeMultiplier:0.##}, player control " +
            $"{playerControlledCount}/{GetPlayerForce(match, playerTaskforce).Ships.Count}, " +
            $"spectator {options.Spectator}, sea state {parameters.SeaState}, civilian naval/air " +
            $"{parameters.CivilianNavalTraffic}/{parameters.CivilianAirTraffic}, separation " +
            $"{parameters.SeparationNm:0.#} nm");
    }

    private static int CreateSeed() => HashCode.Combine(
        DateTime.UtcNow.Ticks,
        System.Environment.TickCount,
        Interlocked.Increment(ref _generationCounter));

    private static string GetScenarioDirectory() => Path.Combine(
        Application.streamingAssetsPath,
        "user",
        "missions",
        ScenarioFolderName);

    private static string GetCommandLauncherPath() => Path.Combine(
        GetScenarioDirectory(),
        CommandLauncherFileName);

    private static string GetSpectatorLauncherPath() => Path.Combine(
        GetScenarioDirectory(),
        SpectatorLauncherFileName);

    private static string BuildCategoryFile() => """
        [General]
        Type=Scenario

        [Language_en]
        Name=Crimean Tom Generated
        Description=Procedurally generated contract scenarios configured inside Sea Power.
        """;

    private static void DeleteObsoleteEntries(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.ini"))
        {
            string name = Path.GetFileName(path);
            if (!string.Equals(name, "_info.ini", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, CommandLauncherFileName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, SpectatorLauncherFileName, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    private static FleetClashMatch ApplyEnemyMultiplier(
        FleetClashMatch match,
        System.Random random,
        FleetClashSize size,
        string playerTaskforce,
        double multiplier)
    {
        multiplier = Math.Clamp(multiplier, 0.5d, 2d);
        if (Math.Abs(multiplier - 1d) < 0.001d)
        {
            return match;
        }

        bool playerIsFirst = playerTaskforce == "Taskforce1";
        FleetClashForce originalEnemy = playerIsFirst ? match.Second : match.First;
        int targetCount = Math.Max(
            1,
            (int)Math.Round(
                originalEnemy.Ships.Count * multiplier,
                MidpointRounding.AwayFromZero));
        List<FleetClashShip> pool = [.. originalEnemy.Ships];
        while (pool.Count < targetCount * 3)
        {
            FleetClashMatch extra = FleetClashBalancer.Generate(random, size, match.Theater);
            pool.AddRange(playerIsFirst ? extra.Second.Ships : extra.First.Ships);
        }

        FleetClashForce? best = null;
        double bestScore = double.MaxValue;
        for (int attempt = 0; attempt < 384; attempt++)
        {
            FleetClashShip[] ships = pool
                .OrderBy(_ => random.Next())
                .Take(targetCount)
                .ToArray();
            FleetClashForce candidate = CreateForce(ships);
            double score = ScoreScaledForce(candidate, originalEnemy, multiplier);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        FleetClashForce scaledEnemy = best ?? originalEnemy;
        return new FleetClashMatch(
            match.Theater,
            match.Latitude,
            match.Longitude,
            match.FirstName,
            match.SecondName,
            playerIsFirst ? match.First : scaledEnemy,
            playerIsFirst ? scaledEnemy : match.Second,
            match.BalanceError);
    }

    private static double ScoreScaledForce(
        FleetClashForce candidate,
        FleetClashForce original,
        double multiplier)
    {
        double duplicatePenalty = candidate.Ships
            .GroupBy(ship => ship.UnitType, StringComparer.OrdinalIgnoreCase)
            .Sum(group => Math.Max(0, group.Count() - 1)) * 0.015d;
        return
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.PointCost,
                original.PointCost * multiplier) * 0.50d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.SurfaceCapability,
                original.SurfaceCapability * multiplier) * 0.25d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.AirDefenseCapability,
                original.AirDefenseCapability * multiplier) * 0.15d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.AswCapability,
                original.AswCapability * multiplier) * 0.05d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.Resilience,
                original.Resilience * multiplier) * 0.05d) +
            duplicatePenalty;
    }

    private static int GetPlayerControlledCount(
        FleetClashMatch match,
        string playerTaskforce,
        int percentage)
    {
        int shipCount = GetPlayerForce(match, playerTaskforce).Ships.Count;
        return Math.Clamp(
            (int)Math.Ceiling(shipCount * Math.Clamp(percentage, 1, 100) / 100d),
            1,
            shipCount);
    }

    private static FleetClashMatch ReorderPlayerForce(
        FleetClashMatch match,
        string playerTaskforce,
        int controlledCount,
        FleetControlPriority priority)
    {
        bool playerIsFirst = playerTaskforce == "Taskforce1";
        FleetClashForce playerForce = GetPlayerForce(match, playerTaskforce);
        IOrderedEnumerable<FleetClashShip> ordered = priority == FleetControlPriority.StrongestFirst
            ? playerForce.Ships.OrderByDescending(ship => ship.CombatValue)
            : playerForce.Ships.OrderBy(ship => ship.CombatValue);
        FleetClashShip[] ranked = ordered
            .ThenBy(ship => ship.UnitType, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        FleetClashForce reordered = CreateForce(
            ranked.Take(controlledCount).Concat(ranked.Skip(controlledCount)));
        return new FleetClashMatch(
            match.Theater,
            match.Latitude,
            match.Longitude,
            match.FirstName,
            match.SecondName,
            playerIsFirst ? reordered : match.First,
            playerIsFirst ? match.Second : reordered,
            match.BalanceError);
    }

    private static FleetClashForce GetPlayerForce(
        FleetClashMatch match,
        string playerTaskforce) =>
        playerTaskforce == "Taskforce1" ? match.First : match.Second;

    private static FleetClashForce CreateForce(IEnumerable<FleetClashShip> source)
    {
        FleetClashShip[] ships = source.ToArray();
        return new FleetClashForce(
            ships,
            ships.Sum(ship => ship.PointCost),
            ships.Sum(ship => ship.SurfaceCapability),
            ships.Sum(ship => ship.AirDefenseCapability),
            ships.Sum(ship => ship.AswCapability),
            ships.Sum(ship => ship.Resilience));
    }

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
        bool spectator)
    {
        mission.AppendLine("[CrimeanTom]");
        mission.Append("Mode=").AppendLine(spectator ? "Spectator" : "Command");
        mission.Append("AlliedAIUnits=").AppendJoin(',', playerDeployment.AlliedReferences).AppendLine();
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
            if (!ship.IsSubmarine && ship.StationRole != "Default")
            {
                mission.Append("StationRole=").AppendLine(ship.StationRole);
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
            mission.Append("Telegraph=").AppendLine(ship.IsSubmarine ? "1" : "3");
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

    private static float RandomRange(System.Random random, float minimum, float maximum) =>
        minimum + ((float)random.NextDouble() * (maximum - minimum));

    private static void AppendTriggers(
        StringBuilder mission,
        IEnumerable<string> firstReferences,
        IEnumerable<string> secondReferences,
        int firstLossesToBreak,
        int secondLossesToBreak)
    {
        mission.AppendLine("[Trigger1]");
        mission.AppendLine("Name=Start message");
        mission.AppendLine("Condition_Condition1_Type=Time");
        mission.AppendLine("Condition_Condition1_Time=2");
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
        mission.AppendLine("Action_EndMissionDelay=5");
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

    private static void WriteIfChanged(string path, string contents)
    {
        string normalized = NormalizeLineEndings(contents);
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), normalized, StringComparison.Ordinal))
        {
            File.WriteAllText(path, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static string NormalizeLineEndings(string contents) =>
        contents.Replace("\r\n", "\n").Replace("\n", System.Environment.NewLine);

    private sealed class ScenarioParameters(
        int seed,
        int hour,
        int minute,
        int seaState,
        string clouds,
        string windDirection,
        float separationNm,
        float lateralOffsetNm,
        bool civilianNavalTraffic,
        bool civilianAirTraffic,
        int neutralVesselCount,
        int neutralAircraftCount)
    {
        internal int Seed { get; } = seed;

        internal int Hour { get; } = hour;

        internal int Minute { get; } = minute;

        internal int SeaState { get; } = seaState;

        internal string Clouds { get; } = clouds;

        internal string WindDirection { get; } = windDirection;

        internal float SeparationNm { get; } = separationNm;

        internal float LateralOffsetNm { get; } = lateralOffsetNm;

        internal bool CivilianNavalTraffic { get; } = civilianNavalTraffic;

        internal bool CivilianAirTraffic { get; } = civilianAirTraffic;

        internal int NeutralVesselCount { get; } = neutralVesselCount;

        internal int NeutralAircraftCount { get; } = neutralAircraftCount;

        internal static ScenarioParameters Create(
            System.Random random,
            int seed,
            FleetClashSize size,
            FleetClashGenerationOptions options,
            FleetClashMatch match)
        {
            string[] weightedClouds = ["Clear", "Clear", "Clear", "Scattered", "Scattered", "Broken"];
            int[] weightedSeaStates =
            [
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
                2, 2, 2, 2,
                3, 3, 3, 3, 3, 3, 3, 3,
                4, 4, 4,
                5,
                6,
            ];
            string[] directions = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
            (float minimum, float maximum) = GetSeparationRange(size, match);
            int neutralVesselCount = options.CivilianNavalTraffic ? random.Next(3, 7) : 0;
            int neutralAircraftCount = options.CivilianAirTraffic ? random.Next(1, 4) : 0;
            return new ScenarioParameters(
                seed,
                random.Next(5, 19),
                random.Next(0, 12) * 5,
                options.SeaState ?? weightedSeaStates[random.Next(weightedSeaStates.Length)],
                weightedClouds[random.Next(weightedClouds.Length)],
                directions[random.Next(directions.Length)],
                minimum + ((float)random.NextDouble() * (maximum - minimum)),
                random.Next(-5, 6),
                options.CivilianNavalTraffic,
                options.CivilianAirTraffic,
                neutralVesselCount,
                neutralAircraftCount);
        }

        private static (float Minimum, float Maximum) GetSeparationRange(
            FleetClashSize size,
            FleetClashMatch match)
        {
            int carrierCount = match.First.Ships.Count(ship => ship.IsCarrier) +
                match.Second.Ships.Count(ship => ship.IsCarrier);
            bool confinedWater = match.Theater is
                "Baltic Sea" or "Eastern Mediterranean" or "South China Sea";
            if (carrierCount >= 2)
            {
                return confinedWater ? (110f, 135f) : (145f, 180f);
            }
            if (carrierCount == 1)
            {
                return confinedWater ? (85f, 110f) : (115f, 145f);
            }

            float minimum = FleetClashBalancer.GetMinimumSeparation(size);
            float maximum = FleetClashBalancer.GetMaximumSeparation(size);
            bool hasSubmarines = match.First.Ships.Any(ship => ship.IsSubmarine) ||
                match.Second.Ships.Any(ship => ship.IsSubmarine);
            if (hasSubmarines)
            {
                minimum += 8f;
                maximum += 12f;
            }

            int combatantCount = match.First.Ships.Count + match.Second.Ships.Count;
            double averageSurfaceCapability = combatantCount == 0
                ? 0d
                : (match.First.SurfaceCapability + match.Second.SurfaceCapability) /
                    combatantCount;
            if (averageSurfaceCapability >= 3.5d)
            {
                minimum += 6f;
                maximum += 10f;
            }

            return (minimum, maximum);
        }
    }
}
