using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal static class EngagementOptions
{
    internal static JObject Read(Taskforce side, ObjectBase[] units, Vehicle track, int limit)
    {
        string id = PlayerPicture.Id(track);
        JObject result = new() { ["targetId"] = id, ["trackRequirement"] = "Live plotted fix and native fire control" };
        if (!PlayerPicture.IsLive(track) || track.PositionEstimate?.Item1 is not { } fix)
        {
            result["blocker"] = "No live plotted fix";
            return result;
        }
        ObjectBase target = track.BaseObject;
        PlayerPicture.SurfaceMotion? motion = PlayerPicture.ObservedSurfaceMotion(track);
        result["posture"] = track.CurrentRelationship().ToString();
        result["surfaceMotion"] = motion?.Read();
        result["guidedWeaponInFlight"] = PlayerCombat.MissileTargetsInFlight(includeAircraft: true).Contains(target);
        result["lastOwnFireAt"] = PlayerCombat.LastOwnFireAt(id);
        var options = units.Where(unit => unit != target && unit is Vessel or Submarine or Aircraft or Helicopter)
            .SelectMany(unit => Options(unit)).OrderBy(option => (double?)option["secondsToRelease"] ?? double.PositiveInfinity)
            .ThenBy(option => (double?)option["rangeNm"] ?? double.PositiveInfinity).ToArray();
        result["shooters"] = new JArray(options.GroupBy(option => (string?)option["unitId"])
            .Take(limit).Select(group => new JObject { ["unitId"] = group.Key, ["name"] = group.First()["name"],
                ["weapons"] = new JArray(group.Take(4).Select(option => { option.Remove("name"); option.Remove("unitId"); return option; })) }));
        if (options.Length == 0) result["blocker"] = "No compatible stocked weapon offered";
        return result;

        IEnumerable<JObject> Options(ObjectBase unit)
        {
            bool air = unit is Aircraft or Helicopter;
            if (air && !PlayerFlightDeck.IsAirborne(unit)) yield break;
            double range = GeoPosition.HaversineDistanceNmi(unit.getGeoPosition(), fix);
            string unitId = PlayerPicture.Id(unit);
            bool committed = unit._currentEngageTasks.Any(task => task._targetObject == target) || unit._ai?._objectToDestroy == target;
            bool busy = !committed && unit._currentEngageTasks.Any(task => task._targetObject && task._targetObject != target);
            string? unitBlocker = unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase ? "Recovering"
                : busy ? "Working a different target" : null;
            var weapons = air ? PlayerFlightDeck.AttackWeapons(side, unit, track).Select(item => (item.Role, Ammo: item.Weapon,
                    Min: (double)item.Weapon._minLaunchRangeInMiles, Max: (double)item.Weapon._maxLaunchRangeInMiles, Blocker: (string?)null))
                : PlayerOrders.EngagementWeapons(unit, target, range).Select(item => (Role: item.Ammo._type.ToString(), item.Ammo, item.Min, item.Max, item.Blocker));
            foreach (var (role, ammo, min, max, nativeBlocker) in weapons)
            {
                if (!double.IsFinite(min) || !double.IsFinite(max) || max < min) continue;
                double? release = null;
                string? blocker = unitBlocker ?? nativeBlocker;
                string readiness = "Blocked";
                if (blocker == null && range >= min && range <= max)
                {
                    readiness = air ? "In nominal range" : "Mount checks passed";
                    release = air ? 10 : 0;
                }
                else if (blocker == null)
                {
                    blocker = range < min ? "Inside minimum release range" : "Outside release range";
                    readiness = "Reposition required";
                    double speed = Math.Abs(unit.SpeedCommand.Value?.CommandSpeedInKnots ?? unit._velocityInKnots);
                    if (range < min && speed > 0.5) release = (min - range) * 3600 / speed + (air ? 10 : 0);
                    if (range > max)
                    {
                        var intercept = PlayerPicture.Intercept(unit.getGeoPosition(), fix, motion, speed, max, horizonHours: 4);
                        if (intercept.HasValue) release = intercept.Value.Hours * 3600 + (air ? 10 : 0);
                        else if (motion != null) blocker = "No projected release position at current speed within four hours";
                    }
                }
                yield return new JObject
                {
                    ["unitId"] = unitId, ["name"] = unit.DisplayNameShort, ["weapon"] = ammo._displayedName, ["role"] = role,
                    ["count"] = unit.getAmmunitionAmountByName(ammo._ammunitionFileName), ["rangeNm"] = Math.Round(range, 1),
                    ["minRangeNm"] = Math.Round(min, 1), ["maxRangeNm"] = Math.Round(max, 1), ["readiness"] = readiness,
                    ["secondsToRelease"] = release.HasValue ? Math.Round(release.Value) : null, ["blocker"] = blocker,
                    ["committed"] = committed, ["shotsObserved"] = PlayerCombat.OwnShotCount(unitId, id, ammo._displayedName),
                    ["weaponPosture"] = unit._weaponStatus.ToString()
                };
            }
        }
    }
}
