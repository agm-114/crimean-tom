namespace CrimeanTom.Campaign.Transport.V1;

public static class CampaignWorkerKind
{
    public const string SeaPower = "sea-power";
    public const string Operational = "operational";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), SeaPower, StringComparison.Ordinal))
        {
            return SeaPower;
        }

        if (string.Equals(value.Trim(), Operational, StringComparison.Ordinal))
        {
            return Operational;
        }

        return string.Empty;
    }

    public static bool IsOperational(string? value) =>
        string.Equals(Normalize(value), Operational, StringComparison.Ordinal);

    public static bool IsSeaPower(string? value) =>
        string.Equals(Normalize(value), SeaPower, StringComparison.Ordinal);

    public static bool PlayerSessionBound(string? value) => IsSeaPower(value);

    public static bool TryValidate(string? value, out string kind)
    {
        kind = Normalize(value);
        return kind.Length > 0;
    }
}
