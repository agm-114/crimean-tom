using CrimeanTom.SeaPower;
using SeaPower;

namespace OODA;

internal static class ReinforcementPlacement
{
    private const double UnityUnitsPerMeter = 0.0148809375;
    private const double MinimumStandoffNm = 28;
    private static readonly ChartPosition[] WesternShore =
        [new(36.62, -6.35), new(37.14, -6.83), new(37.20, -7.32), new(36.19, -5.92)];
    private static readonly ChartPosition[] EasternShore =
        [new(35.21, -4.67), new(35.15, -4.37), new(35.25, -3.93), new(35.27, -2.94)];

    internal record Placement(GeoPosition Position, GeoPosition Entry, GeoPosition? Destination,
        float Heading, double NearestHostileNm);
    private record Threat(GeoPosition Position, double Standoff);

    internal static Placement? Find(GeoPosition center, IReadOnlyList<ChartPosition> occupied,
        IEnumerable<ObjectBase> units, ReinforcementCatalog.Model model, Taskforce side, int direction, Random random)
    {
        ReinforcementKind kind = model.Kind;
        bool maritime = kind is ReinforcementKind.Surface or ReinforcementKind.Submarine or ReinforcementKind.Carrier;
        ChartPosition origin = Chart(center);
        ChartPosition approach = kind == ReinforcementKind.Carrier
            ? origin.Offset(new(direction * 37, 0, direction < 0 ? -8 : 2))
            : origin.Offset(new(direction * 17, 0, direction < 0 ? -2 : 1.5f));
        Threat[] threats = units.Where(unit => unit != null && !unit.IsDestroyed && !unit.IsDespawned
                && side.RelationshipTo(unit._taskforce) == RelationsState.Hostile)
            .Select(unit => new Threat(unit._geoPosition, Math.Max(MinimumStandoffNm * Constants.MILES_TO_UNITY,
                RangeAgainst(unit, kind) + 5 * Constants.MILES_TO_UNITY))).ToArray();

        for (int attempt = 0; attempt < 96; attempt++)
        {
            ChartPosition point = model.Port ? SampleCoast(direction, random) : Sample(origin, kind, direction, random);
            ChartPosition? seaward = null;
            if (model.Port)
            {
                if (FindShore(point) is not { } shore) continue;
                (point, seaward) = shore;
            }
            if (occupied.Any(other => other.DistanceTo(point) < 0.8)) continue;
            int depth = kind == ReinforcementKind.Submarine ? 80 : 20;
            if (maritime && (!ScenarioTerrain.HasSpawnClearance(point, depth)
                || !ScenarioTerrain.IsNavigable(point, approach, depth)
                || (kind != ReinforcementKind.Carrier && !ScenarioTerrain.IsNavigable(approach, origin, depth)))) continue;

            double height = kind switch
            {
                ReinforcementKind.Land => HeightAt(point),
                ReinforcementKind.Submarine => -30 * UnityUnitsPerMeter,
                ReinforcementKind.Aircraft => (1800 + random.Next(1200)) * UnityUnitsPerMeter,
                ReinforcementKind.Helicopter => (350 + random.Next(250)) * UnityUnitsPerMeter,
                _ => 0,
            };
            if (kind == ReinforcementKind.Land && !model.Port && !LevelGround(point, height)) continue;
            GeoPosition position = Geo(point, height);
            if (threats.Any(threat => position.GetDistance(threat.Position) < threat.Standoff)) continue;

            double nearest = threats.Select(threat => position.GetDistance(threat.Position) * Constants.UNITY_TO_MILES)
                .DefaultIfEmpty(double.PositiveInfinity).Min();
            MissionPosition bearing = (seaward ?? approach).RelativeTo(point);
            return new(position, Geo(approach, height), kind == ReinforcementKind.Carrier ? (GeoPosition?)null : Geo(origin, height),
                (float)(Math.Atan2(bearing.X, bearing.Z) * 180 / Math.PI), nearest);
        }
        return null;
    }

    private static ChartPosition Sample(ChartPosition origin, ReinforcementKind kind, int direction, Random random)
    {
        if (kind == ReinforcementKind.Land)
            return origin.Offset(new(direction * (float)(39 + random.NextDouble() * 63), 0,
                -direction * (float)(15 + random.NextDouble() * 51)));

        float distance = (float)(kind switch
        {
            ReinforcementKind.Aircraft => 75 + random.NextDouble() * 70,
            ReinforcementKind.Helicopter => 50 + random.NextDouble() * 60,
            _ => 53 + random.NextDouble() * 79,
        });
        float lane = direction < 0 ? -0.24f * distance : 3;
        return origin.Offset(new(direction * distance, 0, lane + (float)(random.NextDouble() - 0.5) * 14));
    }

    private static ChartPosition SampleCoast(int direction, Random random)
    {
        ChartPosition[] coast = direction < 0 ? WesternShore : EasternShore;
        return coast[random.Next(coast.Length)].Offset(new(
            (float)(random.NextDouble() - 0.5) * 5, 0, (float)(random.NextDouble() - 0.5) * 5));
    }

    private static (ChartPosition Land, ChartPosition Water)? FindShore(ChartPosition inland)
    {
        if (HeightAt(inland) <= 0) return null;
        for (int direction = 0; direction < 8; direction++)
        {
            double angle = direction * Math.PI / 4;
            ChartPosition water = inland.Offset(new((float)(Math.Cos(angle) * 3), 0, (float)(Math.Sin(angle) * 3)));
            if (HeightAt(water) >= -2 * UnityUnitsPerMeter) continue;

            ChartPosition landEdge = inland, waterEdge = water;
            for (int step = 0; step < 12; step++)
            {
                ChartPosition midpoint = landEdge.Towards(waterEdge, 0.5);
                if (HeightAt(midpoint) > 0) landEdge = midpoint;
                else waterEdge = midpoint;
            }
            if (HeightAt(landEdge) <= 5 * UnityUnitsPerMeter) return (landEdge, water);
        }
        return null;
    }

    private static bool LevelGround(ChartPosition point, double height)
    {
        if (height < 3 * UnityUnitsPerMeter) return false;
        foreach (float x in new[] { -0.06f, 0.06f })
        foreach (float z in new[] { -0.06f, 0.06f })
            if (Math.Abs(HeightAt(point.Offset(new(x, 0, z))) - height) > 8 * UnityUnitsPerMeter) return false;
        return true;
    }

    private static float RangeAgainst(ObjectBase unit, ReinforcementKind kind)
    {
        Ammunition.Target target = kind switch
        {
            ReinforcementKind.Aircraft or ReinforcementKind.Helicopter => Ammunition.Target.AAW,
            ReinforcementKind.Submarine => Ammunition.Target.ASW,
            _ => Ammunition.Target.ASuW,
        };
        float range = 0;
        foreach (var (name, amount) in unit.AmmunitionAmountDictionary)
        {
            if (amount < 1 || unit.getAmmunitionByName(name)?._ap is not { } ammo) continue;
            bool matches = kind == ReinforcementKind.Land ? ammo._landAttack.Count > 0
                : ammo._targetType == target || ammo._secondaryTargetType == target;
            if (matches) range = Math.Max(range, ammo._launchRangesInUnity.y);
        }
        return range;
    }

    internal static ChartPosition Chart(GeoPosition position) => new(position._latitude, position._longitude);
    private static GeoPosition Geo(ChartPosition chart, double height) =>
        new() { _latitude = chart.Latitude, _longitude = chart.Longitude, _height = height };
    private static float HeightAt(ChartPosition point) => TerrainUtils.GetHeightAt(Geo(point, 0));
}
