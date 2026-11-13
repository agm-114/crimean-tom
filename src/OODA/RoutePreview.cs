using CrimeanTom.SeaPower;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal record RouteHazard(ChartPosition Center, double RadiusNm, string Reason);

internal static class RoutePreview
{
    private const double FeetPerUnity = 220.47266;

    internal static JObject Read(ObjectBase unit, GeoPosition[] route, RouteHazard[] hazards)
    {
        double speed = Math.Abs(unit.SpeedCommand.Value?.CommandSpeedInKnots ?? unit._velocityInKnots);
        bool air = unit is Aircraft or Helicopter;
        GeoPosition start = unit.getGeoPosition();
        double distance = 0, exposed = 0;
        JObject? firstUnsafe = null;
        bool terrainChecked = true;
        for (int leg = 0; leg < route.Length; leg++)
        {
            GeoPosition end = route[leg];
            ChartPosition from = Chart(start), to = Chart(end);
            double length = GeoPosition.HaversineDistanceNmi(start, end);
            var overlaps = hazards.Select(hazard => (hazard.Reason, Interval: AirDefenseRoutes.Exposure(from, to, hazard)))
                .Where(item => item.Interval.HasValue).Select(item => (item.Reason, item.Interval!.Value))
                .OrderBy(item => item.Value.Enter).ToArray();
            double lastExit = 0;
            foreach (var (_, (enter, exit)) in overlaps)
            {
                exposed += Math.Max(0, exit - Math.Max(enter, lastExit)) * length;
                lastExit = Math.Max(lastExit, exit);
            }
            (double Fraction, string Reason)? unsafePoint = overlaps.Length > 0 ? (overlaps[0].Value.Enter, overlaps[0].Reason) : null;
            try
            {
                if (TerrainObstacle(unit, start, end, length) is { } obstacle && (unsafePoint == null || obstacle.Fraction < unsafePoint.Value.Fraction))
                    unsafePoint = obstacle;
            }
            catch (IOException)
            {
                terrainChecked = false;
            }
            if (firstUnsafe == null && unsafePoint is { } hit)
            {
                ChartPosition point = Between(from, to, hit.Fraction);
                firstUnsafe = new() { ["leg"] = leg + 1, ["position"] = new JObject { ["latitude"] = point.Latitude,
                    ["longitude"] = point.Longitude }, ["reason"] = hit.Reason };
            }
            distance += length;
            start = end;
        }
        double? remainingRange = air && double.IsFinite(unit.RangeInKm.Value) ? unit.RangeInKm.Value / 1.852 - distance : null;
        double? remainingFuel = remainingRange is double range && unit._obp._maxFuel > 0 && unit.RangeInKm.Value > 0
            ? 100 * Math.Max(0, range) / (unit.RangeInKm.Value / 1.852) * unit._obp._currentFuelMass / unit._obp._maxFuel : null;
        return new JObject
        {
            ["unitId"] = PlayerPicture.Id(unit), ["distanceNm"] = Round(distance), ["speedKnots"] = Round(speed),
            ["etaSeconds"] = Round(TravelTime(distance, speed)), ["firstUnsafe"] = firstUnsafe, ["terrainChecked"] = terrainChecked,
            ["exposureNm"] = Round(exposed), ["exposureSeconds"] = Round(TravelTime(exposed, speed)),
            ["arrivalRangeNm"] = Round(remainingRange), ["arrivalFuelPercent"] = Round(remainingFuel),
            ["arrivalEnduranceSeconds"] = Round(remainingRange.HasValue ? TravelTime(Math.Max(0, remainingRange.Value), speed) : null)
        };
    }

    private static (double Fraction, string Reason)? TerrainObstacle(ObjectBase unit, GeoPosition start, GeoPosition end, double length)
    {
        ChartPosition from = Chart(start), to = Chart(end);
        double depthMeters = unit is Submarine ? Math.Max(20, -unit.DesiredAltitude.Value * FeetPerUnity * 0.3048 + 10) : 12;
        bool air = unit is Aircraft or Helicopter;
        int samples = Math.Max(1, (int)Math.Ceiling(length * 2));
        for (int sample = 0; sample <= samples; sample++)
        {
            double fraction = (double)sample / samples;
            double terrain = ScenarioTerrain.HeightMeters(Between(from, to, fraction));
            if (!air && terrain > -depthMeters) return (fraction, "Charted water depth below route clearance");
            double altitude = (start._height + (end._height - start._height) * fraction) * FeetPerUnity * 0.3048;
            if (air && terrain >= altitude) return (fraction, "Route altitude intersects charted terrain");
        }
        return null;
    }

    private static ChartPosition Chart(GeoPosition point) => new(point._latitude, point._longitude);
    internal static ChartPosition Between(ChartPosition start, ChartPosition end, double fraction) => new(
        start.Latitude + (end.Latitude - start.Latitude) * fraction,
        Math.IEEERemainder(start.Longitude + Math.IEEERemainder(end.Longitude - start.Longitude, 360) * fraction, 360));
    private static double? TravelTime(double distance, double speed) => distance == 0 ? 0 : speed > 0.5 ? distance / speed * 3600 : null;
    private static double? Round(double? value) => value is double number && double.IsFinite(number) ? Math.Round(number, 1) : null;
}
