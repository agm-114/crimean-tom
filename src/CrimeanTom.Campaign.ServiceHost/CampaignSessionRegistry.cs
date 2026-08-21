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
    private readonly object _capabilityGate = new();
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
    private string _gameSessionId;
    private string _runtimeFingerprintSha256;
    private string _catalogFingerprintSha256;
    private string _playerTaskforceId;
    private TacticalMaterializationProfile[] _materializationProfiles;
    private Dictionary<string, ObservedCouplingRelationship> _couplingRelationships =
        new(StringComparer.Ordinal);

    public CampaignCompanionSession(CompanionHello hello)
    {
        Hello = hello.Clone();
        SessionId = Guid.NewGuid().ToString("N");
        ConnectedAt = DateTimeOffset.UtcNow;
        _gameSessionId = hello.GameSessionId;
        _runtimeFingerprintSha256 = hello.RuntimeFingerprintSha256;
        _catalogFingerprintSha256 = hello.CatalogFingerprintSha256;
        _playerTaskforceId = hello.PlayerTaskforceId;
        _materializationProfiles = hello.MaterializationProfiles.Select(item => item.Clone()).ToArray();
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
            if (!string.IsNullOrWhiteSpace(frame.Heartbeat.GameSessionId)
                && !string.IsNullOrWhiteSpace(frame.Heartbeat.RuntimeFingerprintSha256)
                && !string.IsNullOrWhiteSpace(frame.Heartbeat.CatalogFingerprintSha256))
            {
                lock (_capabilityGate)
                {
                    _gameSessionId = frame.Heartbeat.GameSessionId;
                    _runtimeFingerprintSha256 = frame.Heartbeat.RuntimeFingerprintSha256;
                    _catalogFingerprintSha256 = frame.Heartbeat.CatalogFingerprintSha256;
                    _playerTaskforceId = frame.Heartbeat.PlayerTaskforceId;
                    _materializationProfiles = frame.Heartbeat.MaterializationProfiles
                        .Select(item => item.Clone()).ToArray();
                    ObserveCouplingRelationships(frame.Heartbeat.CouplingRelationships);
                }
            }
        }
    }

    public CampaignSessionSnapshot Snapshot()
    {
        lock (_capabilityGate)
        {
            return new(
                SessionId,
                Hello.ClientInstanceId,
                _gameSessionId,
                Hello.CampaignId,
                Hello.ReconnectCommitPosition,
                ConnectedAt,
                Interlocked.Read(ref _nextServiceSequence),
                Interlocked.Read(ref _lastAcknowledgedSequence),
                Interlocked.Read(ref _lastAppliedCommitPosition),
                Interlocked.Read(ref _receivedFrames),
                _runtimeFingerprintSha256,
                _catalogFingerprintSha256,
                _playerTaskforceId,
                _materializationProfiles.Select(profile => new CampaignMaterializationProfile(
                    profile.UnitTypeId,
                    profile.VariantId,
                    profile.LoadoutId)).ToArray(),
                _couplingRelationships.Values
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new CampaignCouplingRelationship(
                        item.Relationship.Kind,
                        item.Relationship.SourceEntityId,
                        item.Relationship.TargetEntityId,
                        item.Relationship.EvidenceId,
                        item.Relationship.GuidanceType,
                        item.Relationship.UnsafeToSplit,
                        item.MissingSamples)).ToArray());
        }
    }

    private void ObserveCouplingRelationships(
        IEnumerable<TacticalCouplingRelationship> observed)
    {
        var next = observed
            .Where(item => !string.IsNullOrWhiteSpace(item.Kind)
                && !string.IsNullOrWhiteSpace(item.SourceEntityId)
                && !string.IsNullOrWhiteSpace(item.TargetEntityId))
            .Take(4_096)
            .Select(item => new ObservedCouplingRelationship(
                CouplingKey(item),
                item.Clone(),
                0))
            .ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var prior in _couplingRelationships.Values)
        {
            if (next.ContainsKey(prior.Key) || prior.MissingSamples >= 1)
            {
                continue;
            }
            next[prior.Key] = prior with { MissingSamples = prior.MissingSamples + 1 };
        }
        _couplingRelationships = next;
    }

    private static string CouplingKey(TacticalCouplingRelationship item) => string.Join('\n',
        item.Kind,
        item.SourceEntityId,
        item.TargetEntityId,
        item.EvidenceId,
        item.GuidanceType,
        item.UnsafeToSplit);

    public void Complete(string reason) => _outgoing.Writer.TryComplete();

    private sealed record ObservedCouplingRelationship(
        string Key,
        TacticalCouplingRelationship Relationship,
        int MissingSamples);
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
    long ReceivedFrameCount,
    string RuntimeFingerprintSha256,
    string CatalogFingerprintSha256,
    string PlayerTaskforceId,
    IReadOnlyList<CampaignMaterializationProfile> MaterializationProfiles,
    IReadOnlyList<CampaignCouplingRelationship> CouplingRelationships);

public sealed record CampaignMaterializationProfile(
    string UnitTypeId,
    string VariantId,
    string LoadoutId);

public sealed record CampaignCouplingRelationship(
    string Kind,
    string SourceEntityId,
    string TargetEntityId,
    string EvidenceId,
    string GuidanceType,
    bool UnsafeToSplit,
    int MissingSamples);
