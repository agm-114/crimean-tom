using System.Threading.Channels;

using CrimeanTom.Campaign.Transport.V1;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignSessionRegistry
{
    private readonly object _gate = new();
    private CampaignCompanionSession? _active;

    public CampaignCompanionSession Open(CompanionHello hello)
    {
        lock (_gate)
        {
            _active?.Complete("session-replaced");
            _active = new CampaignCompanionSession(hello);
            return _active;
        }
    }

    public void Close(CampaignCompanionSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, session))
            {
                _active = null;
            }
            session.Complete("session-closed");
        }
    }

    public CampaignSessionSnapshot? Snapshot()
    {
        lock (_gate)
        {
            return _active?.Snapshot();
        }
    }

    public bool TryQueue(ServiceFrame frame)
    {
        lock (_gate)
        {
            return _active?.TryQueue(frame) == true;
        }
    }

    public bool TryQueueForCampaign(string campaignId, ServiceFrame frame)
    {
        lock (_gate)
        {
            return _active is not null
                && string.Equals(_active.Hello.CampaignId, campaignId, StringComparison.Ordinal)
                && _active.TryQueue(frame);
        }
    }
}

public sealed class CampaignCompanionSession
{
    private readonly Channel<ServiceFrame> _outgoing = Channel.CreateBounded<ServiceFrame>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private long _nextServiceSequence;
    private long _lastAcknowledgedSequence;
    private long _lastAppliedCommitPosition;
    private long _receivedFrames;

    public CampaignCompanionSession(CompanionHello hello)
    {
        Hello = hello.Clone();
        SessionId = Guid.NewGuid().ToString("N");
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    public CompanionHello Hello { get; }
    public string SessionId { get; }
    public DateTimeOffset ConnectedAt { get; }
    public ChannelReader<ServiceFrame> Outgoing => _outgoing.Reader;

    public ulong NextSequence() => checked((ulong)Interlocked.Increment(ref _nextServiceSequence));

    public bool TryQueue(ServiceFrame frame)
    {
        var queued = frame.Clone();
        queued.Sequence = 0;
        return _outgoing.Writer.TryWrite(queued);
    }

    public void Observe(CompanionFrame frame)
    {
        Interlocked.Increment(ref _receivedFrames);
        if (frame.PayloadCase == CompanionFrame.PayloadOneofCase.Acknowledgement)
        {
            Interlocked.Exchange(ref _lastAcknowledgedSequence, checked((long)frame.Acknowledgement.ServiceSequence));
            Interlocked.Exchange(ref _lastAppliedCommitPosition, frame.Acknowledgement.AppliedCommitPosition);
        }
        else if (frame.PayloadCase == CompanionFrame.PayloadOneofCase.Heartbeat)
        {
            Interlocked.Exchange(ref _lastAppliedCommitPosition, frame.Heartbeat.ObservedCommitPosition);
        }
    }

    public CampaignSessionSnapshot Snapshot() => new(
        SessionId,
        Hello.ClientInstanceId,
        Hello.GameSessionId,
        Hello.CampaignId,
        Hello.ReconnectCommitPosition,
        ConnectedAt,
        Interlocked.Read(ref _nextServiceSequence),
        Interlocked.Read(ref _lastAcknowledgedSequence),
        Interlocked.Read(ref _lastAppliedCommitPosition),
        Interlocked.Read(ref _receivedFrames));

    public void Complete(string reason) => _outgoing.Writer.TryComplete();
}

public sealed record CampaignSessionSnapshot(
    string SessionId,
    string ClientInstanceId,
    string GameSessionId,
    string CampaignId,
    long ReconnectCommitPosition,
    DateTimeOffset ConnectedAt,
    long LastServiceSequence,
    long LastAcknowledgedSequence,
    long LastAppliedCommitPosition,
    long ReceivedFrameCount);
