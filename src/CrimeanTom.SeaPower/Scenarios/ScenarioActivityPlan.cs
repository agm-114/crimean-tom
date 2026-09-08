using System.Globalization;
using Application = UnityEngine.Application;

namespace CrimeanTom.SeaPower;

internal record NearbyUnit(string Side, string Type, string Nation, string Variant, string Kind,
    int Points, MissionPosition Position, IReadOnlyList<MissionWaypoint> Waypoints);

internal record ScenarioActivityPlan(IReadOnlyList<NearbyUnit> Units, IReadOnlyList<string> Notices,
    int FriendlyAllowance, int EnemyAllowance, string PlayerSide, bool SecondaryEncounter, ScenarioAirSupportPlan? AirSupport = null)
{
    internal int Cost(string side) => Units.Where(unit => unit.Side == side).Sum(unit => unit.Points) + (AirSupport?.Cost(side) ?? 0);
    internal int MinimumFriendlyFleetPoints
    {
        get
        {
            int nearby = Units.Where(unit => unit.Side == PlayerSide).Sum(unit => unit.Points);
            int air = AirSupport?.Cost(PlayerSide) ?? 0;
            return Math.Max(nearby <= 24 ? nearby : (int)Math.Ceiling(nearby / 0.4),
                air <= 10 ? air : (int)Math.Ceiling(air / 0.25));
        }
    }
    internal string Summary => string.Join("\n", new[]
    {
        $"Nearby support: friendly {Cost(PlayerSide)} / {FriendlyAllowance} pts, enemy {Cost(PlayerSide == "Taskforce1" ? "Taskforce2" : "Taskforce1")} / {EnemyAllowance} pts used.",
        SecondaryEncounter ? "Opposing local patrols may meet off the main route." : string.Empty,
    }.Concat(Notices).Where(line => line.Length > 0));

    internal static ScenarioActivityPlan Generate(FleetClashMatch match, int seed, ScenarioActivitySettings settings,
        string playerSide, int playerPoints)
    {
        Random random = new(seed ^ 0x4E454152);
        List<NearbyUnit> units = [];
        List<string> notices = [];
        int friendlyPoints = playerPoints;
        int enemyPoints = playerSide == "Taskforce1" ? match.Second.PointCost : match.First.PointCost;
        int friendlyBudget = settings.Friendly.Budget(friendlyPoints, random);
        int enemyBudget = settings.Enemy.Budget(enemyPoints, random);
        ChartPosition center = new(match.Latitude, match.Longitude);
        bool secondary = settings.SecondaryEncounters && random.NextDouble() < 0.5;
        foreach (string side in new[] { "Taskforce1", "Taskforce2" })
        {
            bool friendly = side == playerSide;
            NearbySideSettings selection = friendly ? settings.Friendly : settings.Enemy;
            int remaining = friendly ? friendlyBudget : enemyBudget;
            FleetClashForce force = side == "Taskforce1" ? match.First : match.Second;
            string[] nations = NationsFor(match, side);
            bool naval = NearbySideSettings.Enabled(selection.Naval, random);
            bool coastal = NearbySideSettings.Enabled(selection.Coastal, random);
            int before = units.Count;
            if (coastal)
            {
                int landBudget = naval ? Math.Max(3, (int)Math.Ceiling(remaining * 0.7)) : remaining;
                landBudget = Math.Min(remaining, landBudget);
                NearbyGroundOption[] options = NearbyGroundCatalog.All
                    .Where(option => nations.Contains(option.Nation, StringComparer.OrdinalIgnoreCase)).ToArray();
                foreach (NearbyGroundOption option in options.OrderBy(_ => random.Next()))
                {
                    if (option.Points > landBudget || units.Count - before >= 3) continue;
                    ChartPosition? position = FindLand(center, option, random);
                    if (position is null) continue;
                    if (units.Any(unit => unit.Kind == "LandUnit" && center.Offset(unit.Position).DistanceTo(position) < 1)) continue;
                    units.Add(new(side, option.Type, option.Nation, option.Variant, "LandUnit", option.Points, position.RelativeTo(center), []));
                    remaining -= option.Points;
                    landBudget -= option.Points;
                }
                if (units.Count == before && selection.Coastal == NearbyPresence.On)
                {
                    string reason = options.Length == 0 ? "no installations available for this force's nations"
                        : options.Min(option => option.Points) > landBudget
                            ? $"{landBudget} pts available, cheapest site costs {options.Min(option => option.Points)} pts"
                            : "no suitable owned coast within support range";
                    notices.Add($"{(friendly ? "Friendly" : "Enemy")} coastal sites: none ({reason}).");
                }
            }
            before = units.Count;
            if (naval)
            {
                var pool = FleetClashForceGenerator.BuildPool(new(force.PrimaryNation, nations, []))
                    .Where(unit => !unit.IsCarrier && unit.PointCost <= remaining).OrderBy(_ => random.Next()).ToArray();
                foreach (FleetUnitArchetype ship in pool)
                {
                    if (ship.PointCost > remaining || units.Count - before >= 3) continue;
                    NearbyUnit? placed = PlaceNaval(ship, side, center, secondary, random, units.Count - before);
                    if (placed is null) continue;
                    units.Add(placed);
                    remaining -= ship.PointCost;
                }
                if (units.Count == before && selection.Naval == NearbyPresence.On)
                    notices.Add($"{(friendly ? "Friendly" : "Enemy")} patrol omitted: no affordable unit with a safe patrol route.");
            }
        }
        secondary &= units.Where(unit => unit.Side == "Taskforce1" && unit.Kind != "LandUnit").Any(first =>
            units.Any(second => second.Side == "Taskforce2" && second.Kind != "LandUnit" &&
                Math.Abs(first.Position.Z - second.Position.Z) < 0.1f));
        ScenarioAirSupportPlan air = ScenarioAirSupportPlan.Generate(match, seed, settings, playerSide, friendlyPoints, enemyPoints);
        notices.AddRange(air.Notices);
        return new(units, notices, friendlyBudget + air.FriendlyAllowance, enemyBudget + air.EnemyAllowance, playerSide, secondary, air);
    }

    internal static string[] NationsFor(FleetClashMatch match, string side)
    {
        FleetClashForce force = side == "Taskforce1" ? match.First : match.Second;
        FleetAlignment alignment = FactionCatalog.Get(force.PrimaryNation)?.Alignment ?? FleetAlignment.Unaligned;
        return force.Ships.Select(ship => ship.Nation).Concat(FactionCatalog.Nations
            .Where(nation => alignment != FleetAlignment.Unaligned && nation.Alignment == alignment &&
                nation.ActiveTheaters.Any(theater => theater.Name == match.Theater)).Select(nation => nation.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static NearbyUnit? PlaceNaval(FleetUnitArchetype ship, string side, ChartPosition center,
        bool secondary, Random random, int index)
    {
        float sign = side == "Taskforce1" ? -1 : 1;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            float z = secondary ? (38 + index * 3 + attempt / 2 * 2) * (attempt % 2 == 0 ? 1 : -1) : 32 + (float)random.NextDouble() * 26;
            float x = sign * (secondary ? 12 : 28 + (float)random.NextDouble() * 15);
            // Both sides' secondary routes cross the same off-axis area. No forced target or result.
            MissionPosition start = new(x, 0, z, ship.IsSubmarine ? "shallow" : null);
            MissionPosition end = new(secondary ? -x : x + sign * 10, 0, z + (secondary ? 0 : 8), start.Depth);
            if (!ScenarioTerrain.IsNavigable(center.Offset(start), center.Offset(end), ship.IsSubmarine ? 80 : 20)) continue;
            return new(side, ship.UnitType, ship.Nation, ship.VariantReferences[random.Next(ship.VariantReferences.Count)],
                ship.IsSubmarine ? "Submarine" : "Vessel", ship.PointCost, start, [new(end), new(start)]);
        }
        return null;
    }

    internal static ChartPosition? FindLand(ChartPosition center, NearbyGroundOption option, Random random)
    {
        // A nearby installed port establishes local ownership; do not put an allied battery on an enemy shore.
        foreach (ShippingPort port in ShippingRoutes.Ports.Values.Where(port => port.Nation.Equals(option.Nation, StringComparison.OrdinalIgnoreCase) &&
            port.Position.DistanceTo(center) < option.CoverageNm).OrderBy(port => port.Position.DistanceTo(center)))
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                ChartPosition position = port.Position.Offset(new((float)random.NextDouble() * 6 - 3, 0, (float)random.NextDouble() * 6 - 3));
                if (position.DistanceTo(center) > option.CoverageNm) continue;
                int height = ScenarioTerrain.HeightMeters(position);
                if (height is < 2 or > 220) continue;
                if (new[] { new MissionPosition(0.2f, 0, 0), new(-0.2f, 0, 0), new(0, 0, 0.2f), new(0, 0, -0.2f) }
                    .All(offset => Math.Abs(ScenarioTerrain.HeightMeters(position.Offset(offset)) - height) < 25)) return position;
            }
        }
        return null;
    }

    internal void WriteTo(MissionWriter mission)
    {
        List<string> all = [];
        List<string> allied = mission.Read("CrimeanTom", "AlliedAIUnits").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (NearbyUnit unit in Units)
        {
            string countKey = "NumberOf" + unit.Side + unit.Kind + "s";
            int index = int.Parse(mission.Read("Mission", countKey, "0"), CultureInfo.InvariantCulture) + 1;
            mission.Set("Mission", countKey, index);
            string reference = unit.Side + unit.Kind + index;
            all.Add(reference);
            if (unit.Side == PlayerSide) allied.Add(reference);
            mission.Section(reference);
            mission.Write("Type", unit.Type);
            mission.Write("VariantReference", unit.Variant);
            mission.Write("Nation", unit.Nation);
            mission.Write("TaskForceModeIgnoreUnit", true);
            mission.Write("CrewSkill", "Trained");
            mission.WritePosition("RelativePositionInNM", unit.Position);
            mission.Write("WeaponStatus", "Tight");
            mission.Write("OverrideWeaponStatus", "Tight");
            mission.Write("RadarsActive", unit.Kind != "Submarine");
            mission.Write("ExcludeFromAIRadarRoutine", true);
            mission.Write("Telegraph", unit.Kind == "LandUnit" ? 0 : 2);
            if (unit.Waypoints.Count > 0)
            {
                MissionPosition destination = unit.Waypoints[0].Position;
                mission.Write("Heading", Math.Atan2(destination.X - unit.Position.X, destination.Z - unit.Position.Z) * 180 / Math.PI);
                mission.WriteWaypoints(unit.Waypoints, loop: true);
            }
            int delay = 600 + index * 67;
            ScenarioOpeningOrders.Release(mission, reference, [reference], unit.Waypoints, unit.Kind == "Submarine",
                unit.Kind == "LandUnit" ? 0 : 2, 240 + index * 31, delay, loop: unit.Waypoints.Count > 0);
        }
        AirSupport?.WriteTo(mission, PlayerSide, all, allied);
        mission.Set("CrimeanTom", "NearbyAIUnits", string.Join(',', all));
        mission.Set("CrimeanTom", "AlliedAIUnits", string.Join(',', allied));
        mission.Set("CrimeanTom", "NearbyFriendlyAllowance", FriendlyAllowance);
        mission.Set("CrimeanTom", "NearbyEnemyAllowance", EnemyAllowance);
        mission.Set("CrimeanTom", "NearbyFirstPoints", Cost("Taskforce1"));
        mission.Set("CrimeanTom", "NearbySecondPoints", Cost("Taskforce2"));
        mission.Set("CrimeanTom", "SecondaryEncounter", SecondaryEncounter);
        mission.Set("Language_en", "Description", mission.Read("Language_en", "Description") + "\n\n" + Summary);
        foreach (string side in new[] { "Taskforce1", "Taskforce2" })
        {
            string key = side + "StartMessage";
            string message = mission.Read("Language_en", key);
            int separator = message.LastIndexOf('|');
            if (separator >= 0) mission.Set("Language_en", key, message.Insert(separator, " " + Summary));
        }
    }
}

