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

    public CampaignInboundSpool(string root) => _root = Path.GetFullPath(root);

    public async Task AcceptAsync(
        CampaignCompanionSession session,
        CompanionFrame frame,
        CancellationToken cancellationToken)
    {
        var content = frame.ToByteArray();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            var files = Directory.EnumerateFiles(_root, "*.frame", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToArray();
            if (files.Length >= MaximumEntries || files.Sum(item => item.Length) + content.Length > MaximumBytes)
            {
                throw new RpcException(new Grpc.Core.Status(
                    Grpc.Core.StatusCode.ResourceExhausted,
                    "The durable companion inbound spool is full."));
            }

            var sessionDirectory = Path.Combine(_root, session.Hello.ClientInstanceId, session.SessionId);
            Directory.CreateDirectory(sessionDirectory);
            var destination = Path.Combine(sessionDirectory, $"{frame.Sequence:D20}.frame");
            if (File.Exists(destination))
            {
                if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
                {
                    throw new RpcException(new Grpc.Core.Status(
                        Grpc.Core.StatusCode.AlreadyExists,
                        "A companion sequence was reused with different bytes."));
                }
                return;
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
}
