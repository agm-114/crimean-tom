using Newtonsoft.Json.Linq;
using SeaPower;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SeaPower.GameBridge;

internal static partial class PlayerFlightDeck
{
    private record AirFit(VehicleTypeOnBoard Aircraft, Loadout Loadout, int Available, PendingLaunchTask[] Held)
    {
        internal string Id => $"{Aircraft._fileName}:{Loadout.Name}";
        internal string[] Roles => AirRoles(Aircraft._objectType, Loadout.Name, Loadout._loadoutCapabilities,
            HasAntiShipStores(Loadout)).ToArray();
    }
    private record Departure(string Role, bool UseRadar, string? TargetId, bool Hold, GeoPosition[] Route);
    private static readonly Dictionary<ObjectBase, Departure> Departures = [];
    private static readonly ConditionalWeakTable<ObjectBase, List<WaypointData>> HeldRoutes = new();
    private const double UnityPerFoot = 0.00453571;

    internal static JArray AirOperations(Taskforce side)
    {
        ObjectBase[] airborne = FlightUnits(side).ToArray();
        return new JArray(PlayerPicture.Units(side).Where(host => host._obp?._flightDeck != null).Select(host =>
        {
            FlightDeck deck = host._obp._flightDeck;
            FlightDeckTask[] tasks = AirTasks(deck);
            return new JObject
            {
                ["shipId"] = PlayerPicture.Id(host), ["shipName"] = host.DisplayNameShort,
                ["launchesBlocked"] = LaunchesBlocked(deck), ["position"] = PlayerPicture.Point(host.getGeoPosition()),
                ["available"] = new JArray(deck._vehiclesOnBoard.Where(unit => IsAirType(unit) && unit.Numbers > 0)
                    .Select(unit => new JObject { ["type"] = unit.DisplayName, ["count"] = unit.Numbers })),
                ["fits"] = new JArray(AirFits(deck, tasks).Select(fit => new JObject
                {
                    ["id"] = fit.Id, ["aircraft"] = fit.Aircraft.DisplayName, ["loadout"] = fit.Loadout.DisplayName,
                    ["roles"] = new JArray(fit.Roles), ["availableCount"] = fit.Available,
                    ["preparingCount"] = fit.Held.Sum(held => held.LaunchCount),
                    ["heldBatches"] = new JArray(fit.Held.Select(held => held.LaunchCount)),
                    ["stores"] = new JArray(fit.Loadout.AmmunitionAmounts.Where(ammo => ammo.Value > 0).Select(ammo => $"{ammo.Value}× {ammo.Key}"))
                })),
                ["deckTasks"] = new JArray(tasks.Select(task => new JObject
                {
                    ["type"] = task.AircraftType, ["state"] = task.FlightDeckTaskLabel, ["preparation"] = task.Info,
                    ["mission"] = task._ltp?._missionType.ToString(), ["count"] = task is PendingLaunchTask pending ? pending.LaunchCount : 1,
                    ["launchAuthorized"] = task is PendingLaunchTask ready ? ready.launchAllowed : task is LaunchTask,
                    ["loadout"] = task is PendingLaunchTask preparing ? CurrentFit(preparing).DisplayName : null
                })),
                ["airborne"] = new JArray(airborne.Where(unit => unit.getHomeBase() == host).Select(PlayerPicture.Id))
            };
        }));
    }

