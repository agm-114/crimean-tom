using System.Globalization;

namespace CrimeanTom.SeaPower;

internal record SupportingAirfield(string Side, string Nation, string Name, MissionPosition Position, int Points = 4);
internal record SupportingAircraft(string Side, string Nation, CarrierAirPlan Plan, string Role,
    MissionPosition Position, IReadOnlyList<MissionWaypoint> Waypoints, int Points);

internal record ScenarioAirSupportPlan(IReadOnlyList<SupportingAirfield> Airfields,
    IReadOnlyList<SupportingAircraft> Aircraft, IReadOnlyList<string> Notices, int FriendlyAllowance, int EnemyAllowance)
{
    internal int Cost(string side) => Airfields.Where(field => field.Side == side).Sum(field => field.Points) +
        Aircraft.Where(aircraft => aircraft.Side == side).Sum(aircraft => aircraft.Points);

    internal static ScenarioAirSupportPlan Generate(FleetClashMatch match, int seed, ScenarioActivitySettings settings,
        string playerSide, int friendlyPoints, int enemyPoints)
    {
        List<SupportingAirfield> fields = [];
        List<SupportingAircraft> aircraft = [];
        List<string> notices = [];
        int friendlyBudget = settings.Friendly.AirBudget(friendlyPoints), enemyBudget = settings.Enemy.AirBudget(enemyPoints);
        ChartPosition center = new(match.Latitude, match.Longitude);
        foreach (string side in new[] { "Taskforce1", "Taskforce2" })
        {
            Random random = new(seed ^ (side == "Taskforce1" ? 0x41697231 : 0x41697232));
            bool friendly = side == playerSide;
            NearbySideSettings selection = friendly ? settings.Friendly : settings.Enemy;
            int remaining = friendly ? friendlyBudget : enemyBudget;
            string label = friendly ? "Friendly" : "Enemy";
            string[] nations = ScenarioActivityPlan.NationsFor(match, side);
            float rear = side == "Taskforce1" ? -1 : 1;
            SupportingAirfield? field = null;
            if (NearbySideSettings.Enabled(selection.Airfields, random) && remaining >= 4)
            {
                // Existing military airport locations take precedence over a generic field near an owned port.
                var airport = MilitaryAirports.All.Where(airport => nations.Contains(airport.Nation, StringComparer.OrdinalIgnoreCase) &&
                    airport.Position.DistanceTo(center) is >= 35 and <= 500)
                    .OrderBy(airport => airport.Position.DistanceTo(center)).FirstOrDefault();
                if (airport is not null && ScenarioTerrain.HeightMeters(airport.Position) >= 0)
                    field = new(side, airport.Nation, airport.Name, airport.Position.RelativeTo(center));
                foreach (string nation in nations.OrderBy(_ => random.Next()))
                {
                    if (field is not null) break;
                    ChartPosition? position = ScenarioActivityPlan.FindLand(center,
                        new("airfield_small_1", nation, "Variant1", 4, 360), random);
                    if (position is not null && position.DistanceTo(center) >= 35)
                        field = new(side, nation, $"{nation} regional airfield", position.RelativeTo(center));
                }
                if (field is not null && fields.All(other => Math.Pow(other.Position.X - field.Position.X, 2) +
                    Math.Pow(other.Position.Z - field.Position.Z, 2) > 25))
                {
                    fields.Add(field);
                    remaining -= field.Points;
                    notices.Add($"{label} airfield: {field.Name}.");
                }
                else
                {
                    field = null;
                    notices.Add($"{label} airfield: no suitable owned land within 500 nm.");
                }
            }
            if (random.Next(100) >= Math.Clamp(selection.AircraftChance, 0, 100)) continue;
            var pool = SupportingAircraftCatalog.All.Where(option => nations.Contains(option.Nation, StringComparer.OrdinalIgnoreCase) &&
                option.Points <= remaining).ToArray();
            if (pool.Length == 0)
            {
                notices.Add($"{label} independent aircraft: no affordable aircraft for the available nations.");
                continue;
            }
            string[] roles = pool.Select(option => option.Role).Distinct().ToArray();
            // Pick a role before a type, so a large fighter catalog does not drown out reconnaissance and bombers.
            string role = roles[random.Next(roles.Length)];
            var choices = pool.Where(option => option.Role == role).GroupBy(option => option.Plan.AircraftType).ToArray();
            var variants = choices[random.Next(choices.Length)].ToArray();
            var selected = variants[random.Next(variants.Length)];
            int count = Math.Min(role == "Recon" ? 1 : remaining >= selected.Points * 4 ? 4 : 2, remaining / selected.Points);
            // Stored in side-relative rear coordinates; serialization anchors them behind the actual fleet.
            float x = rear * (150 + (float)random.NextDouble() * 70);
            float z = (float)random.NextDouble() * 60 - 30;
            for (int index = 0; index < count; index++)
            {
                MissionPosition position = new(x + rear * index * 0.8f, role == "Recon" ? 28000 : 22000, z + index * 0.8f);
                MissionWaypoint[] route =
                [
                    new(position with { X = x - rear * 20, Z = z + 18 }),
                    new(position with { X = rear * (role == "Strike" ? 50 : 70), Z = z + 24 }),
                    new(position with { X = rear * 65, Z = z - 24 }),
                ];
                aircraft.Add(new(side, selected.Nation, selected.Plan, role, position, route, selected.Points));
            }
            notices.Add($"{label} independent air support: {count} {role.ToLowerInvariant()} aircraft, starting in the rear.");
        }
        return new(fields, aircraft, notices, friendlyBudget, enemyBudget);
    }

    internal void WriteTo(MissionWriter mission, string playerSide, List<string> all, List<string> allied)
    {
        Dictionary<string, string> bases = [];
        foreach (SupportingAirfield field in Airfields)
        {
            string reference = Next(field.Side, "LandUnit", "LandUnits");
            bases[field.Side] = reference;
            mission.Section(reference);
            mission.Write("Type", "airfield_small_1");
            mission.Write("VariantReference", "Variant1");
            mission.Write("Nation", field.Nation);
            mission.Write("CustomAirGroup", true);
            // An empty field must not inherit a free default wing of dozens of aircraft.
            mission.Write("FlightDeck_AutocalculateAmmo", true);
            mission.Write("CrewSkill", "Trained");
            mission.Write("TaskForceModeIgnoreUnit", true);
            mission.WritePosition("RelativePositionInNM", field.Position);
            mission.Write("WeaponStatus", "Tight");
            mission.Write("RadarsActive", false);
            foreach (var group in Aircraft.Where(unit => unit.Side == field.Side).GroupBy(unit => unit.Plan.AircraftType))
                mission.Write(group.Key, string.Join('|', group.GroupBy(unit => unit.Plan.SquadronReference)
                    .Select(squadron => $"{squadron.Key},{(Deploy(field.Side) ? 0 : squadron.Count())}")));
        }
        foreach (SupportingAircraft aircraft in Aircraft)
        {
            if (!Deploy(aircraft.Side)) continue;
            string reference = Next(aircraft.Side, "Aircraft", "Aircraft");
            mission.Section(reference);
            mission.Write("Type", aircraft.Plan.AircraftType);
            mission.Write("SquadronReference", aircraft.Plan.SquadronReference);
            mission.Write("LoadoutVariant", aircraft.Plan.LoadoutVariant);
            mission.Write("Nation", aircraft.Nation);
            mission.Write("CrewSkill", "Trained");
            mission.Write("MissionType", aircraft.Plan.MissionType);
            mission.Write("TaskForceModeIgnoreUnit", true);
            mission.Write("CrimeanTomIndependentAircraft", true);
            MissionPosition position = BehindFleet(aircraft.Position, aircraft.Side);
            var waypoints = aircraft.Waypoints.Select(waypoint => waypoint with { Position = BehindFleet(waypoint.Position, aircraft.Side) }).ToArray();
            mission.WritePosition("RelativePositionInNM", position);
            mission.Write("Telegraph", 3);
            mission.Write("Heading", Math.Atan2(waypoints[0].Position.X - position.X, waypoints[0].Position.Z - position.Z) * 180 / Math.PI);
            if (bases.TryGetValue(aircraft.Side, out string home)) mission.Write("HomeBase", home);
            mission.WriteWaypoints(waypoints, loop: aircraft.Role != "Strike");
            ScenarioOpeningOrders.Defend(mission, reference);
            // Spread packages in time too; all start moving before their sensors/attack permission are released.
            int stagger = all.Count * 11 % 150;
            ScenarioOpeningOrders.Release(mission, reference, [reference], waypoints, false, 3,
                300 + stagger, 720 + stagger, loop: aircraft.Role != "Strike");
        }

        bool Deploy(string side) => side != playerSide || mission.Read("CrimeanTom", "FriendlyAircraftDeployed", "True") != "False";

        MissionPosition BehindFleet(MissionPosition position, string side)
        {
            MissionPosition Read(string force)
            {
                string raw = mission.Read(force + "Vessel1", "RelativePositionInNM",
                    mission.Read(force + "Submarine1", "RelativePositionInNM", "0,0,0"));
                string[] parts = raw.Split(',');
                return new(float.Parse(parts[0], CultureInfo.InvariantCulture), 0, float.Parse(parts[2], CultureInfo.InvariantCulture));
            }
            MissionPosition own = Read(side), enemy = Read(side == "Taskforce1" ? "Taskforce2" : "Taskforce1");
            double dx = own.X - enemy.X, dz = own.Z - enemy.Z, length = Math.Sqrt(dx * dx + dz * dz);
            if (length < 1) { dx = side == "Taskforce1" ? -1 : 1; dz = 0; length = 1; }
            double rear = Math.Abs(position.X);
            return position with { X = own.X + (float)((dx * rear - dz * position.Z) / length),
                Z = own.Z + (float)((dz * rear + dx * position.Z) / length) };
        }

        string Next(string side, string kind, string plural)
        {
            string key = $"NumberOf{side}{plural}";
            int index = int.Parse(mission.Read("Mission", key, "0"), CultureInfo.InvariantCulture) + 1;
            mission.Set("Mission", key, index);
            string reference = side + kind + index;
            all.Add(reference);
            if (side == playerSide) allied.Add(reference);
            return reference;
        }
    }
}

internal static class MilitaryAirports
{
    private static ShippingPort[]? all;
    internal static IReadOnlyList<ShippingPort> All => all ??= Load();
    private static ShippingPort[] Load()
    {
        List<ShippingPort> result = [];
        string directory = Path.Combine(UnityEngine.Application.streamingAssetsPath, "original", "campaigns");
        foreach (string file in Directory.EnumerateFiles(directory, "*airports*.ini").OrderBy(path => path, StringComparer.Ordinal))
        foreach (var (name, values) in ScenarioUnitCatalog.ReadSections(file))
        {
            if (!values.TryGetValue("Role", out string role) || !role.Contains("Military", StringComparison.OrdinalIgnoreCase) ||
                !values.TryGetValue("Nation", out string nation) || !values.TryGetValue("Location", out string location)) continue;
            string[] coordinates = location.Split(',');
            if (coordinates.Length >= 2 && double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) &&
                double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
                result.Add(new(name, nation, new(latitude, longitude)));
        }
        return result.ToArray();
    }
}
