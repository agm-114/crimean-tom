using System.Globalization;
using System.Text.RegularExpressions;

namespace CrimeanTom.SeaPower;

internal static class NavalWeaponReach
{
    private static readonly Dictionary<(string Type, bool SubmarineTarget), float> cache = [];
    internal static float Read(string type, bool submarineTarget = false)
    {
        var key = (type.ToLowerInvariant(), submarineTarget);
        if (cache.TryGetValue(key, out float reach)) return reach;
        string root = Path.Combine(UnityEngine.Application.streamingAssetsPath, "original");
        string? path = new[] { "vessels", "submarines", "aircraft", "land_units" }
            .Select(folder => Path.Combine(root, folder, type + ".ini")).FirstOrDefault(File.Exists);
        if (path is null) return float.PositiveInfinity;
        var values = ScenarioUnitCatalog.ReadSections(path).SelectMany(section => section.Values).ToArray();
        string[] ammunition = values.Where(pair => Regex.IsMatch(pair.Key, @"^Ammunition\d*$", RegexOptions.IgnoreCase))
            .SelectMany(pair => ResolveAmmunition(pair.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        reach = 0;
        foreach (string ammo in ammunition)
        {
            string ammoPath = Path.Combine(root, "ammunition", ammo + ".ini");
            // Unknown references remain conservative; dated ammunition uses its greatest possible reach.
            if (!File.Exists(ammoPath)) { reach = float.PositiveInfinity; break; }
            var data = ScenarioUnitCatalog.ReadSections(ammoPath).SelectMany(section => section.Values).ToArray();
            // Countermeasures name the threats they defend against, not targets they can attack.
            if (data.Any(pair => pair.Key == "Type" && pair.Value is "Noisemaker" or "Chaff" or "Flare" or "Decoy" or "MOSS")) continue;
            if (!data.Any(pair => pair.Key.EndsWith("TargetType", StringComparison.OrdinalIgnoreCase) &&
                pair.Value.Contains(submarineTarget ? "ASW" : "ASuW", StringComparison.OrdinalIgnoreCase))) continue;
            float maximum = data.Where(pair => pair.Key is "MaxLaunchRange" or "MaxRange" or "SpeedSettingRanges")
                .SelectMany(pair => pair.Value.Split(',').Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float range)
                    ? range / (pair.Key == "MaxRange" ? 1852 : 1) : float.PositiveInfinity)).DefaultIfEmpty(float.PositiveInfinity).Max();
            reach = Math.Max(reach, maximum);
        }
        cache[key] = reach;
        return reach;

        IEnumerable<string> ResolveAmmunition(string reference)
        {
            if (!reference.StartsWith("DateBased_", StringComparison.OrdinalIgnoreCase)) return [reference];
            string? variants = values.FirstOrDefault(pair => pair.Key.Equals(reference, StringComparison.OrdinalIgnoreCase)).Value;
            return string.IsNullOrWhiteSpace(variants) ? [reference] :
                variants.Split('|').Select(entry => entry.Split(',').Last().Trim());
        }
    }
}

internal record NavalWeaponEnvelope(string UnitType, float X, float Z, bool IsSubmarine = false)
{
    internal static NavalWeaponEnvelope From(DeployedFleetUnit unit) => new(unit.Ship.UnitType, unit.X, unit.Z, unit.Ship.IsSubmarine);
    internal bool Overlaps(NavalWeaponEnvelope other) =>
        Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Z - other.Z, 2)) <=
        Math.Max(NavalWeaponReach.Read(UnitType, other.IsSubmarine), NavalWeaponReach.Read(other.UnitType, IsSubmarine));
}

internal static partial class ScenarioDebugGenerator
{
    internal static (FleetClashMatch Match, double Allowance) ApplyRouteChaseAllowance(FleetClashMatch match,
        int seed, FleetClashSize size, RouteOperationVariant variant, RouteEncounterKind kind, bool persistentAttackers = false,
        FleetClashGenerationOptions? generationOptions = null)
    {
        FleetClashGenerationOptions options = generationOptions ?? new() { SeaState = 1 };
        RouteOperationPlan Plan(FleetClashMatch force)
        {
            string playerSide = options.RoutePerspective == RouteOperationPerspective.Interdict ? "Taskforce2" : "Taskforce1";
            int controlled = GetPlayerControlledCount(force, playerSide, options.PlayerControlPercentage);
            force = ReorderPlayerForce(force, playerSide, controlled, options.ControlPriority);
            Random random = new(seed);
            ScenarioParameters parameters = ScenarioParameters.Create(random, seed, size, options, force);
            return CreateRouteOperationPlan(random, force, parameters, size, playerSide, controlled, variant, kind);
        }
        RouteOperationPlan original = Plan(match);
        double allowance = ChaseAllowance(original);
        if (allowance < 0.01) return (match, 0);
        try
        {
            string scaledOpponent = persistentAttackers ? "Taskforce2" : "Taskforce1";
            double multiplier = persistentAttackers ? 1 - allowance : 1 / (1 - allowance);
            FleetClashMatch candidate = ApplyEnemyMultiplier(match, new Random(seed ^ 0x43484153), size, scaledOpponent, multiplier, true);
            RouteOperationPlan revised = Plan(candidate);
            double actual = persistentAttackers ? 1 - (double)candidate.First.PointCost / match.First.PointCost :
                1 - (double)match.Second.PointCost / candidate.Second.PointCost;
            if (actual <= 0 || actual > Math.Min(allowance, ChaseAllowance(revised)) + 0.001) return (match, 0);
            return (candidate, actual);
        }
        catch (InvalidOperationException) { return (match, 0); }
    }

    private static double ChaseAllowance(RouteOperationPlan route)
    {
        double weighted = 0;
        foreach (RouteAttackGroup group in route.AttackGroups ?? [])
        {
            if (group.Mode != RouteAttackMode.Pursuit || group.InitialRangeNm <= 0) continue;
            var formation = route.SecondDeployment.Formations.First(item => item.Name == group.Name);
            var ships = route.SecondDeployment.Units.Where(unit => formation.References.Contains(unit.Reference)).Select(unit => unit.Ship).ToArray();
            float reach = ships.Select(ship => NavalWeaponReach.Read(ship.UnitType)).DefaultIfEmpty(float.PositiveInfinity).Max();
            double outsideReach = Math.Clamp((group.InitialRangeNm - reach) / group.InitialRangeNm, 0, 1);
            double delay = Math.Clamp(group.CatchUpHours / Math.Max(0.1, route.Voyage!.LengthNm / group.ConvoySpeedKnots * 0.55), 0, 1);
            weighted += ships.Sum(ship => ship.PointCost) * 0.20 * outsideReach * delay;
        }
        return weighted / Math.Max(1, route.SecondDeployment.Units.Sum(unit => unit.Ship.PointCost));
    }
}
