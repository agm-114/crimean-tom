using System.Globalization;
using System.Text;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private const string ScenarioFolderName = "Crimean Tom Generated";
    private const string CommandLauncherFileName = "Generated Fleet Clash.ini";
    private const string SpectatorLauncherFileName = "Generated Fleet Clash Spectator.ini";
    private const string RouteOperationLauncherFileName = "Generated Route Operation.ini";

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
            GenerateRouteOperationEntry(
                GetRouteOperationLauncherPath(),
                new FleetClashGenerationOptions
                {
                    SeaState = 1,
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
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fullPath,
                Path.GetFullPath(GetRouteOperationLauncherPath()),
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSpectatorLauncherPath(string? scenarioPath) =>
        !string.IsNullOrWhiteSpace(scenarioPath) &&
        string.Equals(
            Path.GetFullPath(scenarioPath),
            Path.GetFullPath(GetSpectatorLauncherPath()),
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsRouteOperationLauncherPath(string? scenarioPath) =>
        !string.IsNullOrWhiteSpace(scenarioPath) &&
        string.Equals(
            Path.GetFullPath(scenarioPath),
            Path.GetFullPath(GetRouteOperationLauncherPath()),
            StringComparison.OrdinalIgnoreCase);

    internal static bool GenerateScenario(
        string scenarioPath,
        FleetClashGenerationOptions options)
    {
        try
        {
            if (IsRouteOperationLauncherPath(scenarioPath))
            {
                GenerateRouteOperationEntry(scenarioPath, options, useLauncherMetadata: false);
            }
            else
            {
                GenerateEntry(scenarioPath, options, useLauncherMetadata: false);
            }
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

    private static string GetRouteOperationLauncherPath() => Path.Combine(
        GetScenarioDirectory(),
        RouteOperationLauncherFileName);

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
                !string.Equals(name, SpectatorLauncherFileName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, RouteOperationLauncherFileName, StringComparison.OrdinalIgnoreCase))
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
        double multiplier,
        bool excludeCarriers = false)
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
            IEnumerable<FleetClashShip> additions = playerIsFirst
                ? extra.Second.Ships
                : extra.First.Ships;
            pool.AddRange(excludeCarriers
                ? additions.Where(ship => !ship.IsCarrier)
                : additions);
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
        return playerIsFirst ? match with { Second = scaledEnemy } : match with { First = scaledEnemy };
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
        return playerIsFirst ? match with { First = reordered } : match with { Second = reordered };
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

    private static float RandomRange(System.Random random, float minimum, float maximum) =>
        minimum + ((float)random.NextDouble() * (maximum - minimum));

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

    private class ScenarioParameters(
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