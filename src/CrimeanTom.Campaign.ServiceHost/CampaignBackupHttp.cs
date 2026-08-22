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
            : RequireDescendant(paths.BackupRoot, request.DestinationDirectory, "backup-destination-outside-root");
        var result = await journal.CreateBackupAsync(destination, cancellationToken);
        return ToItem(result);
    }

    public static async Task<IReadOnlyList<CampaignBackupListItem>> ListAsync(
        CampaignHostPaths paths,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.BackupRoot))
        {
            return [];
        }

        var backupDirectories = Directory.EnumerateDirectories(paths.BackupRoot)
            .Select(directory => Path.Combine(directory, "manifest.json"))
            .Where(File.Exists)
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        var result = new List<CampaignBackupListItem>(backupDirectories.Length);
        foreach (var backupDirectory in backupDirectories)
        {
            result.Add(ToItem(await SqliteCampaignJournal.InspectBackupAsync(backupDirectory, cancellationToken)));
        }
        return result;
    }

    public static async Task<CampaignRestoreView> RestoreAsync(
        CampaignHostPaths paths,
        CampaignRestoreRequest request,
        CancellationToken cancellationToken)
    {
        var backupDirectory = RequireDescendant(paths.BackupRoot, request.BackupDirectory, "backup-source-outside-root");
        var destination = RequireDescendant(paths.RunDirectory, request.DestinationStorageDirectory, "restore-destination-outside-run-directory");
        var result = await SqliteCampaignJournal.RestoreBackupAsync(
            backupDirectory,
            destination,
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

    private static string RequireDescendant(string root, string? candidate, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new CampaignStoreConflict(errorCode, "A directory is required.");
        }
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (relative == "." || Path.IsPathRooted(relative)
            || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict(errorCode, $"Directory must be below {fullRoot}.");
        }
        return fullCandidate;
    }
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
