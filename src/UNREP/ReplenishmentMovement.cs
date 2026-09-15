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
    internal static float? Demand(ObjectBase unit) => Active.TryGetValue(unit, out var lease) && lease.Owned && lease.hasSpeed ? lease.demand : null;
    internal bool Owned => unit != null && !unit.IsDestroyed && waypoint != null
        && ReferenceEquals(unit.GetLastWaypoint(), waypoint);
    internal void ReleaseSpeed()
    {
        bool restore = hasSpeed && Owned && ReferenceEquals(unit.SpeedCommand.Value, speedCommand);
        hasSpeed = false;
        if (restore) unit.SetSpeedCommand(new ConstantSpeed(speed, unit));
        speedCommand = null;
    }
    private Vector3 Deviation => Owned ? unit.GetLastWaypoint().WaypointPosition.ToUnity()
        - anchor!.transform.forward * (leadNm * 27.559496f) - unit.Position.Value.ToUnity() : Vector3.zero;
    internal float Distance => Owned ? Deviation.magnitude / 27.559496f : float.MaxValue;
    internal float Ahead => Vector3.Dot(Deviation, anchor!.transform.forward) / 27.559496f;
    internal float Across => Vector3.Dot(Deviation, anchor!.transform.right) / 27.559496f;

    internal void Follow(ObjectBase anchor, float lateralNm, float asternNm, string name, float height = 0, float leadNm = 0)
    {
        this.anchor = anchor;
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
        // Native avoidance uses telegraph changes to break mutual collision locks.
        if (unit is Vessel && unit.CollisionAvoidance?.UnitToAvoid != null) return;
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
        if (__instance is Vessel && __instance.CollisionAvoidance?.UnitToAvoid != null) return;
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
    private enum Phase { Rendezvous, Alongside, Breakaway }
    private MovementLease? receiver;
    private int side;
    private Phase phase;
    private float previousHeading = job.Source.Heading.Value;
    private float speedDemand = Math.Max(0, job.Target._velocityInKnots);
    private float bestError = float.MaxValue;
    private double stalledSeconds;
    private double steadySeconds;
    private bool onStation;
    internal bool Released { get; private set; }
    internal bool Failed { get; private set; }
    internal int Side => side;
    internal bool OwnsReceiver => receiver?.Owned == true;
    internal string State { get; private set; } = "Waiting for transfer station";
    private float HeadingError => Math.Abs(Mathf.DeltaAngle(job.Source.Heading.Value, job.Target.Heading.Value));
    private Vector3 Offset => (job.Target.transform.position - job.Source.transform.position) / 27.559496f;
    private float Lateral => Vector3.Dot(Offset, job.Source.transform.right);
    private float Tolerance => (float)SupplySettings.StationToleranceNm;

    internal bool Update(bool ending, double seconds)
    {
        if (Released || seconds <= 0) return false;
        if (receiver == null)
        {
            if (ending) { Released = true; return false; }
            var used = SupplyTransfers.Jobs.Where(other => other != job && !other.Finished && other.Source == job.Source)
                .Select(other => other.Station?.Side ?? 0).ToArray();
            int preferred = Lateral < 0 ? -1 : 1;
            side = new[] { preferred, -preferred }.FirstOrDefault(candidate => !used.Contains(candidate)
                && AlongsideClearance.StationClear(job.Source, job.Target, candidate));
            if (side == 0) { State = "Waiting for a clear station"; return false; }
            receiver = new(job.Target);
            Begin(Phase.Rendezvous);
        }
        if (!receiver.Owned) { Release(); State = "Movement order replaced"; return false; }
        if (ending && phase != Phase.Breakaway) Begin(Phase.Breakaway);

        float sourceSpeed = Math.Max(0, job.Source._velocityInKnots);
        float turnRate = Math.Abs(Mathf.DeltaAngle(previousHeading, job.Source.Heading.Value)) / (float)seconds;
        previousHeading = job.Source.Heading.Value;
        steadySeconds = turnRate <= 0.25f ? steadySeconds + seconds : 0;
        float gap = receiver.Distance;
        float heading = HeadingError;
        float relativeSpeed = (job.Target.transform.forward * job.Target._velocityInKnots
            - job.Source.transform.forward * sourceSpeed).magnitude;
        SetSpeed(sourceSpeed, heading, seconds);

        if (phase == Phase.Breakaway)
        {
            State = "Breaking away";
            // Clearing the lateral boundary is sufficient; do not chase an exact moving point.
            if (side * Lateral >= SupplySettings.BreakawayNm - Tolerance) { Release(); return false; }
        }
        else if (phase == Phase.Rendezvous)
        {
            State = gap > Tolerance ? "Rendezvous" : "Aligning astern";
            if (gap <= Tolerance && heading <= SupplySettings.HeadingTolerance
                && relativeSpeed <= SupplySettings.RelativeSpeedTolerance && steadySeconds >= 5)
            { Begin(Phase.Alongside); return false; }
        }
        else
        {
            // A wider exit boundary avoids repeatedly dropping station at the entry threshold.
            onStation = gap <= Tolerance * (onStation ? 1.5f : 1);
            State = !onStation ? "Taking station" : steadySeconds < 5 || heading > SupplySettings.HeadingTolerance
                ? "Waiting for steady course" : relativeSpeed > SupplySettings.RelativeSpeedTolerance
                ? "Matching speed" : side < 0 ? "Port station" : "Starboard station";
            if (onStation && steadySeconds >= 5 && heading <= SupplySettings.HeadingTolerance
                && relativeSpeed <= SupplySettings.RelativeSpeedTolerance)
                return true;
        }

        if (phase != Phase.Breakaway) return false;
        float error = Math.Max(0, (float)SupplySettings.BreakawayNm - side * Lateral);
        if (error < bestError - 0.005f) { bestError = error; stalledSeconds = 0; }
        else stalledSeconds += seconds;
        if (stalledSeconds >= 300)
        {
            Failed = true;
            State = "Breakaway stalled; orders restored";
            Release();
        }
        return false;
    }

    private void Begin(Phase next)
    {
        phase = next;
        onStation = false;
        ResetProgress();
        float astern = next == Phase.Rendezvous ? (float)SupplySettings.RendezvousNm : 0;
        float lateral = next == Phase.Breakaway ? (float)SupplySettings.BreakawayNm : (float)SupplySettings.StationNm;
        receiver!.Follow(job.Source, side * lateral, astern, next == Phase.Rendezvous ? "UNREP rendezvous"
            : next == Phase.Alongside ? "UNREP alongside" : "UNREP breakaway", leadNm: 0.15f);
    }

    private void SetSpeed(float sourceSpeed, float heading, double seconds)
    {
        float ahead = receiver!.Ahead;
        float across = Math.Abs(receiver.Across);
        float alignment = Vector3.Dot(job.Target.transform.forward, job.Source.transform.forward);
        float closingSpeed = job.Target._velocityInKnots * alignment - sourceSpeed;
        // Compensate for the forward speed lost while steering toward the station.
        float desired = sourceSpeed / Math.Max(0.5f, alignment) + ahead * 10 - closingSpeed * 0.5f;
        bool maneuvering = across > Tolerance || heading > SupplySettings.HeadingTolerance;
        if (maneuvering) desired = Math.Max(desired, 3 + Math.Min(3, across * 10));
        float maximum = Math.Min((float)SupplySettings.MaxSpeed, job.Target.MaxForwardSpeedInKnots);
        desired = Math.Clamp(desired, 0, Math.Max(0, maximum));
        speedDemand = Mathf.MoveTowards(speedDemand, desired, (float)seconds * 0.1f);
        receiver.Speed(speedDemand);
    }

    private void ResetProgress() { bestError = float.MaxValue; stalledSeconds = 0; }
    internal void Release() { receiver?.Restore(); Released = true; }
}

