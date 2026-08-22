namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignWorkerHealth
{
    public const string Healthy = "Healthy";
    public const string Pressured = "Pressured";
    public const string Overloaded = "Overloaded";
    public const string Draining = "Draining";
    public const string Quarantined = "Quarantined";
    public const string Offline = "Offline";

    public const string Eligible = "Eligible";
    public const string DrainOnly = "DrainOnly";
    public const string Disabled = "Disabled";

    public static string NormalizeHealth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Healthy;
        }

        return value.Trim() switch
        {
            Healthy or Pressured or Overloaded or Draining or Quarantined or Offline => value.Trim(),
            _ => Quarantined,
        };
    }

    public static string NormalizeEligibility(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Eligible;
        }

        return value.Trim() switch
        {
            Eligible or DrainOnly or Disabled or Quarantined => value.Trim(),
            _ => Quarantined,
        };
    }

    public static bool CanReceiveNewAssignments(CampaignSessionSnapshot worker) =>
        string.Equals(NormalizeEligibility(worker.Eligibility), Eligible, StringComparison.Ordinal)
        && string.Equals(NormalizeHealth(worker.Health), Healthy, StringComparison.Ordinal);

    public static bool CanReceiveShadow(CampaignSessionSnapshot worker) =>
        CanReceiveNewAssignments(worker) && worker.OwnedRecoveryRequiredEntityCount == 0;

    public static bool MustEvacuate(CampaignSessionSnapshot worker)
    {
        var health = NormalizeHealth(worker.Health);
        var eligibility = NormalizeEligibility(worker.Eligibility);
        return health is Draining or Quarantined or Offline
            || eligibility != Eligible;
    }

    public static bool MustLoadShed(CampaignSessionSnapshot worker) =>
        MustEvacuate(worker)
        || string.Equals(NormalizeHealth(worker.Health), Overloaded, StringComparison.Ordinal);
}
