using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPower.GameBridge;

internal enum WaypointDisposition { Applied, Unchanged, Rejected }

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

    internal static string EngageSurface(Vessel ship, Taskforce side, Vehicle contact)
    {
        if (Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side) || ship._ai == null)
            return "Rejected · ship is unavailable for defensive fire";
        if (!PlayerPicture.IsLive(contact) || contact.BaseObject is not Vessel target || target == ship)
            return "Rejected · target is no longer an eligible surface contact";
        if (PlayerPicture.Ships(side).Any(unit => unit._currentEngageTasks.Any(task => task._targetObject == target)))
            return "Kept · contact already engaged";

        ship._ai.AutoAttackByClick(target, Ammunition.Type.Projectile, salvo: 6, attackAsFormation: false);
        if (!ship._currentEngageTasks.Any(task => task._targetObject == target))
            ship._ai.AutoAttackByClick(target, attackAsFormation: false);
        return ship._currentEngageTasks.Any(task => task._targetObject == target)
            ? "Applied · native engagement queued" : "Rejected · no native weapon could engage";
    }
}
