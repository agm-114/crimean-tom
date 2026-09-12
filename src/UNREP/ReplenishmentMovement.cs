using SeaPower;
using UnityEngine;
using HarmonyLib;

namespace UNREP;

// Own only the waypoint and speed commands we issue. Player replacement orders win.
[HarmonyPatch]
internal class MovementLease(ObjectBase unit)
{
    private static readonly Dictionary<ObjectBase, MovementLease> Active = [];
    private readonly List<WaypointData> route = unit.ExportWaypoints();
    private readonly float speed = unit.SpeedCommand.Value?.GetDemandedSpeedInKnots(unit) ?? 0;
    private readonly UnitFormation? formation = unit.Formation;
    private readonly bool independent = unit.Formation?.GetStationForUnit(unit)?.UnitActsIndependently.Value ?? true;
    private object? waypoint;
    private ISpeedCommand? speedCommand;
    private ObjectBase? anchor;
    private float leadNm;
    private float demand;
    private bool hasSpeed;
    private bool nativeOrders;
    internal static float? Demand(ObjectBase unit) => Active.TryGetValue(unit, out var lease) && lease.Owned && lease.hasSpeed ? lease.demand : null;
    internal bool Owned => unit != null && !unit.IsDestroyed && waypoint != null
        && (ReferenceEquals(unit.GetLastWaypoint(), waypoint) || nativeOrders && unit.GetNumberOfWaypoints() == 0);
    internal void ReleaseSpeed() => hasSpeed = false;
    internal void TrackNativeOrders() { waypoint = unit.GetLastWaypoint(); nativeOrders = true; }
    private Vector3 Deviation => Owned ? unit.GetLastWaypoint().WaypointPosition.ToUnity()
        - anchor!.transform.forward * (leadNm * 27.559496f) - unit.Position.Value.ToUnity() : Vector3.zero;
    internal float Distance => Owned ? Deviation.magnitude / 27.559496f : float.MaxValue;
    internal float Ahead => Vector3.Dot(Deviation, anchor!.transform.forward) / 27.559496f;

    internal void Follow(ObjectBase anchor, float lateralNm, float asternNm, string name, float height = 0, float leadNm = 0)
    {
        this.anchor = anchor;
        nativeOrders = false;
        this.leadNm = leadNm;
        Active[unit] = this;
        unit.Formation?.MakeUnitActIndependently(unit);
        unit.RemoveWaypoints();
        unit.SetRelativeWaypointTask(new Vector3(lateralNm * 27.559496f, height, (leadNm - asternNm) * 27.559496f), anchor,
            name, reachable: false, setHeight: height > 0);
        waypoint = unit.GetLastWaypoint();
    }

    internal void Speed(float knots)
    {
        demand = knots;
        hasSpeed = true;
        if (speedCommand != null && ReferenceEquals(unit.SpeedCommand.Value, speedCommand)
            && Math.Abs(speedCommand.GetDemandedSpeedInKnots(unit) - knots) < 0.2) return;
        speedCommand = new ConstantSpeed(knots, unit);
        unit.SetSpeedCommand(speedCommand);
    }

    internal void Restore()
    {
        Active.Remove(unit);
        if (!Owned) return;
        unit.RemoveWaypoints();
        unit.ImportWaypoints(route);
        if (ReferenceEquals(unit.SpeedCommand.Value, speedCommand)) unit.SetSpeedCommand(new ConstantSpeed(speed, unit));
        if (!independent && unit.Formation == formation) formation?.ReturnToFormation(unit);
        waypoint = null;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.SetSpeedCommand))]
    private static void KeepStationSpeed(ObjectBase __instance, ref ISpeedCommand speedCommand)
    {
        if (!Active.TryGetValue(__instance, out var lease) || !lease.Owned || !lease.hasSpeed) return;
        if (Math.Abs(speedCommand.GetDemandedSpeedInKnots(__instance) - lease.demand) > 0.01f)
        {
            (speedCommand as IDisposable)?.Dispose();
            speedCommand = new ConstantSpeed(lease.demand, __instance);
        }
        lease.speedCommand = speedCommand;
    }
}

internal class AlongsideStation(SupplyTransfer job)
{
    private MovementLease? receiver;
    private int side;
    private int phase;
    private float previousHeading = job.Source.Heading.Value;
    internal bool Released { get; private set; }
    internal int Side => side;
    internal bool OwnsReceiver => receiver?.Owned == true;
    internal string State { get; private set; } = "Waiting for transfer station";

