using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private static string ObjectivePath(ObjectiveOperation operation, bool observer) => Path.Combine(GetScenarioDirectory(),
        $"Generated {ObjectiveName(operation)}{(observer ? " Observer" : "")}.ini");
    private static string ObjectiveName(ObjectiveOperation operation) => operation switch
    {
        ObjectiveOperation.AreaControl => "Area Control",
        ObjectiveOperation.FixedTarget => "Fixed Target",
        _ => "Passage",
    };
    internal static ObjectiveOperation? ObjectiveFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (ObjectiveOperation operation in Enum.GetValues(typeof(ObjectiveOperation)))
        foreach (bool observer in new[] { false, true })
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(ObjectivePath(operation, observer)), StringComparison.OrdinalIgnoreCase)) return operation;
        return null;
    }
    private static bool IsObjectiveObserver(string? path) => ObjectiveFromPath(path) is { } operation &&
        string.Equals(Path.GetFullPath(path!), Path.GetFullPath(ObjectivePath(operation, true)), StringComparison.OrdinalIgnoreCase);

    private static void GenerateObjectiveEntry(string path, FleetClashGenerationOptions options, ObjectiveOperation operation, bool launcher)
    {
        int seed = options.Seed ?? CreateSeed();
        for (int attempt = 0; ; attempt++)
        {
            try { GenerateObjectiveAttempt(path, options, operation, launcher, unchecked(seed + attempt * 104729)); return; }
            catch (InvalidOperationException) when (attempt < 7) { }
        }
    }

    private static void GenerateObjectiveAttempt(string path, FleetClashGenerationOptions options, ObjectiveOperation operation, bool launcher, int seed)
    {
        LastGenerationSeed = seed;
        System.Random random = new(seed);
        FleetClashSize size = options.Size ?? (FleetClashSize)random.Next(3);
        string player = options.Spectator ? "Taskforce1" : options.PlayerTaskforce ?? "Taskforce1";
        bool strike = operation == ObjectiveOperation.FixedTarget;
        bool passage = operation == ObjectiveOperation.Passage;
        bool breakout = passage && options.PassageVariant is PassageVariant.Breakout or PassageVariant.CarrierBreakout or PassageVariant.RaiderBreakout;
        bool rearguard = passage && options.PassageVariant == PassageVariant.Rearguard;
        bool relief = operation == ObjectiveOperation.AreaControl && options.AreaVariant == AreaVariant.Relief;
        bool patrol = operation == ObjectiveOperation.AreaControl && options.AreaVariant == AreaVariant.FleetInBeing;
        bool sweep = operation == ObjectiveOperation.AreaControl && options.AreaVariant is AreaVariant.SurfaceSweep or AreaVariant.AswSweep;
        bool bastion = operation == ObjectiveOperation.AreaControl && options.AreaVariant == AreaVariant.Bastion;
        bool asw = bastion || operation == ObjectiveOperation.AreaControl && options.AreaVariant == AreaVariant.AswSweep;
        bool anchored = strike && options.StrikeVariant is StrikeVariant.PortRaid or StrikeVariant.RearmingShips or StrikeVariant.RepairingShips;
        bool chokepoint = passage && options.PassageVariant == PassageVariant.StraitTransit || operation == ObjectiveOperation.AreaControl && options.AreaVariant == AreaVariant.Chokepoint;
        string? region = options.Region;
        if (chokepoint && region is null)
        {
            var regions = FleetClashTheaterCatalog.RegionChances(FleetClashForceGenerator.IsViable).Keys.Where(name =>
                ShippingRoutes.EncounterChance(RouteEncounterKind.Chokepoint, new Dictionary<string, double> { [name] = 1 }) > 0).ToArray();
            if (regions.Length == 0) throw new InvalidOperationException("No navigable chokepoint routes are installed.");
            region = regions[random.Next(regions.Length)];
        }
        FleetClashMatch match = GenerateRouteMatch(random, size, region, player, options.EnemyComposition, options.ChaosMode, options.SpecialMode);
        if (chokepoint)
            match = match with
            {
                First = ReplaceForRole(match.First, "Taskforce1", unit => !unit.IsSubmarine && !unit.IsCarrier, match.First.Ships.Count),
                Second = ReplaceForRole(match.Second, "Taskforce2", unit => !unit.IsSubmarine && !unit.IsCarrier, match.Second.Ships.Count),
            };
        if (relief || rearguard || patrol)
            match = match with { First = ReplaceForRole(match.First, "Taskforce1", unit => !unit.IsSubmarine && !unit.IsCarrier, match.First.Ships.Count) };
        if (passage && options.PassageVariant == PassageVariant.AswBarrier)
            match = match with
            {
                First = ReplaceForRole(match.First, "Taskforce1", unit => unit.IsSubmarine, match.First.Ships.Count),
                Second = ReplaceForRole(match.Second, "Taskforce2", CanHunt, match.Second.Ships.Count),
            };
        if (passage && options.PassageVariant == PassageVariant.SurfacePicket)
            match = match with { Second = ReplaceForRole(match.Second, "Taskforce2", unit => !unit.IsSubmarine && !unit.IsCarrier, match.Second.Ships.Count) };
        if (passage && options.PassageVariant is PassageVariant.CarrierBreakout or PassageVariant.RaiderBreakout)
            match = match with { First = ReplaceForRole(match.First, "Taskforce1", unit => options.PassageVariant == PassageVariant.CarrierBreakout
                ? unit.IsCarrier : IsCruiser(unit.UnitType), 1) };
        if (passage && options.PassageVariant == PassageVariant.BlockadeRun)
            match = match with { First = ReplaceForRole(match.First, "Taskforce1", unit => !unit.IsCarrier && !unit.IsSubmarine &&
                ScenarioUnitCatalog.SpeedAtTelegraph(unit.UnitType, 4) >= 25, match.First.Ships.Count) };
        if (asw)
        {
            match = match with
            {
                First = ReplaceForRole(match.First, "Taskforce1", unit => CanHunt(unit), match.First.Ships.Count),
                Second = ReplaceForRole(match.Second, "Taskforce2", unit => unit.IsSubmarine, match.Second.Ships.Count),
            };
        }
        if (sweep && !asw || anchored)
            match = match with { Second = ReplaceForRole(match.Second, "Taskforce2", unit => !unit.IsSubmarine && !unit.IsCarrier, match.Second.Ships.Count) };
        if (strike && !match.First.Ships.Any(ship => ShoreGun(ship.UnitType) is not null))
        {
            var gunShips = FleetClashForceGenerator.BuildPool(new(match.FirstName, ScenarioActivityPlan.NationsFor(match, "Taskforce1"), []), options.SpecialMode)
                .Where(unit => !unit.IsCarrier && ShoreGun(unit.UnitType) is not null).OrderBy(unit => Math.Abs(unit.PointCost - match.First.Ships[0].PointCost)).ToArray();
            if (gunShips.Length == 0) throw new InvalidOperationException("No shore-bombardment ship available to this force. Choose another region.");
            var replacement = FleetClashForceGenerator.Finalize(new([gunShips[0]], [FleetRole.SurfaceStrike]), random).Ships[0];
            match = match with { First = CreateForce(new[] { replacement }.Concat(match.First.Ships.Skip(1))) };
        }
        if (!passage && !match.First.Ships.Any(ship => !ship.IsSubmarine))
            match = match with { First = ReplaceForRole(match.First, "Taskforce1", unit => !unit.IsSubmarine && !unit.IsCarrier, 1) };
        match = ApplyEnemyMultiplier(match, random, size, player, options.EnemySizeMultiplier, excludeCarriers: true,
            preserveComposition: true, requestedComposition: asw || passage ? null : options.EnemyComposition,
            acceptsForce: AcceptsForce);
        int controlled = options.Spectator ? 0 : GetPlayerControlledCount(match, player, options.PlayerControlPercentage);
        match = ReorderPlayerForce(match, player, controlled, options.ControlPriority);
        var parameters = ScenarioParameters.Create(random, seed, size, options, match);
        ChartPosition center = new(match.Latitude, match.Longitude);
        RouteEncounter? transit = passage && options.PassageVariant is PassageVariant.StraitTransit or PassageVariant.BlockadeRun or PassageVariant.Reinforcement
            || operation == ObjectiveOperation.AreaControl && options.AreaVariant == AreaVariant.Chokepoint
            ? ShippingRoutes.Pick(match.Theater, seed, size, options.PassageVariant == PassageVariant.BlockadeRun ? RouteEncounterKind.PortApproach
                : options.PassageVariant == PassageVariant.Reinforcement ? RouteEncounterKind.MidCrossing : RouteEncounterKind.Chokepoint) : null;
        if (transit is not null) center = transit.Center;
        List<(NearbyGroundOption Option, ChartPosition Position)> sites = [];
        bool coastal = strike || operation == ObjectiveOperation.AreaControl && options.AreaVariant is AreaVariant.Anchorage or AreaVariant.PortApproach;
        if (coastal)
        {
            string[] nations = ScenarioActivityPlan.NationsFor(match, "Taskforce2");
            // Regional operators can use allied export equipment even when the game only supplies the original nation's skin.
            var choices = (from nation in nations
                           let alignment = FactionCatalog.Get(nation)?.Alignment ?? FleetAlignment.Unaligned
                           from site in strike && !anchored ? StrikeSites(options.StrikeVariant, nation) : NearbyGroundCatalog.All
                           where (site.Nation == nation || alignment != FleetAlignment.Unaligned && FactionCatalog.Get(site.Nation)?.Alignment == alignment)
                               && match.SpecialMode.Allows(site.Type)
                           orderby site.Nation == nation descending, random.Next()
                           select site with { Nation = nation }).ToArray();
            foreach (var choice in choices.SelectMany(choice => Enumerable.Repeat(choice, 6)))
            {
                ChartPosition? position = ScenarioActivityPlan.FindLand(center, choice with { CoverageNm = 500 }, random);
                if (position is null || sites.Any(site => site.Position.DistanceTo(position) < 1)) continue;
                if (options.StrikeVariant == StrikeVariant.ForwardBase && sites.Any(site => site.Option.Type == choice.Type)) continue;
                if (sites.Count > 0 && sites[0].Position.DistanceTo(position) > 3) continue;
                sites.Add((choice, position));
                if (sites.Count >= (strike ? options.StrikeVariant == StrikeVariant.ForwardBase ? 3 : (int)size + 1 : 1)) break;
            }
            if (sites.Count == 0) throw new InvalidOperationException("No suitable owned coastal site in this region for the selected target. Choose another region or target type.");
            if (strike && options.StrikeVariant == StrikeVariant.ForwardBase && sites.Count < 3)
                throw new InvalidOperationException("No safe compact site for an airfield, depot and fuel stores. Choose another region.");
        }
        FleetDeployment? first = null, second = null;
        MissionPosition objective = new(0, 0, 0);
        float radius = passage ? 6 : bastion ? 22 : options.AreaVariant is AreaVariant.PortApproach or AreaVariant.Chokepoint ? 8 : 16;
        // Rotate the whole deployment, including its orders, rather than relocating individual hulls onto a different battle.
        for (int attempt = 0; attempt < 80; attempt++)
        {
            double angle = random.NextDouble() * Math.PI * 2;
            MissionPosition Rotate(MissionPosition point)
            {
                if (transit is null) return point with
                {
                    X = (float)(point.X * Math.Cos(angle) - point.Z * Math.Sin(angle)),
                    Z = (float)(point.X * Math.Sin(angle) + point.Z * Math.Cos(angle)),
                };
                var along = transit.At((point.X + 38) / 76 * transit.LengthNm);
                float spacing = Math.Max(0.05f, 0.7f * (1 - attempt / 80f));
                var offset = transit.Orient(new(0, point.Altitude, point.Z * spacing * (attempt % 2 == 0 ? 1 : -1)));
                return new(along.X + offset.X, point.Altitude, along.Z + offset.Z);
            }
            ChartPosition candidate = coastal ? sites[0].Position.Offset(Rotate(new(-7, 0, 0))) : center;
            var a = FleetOrderOfBattlePlanner.Plan("Taskforce1", match.First, -38, 90, 0, 65,
                player == "Taskforce1" ? controlled : match.First.Ships.Count, "Player", "Friendly AI", seed);
            var b = FleetOrderOfBattlePlanner.Plan("Taskforce2", match.Second, -9, -90, 6, 65,
                player == "Taskforce2" ? controlled : match.Second.Ships.Count, "Player", "Friendly AI", seed);
            a = Layout(a, false); b = Layout(b, true);
            if (!Safe(a, candidate) || !Safe(b, candidate)) continue;
            objective = Rotate(new(passage ? 38 : 0, 0, 0));
            center = candidate; first = a; second = b; break;

            FleetDeployment Layout(FleetDeployment fleet, bool defending)
            {
                var units = fleet.Units.Select((unit, index) =>
                {
                    float lane = (index - (fleet.Units.Count - 1) / 2f) * 2;
                    float defenderX = anchored && index < (int)size + 1 ? 3 : breakout ? index % 2 == 0 ? 12 : -18 : rearguard ? -48 : passage ? 0 : -8;
                    if (coastal && unit.Ship.IsSubmarine) defenderX = -24;
                    float attackerX = relief && index == 0 ? 0 : rearguard && index == 0 ? -8 : breakout ? 0 : -38;
                    if (passage && options.PassageVariant == PassageVariant.Reinforcement && index >= fleet.Units.Count / 2 && !defending) attackerX -= 12;
                    var start = Rotate(new(defending ? defenderX - index % 2 * 3 : attackerX - index % 3 * 3, 0, lane));
                    MissionPosition[] route = defending
                        ? [Rotate(new(defenderX + 3, 0, lane + 3)), Rotate(new(defenderX - 4, 0, lane - 3)), Rotate(new(defenderX + 3, 0, lane + 3))]
                        : [Rotate(new(breakout ? 18 : -20, 0, lane)), Rotate(new(passage ? 38 : strike ? 3 : -3, 0, lane * 0.2f))];
                    if (transit is not null && !defending)
                        route = Enumerable.Range(1, 10).Select(step => Rotate(new(-38 + step * 7.6f, 0, lane * (1 - step / 10f)))).ToArray();
                    if (rearguard)
                        route = defending ? [Rotate(new(-24, 0, lane)), Rotate(new(12, 0, lane)), Rotate(new(38, 0, 0))]
                            : index == 0 ? [Rotate(new(12, 0, 0)), Rotate(new(38, 0, 0))]
                            : [Rotate(new(-28, 0, lane)), Rotate(new(-10, 0, lane + 4)), Rotate(new(38, 0, 0))];
                    if (patrol && !defending)
                        route = [Rotate(new(-12, 0, 0)), Rotate(new(0, 0, 10)), Rotate(new(12, 0, 0))];
                    if (sweep)
                        route = [Rotate(new(defending ? -6 : -12, 0, lane + 8)), Rotate(new(defending ? 8 : 12, 0, lane - 8)), Rotate(new(0, 0, lane))];
                    if (relief && !defending)
                        route = index == 0 ? [Rotate(new(0, 0, 2)), Rotate(new(0, 0, -2))]
                            : [Rotate(new(-20, 0, lane)), Rotate(new(0, 0, lane * 0.1f))];
                    if (coastal && unit.Ship.IsSubmarine)
                        route = [Rotate(new(-22, -100, lane + 3)), Rotate(new(-28, -100, lane - 3))];
                    if (unit.Ship.IsSubmarine) route = route.Select(point => point with { Altitude = -100 }).ToArray();
                    var placed = Place(unit, start, route, asw && !defending || passage && defending && options.PassageVariant == PassageVariant.AswBarrier ? "AntiSub" : "AntiSurface", 2);
                    if (anchored && defending && index < (int)size + 1)
                        placed = placed with { Telegraph = 0, Waypoints = [], MissionType = "NoMission" };
                    if (strike && !anchored && !defending && ShoreGun(unit.Ship.UnitType) is { } gun)
                    {
                        var firing = route[^1];
                        placed = placed with { Waypoints = new[] { new MissionWaypoint(route[0]) }.Concat(sites.Select(site =>
                            new MissionWaypoint(firing, new AttackAtWaypointOrder(gun, site.Position.RelativeTo(candidate) with
                                { Altitude = ScenarioTerrain.HeightMeters(site.Position) / 0.3048f }, 30)))).ToArray() };
                    }
                    return placed;
                }).ToArray();
                var aircraft = fleet.Aircraft.Select(aircraft =>
                {
                    var old = fleet.Units.First(unit => unit.SourceIndex == aircraft.ParentSourceIndex);
                    var host = units.First(unit => unit.SourceIndex == aircraft.ParentSourceIndex);
                    MissionPosition Shift(MissionPosition p)
                    {
                        var offset = Rotate(new(p.X - old.X, p.Altitude, p.Z - old.Z));
                        return offset with { X = host.X + offset.X, Z = host.Z + offset.Z };
                    }
                    var p = Shift(new(aircraft.X, aircraft.Altitude, aircraft.Z));
                    return aircraft with { X = p.X, Z = p.Z, Waypoints = aircraft.Waypoints?.Select(w => w.Transform(Shift)).ToArray() };
                }).ToArray();
                return fleet with { Units = units, Aircraft = aircraft, Formations = Solo(units).Concat(
                    fleet.Formations.Where(f => f.References.All(reference => !fleet.Units.Any(unit => unit.Reference == reference)))).ToArray() };
            }
        }
        if (first is null || second is null) throw new UnsafeNavalSpawnException("No safe deployment and approach tracks here. Reroll or choose a more open coast.");
        MissionPosition[] patrolStations = first.Units[0].Waypoints?.Select(waypoint => waypoint.Position).ToArray() ?? [];
        match = match with { Latitude = (float)center.Latitude, Longitude = (float)center.Longitude };
        if (!options.Spectator)
        {
            if (player == "Taskforce1") first = options.Starting.Apply(first);
            else second = options.Starting.Apply(second);
        }
        string label = ObjectiveName(operation);
        string variant = passage ? options.PassageVariant.ToString() : strike ? options.StrikeVariant.ToString() : options.AreaVariant.ToString();
        string objectiveText = relief ? "Reach the isolated group with a relief ship, then escort the isolated group to safety." :
            patrol ? "Take your designated flagship through all three patrol stations without losing it." :
            sweep ? "Find and destroy every designated defending combatant. Nearby activity does not count." :
            passage ? "Get every designated high-value unit into the exit area. Losing any designated unit fails the passage." :
            anchored ? "Destroy every designated anchored ship before any escapes." :
            strike ? sites.Count == 1 ? "Destroy the designated shore site." : $"Destroy all {sites.Count} designated shore sites." :
            $"Bring a qualifying surface combatant into the {radius}-NM area and clear every defending combatant from it.";
        string defenseText = passage ? "Destroy any designated running unit before they all reach the exit." : "Destroy all designated attacking combatants.";
        string[] qualifying = first.Units.Where(unit => !unit.Ship.IsSubmarine && unit.Ship.PointCost >=
            first.Units.Where(ship => !ship.Ship.IsSubmarine).Select(ship => ship.Ship.PointCost).DefaultIfEmpty().Max() * 0.5).Select(unit => unit.Reference).ToArray();
        MissionWriter mission = new();
        mission.Section("Debug"); mission.Write("AllowAIRadarRoutine", true);
        mission.Section("Language_en");
        mission.Write("Name", launcher ? $"Generated {label}{(options.Spectator ? " Observer" : "")}" : $"{label} - {variant} - {size}");
        mission.Write("Description", $"{match.Theater}. {objectiveText}\nDefenders: {defenseText} No mission deadline.\nFleet points {match.First.PointCost}/{match.Second.PointCost}; shore sites {sites.Sum(site => site.Option.Points)} points.");
        mission.Write("Taskforce1StartMessage", $"{label.ToUpperInvariant()}|{objectiveText}|Begin");
        mission.Write("Taskforce2StartMessage", $"DEFEND|{defenseText}|Begin");
        foreach (string side in new[] { "Taskforce1", "Taskforce2" })
        {
            mission.Write(side + "VictoryMessage", "VICTORY|Objective achieved.|End mission");
            mission.Write(side + "DefeatMessage", "DEFEAT|The opposing force achieved its objective.|End mission");
        }
        string[] breakTargets = passage && options.PassageVariant == PassageVariant.CarrierBreakout
            ? first.Units.Where(unit => unit.Ship.IsCarrier).Select(unit => unit.Reference).ToArray()
            : passage && options.PassageVariant == PassageVariant.RaiderBreakout
            ? first.Units.Where(unit => IsCruiser(unit.Ship.UnitType)).Select(unit => unit.Reference).ToArray()
            : rearguard || relief || patrol ? [first.Units[0].Reference] :
            CampaignContractRules.PassageTargets(first.Units.Select(unit => (unit.Reference, unit.Ship.PointCost)));
        first = first with { Formations = first.Formations.Select(formation =>
        {
            int target = Array.FindIndex(breakTargets, formation.References.Contains);
            return target < 0 ? formation : formation with { Name = $"Essential group {target + 1}" };
        }).ToArray() };
        mission.Write("Objective_Attack", passage ? $"Escape with all {breakTargets.Length} essential groups" :
            strike ? $"Destroy {sites.Count} shore targets" : $"Clear and occupy the {radius}-NM area");
        mission.Write("Objective_Defend", passage ? "Destroy any essential running group" : $"Destroy all {breakTargets.Length} essential attacking groups");
        AppendCrimeanTomSettings(mission, player == "Taskforce1" ? first : second, options.Spectator);
        options.Starting.WriteTo(mission);
        mission.Write("GenerationSeed", seed); mission.Write("ScenarioFamily", operation); mission.Write("ScenarioVariant", variant);
        AppendEnvironment(mission, match, parameters); AppendMission(mission, match, size, player, parameters, first, second);
        first.WriteTo(mission); second.WriteTo(mission);
        List<string> targets = [];
        foreach (var (site, position) in anchored ? [] : sites)
        {
            string reference = $"Taskforce2LandUnit{targets.Count + 1}"; targets.Add(reference);
            mission.Section(reference); mission.Write("Type", site.Type); mission.Write("VariantReference", site.Variant);
            mission.Write("Nation", site.Nation); mission.WriteGroundPosition(position.RelativeTo(center));
            if (site.Type == "airfield_small_1") { mission.Write("CustomAirGroup", true); mission.Write("FlightDeck_AutocalculateAmmo", true); }
            mission.Write("WeaponStatus", "Free"); mission.Write("RadarsActive", true); mission.Write("CrewSkill", "Trained");
        }
        if (anchored) targets.AddRange(second.Units.Take((int)size + 1).Select(unit => unit.Reference));
        mission.Set("Mission", "NumberOfTaskforce2LandUnits", anchored ? 0 : sites.Count);
        AppendCivilianTraffic(mission, parameters, match); AppendStartTrigger(mission, "Issue objectives");
        new MissionTrigger(2, "Attack succeeds", passage ? new(breakTargets, breakTargets.Length, new(objective, radius, "None")) : new(targets, targets.Count),
            new("Taskforce1", "Taskforce1VictoryMessage", "Taskforce2DefeatMessage", ["Attack"], ["Defend"])).WriteTo(mission);
        if (operation == ObjectiveOperation.AreaControl)
        {
            mission.Section("Trigger2");
            new MissionUnitCondition(qualifying, 1, new(objective, radius, "None")).WriteTo(mission);
            mission.Write("Condition_Enemy_Type", "UnitsInTheArea");
            mission.WriteList("Condition_Enemy_Units", second.Units.Select(unit => unit.Reference));
            mission.Write("Condition_Enemy_MinimumUnits", 1);
            mission.WritePosition("Condition_Enemy_PositionNM", objective); mission.Write("Condition_Enemy_AreaRadiusNM", radius);
            mission.Write("ConditionsCompleted", "<Condition1> AND NOT <Enemy>");
        }
        new MissionTrigger(3, "Attacking force broken", new(breakTargets, passage ? 1 : breakTargets.Length),
            new("Taskforce2", "Taskforce2VictoryMessage", "Taskforce1DefeatMessage", ["Defend"], ["Attack"])).WriteTo(mission);
        AppendOpposedObjectives(mission, "Attack", "Defend"); AppendRetreatAnchors(mission, match);
        AppendActivity(mission, match, parameters, options, player);
        // Keep objective sites separate from optional activity and from campaign ownership.
        if (!relief && !patrol) ScenarioOpeningOrders.WriteTo(mission, first, second, seed, player == "Taskforce1", options.Spectator);
        ScenarioOpeningOrders.WriteTo(mission, anchored ? second with { Units = second.Units.Where(unit => !targets.Contains(unit.Reference)).ToArray() } : second,
            first, seed ^ 0x1020, player == "Taskforce2", options.Spectator);
        var markers = (relief || patrol ? Array.Empty<(string, MissionPosition, float)>()
            : anchored ? second.Units.Where(unit => targets.Contains(unit.Reference)).Select((unit, index) => ($"TARGET {index + 1}", new MissionPosition(unit.X, 0, unit.Z), 1f)).ToArray()
            : strike ? sites.Select((site, index) => ($"TARGET {index + 1}", site.Position.RelativeTo(center), 1f)).ToArray()
            : new[] { (passage ? "EXIT AREA" : sweep ? "SEARCH AREA" : "CONTROL AREA", objective, radius) }).ToList();
        var friendly = player == "Taskforce1" ? first : second;
        markers.Add(("FRIENDLY START", new(friendly.Units[0].X, 0, friendly.Units[0].Z), 0));
        if (passage) markers.Add(("POSSIBLE BLOCKING FORCE", new(0, 0, 0), 16));
        if (transit is null) WriteObjectiveChart(mission, center, markers.ToArray());
        else
        {
            transit.WriteChart(mission, second);
            markers.ForEach(marker => AddChartMarker(mission, center, marker.Item1, marker.Item2, marker.Item3));
        }
        WriteObjectiveVariant(mission, options, operation, first, second, center, targets, breakTargets, patrolStations, seed);
        WriteObjectiveBriefing(mission, path, label, mission.Read("Language_en", "Objective_Attack", objectiveText),
            $"Defenders: {mission.Read("Language_en", "Objective_Defend", defenseText)}. There is no mission deadline.",
            "Designated units are marked as Essential groups in the formation manager.");
        ScenarioTerrain.ValidateNavalSpawns(mission);
        File.WriteAllText(path, NormalizeLineEndings(mission.ToString()), new UTF8Encoding(false));
        Debug.Log($"[Crimean Tom Operations] generated {operation} {variant} seed {seed}, {match.Theater}, {size}, player {player}, observer {options.Spectator}, sites {sites.Count}");

        bool AcceptsForce(FleetClashForce force)
        {
            if (passage)
                return options.PassageVariant == PassageVariant.AswBarrier ? player == "Taskforce2"
                    ? force.Ships.All(ship => ship.IsSubmarine) : force.Ships.All(ship => !ship.IsSubmarine && ship.AswCapability > 0)
                    : player != "Taskforce2" || options.PassageVariant switch
                    {
                        PassageVariant.CarrierBreakout => force.Ships.Any(ship => ship.IsCarrier),
                        PassageVariant.RaiderBreakout => force.Ships.Any(ship => IsCruiser(ship.UnitType)),
                        PassageVariant.BlockadeRun => force.Ships.All(ship => !ship.IsSubmarine && ScenarioUnitCatalog.SpeedAtTelegraph(ship.UnitType, 4) >= 25),
                        _ => true,
                    };
            if (player == "Taskforce2")
                return force.Ships.Any(ship => !ship.IsSubmarine) && (!strike || force.Ships.Any(ship => ShoreGun(ship.UnitType) is not null)) &&
                    (!asw || force.Ships.All(ship => !ship.IsSubmarine && ship.AswCapability > 0)) &&
                    (!(relief || patrol) || force.Ships.All(ship => !ship.IsSubmarine));
            return !asw || force.Ships.All(ship => ship.IsSubmarine);
        }

        FleetClashForce ReplaceForRole(FleetClashForce force, string side, Func<FleetUnitArchetype, bool> suitable, int count)
        {
            var pool = FleetClashForceGenerator.BuildPool(new(side, ScenarioActivityPlan.NationsFor(match, side), []), options.SpecialMode)
                .Where(suitable).ToArray();
            if (pool.Length == 0) throw new InvalidOperationException("This objective needs units unavailable to these nations. Choose another region.");
            HashSet<string> used = [];
            return CreateForce(force.Ships.Select((ship, index) =>
            {
                if (index >= count) return ship;
                var ordered = pool.OrderBy(unit => Math.Abs(unit.PointCost - ship.PointCost)).ToArray();
                var alternatives = ordered.Where(unit => !used.Contains(unit.UnitType) && Math.Abs(unit.PointCost - ship.PointCost) <=
                    Math.Abs(ordered[0].PointCost - ship.PointCost) + ship.PointCost * 0.25).Take(3).ToArray();
                var selected = alternatives.Length == 0 ? ordered[0] : alternatives[random.Next(alternatives.Length)];
                used.Add(selected.UnitType);
                FleetRole role = selected.IsSubmarine || bastion || options.PassageVariant == PassageVariant.AswBarrier
                    ? FleetRole.AntiSubmarine : FleetRole.SurfaceStrike;
                return FleetClashForceGenerator.Finalize(new([selected], [role]), random).Ships[0];
            }));
        }
    }

    private static bool Safe(FleetDeployment fleet, ChartPosition center) => fleet.Units.All(unit =>
    {
        float depth = unit.Ship.IsSubmarine ? 80 : 20;
        ChartPosition previous = center.Offset(new(unit.X, 0, unit.Z));
        if (!ScenarioTerrain.HasSpawnClearance(previous, depth)) return false;
        foreach (var waypoint in unit.Waypoints ?? [])
        {
            ChartPosition next = center.Offset(waypoint.Position);
            if (!ScenarioTerrain.IsNavigable(previous, next, depth)) return false;
            previous = next;
        }
        return true;
    });

    private static bool MatchesStrike(NearbyGroundOption site, StrikeVariant variant) => variant switch
    {
        StrikeVariant.Radar => site.Points == 3 && site.CoverageNm == 100,
        StrikeVariant.CoastalBattery => site.Points == 8,
        _ => site.Points == 6,
    };

    private static readonly Dictionary<string, string?> shoreGuns = [];
    private static string? ShoreGun(string type)
    {
        if (shoreGuns.TryGetValue(type, out string? gun)) return gun;
        string root = Path.Combine(Application.streamingAssetsPath, "original"), file = Path.Combine(root, "vessels", type + ".ini");
        if (!File.Exists(file)) return shoreGuns[type] = null;
        foreach (var pair in ScenarioUnitCatalog.ReadSections(file).SelectMany(section => section.Values))
        {
            if (!Regex.IsMatch(pair.Key, @"^Ammunition\d*$")) continue;
            string ammoFile = Path.Combine(root, "ammunition", pair.Value + ".ini");
            if (!File.Exists(ammoFile)) continue;
            var data = ScenarioUnitCatalog.ReadSections(ammoFile).SelectMany(section => section.Values).ToArray();
            if (data.Any(value => value.Key == "Type" && value.Value == "Projectile") &&
                data.Any(value => value.Key.EndsWith("TargetType") && value.Value.Contains("ASuW")) &&
                data.Any(value => value.Key == "MaxRange" && float.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float range) && range >= 14000))
                return shoreGuns[type] = pair.Value;
        }
        return shoreGuns[type] = null;
    }

    private static void WriteObjectiveChart(MissionWriter mission, ChartPosition center, (string Label, MissionPosition Position, float Radius)[] symbols)
    {
        mission.Set("MapSymbols", "NumberOfSymbols", symbols.Length);
        symbols.ForEach((symbol, index) =>
        {
            string key = "ObjectiveChart" + index;
            mission.Set("MapSymbols", "Symbol" + (index + 1), key); mission.Set("Language_en", key + "Label_en", symbol.Label);
            mission.Section(key); mission.Write("Kind", symbol.Radius == 0 ? "Label" : "Circle"); mission.Write("LabelKey", key + "Label");
            mission.Write("GeoPoint", center.Offset(symbol.Position).ToMissionValue()); mission.Write("RadiusNm", symbol.Radius);
            mission.Write("Color", "Red"); mission.Write("BorderColor", "Red"); mission.Write("FillOpacity", 0.03);
            mission.Write("VisibleIn", "CampaignMap,BriefingMap,Mission");
        });
        ScenarioChart.WriteViewport(mission, center, symbols.Select(s => s.Position), symbols.Select(s => center.Offset(s.Position)), 45);
    }

    private static void WriteObjectiveBriefing(MissionWriter mission, string path, string title, params string[] paragraphs)
    {
        string briefing = Path.ChangeExtension(path, ".xml");
        static string Escape(string text) => System.Security.SecurityElement.Escape(text);
        WriteIfChanged(briefing, "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Margin=\"24\">" +
            $"<TextBlock Text=\"{Escape(title)}\" FontSize=\"24\" Margin=\"0,0,0,20\"/>" +
            string.Concat(paragraphs.Select(text => $"<TextBlock Text=\"{Escape(text)}\" TextWrapping=\"Wrap\" Margin=\"0,0,0,20\"/>")) + "</StackPanel>");
        mission.Set("Language_en", "MissionBriefingLeftPane", briefing);
        mission.Set("Language_en", "MissionBriefingGenerateMapFromSymbols", true);
    }
}
