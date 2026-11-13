using CrimeanTom.SeaPower;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal class AirDefenseRoutes
{
    internal record Warning(string UnitId, string UnitName, string TargetId, string TargetName, double RangeNm,
        double EntryDistanceNm, ChartPosition Entry, double? SecondsToEntry, string Basis, bool Suppressed, bool Emitting)
    {
        private bool Inside => EntryDistanceNm == 0;
        internal bool Imminent => Inside || SecondsToEntry is <= 120;
        private string Assessment => Suppressed ? "Likely suppressed, residual risk remains"
            : Emitting ? "Radar emitting" : "Air-defense capability unconfirmed";
        internal string Detail => $"{UnitName} · {(Inside ? "inside" : "route crosses")} {TargetName}'s estimated {RangeNm:0.#} nm air-defense envelope"
            + (Inside ? "." : $" in {EntryDistanceNm:0.#} nm.")
            + (Basis == "Route" ? "" : $" {Basis} estimate.") + $" {Assessment}.";

        internal JObject Describe() => new()
        {
            ["unitId"] = UnitId, ["unitName"] = UnitName, ["targetId"] = TargetId, ["targetName"] = TargetName,
            ["airDefenseRangeNm"] = RangeNm, ["distanceToEntryNm"] = Math.Round(EntryDistanceNm, 2), ["alreadyInside"] = Inside,
            ["entry"] = new JObject { ["latitude"] = Entry.Latitude, ["longitude"] = Entry.Longitude },
            ["secondsToEntry"] = SecondsToEntry.HasValue ? Math.Round(SecondsToEntry.Value) : null,
            ["directAttackEstimate"] = Basis == "Direct attack approach", ["basis"] = Basis, ["assessment"] = Assessment, ["warning"] = Detail
        };
    }

    private record Defense(string Id, string Name, ChartPosition Center, double Range, bool Suppressed, bool Emitting);
    private readonly Dictionary<(string Folder, string Class), double?> ranges = [];
    private Dictionary<(string Unit, string Target), Warning> active = [];
    internal bool HasImminentWarning => active.Values.Any(warning => warning.Imminent && !warning.Suppressed);

    internal Warning[] Poll(Taskforce side, AirDefenseAssessment assessment)
    {
        var next = Crossings(PlayerFlightDeck.FlightRoutes(side), side, assessment).ToDictionary(item => (item.UnitId, item.TargetId));
        Warning[] added = next.Where(item => !active.TryGetValue(item.Key, out Warning? prior)
                || prior.Suppressed && !item.Value.Suppressed || !prior.Imminent && item.Value.Imminent)
            .Select(item => item.Value).OrderBy(warning => warning.EntryDistanceNm).ToArray();
        active = next;
        return added;
    }

    internal JArray HeldTransportCrossings(Taskforce side, AirDefenseAssessment assessment) => new(
        Crossings(PlayerFlightDeck.HeldTransportRoutes(side), side, assessment).OrderBy(warning => warning.EntryDistanceNm)
            .Select(warning => warning.Describe()));

    internal bool Intersects(Taskforce side, AirDefenseAssessment assessment, GeoPosition origin, GeoPosition[] route, bool repeat = true)
    {
        ChartPosition[] points = repeat ? [Chart(origin), .. route.Select(Chart), Chart(route[0])] : [Chart(origin), .. route.Select(Chart)];
        return Defenses(side, assessment).Any(defense => !defense.Suppressed && FirstEntry(defense.Center, defense.Range, points) != null);
    }

    internal bool TransportRouteBlocked(Taskforce side, AirDefenseAssessment assessment, string id) =>
        Crossings(PlayerFlightDeck.HeldTransportRoutes(side).Where(flight => flight.Id == id), side, assessment)
            .Any(warning => !warning.Suppressed);

    internal RouteHazard[] Hazards(Taskforce side, AirDefenseAssessment assessment) => Defenses(side, assessment)
        .Where(defense => !defense.Suppressed)
        .Select(defense => new RouteHazard(defense.Center, defense.Range, $"{defense.Name} air-defense envelope")).ToArray();

    internal bool CanWithdraw(Taskforce side, AirDefenseAssessment assessment, GeoPosition origin, GeoPosition[] route)
    {
        ChartPosition[] points = [Chart(origin), .. route.Select(Chart)];
        return Defenses(side, assessment).Where(defense => !defense.Suppressed).All(defense =>
        {
            if (FirstEntry(defense.Center, defense.Range, points) is not { } entry) return true;
            if (entry.Distance > 0 || !MovingAway(points[0], points[1], defense)) return false;
            return FirstEntry(defense.Center, defense.Range, points[1..]) == null;
        });
    }

    internal bool GroundAttackPathClear(Taskforce side, AirDefenseAssessment assessment, GeoPosition origin, GeoPosition fix, double releaseNm)
    {
        double range = GeoPosition.HaversineDistanceNmi(origin, fix);
        ChartPosition start = Chart(origin), target = Chart(fix);
        ChartPosition release = range <= releaseNm ? start : start.Towards(target, 1 - releaseNm / range);
        return Defenses(side, assessment).All(defense => defense.Suppressed
            || FirstEntry(defense.Center, defense.Range + 0.5, [start, release]) == null);
    }

    internal GeoPosition[] EscapeRoute(Taskforce side, AirDefenseAssessment assessment, Helicopter helo)
    {
        GeoPosition origin = helo.getGeoPosition();
        Defense[] defenses = Defenses(side, assessment).Where(defense => !defense.Suppressed).ToArray();
        float clearance = Utils.heightInMeterToUnity(30);
        foreach (float offset in new[] { 180f, 135f, -135f, 90f, -90f, 45f, -45f, 0f })
        {
            GeoPosition hold = origin.GeoOffsetByDistanceOnHeading(10, helo.Heading.Value + offset);
            GeoPosition turn = origin.GeoOffsetByDistanceOnHeading(6, helo.Heading.Value + offset);
            if (defenses.Any(defense => !ClearEscape(defense, origin, turn, hold))) continue;
            float terrain = Enumerable.Range(1, 20).Max(i => TerrainUtils.GetRealHeightInMetersAt(
                origin.GeoOffsetByDistanceOnHeading(i / 2f, helo.Heading.Value + offset)));
            if (Utils.heightInMeterToUnity(terrain) + clearance > origin._height) continue;
            turn._height = hold._height = origin._height;
            return [turn, hold];
        }
        return [];
    }

    private static bool ClearEscape(Defense defense, GeoPosition origin, GeoPosition turn, GeoPosition hold)
    {
        ChartPosition start = Chart(origin), end = Chart(turn);
        if (FirstEntry(defense.Center, defense.Range + 0.5, [end, Chart(hold)]) != null) return false;
        if (FirstEntry(defense.Center, defense.Range, [start]) == null)
            return FirstEntry(defense.Center, defense.Range, [start, end]) == null;
        return MovingAway(start, end, defense);
    }

    private static bool MovingAway(ChartPosition start, ChartPosition end, Defense defense)
    {
        double x = LongitudeDelta(start, defense.Center) * Math.Cos(defense.Center.Latitude * Math.PI / 180);
        double dx = LongitudeDelta(end, start) * Math.Cos(defense.Center.Latitude * Math.PI / 180);
        return x * dx + (start.Latitude - defense.Center.Latitude) * (end.Latitude - start.Latitude) >= 0;
    }

    private IEnumerable<Warning> Crossings(IEnumerable<PlayerFlightDeck.FlightRoute> flights, Taskforce side, AirDefenseAssessment assessment)
    {
        Defense[]? defenses = null;
        foreach (PlayerFlightDeck.FlightRoute flight in flights)
        {
            defenses ??= Defenses(side, assessment).ToArray();
            ChartPosition[] points = flight.Points.Select(Chart).ToArray();
            ChartPosition[]? heading = flight.ProjectedPosition is { } projection ? [points[0], Chart(projection)] : null;
            foreach (Defense defense in defenses)
            {
                var entry = FirstEntry(defense.Center, defense.Range, points);
                string basis = flight.AttackApproach ? "Direct attack approach" : "Route";
                if (heading != null && FirstEntry(defense.Center, defense.Range, heading) is { } projected
                    && (entry == null || projected.Distance < entry.Value.Distance))
                {
                    entry = projected;
                    basis = "Current heading";
                }
                if (entry is not { } crossing) continue;
                yield return new(flight.Id, flight.Name, defense.Id, defense.Name, defense.Range, crossing.Distance, crossing.Point,
                    flight.SpeedKnots >= 30 ? crossing.Distance / flight.SpeedKnots * 3600 : null, basis, defense.Suppressed, defense.Emitting);
            }
        }
    }

    private IEnumerable<Defense> Defenses(Taskforce side, AirDefenseAssessment assessment)
    {
        foreach (Vehicle track in side.PlottingTable?.Vehicles.AsEnumerable() ?? [])
        {
            if (!PlayerPicture.IsLive(track) || track.CurrentRelationship() != RelationsState.Hostile || !track.Identified.Value
                || track.PositionEstimate?.Item1 is not { } fix || track.Class?.Value is not { Length: > 0 } classId
                || track.BaseObject is not (Vessel or LandUnit)) continue;
            string folder = track.BaseObject is LandUnit ? "land_units" : "vessels";
            if (!ranges.TryGetValue((folder, classId), out double? range))
                ranges[(folder, classId)] = range = SurfaceWeaponRange.ForClass(classId, "AAW", folder);
            if (range is not > 0) continue;
            string targetId = PlayerPicture.Id(track);
            bool emitting = PlayerPicture.ObservesEmission(track);
            yield return new(targetId, PlayerPicture.ContactLabel(track), Chart(fix), range.Value,
                !emitting && assessment.LikelySuppressed(targetId), emitting);
        }
    }

    private static ChartPosition Chart(GeoPosition point) => new(point._latitude, point._longitude);

    // Solve each segment against the circle, including crossings with both endpoints outside.
    internal static (double Distance, ChartPosition Point)? FirstEntry(ChartPosition center, double radius, ChartPosition[] points)
    {
        if (points.Length == 0) return null;
        double longitudeScale = 60 * Math.Cos(center.Latitude * Math.PI / 180);
        RouteHazard hazard = new(center, radius, "");
        if (Exposure(points[0], points[0], hazard) != null) return (0, points[0]);
        double traveled = 0;
        for (int i = 1; i < points.Length; i++)
        {
            ChartPosition start = points[i - 1];
            ChartPosition end = points[i];
            double longitudeDelta = LongitudeDelta(end, start);
            double dx = longitudeDelta * longitudeScale;
            double dy = (end.Latitude - start.Latitude) * 60;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (Exposure(start, end, hazard) is { } overlap)
                return (traveled + overlap.Enter * length, RoutePreview.Between(start, end, overlap.Enter));
            traveled += length;
        }
        return null;
    }

    private static double LongitudeDelta(ChartPosition end, ChartPosition start) => Math.IEEERemainder(end.Longitude - start.Longitude, 360);

    internal static (double Enter, double Exit)? Exposure(ChartPosition start, ChartPosition end, RouteHazard hazard)
    {
        double scale = 60 * Math.Cos(hazard.Center.Latitude * Math.PI / 180);
        double x = LongitudeDelta(start, hazard.Center) * scale, y = (start.Latitude - hazard.Center.Latitude) * 60;
        double dx = LongitudeDelta(end, start) * scale, dy = (end.Latitude - start.Latitude) * 60;
        double length = dx * dx + dy * dy, distance = x * x + y * y - hazard.RadiusNm * hazard.RadiusNm;
        if (length <= 0) return distance <= 0 ? (0, 1) : null;
        double projection = x * dx + y * dy, discriminant = projection * projection - length * distance;
        if (discriminant < 0) return null;
        double root = Math.Sqrt(discriminant);
        double enter = Math.Max(0, (-projection - root) / length), exit = Math.Min(1, (-projection + root) / length);
        return enter <= exit ? (enter, exit) : null;
    }

    internal JArray Read() => new(active.Values.OrderBy(warning => warning.EntryDistanceNm).Select(warning => warning.Describe()));

    internal string Summary() => active.Count == 0 ? "No current route warnings"
        : string.Join("\n", active.Values.OrderBy(warning => warning.EntryDistanceNm).Select(warning => warning.Detail));

    internal void Reset()
    {
        active.Clear();
        ranges.Clear();
    }
}
