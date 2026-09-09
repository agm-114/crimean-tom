using System.Globalization;

namespace CrimeanTom.SeaPower;

internal record MissionPosition(float X, float Altitude, float Z, string? Depth = null)
{
    internal string ToMissionValue(char separator = ',') => FormattableString.Invariant(
        $"{X:0.##}{separator}{Depth ?? Altitude.ToString("0", CultureInfo.InvariantCulture)}{separator}{Z:0.##}");
}

internal record MissionWaypoint(MissionPosition Position, WaypointOrder? Order = null)
{
    internal MissionWaypoint Transform(Func<MissionPosition, MissionPosition> transform) => new(transform(Position),
        Order is AttackAtWaypointOrder attack ? attack with { Target = transform(attack.Target) } : Order);

    internal string ToMissionValue() => Position.ToMissionValue() +
        (Order is null ? string.Empty : $"/{Order.ToMissionValue()}");

    internal static string FormatRoute(IEnumerable<MissionWaypoint> waypoints) =>
        string.Join('|', waypoints.Select(waypoint => waypoint.ToMissionValue()));
}

internal abstract record WaypointOrder
{
    internal static WaypointOrder WeaponsFree { get; } = new SetWeaponStatusOrder("Free");
    internal abstract string ToMissionValue();
}

internal record SetWeaponStatusOrder(string Status) : WaypointOrder
{
    internal override string ToMissionValue() => $"SetWeaponStatus,{Status}";
}

internal record AttackAtWaypointOrder(string Ammunition, MissionPosition Target, int Salvos = 1) : WaypointOrder
{
    internal override string ToMissionValue() => FormattableString.Invariant(
        $"AttackAtWaypoint,{Ammunition},{Target.ToMissionValue(':')},{Salvos}");
}
