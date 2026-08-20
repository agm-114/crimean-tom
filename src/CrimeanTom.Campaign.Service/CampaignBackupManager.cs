using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrimeanTom.Campaign.Contracts;
using Microsoft.Data.Sqlite;

namespace CrimeanTom.Campaign.Service;

internal static class CampaignBackupManager
{
    private const int BackupFormatVersion = 1;
    private const string DatabaseFileName = "campaign.db";
    private const string BlobDirectoryName = "blobs";
    private const string ManifestFileName = "manifest.json";

    public static async Task<CampaignBackupResult> CreateAsync(
        string sourceDatabasePath,
        ContentAddressedBlobStore sourceBlobs,
        string destinationDirectory,
        ICampaignPersistenceFaultInjector faultInjector,
        CancellationToken cancellationToken)
    {
        var destination = RequireNewDirectory(destinationDirectory);
        var staging = CreateStagingPath(destination);
        Directory.CreateDirectory(staging);
        try
        {
            var backupDatabase = Path.Combine(staging, DatabaseFileName);
            await CopyDatabaseOnlineAsync(sourceDatabasePath, backupDatabase, cancellationToken);
            var backupBlobs = new ContentAddressedBlobStore(Path.Combine(staging, BlobDirectoryName));
            var descriptors = await ReadBlobDescriptorsAsync(backupDatabase, cancellationToken);
            foreach (var descriptor in descriptors)
            {
                await sourceBlobs.CopyVerifiedToAsync(backupBlobs, descriptor.Sha256, descriptor.Length, cancellationToken);
            }

            var backupJournal = new SqliteCampaignJournal(backupDatabase, backupBlobs.RootDirectory);
            await backupJournal.InitializeAsync(cancellationToken);
            var integrity = await backupJournal.VerifyIntegrityAsync(cancellationToken);
            var databaseHash = await HashFileAsync(backupDatabase, cancellationToken);
            var manifest = new BackupManifestDocument
            {
                FormatVersion = BackupFormatVersion,
                DatabaseFile = DatabaseFileName,
                DatabaseSha256 = databaseHash,
                Campaigns = integrity.Campaigns.Select(item => new BackupCampaignDocument
                {
                    CampaignId = item.CampaignId.ToString(),
                    CommitPosition = item.CommitPosition.Value,
                    CanonicalHash = item.CanonicalHash,
                }).ToArray(),
                Blobs = descriptors.Select(item => new BackupBlobDocument
                {
                    Sha256 = item.Sha256,
                    Length = item.Length,
                }).ToArray(),
            };
            await WriteDurableFileAsync(
                Path.Combine(staging, ManifestFileName),
                JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                cancellationToken);

            faultInjector.Reach(CampaignPersistenceBoundary.BackupBeforePublication);
            Directory.Move(staging, destination);
            faultInjector.Reach(CampaignPersistenceBoundary.BackupAfterPublicationBeforeAcknowledgement);
            return new CampaignBackupResult(destination, databaseHash, integrity);
        }
        finally
        {
            DeleteStagingDirectory(staging);
        }
    }

    public static async Task<CampaignRestoreResult> RestoreAsync(
        string backupDirectory,
        string destinationStorageDirectory,
        CancellationToken cancellationToken)
    {
        var backup = Path.GetFullPath(backupDirectory);
        if (!Directory.Exists(backup))
        {
            throw Conflict("backup-missing", "The backup directory does not exist.");
        }
        var destination = RequireNewDirectory(destinationStorageDirectory);
        var manifest = await ReadAndVerifyManifestAsync(backup, cancellationToken);
        var backupDatabase = Path.Combine(backup, DatabaseFileName);
        var backupBlobs = new ContentAddressedBlobStore(Path.Combine(backup, BlobDirectoryName));
        foreach (var blob in manifest.Blobs)
        {
            await backupBlobs.VerifyAsync(blob.Sha256, blob.Length, cancellationToken);
        }

        var backupJournal = new SqliteCampaignJournal(backupDatabase, backupBlobs.RootDirectory);
        await backupJournal.InitializeAsync(cancellationToken);
        var backupIntegrity = await backupJournal.VerifyIntegrityAsync(cancellationToken);
        RequireManifestHeads(manifest, backupIntegrity);

        var staging = CreateStagingPath(destination);
        Directory.CreateDirectory(staging);
        try
        {
            var restoredDatabase = Path.Combine(staging, DatabaseFileName);
            await CopyFileDurablyAsync(backupDatabase, restoredDatabase, cancellationToken);
            var restoredBlobs = new ContentAddressedBlobStore(Path.Combine(staging, BlobDirectoryName));
            foreach (var blob in manifest.Blobs)
            {
                await backupBlobs.CopyVerifiedToAsync(restoredBlobs, blob.Sha256, blob.Length, cancellationToken);
            }

            await FenceRestoredWritersAsync(restoredDatabase, cancellationToken);
            var restoredJournal = new SqliteCampaignJournal(restoredDatabase, restoredBlobs.RootDirectory);
            await restoredJournal.InitializeAsync(cancellationToken);
            var restoredIntegrity = await restoredJournal.VerifyIntegrityAsync(cancellationToken);
            RequireEquivalentCampaignHeads(backupIntegrity, restoredIntegrity);

            Directory.Move(staging, destination);
            return new CampaignRestoreResult(
                destination,
                Path.Combine(destination, DatabaseFileName),
                Path.Combine(destination, BlobDirectoryName),
                restoredIntegrity);
        }
        finally
        {
            DeleteStagingDirectory(staging);
        }
    }

