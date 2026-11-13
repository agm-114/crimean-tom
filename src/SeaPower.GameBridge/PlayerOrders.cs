using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPower.GameBridge;

internal enum WaypointDisposition { Applied, Unchanged, Rejected }
internal enum SurfaceWeapon { Guns, Missiles, BestAvailable }
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

    internal static bool HasWaypoint(Vessel ship, GeoPosition point)
    {
        var route = ship.GetAllWaypointsGeoPositions(getRelative: false);
        return route.Count > 0 && Math.Abs(route[0].wpGP._latitude - point._latitude) < 0.0004
            && Math.Abs(route[0].wpGP._longitude - point._longitude) < 0.0004;
    }

    internal static SurfaceOrderResult EngageSurface(Vessel ship, Taskforce side, Vehicle contact, SurfaceWeapon weapon)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || ship._ai == null)
            return new("Rejected", "Ship is unavailable for defensive fire");
        if (!PlayerPicture.IsLive(contact) || contact.BaseObject is not Vessel target || target == ship)
            return new("Rejected", "Target is no longer an eligible surface contact");
        SurfaceOrderResult gun = QueueSurfaceGun(ship, contact, target);
        if (weapon == SurfaceWeapon.Guns || weapon == SurfaceWeapon.BestAvailable && gun.Status != "Guns unavailable") return gun;
        SurfaceOrderResult missile = QueueSurfaceMissile(ship, side, target);
        string status = missile.Status == "Missile order queued" && gun.Status == "Gun order queued" ? "Missile and gun orders queued"
            : gun.Status == "Gun order queued" ? gun.Status : missile.Status;
        return new(status, $"{missile.Status}: {missile.Detail} · {gun.Status}: {gun.Detail}");
    }

    private static SurfaceOrderResult QueueSurfaceGun(Vessel ship, Vehicle contact, Vessel target)
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

    private static SurfaceOrderResult QueueAttack(Vessel ship, Vessel target, Ammunition.Type type, int salvo, string queued, string unavailable)
    {
        string weapons = QueuedWeapons(ship, target, type);
        if (weapons.Length > 0) return new("Already engaging", weapons);
        ship._ai.AutoAttackByClick(target, type, salvo: salvo, attackAsFormation: false);
        weapons = QueuedWeapons(ship, target, type);
        return weapons.Length > 0 ? new(queued, weapons) : new(unavailable, $"Sea Power queued no {type.ToString().ToLowerInvariant()} task");
    }

    private static string QueuedWeapons(Vessel ship, ObjectBase target, Ammunition.Type? type) => string.Join(", ", ship._currentEngageTasks
        .Where(task => task._targetObject == target && (type == null || task._ammunition?._ap?._type == type))
        .Select(task => $"{task._shotsToFire}× {task._ammunition?._ap?._displayedName ?? task._ammoId}"));

    private static string? GunBlockers(Vessel ship, Vessel target)
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

    private static string? GunMountCheck(WeaponSystem mount, Ammunition ammo, Vessel target, Vector3 position)
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
