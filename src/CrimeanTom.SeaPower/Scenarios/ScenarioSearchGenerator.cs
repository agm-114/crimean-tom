using System.Text;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private static string GetSearchPath(bool observer) => Path.Combine(GetScenarioDirectory(),
        observer ? SearchObserverFileName : SearchLauncherFileName);
    private static bool IsSearchObserverPath(string? path) => MatchesLauncher(path, SearchObserverFileName);
    internal static bool IsSearchLauncherPath(string? path) =>
        MatchesLauncher(path, SearchLauncherFileName) || IsSearchObserverPath(path);

    private static bool CanHunt(FleetUnitArchetype unit) => !unit.IsSubmarine && !unit.IsCarrier &&
        unit.AswCapability > 0 && NavalWeaponReach.Read(unit.UnitType, true) is > 0 and < float.PositiveInfinity;
    private static bool SearchTarget(FleetUnitArchetype unit, SearchVariant variant) => variant switch
    {
        SearchVariant.Submarine or SearchVariant.CounterHunt => unit.IsSubmarine && !unit.UnitType.Contains("_ssbn_"),
        SearchVariant.BallisticSubmarine => unit.IsSubmarine && unit.UnitType.Contains("_ssbn_"),
        SearchVariant.Carrier => unit.IsCarrier,
        SearchVariant.Cruiser => IsCruiser(unit.UnitType),
        SearchVariant.Tender => unit.UnitType == "wp_pb_don",
        _ => !unit.IsCarrier && !unit.IsSubmarine && unit.SurfaceCapability > 0,
    };
    internal static bool IsSearchSideViable(FleetSidePlan side, ScenarioSpecialMode mode = ScenarioSpecialMode.None, SearchVariant variant = SearchVariant.Submarine)
    {
        var pool = FleetClashForceGenerator.BuildPool(side, mode);
        return pool.Any(CanHunt);
    }

    private static bool IsCruiser(string type) => type.Contains("_cg_", StringComparison.OrdinalIgnoreCase) || type.Contains("_cgn_") ||
        type.Contains("_rkr_") || type.Contains("_tarkr_") || type.Contains("_kr_");

    private static FleetUnitArchetype[] HuntTargets(FleetSidePlan side, ScenarioSpecialMode mode, SearchVariant variant) =>
        variant == SearchVariant.Tender ? SupportHullPool(side, ["wp_pb_don"], mode)
            : variant == SearchVariant.BallisticSubmarine ? SupportHullPool(side,
                ["usn_ssbn_franklin", "usn_ssbn_james_madison", "usn_ssbn_lafayette", "wp_ssbn_delta3", "wp_ssbn_yankee"], mode)
            : FleetClashForceGenerator.BuildPool(side, mode).Where(unit => SearchTarget(unit, variant)).ToArray();

    private static void GenerateSearchEntry(string path, FleetClashGenerationOptions options, bool launcher)
    {
        int seed = options.Seed ?? CreateSeed();
        for (int attempt = 0; ; attempt++)
        {
            try { GenerateSearchAttempt(path, options, launcher, unchecked(seed + attempt * 104729)); return; }
            catch (InvalidOperationException) when (attempt < 7) { }
        }
    }

    private static void GenerateSearchAttempt(string path, FleetClashGenerationOptions options, bool launcher, int seed)
    {
        LastGenerationSeed = seed;
        System.Random random = new(seed);
        FleetClashSize size = options.Size ?? (FleetClashSize)random.Next(3);
        string player = options.Spectator ? "Taskforce1" : options.PlayerTaskforce ?? "Taskforce1";
        bool underwater = options.SearchVariant is SearchVariant.Submarine or SearchVariant.BallisticSubmarine or SearchVariant.CounterHunt;
        bool Viable(FleetSidePlan side) => IsSearchSideViable(side, options.SpecialMode, options.SearchVariant);
        var (theater, first, second) = FleetClashTheaterCatalog.Pick(random, Viable, options.Region);
        if (options.ChaosMode) (first, second) = FactionCatalog.CreateChaosSides(theater, random, Viable);
        if (HuntTargets(second, options.SpecialMode, options.SearchVariant).Length == 0) (first, second) = (second, first);
        var hunterPool = FleetClashForceGenerator.BuildPool(first, options.SpecialMode)
            .Where(unit => underwater ? CanHunt(unit) : !unit.IsCarrier && !unit.IsSubmarine && unit.SurfaceCapability > 0).ToArray();
        var targetPool = HuntTargets(second, options.SpecialMode, options.SearchVariant);
        if (targetPool.Length == 0 || hunterPool.Length == 0)
            throw new InvalidOperationException("No matching hunted target and hunters in this region. Choose another region or hunt type.");
        int targetCount = options.SearchVariant is SearchVariant.Carrier or SearchVariant.Cruiser or SearchVariant.Tender ? 1 : (int)size + 1;
        int hunterCount = 3 + (int)size * 2;
        FleetCandidate targets = Draw(targetPool, targetCount);
        FleetCandidate hunters = Enumerable.Range(0, 64).Select(_ => Draw(hunterPool, hunterCount))
            .OrderBy(candidate => Math.Abs(candidate.PointCost - targets.PointCost * 1.5)).First();
        double multiplier = Math.Clamp(options.EnemySizeMultiplier, 0.5, 2);
        if (Math.Abs(multiplier - 1) > 0.001)
        {
            bool enemyHunters = player == "Taskforce2";
            var original = enemyHunters ? hunters : targets;
            var pool = enemyHunters ? hunterPool : targetPool;
            int count = original.Ships.Count;
            var scaled = Enumerable.Range(0, 128).Select(_ => Draw(pool, random.Next(1, count * 2 + 1)))
                .OrderBy(candidate => Math.Abs(candidate.PointCost - original.PointCost * multiplier)).First();
            if (enemyHunters) hunters = scaled; else targets = scaled;
        }
        FleetClashMatch match = new(theater.Name, theater.Latitude, theater.Longitude, first.DisplayName, second.DisplayName,
            FleetClashForceGenerator.Finalize(hunters, random), FleetClashForceGenerator.Finalize(targets, random), 0,
            first, second, options.SpecialMode);
        int controlled = options.Spectator ? 0 : GetPlayerControlledCount(match, player, options.PlayerControlPercentage);
        match = ReorderPlayerForce(match, player, controlled, options.ControlPriority);
        var parameters = ScenarioParameters.Create(random, seed, size, options, match);
        string mission = BuildSearchMission(match, parameters, size, player, controlled, options, launcher);
        string briefingPath = Path.ChangeExtension(path, ".xml");
        WriteIfChanged(briefingPath, """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Margin="24">
                <TextBlock Text="HUNT" FontSize="24" Margin="0,0,0,20" />
                <TextBlock Text="Hunters" FontSize="20" Margin="0,0,0,8" />
                <TextBlock Text="Sink every hunted target before any reaches the escape area. Preserve at least one hunter ship." TextWrapping="Wrap" Margin="0,0,0,20" />
                <TextBlock Text="Hunted" FontSize="20" Margin="0,0,0,8" />
                <TextBlock Text="Reach the escape area with at least one target, or destroy every hunter ship." TextWrapping="Wrap" Margin="0,0,0,20" />
                <TextBlock Text="The last reported area is uncertain. Contacts must be acquired through sensors. There is no mission deadline." TextWrapping="Wrap" />
            </StackPanel>
            """);
        File.WriteAllText(path, NormalizeLineEndings(mission), new UTF8Encoding(false));
        Debug.Log($"[Crimean Tom Operations] generated hunt {options.SearchVariant} seed {seed}, {size}, {match.Theater}; " +
            $"hunters [{match.First.Summary}] / targets [{match.Second.Summary}], points {match.First.PointCost}/{match.Second.PointCost}, player {player}, observer {options.Spectator}");

        FleetCandidate Draw(FleetUnitArchetype[] pool, int count)
        {
            List<FleetUnitArchetype> ships = [];
            for (int index = 0; index < count; index++)
            {
                var preferred = pool.Where(unit => unit.IsRegional && !ships.Any(ship => ship.UnitType == unit.UnitType)).ToArray();
                var choices = preferred.Length > 0 && random.NextDouble() < 0.8 ? preferred : pool;
                ships.Add(choices[random.Next(choices.Length)]);
            }
            return new(ships, ships.Select(_ => underwater ? FleetRole.AntiSubmarine : FleetRole.SurfaceStrike).ToArray());
        }
    }

    private static string BuildSearchMission(FleetClashMatch match, ScenarioParameters parameters, FleetClashSize size,
        string player, int controlled, FleetClashGenerationOptions options, bool launcher)
    {
        ChartPosition center = new(match.Latitude, match.Longitude);
        System.Random random = new(parameters.Seed ^ 0x48554E54);
        float escapeX = 55 + (int)size * 15;
        FleetDeployment hunters = FleetOrderOfBattlePlanner.Plan("Taskforce1", match.First, -24, 90, 0, 40,
            player == "Taskforce1" ? controlled : match.First.Ships.Count, "Player", "Friendly AI", parameters.Seed);
        FleetDeployment targets = FleetOrderOfBattlePlanner.Plan("Taskforce2", match.Second, 8, 90, 0, 40,
            player == "Taskforce2" ? controlled : match.Second.Ships.Count, "Player", "Friendly AI", parameters.Seed);
        // Keep search tracks independent of the true target positions. Rotate the whole encounter to find safe water.
        bool placed = false;
        MissionPosition escape = new(escapeX, 0, 0), report = new(8, 0, 0);
        for (int attempt = 0; attempt < 24 && !placed; attempt++)
        {
            double angle = random.NextDouble() * Math.PI * 2;
            MissionPosition Rotate(MissionPosition p) => p with
            {
                X = (float)(p.X * Math.Cos(angle) - p.Z * Math.Sin(angle)),
                Z = (float)(p.X * Math.Sin(angle) + p.Z * Math.Cos(angle)),
            };
            escape = Rotate(new(escapeX, 0, 0)); report = Rotate(new(8, 0, 0));
            var searchers = hunters.Units.Select((unit, index) =>
            {
                float lane = (index - (hunters.Units.Count - 1) / 2f) * 5;
                bool victim = options.SearchVariant == SearchVariant.CounterHunt && index == 0;
                return Place(unit, Rotate(new(victim ? -5 : 28 + index % 2 * 4, 0, lane)),
                    [Rotate(new(12, 0, lane + 6)), Rotate(new(32, 0, lane - 6)), Rotate(new(48, 0, lane + 6)), escape],
                    options.SearchVariant is SearchVariant.Submarine or SearchVariant.BallisticSubmarine or SearchVariant.CounterHunt ? "AntiSub" : "AntiSurface", 2);
            }).ToArray();
            var fugitives = targets.Units.Select((unit, index) =>
            {
                float lane = (index - (targets.Units.Count - 1) / 2f) * 6;
                float altitude = unit.Ship.IsSubmarine ? -100 : 0;
                var start = Rotate(new(4 + (float)random.NextDouble() * 8, altitude, lane));
                return Place(unit, start, [Rotate(new(30, altitude, lane * 0.25f)), escape with { Altitude = altitude }], "NoMission", 2);
            }).ToArray();
            if (!searchers.Concat(fugitives).All(Safe)) continue;
            var aircraft = hunters.Aircraft.Select((aircraft, index) =>
            {
                var host = searchers.First(unit => unit.SourceIndex == aircraft.ParentSourceIndex);
                if (options.SearchVariant is not (SearchVariant.Submarine or SearchVariant.BallisticSubmarine or SearchVariant.CounterHunt))
                    return aircraft with { X = host.X, Z = host.Z + 3,
                        Waypoints = [new(new(host.X + 5, aircraft.Altitude, host.Z + 5)), new(new(host.X - 5, aircraft.Altitude, host.Z - 5))] };
                var plan = ScenarioUnitCatalog.GetCarrierAirPackage(host.Ship.UnitType)?.AswPatrol ?? aircraft.AirPlan;
                float lane = index % 2 == 0 ? 1 : -1;
                MissionWaypoint[] barrier = Enumerable.Range(0, 6).Select(buoy =>
                {
                    MissionPosition point = new(host.X + 3 + buoy, 200, host.Z + lane * 4);
                    return new MissionWaypoint(point, plan.SonobuoyAmmunition is { } ammo
                        ? new AttackAtWaypointOrder(ammo, point with { Altitude = 0 }) : null);
                }).ToArray();
                return aircraft with { X = host.X, Z = host.Z + lane * 3, AirPlan = plan, StationRole = "HeloASW", Waypoints = barrier };
            }).ToArray();
            hunters = hunters with { Units = searchers, Aircraft = aircraft, Formations = Solo(searchers) };
            var targetAircraft = targets.Aircraft.Select(aircraft =>
            {
                var old = targets.Units.First(unit => unit.SourceIndex == aircraft.ParentSourceIndex);
                var host = fugitives.First(unit => unit.SourceIndex == aircraft.ParentSourceIndex);
                MissionPosition Shift(MissionPosition p) => p with { X = p.X + host.X - old.X, Z = p.Z + host.Z - old.Z };
                return aircraft with { X = aircraft.X + host.X - old.X, Z = aircraft.Z + host.Z - old.Z,
                    Waypoints = aircraft.Waypoints?.Select(w => w.Transform(Shift)).ToArray() };
            }).ToArray();
            targets = targets with { Units = fugitives, Aircraft = targetAircraft, Formations = Solo(fugitives).Concat(
                targets.Formations.Where(f => f.References.All(reference => !fugitives.Any(unit => unit.Reference == reference)))).ToArray() };
            placed = true;
        }
        if (!placed) throw new UnsafeNavalSpawnException("No safe escape corridor in this region. Reroll or choose open water.");
        if (!options.Spectator)
        {
            if (player == "Taskforce1") hunters = options.Starting.Apply(hunters);
            else targets = options.Starting.Apply(targets);
        }
        MissionWriter mission = new();
        mission.Section("Debug"); mission.Write("AllowAIRadarRoutine", true);
        mission.Section("Language_en");
        mission.Write("Name", launcher ? options.Spectator ? "Generated Hunt Observer" : "Generated Hunt"
            : $"Hunt - {options.SearchVariant} - {size} - {(options.Spectator ? "Observer" : player == "Taskforce1" ? "Hunter" : "Hunted")}");
        mission.Write("Description", $"{match.Theater}. {options.SearchVariant}: {hunters.Units.Count} hunters against {targets.Units.Count} targets.\n" +
            "Hunters must sink every target before any reaches the escape area. Targets win by escaping or destroying every hunter ship.\n" +
            $"Starting fleet points: {match.First.PointCost} hunters / {match.Second.PointCost} targets. No mission deadline.");
        mission.Write("MissionBriefingGenerateMapFromSymbols", true);
        mission.Write("MissionBriefingLeftPane", Path.ChangeExtension(GetSearchPath(options.Spectator), ".xml"));
        foreach (string side in new[] { "Taskforce1", "Taskforce2" })
        {
            mission.Write(side + "StartMessage", side == "Taskforce1"
                ? "HUNT|Search the last reported area and prevent escape. The circle is an uncertainty area, not a detected contact.|Begin search"
                : "EVASION|Reach the escape area with at least one hunted unit. Avoid the search group.|Begin transit");
            mission.Write(side + "VictoryMessage", "VICTORY|Objective achieved.|End mission");
            mission.Write(side + "DefeatMessage", "DEFEAT|The opposing force achieved its objective.|End mission");
        }
        mission.Write("Objective_Taskforce1Hunt", "Sink every hunted target before any escapes; preserve at least one hunter ship");
        mission.Write("Objective_Taskforce2Escape", "Escape with at least one hunted unit or destroy every hunter ship");
        AppendCrimeanTomSettings(mission, player == "Taskforce1" ? hunters : targets, options.Spectator);
        options.Starting.WriteTo(mission);
        mission.Write("GenerationSeed", parameters.Seed); mission.Write("ScenarioFamily", "Search"); mission.Write("ScenarioVariant", options.SearchVariant);
        AppendEnvironment(mission, match, parameters);
        AppendMission(mission, match, size, player, parameters, hunters, targets);
        hunters.WriteTo(mission, launcher && player == "Taskforce1"); targets.WriteTo(mission, launcher && player == "Taskforce2");
        hunters.Units.ForEach(unit =>
        {
            mission.Set(unit.Reference, "ActiveSonarsEnabled", options.Starting.PreloadOrders || player != "Taskforce1" || options.Spectator || unit.SourceIndex >= controlled);
            mission.Set(unit.Reference, "TowedArrayDeployed", true);
        });
        targets.Units.ForEach(unit => mission.Set(unit.Reference, "RetreatAfterWeaponsExpended", false));
        if (options.SearchVariant == SearchVariant.DamagedGroup)
            targets.Units.ForEach(unit => mission.Set(unit.Reference, "Damage", "Medium"));
        if (options.SearchVariant == SearchVariant.CounterHunt)
            mission.Set(hunters.Units[0].Reference, "Damage", "Medium");
        AppendCivilianTraffic(mission, parameters, match);
        AppendStartTrigger(mission, "Search begins");
        string[] refs = targets.Units.Select(unit => unit.Reference).ToArray();
        Outcome(2, "Targets destroyed", new(refs, refs.Length), true);
        Outcome(3, "Target escaped", new(refs, 1, new(escape, 4, "None")), false);
        Outcome(4, "Hunters destroyed", new(hunters.Units.Select(unit => unit.Reference).ToArray(), hunters.Units.Count), false);
        mission.Set("Mission", "NumberOfTriggers", 4);
        AppendOpposedObjectives(mission, "Taskforce1Hunt", "Taskforce2Escape");
        AppendRetreatAnchors(mission, match);
        AppendActivity(mission, match, parameters, options, player);
        if (options.SearchVariant is not (SearchVariant.Submarine or SearchVariant.BallisticSubmarine or SearchVariant.CounterHunt))
        {
            ScenarioOpeningOrders.WriteTo(mission, hunters, targets, parameters.Seed, player == "Taskforce1", options.Spectator);
            ScenarioOpeningOrders.WriteTo(mission, targets, hunters, parameters.Seed ^ 0x1020, player == "Taskforce2", options.Spectator);
        }
        WriteSearchChart(mission, center, report, escape, player == "Taskforce1" ? hunters : targets);
        if (options.SearchVariant == SearchVariant.Decoys)
            AppendHuntDecoys(mission, match, center, report, escape, random, player);
        if (options.SearchVariant == SearchVariant.CounterHunt)
        {
            mission.Set("Language_en", "Taskforce1StartMessage", "COUNTER-HUNT|A ship has taken damage. Search outward from the attack datum before the submarine escapes.|Begin search");
            AddChartMarker(mission, center, "ATTACK DATUM", new(hunters.Units[0].X, 0, hunters.Units[0].Z), 3);
        }
        ScenarioTerrain.ValidateNavalSpawns(mission);
        return mission.ToString();

        bool Safe(DeployedFleetUnit unit)
        {
            ChartPosition previous = center.Offset(new(unit.X, 0, unit.Z));
            float depth = unit.Ship.IsSubmarine ? 80 : 20;
            if (!ScenarioTerrain.HasSpawnClearance(previous, depth)) return false;
            foreach (var waypoint in unit.Waypoints!)
            {
                ChartPosition next = center.Offset(waypoint.Position);
                if (!ScenarioTerrain.IsNavigable(previous, next, depth)) return false;
                previous = next;
            }
            return true;
        }
        void Outcome(int number, string name, MissionUnitCondition condition, bool hunterWins)
        {
            string winner = hunterWins ? "Taskforce1" : "Taskforce2", loser = hunterWins ? "Taskforce2" : "Taskforce1";
            new MissionTrigger(number, name, condition, new(winner, winner + "VictoryMessage", loser + "DefeatMessage",
                [hunterWins ? "Taskforce1Hunt" : "Taskforce2Escape"], [hunterWins ? "Taskforce2Escape" : "Taskforce1Hunt"])).WriteTo(mission);
        }
    }

    private static DeployedFleetUnit Place(DeployedFleetUnit unit, MissionPosition start, MissionPosition[] route, string task, int telegraph) =>
        unit with { X = start.X, Z = start.Z, Waypoints = route.Select(point => new MissionWaypoint(point)).ToArray(),
            MissionType = task, StationRole = task == "AntiSub" ? "ASW" : "Default", Telegraph = telegraph,
            Heading = (float)(Math.Atan2(route[0].X - start.X, route[0].Z - start.Z) * 180 / Math.PI) };
    private static FleetFormationPlan[] Solo(IEnumerable<DeployedFleetUnit> units) => units
        .Select(unit => new FleetFormationPlan([unit.Reference], unit.Ship.IsSubmarine ? "Submarine" : unit.MissionType == "AntiSub" ? "ASW search ship" : "Surface group", "Loose", 1)).ToArray();

    private static void WriteSearchChart(MissionWriter mission, ChartPosition center, MissionPosition report,
        MissionPosition escape, FleetDeployment friendly)
    {
        var symbols = new[] { ("Last reported area", report, 18, "Red"), ("ESCAPE AREA", escape, 4, "Blue"),
            ("FRIENDLY FORCE", new MissionPosition(friendly.Units[0].X, 0, friendly.Units[0].Z), 0, "Blue") };
        mission.Set("MapSymbols", "NumberOfSymbols", symbols.Length);
        symbols.ForEach((symbol, index) =>
        {
            string name = $"HuntChart{index + 1}";
            mission.Set("MapSymbols", $"Symbol{index + 1}", name);
            mission.Set("Language_en", name + "Label_en", symbol.Item1);
            mission.Section(name);
            mission.Write("Kind", symbol.Item3 == 0 ? "Label" : "Circle"); mission.Write("LabelKey", name + "Label");
            mission.Write("GeoPoint", center.Offset(symbol.Item2).ToMissionValue()); mission.Write("RadiusNm", symbol.Item3);
            mission.Write("Color", symbol.Item4); mission.Write("BorderColor", symbol.Item4);
            mission.Write("BorderWidth", 1.5); mission.Write("BorderOpacity", 0.7); mission.Write("FillOpacity", 0.03);
            mission.Write("FillStyle", "Fill"); mission.Write("FontSize", 12); mission.Write("VisibleIn", "CampaignMap,BriefingMap,Mission");
        });
        ScenarioChart.WriteViewport(mission, center, [report, escape], symbols.Select(item => center.Offset(item.Item2)), 18);
    }
}
