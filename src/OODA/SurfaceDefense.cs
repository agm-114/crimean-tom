using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using CrimeanTom.SeaPower;
using SeaPower;
using SeaPower.GameBridge;
using SeapowerUI;

namespace OODA;

internal class SurfaceDefense
{
    private const double WatchRangeNm = 20;
    private const double ForecastSeconds = 120;
    private const double SampleIntervalSeconds = 10;
    private static readonly Regex AttackLanguage = new(
        @"\b(?:under (?:attack|fire)|taking fire|opened fire|fired (?:on|at|upon)|fir(?:e|ing) (?:on|at|upon|from)|fire (?:was |is )?reported from|(?:is|are) attacking|being attacked|shooting (?:at|on))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ProtectionLanguage = new(@"\b(?:assist(?:ance)?|escort|protect|defend)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private record CloseThreat(string TargetId, string VictimId, double RangeNm, double? WeaponRangeNm,
        double? ClosingKnots, double? SecondsToWeaponRange)
    {
        internal bool Immediate => SecondsToWeaponRange is <= ForecastSeconds;
    }
    private readonly record struct RangeSample(double Time, double RangeNm, double? ClosingKnots);
    private HashSet<(string Attacker, string Victim)> gunfire = [];
    private HashSet<string> liveAttackers = [];
    private readonly HashSet<(string Target, string Victim, bool Inside)> reviewedThreats = [];
    private readonly Dictionary<(string Target, string Victim), RangeSample> rangeSamples = [];
    private readonly Dictionary<string, double?> weaponRanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double?> airDefenseRanges = new(StringComparer.OrdinalIgnoreCase);
    private CloseThreat[] nearbyThreats = [];
    private HashSet<string> reportContacts = [];
    private readonly Dictionary<string, double> nextReportReview = [];
    private readonly HashSet<string> reviewedGunFailures = [];
    private double lastRequested;
    internal bool Pending { get; set; }
    internal bool ActiveGunfire { get; private set; }
    internal bool HasNearbyThreat { get; private set; }
    internal PlayerCombat.Gunfire[] NewFriendlyAttacks { get; private set; } = [];

    internal void Poll(Taskforce side, bool canReview)
    {
        while (PlayerCombat.TryTakeLaunchReport(out string launch)) UnityEngine.Debug.Log($"[OODA] Weapon fired | {launch}");
        bool hasReport = Reports().Any();
        PlayerCombat.Gunfire[] recent = PlayerCombat.Recent().Where(item => item.FriendlyVictim || hasReport).ToArray();
        double now = GameTime.missionElapsedTime;
        ActiveGunfire = recent.Any(item => now - item.LastSeen <= 12);
        NewFriendlyAttacks = recent.Where(item => item.FriendlyVictim && now - item.LastSeen <= 12
            && !gunfire.Contains((item.AttackerId, item.VictimId))).ToArray();
        HashSet<(string, string)> observed = recent.Select(item => (item.AttackerId, item.VictimId)).ToHashSet();
        if (observed.Except(gunfire).Any()) Pending = true;
        HashSet<string> currentAttackers = recent.Where(item => now - item.LastSeen <= 45
            && PlayerPicture.LiveContact(side, item.AttackerId) != null).Select(item => item.AttackerId).ToHashSet();
        if (currentAttackers.Except(liveAttackers).Any()) Pending = true;
        if (canReview && now - lastRequested >= 30 && recent.Any(item => item.LastSeen > lastRequested
            && now - item.LastSeen <= 12 && currentAttackers.Contains(item.AttackerId))) Pending = true;
        liveAttackers = currentAttackers;
        gunfire = observed;

        HashSet<string> contacts = hasReport && side.PlottingTable?.Vehicles is { } tracks
            ? tracks.Where(IsReportCandidate).Select(PlayerPicture.Id).ToHashSet() : [];
        double wallTime = UnityEngine.Time.realtimeSinceStartupAsDouble;
        if (canReview)
            foreach (string id in contacts.Intersect(reportContacts))
            {
                if (nextReportReview.GetValueOrDefault(id) > wallTime) continue;
                nextReportReview[id] = wallTime + 15;
                Pending = true;
            }
        reportContacts = contacts;

        nearbyThreats = NearbyThreats(side, now).ToArray();
        HasNearbyThreat = nearbyThreats.Any(threat => threat.Immediate);
        foreach (CloseThreat threat in nearbyThreats.Where(threat => threat.Immediate))
        {
            bool inside = threat.SecondsToWeaponRange == 0;
            if (inside) reviewedThreats.Add((threat.TargetId, threat.VictimId, false));
            Pending |= reviewedThreats.Add((threat.TargetId, threat.VictimId, inside));
        }
        foreach (var pair in rangeSamples.Where(sample => now - sample.Value.Time > ForecastSeconds).Select(sample => sample.Key).ToArray())
            rangeSamples.Remove(pair);
    }

    internal void OnMessages(IEnumerable<MissionCommunications.Message> updates)
    {
        if (!updates.Any(message => AttackLanguage.IsMatch(message.Body))) return;
        nextReportReview.Clear();
        Pending = true;
    }

    internal void MarkRequested()
    {
        Pending = false;
        lastRequested = GameTime.missionElapsedTime;
    }

    internal void ReviewGunFailure(string targetId) => Pending |= reviewedGunFailures.Add(targetId);

    internal void Reset()
    {
        Pending = ActiveGunfire = HasNearbyThreat = false;
        NewFriendlyAttacks = [];
        lastRequested = 0;
        gunfire.Clear();
        liveAttackers.Clear();
        reviewedThreats.Clear();
        rangeSamples.Clear();
        weaponRanges.Clear();
        airDefenseRanges.Clear();
        nearbyThreats = [];
        reportContacts.Clear();
        nextReportReview.Clear();
        reviewedGunFailures.Clear();
    }

    internal void Describe(Taskforce side, JObject view)
    {
        view["ownWeaponFire"] = PlayerCombat.OwnFireSnapshot();
        view["ownMissileTargetsInFlight"] = new JArray(PlayerCombat.MissileTargetsInFlight()
            .Select(target => target.GetMapVehicle(side)).OfType<Vehicle>().Where(track => track.IsVisible()).Select(PlayerPicture.Id));
        view["nearbySurfaceThreats"] = new JArray(nearbyThreats.OrderBy(threat => threat.SecondsToWeaponRange ?? double.PositiveInfinity)
            .ThenBy(threat => threat.RangeNm).Take(32).Select(threat => new JObject
            {
                ["targetId"] = threat.TargetId, ["victimId"] = threat.VictimId, ["rangeNm"] = Math.Round(threat.RangeNm, 1),
                ["weaponRangeNm"] = Rounded(threat.WeaponRangeNm), ["closingKnots"] = Rounded(threat.ClosingKnots),
                ["secondsToWeaponRange"] = Rounded(threat.SecondsToWeaponRange), ["immediate"] = threat.Immediate
            }));
        view["attackReports"] = new JArray(Reports().Select(report => new JObject
        {
            ["messageIndex"] = report.Index, ["elapsedSeconds"] = report.Message.Time,
            ["title"] = report.Message.Title, ["from"] = report.Message.From, ["body"] = report.Message.Body
        }));
        view["protectedUnits"] = new JArray(OwnVessels(side).Where(ship => !PlayerPicture.CanCommand(ship, side)).Select(ship => new JObject
        {
            ["id"] = PlayerPicture.Id(ship), ["name"] = ship.DisplayNameShort, ["position"] = PlayerPicture.Point(ship.getGeoPosition())
        }).Concat(ProtectedContacts(side).Select(track => new JObject
        {
            ["id"] = PlayerPicture.Id(track), ["name"] = PlayerPicture.ContactLabel(track),
            ["position"] = PlayerPicture.Point(track.PositionEstimate!.Value.Item1)
        })));
    }

    internal JArray CoastalThreats(Taskforce side, JArray airborne)
    {
        var flightRoutes = airborne.Where(flight => flight["leaderId"]?.Type == JTokenType.Null)
            .Select(flight => (Id: (string)flight["id"]!, Points: new[] { flight["position"] }
                .Concat((flight["holding"]?.Value<bool>() == true ? flight["heldRoute"] : flight["route"])?.Children() ?? [])
                .OfType<JObject>().Select(point => new ChartPosition((double)point["latitude"]!, (double)point["longitude"]!)).ToArray()))
            .Where(route => route.Points.Length > 1).ToArray();
        HashSet<ObjectBase> engaged = new(PlayerCombat.MissileTargetsInFlight(includeAircraft: true));
        foreach (ObjectBase unit in PlayerPicture.Units(side))
        {
            engaged.UnionWith(unit._currentEngageTasks.Select(task => task._targetObject).Where(target => target));
            if (unit._ai?._objectToDestroy is { } target && target) engaged.Add(target);
        }
        var tracks = (side.PlottingTable?.Vehicles.AsEnumerable() ?? []).Where(track => PlayerPicture.IsLive(track)
            && track.CurrentRelationship() == RelationsState.Hostile && track.PositionEstimate.HasValue
            && (track.BaseObject is Vessel || track.GetMapType() is MapVisualType.Radar or MapVisualType.SAM or MapVisualType.AAA));
        return new JArray(tracks.Select(track =>
        {
            GeoPosition estimate = track.PositionEstimate!.Value.Item1;
            ChartPosition position = new(estimate._latitude, estimate._longitude);
            return new JObject
            {
                ["targetId"] = PlayerPicture.Id(track), ["label"] = PlayerPicture.ContactLabel(track),
                ["category"] = track.GetMapType().ToString(),
                ["estimatedPosition"] = PlayerPicture.Point(estimate), ["emitting"] = PlayerPicture.ObservesEmission(track),
                ["weaponRangeNm"] = track.BaseObject is Vessel ? Rounded(WeaponRange(track)) : null,
                ["airDefenseRangeNm"] = track.BaseObject is Vessel ? Rounded(ShipAirDefenseRange(track)) : null,
                ["engaged"] = engaged.Contains(track.BaseObject) || PlayerCombat.TorpedoInbound(track.BaseObject),
                ["nearFlightRoutes"] = new JArray(flightRoutes.Where(route => route.Points.Zip(route.Points.Skip(1),
                    (start, end) => DistanceToLeg(position, start, end)).Any(distance => distance <= WatchRangeNm)).Select(route => route.Id))
            };
        }));
    }

    internal string? ApproachBlocker(Taskforce side, ChartPosition start, ChartPosition end, bool allowWithdrawal = false) => HostileSurfaceTracks(side)
        .FirstOrDefault(track =>
        {
            GeoPosition estimate = track.PositionEstimate!.Value.Item1;
            ChartPosition threat = new(estimate._latitude, estimate._longitude);
            double distance = DistanceToLeg(threat, start, end);
            double radius = WeaponRange(track) ?? WatchRangeNm;
            if (distance > radius) return false;
            return !allowWithdrawal || distance < threat.DistanceTo(start) - 0.01 || threat.DistanceTo(end) <= radius;
        }) is { } blocker ? PlayerPicture.ContactLabel(blocker) : null;

    internal RouteHazard[] Hazards(Taskforce side) => HostileSurfaceTracks(side).Select(track =>
    {
        GeoPosition fix = track.PositionEstimate!.Value.Item1;
        double? range = WeaponRange(track);
        return new RouteHazard(new(fix._latitude, fix._longitude), range ?? WatchRangeNm,
            $"{PlayerPicture.ContactLabel(track)} {(range.HasValue ? "surface-weapon envelope" : "unidentified weapon-range caution area")}");
    }).ToArray();

    internal string? HelicopterRouteBlocker(Taskforce side, GeoPosition origin, GeoPosition[] route, bool repeat)
    {
        if (route.Length == 0) return null;
        ChartPosition[] points = new[] { origin }.Concat(route).Concat(repeat ? [route[0]] : [])
            .Select(point => new ChartPosition(point._latitude, point._longitude)).ToArray();
        foreach (Vehicle track in HostileSurfaceTracks(side))
        {
            if (ShipAirDefenseRange(track) is not double range) continue;
            GeoPosition estimate = track.PositionEstimate!.Value.Item1;
            ChartPosition target = new(estimate._latitude, estimate._longitude);
            double safeRange = range + 2;
            // Allow an aircraft already inside the envelope to fly directly out of it.
            bool leaving = points[0].DistanceTo(target) < safeRange && points[1].DistanceTo(target) >= safeRange
                && DistanceToLeg(target, points[0], points[1]) >= points[0].DistanceTo(target) - 0.01;
            if (points.Zip(points.Skip(1), (start, end) => DistanceToLeg(target, start, end)).Skip(leaving ? 1 : 0)
                .Any(distance => distance < safeRange))
                return $"{PlayerPicture.ContactLabel(track)} · estimated {range:0.#} nm air-defense range";
        }
        return null;
    }

    private double? ShipAirDefenseRange(Vehicle track)
    {
        if (!track.Identified.Value || track.Class?.Value is not { Length: > 0 } classId) return null;
        if (!airDefenseRanges.TryGetValue(classId, out double? range)) airDefenseRanges[classId] = range = SurfaceWeaponRange.ForClass(classId, "AAW");
        return range;
    }

    private static IEnumerable<Vehicle> HostileSurfaceTracks(Taskforce side) => (side.PlottingTable?.Vehicles.AsEnumerable() ?? [])
        .Where(track => PlayerPicture.IsLive(track) && track.BaseObject is Vessel && track.CurrentRelationship() == RelationsState.Hostile
            && track.PositionEstimate.HasValue);

    private double? WeaponRange(Vehicle track)
    {
        if (!track.Identified.Value || track.Class?.Value is not { Length: > 0 } classId) return null;
        if (!weaponRanges.TryGetValue(classId, out double? range)) weaponRanges[classId] = range = SurfaceWeaponRange.ForClass(classId);
        return range;
    }

    private static double DistanceToLeg(ChartPosition point, ChartPosition start, ChartPosition end)
    {
        MissionPosition leg = end.RelativeTo(start);
        MissionPosition offset = point.RelativeTo(start);
        double lengthSquared = leg.X * leg.X + leg.Z * leg.Z;
        double fraction = lengthSquared > 0 ? Math.Clamp((offset.X * leg.X + offset.Z * leg.Z) / lengthSquared, 0, 1) : 0;
        return point.DistanceTo(start.Towards(end, fraction));
    }

    internal static IEnumerable<Vehicle> ProtectedContacts(Taskforce side)
    {
        if (side.PlottingTable?.Vehicles is not { } tracks) yield break;
        string[] messages = MissionCommunications.Messages.Select(message => $"{message.Title}\n{message.From}\n{message.Body}")
            .Where(text => ProtectionLanguage.IsMatch(text) || AttackLanguage.IsMatch(text)).ToArray();
        if (messages.Length == 0) yield break;
        foreach (Vehicle track in tracks)
        {
            if (!PlayerPicture.IsLive(track) || track.BaseObject is not Vessel || track.BaseObject._taskforce == side
                || track.CurrentRelationship() is not (RelationsState.Friendly or RelationsState.Neutral)
                || track.PositionEstimate?.Item1 == null || !track.HasReportedIdentity) continue;
            string label = PlayerPicture.ContactLabel(track);
            if (messages.Any(text => text.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)) yield return track;
        }
    }

    private IEnumerable<CloseThreat> NearbyThreats(Taskforce side, double now)
    {
        var victims = OwnVessels(side).Select(ship => (Id: PlayerPicture.Id(ship), Position: ship.getGeoPosition()))
            .Concat(ProtectedContacts(side).Select(track => (Id: PlayerPicture.Id(track), Position: track.PositionEstimate!.Value.Item1))).ToArray();
        foreach (Vehicle track in HostileSurfaceTracks(side))
        {
            GeoPosition estimate = track.PositionEstimate!.Value.Item1;
            string targetId = PlayerPicture.Id(track);
            double? weaponRange = WeaponRange(track);
            foreach (var victim in victims)
            {
                double range = GeoPosition.HaversineDistanceNmi(victim.Position, estimate);
                if (range > Math.Max(WatchRangeNm, (weaponRange ?? 0) + 5)) continue;
                double? closingKnots = SampleClosingSpeed((targetId, victim.Id), range, now);
                double? eta = null;
                if (weaponRange is double limit)
                {
                    if (range <= limit) eta = 0;
                    else if (closingKnots is > 1) eta = 3600 * (range - limit) / closingKnots.Value;
                }
                yield return new(targetId, victim.Id, range, weaponRange, closingKnots, eta);
            }
        }
    }

    private double? SampleClosingSpeed((string Target, string Victim) pair, double range, double now)
    {
        if (!rangeSamples.TryGetValue(pair, out RangeSample previous) || now - previous.Time > ForecastSeconds)
        {
            rangeSamples[pair] = new(now, range, null);
            return null;
        }
        if (now - previous.Time < SampleIntervalSeconds) return previous.ClosingKnots;
        double closingKnots = (previous.RangeNm - range) * 3600 / (now - previous.Time);
        rangeSamples[pair] = new(now, range, closingKnots);
        return closingKnots;
    }

    private static double? Rounded(double? value) => value is double number ? Math.Round(number, 1) : null;

    internal string? ReviewEvidence(JObject decision, Taskforce side, Vehicle target)
    {
        string targetId = PlayerPicture.Id(target);
        string? victimId = (string?)decision["victimId"];
        if (victimId == null || targetId == victimId) return "The decision did not identify a separate victim.";
        return (string?)decision["evidence"] switch
        {
            "ObservedGunfire" => PlayerCombat.Recent().Any(item => item.AttackerId == targetId && item.VictimId == victimId)
                ? null : "Recent visual gunfire does not corroborate this attacker and victim.",
            "AttackReport" => MatchesReport(side, target, victimId, (int?)decision["messageIndex"])
                ? null : "The attack report does not uniquely corroborate this live contact near the named victim.",
            "HostileThreat" => nearbyThreats.Any(threat => threat.TargetId == targetId && threat.VictimId == victimId && threat.Immediate)
                ? null : "The current picture does not show this hostile contact entering weapon range of the defended ship.",
            _ => "The order did not cite observed gunfire or a specific attack report."
        };
    }

    private static bool MatchesReport(Taskforce side, Vehicle target, string victimId, int? messageIndex)
    {
        if (!IsReportCandidate(target) || target.PositionEstimate is not { } estimate || messageIndex == null) return false;
        MissionCommunications.Message? message = Reports().FirstOrDefault(report => report.Index == messageIndex).Message;
        if (message == null) return false;

        Vessel? own = OwnVessels(side).FirstOrDefault(ship => PlayerPicture.Id(ship) == victimId);
        Vehicle? other = own == null ? PlayerPicture.LiveContact(side, victimId) : null;
        if (own == null && (other?.BaseObject is not Vessel || other.CurrentRelationship() == RelationsState.Hostile)) return false;
        string victimName = own?.DisplayNameShort ?? (other!.HasReportedIdentity ? PlayerPicture.ContactLabel(other) : "");
        GeoPosition? victimPosition = own != null ? own.getGeoPosition() : other?.PositionEstimate?.Item1;
        string reportText = $"{message.From}\n{message.Title}\n{message.Body}";
        if (string.IsNullOrWhiteSpace(victimName) || reportText.IndexOf(victimName, StringComparison.OrdinalIgnoreCase) < 0
            || victimPosition is not { } position || !Near(estimate.Item1, position)) return false;
        if (target.HasReportedIdentity && PlayerPicture.ContactLabel(target) is { Length: > 0 } targetName
            && reportText.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        string? classification = target.Class?.Value;
        return side.PlottingTable?.Vehicles.Any(otherTrack => otherTrack != target && IsReportCandidate(otherTrack)
            && string.Equals(otherTrack.Class?.Value, classification, StringComparison.OrdinalIgnoreCase)
            && otherTrack.PositionEstimate is { } otherEstimate && Near(otherEstimate.Item1, position)) != true;
    }

    private static bool IsReportCandidate(Vehicle track) => PlayerPicture.IsLive(track) && track.BaseObject is Vessel
        && track.CurrentRelationship() is not RelationsState.Friendly and not RelationsState.Neutral
        && (!string.IsNullOrWhiteSpace(track.Class?.Value) || track.Identified.Value)
        && (track.CurrentRelationship() == RelationsState.Hostile || PlayerPicture.IsVisual(track));

    private static bool Near(GeoPosition attacker, GeoPosition victim) => GeoPosition.HaversineDistanceNmi(attacker, victim) <= 20;

    private static IEnumerable<Vessel> OwnVessels(Taskforce side) => Singleton<ObjectsManager>.Instance._listOfAllUnits.OfType<Vessel>()
        .Where(ship => ship && !ship.IsDestroyed && ship._taskforce == side);

    private static IEnumerable<(MissionCommunications.Message Message, int Index)> Reports() =>
        MissionCommunications.Messages.Select((message, index) => (Message: message, Index: index))
            .Where(report => GameTime.missionElapsedTime - report.Message.Time <= PlayerCombat.EvidenceLifetime
                && AttackLanguage.IsMatch(report.Message.Body));
}
