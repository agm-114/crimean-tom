using Newtonsoft.Json.Linq;
using SeaPower;
using SeapowerUI;
using System.Runtime.CompilerServices;
using System.Globalization;
using UnityEngine;

namespace SeaPower.GameBridge;

internal static partial class PlayerFlightDeck
{
    private record AirFit(VehicleTypeOnBoard Aircraft, Loadout Loadout, int Available, PendingLaunchTask[] Held, PendingLaunchTask[] Refits)
    {
        internal string Id => $"{Aircraft._fileName}:{Loadout.Name}";
        internal string[] Roles => AirRoles(Aircraft._objectType, Loadout.Name, Loadout._loadoutCapabilities,
            HasAntiShipStores(Loadout)).ToArray();
    }
    private record Departure(string Role, bool UseRadar, string? TargetId, bool Patrol, GeoPosition[] Route, bool SurfaceRadarOnly = false);
    private record HeldRoute(List<WaypointData> Waypoints, GeoPosition[] Points);
    internal record AirDepartureResult(string UnitId, string Name, string Role, string? TargetId, string Status);
    private static readonly Dictionary<ObjectBase, Departure> Departures = [];
    private static readonly ConditionalWeakTable<ObjectBase, HeldRoute> HeldRoutes = new();
    private static readonly ConditionalWeakTable<Taskforce, HashSet<PatrolZone>> CapZones = new();
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
                ["hostType"] = host is LandUnit ? "Airbase" : "Ship",
                ["launchesBlocked"] = LaunchesBlocked(deck), ["position"] = PlayerPicture.Point(host.getGeoPosition()),
                ["available"] = new JArray(deck._vehiclesOnBoard.Where(unit => IsAirType(unit) && unit.Numbers > 0)
                    .Select(unit => new JObject { ["type"] = unit.DisplayName, ["count"] = unit.Numbers })),
                ["fits"] = new JArray(AirFits(deck, tasks).Select(fit => new JObject
                {
                    ["id"] = fit.Id, ["aircraft"] = fit.Aircraft.DisplayName, ["loadout"] = fit.Loadout.DisplayName,
                    ["helicopter"] = fit.Aircraft._objectType == ObjectBase.ObjectType.Helicopter,
                    ["roles"] = new JArray(fit.Roles), ["availableCount"] = fit.Available,
                    ["preparingCount"] = fit.Held.Sum(held => held.LaunchCount),
                    ["heldBatches"] = new JArray(fit.Held.Select(held => held.LaunchCount)),
                    ["nominalPreparationSeconds"] = Math.Round(fit.Loadout._readyUpDuration),
                    ["nominalCooldownSeconds"] = fit.Loadout._coolDownTime >= 0 ? Math.Round(fit.Loadout._coolDownTime) : null,
                    ["planningCruiseKnots"] = PlanningCruiseKnots(fit),
                    ["readyCount"] = fit.Held.Where(task => task._stateMachine.CurrentState is HandleAwaitSpawnTask).Sum(task => task.LaunchCount),
                    ["refitBatches"] = new JArray(fit.Refits.Select(task => new JObject { ["id"] = task._uid.ToString(),
                        ["fromLoadout"] = CurrentFit(task).DisplayName, ["count"] = task.LaunchCount, ["state"] = task.FlightDeckTaskLabel })),
                    ["stores"] = new JArray(fit.Loadout.AmmunitionAmounts.Where(ammo => ammo.Value > 0).Select(ammo => $"{ammo.Value}× {ammo.Key}"))
                })),
                ["deckTasks"] = new JArray(tasks.Select(task => new JObject
                {
                    ["type"] = task.AircraftType, ["state"] = task.FlightDeckTaskLabel, ["preparation"] = task.Info,
                    ["mission"] = task._ltp?._missionType.ToString(), ["count"] = task is PendingLaunchTask pending ? pending.LaunchCount : 1,
                    ["launchAuthorized"] = task is PendingLaunchTask ready ? ready.launchAllowed : task is LaunchTask,
                    ["loadout"] = task is PendingLaunchTask preparing ? CurrentFit(preparing).DisplayName : null,
                    ["patrolId"] = task._ltp?._patrolZoneID is { Length: > 0 } id ? id : null
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
        GeoPosition position = unit.getGeoPosition();
        bool hasFriendlyBase = home && home!._taskforce == side;
        string[] roles = AirRoles(unit).ToArray();
        AmmunitionParameters[] weapons = StockedAirWeapons(unit).ToArray();
        string[] armedRoles = roles.Where(role => weapons.Any(ammo => MatchesAirWeapon(ammo, role))).ToArray();
        HeldRoutes.TryGetValue(unit, out HeldRoute? held);
        GeoPosition[] route = unit.GetAllWaypointsGeoPositions(getRelative: false).Select(point => point.wpGP).ToArray();
        VisualActionTask? lastWaypoint = unit.GetLastWaypoint();
        IEnumerable<ObjectBase> engaged = unit._currentEngageTasks.Select(task => task._targetObject);
        if (unit._ai?._objectToDestroy is { } ordered) engaged = engaged.Append(ordered);
        return new JObject
        {
            ["id"] = PlayerPicture.Id(unit), ["name"] = unit.DisplayNameShort, ["type"] = unit.ClassNameShort,
            ["role"] = unit._patrolZone?._zoneType.ToString() ?? AirRole(unit, roles), ["roles"] = new JArray(roles),
            ["armedRoles"] = new JArray(armedRoles),
            ["routeLoops"] = lastWaypoint?._waypointsData.Count > 0,
            ["dippingSonarPatrol"] = lastWaypoint is { _setTowedSonars: (true, true), _waypointsData.Count: > 0 },
            ["loadout"] = fit, ["order"] = unit.CurrentOrderText.Value, ["patrol"] = unit._patrolZone?._zoneName,
            ["patrolId"] = unit._patrolZone?._zoneID,
            ["holding"] = held != null, ["heldRoute"] = held == null ? null : new JArray(held.Points.Select(AirPoint)),
            ["position"] = PlayerPicture.Point(position), ["altitudeFeet"] = Math.Round(position._height / UnityPerFoot),
            ["fuelPercent"] = unit._obp._maxFuel > 0 ? Math.Round(100 * unit._obp._currentFuelMass / unit._obp._maxFuel) : null,
            ["rangeNm"] = Math.Round(unit.RangeInKm.Value / 1.852, 1),
            ["transportWindow"] = IsTransportFit(fit) && !unit.IsFormationWingman
                && unit.CurrentOrder.Value?.OrderType != Order.Type.ReturnToBase
                ? TransportWindow(unit, position, held?.Points ?? route, hasFriendlyBase ? home : null) : null,
            ["homeBaseId"] = hasFriendlyBase ? PlayerPicture.Id(home!) : null,
            ["homeDistanceNm"] = hasFriendlyBase ? Math.Round(GeoPosition.HaversineDistanceNmi(position, home!.getGeoPosition()), 1) : null,
            ["leaderId"] = leader && leader!._taskforce == side && leader != unit ? PlayerPicture.Id(leader) : null,
            ["formation"] = unit.Formation?.Name, ["speedKnots"] = Math.Round(unit._velocityInKnots),
            ["airframe"] = unit is Helicopter ? "Helicopter" : "FixedWing",
            ["route"] = new JArray(route.Take(12).Select(AirPoint)),
            ["engagedContacts"] = new JArray(engaged.Where(target => target).Select(target => target.GetMapVehicle(side))
                .OfType<Vehicle>().Where(PlayerPicture.IsLive).Select(PlayerPicture.Id).Distinct()),
            ["stores"] = new JArray(unit.AmmunitionAmountDictionary.Where(ammo => ammo.Value > 0).Select(ammo => $"{ammo.Value}× {ammo.Key}")),
            ["missileRangesNm"] = new JObject(weapons.Where(ammo => ammo is { _type: Ammunition.Type.Missile, _maxLaunchRangeInMiles: > 0 })
                .GroupBy(ammo => ammo._ammunitionFileName).Select(group => new JProperty(group.Key, Math.Round(group.First()._maxLaunchRangeInMiles, 1))))
        };
    }));

