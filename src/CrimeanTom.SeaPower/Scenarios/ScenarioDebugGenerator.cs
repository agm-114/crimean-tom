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
    private const string RouteObserverLauncherFileName = "Generated Route Operation Observer.ini";
    private const string SearchLauncherFileName = "Generated Submarine Hunt.ini";
    private const string SearchObserverFileName = "Generated Submarine Hunt Observer.ini";

    private static int _generationCounter;
    internal static string? LastGenerationError { get; private set; }
    internal static int LastGenerationSeed { get; private set; }

    internal static void WriteDebugScenarios()
    {
        try
        {
            string directory = GetScenarioDirectory();
            Directory.CreateDirectory(directory);
            WriteIfChanged(Path.Combine(directory, "_info.ini"), BuildCategoryFile());
            DeleteObsoleteEntries(directory);
            WriteTemplate(() => GenerateEntry(
                GetCommandLauncherPath(),
                new FleetClashGenerationOptions
                {
                    SeaState = 1,
                },
                useLauncherMetadata: true));
            WriteTemplate(() => GenerateEntry(
                GetSpectatorLauncherPath(),
                new FleetClashGenerationOptions
                {
                    SeaState = 1,
                    Spectator = true,
                },
                useLauncherMetadata: true));
            WriteTemplate(() => GenerateRouteOperationEntry(
                GetRouteOperationLauncherPath(),
                new FleetClashGenerationOptions
                {
                    SeaState = 1,
                    Region = "North Atlantic",
                    RouteEncounter = RouteEncounterKind.MidCrossing,
                },
                useLauncherMetadata: true));
            WriteTemplate(() => GenerateRouteOperationEntry(GetRouteObserverLauncherPath(), new()
            {
                SeaState = 1, Region = "North Atlantic", RouteEncounter = RouteEncounterKind.MidCrossing, Spectator = true,
            }, useLauncherMetadata: true));
            foreach (bool observer in new[] { false, true })
                WriteTemplate(() => GenerateSearchEntry(GetSearchPath(observer), new()
                {
                    Size = FleetClashSize.Small, Region = "North Atlantic", Spectator = observer,
                }, true));
            foreach (ObjectiveOperation operation in Enum.GetValues(typeof(ObjectiveOperation)))
            foreach (bool observer in new[] { false, true })
                WriteTemplate(() => GenerateObjectiveEntry(ObjectivePath(operation, observer), new()
                {
                    Size = FleetClashSize.Small, Region = operation == ObjectiveOperation.FixedTarget ? "Baltic Sea" : "North Atlantic",
                    Spectator = observer, EnemyComposition = FleetComposition.SurfaceFocused,
                }, operation, true));
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] could not generate scenario templates: {exception}");
        }
    }

    private static void WriteTemplate(Action generate)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { generate(); return; }
            catch (UnsafeNavalSpawnException) when (attempt < 3) { }
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
            StringComparison.OrdinalIgnoreCase) || IsRouteObserverPath(scenarioPath) || IsSearchLauncherPath(scenarioPath) || ObjectiveFromPath(scenarioPath) is not null;
    }

    internal static bool IsSpectatorLauncherPath(string? scenarioPath) =>
        !string.IsNullOrWhiteSpace(scenarioPath) &&
        string.Equals(
            Path.GetFullPath(scenarioPath),
            Path.GetFullPath(GetSpectatorLauncherPath()),
            StringComparison.OrdinalIgnoreCase) || IsRouteObserverPath(scenarioPath) || IsSearchObserverPath(scenarioPath) || IsObjectiveObserver(scenarioPath);

    internal static bool IsRouteOperationLauncherPath(string? scenarioPath) =>
        !string.IsNullOrWhiteSpace(scenarioPath) &&
        string.Equals(
            Path.GetFullPath(scenarioPath),
            Path.GetFullPath(GetRouteOperationLauncherPath()),
            StringComparison.OrdinalIgnoreCase) || IsRouteObserverPath(scenarioPath);

    private static bool IsRouteObserverPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(GetRouteObserverLauncherPath()), StringComparison.OrdinalIgnoreCase);

    internal static bool GenerateScenario(
        string scenarioPath,
        FleetClashGenerationOptions options)
    {
        LastGenerationError = null;
        try
        {
            if (ObjectiveFromPath(scenarioPath) is { } operation)
            {
                GenerateObjectiveEntry(scenarioPath, options, operation, false);
            }
            else if (IsSearchLauncherPath(scenarioPath))
            {
                GenerateSearchEntry(scenarioPath, options, false);
            }
            else if (IsRouteOperationLauncherPath(scenarioPath))
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
            LastGenerationError = exception.Message;
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
        int seed = LastGenerationSeed = options.Seed ?? CreateSeed();
        System.Random random = new(seed);
        FleetClashSize size = options.Size ?? (FleetClashSize)random.Next(3);
        string playerTaskforce = options.Spectator
            ? "Taskforce1"
            : options.PlayerTaskforce ??
                (random.Next(2) == 0 ? "Taskforce1" : "Taskforce2");
        FleetClashMatch match = FleetClashBalancer.Generate(random, size, options.Region, playerTaskforce,
            options.EnemyComposition, chaosMode: options.ChaosMode, specialMode: options.SpecialMode);
        match = ApplyEnemyMultiplier(match, random, size, playerTaskforce, options.EnemySizeMultiplier,
            preserveComposition: options.EnemyComposition != FleetComposition.Random, requestedComposition: options.EnemyComposition);
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
    private static string GetRouteObserverLauncherPath() => Path.Combine(GetScenarioDirectory(), RouteObserverLauncherFileName);

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
                !string.Equals(name, RouteOperationLauncherFileName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, RouteObserverLauncherFileName, StringComparison.OrdinalIgnoreCase) &&
                  !IsSearchLauncherPath(path) && ObjectiveFromPath(path) is null)
            {
                File.Delete(path);
            }
        }
    }

    internal static FleetClashMatch ApplyEnemyMultiplier(
        FleetClashMatch match,
        System.Random random,
        FleetClashSize size,
        string playerTaskforce,
        double multiplier,
        bool excludeCarriers = false,
        bool preserveComposition = true,
        FleetComposition? requestedComposition = null,
        Func<FleetClashForce, bool>? acceptsForce = null)
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
        FleetComposition composition = requestedComposition is { } requested && requested != FleetComposition.Random
            ? requested : GetComposition(originalEnemy);
        FleetSidePlan enemyNations = new(playerIsFirst ? match.SecondName : match.FirstName,
            originalEnemy.Ships.Select(ship => ship.Nation).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), []);
        FleetUnitArchetype[] alternatives = FleetClashForceGenerator.BuildPool(enemyNations, match.SpecialMode)
            .Where(unit => !excludeCarriers || !unit.IsCarrier).ToArray();
        pool.AddRange(FleetClashForceGenerator.Finalize(new(alternatives,
            alternatives.Select(unit => unit.IsSubmarine ? FleetRole.AntiSubmarine : FleetRole.Escort).ToArray()), random).Ships);
        FleetClashShip[] surfacePool = pool.Where(ship => !ship.IsSubmarine).ToArray();
        FleetClashShip[] submarinePool = pool.Where(ship => ship.IsSubmarine).ToArray();
        double targetCost = originalEnemy.PointCost * multiplier;
        double roundingAllowance = Math.Max(targetCost * 0.25, pool.Min(ship => ship.PointCost) / 2d);

        FleetClashForce? best = null;
        double bestScore = double.MaxValue;
        for (int attempt = 0; attempt < 384; attempt++)
        {
            FleetComposition profile = !preserveComposition && attempt >= 288 && surfacePool.Length > 0 &&
                (best is null || Math.Abs(best.PointCost - targetCost) > roundingAllowance)
                ? FleetComposition.SurfaceFocused : composition;
            int count = random.Next(Math.Max(profile is FleetComposition.Mixed or FleetComposition.EvenMix ? 2 : 1, targetCount / 2),
                Math.Max(3, targetCount * 2 + 1));
            int evenSubmarines = profile == FleetComposition.EvenMix ? count / 2 + (count % 2 == 0 ? 0 : random.Next(2)) : 0;
            FleetClashShip[] ships = Enumerable.Range(0, count).Select(index =>
            {
                bool submarine = profile == FleetComposition.EvenMix ? index < evenSubmarines :
                    profile == FleetComposition.Mixed ? index == 0 :
                    profile == FleetComposition.SubmarineHeavy && (surfacePool.Length == 0 || index == 0 || random.NextDouble() < 0.7);
                FleetClashShip[] source = submarine ? submarinePool : surfacePool;
                return source[random.Next(source.Length)];
            }).ToArray();
            FleetClashForce candidate = CreateForce(ships);
            if (acceptsForce is not null && !acceptsForce(candidate)) continue;
            if (profile != FleetComposition.EvenMix && GetComposition(candidate) != profile) continue;
            if (multiplier < 1 && candidate.PointCost >= originalEnemy.PointCost ||
                multiplier > 1 && candidate.PointCost <= originalEnemy.PointCost) continue;
            double score = ScoreScaledForce(candidate, originalEnemy, multiplier);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        FleetClashForce scaledEnemy = best is not null && Math.Abs(best.PointCost - targetCost) <= roundingAllowance ? best : throw new InvalidOperationException(
            $"The selected nations cannot fit a {composition} fleet at {multiplier:0.00}x budget. Choose a larger battle or another composition.");
        return playerIsFirst ? match with { Second = scaledEnemy } : match with { First = scaledEnemy };
    }

    internal static FleetComposition GetComposition(FleetClashForce force)
    {
        int submarines = force.Ships.Where(ship => ship.IsSubmarine).Sum(ship => ship.PointCost);
        return submarines == 0 ? FleetComposition.SurfaceFocused
            : submarines >= force.PointCost * 0.5 ? FleetComposition.SubmarineHeavy : FleetComposition.Mixed;
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
                original.PointCost * multiplier) * 0.80d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.SurfaceCapability,
                original.SurfaceCapability * multiplier) * 0.10d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.AirDefenseCapability,
                original.AirDefenseCapability * multiplier) * 0.05d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.AswCapability,
                original.AswCapability * multiplier) * 0.025d) +
            (FleetClashBalanceEvaluator.RelativeDifference(
                candidate.Resilience,
                original.Resilience * multiplier) * 0.025d) +
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
            string[] directions = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
            (float minimum, float maximum) = GetSeparationRange(size, match);
            int scale = size switch { FleetClashSize.Small => 0, FleetClashSize.Medium => 1, _ => 2 };
            int neutralVesselCount = options.CivilianNavalTraffic
                ? new System.Random(seed ^ 0x536561).Next(12 + scale * 6, 19 + scale * 8) : 0;
            int neutralAircraftCount = options.CivilianAirTraffic
                ? new System.Random(seed ^ 0x536B79).Next(6 + scale * 2, 10 + scale * 3) : 0;
            return new ScenarioParameters(
                seed,
                random.Next(5, 19),
                random.Next(0, 12) * 5,
                options.SeaState ?? ScenarioRandomization.PickIndex(random, ScenarioRandomization.SeaStateWeights) + 1,
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
