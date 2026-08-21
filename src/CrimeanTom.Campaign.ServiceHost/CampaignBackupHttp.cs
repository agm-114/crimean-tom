using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed record CampaignHostPaths(string RunDirectory, string BackupRoot);

internal static class CampaignBackupHttp
{
    public static async Task<CampaignBackupListItem> CreateAsync(
        SqliteCampaignJournal journal,
        CampaignHostPaths paths,
        CampaignBackupRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.BackupRoot);
        var destination = string.IsNullOrWhiteSpace(request.DestinationDirectory)
            ? Path.Combine(paths.BackupRoot, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"))
            : Path.GetFullPath(request.DestinationDirectory);
        var result = await journal.CreateBackupAsync(destination, cancellationToken);
        return ToItem(result);
    }

    public static IReadOnlyList<CampaignBackupListItem> List(CampaignHostPaths paths)
    {
        if (!Directory.Exists(paths.BackupRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(paths.BackupRoot)
            .Select(directory => Path.Combine(directory, "manifest.json"))
            .Where(File.Exists)
            .Select(manifest => new CampaignBackupListItem(
                Path.GetDirectoryName(manifest)!,
                DatabaseSha256: "",
                EventCount: 0,
                SnapshotCount: 0,
                ReferencedBlobCount: 0,
                Campaigns: []))
            .ToArray();
    }

    public static async Task<CampaignRestoreView> RestoreAsync(
        CampaignRestoreRequest request,
        CancellationToken cancellationToken)
    {
        var result = await SqliteCampaignJournal.RestoreBackupAsync(
            request.BackupDirectory,
            request.DestinationStorageDirectory,
            cancellationToken);
        return new CampaignRestoreView(
            result.StorageDirectory,
            result.DatabasePath,
            result.BlobDirectory,
            result.Integrity.EventCount,
            result.Integrity.SnapshotCount,
            result.Integrity.ReferencedBlobCount,
            result.Integrity.Campaigns.Select(item => new CampaignBackupHeadView(
                item.CampaignId.ToString(),
                item.CommitPosition.Value,
                item.CanonicalHash)).ToArray());
    }

    private static CampaignBackupListItem ToItem(CampaignBackupResult result) => new(
        result.BackupDirectory,
        result.DatabaseSha256,
        result.Integrity.EventCount,
        result.Integrity.SnapshotCount,
        result.Integrity.ReferencedBlobCount,
        result.Integrity.Campaigns.Select(item => new CampaignBackupHeadView(
            item.CampaignId.ToString(),
            item.CommitPosition.Value,
            item.CanonicalHash)).ToArray());
}

public sealed record CampaignBackupRequest(string? DestinationDirectory);

public sealed record CampaignRestoreRequest(string BackupDirectory, string DestinationStorageDirectory);

public sealed record CampaignBackupListItem(
    string BackupDirectory,
    string DatabaseSha256,
    int EventCount,
    int SnapshotCount,
    int ReferencedBlobCount,
    IReadOnlyList<CampaignBackupHeadView> Campaigns);

public sealed record CampaignBackupHeadView(
    string CampaignId,
    long CommitPosition,
    string CanonicalHash);

public sealed record CampaignRestoreView(
    string StorageDirectory,
    string DatabasePath,
    string BlobDirectory,
    int EventCount,
    int SnapshotCount,
    int ReferencedBlobCount,
    IReadOnlyList<CampaignBackupHeadView> Campaigns);