    internal static JArray Airborne(Taskforce side) => new(FlightUnits(side).Select(unit =>
    {
        string fit = unit._obp._loadoutVariant;
        ObjectBase? leader = unit.Formation?.LeaderStation?.UnitObject;
        ObjectBase? home = unit.getHomeBase();
        string[] roles = AirRoles(unit).ToArray();
        IEnumerable<ObjectBase> engaged = unit._currentEngageTasks.Select(task => task._targetObject);
        if (unit._ai?._objectToDestroy is { } ordered) engaged = engaged.Append(ordered);
        return new JObject
        {
            ["id"] = PlayerPicture.Id(unit), ["name"] = unit.DisplayNameShort, ["type"] = unit.ClassNameShort,
            ["role"] = unit._patrolZone?._zoneType.ToString() ?? AirRole(unit, roles), ["roles"] = new JArray(roles),
            ["loadout"] = fit, ["order"] = unit.CurrentOrderText.Value, ["patrol"] = unit._patrolZone?._zoneName,
            ["holding"] = HeldRoutes.TryGetValue(unit, out _),
            ["position"] = PlayerPicture.Point(unit.getGeoPosition()), ["altitudeFeet"] = Math.Round(unit.getGeoPosition()._height / UnityPerFoot),
            ["fuelPercent"] = unit._obp._maxFuel > 0 ? Math.Round(100 * unit._obp._currentFuelMass / unit._obp._maxFuel) : null,
            ["rangeNm"] = Math.Round(unit.RangeInKm.Value / 1.852, 1),
            ["homeBaseId"] = home && home!._taskforce == side ? PlayerPicture.Id(home) : null,
            ["leaderId"] = leader && leader!._taskforce == side && leader != unit ? PlayerPicture.Id(leader) : null,
            ["route"] = new JArray(unit.GetAllWaypointsGeoPositions(getRelative: false).Take(12).Select(point => AirPoint(point.wpGP))),
            ["engagedContacts"] = new JArray(engaged.Where(target => target).Select(target => target.GetMapVehicle(side))
                .OfType<Vehicle>().Where(PlayerPicture.IsLive).Select(PlayerPicture.Id).Distinct()),
            ["stores"] = new JArray(unit.AmmunitionAmountDictionary.Where(ammo => ammo.Value > 0).Select(ammo => $"{ammo.Value}× {ammo.Key}")),
            ["missileRangesNm"] = new JObject(unit.AmmunitionNameToAmmunitionDictionary.Values.Select(ammo => ammo._ap)
                .Where(ammo => ammo is { _type: Ammunition.Type.Missile, _maxLaunchRangeInMiles: > 0 }
                    && unit.getAmmunitionAmountByName(ammo._ammunitionFileName) > 0)
                .GroupBy(ammo => ammo._ammunitionFileName).Select(group => new JProperty(group.Key, Math.Round(group.First()._maxLaunchRangeInMiles, 1))))
        };
    }));

    internal static JArray AirTargets(Taskforce side) => new((side.PlottingTable?.Vehicles.AsEnumerable() ?? [])
        .Where(track => HostileFix(track) && track.BaseObject is LandUnit or Vessel)
        .GroupBy(track => track.BaseObject is LandUnit).SelectMany(group => group.OrderByDescending(Emitting).Take(32))
        .Select(track => new JObject
        {
            ["id"] = PlayerPicture.Id(track), ["label"] = PlayerPicture.ContactLabel(track), ["esm"] = Emitting(track),
            ["category"] = track.GetMapType().ToString(),
            ["position"] = PlayerPicture.Point(track.PositionEstimate!.Value.Item1)
        }));

