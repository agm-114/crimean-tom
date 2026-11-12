namespace CrimeanTom.SeaPower;

internal record ChartPosition(double Latitude, double Longitude)
{
    // Sea Power's mission grid uses 60 NM per degree on both axes, without a cosine correction.
    internal MissionPosition RelativeTo(ChartPosition center) => new(
        (float)((Longitude - center.Longitude) * 60), 0,
        (float)((Latitude - center.Latitude) * 60));
    internal ChartPosition Offset(MissionPosition position) => new(Latitude + position.Z / 60,
        Longitude + position.X / 60);
    internal double DistanceTo(ChartPosition other)
    {
        MissionPosition delta = other.RelativeTo(this);
        return Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
    }
    internal ChartPosition Towards(ChartPosition other, double fraction) =>
        new(Latitude + (other.Latitude - Latitude) * fraction, Longitude + (other.Longitude - Longitude) * fraction);
    internal string ToMissionValue() => FormattableString.Invariant($"{Latitude:0.######},{Longitude:0.######}");
}
