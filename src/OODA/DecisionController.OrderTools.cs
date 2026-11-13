using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal partial class DecisionController
{
    private readonly HashSet<string> loggedCapabilityRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SensorSweeps sensorSweeps = new();
    private double nextSurfaceSample;

    internal void TickSensorSweeps()
    {
        if (!sensorSweeps.Active || PlayerPicture.Side is not { } side || mission != (Globals.currentMissionFilePath ?? "")) return;
        sensorSweeps.Tick(side, ReportSweep);
    }

    private void ReportSweep(ObjectBase unit, string outcome)
    {
        Add("Sensor sweep", outcome, "One-shot sensor order ended", PlayerPicture.Id(unit), unit.DisplayNameShort);
        nextTactical = 0;
        previousSituation = null;
    }

    private void LogCapabilityRequests(DecisionRole role, JToken? requests)
    {
        if (loggedCapabilityRequests.Count >= 24) return;
        foreach (JObject request in requests?.OfType<JObject>().Take(2) ?? [])
        {
            string? kind = (string?)request["kind"];
            string title = Short((string?)request["title"], 100).Trim();
            string problem = Short((string?)request["problem"], 400).Trim();
            string behavior = Short((string?)request["requestedBehavior"], 500).Trim();
            if (kind is not ("Automation" or "Tool" or "Analysis") || title.Length == 0 || problem.Length == 0 || behavior.Length == 0
                || !loggedCapabilityRequests.Add($"{kind}|{title}")) continue;
            JObject entry = new() { ["mission"] = Path.GetFileNameWithoutExtension(mission), ["elapsedSeconds"] = GameTime.missionElapsedTime,
                ["role"] = role.ToString(), ["kind"] = kind, ["title"] = title, ["problem"] = problem, ["requestedBehavior"] = behavior };
            UnityEngine.Debug.Log($"[OODA] Capability request | {entry.ToString(Newtonsoft.Json.Formatting.None)}");
            if (loggedCapabilityRequests.Count >= 24) break;
        }
    }

    private void SampleSurfaceMotion(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (now < nextSurfaceSample) return;
        nextSurfaceSample = now + 10;
        PlayerPicture.SampleSurfaceTracks(side);
    }

    private void ReadOrderTools(JObject view, Taskforce side)
    {
        ObjectBase[] units = PlayerPicture.Units(side);
        view["engagementOptions"] = NearbyEngagementOptions(side, units, 5);
        view["sortieCycleEstimates"] = SortieCycleEstimates(view);
        view["airWeaponProfiles"] = WeaponEffects.Profiles(view);
        view["sensorOptions"] = SensorEstimates.Read(units);
        view["catalogEmitterProfiles"] = SensorEstimates.EmitterProfiles(view);
        view["sensorSweeps"] = sensorSweeps.Read();
        view["orderStatuses"] = ReadOrderStatuses(20);
        view["conditionalOrders"] = ReadConditionalOrders();
        view["movementProfiles"] = new JArray(units.Where(unit => PlayerOrders.MaximumProfileSpeed(unit).HasValue
                && !unit.IsFormationWingman && (!(unit is Aircraft or Helicopter) || PlayerFlightDeck.IsAirborne(unit)))
            .Select(unit => new JObject
            {
                ["unitId"] = PlayerPicture.Id(unit), ["maxSpeedKnots"] = PlayerOrders.MaximumProfileSpeed(unit),
                ["maxAltitudeFeet"] = PlayerOrders.MaximumProfileAltitude(unit), ["depthPresets"] = unit is Submarine ? new JArray(0, 1, 2, 3, 4, 5, 6) : null,
                ["searchRadar"] = unit.HasAirSearchRadar() || unit.HasSurfaceSearchRadar(), ["activeSonar"] = unit.HasActiveSonar()
            }));
    }

    private static JArray NearbyEngagementOptions(Taskforce side, ObjectBase[] units, int limit, bool airOnly = false)
    {
        GeoPosition[] origins = units.Where(unit => unit is Vessel or Submarine || unit is (Aircraft or Helicopter)
            && PlayerFlightDeck.IsAirborne(unit)).Select(unit => unit.getGeoPosition()).ToArray();
        if (origins.Length == 0) return [];
        return new JArray((side.PlottingTable?.Vehicles.AsEnumerable() ?? []).Where(track => PlayerPicture.IsLive(track)
                && track.PositionEstimate != null && track.CurrentRelationship() == RelationsState.Hostile
                && track.BaseObject is not WeaponBase && (!airOnly || track.BaseObject.IsAirUnit))
            .Select(track => (Track: track, Range: origins.Min(origin => GeoPosition.HaversineDistanceNmi(origin, track.PositionEstimate!.Value.Item1))))
            .OrderBy(item => item.Range).Take(limit).Select(item => EngagementOptions.Read(side, units, item.Track, 4)));
    }

    private JArray ReadToolResults(JToken? queries, Taskforce side, JObject input)
    {
        JArray results = new();
        if (queries is not JArray { Count: > 0 }) return results;
        ObjectBase[] units = PlayerPicture.Units(side);
        foreach (JObject query in queries.OfType<JObject>().Take(4))
        {
            string? tool = (string?)query["tool"], unitId = (string?)query["unitId"], targetId = (string?)query["targetId"], orderId = (string?)query["orderId"];
            JObject? result;
            try { result = ReadQuery(query, tool, unitId, targetId, orderId); }
            catch (Exception error) { result = Error($"Unable to complete the lookup · {Short(error.Message)}"); }
            results.Add(new JObject { ["tool"] = tool, ["unitId"] = unitId, ["targetId"] = targetId, ["orderId"] = orderId,
                ["fitId"] = query["fitId"], ["result"] = result ?? Error("Invalid parameters or ID outside the player picture") });
        }
        return results;

        JObject? ReadQuery(JObject query, string? tool, string? unitId, string? targetId, string? orderId)
        {
            JObject? result = null;
            if (tool == "preview_route" && units.FirstOrDefault(unit => PlayerPicture.Id(unit) == unitId) is { } unit
                && unit is Vessel or Submarine or Aircraft or Helicopter && TryReadToolRoute(query["waypoints"], unit, out GeoPosition[] route))
            {
                bool air = unit is Aircraft or Helicopter;
                if (air && !PlayerFlightDeck.IsAirborne(unit)) result = Error("Unit is not airborne");
                else result = RoutePreview.Read(unit, route, air ? airDefenseRoutes.Hazards(side, airDefenseAssessment) : defense.Hazards(side));
            }
            else if (tool == "get_engagement_options" && targetId != null && input["contacts"]?.Any(track => (string?)track["id"] == targetId) == true)
            {
                Vehicle? track = side.PlottingTable?.Vehicles.FirstOrDefault(item => PlayerPicture.Id(item) == targetId);
                result = track == null ? Error("Track is no longer plotted") : EngagementOptions.Read(side, units, track, 10);
            }
            else if (tool == "get_order_status" && orderId != null && orderStatuses.TryGetValue(orderId, out OrderStatus order))
                result = order.Read();
            else if (tool == "estimate_damage" && targetId != null && query["damage"] is JObject damage
                && (string?)damage["weaponId"] is { Length: > 0 and <= 100 } weaponId
                && input["contacts"]?.OfType<JObject>().FirstOrDefault(track => (string?)track["id"] == targetId) is { } damageTarget)
                result = WeaponEffects.Estimate(damageTarget, weaponId, Math.Clamp((int?)damage["directHits"] ?? 1, 1, 240),
                    units.Any(shooter => PlayerPicture.Id(shooter) == unitId && shooter is Aircraft or Helicopter));
            else if (tool == "estimate_detection" && unitId != null && units.FirstOrDefault(observer => PlayerPicture.Id(observer) == unitId) is { } observer
                && query["detection"] is JObject detection)
                result = SensorEstimates.Estimate(observer, (string?)detection["sensorId"], (string?)detection["targetKind"] ?? "",
                    (float?)detection["targetHeightMeters"], (float?)detection["rcsSquareMeters"], (float?)detection["observerHeightMeters"], (float?)detection["observerSpeedKnots"]);
            else if (tool == "preview_sortie_cycle" && unitId != null && (string?)query["fitId"] is { } fitId
                && input["airDecks"]?.OfType<JObject>().FirstOrDefault(deck => (string?)deck["shipId"] == unitId) is { } deck
                && deck["fits"]?.OfType<JObject>().FirstOrDefault(fit => (string?)fit["id"] == fitId) is { } fit
                && units.FirstOrDefault(host => PlayerPicture.Id(host) == unitId) is { } host
                && TryReadSurfaceRoute(query["waypoints"], out GeoPosition[] legs))
            {
                double stationSeconds = (double?)query["onStationSeconds"] ?? 0;
                if (double.IsFinite(stationSeconds) && stationSeconds is >= 0 and <= 14400)
                    result = SortieCycle(host.getGeoPosition(), legs, fit, stationSeconds);
            }
            return result;
        }

        static JObject Error(string reason) => new() { ["error"] = reason };
    }

    private static JArray SortieCycleEstimates(JObject view)
    {
        var stations = (view["capStations"]?.OfType<JObject>() ?? []).Take(2).Select(station => (Id: station["id"], Point: station["center"], Role: "CAP"))
            .Concat((view["airTargets"]?.OfType<JObject>() ?? []).Where(target => (bool?)target["groundTarget"] == true).Take(2)
                .Select(target => (Id: target["id"], Point: target["position"], Role: (bool?)target["esm"] == true ? "SEAD" : "Strike")))
            .Where(station => ReadPoint(station.Point).HasValue).ToArray();
        return new JArray((view["airDecks"]?.OfType<JObject>() ?? []).Where(deck => (bool?)deck["launchesBlocked"] != true
            && ReadPoint(deck["position"]).HasValue).Take(6).Select(deck => new JObject
        {
            ["unitId"] = deck["shipId"],
            ["stations"] = new JArray(stations.Select(station => new JObject
            {
                ["id"] = station.Id, ["role"] = station.Role,
                ["estimates"] = new JArray((deck["fits"]?.OfType<JObject>() ?? [])
                    .Where(fit => (double?)fit["planningCruiseKnots"] > 0 && fit["roles"]?.Values<string>().Contains(station.Role) == true)
                    .Select(fit => SortieCycle(ReadPoint(deck["position"])!.Value, [ReadPoint(station.Point)!.Value], fit, 0))
                    .OrderBy(estimate => (double?)estimate["timeToStationSeconds"]).Take(2))
            }))
        }));
    }

    private static JObject SortieCycle(GeoPosition origin, GeoPosition[] outbound, JObject fit, double stationSeconds)
    {
        double outboundNm = 0;
        GeoPosition from = origin;
        foreach (GeoPosition to in outbound)
        {
            outboundNm += GeoPosition.HaversineDistanceNmi(from, to);
            from = to;
        }
        double returnNm = GeoPosition.HaversineDistanceNmi(from, origin);
        double? knots = (double?)fit["planningCruiseKnots"];
        double? nominalPrep = (double?)fit["nominalPreparationSeconds"] is double prep && prep >= 0 ? prep : null;
        double? cooldown = (double?)fit["nominalCooldownSeconds"];
        double? initialPrep = (int?)fit["readyCount"] > 0 ? 0 : nominalPrep;
        double? outward = knots > 0 ? 3600 * outboundNm / knots : null;
        double? back = knots > 0 ? 3600 * returnNm / knots : null;
        return new JObject { ["fitId"] = fit["id"], ["outboundNm"] = Math.Round(outboundNm, 1), ["directReturnNm"] = Math.Round(returnNm, 1),
            ["planningCruiseKnots"] = knots, ["initialPreparationSeconds"] = initialPrep, ["outboundSeconds"] = Round(outward),
            ["returnSeconds"] = Round(back), ["onStationSeconds"] = stationSeconds, ["timeToStationSeconds"] = Round(initialPrep + outward),
            ["earliestBackAtHostSeconds"] = Round(initialPrep + outward + stationSeconds + back),
            ["nominalCooldownSeconds"] = cooldown,
            ["repeatCycleLowerBoundSeconds"] = Round(nominalPrep + (cooldown ?? 0) + outward + stationSeconds + back),
            ["assumptions"] = "Direct return to present host position. Add launch, recovery, and actual refit or deck queue delays." };

        static double? Round(double? value) => value.HasValue ? Math.Round(value.Value) : null;
    }

    private static GeoPosition? ReadPoint(JToken? point) => (double?)point?["latitude"] is { } lat && (double?)point?["longitude"] is { } lon
        ? new GeoPosition { _latitude = lat, _longitude = lon } : (GeoPosition?)null;

    private static bool TryReadToolRoute(JToken? value, ObjectBase unit, out GeoPosition[] route)
    {
        route = [];
        if (!TryReadSurfaceRoute(value, out GeoPosition[] points) || value is not JArray entries) return false;
        if (unit is Aircraft or Helicopter)
        {
            double defaultFeet = Math.Max(100, unit.DesiredAltitude.Value * 220.47266);
            for (int index = 0; index < points.Length; index++)
            {
                double feet = (double?)entries[index]["altitudeFeet"] ?? defaultFeet;
                if (!double.IsFinite(feet) || feet < 100 || feet > PlayerOrders.MaximumProfileAltitude(unit)) return false;
                points[index]._height = feet / 220.47266;
            }
        }
        else if (entries.Any(point => (double?)point["altitudeFeet"] != null)) return false;
        double distance = 0;
        GeoPosition start = unit.getGeoPosition();
        foreach (GeoPosition point in points)
        {
            distance += GeoPosition.HaversineDistanceNmi(start, point);
            start = point;
        }
        if (!double.IsFinite(distance) || distance > 1200) return false;
        route = points;
        return true;
    }

    private void ApplyMovementProfiles(JToken? orders, Taskforce side)
    {
        if (orders is not JArray { Count: > 0 } entries) return;
        Dictionary<string, ObjectBase> units = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        HashSet<string> handled = [];
        foreach (JObject order in entries.OfType<JObject>().Take(8))
        {
            if ((string?)order["unitId"] is not { } id || !units.TryGetValue(id, out ObjectBase unit) || !handled.Add(id)
                || LastInput?["movementProfiles"]?.Any(profile => (string?)profile["unitId"] == id) != true) continue;
            string outcome = Mode != ControlMode.AiControl ? "Profile suggested"
                : PlayerOrders.SetMovementProfile(side, unit, (float?)order["speedKnots"], (float?)order["altitudeFeet"],
                    (int?)order["depthPreset"], (string?)order["emissions"] ?? "Keep");
            if (Mode == ControlMode.AiControl)
                NewOrder(id, "Movement profile").Set(OrderRejected(outcome) ? "blocked" : "completed", outcome);
            Add("Movement profile", outcome, Short((string?)order["reason"]), id, unit.DisplayNameShort);
        }
    }

    private static bool OrderRejected(string outcome) => outcome.StartsWith("Rejected", StringComparison.Ordinal) || outcome.StartsWith("Failed", StringComparison.Ordinal);

    private void ApplySensorSweeps(JToken? orders, Taskforce side)
    {
        if (orders is not JArray { Count: > 0 } entries) return;
        Dictionary<string, ObjectBase> units = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        foreach (JObject order in entries.OfType<JObject>().Take(4))
        {
            if ((string?)order["unitId"] is not { } id || !units.TryGetValue(id, out ObjectBase unit)
                || (string?)order["kind"] is not { } kind || LastInput?["sensorOptions"]?.Any(option => (string?)option["unitId"] == id) != true) continue;
            string outcome = Mode == ControlMode.AiControl ? sensorSweeps.Start(side, unit, kind, (string?)order["sensorId"], (double?)order["seconds"])
                : "Sweep suggested";
            Add("Sensor sweep", outcome, Short((string?)order["reason"]), id, unit.DisplayNameShort);
        }
    }
}