    private static JObject? TransportWindow(ObjectBase leader, GeoPosition origin, GeoPosition[] route, ObjectBase? home)
    {
        if (route.Length == 0) return null;
        ObjectBase[] flight = leader.Formation?.Stations.Select(station => station?.UnitObject).OfType<ObjectBase>()
            .Where(unit => unit && !unit.IsDestroyed && InFlight(unit)).Append(leader).Distinct().ToArray() ?? [leader];
        double distance = route.Prepend(origin).Zip(route, (start, end) => GeoPosition.HaversineDistanceNmi(start, end)).Sum();
        if (home != null) distance += GeoPosition.HaversineDistanceNmi(route[^1], home.getGeoPosition());
        double reserve = Math.Max(10, distance * 0.1);
        double required = distance + reserve;
        var budgets = flight.Select(unit =>
        {
            // Helicopter fuel drops while hovering, too. Use the native fuel burn estimate.
            double loadFactor = unit is Helicopter { _hp: { } parameters }
                ? 1 + parameters._loadoutMass / Math.Max(1, parameters._emptyMass) : 1;
            double usableRange = Math.Max(0, unit.RangeInKm.Value) / (1.852 * loadFactor);
            double endurance = unit switch
            {
                Helicopter helo => helo.EnduranceInSec.Value,
                Aircraft aircraft => aircraft.EnduranceInSec.Value,
                _ => 0
            };
            return (Range: usableRange, Hold: usableRange > 0 && endurance > 0 && double.IsFinite(endurance)
                ? Math.Max(0, 1 - required / usableRange) * endurance / 60 : usableRange <= 0 ? 0 : (double?)null);
        }).ToArray();
        double range = budgets.Min(unit => unit.Range);
        double? hold = budgets.Min(unit => unit.Hold);
        return new JObject
        {
            ["aircraftCount"] = flight.Length, ["limitingRangeNm"] = Math.Round(range),
            ["routeAndRecoveryNm"] = Math.Round(distance), ["reserveNm"] = Math.Round(reserve),
            ["spareRangeNm"] = Math.Round(range - required), ["includesHomeLeg"] = home != null,
            ["minutesOfHoldRemaining"] = hold.HasValue ? Math.Round(hold.Value, 1) : null
        };
    }

    internal static JArray AirTargets(Taskforce side) => new((side.PlottingTable?.Vehicles.AsEnumerable() ?? [])
        .Where(track => HostileFix(track) && track.BaseObject is LandUnit or Vessel)
        .GroupBy(track => track.BaseObject is LandUnit).SelectMany(group => group.OrderByDescending(PlayerPicture.ObservesEmission).Take(32))
        .Select(track => new JObject
        {
            ["id"] = PlayerPicture.Id(track), ["label"] = PlayerPicture.ContactLabel(track), ["esm"] = PlayerPicture.ObservesEmission(track),
            ["category"] = track.GetMapType().ToString(), ["groundTarget"] = track.BaseObject is LandUnit,
            ["position"] = PlayerPicture.Point(track.PositionEstimate!.Value.Item1)
        }));

    internal static JArray GroundAttackOptions(Taskforce side)
    {
        Vehicle[] targets = (side.PlottingTable?.Vehicles.AsEnumerable() ?? []).Where(track => HostileFix(track)
            && track.Identified.Value && track.BaseObject is LandUnit).ToArray();
        return new JArray(FlightUnits(side).Where(unit => !unit.IsFormationWingman).SelectMany(unit =>
        {
            AmmunitionParameters[] weapons = StockedAirWeapons(unit).Where(ammo => MatchesAirWeapon(ammo, "Strike")
                || MatchesAirWeapon(ammo, "SEAD")).ToArray();
            if (weapons.Length == 0) return Enumerable.Empty<JObject>();
            GeoPosition origin = unit.getGeoPosition();
            return targets.Select(track => (Track: track, Range: GeoPosition.HaversineDistanceNmi(origin, track.PositionEstimate!.Value.Item1)))
                .OrderBy(target => target.Range).Take(8).SelectMany(target => weapons.Where(ammo =>
                    unit.DoesAmmoMatchTarget(ammo._ammunitionFileName, target.Track.BaseObject))
                    .GroupBy(ammo => MatchesAirWeapon(ammo, "SEAD") ? "SEAD" : "Strike")
                    .Select(group => (Role: group.Key, Ammo: group.OrderByDescending(ammo => ammo._maxLaunchRangeInMiles).First()))
                    .Select(option => new JObject
                    {
                        ["unitId"] = PlayerPicture.Id(unit), ["targetId"] = PlayerPicture.Id(target.Track), ["role"] = option.Role,
                        ["weapon"] = option.Ammo._displayedName, ["distanceNm"] = Math.Round(target.Range, 1),
                        ["nominalReleaseRangeNm"] = option.Ammo._type == Ammunition.Type.Missile
                            ? Math.Round(option.Ammo._maxLaunchRangeInMiles, 1) : null
                    }));
        }).Take(64));
    }

    internal record GroundOpportunity(ObjectBase Unit, Vehicle Track, string Role, double ReleaseNm);

