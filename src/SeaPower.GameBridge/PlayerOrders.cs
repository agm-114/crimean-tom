using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace SeaPower.GameBridge;

internal enum WaypointDisposition { Applied, Unchanged, Rejected }
internal enum SurfaceWeapon { Guns, Torpedoes, Missiles, BestAvailable }
internal record SurfaceOrderResult(string Status, string Detail = "");

internal static class PlayerOrders
{
    private static readonly Type? SelectionState = AccessTools.TypeByName("SeapowerUI.ViewModels.UnitSelectedState");
    private static readonly System.Reflection.MethodInfo? WaypointHandler = SelectionState == null ? null : AccessTools.Method(SelectionState, "HandleUnit");

    internal static bool Blocked => GameTime.IsPaused() || PlayerTime.Blocked;

    internal static (WaypointDisposition Disposition, string Detail) ReplaceWaypoint(Vessel ship, Taskforce side, GeoPosition point)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || !PlayerPicture.CanNavigate(ship))
            return (WaypointDisposition.Rejected, "Ship is unavailable for native waypoint orders");
        if (!double.IsFinite(point._latitude) || !double.IsFinite(point._longitude)
            || Math.Abs(point._latitude) > 90 || Math.Abs(point._longitude) > 180)
            return (WaypointDisposition.Rejected, "Waypoint coordinates are invalid");
        if (HasWaypoint(ship, point)) return (WaypointDisposition.Unchanged, "Waypoint already active");
        if (!DM._waypointsByRMB || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
            || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)
            || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            return (WaypointDisposition.Rejected, "Native map waypoint input is unavailable");
        if (SelectionState == null || WaypointHandler == null || Globals._mainGameViewModel?.Map?.DisplayMap is not { } map)
            return (WaypointDisposition.Rejected, "Native waypoint handler is unavailable");

        object? state = Activator.CreateInstance(SelectionState, [ship]);
        bool accepted = WaypointHandler.Invoke(state, [ship, point, map]) is true;
        return accepted && ship is { } recipient && HasWaypoint(recipient, point)
            ? (WaypointDisposition.Applied, "Native waypoint confirmed")
            : (WaypointDisposition.Rejected, "Native route did not change");
    }

    internal static bool HasWaypoint(ObjectBase ship, GeoPosition point)
    {
        var route = ship.GetAllWaypointsGeoPositions(getRelative: false);
        return route.Count > 0 && SameWaypoint(route[0].wpGP, point);
    }

    private static bool SameWaypoint(GeoPosition first, GeoPosition second) => Math.Abs(first._latitude - second._latitude) < 0.0004
        && Math.Abs(first._longitude - second._longitude) < 0.0004;

    internal static (WaypointDisposition Disposition, string Detail) ReplaceSurfaceRoute(Vessel ship, Taskforce side, GeoPosition[] points)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || !PlayerPicture.CanNavigate(ship) || ship._userRoot == null)
            return (WaypointDisposition.Rejected, "Ship is unavailable for native waypoint orders");
        return ReplaceWaypoints(ship, points);
    }

    private static (WaypointDisposition Disposition, string Detail) ReplaceWaypoints(ObjectBase unit, GeoPosition[] points)
    {
        if (RouteMatches()) return (WaypointDisposition.Unchanged, "Route already active");
        List<WaypointData> original = unit.ExportWaypoints();
        unit.RemoveWaypoints();
        try
        {
            foreach (GeoPosition point in points) unit.setWaypointTask(point);
            if (RouteMatches()) return (WaypointDisposition.Applied, "Native route confirmed");
        }
        catch
        {
            Restore();
            throw;
        }
        Restore();
        return (WaypointDisposition.Rejected, "Native route did not change");

        bool RouteMatches()
        {
            var route = unit.GetAllWaypointsGeoPositions(getRelative: false);
            return route.Count == points.Length && route.Zip(points, (waypoint, point) => SameWaypoint(waypoint.wpGP, point)).All(match => match);
        }

        void Restore()
        {
            unit.RemoveWaypoints();
            unit.ImportWaypoints(original);
        }
    }

    internal static (WaypointDisposition Disposition, string Detail) ReplaceSubmarineWaypoint(Submarine sub, Taskforce side, GeoPosition point)
    {
        if (Blocked || PlayerPicture.Side != side || !CanStageSubmarine(sub, side))
            return (WaypointDisposition.Rejected, "Submarine is unavailable for a staging waypoint");
        if (HasWaypoint(sub, point)) return (WaypointDisposition.Unchanged, "Waypoint already active");
        var result = ReplaceWaypoints(sub, [point]);
        if (result.Disposition != WaypointDisposition.Applied) return result;
        int quiet = QuietSubmarineTelegraph(sub);
        if (quiet > 0 && sub.getTelegraph() < quiet) sub.setTelegraph(quiet);
        return result;
    }

    private static int QuietSubmarineTelegraph(Submarine sub)
    {
        float[] speeds = sub.SP._telegraphVelocities;
        float limit = Math.Min(8, sub.CavitationOnsetKts());
        for (int setting = Math.Min(3, speeds.Length - 1); setting >= 1; setting--)
            if (speeds[setting] <= limit) return setting;
        return 0;
    }

    internal static bool CanStageSubmarine(Submarine sub, Taskforce side) => PlayerPicture.CanCommand(sub, side)
        && (sub.Formation == null || sub.IsFormationLeader || !sub.FollowingFormation)
        && sub._userRoot != null && sub.CurrentOrder.Value?.OrderType == Order.Type.None && sub._patrolZone == null
        && sub._currentEngageTasks.Count == 0 && sub._alignmentTasks.Count == 0 && sub.ThreatingTorpedo == null;

    internal static JArray SurfaceStrikeOptions(IEnumerable<ObjectBase> ships) => new(ships.Select(ship => new JObject
    {
        ["shipId"] = PlayerPicture.Id(ship), ["submarine"] = ship is Submarine,
        ["missiles"] = new JArray(ship.AmmunitionNameToAmmunitionDictionary.Values.Select(ammo => ammo._ap)
            .Where(ammo => ammo is { _type: Ammunition.Type.Missile } && (ammo._targetType == Ammunition.Target.ASuW
                || ammo._secondaryTargetType == Ammunition.Target.ASuW))
            .Select(ammo => new { Ammo = ammo, Count = ship.getAmmunitionAmountByName(ammo._ammunitionFileName) })
            .Where(item => item.Count > 0).Select(item => new JObject
            {
                ["name"] = item.Ammo._displayedName, ["count"] = item.Count,
                ["maxRangeNm"] = Math.Round(item.Ammo._maxLaunchRangeInMiles, 1)
            }))
    }));

    internal static bool InTorpedoEnvelope(Submarine sub, Vehicle contact)
    {
        if (sub._ai == null || contact.PositionEstimate?.Item1 is not { } estimate) return false;
        double distance = Utils.heightInMeterToUnity((float)(GeoPosition.HaversineDistanceNmi(sub.getGeoPosition(), estimate) * 1852));
        return sub._ai.AmmunitionForTarget(contact.BaseObject, Ammunition.Type.Torpedo).Any(ammo =>
            ammo._ap._type == Ammunition.Type.Torpedo && distance > ammo._ap._launchRangesInUnity.x
            && distance <= sub.GetMaxRangeForAmmo(ammo, contact.BaseObject));
    }

    internal static IEnumerable<Vehicle> SubmarineTorpedoTargets(Taskforce side, Submarine sub) =>
        (side.PlottingTable?.Vehicles.AsEnumerable() ?? []).Where(track => PlayerPicture.IsLive(track)
            && track.BaseObject is Vessel && track.CurrentRelationship() == RelationsState.Hostile && InTorpedoEnvelope(sub, track));

    internal static bool CanStageOnContact(Vehicle track) => PlayerPicture.IsLive(track) && track.GetMapType() == MapVisualType.Surface
        && track.PositionEstimate.HasValue && track.CurrentRelationship() is not (RelationsState.Friendly or RelationsState.Neutral);

    internal static string? RequestIdentification(Taskforce side, Vessel sender, Vehicle track)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(sender, side) || !CanStageOnContact(track)
            || track.Identified.Value || Globals._mainGameViewModel is not { } view) return null;
        ObjectBase target = track.BaseObject;
        if (!target.isUnit() || sender.isSubmerged() || target.isSubmerged()
            || !TerrainUtils.IsLineOfSightBetween(sender, target, sender.VisibleAltitude)._los)
            return null;
        float horizon = 1.2f * RadarCalculator.getRadarHorizon(RadarCalculator.GetCommPosition(sender), RadarCalculator.GetCommPosition(target),
            visual: false, Utils.TerrainAltForHorizon(sender, target));
        if ((sender.transform.position - target.transform.position).magnitude > horizon) return null;

        Alert("IdentificationRequest");
        Utils.RevealContactToObject(target, sender, "Comms", 3);
        if (target._ai?.CurrentCompliance == AI.Compliance.Follow)
        {
            Alert("FollowRequest");
            Utils.RevealContactToObject(sender, target, "Comms", 3);
            return "Identified by radio";
        }
        bool refused = target._ai?.CurrentCompliance == AI.Compliance.Refuse;
        Alert(refused ? "RefuseRequest" : "IgnoreComm");
        return refused ? "Identification refused" : "No identification reply";

        void Alert(string key) => view.RaiseAlert(sender, MapUnitViewModel.FormatCommText(track,
            Singleton<LanguageResourceHandler>.Instance.getText("Contact", key)));
    }

    internal static double SubmarineTorpedoStandoffNm(Submarine sub) => sub.AmmunitionNameToAmmunitionDictionary.Values
        .Select(ammo => ammo._ap).Where(ammo => ammo is { _type: Ammunition.Type.Torpedo }
            && (ammo._targetType == Ammunition.Target.ASuW || ammo._secondaryTargetType == Ammunition.Target.ASuW)
            && sub.getAmmunitionAmountByName(ammo._ammunitionFileName) > 0)
        .Select(ammo =>
        {
            float min = ammo._launchRangesInUnity.x;
            float max = ammo._launchRangesInUnity.y;
            return min < max && float.IsFinite(max) ? (max - Math.Min(max * 0.1, (max - min) / 2)) / Utils.heightInMeterToUnity(1852) : 0;
        }).DefaultIfEmpty().Max();

    internal static JArray SubmarineStrikeOptions(Taskforce side) => new(PlayerPicture.Units(side).OfType<Submarine>().Select(sub =>
    {
        GeoPosition origin = sub.getGeoPosition();
        double standoff = SubmarineTorpedoStandoffNm(sub);
        int quietSetting = QuietSubmarineTelegraph(sub);
        double quietSpeed = quietSetting > 0 ? sub.SP._telegraphVelocities[quietSetting] : 0;
        var candidates = CanStageSubmarine(sub, side) && standoff > 0
            ? (side.PlottingTable?.Vehicles.AsEnumerable() ?? []).Where(CanStageOnContact)
                .Select(track => (Track: track, Range: GeoPosition.HaversineDistanceNmi(origin, track.PositionEstimate!.Value.Item1)))
                .OrderBy(item => item.Range).Take(8)
            : Enumerable.Empty<(Vehicle Track, double Range)>();
        return new JObject
        {
            ["shipId"] = PlayerPicture.Id(sub), ["name"] = sub.DisplayNameShort,
            ["speedKnots"] = Math.Round(sub._velocityInKnots, 1), ["position"] = PlayerPicture.Point(origin),
            ["quietTransitSpeedKnots"] = Math.Round(quietSpeed, 1),
            ["depthFeet"] = Math.Round(Math.Max(0, -origin._height) * 220.47266),
            ["order"] = sub.CurrentOrderText.Value, ["weaponsPosture"] = sub._weaponStatus.ToString(),
            ["route"] = SubmarineRoute(sub),
            ["torpedoStandoffNm"] = standoff > 0 ? Math.Round(standoff, 1) : null,
            ["stagingContacts"] = new JArray(candidates.Select(item => new JObject
            {
                ["targetId"] = PlayerPicture.Id(item.Track), ["rangeNm"] = Math.Round(item.Range, 1),
                ["distanceToStandoffNm"] = Math.Round(Math.Max(0, item.Range - standoff), 1),
                ["minutesToStandoffAtQuietSpeed"] = quietSpeed > 0.5
                    ? Math.Round(Math.Max(0, item.Range - standoff) * 60 / quietSpeed) : null
            })),
            ["torpedoTargets"] = new JArray(SubmarineTorpedoTargets(side, sub)
                .Select(track => new JObject { ["targetId"] = PlayerPicture.Id(track), ["label"] = PlayerPicture.ContactLabel(track),
                    ["rangeNm"] = Math.Round(GeoPosition.HaversineDistanceNmi(origin, track.PositionEstimate!.Value.Item1), 1),
                    ["torpedoInbound"] = PlayerCombat.TorpedoInbound(track.BaseObject), ["immediateThreat"] = SubmarineUnderImmediateThreat(sub, track) }))
        };
    }));

    internal static JArray SubmarineRoute(Submarine sub) => new(sub.GetAllWaypointsGeoPositions(getRelative: false).Take(8)
        .Select(point => PlayerPicture.Point(point.wpGP)));

    internal static SurfaceOrderResult EngageWithTorpedoes(ObjectBase unit, Taskforce side, Vehicle contact)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(unit, side) || unit._ai == null
            || !PlayerPicture.IsLive(contact) || contact.BaseObject is not Vessel || unit == contact.BaseObject)
            return new("Rejected", "No eligible torpedo shooter or live surface target");
        if (unit is Submarine sub && !InTorpedoEnvelope(sub, contact))
            return new("Torpedoes unavailable", "No stocked torpedo is in range of the plotted target");
        if (PlayerCombat.TorpedoInbound(contact.BaseObject)) return new("Torpedo held", "A friendly torpedo is already inbound");
        return QueueAttack(unit, contact.BaseObject, Ammunition.Type.Torpedo, 1, "Torpedo order queued", "Torpedoes unavailable");
    }

    internal static bool HasTorpedoOrder(Submarine sub) => sub._currentEngageTasks.Any(task =>
        task._targetObject && task._ammunition?._ap?._type == Ammunition.Type.Torpedo);

    internal static bool SubmarineUnderImmediateThreat(Submarine sub, Vehicle contact) => sub.ThreatingTorpedo != null
        || PlayerCombat.Recent().Any(fire => fire.AttackerId == PlayerPicture.Id(contact) && fire.VictimId == PlayerPicture.Id(sub)
            && GameTime.missionElapsedTime - fire.LastSeen <= 30);

    internal static SurfaceOrderResult EngageSubmarineMissile(Submarine sub, Taskforce side, Vehicle contact)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(sub, side) || sub._ai == null
            || !PlayerPicture.IsLive(contact) || contact.BaseObject is not Vessel target)
            return new("Rejected", "No eligible submarine missile shooter or live surface target");
        if (PlayerCombat.MissileTargetsInFlight(includeAircraft: true).Contains(target))
            return new("Missile held", "A friendly missile is already airborne against this target");
        return QueueAttack(sub, target, Ammunition.Type.Missile, 1, "Missile order queued", "Missiles unavailable");
    }

    internal static SurfaceOrderResult EngageSurface(Vessel ship, Taskforce side, Vehicle contact, SurfaceWeapon weapon)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || ship._ai == null)
            return new("Rejected", "Ship is unavailable for defensive fire");
        if (!PlayerPicture.IsLive(contact) || contact.BaseObject is not Vessel target || target == ship)
            return new("Rejected", "Target is no longer an eligible surface contact");
        if (weapon == SurfaceWeapon.Torpedoes) return EngageWithTorpedoes(ship, side, contact);
        SurfaceOrderResult gun = QueueGun(ship, contact, target);
        if (weapon == SurfaceWeapon.Guns || weapon == SurfaceWeapon.BestAvailable && gun.Status != "Guns unavailable") return gun;
        SurfaceOrderResult missile = QueueSurfaceMissile(ship, side, target);
        string status = missile.Status == "Missile order queued" && gun.Status == "Gun order queued" ? "Missile and gun orders queued"
            : gun.Status == "Gun order queued" ? gun.Status : missile.Status;
        return new(status, $"{missile.Status}: {missile.Detail} · {gun.Status}: {gun.Detail}");
    }

    internal static SurfaceOrderResult EngageLand(Vessel ship, Taskforce side, Vehicle contact, SurfaceWeapon weapon)
    {
        if (weapon == SurfaceWeapon.Torpedoes) return new("Rejected", "Torpedoes cannot bombard a shore target");
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || ship._ai == null)
            return new("Rejected", "Ship is unavailable for shore bombardment");
        if (!PlayerPicture.IsLive(contact) || contact.BaseObject is not LandUnit target)
            return new("Rejected", "Target is no longer an eligible shore contact");
        if (weapon != SurfaceWeapon.Missiles)
        {
            SurfaceOrderResult gun = QueueGun(ship, contact, target);
            if (weapon == SurfaceWeapon.Guns || gun.Status != "Guns unavailable") return gun;
        }
        if (PlayerCombat.MissileTargetsInFlight().Contains(target))
            return new("Missile held", "A friendly missile is already airborne against this target");
        return QueueAttack(ship, target, Ammunition.Type.Missile, 1, "Land-attack missile queued", "Land-attack missiles unavailable");
    }

    internal static double ShoreGunRangeNm(Vessel ship, LandUnit target) => ship._ai == null ? 0
        : ship._ai.AmmunitionForTarget(target, Ammunition.Type.Projectile)
            .Select(ammo => ship.GetMaxRangeForAmmo(ammo, target) / Utils.heightInMeterToUnity(1852)).DefaultIfEmpty().Max();

    private static SurfaceOrderResult QueueGun(Vessel ship, Vehicle contact, ObjectBase target)
    {
        string existing = QueuedWeapons(ship, target, Ammunition.Type.Projectile);
        if (existing.Length > 0) return new("Already engaging", existing);
        string? gunFailure = GunBlockers(ship, target);
        if (gunFailure == null)
        {
            SurfaceOrderResult gunOrder = QueueAttack(ship, target, Ammunition.Type.Projectile, 6, "Gun order queued", "Guns unavailable");
            if (gunOrder.Status == "Gun order queued") return gunOrder;
            gunFailure = "Sea Power declined the gun order despite a ready mount";
        }
        if (contact.PositionEstimate is { } estimate)
            gunFailure = $"{gunFailure} · plotted range {GeoPosition.HaversineDistanceNmi(ship.getGeoPosition(), estimate.Item1):0.#} nm";
        return new("Guns unavailable", gunFailure);
    }

    private static SurfaceOrderResult QueueSurfaceMissile(Vessel ship, Taskforce side, Vessel target)
    {
        if (PlayerCombat.MissileTargetsInFlight().Contains(target))
            return new("Missile held", "A friendly missile is already airborne against this target");
        foreach (Vessel responder in PlayerPicture.Ships(side))
            if (QueuedWeapons(responder, target, Ammunition.Type.Missile) is { Length: > 0 } queued)
                return new("Missile held", $"{responder.DisplayNameShort} already has {queued} queued against this target");
        if (FriendlyNearTarget(side, target)) return QueueGuidedSurfaceMissile(ship, target);
        return QueueAttack(ship, target, Ammunition.Type.Missile, 1, "Missile order queued", "Missiles unavailable");
    }

    private static bool FriendlyNearTarget(Taskforce side, Vessel target)
    {
        if (target.GetMapVehicle(side)?.PositionEstimate?.Item1 is not { } position) return false;
        return PlayerPicture.Ships(side).Any(ship => GeoPosition.HaversineDistanceNmi(ship.getGeoPosition(), position) <= 4)
            || side.PlottingTable?.Vehicles.Any(track => track.BaseObject is Vessel && PlayerPicture.IsLive(track)
                && track.CurrentRelationship() == RelationsState.Friendly && track.PositionEstimate?.Item1 is { } estimate
                && GeoPosition.HaversineDistanceNmi(estimate, position) <= 4) == true;
    }

    private static SurfaceOrderResult QueueGuidedSurfaceMissile(Vessel ship, Vessel target)
    {
        Vector3 position = target.transform.position;
        float distance = (position - ship.transform.position).magnitude;
        foreach (Ammunition ammo in ship._ai.AmmunitionForTarget(target, Ammunition.Type.Missile))
        {
            AmmunitionParameters parameters = ammo._ap;
            if (parameters.GuidanceTypeForTarget(target) != AmmunitionParameters.GuidanceType.SemiActiveRadarHoming
                && parameters._midCourseCorrection != AmmunitionParameters.MidCourseCorrection.RadioCommand) continue;
            if (distance <= parameters._launchRangesInUnity.x || distance > ship.GetMaxRangeForAmmo(ammo, target)) continue;
            List<WeaponSystem>? mounts = WeaponSelector.GetWeaponSystemsForAmmunition(ship, position, parameters._ammunitionFileName, target);
            if (mounts?.Any(mount => WeaponSelector.isWeaponSystemUsable(mount, ammo, target)) != true) continue;
            ship.AddEngageTask(new EngageTask(parameters._ammunitionFileName, target, ship, 1));
            return new("Missile order queued", $"1× {parameters._displayedName} · ship-guided weapon selected near a friendly vessel");
        }
        return new("Missiles unavailable", "Friendly vessel within 4 nm of the target and no effective ship-guided missile available");
    }

    private static SurfaceOrderResult QueueAttack(ObjectBase ship, ObjectBase target, Ammunition.Type type, int salvo, string queued, string unavailable)
    {
        string weapons = QueuedWeapons(ship, target, type);
        if (weapons.Length > 0) return new("Already engaging", weapons);
        ship._ai.AutoAttackByClick(target, type, salvo: salvo, attackAsFormation: false);
        weapons = QueuedWeapons(ship, target, type);
        return weapons.Length > 0 ? new(queued, weapons) : new(unavailable, $"Sea Power queued no {type.ToString().ToLowerInvariant()} task");
    }

    private static string QueuedWeapons(ObjectBase ship, ObjectBase target, Ammunition.Type? type) => string.Join(", ", ship._currentEngageTasks
        .Where(task => task._targetObject == target && (type == null || task._ammunition?._ap?._type == type))
        .Select(task => $"{task._shotsToFire}× {task._ammunition?._ap?._displayedName ?? task._ammoId}"));

    private static string? GunBlockers(Vessel ship, ObjectBase target)
    {
        List<Ammunition> candidates = ship._ai.AmmunitionForTarget(target, Ammunition.Type.Projectile);
        if (candidates.Count == 0) return "No stocked gun ammunition matched the target";
        Vector3 position = target.transform.position;
        float distance = (position - ship.transform.position).magnitude;
        HashSet<string> blockers = [];
        foreach (Ammunition ammo in candidates)
        {
            AmmunitionParameters parameters = ammo._ap;
            if (distance <= parameters._launchRangesInUnity.x) { blockers.Add("target inside minimum gun range"); continue; }
            if (distance > ship.GetMaxRangeForAmmo(ammo, target)) { blockers.Add("target outside effective gun range"); continue; }
            List<WeaponSystem>? mounts = ship.GetWeaponSystemsForAmmunition(parameters._ammunitionFileName, copyList: false);
            if (mounts is not { Count: > 0 }) { blockers.Add("no mount for matching ammunition"); continue; }
            foreach (WeaponSystem mount in mounts)
            {
                string? blocker = GunMountCheck(mount, ammo, target, position);
                if (blocker == null) return null;
                blockers.Add(blocker);
            }
        }
        return string.Join(", ", blockers);
    }

    private static string? GunMountCheck(WeaponSystem mount, Ammunition ammo, ObjectBase target, Vector3 position)
    {
        if (mount is WeaponSystemCIWS { _isOffensive: false }) return "mount is defensive only";
        if (mount.Inoperable.Value || mount._isOffline) return "mount offline or inoperable";
        if (mount._isEmpty || WeaponSelector.isWeaponSystemOutOfAmmo(mount, ammo._ap._ammunitionFileName)) return "mount out of matching ammunition";
        if (mount._vwp._engageOnlyInsideArc && !mount.targetIsInFiringArc(position)) return "target outside firing arc";
        if (mount._executingEngageTask) return "mount busy with another engagement";
        if (mount._isReloading) return "mount reloading";
        if (mount._vwp._blockedByFlightDeck && mount._baseObject._performingAirOps) return "mount blocked by flight operations";
        return WeaponSelector.isWeaponSystemUsable(mount, ammo, target) ? null : "native weapon or fire-control check failed";
    }

    internal static bool EngageAir(Vessel ship, Taskforce side, Vehicle contact)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || ship._ai == null
            || !PlayerPicture.IsLive(contact) || contact.CurrentRelationship() != RelationsState.Hostile
            || !contact.BaseObject.IsAirUnit) return false;
        ObjectBase target = contact.BaseObject;
        ship._ai.AutoAttackByClick(target, Ammunition.Type.Missile, attackAsFormation: false);
        return ship._currentEngageTasks.Any(task => task._targetObject == target);
    }
}
