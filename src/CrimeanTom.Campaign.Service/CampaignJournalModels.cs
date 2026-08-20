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

public enum CampaignBlobPurpose
{
    TacticalCheckpoint,
    Catalog,
}

public sealed record ContentAddressedBlob(
    string Sha256,
    long Length,
    bool Duplicate);

public sealed record CampaignBlobReference(
    CampaignId CampaignId,
    CampaignBlobPurpose Purpose,
    string LogicalId,
    CommitPosition CommitPosition,
    string Sha256,
    long Length,
    bool Duplicate);

public sealed record CampaignIntegrityHead(
    CampaignId CampaignId,
    CommitPosition CommitPosition,
    string CanonicalHash);

public sealed record CampaignIntegrityReport(
    IReadOnlyList<CampaignIntegrityHead> Campaigns,
    int EventCount,
    int SnapshotCount,
    int ReferencedBlobCount);

public sealed record CampaignBackupResult(
    string BackupDirectory,
    string DatabaseSha256,
    CampaignIntegrityReport Integrity);

public sealed record CampaignRestoreResult(
    string StorageDirectory,
    string DatabasePath,
    string BlobDirectory,
    CampaignIntegrityReport Integrity);

public sealed class CampaignStoreConflict(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