internal record NearbyGroundOption(string Type, string Nation, string Variant, int Points, int CoverageNm);

internal static class NearbyGroundCatalog
{
    private static IReadOnlyList<NearbyGroundOption>? all;
    internal static IReadOnlyList<NearbyGroundOption> All => all ??= Load();

    private static IReadOnlyList<NearbyGroundOption> Load()
    {
        List<NearbyGroundOption> output = [];
        string directory = Path.Combine(Application.streamingAssetsPath, "original", "land_units");
        foreach (string file in Directory.EnumerateFiles(directory, "*_variants.ini").OrderBy(path => path, StringComparer.Ordinal))
        {
            string type = Path.GetFileNameWithoutExtension(file)[..^9];
            string definition = Path.Combine(directory, type + ".ini");
            if (!File.Exists(definition)) continue;
            var sections = ScenarioUnitCatalog.ReadSections(definition).GroupBy(section => section.Section, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Values, StringComparer.OrdinalIgnoreCase);
            string subtype = sections.TryGetValue("General", out var general) && general.TryGetValue("LandUnitSubType", out string sub) ? sub : "";
            string role = sections.TryGetValue("AI", out var ai) && ai.TryGetValue("Role", out string roles) ? roles : "";
            // Match fleet-generation points, not native campaign purchase prices.
            int points = subtype == "Radar" ? 3 : subtype == "SAM" ? 6 : role.Contains("ASuW", StringComparison.OrdinalIgnoreCase) ? 8 :
                role.Contains("AAW", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
            if (points == 0) continue;
            string defaultNation = "";
            foreach (var section in ScenarioUnitCatalog.ReadSections(file))
            {
                if (section.Section == "Default" && section.Values.TryGetValue("Nation", out string nation)) defaultNation = nation;
                if (!section.Section.StartsWith("Variant", StringComparison.OrdinalIgnoreCase)) continue;
                output.Add(new(type, section.Values.TryGetValue("Nation", out string selected) ? selected : defaultNation,
                    section.Section, points, subtype == "Radar" ? 100 : 65));
            }
        }
        return output;
    }
}
