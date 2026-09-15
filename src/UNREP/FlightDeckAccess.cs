using System.Reflection;
using SeaPower;
using UnityEngine;

namespace UNREP;

// Some Sea Power builds omit the traffic/zone managers. Keep those calls optional
// while retaining point claims, native blocking links and physical deck clearance.
internal static class FlightDeckAccess
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo? Traffic = typeof(FlightDeck).GetField("_trafficManager", Members);
    private static readonly FieldInfo? Zones = typeof(FlightDeck).GetField("_zoneManager", Members);
    private static readonly MethodInfo? Release = Traffic?.FieldType.GetMethod("ReleaseVehicle", Members, null, [typeof(ObjectBase)], null);
    private static readonly MethodInfo? Claim = Traffic?.FieldType.GetMethod("ForceClaimCorridor", Members, null,
        [typeof(ObjectBase), typeof(Vector3), typeof(Vector3)], null);
    private static readonly MethodInfo? Occupied = Traffic?.FieldType.GetMethod("IsRunwayZoneOccupied", Members, null,
        [typeof(Vector3), typeof(ObjectBase)], null);
    private static readonly MethodInfo? Runway = Zones?.FieldType.GetMethod("RunwayContaining", Members, null, [typeof(Vector3)], null);
    private static readonly MethodInfo? Conflict = Zones?.FieldType.GetMethod("RunwayOrConflictContains", Members, null,
        [typeof(DeckZone), typeof(Vector3)], null);
    private static readonly MethodInfo? Strip = typeof(FlightDeck).GetMethod("RecoveryStripClear", Members, null,
        [typeof(RecoveryPoint), typeof(ObjectBase)], null);

    internal static void ReleaseVehicle(FlightDeck deck, ObjectBase aircraft)
    {
        if (Traffic?.GetValue(deck) is { } traffic) Release?.Invoke(traffic, [aircraft]);
    }

    internal static void ClaimCorridor(FlightDeck deck, ObjectBase aircraft, RecoveryPoint point)
    {
        if (Traffic?.GetValue(deck) is { } traffic) Claim?.Invoke(traffic, [aircraft, point._position, point._exitPosition]);
    }

    internal static bool CorridorClear(FlightDeck deck, RecoveryPoint point, ObjectBase aircraft)
    {
        bool clear = Strip != null ? (bool)Strip.Invoke(deck, [point, aircraft])! : StripClear(deck, point, aircraft);
        return clear && !(Traffic?.GetValue(deck) is { } traffic && Occupied?.Invoke(traffic, [point._position, aircraft]) is true);
    }

    internal static bool ZonesConflict(FlightDeck deck, Vector3 first, Vector3 second)
    {
        if (Zones?.GetValue(deck) is not { } zones) return false;
        var firstRunway = Runway?.Invoke(zones, [first]);
        var secondRunway = Runway?.Invoke(zones, [second]);
        return (firstRunway != null && Conflict?.Invoke(zones, [firstRunway, second]) is true)
            || (secondRunway != null && Conflict?.Invoke(zones, [secondRunway, first]) is true);
    }

    private static bool StripClear(FlightDeck deck, RecoveryPoint point, ObjectBase aircraft)
    {
        // Match the native strip check's no-zone branch: two swept segments and
        // aircraft half-wingspans, including parked/taxiing aircraft attached to the ship.
        Vector3 start = point._position;
        float length = Vector3.ProjectOnPlane(point._exitPosition - start, Vector3.up).magnitude;
        Vector3 end = start + Quaternion.Euler(0, point._rotation, 0) * Vector3.forward * length;
        foreach (var other in deck._baseObject._childAircraft)
        {
            if (other == null || other == aircraft || other.IsDestroyed
                || !other.transform.IsChildOf(deck._baseObject.transform)) continue;
            Vector3 position = deck._baseObject.transform.InverseTransformPoint(other.transform.position);
            float radius = HoldRadius(other) + HoldRadius(aircraft);
            if (DeckZone.SegPointDistSqXZ(start, end, position) < radius * radius
                || DeckZone.SegPointDistSqXZ(end, point._exitPosition, position) < radius * radius) return false;
        }
        return true;
    }

    private static float HoldRadius(ObjectBase aircraft) => Utils.heightInMeterToUnity(
        aircraft._obp is AircraftParameters { _wingSpan: > 1 } parameters ? parameters._wingSpan * 0.5f : 10);
}
