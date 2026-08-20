using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Service;

public sealed record CampaignWriterLease(
    CampaignId CampaignId,
    CoordinatorId CoordinatorId,
    WriterEpoch WriterEpoch,
    DateTimeOffset ExpiresAt);

public sealed record CampaignAppendResult(
    CommittedCampaignEvent CampaignEvent,
    bool Duplicate);

public sealed record CampaignSnapshotInfo(
    CampaignId CampaignId,
    CommitPosition CommitPosition,
    string StateHash,
    bool Duplicate);

public sealed record CampaignRecoveryResult(
    CampaignState State,
    CommitPosition SnapshotPosition,
    string CanonicalHash);

public sealed class CampaignStoreConflict(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