    internal static IEnumerable<GroundOpportunity> GroundOpportunities(Taskforce side)
    {
        Vehicle[] targets = (side.PlottingTable?.Vehicles.AsEnumerable() ?? []).Where(track => HostileFix(track)
            && track.Identified.Value && track.BaseObject is LandUnit && track.GetMapType() is MapVisualType.Radar or MapVisualType.AAA).ToArray();
        if (targets.Length == 0) yield break;
        ObjectBase[] units = PlayerPicture.Units(side);
        HashSet<ObjectBase> assigned = new(PlayerCombat.MissileTargetsInFlight(includeAircraft: true));
        foreach (ObjectBase unit in units)
        {
            assigned.UnionWith(unit._currentEngageTasks.Select(task => task._targetObject));
            if (unit._ai?._objectToDestroy is { } target && target) assigned.Add(target);
        }
        foreach (ObjectBase unit in units.Where(InFlight))
        {
            if (unit.IsFormationWingman || unit._weaponStatus == ObjectBase.WeaponStatus.Hold || HasAirEngagement(unit)
                || unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase || IsTransportFit(unit._obp._loadoutVariant)
                || unit.getHomeBase() is not { } home || !home || home._taskforce != side) continue;
            GeoPosition origin = unit.getGeoPosition();
            double homeDistance = GeoPosition.HaversineDistanceNmi(origin, home.getGeoPosition());
            if (unit.RangeInKm.Value / 1.852 < homeDistance * 1.25 + 12) continue;
            // Keep dedicated CAP on station and use its guns only when the planner assigns the pass.
            if (unit._patrolZone?._zoneType == PatrolZone.ZoneType.CAP) continue;
            AmmunitionParameters[] weapons = StockedAirWeapons(unit).Where(ammo => unit is Helicopter
                ? ammo._type == Ammunition.Type.Missile && MatchesAirWeapon(ammo, "Strike") : MatchesAirWeapon(ammo, "Strafe")).ToArray();
            if (weapons.Length == 0) continue;
            foreach (Vehicle track in targets.Where(track => !assigned.Contains(track.BaseObject)))
            {
                double distance = GeoPosition.HaversineDistanceNmi(origin, track.PositionEstimate!.Value.Item1);
                if (distance > (unit is Helicopter ? 8 : 4)) continue;
                AmmunitionParameters? ammo = weapons.OrderByDescending(weapon => weapon._maxLaunchRangeInMiles)
                    .FirstOrDefault(weapon => unit.DoesAmmoMatchTarget(weapon._ammunitionFileName, track.BaseObject));
                if (ammo == null) continue;
                yield return new(unit, track, unit is Helicopter ? "Strike" : "Strafe",
                    unit is Helicopter ? Math.Max(0, ammo._maxLaunchRangeInMiles * 0.85) : 0);
            }
        }
    }