    private static async Task CopyDatabaseOnlineAsync(
        string sourceDatabasePath,
        string destinationDatabasePath,
        CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(BuildConnectionString(sourceDatabasePath, SqliteOpenMode.ReadOnly));
        await using var destination = new SqliteConnection(BuildConnectionString(destinationDatabasePath, SqliteOpenMode.ReadWriteCreate));
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
    }

    private static async Task<IReadOnlyList<BlobDescriptor>> ReadBlobDescriptorsAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT blobs.content_hash, blobs.content_length FROM blobs
            JOIN campaign_blob_refs USING (content_hash) ORDER BY blobs.content_hash;
            """;
        var result = new List<BlobDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new BlobDescriptor(reader.GetString(0), reader.GetInt64(1)));
        }
        return result;
    }

    private static async Task<BackupManifestDocument> ReadAndVerifyManifestAsync(
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(backupDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw Conflict("backup-manifest-missing", "The backup manifest is missing.");
        }
        BackupManifestDocument manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifestDocument>(
                await File.ReadAllBytesAsync(manifestPath, cancellationToken), JsonOptions)
                ?? throw new JsonException("The backup manifest was null.");
        }
        catch (JsonException exception)
        {
            throw Conflict("backup-manifest-invalid", exception.Message);
        }
        if (manifest.FormatVersion != BackupFormatVersion || manifest.DatabaseFile != DatabaseFileName
            || manifest.Campaigns.Length == 0 || manifest.Campaigns.Select(item => item.CampaignId).Distinct().Count() != manifest.Campaigns.Length
            || manifest.Blobs.Select(item => item.Sha256).Distinct().Count() != manifest.Blobs.Length)
        {
            throw Conflict("backup-manifest-invalid", "The backup manifest version, identity, or uniqueness constraints failed.");
        }

        var databasePath = Path.Combine(backupDirectory, manifest.DatabaseFile);
        if (!File.Exists(databasePath)
            || !FixedHashEquals(await HashFileAsync(databasePath, cancellationToken), manifest.DatabaseSha256))
        {
            throw Conflict("backup-database-corrupt", "The backup database does not match its manifest hash.");
        }
        return manifest;
    }

    private static void RequireManifestHeads(BackupManifestDocument manifest, CampaignIntegrityReport integrity)
    {
        var expected = manifest.Campaigns.OrderBy(item => item.CampaignId).ToArray();
        var actual = integrity.Campaigns.OrderBy(item => item.CampaignId).ToArray();
        if (expected.Length != actual.Length)
        {
            throw Conflict("backup-manifest-conflict", "The manifest and database contain different campaigns.");
        }
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].CampaignId != actual[index].CampaignId.ToString()
                || expected[index].CommitPosition != actual[index].CommitPosition.Value
                || expected[index].CanonicalHash != actual[index].CanonicalHash)
            {
                throw Conflict("backup-manifest-conflict", "A manifest campaign head differs from the backup database.");
            }
        }
    }

    private static void RequireEquivalentCampaignHeads(CampaignIntegrityReport expected, CampaignIntegrityReport actual)
    {
        var left = expected.Campaigns.OrderBy(item => item.CampaignId).ToArray();
        var right = actual.Campaigns.OrderBy(item => item.CampaignId).ToArray();
        if (left.Length != right.Length || left.Where((item, index) => item != right[index]).Any())
        {
            throw Conflict("restore-head-conflict", "The restored campaign heads differ from the verified backup.");
        }
    }

    private static async Task FenceRestoredWritersAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadWrite));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE campaigns SET writer_epoch = writer_epoch + 1,
                writer_owner = NULL, writer_lease_expires_unix_ms = NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task CopyFileDurablyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static Task WriteDurableFileAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        WriteDurableFileCoreAsync(path, content, cancellationToken);

    private static async Task WriteDurableFileCoreAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string BuildConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(path),
        Mode = mode,
        Pooling = false,
        ForeignKeys = true,
        DefaultTimeout = 30,
    }.ToString();

    private static string RequireNewDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A directory is required.", nameof(path));
        }
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw Conflict("destination-exists", $"Destination already exists: {fullPath}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static string CreateStagingPath(string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);
        return Path.Combine(parent, $".{name}.staging-{Guid.NewGuid():N}");
    }

    private static void DeleteStagingDirectory(string staging)
    {
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static bool FixedHashEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static CampaignStoreConflict Conflict(string code, string message) => new(code, message);

    private sealed record BlobDescriptor(string Sha256, long Length);

    private sealed class BackupManifestDocument
    {
        public required int FormatVersion { get; init; }
        public required string DatabaseFile { get; init; }
        public required string DatabaseSha256 { get; init; }
        public required BackupCampaignDocument[] Campaigns { get; init; }
        public required BackupBlobDocument[] Blobs { get; init; }
    }

    private sealed class BackupCampaignDocument
    {
        public required string CampaignId { get; init; }
        public required long CommitPosition { get; init; }
        public required string CanonicalHash { get; init; }
    }

    private sealed class BackupBlobDocument
    {
        public required string Sha256 { get; init; }
        public required long Length { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