    internal static string QueueAirMission(ObjectBase host, Taskforce side, string fitId, string role, int count,
        GeoPosition[] route, bool useRadar, string? targetId = null)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(host, side)
            || host._obp?._flightDeck is not { } deck || LaunchesBlocked(deck)) return "Rejected · deck launches unavailable";
        if (count < 1 || !ValidRoute(route)) return "Rejected · invalid aircraft count or route";
        if (role is "Recon" or "Transport" && route.Length == 0) return $"Rejected · {role} needs a route";
        if (IsStrikeRole(role) && targetId == null && route.Length == 0)
            return $"Rejected · {role} needs a live target or a staging route";
        if (targetId != null && AirTarget(side, targetId, role) == null) return TargetUnavailable(role);

        FlightDeckTask[] tasks = AirTasks(deck);
        AirFit? fit = AirFits(deck, tasks).FirstOrDefault(option => option.Id == fitId && option.Roles.Contains(role));
        if (fit == null) return "Rejected · selected aircraft fit is no longer available for this mission";
        FlightDeckTask.MissionType mission = role switch
        {
            "CAP" => FlightDeckTask.MissionType.CAP,
            "SEAD" => FlightDeckTask.MissionType.SEAD,
            "Strike" => FlightDeckTask.MissionType.BombStrike,
            "AntiShip" => FlightDeckTask.MissionType.MissileAttack,
            "Recon" => FlightDeckTask.MissionType.Recon,
            _ => FlightDeckTask.MissionType.None
        };
        if (tasks.Any(task => task.VehicleType == fit.Aircraft && task._ltp?._missionType == mission
            && task is PendingLaunchTask { launchAllowed: true } pending && CurrentFit(pending) == fit.Loadout))
            return $"Kept · {role} launch already queued for {fit.Loadout.DisplayName}";

        PendingLaunchTask? held = fit.Held.FirstOrDefault(task => task.LaunchCount == count);
        Squadron? squadron = held == null ? fit.Aircraft.Squadrons.FirstOrDefault(unit => unit.Numbers >= count && unit.Callsigns.Count > 0) : null;
        if (held == null && (squadron == null || count > fit.Available || !deck.CanLaunchVehicle(fit.Loadout, count)))
            return "Rejected · requested count exceeds an available squadron or held batch";
        route = SetDefaultAltitude(route, fit.Aircraft._objectType);
        bool hold = IsStrikeRole(role) && targetId == null;
        PatrolZone? zone = role == "CAP" && route.Length > 0 ? CapZone(side, route) : null;
        LaunchTaskParameters parameters = held?._ltp ?? new() { _launchCount = count };
        parameters._missionType = mission;
        parameters._patrolZoneID = zone?._zoneID ?? "";
        parameters._waypoints = zone == null ? route.ToList() : [];
        PendingLaunchTask? task = held ?? deck.createLaunchTask(fit.Aircraft, fit.Loadout, squadron!, squadron!.Callsigns[0], parameters);
        if (task == null) return $"Rejected · deck could not queue {fit.Loadout.DisplayName}";
        task.Launched += (_, unit) =>
        {
            if (unit && (useRadar || targetId != null || hold)) Departures[unit] = new(role, useRadar, targetId, hold, route);
        };
        if (held != null) held.ToggleLaunchAllowedFunc(null);
        return $"{(held == null ? "Launch queued" : "Launch authorized")} · {count}× {fit.Aircraft.DisplayName} · {fit.Loadout.DisplayName} · {role} · {task.FlightDeckTaskLabel}";
    }

    internal static string TaskAirborne(Taskforce side, string unitId, string role, GeoPosition[] route,
        bool useRadar, string? targetId = null)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || !ValidRoute(route)) return "Rejected · flight orders unavailable";
        ObjectBase? unit = FlightUnits(side).FirstOrDefault(aircraft => PlayerPicture.Id(aircraft) == unitId);
        if (unit == null) return "Rejected · aircraft is no longer airborne and commandable";
        if (unit.IsFormationWingman) return "Rejected · give the order to the flight leader";
        if (role == "Recover")
        {
            if (unit.getHomeBase() is not { } home || !home || home.IsDestroyed || home._obp?._flightDeck == null)
                return "Rejected · aircraft has no available home deck";
            if (unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase) return "Kept · aircraft already returning to base";
            unit._patrolZone?.DetachUnit(unit);
            unit.setOrder(Order.Type.ReturnToBase, home, displayOrderText: true);
            return $"Recovery ordered · {home.DisplayNameShort}";
        }
        if (role != "Hold" && !AirRoles(unit).Contains(role))
            return "Rejected · current fit cannot perform that mission";
        if (targetId != null)
        {
            Vehicle? target = AirTarget(side, targetId, role);
            if (target == null) return TargetUnavailable(role);
            if (useRadar) unit.EnableAllRadars();
            return QueueAirAttack(unit, target, role);
        }
        if (route.Length == 0)
        {
            if (role != "Transport" || !HeldRoutes.TryGetValue(unit, out List<WaypointData> original)) return "Kept · existing flight route and orders";
            unit.RemoveWaypoints();
            unit.ImportWaypoints(original);
            HeldRoutes.Remove(unit);
            return $"Transport route resumed · {original.Count} waypoints";
        }
        route = SetDefaultAltitude(route, unit._type, unit.getGeoPosition()._height);
        if (useRadar) unit.EnableAllRadars();
        if (role == "CAP")
        {
            PatrolZone zone = CapZone(side, route);
            if (unit._patrolZone == zone) return "Kept · flight already on this CAP";
            return zone.AttachUnit(unit) ? $"CAP ordered · {zone._zoneName}" : "Rejected · native patrol zone declined the flight";
        }
        if (role == "Hold") HeldRoutes.GetValue(unit, aircraft => aircraft.ExportWaypoints());
        else if (role == "Transport") HeldRoutes.Remove(unit);
        return SetAirRoute(unit, route, hold: role == "Hold" || IsStrikeRole(role));
    }

    internal static void UpdateAirborneOrders(Taskforce side)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || Departures.Count == 0) return;
        foreach (var (unit, departure) in Departures.ToArray())
        {
            if (!unit || unit.IsDestroyed || unit._taskforce != side) { Departures.Remove(unit!); continue; }
            if (!InFlight(unit)) continue;
            Departures.Remove(unit);
            if (!PlayerPicture.CanCommand(unit, side)) continue;
            if (departure.UseRadar) unit.EnableAllRadars();
            string result = departure.TargetId is { } targetId
                ? AirTarget(side, targetId, departure.Role) is { } target ? QueueAirAttack(unit, target, departure.Role) : TargetUnavailable(departure.Role)
                : departure.Hold && !unit.IsFormationWingman ? SetAirRoute(unit, departure.Route, hold: true) : "Airborne";
            Debug.Log($"[Player flight deck] {unit.DisplayNameShort} · {departure.Role} · {result}");
        }
    }

    private static string QueueAirAttack(ObjectBase unit, Vehicle track, string role)
    {
        ObjectBase target = track.BaseObject;
        Ammunition? ammo = unit.AmmunitionNameToAmmunitionDictionary.Values.Where(item => item._ap is { } parameters
            && unit.getAmmunitionAmountByName(parameters._ammunitionFileName) > 0
            && MatchesAirWeapon(parameters, role) && unit.DoesAmmoMatchTarget(parameters._ammunitionFileName, target))
            .OrderByDescending(item => item._ap._type == Ammunition.Type.Missile).ThenByDescending(item => item._ap._maxLaunchRangeInMiles)
            .FirstOrDefault();
        if (ammo == null) return $"Rejected · no usable {role} weapon for {PlayerPicture.ContactLabel(track)}";
        string ammoId = ammo._ap._ammunitionFileName;
        if (unit._currentEngageTasks.Any(task => task._targetObject == target && task._ammoId == ammoId))
            return $"Kept · {ammo._ap._displayedName} already tasked against {PlayerPicture.ContactLabel(track)}";
        if (unit._userRoot?.AddTaskNow<AttackTask>() is not { } attack) return "Rejected · native attack control unavailable";
        attack.targetObject.value = target;
        attack.salvo.value = 1;
        attack._displayOrderText = true;
        attack.ammunitionForEngage.value = ammoId;
        return $"Attack ordered · {ammo._ap._displayedName} · {PlayerPicture.ContactLabel(track)}";
    }

    private static bool MatchesAirWeapon(AmmunitionParameters ammo, string role) => role switch
    {
        "CAP" => ammo._type == Ammunition.Type.Missile && (ammo._targetType == Ammunition.Target.AAW || ammo._secondaryTargetType == Ammunition.Target.AAW),
        "SEAD" => ammo._guidanceType == AmmunitionParameters.GuidanceType.AntiRadiationHoming,
        "Strike" => ammo._guidanceType != AmmunitionParameters.GuidanceType.AntiRadiationHoming
            && ammo._type is Ammunition.Type.Bomb or Ammunition.Type.Missile or Ammunition.Type.AerialRocket,
        "AntiShip" => IsAntiShipWeapon(ammo._type, ammo._targetType, ammo._secondaryTargetType, ammo._guidanceType),
        _ => false
    };

    private static Vehicle? AirTarget(Taskforce side, string id, string role)
    {
        Vehicle? track = PlayerPicture.LiveContact(side, id);
        if (track == null || !HostileFix(track)) return null;
        return role switch
        {
            "CAP" when track.BaseObject.IsAirUnit => track,
            "SEAD" when track.BaseObject is LandUnit && (Emitting(track) || track.Identified.Value) => track,
            "Strike" when track.BaseObject is LandUnit => track,
            "AntiShip" when track.BaseObject is Vessel => track,
            _ => null
        };
    }

    private static bool HostileFix(Vehicle track) => PlayerPicture.IsLive(track) && track.PositionEstimate.HasValue
        && track.CurrentRelationship() == RelationsState.Hostile;

    private static bool Emitting(Vehicle track) => (track.DetectingSensors & SensorSystem.SensorTypeSet.ESM) != 0;

    private static string TargetUnavailable(string role) => role == "SEAD"
        ? "Rejected · no live hostile ground target with a known class or player-observed radar emission"
        : "Rejected · target is no longer a live hostile contact of the right type";

    private static string SetAirRoute(ObjectBase unit, GeoPosition[] route, bool hold)
    {
        if (hold && route.Length == 1) route = [route[0], route[0].GeoOffsetByDistanceOnHeading(4, 90)];
        unit._patrolZone?.DetachUnit(unit);
        unit.RemoveWaypoints();
        foreach (GeoPosition point in route) unit.setWaypointTask(point);
        if (hold && unit.GetLastWaypoint() is { } last) last._waypointsData = unit.ExportWaypoints();
        return hold ? $"Hold ordered · {route.Length} waypoints" : $"Flight route ordered · {route.Length} waypoints";
    }

    private static PatrolZone CapZone(Taskforce side, GeoPosition[] route)
    {
        GeoPosition center = route[0];
        GeoPosition[] points = route.Length == 1
            ? [center.GeoOffsetByDistanceOnHeading(4, 270), center.GeoOffsetByDistanceOnHeading(4, 90)] : route;
        PatrolZone? existing = side._patrolZones.FirstOrDefault(zone => zone._zoneType == PatrolZone.ZoneType.CAP
            && zone._relativeWaypoints.Count == points.Length && points.Select((point, index) => GeoPosition.HaversineDistanceNmi(point, zone.GetWaypoint(index))).All(distance => distance < 0.15));
        if (existing != null) return existing;
        PatrolZone zone = new(side, Singleton<MissionManager>.Instance.GetNextPatrolZoneName(), PatrolZone.ZoneType.CAP)
        {
            _zoneName = "Combat air patrol", _capAircraftOnStation = 2, _capReserveAircraft = 0
        };
        zone.SetCenter(center);
        float radius = 6 + (float)points.Max(point => GeoPosition.HaversineDistanceNmi(point, center));
        for (int heading = 45; heading < 360; heading += 90) zone.AddAreaPoint(center.GeoOffsetByDistanceOnHeading(radius, heading));
        foreach (GeoPosition point in points) zone.AddWaypoint(point);
        side._patrolZones.Add(zone);
        Globals._mainGameViewModel?.Map?.PatrolZoneLayer.Add(zone);
        return zone;
    }

    private static IEnumerable<AirFit> AirFits(FlightDeck deck, FlightDeckTask[] tasks)
    {
        int year = Singleton<SeaPower.Environment>.InstanceExists() ? Singleton<SeaPower.Environment>.Instance.Year : int.MaxValue;
        PendingLaunchTask[] held = tasks.OfType<PendingLaunchTask>().Where(task => !task.launchAllowed && task.LaunchCount > 0
            && task._ltp._missionType == FlightDeckTask.MissionType.None && task._ltp._waypoints.Count == 0
            && !task.FlightDeckTaskLabel.Contains("Aborting", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (VehicleTypeOnBoard aircraft in deck._vehiclesOnBoard.Where(IsAirType))
            foreach (Loadout loadout in aircraft.Loadouts)
            {
                int available = aircraft.Numbers > 0 && loadout.ServiceEntryYear <= year && deck.CanLaunchVehicle(loadout, 1)
                    ? Math.Min(aircraft.Numbers, aircraft.Squadrons.Where(squadron => squadron.Callsigns.Count > 0).Select(squadron => squadron.Numbers).DefaultIfEmpty().Max()) : 0;
                PendingLaunchTask[] matching = held.Where(task => task.VehicleType == aircraft && CurrentFit(task) == loadout).ToArray();
                if (available <= 0 && matching.Length == 0) continue;
                AirFit fit = new(aircraft, loadout, available, matching);
                if (fit.Roles.Length > 0) yield return fit;
            }
    }

    private static IEnumerable<string> AirRoles(ObjectBase unit) => AirRoles(unit._type, unit._obp._loadoutVariant,
        unit._currentLoadoutCapabilities, unit.AmmunitionNameToAmmunitionDictionary.Values.Any(ammo => ammo._ap is { } parameters
            && unit.getAmmunitionAmountByName(parameters._ammunitionFileName) > 0 && MatchesAirWeapon(parameters, "AntiShip")));

    private static IEnumerable<string> AirRoles(ObjectBase.ObjectType type, string loadout,
        ObjectBase.LoadoutCapabilities capabilities, bool antiShip)
    {
        if (type == ObjectBase.ObjectType.Aircraft && PatrolZone.IsCAPLoadout(loadout)) yield return "CAP";
        if (capabilities._hasARM) yield return "SEAD";
        if (antiShip) yield return "AntiShip";
        if (capabilities.HasAGM || capabilities._hasBombs || capabilities._hasCluster || capabilities._hasRockets) yield return "Strike";
        if (IsTransportFit(loadout)) yield return "Transport";
        else yield return "Recon";
    }

    private static bool HasAntiShipStores(Loadout fit) => fit._loadoutCapabilities._hasAShM || fit._loadoutCapabilities._hasASuWTorpedoes
        || Singleton<UnitMetadataProvider>.InstanceExists() && fit.AmmunitionAmounts.Any(store => store.Value > 0
            && Singleton<UnitMetadataProvider>.Instance.AmmunitionData.TryGetValue(store.Key, out AmmunitionSimpleParameters ammo)
            && IsAntiShipWeapon(ammo._type, ammo._targetType, ammo._secondonaryTargetType, ammo._guidanceType));

    private static bool IsAntiShipWeapon(Ammunition.Type type, Ammunition.Target primary, Ammunition.Target secondary,
        AmmunitionParameters.GuidanceType guidance) => guidance != AmmunitionParameters.GuidanceType.AntiRadiationHoming
        && (primary == Ammunition.Target.ASuW || secondary == Ammunition.Target.ASuW)
        && type is Ammunition.Type.Missile or Ammunition.Type.Bomb or Ammunition.Type.Cluster or Ammunition.Type.AerialRocket or Ammunition.Type.Torpedo;

    private static bool IsStrikeRole(string role) => role is "SEAD" or "Strike" or "AntiShip";

    private static string? AirRole(ObjectBase unit, string[] roles) => unit.HasRole(ObjectBaseParameters.UnitRoles.AEW) ? "AEW"
        : roles.FirstOrDefault();

    private static IEnumerable<ObjectBase> FlightUnits(Taskforce side) => PlayerPicture.Units(side).Where(InFlight);
    private static bool InFlight(ObjectBase unit) => unit is Aircraft { _isInFlight: true } or Helicopter { _isInFlight: true };
    private static bool IsAirType(VehicleTypeOnBoard? unit) => unit?._objectType is ObjectBase.ObjectType.Aircraft or ObjectBase.ObjectType.Helicopter;
    private static bool IsTransportFit(string? loadout) => loadout?.StartsWith("Transport", StringComparison.OrdinalIgnoreCase) == true;
    private static Loadout CurrentFit(PendingLaunchTask task) => task.VehicleType.Loadouts[task._loadoutIndex];
    private static FlightDeckTask[] AirTasks(FlightDeck deck) => deck.FlightDeckTasks.Concat(deck._flightDeckTasksToAdd)
        .Where(task => !task._isCancelled && IsAirType(task.VehicleType)).Distinct().ToArray();

    private static bool ValidRoute(GeoPosition[] route) => route.Length <= 8 && route.All(point => double.IsFinite(point._latitude)
        && double.IsFinite(point._longitude) && double.IsFinite(point._height) && Math.Abs(point._latitude) < 90 && Math.Abs(point._longitude) <= 180);

    private static JObject AirPoint(GeoPosition point)
    {
        JObject value = PlayerPicture.Point(point);
        value["altitudeFeet"] = Math.Round(point._height / UnityPerFoot);
        return value;
    }

    private static GeoPosition[] SetDefaultAltitude(GeoPosition[] route, ObjectBase.ObjectType type, double current = 0)
    {
        double height = current > 0 ? current : (type == ObjectBase.ObjectType.Helicopter ? 1500 : 10000) * UnityPerFoot;
        return route.Select(point => point._height > 0 ? point : new GeoPosition(point, height)).ToArray();
    }
}