[HarmonyPatch(typeof(ObjectCollisionAvoidance), nameof(ObjectCollisionAvoidance.UnitToAvoid), MethodType.Setter)]
internal static class AlongsideClearance
{
    internal static bool StationClear(Vessel source, Vessel receiver, int side)
    {
        Vector3 station = source.transform.position + source.transform.right * (side * (float)SupplySettings.StationNm * 27.559496f);
        Vector3 hull = receiver.Compartments.getBounds().size;
        return !source._taskforce.PlottingTable.Vehicles.Any(vehicle => vehicle.Object != null
            && vehicle.Object != source && vehicle.Object != receiver && !vehicle.Object.IsAirUnit && !vehicle.Object.IsDestroyed
            && Overlaps(vehicle.Object));

        bool Overlaps(ObjectBase other)
        {
            Vector3 offset = other.transform.position - station;
            Vector3 otherHull = other.Compartments.getBounds().size;
            return Math.Abs(Vector3.Dot(offset, source.transform.right)) < Clearance(source.transform.right, hull.x, other, otherHull)
                && Math.Abs(Vector3.Dot(offset, source.transform.forward)) < Clearance(source.transform.forward, hull.z, other, otherHull);
        }
    }

    [HarmonyPrefix]
    private static void AllowParallelStation(ObjectCollisionAvoidance __instance, ref ObjectBase? __0)
    {
        if (__instance.ParentObjectBase is not Vessel ship || __0 is not Vessel other) return;
        if (!SupplyTransfers.Jobs.Any(job => !job.Finished && job.Station?.OwnsReceiver == true
            && (job.Source == ship && job.Target == other || job.Source == other && job.Target == ship))) return;
        if (Math.Abs(Mathf.DeltaAngle(ship.Heading.Value, other.Heading.Value)) > 30) return;
        if (CorridorsOverlap(ship, other)) return;
        // The scheduler returns one threat. Keep avoidance if removing that partner
        // would hide traffic crossing either ship's projected hull corridor.
        if (ship._taskforce.PlottingTable.Vehicles.Any(vehicle => vehicle.Object != null
            && vehicle.Object != ship && vehicle.Object != other && !vehicle.Object.IsAirUnit && !vehicle.Object.IsDestroyed
            && (CorridorsOverlap(ship, vehicle.Object) || CorridorsOverlap(other, vehicle.Object)))) return;
        __0 = null;
    }

