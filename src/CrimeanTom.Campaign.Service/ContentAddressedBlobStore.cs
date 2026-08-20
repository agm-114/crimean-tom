using System.Security.Cryptography;

namespace CrimeanTom.Campaign.Service;

public sealed class ContentAddressedBlobStore
{
    private readonly string _rootDirectory;

    public ContentAddressedBlobStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A blob root directory is required.", nameof(rootDirectory));
        }
        _rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public async Task<ContentAddressedBlob> PutAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
        var destination = GetPath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            await VerifyAsync(hash, content.Length, cancellationToken);
            return new ContentAddressedBlob(hash, content.Length, Duplicate: true);
        }

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(temporary);
            }
            await VerifyAsync(hash, content.Length, cancellationToken);
            return new ContentAddressedBlob(hash, content.Length, Duplicate: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<byte[]> ReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var path = GetPath(sha256);
        if (!File.Exists(path))
        {
            throw new CampaignStoreConflict("blob-missing", $"Blob {sha256} is missing.");
        }
        var content = await File.ReadAllBytesAsync(path, cancellationToken);
        await VerifyAsync(sha256, content.LongLength, cancellationToken);
        return content;
    }

    public async Task VerifyAsync(
        string sha256,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(sha256);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new CampaignStoreConflict("blob-missing", $"Blob {sha256} is missing.");
        }
        if (info.Length != expectedLength)
        {
            throw new CampaignStoreConflict("blob-corrupt", $"Blob {sha256} has the wrong length.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(sha256),
                Convert.FromHexString(actual)))
        {
            throw new CampaignStoreConflict("blob-corrupt", $"Blob {sha256} failed content verification.");
        }
    }

    public async Task CopyVerifiedToAsync(
        ContentAddressedBlobStore destination,
        string sha256,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await VerifyAsync(sha256, expectedLength, cancellationToken);
        var sourcePath = GetPath(sha256);
        var destinationPath = destination.GetPath(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!File.Exists(destinationPath))
        {
            var temporary = Path.Combine(Path.GetDirectoryName(destinationPath)!, $".{sha256}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65_536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(target, cancellationToken);
                    await target.FlushAsync(cancellationToken);
                    target.Flush(flushToDisk: true);
                }
                try
                {
                    File.Move(temporary, destinationPath);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        await destination.VerifyAsync(sha256, expectedLength, cancellationToken);
    }

    public string GetPath(string sha256)
    {
        ValidateHash(sha256);
        return Path.Combine(_rootDirectory, "sha256", sha256[..2], sha256);
    }

    private static void ValidateHash(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new CampaignStoreConflict("blob-hash-invalid", "A blob hash must be 64 lowercase hexadecimal characters.");
        }
    }
}