    internal static string ReorganizeFlight(Taskforce side, ObjectBase unit, string action, ObjectBase? leader)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(unit, side) || !InFlight(unit))
            return "Rejected · aircraft is no longer airborne and commandable";
        if (action == "Split")
        {
            if (leader != null) return "Rejected · Split takes no destination leader";
            if (unit.Formation is not { } formation || !formation.Stations.Any(station => station.UnitObject && station.UnitObject != unit))
                return "Kept · aircraft is already independent";
            formation.DetachUnit(unit);
            return unit.Formation == null ? "Split · aircraft is independent" : "Rejected · native formation retained the aircraft";
        }
        if (action != "Join" || leader == null || leader == unit || leader.IsFormationWingman
            || !PlayerPicture.CanCommand(leader, side) || !InFlight(leader) || !UnitFormation.CheckFormationJoin(leader, unit))
            return "Rejected · Join needs a compatible owned airborne leader";
        if (unit.Formation != null && unit.Formation == leader.Formation) return "Kept · already in that flight";
        if (unit.IsFormationLeader && unit.Formation!.Stations.Any(station => station.UnitObject && station.UnitObject != unit))
            return "Rejected · split this leader from its current flight first";
        if (unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase || leader.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase)
            return "Rejected · aircraft already recovering";
        if (HasAirEngagement(unit)) return "Rejected · finish or disengage from the current attack first";
        UnitFormation destination = leader.Formation ?? new UnitFormation(new UnitFormationParameters(isAircraftFormation: true)
        {
            _leaderObject = leader, _name = $"{leader.DisplayNameShort} Flight", _pattern = UnitFormation.FormationPattern.Vic
        });
        unit._patrolZone?.DetachUnit(unit);
        destination.AddUnit(unit);
        return unit.Formation == destination ? $"Joined · {leader.DisplayNameShort}" : "Rejected · native formation did not add the aircraft";
    }

    internal static JArray StrafingOptions(Taskforce side)
    {
        Vehicle[] targets = (side.PlottingTable?.Vehicles.AsEnumerable() ?? [])
            .Where(track => HostileFix(track) && track.BaseObject is LandUnit && track.Identified.Value).ToArray();
        return new JArray(FlightUnits(side).Where(unit => !unit.IsFormationWingman).SelectMany(unit =>
        {
            AmmunitionParameters[] guns = StockedAirWeapons(unit).Where(ammo => MatchesAirWeapon(ammo, "Strafe")).ToArray();
            if (guns.Length == 0) return Enumerable.Empty<JObject>();
            GeoPosition position = unit.getGeoPosition();
            return targets.Select(track => (Track: track, Range: GeoPosition.HaversineDistanceNmi(position, track.PositionEstimate!.Value.Item1)))
                .Where(option => option.Range <= 12).OrderBy(option => option.Range)
                .Select(option => (option.Track, option.Range, Gun: guns.FirstOrDefault(ammo =>
                    unit.DoesAmmoMatchTarget(ammo._ammunitionFileName, option.Track.BaseObject))))
                .Where(option => option.Gun != null).Take(3).Select(option => new JObject
                {
                    ["unitId"] = PlayerPicture.Id(unit), ["targetId"] = PlayerPicture.Id(option.Track),
                    ["rangeNm"] = Math.Round(option.Range, 1), ["gun"] = option.Gun!._displayedName
                });
        }));
    }

    internal static FlightMapOrder[] MapOrders(Taskforce side, IReadOnlyDictionary<string, FlightChartAssignment> assignments) =>
        FlightUnits(side).Where(unit => !unit.IsFormationWingman)
        .Select(unit =>
        {
            GeoPosition position = unit.getGeoPosition();
            string id = PlayerPicture.Id(unit);
            string name = unit.DisplayNameShort;
            if (unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase) return null;
            assignments.TryGetValue(id, out FlightChartAssignment? assignment);
            if (AirEngagementFix(unit, side) is { } track && track.PositionEstimate?.Item1 is { } point)
            {
                string role = track.GetMapType().ToString() switch
                {
                    "Air" or "Helicopter" => "Intercept",
                    "Surface" => "Maritime strike",
                    "Submarine" => "ASW attack",
                    _ => "Strike"
                };
                string targetId = PlayerPicture.Id(track);
                if (assignment?.TargetId == targetId && assignment.Role != "CAP") role = assignment.Role;
                return new FlightMapOrder(id, name, role, position, point, targetId, point);
            }
            if (assignment?.TargetId is { } acceptedId && GameTime.missionElapsedTime - assignment.IssuedAt <= 20
                && PlayerPicture.LiveContact(side, acceptedId)?.PositionEstimate?.Item1 is { } fix)
                return new FlightMapOrder(id, name, assignment.Role == "CAP" ? "Intercept" : assignment.Role, position, fix, acceptedId, fix);
            if (unit._patrolZone is { _zoneType: PatrolZone.ZoneType.CAP } cap)
                return new FlightMapOrder(id, name, "CAP", position, cap.CenterPosition);
            return assignment is { TargetId: null } && unit.GetLastWaypoint() is { } waypoint
                ? new FlightMapOrder(id, name, $"{assignment.Role} staging", position, waypoint.WaypointPosition) : null;
        }).OfType<FlightMapOrder>().ToArray();

    private static Vehicle? AirEngagementFix(ObjectBase unit, Taskforce side)
    {
        IEnumerable<ObjectBase> targets = unit._alignmentTasks.Select(task => task._targetObject)
            .Concat(unit._currentEngageTasks.Select(task => task._targetObject));
        if (unit._ai?._objectToDestroy is { } ordered) targets = targets.Prepend(ordered);
        return targets.Where(target => target).Select(target => target.GetMapVehicle(side))
            .OfType<Vehicle>().FirstOrDefault(track => PlayerPicture.IsLive(track) && track.PositionEstimate.HasValue);
    }

    private static bool HasAirEngagement(ObjectBase unit) => unit._currentEngageTasks.Count > 0
        || unit._alignmentTasks.Count > 0 || unit._ai?._objectToDestroy != null;

    internal static string QueueAirMission(ObjectBase host, Taskforce side, string fitId, string role, int count,
        GeoPosition[] route, bool useRadar, string? targetId = null, bool prepare = false,
        Action<PendingLaunchTask>? trackTask = null, Action? launched = null, string? capStationId = null, string? refitBatchId = null)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(host, side)
            || host._obp?._flightDeck is not { } deck || LaunchesBlocked(deck)) return "Rejected · deck launches unavailable";
        if (count < 1 || !ValidRoute(route)) return "Rejected · invalid aircraft count or route";
        if (prepare && (targetId != null || route.Length > 0)) return "Rejected · preparation needs an empty route and no target";
        if (!prepare && role is ("Recon" or "ASW" or "Transport") && route.Length == 0) return $"Rejected · {role} needs a route";
        if (!prepare && role == "ASW" && !TryAswRoute(ref route)) return "Rejected · ASW needs a patrol pattern over water";
        if (!prepare && IsStrikeRole(role) && targetId == null && route.Length == 0)
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
            "ASW" => FlightDeckTask.MissionType.SearchAndDestroyASW,
            _ => FlightDeckTask.MissionType.None
        };
        if (tasks.OfType<PendingLaunchTask>().FirstOrDefault(task => task.VehicleType == fit.Aircraft && task._ltp?._missionType == mission
            && task.launchAllowed && CurrentFit(task) == fit.Loadout && (capStationId == null || task._ltp._patrolZoneID == capStationId)) is { } queued)
        {
            Watch(queued);
            return $"Kept · {role} launch already queued for {fit.Loadout.DisplayName}";
        }

        PendingLaunchTask? held = fit.Held.FirstOrDefault(task => task.LaunchCount == count);
        if (refitBatchId != null)
        {
            PendingLaunchTask? original = fit.Refits.FirstOrDefault(task => task._uid.ToString() == refitBatchId && task.LaunchCount == count);
            if (original == null) return "Rejected · selected held batch is no longer eligible for a refit";
            held = ChangePreparedLoadout.Value?.Invoke(original, fit.Loadout);
            if (held == null) return $"Rejected · Deck Tweaks could not refit the held batch to {fit.Loadout.DisplayName}";
            if (prepare)
            {
                trackTask?.Invoke(held);
                return $"Preparation queued · refit {count}× {fit.Aircraft.DisplayName} to {fit.Loadout.DisplayName} · {held.FlightDeckTaskLabel}";
            }
        }
        if (prepare && fit.Held.Sum(task => task.LaunchCount) >= count)
            return $"Kept · {count}× {fit.Loadout.DisplayName} already preparing or held";
        Squadron? squadron = held == null ? fit.Aircraft.Squadrons.FirstOrDefault(unit => unit.Numbers >= count && unit.Callsigns.Count > 0) : null;
        if (held == null && (squadron == null || count > fit.Available || !deck.CanLaunchVehicle(fit.Loadout, count)))
            return "Rejected · requested count exceeds an available squadron or held batch";
        if (prepare)
        {
            LaunchTaskParameters ready = new() { _launchCount = count };
            PendingLaunchTask? preparing = deck.createLaunchTask(fit.Aircraft, fit.Loadout, squadron!, squadron!.Callsigns[0], ready, allowLaunch: false);
            if (preparing != null) trackTask?.Invoke(preparing);
            return preparing == null ? "Rejected · deck could not prepare the flight"
                : $"Preparation queued · {count}× {fit.Aircraft.DisplayName} · {fit.Loadout.DisplayName} · {preparing.FlightDeckTaskLabel}";
        }
        route = SetDefaultAltitude(route, fit.Aircraft._objectType);
        bool patrol = targetId == null && RepeatsAirRoute(role);
        PatrolZone? zone = capStationId != null ? FindCapStation(side, capStationId)
            : role == "CAP" && route.Length > 0 ? CapZone(side, route) : null;
        if (capStationId != null && zone == null) return "Rejected · CAP station is no longer available";
        LaunchTaskParameters parameters = held?._ltp ?? new() { _launchCount = count };
        parameters._missionType = mission;
        parameters._patrolZoneID = zone?._zoneID ?? "";
        parameters._waypoints = zone == null ? route.ToList() : [];
        PendingLaunchTask? task = held ?? deck.createLaunchTask(fit.Aircraft, fit.Loadout, squadron!, squadron!.Callsigns[0], parameters);
        if (task == null) return $"Rejected · deck could not queue {fit.Loadout.DisplayName}";
        Watch(task);
        task.Launched += (_, unit) =>
        {
            if (unit && (useRadar || targetId != null || patrol)) Departures[unit] = new(role, useRadar, targetId, patrol, route);
        };
        if (held != null) held.ToggleLaunchAllowedFunc(null);
        return $"{(held == null ? "Launch queued" : "Launch authorized")} · {count}× {fit.Aircraft.DisplayName} · {fit.Loadout.DisplayName} · {role} · {task.FlightDeckTaskLabel}";

        void Watch(PendingLaunchTask pending)
        {
            trackTask?.Invoke(pending);
            if (launched != null) pending.Launched += (_, unit) => { if (unit) launched(); };
        }
    }

    internal static string TaskAirborne(Taskforce side, string unitId, string role, GeoPosition[] route,
        bool useRadar, string? targetId = null, bool preferBombs = false)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || !ValidRoute(route)) return "Rejected · flight orders unavailable";
        ObjectBase? unit = FlightUnits(side).FirstOrDefault(aircraft => PlayerPicture.Id(aircraft) == unitId);
        if (unit == null) return "Rejected · aircraft is no longer airborne and commandable";
        if (role == "Recover") return RecoverFlight(unit, individual: unit.IsFormationWingman);
        if (unit.IsFormationWingman && targetId == null) return "Rejected · give route orders to the flight leader";
        if (role == "Strafe" && targetId == null) return "Rejected · strafing needs a live hostile ground target";
        if (role is not ("Hold" or "Strafe" or "Disengage") && !AirRoles(unit).Contains(role))
            return "Rejected · current fit cannot perform that mission";
        if (role is "Hold" or "Disengage" && targetId != null) return $"Rejected · {role} needs a route, not a target";
        if (role is "Recon" or "ASW" or "Hold" or "Disengage" && route.Length == 0 && targetId == null)
            return $"Rejected · {role} needs a route";
        if (role == "ASW" && targetId == null && !TryAswRoute(ref route)) return "Rejected · ASW needs a patrol pattern over water";
        if (targetId != null)
        {
            Vehicle? target = AirTarget(side, targetId, role);
            if (target == null) return TargetUnavailable(role);
            return QueueAirAttack(unit, target, role, preferBombs && role == "Strike" && !PlayerPicture.ObservesEmission(target), useRadar);
        }
        if (route.Length > 0 && role is not ("Hold" or "Disengage" or "CAP") && AirEngagementFix(unit, side) != null)
            return "Kept · let the current attack finish before replacing the flight route";
        if (route.Length == 0)
        {
            if (role != "Transport" || !HeldRoutes.TryGetValue(unit, out HeldRoute? original)) return "Kept · existing flight route and orders";
            unit.RemoveWaypoints();
            unit.ImportWaypoints(original.Waypoints);
            HeldRoutes.Remove(unit);
            return $"Transport route resumed · {original.Waypoints.Count} waypoints";
        }
        route = SetDefaultAltitude(route, unit._type, unit.getGeoPosition()._height);
        if (useRadar) unit.EnableAllRadars();
        if (role == "CAP")
        {
            PatrolZone zone = CapZone(side, route);
            bool pursuing = HasAirEngagement(unit);
            if (unit._patrolZone == zone && !pursuing) return "Kept · flight already on this CAP";
            if (pursuing)
            {
                unit.CeaseFire(report: false, stopJamming: false);
                unit._patrolZone?.DetachUnit(unit);
            }
            return zone.AttachUnit(unit) ? $"CAP ordered · {zone._zoneName}" : "Rejected · native patrol zone declined the flight";
        }
        if (role == "Hold") HeldRoutes.GetValue(unit, aircraft => new(aircraft.ExportWaypoints(),
            aircraft.GetAllWaypointsGeoPositions(getRelative: false).Select(point => point.wpGP).ToArray()));
        else if (role == "Transport") HeldRoutes.Remove(unit);
        if (role == "Disengage") unit.CeaseFire(report: false, stopJamming: false);
        return SetAirRoute(unit, route, role, RepeatsAirRoute(role));
    }

    internal static void UpdateAirborneOrders(Taskforce side, Action<AirDepartureResult> report)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side) return;
        PruneCapZones(side);
        foreach (var (unit, departure) in Departures.ToArray())
        {
            if (!unit || unit.IsDestroyed || unit._taskforce != side) { Departures.Remove(unit!); continue; }
            if (!InFlight(unit)) continue;
            Departures.Remove(unit);
            if (!PlayerPicture.CanCommand(unit, side)) continue;
            if (departure.UseRadar)
            {
                if (departure.SurfaceRadarOnly) unit.EnableSurfaceSearchRadars();
                else unit.EnableAllRadars();
            }
            if (departure.TargetId is { } targetId)
            {
                string status = AirTarget(side, targetId, departure.Role) is { } target
                    ? QueueAirAttack(unit, target, departure.Role) : TargetUnavailable(departure.Role);
                report(new(PlayerPicture.Id(unit), unit.DisplayNameShort, departure.Role, targetId, status));
            }
            else
            {
                if (!departure.Patrol || unit.IsFormationWingman) continue;
                string status = SetAirRoute(unit, departure.Route, departure.Role, repeat: true);
                report(new(PlayerPicture.Id(unit), unit.DisplayNameShort, departure.Role, null, status));
            }
        }
    }

    internal static IEnumerable<AirDepartureResult> RecoverSpentCap(Taskforce side)
    {
        foreach (ObjectBase unit in FlightUnits(side))
        {
            ObjectBase? leader = unit.Formation?.LeaderStation?.UnitObject;
            PatrolZone? cap = unit._patrolZone ?? leader?._patrolZone;
            if (cap?._zoneType != PatrolZone.ZoneType.CAP || unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase
                || unit.HasGuidingAlignment() || unit._ai?.HasMissilesNeedingGuidance() == true
                || StockedAirWeapons(unit).Any(ammo => MatchesAirWeapon(ammo, "CAP"))) continue;
            string status = RecoverFlight(unit, individual: true);
            if (status.StartsWith("Recovery ordered", StringComparison.Ordinal))
                yield return new(PlayerPicture.Id(unit), unit.DisplayNameShort, "CAP", null, status);
        }
    }

    private static string RecoverFlight(ObjectBase unit, bool individual)
    {
        if (unit.getHomeBase() is not { } home || !home || home.IsDestroyed || home._obp?._flightDeck == null)
            return "Rejected · aircraft has no available home deck";
        if (unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase) return "Kept · aircraft already returning to base";
        if (individual && unit.Formation is { } formation)
        {
            PatrolZone? cap = unit.IsFormationLeader && unit._patrolZone is { _zoneType: PatrolZone.ZoneType.CAP } station ? station : null;
            formation.DetachUnit(unit);
            if (cap != null && formation.LeaderStation?.UnitObject is { } replacement && replacement && replacement != unit
                && replacement._patrolZone != cap) cap.AttachUnit(replacement);
        }
        unit._patrolZone?.DetachUnit(unit);
        unit.setOrder(Order.Type.ReturnToBase, home, displayOrderText: true);
        return $"Recovery ordered · {home.DisplayNameShort}";
    }

    private static string QueueAirAttack(ObjectBase unit, Vehicle track, string role, bool preferBombs = false, bool useRadar = false)
    {
        ObjectBase target = track.BaseObject;
        if (unit._currentEngageTasks.Any(task => task._targetObject == target))
            return $"Kept · attack already assigned against {PlayerPicture.ContactLabel(track)}";
        AmmunitionParameters? ammo = StockedAirWeapons(unit)
            .Where(item => MatchesAirWeapon(item, role) && unit.DoesAmmoMatchTarget(item._ammunitionFileName, target))
            .OrderByDescending(item => preferBombs ? item._type is Ammunition.Type.Bomb or Ammunition.Type.Cluster or Ammunition.Type.AerialRocket
                : item._type == Ammunition.Type.Missile).ThenByDescending(item => item._maxLaunchRangeInMiles)
            .FirstOrDefault();
        if (ammo == null) return $"Rejected · no usable {role} weapon for {PlayerPicture.ContactLabel(track)}";
        if (unit._userRoot == null) return "Rejected · native attack control unavailable";
        if (unit.IsFormationWingman && unit.Formation is { } formation)
        {
            ObjectBase? leader = formation.LeaderStation?.UnitObject;
            if (leader && StockedAirWeapons(leader!).Any(weapon => MatchesAirWeapon(weapon, role)
                && leader!.DoesAmmoMatchTarget(weapon._ammunitionFileName, target))) return "Rejected · flight leader can make this attack";
            formation.DetachUnit(unit);
        }
        if (useRadar) unit.EnableAllRadars();
        string ammoId = ammo._ammunitionFileName;
        if (unit._userRoot?.AddTaskNow<AttackTask>() is not { } attack) return "Rejected · native attack control unavailable";
        attack.targetObject.value = target;
        attack.salvo.value = role == "Strafe" ? 12 : 1;
        attack._displayOrderText = true;
        attack.ammunitionForEngage.value = ammoId;
        return $"Attack ordered · {ammo._displayedName} · {PlayerPicture.ContactLabel(track)}";
    }

    private static IEnumerable<AmmunitionParameters> StockedAirWeapons(ObjectBase unit) =>
        unit.AmmunitionNameToAmmunitionDictionary.Values.Select(ammo => ammo._ap).OfType<AmmunitionParameters>()
            .Where(ammo => unit.getAmmunitionAmountByName(ammo._ammunitionFileName) > 0);

    internal static bool IsAirborne(ObjectBase unit) => InFlight(unit);

    internal static IEnumerable<(string Role, AmmunitionParameters Weapon)> AttackWeapons(Taskforce side, ObjectBase unit, Vehicle contact)
    {
        string targetId = PlayerPicture.Id(contact);
        foreach (string role in new[] { "CAP", "SEAD", "Strike", "Strafe", "AntiShip" })
        {
            if (AirTarget(side, targetId, role) == null || role != "Strafe" && !AirRoles(unit).Contains(role)) continue;
            foreach (AmmunitionParameters ammo in StockedAirWeapons(unit).Where(ammo => MatchesAirWeapon(ammo, role)
                && unit.DoesAmmoMatchTarget(ammo._ammunitionFileName, contact.BaseObject))) yield return (role, ammo);
        }
    }

    private static bool MatchesAirWeapon(AmmunitionParameters ammo, string role) => role switch
    {
        "CAP" => ammo._type == Ammunition.Type.Missile && (ammo._targetType == Ammunition.Target.AAW || ammo._secondaryTargetType == Ammunition.Target.AAW),
        "SEAD" => ammo._guidanceType == AmmunitionParameters.GuidanceType.AntiRadiationHoming,
        "Strafe" => ammo._type == Ammunition.Type.Projectile,
        "Strike" => ammo._guidanceType != AmmunitionParameters.GuidanceType.AntiRadiationHoming
            && ammo._type is Ammunition.Type.Bomb or Ammunition.Type.Cluster or Ammunition.Type.Missile or Ammunition.Type.AerialRocket,
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
            "SEAD" when track.BaseObject is LandUnit && (PlayerPicture.ObservesEmission(track) || track.Identified.Value) => track,
            "Strike" when track.BaseObject is LandUnit => track,
            "Strafe" when track.BaseObject is LandUnit && track.Identified.Value => track,
            "AntiShip" when track.BaseObject is Vessel => track,
            _ => null
        };
    }

    private static bool HostileFix(Vehicle track) => PlayerPicture.IsLive(track) && track.PositionEstimate.HasValue
        && track.CurrentRelationship() == RelationsState.Hostile;

    private static string TargetUnavailable(string role) => role == "SEAD"
        ? "Rejected · no live hostile ground target with a known class or player-observed radar emission"
        : "Rejected · target is no longer a live hostile contact of the right type";

    private static string SetAirRoute(ObjectBase unit, GeoPosition[] route, string role, bool repeat)
    {
        if (repeat && route.Length == 1) route = [route[0], route[0].GeoOffsetByDistanceOnHeading(4, 90)];
        unit._patrolZone?.DetachUnit(unit);
        unit.RemoveWaypoints();
        bool dipSonar = role == "ASW" && unit is Helicopter && unit._obp._towedSonarSystems.Any(sonar => sonar._isDippingSonar);
        string? sonobuoy = role == "ASW" && !dipSonar ? PatrolSonobuoy(unit) : null;
        foreach (GeoPosition point in route)
        {
            GoToWaypointTask waypoint = unit.setWaypointTask(point);
            if (dipSonar)
            {
                waypoint._setTowedSonars = (true, true);
                waypoint._towedSonarsEnabledTime = 120f;
            }
            else if (sonobuoy != null)
            {
                // The native loop recreates waypoints without callbacks. Seed the search area on the first lap.
                waypoint.ReachedWaypoint += (_, _) => DropPatrolBuoy(unit, point, sonobuoy);
            }
        }
        if (repeat && unit.GetLastWaypoint() is { } last) last._waypointsData = unit.ExportWaypoints();
        string order = role == "Disengage" ? "Disengagement" : !repeat ? "Flight route"
            : role is "Recon" or "ASW" or "Screen" ? $"{role} patrol" : "Hold";
        string sensor = dipSonar ? " · dipping sonar" : sonobuoy != null ? " · sonobuoys on first lap" : "";
        return $"{order} ordered · {route.Length} waypoints{sensor}";
    }

    private static string? PatrolSonobuoy(ObjectBase unit)
    {
        if (!Singleton<UnitMetadataProvider>.InstanceExists()) return null;
        var catalog = Singleton<UnitMetadataProvider>.Instance.AmmunitionData;
        return unit.AmmunitionAmountDictionary.FirstOrDefault(store => store.Value > 0
            && catalog.TryGetValue(store.Key, out AmmunitionSimpleParameters ammo) && ammo._type == Ammunition.Type.Sonobuoy).Key;
    }

    private static void DropPatrolBuoy(ObjectBase unit, GeoPosition point, string ammoId)
    {
        if (unit.getAmmunitionAmountByName(ammoId) <= 0 || unit._userRoot?.AddTaskNow<AttackTask>() is not { } drop) return;
        drop.bearingPosition.value = point;
        drop.salvo.value = 1;
        drop._displayOrderText = true;
        drop.ammunitionForEngage.value = ammoId;
    }

    private static bool TryAswRoute(ref GeoPosition[] route)
    {
        if (route.Any(point => TerrainUtils.GetRealHeightInMetersAt(point) >= 0)) return false;
        if (route.Length > 1) return true;
        foreach (float heading in new[] { 90f, 270f, 0f, 180f })
        {
            GeoPosition next = route[0].GeoOffsetByDistanceOnHeading(4, heading);
            if (TerrainUtils.GetRealHeightInMetersAt(next) >= 0) continue;
            route = [route[0], next];
            return true;
        }
        return false;
    }

    private static PatrolZone CapZone(Taskforce side, GeoPosition[] route)
    {
        GeoPosition origin = route[0];
        GeoPosition[] points = route.Length == 1
            ? [origin.GeoOffsetByDistanceOnHeading(4, 270), origin.GeoOffsetByDistanceOnHeading(4, 90)] : route;
        HashSet<PatrolZone> owned = CapZones.GetOrCreateValue(side);
        PatrolZone? existing = side._patrolZones.FirstOrDefault(zone => zone._zoneType == PatrolZone.ZoneType.CAP
            && zone._relativeWaypoints.Count > 0 && Math.Abs(points[0]._height - zone.GetWaypoint(0)._height) <= 2000 * UnityPerFoot
            && (owned.Contains(zone) ? points.All(point => zone.Contains(UnwrapLongitude(point, zone.CenterPosition._longitude)))
                : zone._relativeWaypoints.Count == points.Length && points.Select((point, index) =>
                    GeoPosition.HaversineDistanceNmi(point, zone.GetWaypoint(index))).All(distance => distance < 0.15)));
        if (existing != null) return existing;

        points = points.Select(point => UnwrapLongitude(point, origin._longitude)).ToArray();
        double south = points.Min(point => point._latitude);
        double north = points.Max(point => point._latitude);
        double west = points.Min(point => point._longitude);
        double east = points.Max(point => point._longitude);
        GeoPosition center = new() { _latitude = (south + north) / 2, _longitude = (west + east) / 2, _height = origin._height };
        const double latitudeMargin = 3.0 / 60;
        double longitudeMargin = latitudeMargin / Math.Max(0.05, Math.Cos(center._latitude * Math.PI / 180));
        south = Math.Max(-90, south - latitudeMargin);
        north = Math.Min(90, north + latitudeMargin);
        west -= longitudeMargin;
        east += longitudeMargin;
        PatrolZone zone = new(side, Singleton<MissionManager>.Instance.GetNextPatrolZoneName(), PatrolZone.ZoneType.CAP)
            { _capAircraftOnStation = 2, _capReserveAircraft = 0 };
        zone.SetCenter(center);
        zone.SetPatrolAltitude((float)(points[0]._height / UnityPerFoot));
        foreach (var (latitude, longitude) in new[] { (south, west), (north, west), (north, east), (south, east) })
            zone.AddAreaPoint(new GeoPosition { _latitude = latitude, _longitude = longitude });
        foreach (GeoPosition point in points) zone.AddWaypoint(point);
        owned.Add(zone);
        side._patrolZones.Add(zone);
        zone.NotifyCreated();
        return zone;
    }

    private static GeoPosition UnwrapLongitude(GeoPosition point, double reference)
    {
        point._longitude = reference + Math.IEEERemainder(point._longitude - reference, 360);
        return point;
    }

    internal static bool IsLineCap(PatrolZone zone) => zone._taskforce is { } side
        && CapZones.TryGetValue(side, out HashSet<PatrolZone>? owned) && owned.Contains(zone)
        && zone._relativeWaypoints.Count == 2;

    private static void PruneCapZones(Taskforce side)
    {
        if (!CapZones.TryGetValue(side, out HashSet<PatrolZone>? owned) || owned.Count == 0) return;
        owned.RemoveWhere(zone => !side._patrolZones.Contains(zone));
        PatrolZone[] empty = owned.Where(zone => !zone._attachedUnits.Any(unit => unit && !unit.IsDestroyed)
            && zone._attachedAirbases.Count == 0).ToArray();
        if (empty.Length == 0) return;
        HashSet<string> queued = new(PlayerPicture.Units(side).Where(unit => unit._obp?._flightDeck != null)
            .SelectMany(unit => AirTasks(unit._obp._flightDeck)).Select(task => task._ltp?._patrolZoneID ?? ""));
        foreach (PatrolZone zone in empty.Where(zone => !queued.Contains(zone._zoneID)))
        {
            zone.Destroy();
            owned.Remove(zone);
        }
    }

    internal static JArray CapStations(Taskforce side) => new(side._patrolZones.Where(zone => zone._zoneType == PatrolZone.ZoneType.CAP)
        .Select(zone => new JObject
        {
            ["id"] = zone._zoneID, ["name"] = zone._zoneName,
            ["center"] = PlayerPicture.Point(zone.CenterPosition), ["armedOnStation"] = CapCoverage(side, zone),
            ["route"] = new JArray(Enumerable.Range(0, zone._relativeWaypoints.Count).Select(index => AirPoint(zone.GetWaypoint(index))))
        }));

    internal static PatrolZone? FindCapStation(Taskforce side, string id) => side._patrolZones
        .FirstOrDefault(zone => zone._zoneType == PatrolZone.ZoneType.CAP && zone._zoneID == id);

    internal static int CapCoverage(Taskforce side, PatrolZone zone) => FlightUnits(side).Count(unit =>
        (unit._patrolZone == zone || unit.Formation?.LeaderStation?.UnitObject?._patrolZone == zone)
        && unit.CurrentOrder.Value?.OrderType != Order.Type.ReturnToBase
        && zone.Contains(UnwrapLongitude(unit.getGeoPosition(), zone.CenterPosition._longitude))
        && StockedAirWeapons(unit).Any(ammo => MatchesAirWeapon(ammo, "CAP")));

    internal static bool HasQueuedCap(Taskforce side, string stationId) => PlayerPicture.Units(side).Any(unit =>
        unit._obp?._flightDeck is { } deck && AirTasks(deck).OfType<PendingLaunchTask>()
            .Any(task => task.launchAllowed && task._ltp?._patrolZoneID == stationId && task.LaunchCount > 0));

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
                PendingLaunchTask[] refits = ChangePreparedLoadout.Value != null && loadout.ServiceEntryYear <= year
                    ? held.Where(task => task.VehicleType == aircraft && CurrentFit(task) != loadout && task._stateMachine.CurrentState is
                        HandleAwaitDeckSpaceTask or HandleAwaitGroundCrewTask or HandleReadyUpTask or HandleAwaitSpawnTask).ToArray() : [];
                if (available <= 0 && matching.Length == 0 && refits.Length == 0) continue;
                AirFit fit = new(aircraft, loadout, available, matching, refits);
                if (fit.Roles.Length > 0) yield return fit;
            }
    }

    private static double? PlanningCruiseKnots(AirFit fit)
    {
        bool helicopter = fit.Aircraft._objectType == ObjectBase.ObjectType.Helicopter;
        string section = helicopter ? "Physics" : "Performance";
        if (fit.Loadout.OwnerIni is not { } ini) return null;
        if (!helicopter)
        {
            string cruise = ini.readValue(section, "SpeedAndRange_Cruise", "", performLogging: false).Split(',')[0];
            if (double.TryParse(cruise, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) && speed > 0 && double.IsFinite(speed))
                return Math.Round(speed < 3 ? speed * 575 : speed);
        }
        float max = ini.readValue(section, "MaxSpeedAtSeaLevel", 0f, performLogging: false);
        return max > 0 && float.IsFinite(max) ? Math.Round(max * (helicopter ? 0.75 : 0.65)) : null;
    }

    private static IEnumerable<string> AirRoles(ObjectBase unit) => AirRoles(unit._type, unit._obp._loadoutVariant,
        unit._currentLoadoutCapabilities, StockedAirWeapons(unit).Any(ammo => MatchesAirWeapon(ammo, "AntiShip")));

    private static IEnumerable<string> AirRoles(ObjectBase.ObjectType type, string loadout,
        ObjectBase.LoadoutCapabilities capabilities, bool antiShip)
    {
        if (type == ObjectBase.ObjectType.Aircraft && PatrolZone.IsCAPLoadout(loadout)) yield return "CAP";
        if (IsAswFit(loadout, capabilities)) yield return "ASW";
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
    private static bool RepeatsAirRoute(string role) => role is "Hold" or "Recon" or "ASW" || IsStrikeRole(role);
    private static bool IsAswFit(string? loadout, ObjectBase.LoadoutCapabilities capabilities) =>
        capabilities._hasASWTorpedoes || loadout?.StartsWith("ASW", StringComparison.OrdinalIgnoreCase) == true;

    private static string? AirRole(ObjectBase unit, string[] roles) => unit.HasRole(ObjectBaseParameters.UnitRoles.AEW) ? "AEW"
        : roles.FirstOrDefault();

    private static IEnumerable<ObjectBase> FlightUnits(Taskforce side) => PlayerPicture.Units(side).Where(InFlight);

    internal record FlightRoute(string Id, string Name, GeoPosition[] Points, double SpeedKnots, GeoPosition? ProjectedPosition,
        bool AttackApproach);

    internal static IEnumerable<FlightRoute> HeldTransportRoutes(Taskforce side)
    {
        foreach (ObjectBase unit in FlightUnits(side).Where(unit => !unit.IsFormationWingman && IsTransportFit(unit._obp._loadoutVariant)))
            if (HeldRoutes.TryGetValue(unit, out HeldRoute? held) && unit.CurrentOrder.Value?.OrderType != Order.Type.ReturnToBase)
                yield return new(PlayerPicture.Id(unit), unit.DisplayNameShort, [unit.getGeoPosition(), .. held.Points],
                    unit._velocityInKnots, null, false);
    }

    internal static IEnumerable<FlightRoute> FlightRoutes(Taskforce side)
    {
        foreach (ObjectBase unit in FlightUnits(side).Where(unit => !unit.IsFormationWingman))
        {
            GeoPosition origin = unit.getGeoPosition();
            double speed = unit._velocityInKnots;
            GeoPosition? projected = speed >= 30
                ? origin.GeoOffsetByDistanceOnHeading((float)(speed / 60), unit.Heading.Value) : default(GeoPosition?);
            bool returning = unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase;
            if (!returning && AirEngagementFix(unit, side)?.PositionEstimate?.Item1 is { } fix)
            {
                // Native attacks choose their own ingress. This is the direct approach estimate.
                yield return new(PlayerPicture.Id(unit), unit.DisplayNameShort, [origin, fix], speed, projected, true);
                continue;
            }
            List<GeoPosition> route = unit.GetAllWaypointsGeoPositions(getRelative: false).Select(point => point.wpGP).ToList();
            PatrolZone? zone = returning ? null : unit._patrolZone;
            if (route.Count == 0 && zone != null)
                route.AddRange(Enumerable.Range(0, zone._relativeWaypoints.Count).Select(zone.GetWaypoint));
            if (route.Count == 0 && returning && unit.getHomeBase() is { } home && home && home._taskforce == side)
                route.Add(home.getGeoPosition());
            if (route.Count > 1 && (zone != null || unit.GetLastWaypoint()?._waypointsData.Count > 0))
                route.Add(route[0]);
            route.Insert(0, origin);
            yield return new(PlayerPicture.Id(unit), unit.DisplayNameShort, route.ToArray(), speed, projected, false);
        }
    }

    private static bool InFlight(ObjectBase unit) => unit is Aircraft { _isInFlight: true } or Helicopter { _isInFlight: true };
    private static bool IsAirType(VehicleTypeOnBoard? unit) => unit?._objectType is ObjectBase.ObjectType.Aircraft or ObjectBase.ObjectType.Helicopter;
    private static bool IsTransportFit(string? loadout) => loadout?.StartsWith("Transport", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool CanEvadeInHelicopter(Helicopter helo) => InFlight(helo) && !helo.IsFormationWingman
        && helo.CurrentOrder.Value?.OrderType != Order.Type.ReturnToBase
        && (IsTransportFit(helo._obp._loadoutVariant) || !HasAirEngagement(helo));

    internal static string EvadeInHelicopter(Taskforce side, Helicopter helo, GeoPosition[] route) =>
        TaskAirborne(side, PlayerPicture.Id(helo), IsTransportFit(helo._obp._loadoutVariant) ? "Hold" : "Disengage", route, useRadar: false);
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