    internal static bool CorridorsOverlap(Vessel ship, ObjectBase other)
    {
        Vector3 offset = other.transform.position - ship.transform.position;
        Vector3 velocity = (other.transform.forward * other._velocityInKnots
            - ship.transform.forward * ship._velocityInKnots) * 0.0076554087f;
        Vector3 hull = ship.Compartments.getBounds().size;
        Vector3 otherHull = other.Compartments.getBounds().size;
        float start = 0, end = 90;
        return Intersects(ship.transform.right, hull.x) && Intersects(ship.transform.forward, hull.z);

        bool Intersects(Vector3 axis, float size)
        {
            float extent = Clearance(axis, size, other, otherHull);
            float distance = Vector3.Dot(offset, axis), speed = Vector3.Dot(velocity, axis);
            if (Math.Abs(speed) < 0.00001f) return Math.Abs(distance) <= extent;
            float first = (-extent - distance) / speed, last = (extent - distance) / speed;
            start = Math.Max(start, Math.Min(first, last));
            end = Math.Min(end, Math.Max(first, last));
            return start <= end;
        }
    }
    private static float Clearance(Vector3 axis, float size, ObjectBase other, Vector3 hull) =>
        (size + Math.Abs(Vector3.Dot(other.transform.right, axis)) * hull.x
            + Math.Abs(Vector3.Dot(other.transform.forward, axis)) * hull.z) / 2 + 0.015f * 27.559496f;
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
            if (ship.CollisionAvoidance?.UnitToAvoid != null) continue;
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
