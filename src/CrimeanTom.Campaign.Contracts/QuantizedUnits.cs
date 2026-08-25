namespace CrimeanTom.Campaign.Contracts;

public readonly record struct CampaignTime(long Quanta) : IComparable<CampaignTime>
{
    public const int SimulatedSecondsPerQuantum = 1;
    public static readonly CampaignTime Origin = new(0);

    public CampaignTime Add(CampaignDuration duration) => new(checked(Quanta + duration.Quanta));
    public int CompareTo(CampaignTime other) => Quanta.CompareTo(other.Quanta);
}

public readonly record struct CampaignDuration(long Quanta)
{
    public static CampaignDuration FromQuanta(long quanta) => quanta >= 0
        ? new CampaignDuration(quanta)
        : throw new ArgumentOutOfRangeException(nameof(quanta));
}

public readonly record struct GeoCoordinate(int LatitudeMicrodegrees, int LongitudeMicrodegrees)
{
    public static GeoCoordinate FromMicrodegrees(int latitude, int longitude)
    {
        if (latitude is < -90_000_000 or > 90_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (longitude is < -180_000_000 or > 180_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }

        return new GeoCoordinate(latitude, longitude);
    }
}

public readonly record struct ResourceAmount(long MilliUnits)
{
    public static readonly ResourceAmount Zero = new(0);

    public static ResourceAmount FromMilliUnits(long value) => value >= 0
        ? new ResourceAmount(value)
        : throw new ArgumentOutOfRangeException(nameof(value));
}

public readonly record struct FractionPpm(int PartsPerMillion)
{
    public static readonly FractionPpm Zero = new(0);
    public static readonly FractionPpm One = new(1_000_000);

    public static FractionPpm FromPartsPerMillion(int value) => value is >= 0 and <= 1_000_000
        ? new FractionPpm(value)
        : throw new ArgumentOutOfRangeException(nameof(value));
}

public readonly record struct HeadingMilliDegrees(int Value)
{
    public static HeadingMilliDegrees FromMilliDegrees(int value) => value is >= 0 and < 360_000
        ? new HeadingMilliDegrees(value)
        : throw new ArgumentOutOfRangeException(nameof(value));
}

public readonly record struct SpeedMillimetersPerSecond(long Value)
{
    public static SpeedMillimetersPerSecond FromMillimetersPerSecond(long value) => value >= 0
        ? new SpeedMillimetersPerSecond(value)
        : throw new ArgumentOutOfRangeException(nameof(value));
}

public readonly record struct DistanceMeters(long Value)
{
    public static DistanceMeters FromMeters(long value) => value >= 0
        ? new DistanceMeters(value)
        : throw new ArgumentOutOfRangeException(nameof(value));
}
