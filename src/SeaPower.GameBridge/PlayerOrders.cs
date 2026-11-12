using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPower.GameBridge;

internal enum WaypointDisposition { Applied, Unchanged, Rejected }
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

        object? state = Activator.CreateInstance(SelectionState, ship);
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

    internal static SurfaceOrderResult EngageSurface(Vessel ship, Taskforce side, Vehicle contact, bool gunsOnly)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || ship._ai == null)
            return new("Rejected", "Ship is unavailable for defensive fire");
        if (!PlayerPicture.IsLive(contact) || contact.BaseObject is not Vessel target || target == ship)
            return new("Rejected", "Target is no longer an eligible surface contact");
        if (ship._currentEngageTasks.Any(task => task._targetObject == target))
            return new("Already engaging", QueuedWeapons(ship, target));

        ship._ai.AutoAttackByClick(target, Ammunition.Type.Projectile, salvo: 6, attackAsFormation: false);
        if (ship._currentEngageTasks.Any(task => task._targetObject == target)) return new("Gun order queued", QueuedWeapons(ship, target));
        string gunFailure;
        try { gunFailure = ExplainGunRejection(ship, target); }
        catch (Exception error)
        {
            Debug.LogWarning($"[Game Bridge] Gun readiness check failed: {error.Message}");
            gunFailure = "Sea Power queued no gun task and the mount check could not determine why";
        }
        if (gunsOnly) return new("Guns unavailable", gunFailure);
        ship._ai.AutoAttackByClick(target, attackAsFormation: false);
        return ship._currentEngageTasks.Any(task => task._targetObject == target)
            ? new("Engagement queued", $"Guns unavailable: {gunFailure}. Native fallback queued {QueuedWeapons(ship, target)}")
            : new("No weapon available", $"Guns unavailable: {gunFailure}. Native fallback queued no task");
    }

    private static string QueuedWeapons(Vessel ship, ObjectBase target) => string.Join(", ", ship._currentEngageTasks
        .Where(task => task._targetObject == target)
        .Select(task => $"{task._shotsToFire}× {task._ammunition?._ap?._displayedName ?? task._ammoId}"));

    private static string ExplainGunRejection(Vessel ship, Vessel target)
    {
        List<Ammunition> candidates = ship._ai.AmmunitionForTarget(target, Ammunition.Type.Projectile);
        if (candidates.Count == 0) return "No stocked gun ammunition matched the target";
        Vector3 position = target.transform.position;
        float distance = (position - ship.transform.position).magnitude;
        IEnumerable<string> checks = candidates.SelectMany(ammo =>
        {
            AmmunitionParameters parameters = ammo._ap;
            if (distance <= parameters._launchRangesInUnity.x) return ["target inside minimum range"];
            List<WeaponSystem>? mounts = ship.GetWeaponSystemsForAmmunition(parameters._ammunitionFileName, copyList: false);
            return mounts is { Count: > 0 } ? mounts.Select(mount => GunMountCheck(mount, ammo, target, position))
                : ["no mount for matching ammunition"];
        });
        return string.Join(", ", checks.Distinct());
    }

    private static string GunMountCheck(WeaponSystem mount, Ammunition ammo, Vessel target, Vector3 position)
    {
        if (mount is WeaponSystemCIWS { _isOffensive: false }) return "mount is defensive only";
        if (mount.Inoperable.Value || mount._isOffline) return "mount offline or inoperable";
        if (mount._isEmpty || WeaponSelector.isWeaponSystemOutOfAmmo(mount, ammo._ap._ammunitionFileName)) return "mount out of matching ammunition";
        if (mount._vwp._engageOnlyInsideArc && !mount.targetIsInFiringArc(position)) return "target outside firing arc";
        if (mount._executingEngageTask) return "mount busy with another engagement";
        if (mount._isReloading) return "mount reloading";
        if (mount._vwp._blockedByFlightDeck && mount._baseObject._performingAirOps) return "mount blocked by flight operations";
        return WeaponSelector.isWeaponSystemUsable(mount, ammo, target)
            ? "mount appeared usable but Sea Power queued no gun task" : "native weapon or fire-control check failed";
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
