using System.Security.Cryptography;
using System.Text;
using CrimeanTom.Campaign.Transport.V1;
using Google.Protobuf;

namespace CrimeanTom.Companion;

public sealed class CampaignCompanionStateStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CampaignCompanionStateStore(string root) => _root = Path.GetFullPath(root);

    public long ReadCursor()
    {
        var path = Path.Combine(_root, "cursor");
        return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var value) && value >= 0
            ? value
            : 0;
    }

    public async Task AcceptCommittedEventAsync(
        CommittedCampaignEvent campaignEvent,
        CancellationToken cancellationToken)
    {
        var content = campaignEvent.EventJson.ToByteArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actualHash, campaignEvent.EventSha256, StringComparison.Ordinal)
            || campaignEvent.CommitPosition != ReadCursor() + 1)
        {
            throw new InvalidDataException("Committed campaign catch-up is corrupt or non-contiguous.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var events = Path.Combine(_root, "committed-events");
            Directory.CreateDirectory(events);
            await WriteAtomicallyAsync(
                Path.Combine(events, $"{campaignEvent.CommitPosition:D20}-{campaignEvent.EventId}.json"),
                content,
                cancellationToken);
            await WriteAtomicallyAsync(
                Path.Combine(_root, "cursor"),
                Encoding.UTF8.GetBytes(campaignEvent.CommitPosition.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StageOutboundAsync(string key, CompanionFrame frame, CancellationToken cancellationToken)
    {
        var safeKey = string.Concat(key.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        if (safeKey.Length is 0 or > 180)
        {
            throw new InvalidDataException("A bounded durable outbound key is required.");
        }
        var directory = Path.Combine(_root, "outbound");
        Directory.CreateDirectory(directory);
        var normalized = frame.Clone();
        normalized.Sequence = 0;
        var destination = Path.Combine(directory, $"{safeKey}.frame");
        var content = normalized.ToByteArray();
        if (File.Exists(destination))
        {
            if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
            {
                throw new InvalidDataException("A durable outbound key was reused with different bytes.");
            }
            return;
        }
        await WriteAtomicallyAsync(destination, content, cancellationToken);
    }

    public IReadOnlyList<StagedCompanionFrame> ReadOutbound()
    {
        var directory = Path.Combine(_root, "outbound");
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.EnumerateFiles(directory, "*.frame")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new StagedCompanionFrame(
                Path.GetFileNameWithoutExtension(path),
                CompanionFrame.Parser.ParseFrom(File.ReadAllBytes(path))))
            .ToArray();
    }

    public void AcknowledgeOutbound(string key)
    {
        var path = Path.Combine(_root, "outbound", $"{key}.frame");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task WriteAtomicallyAsync(
        string destination,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

public sealed record StagedCompanionFrame(string Key, CompanionFrame Frame);