    internal bool Update(bool ending)
    {
        if (Released) return false;
        if (receiver == null)
        {
            if (ending) { Released = true; return false; }
            var used = SupplyTransfers.Jobs.Where(other => other != job && !other.Finished && other.Source == job.Source)
                .Select(other => other.Station?.Side ?? 0).ToArray();
            side = !used.Contains(-1) ? -1 : !used.Contains(1) ? 1 : 0;
            if (side == 0) return false;
            receiver = new(job.Target);
            receiver.Follow(job.Source, side * (float)SupplySettings.StationNm, (float)SupplySettings.RendezvousNm, "UNREP rendezvous", leadNm: 0.15f);
        }
        if (!receiver.Owned) { receiver.Restore(); Released = true; State = "Movement order replaced"; return false; }
        if (ending && phase != 2)
        {
            phase = 2;
            receiver.Follow(job.Source, side * (float)SupplySettings.BreakawayNm, 0, "UNREP breakaway", leadNm: 0.15f);
        }
        float sourceSpeed = Math.Max(0, job.Source._velocityInKnots);
        float gap = receiver.Distance;
        receiver.Speed(Math.Clamp(sourceSpeed + receiver.Ahead * 10, 0, (float)SupplySettings.MaxSpeed));
        if (phase == 2)
        {
            State = "Breaking away";
            if (gap < SupplySettings.StationToleranceNm) { receiver.Restore(); Released = true; }
            return false;
        }
        if (phase == 0)
        {
            State = "Rendezvous";
            if (gap < SupplySettings.StationToleranceNm)
            {
                phase = 1;
                receiver.Follow(job.Source, side * (float)SupplySettings.StationNm, 0, "UNREP alongside", leadNm: 0.15f);
            }
            return false;
        }
        float turn = Math.Abs(Mathf.DeltaAngle(previousHeading, job.Source.Heading.Value));
        previousHeading = job.Source.Heading.Value;
        if (gap > SupplySettings.StationToleranceNm) { State = "Taking station"; return false; }
        receiver.Speed(sourceSpeed);
        if (Math.Abs(Mathf.DeltaAngle(job.Source.Heading.Value, job.Target.Heading.Value)) > SupplySettings.HeadingTolerance
            || turn > 0.5f) { State = "Waiting for steady course"; return false; }
        if (Math.Abs(sourceSpeed - job.Target._velocityInKnots) > SupplySettings.RelativeSpeedTolerance)
        { State = "Matching speed"; return false; }
        State = side < 0 ? "Port station" : "Starboard station";
        return true;
    }

    internal void Release() { receiver?.Restore(); Released = true; }
}

[HarmonyPatch(typeof(ObjectCollisionAvoidance), nameof(ObjectCollisionAvoidance.UnitToAvoid), MethodType.Setter)]
internal static class AlongsideClearance
{
    [HarmonyPrefix]
    private static void AllowParallelStation(ObjectCollisionAvoidance __instance, ref ObjectBase? __0)
    {
        if (__instance.ParentObjectBase is not Vessel ship || __0 is not Vessel other) return;
        if (!SupplyTransfers.Jobs.Any(job => !job.Finished && job.Station?.OwnsReceiver == true
            && (job.Source == ship && job.Target == other || job.Source == other && job.Target == ship))) return;
        if (Math.Abs(Mathf.DeltaAngle(ship.Heading.Value, other.Heading.Value)) > 30) return;
        var separation = other.transform.position - ship.transform.position;
        float clearance = (ship.Compartments.getBounds().size.x + other.Compartments.getBounds().size.x) / 2 + 0.015f * 27.559496f;
        if (Math.Abs(Vector3.Dot(separation, ship.transform.right)) < clearance) return;
        // Vanilla reacts four hull lengths away. Permit our parallel partner,
        // but retain avoidance when any third unit is in the immediate corridor.
        float radius = Math.Max(ship.Compartments.getBounds().size.z, other.Compartments.getBounds().size.z) * 4;
        if (ship._taskforce.PlottingTable.Vehicles.Any(vehicle => vehicle.Object != null
            && vehicle.Object != ship && vehicle.Object != other && !vehicle.Object.IsAirUnit && !vehicle.Object.IsDestroyed
            && Vector3.Distance(vehicle.Object.transform.position, ship.transform.position) < radius)) return;
        __0 = null;
    }
}

internal static class SupplySpeedLimits
{
    private static readonly Dictionary<Vessel, (float Original, ISpeedCommand Command)> Limits = [];
    internal static void Update()
    {
        var sources = SupplyTransfers.Jobs.Where(job => !job.Finished && job.Station != null).Select(job => job.Source).Distinct().ToArray();
        foreach (var ship in Limits.Keys.Except(sources).ToArray())
        {
            var previous = Limits[ship];
            if (ship != null && !ship.IsDestroyed && ReferenceEquals(ship.SpeedCommand.Value, previous.Command))
                ship.SetSpeedCommand(new ConstantSpeed(previous.Original, ship));
            Limits.Remove(ship!);
        }
        foreach (var ship in sources.Where(SupplyTransfers.Available))
        {
            if (!Limits.TryGetValue(ship, out var previous))
                previous = (ship.SpeedCommand.Value?.GetDemandedSpeedInKnots(ship) ?? 0, null!);
            if (ReferenceEquals(ship.SpeedCommand.Value, previous.Command)) continue;
            // Keep the supplier's course and route; cap only its demanded speed.
            float demand = ship.SpeedCommand.Value?.GetDemandedSpeedInKnots(ship) ?? 0;
            var command = new ConstantSpeed(Math.Clamp(demand, 0, (float)SupplySettings.UnrepSpeed), ship);
            ship.SetSpeedCommand(command);
            Limits[ship] = (previous.Original, command);
        }
    }
}
