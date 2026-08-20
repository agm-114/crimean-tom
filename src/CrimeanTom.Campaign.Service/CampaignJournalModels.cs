using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.Service;

public sealed record CampaignWriterLease(
    CampaignId CampaignId,
    CoordinatorId CoordinatorId,
    WriterEpoch WriterEpoch,
    DateTimeOffset ExpiresAt);

public sealed record CampaignAppendResult(
    CommittedCampaignEvent CampaignEvent,
    bool Duplicate);

public sealed class CampaignStoreConflict(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
