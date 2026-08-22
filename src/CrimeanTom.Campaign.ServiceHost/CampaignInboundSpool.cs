using CrimeanTom.Campaign.Transport.V1;
using Google.Protobuf;
using Grpc.Core;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignInboundSpool
{
    private const int MaximumEntries = 4_096;
    private const long MaximumBytes = 64L * 1024 * 1024;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _completionsSincePrune;

    public CampaignInboundSpool(string root) => _root = Path.GetFullPath(root);

    public async Task<CampaignInboundReceipt> AcceptAsync(
        CampaignCompanionSession session,
        CompanionFrame frame,
        CancellationToken cancellationToken)
    {
        var content = frame.ToByteArray();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            var sessionDirectory = Path.Combine(_root, session.Hello.ClientInstanceId, session.SessionId);
            Directory.CreateDirectory(sessionDirectory);
            var destination = Path.Combine(sessionDirectory, $"{frame.Sequence:D20}.frame");
            var processed = Path.ChangeExtension(destination, ".processed");
            if (File.Exists(processed))
            {
                if (!File.ReadAllBytes(processed).AsSpan().SequenceEqual(content))
                {
                    throw SequenceConflict();
                }
                return new CampaignInboundReceipt(destination, processed, AlreadyProcessed: true);
            }
            if (File.Exists(destination))
            {
                if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
                {
                    throw SequenceConflict();
                }
                return new CampaignInboundReceipt(destination, processed, AlreadyProcessed: false);
            }

            var files = Directory.EnumerateFiles(_root, "*.frame", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToArray();
            if (files.Length >= MaximumEntries || files.Sum(item => item.Length) + content.Length > MaximumBytes)
            {
                throw new RpcException(new Grpc.Core.Status(
                    Grpc.Core.StatusCode.ResourceExhausted,
                    "The durable companion inbound spool is full."));
            }

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
                File.Move(temporary, destination);
                return new CampaignInboundReceipt(destination, processed, AlreadyProcessed: false);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(CampaignInboundReceipt receipt, CancellationToken cancellationToken)
    {
        if (receipt.AlreadyProcessed)
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(receipt.ProcessedPath))
            {
                if (File.Exists(receipt.PendingPath))
                {
                    File.Delete(receipt.PendingPath);
                }
                return;
            }
            if (File.Exists(receipt.PendingPath))
            {
                File.Move(receipt.PendingPath, receipt.ProcessedPath);
            }
            _completionsSincePrune++;
            if (_completionsSincePrune >= 256)
            {
                _completionsSincePrune = 0;
                PruneProcessedEntries();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AbandonSessionAsync(
        CampaignCompanionSession session,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sessionDirectory = Path.Combine(_root, session.Hello.ClientInstanceId, session.SessionId);
            if (!Directory.Exists(sessionDirectory))
            {
                return;
            }
            foreach (var pending in Directory.EnumerateFiles(sessionDirectory, "*.frame"))
            {
                File.Delete(pending);
            }
            DeleteIfEmpty(sessionDirectory);
            DeleteIfEmpty(Path.GetDirectoryName(sessionDirectory)!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PruneProcessedEntries()
    {
        const int maximumRetainedEntries = 4_096;
        var processed = Directory.EnumerateFiles(_root, "*.processed", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(item => item.LastWriteTimeUtc)
            .ToArray();
        var retainedBytes = 0L;
        for (var index = 0; index < processed.Length; index++)
        {
            retainedBytes += processed[index].Length;
            if (index >= maximumRetainedEntries || retainedBytes > MaximumBytes)
            {
                processed[index].Delete();
            }
        }
    }

    private static void DeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private static RpcException SequenceConflict() => new(new Grpc.Core.Status(
        Grpc.Core.StatusCode.AlreadyExists,
        "A companion sequence was reused with different bytes."));
}

public sealed record CampaignInboundReceipt(
    string PendingPath,
    string ProcessedPath,
    bool AlreadyProcessed);
