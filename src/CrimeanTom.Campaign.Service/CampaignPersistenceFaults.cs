namespace CrimeanTom.Campaign.Service;

public enum CampaignPersistenceBoundary
{
    AppendAfterValidation,
    AppendAfterCommandInsert,
    AppendAfterEventInsert,
    AppendAfterRevisionUpdate,
    AppendAfterHeadUpdate,
    AppendAfterCommitBeforeAcknowledgement,
    SnapshotAfterValidation,
    SnapshotAfterInsert,
    SnapshotAfterCommitBeforeAcknowledgement,
    BlobAfterFileWrite,
    BlobAfterReferenceInsert,
    BlobAfterCommitBeforeAcknowledgement,
    BackupBeforePublication,
    BackupAfterPublicationBeforeAcknowledgement,
}

public interface ICampaignPersistenceFaultInjector
{
    void Reach(CampaignPersistenceBoundary boundary);
}

public sealed class ControlledCampaignPersistenceFaultInjector : ICampaignPersistenceFaultInjector
{
    private int _armedBoundary = -1;

    public void Arm(CampaignPersistenceBoundary boundary) =>
        Interlocked.Exchange(ref _armedBoundary, (int)boundary);

    public void Reach(CampaignPersistenceBoundary boundary)
    {
        if (Interlocked.CompareExchange(ref _armedBoundary, -1, (int)boundary) == (int)boundary)
        {
            throw new CampaignInjectedPersistenceFault(boundary);
        }
    }
}

public sealed class CampaignInjectedPersistenceFault(CampaignPersistenceBoundary boundary)
    : Exception($"Injected campaign persistence fault at {boundary}.")
{
    public CampaignPersistenceBoundary Boundary { get; } = boundary;
}

internal sealed class NullCampaignPersistenceFaultInjector : ICampaignPersistenceFaultInjector
{
    public static readonly NullCampaignPersistenceFaultInjector Instance = new();

    public void Reach(CampaignPersistenceBoundary boundary)
    {
    }
}
