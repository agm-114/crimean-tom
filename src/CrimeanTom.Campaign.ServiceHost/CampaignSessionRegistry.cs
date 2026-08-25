using System.Threading.Channels;

using CrimeanTom.Campaign.Transport.V1;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CampaignCompanionSession> _activeByClient =
        new(StringComparer.Ordinal);
    private readonly Dictionary<TacticalLeaseKey, DateTimeOffset> _tacticalLeaseGraceDeadlines = [];
    private readonly CampaignTacticalLeasePolicy _tacticalLeasePolicy = CampaignTacticalLeasePolicy.Load();

    public CampaignTacticalLeasePolicy TacticalLeasePolicy => _tacticalLeasePolicy;

    public CampaignCompanionSession Open(CompanionHello hello)
    {
        lock (_gate)
        {
            if (_activeByClient.Remove(hello.ClientInstanceId, out var prior))
            {
                prior.Complete("client-session-replaced");
            }
            var session = new CampaignCompanionSession(hello, _tacticalLeasePolicy);
            _activeByClient.Add(hello.ClientInstanceId, session);
            return session;
        }
    }

    public void Close(CampaignCompanionSession session)
    {
        lock (_gate)
        {
            if (_activeByClient.TryGetValue(session.Hello.ClientInstanceId, out var active)
                && ReferenceEquals(active, session))
            {
                _activeByClient.Remove(session.Hello.ClientInstanceId);
                StartTacticalLeaseGrace(session.Hello, DateTimeOffset.UtcNow);
            }
            session.Complete("session-closed");
        }
    }

    public bool CloseClient(string clientInstanceId, string reason)
    {
        lock (_gate)
        {
            if (!_activeByClient.Remove(clientInstanceId, out var session))
            {
                return false;
            }

            StartTacticalLeaseGrace(session.Hello, DateTimeOffset.UtcNow);
            session.Complete(reason);
            return true;
        }
    }

    public CampaignSessionSnapshot? Snapshot(string? campaignId = null)
    {
        lock (_gate)
        {
            return SelectSession(campaignId)?.Snapshot();
        }
    }

    public IReadOnlyList<CampaignSessionSnapshot> Snapshots(string? campaignId = null)
    {
        lock (_gate)
        {
            return _activeByClient.Values
                .Where(session => campaignId is null || string.Equals(
                    session.Hello.CampaignId,
                    campaignId,
                    StringComparison.Ordinal))
                .Select(session => session.Snapshot())
                .OrderBy(snapshot => snapshot.ClientInstanceId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public CampaignSessionSnapshot? SnapshotForClient(
        string campaignId,
        string clientInstanceId)
    {
        lock (_gate)
        {
            return _activeByClient.TryGetValue(clientInstanceId, out var session)
                && string.Equals(session.Hello.CampaignId, campaignId, StringComparison.Ordinal)
                    ? session.Snapshot()
                    : null;
        }
    }

    public CampaignTacticalLeaseState TacticalLeaseState(
        string campaignId,
        string clientInstanceId)
    {
        if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(clientInstanceId))
        {
            return CampaignTacticalLeaseState.Expired;
        }

        lock (_gate)
        {
            var key = new TacticalLeaseKey(campaignId, clientInstanceId);
            if (_activeByClient.TryGetValue(clientInstanceId, out var session)
                && string.Equals(session.Hello.CampaignId, campaignId, StringComparison.Ordinal)
                && IsLiveTacticalLease(session.Snapshot()))
            {
                _tacticalLeaseGraceDeadlines.Remove(key);
                return new CampaignTacticalLeaseState(
                    Live: true,
                    InGrace: false,
                    GraceExpiresAt: null,
                    _tacticalLeasePolicy.HeartbeatStaleAfter,
                    _tacticalLeasePolicy.GracePeriod);
            }

            var now = DateTimeOffset.UtcNow;
            if (!_tacticalLeaseGraceDeadlines.TryGetValue(key, out var deadline))
            {
                deadline = now + _tacticalLeasePolicy.GracePeriod;
                _tacticalLeaseGraceDeadlines.Add(key, deadline);
            }
            var inGrace = now < deadline;
            return new CampaignTacticalLeaseState(
                Live: false,
                InGrace: inGrace,
                GraceExpiresAt: deadline,
                _tacticalLeasePolicy.HeartbeatStaleAfter,
                _tacticalLeasePolicy.GracePeriod);
        }
    }

    public void CompleteTacticalLease(string campaignId, string clientInstanceId)
    {
        if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(clientInstanceId))
        {
            return;
        }
        lock (_gate)
        {
            _tacticalLeaseGraceDeadlines.Remove(new TacticalLeaseKey(campaignId, clientInstanceId));
        }
    }

    public bool TryQueue(ServiceFrame frame)
    {
        lock (_gate)
        {
            return SelectSession(null)?.TryQueue(frame) == true;
        }
    }

    public bool TryQueueForCampaign(string campaignId, ServiceFrame frame)
    {
        lock (_gate)
        {
            return SelectSession(campaignId)?.TryQueue(frame) == true;
        }
    }

    public bool TryQueueForClient(
        string campaignId,
        string clientInstanceId,
        ServiceFrame frame)
    {
        lock (_gate)
        {
            return _activeByClient.TryGetValue(clientInstanceId, out var session)
                && string.Equals(session.Hello.CampaignId, campaignId, StringComparison.Ordinal)
                && session.TryQueue(frame);
        }
    }

    public bool HasSessionsForCampaign(string campaignId)
    {
        lock (_gate)
        {
            return _activeByClient.Values.Any(session => string.Equals(
                session.Hello.CampaignId,
                campaignId,
                StringComparison.Ordinal));
        }
    }

    public CampaignBroadcastResult TryBroadcastForCampaign(string campaignId, ServiceFrame frame)
    {
        lock (_gate)
        {
            var matches = _activeByClient.Values
                .Where(session => string.Equals(
                    session.Hello.CampaignId,
                    campaignId,
                    StringComparison.Ordinal))
                .OrderBy(session => session.Hello.ClientInstanceId, StringComparer.Ordinal)
                .ToArray();
            var queued = 0;
            foreach (var session in matches)
            {
                if (session.TryQueue(frame))
                {
                    queued++;
                    continue;
                }
                if (_activeByClient.TryGetValue(session.Hello.ClientInstanceId, out var active)
                    && ReferenceEquals(active, session))
                {
                    _activeByClient.Remove(session.Hello.ClientInstanceId);
                    StartTacticalLeaseGrace(session.Hello, DateTimeOffset.UtcNow);
                }
                session.Complete("campaign-event-backpressure");
            }
            return new CampaignBroadcastResult(matches.Length, queued);
        }
    }

    private CampaignCompanionSession? SelectSession(string? campaignId)
    {
        return _activeByClient.Values
            .Where(session => campaignId is null || string.Equals(
                session.Hello.CampaignId,
                campaignId,
                StringComparison.Ordinal))
            .Select(session => (Session: session, Snapshot: session.Snapshot()))
            .OrderBy(item => string.IsNullOrWhiteSpace(item.Snapshot.PlayerTaskforceId) ? 1 : 0)
            .ThenBy(item => item.Snapshot.OwnedRecoveryRequiredEntityCount)
            .ThenBy(item => item.Snapshot.OwnedMaterializedEntityCount)
            .ThenBy(item => item.Snapshot.ClientInstanceId, StringComparer.Ordinal)
            .Select(item => item.Session)
            .FirstOrDefault();
    }

    private static bool IsLiveTacticalLease(CampaignSessionSnapshot snapshot)
    {
        var health = CampaignWorkerHealth.NormalizeHealth(snapshot.Health);
        return snapshot.PlayerSessionBound
            && !string.IsNullOrWhiteSpace(snapshot.GameSessionId)
            && health is CampaignWorkerHealth.Healthy
                or CampaignWorkerHealth.Pressured
                or CampaignWorkerHealth.Overloaded
                or CampaignWorkerHealth.Draining;
    }

    private void StartTacticalLeaseGrace(CompanionHello hello, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(hello.CampaignId)
            || string.IsNullOrWhiteSpace(hello.ClientInstanceId))
        {
            return;
        }
        var key = new TacticalLeaseKey(hello.CampaignId, hello.ClientInstanceId);
        _tacticalLeaseGraceDeadlines.TryAdd(key, now + _tacticalLeasePolicy.GracePeriod);
    }

    private readonly record struct TacticalLeaseKey(string CampaignId, string ClientInstanceId);
}

public sealed record CampaignTacticalLeasePolicy(
    TimeSpan HeartbeatStaleAfter,
    TimeSpan GracePeriod)
{
    public static CampaignTacticalLeasePolicy Load() => new(
        TimeSpan.FromSeconds(8),
        TimeSpan.FromMilliseconds(ParseGraceMilliseconds()));

    private static int ParseGraceMilliseconds()
    {
        var value = Environment.GetEnvironmentVariable("CRIMEAN_TOM_TACTICAL_LEASE_GRACE_MS");
        return int.TryParse(value, out var parsed) && parsed is >= 100 and <= 300_000
            ? parsed
            : 12_000;
    }
}

public sealed record CampaignTacticalLeaseState(
    bool Live,
    bool InGrace,
    DateTimeOffset? GraceExpiresAt,
    TimeSpan HeartbeatStaleAfter,
    TimeSpan GracePeriod)
{
    public bool HoldsAuthority => Live || InGrace;

    public static CampaignTacticalLeaseState Expired { get; } = new(
        Live: false,
        InGrace: false,
        GraceExpiresAt: null,
        HeartbeatStaleAfter: TimeSpan.Zero,
        GracePeriod: TimeSpan.Zero);
}

public sealed record CampaignBroadcastResult(int MatchedSessionCount, int QueuedSessionCount)
{
    public bool AllQueued => MatchedSessionCount == QueuedSessionCount;
    public int DroppedSessionCount => MatchedSessionCount - QueuedSessionCount;
}

public sealed class CampaignCompanionSession
{
    private readonly CampaignTacticalLeasePolicy _tacticalLeasePolicy;
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
    private string _playerCoalitionId;
    private TacticalMaterializationProfile[] _materializationProfiles;
    private int _ownedMaterializedEntityCount;
    private int _ownedActiveEntityCount;
    private int _ownedRetainedSuspendedEntityCount;
    private int _ownedRecoveryRequiredEntityCount;
    private int _ownedOperationalEntityCount;
    private int _tacticalEntityCapacity;
    private int _tacticalReserveCapacity;
    private int _operationalEntityCapacity;
    private int _operationalReserveCapacity;
    private string _health = CampaignWorkerHealth.Healthy;
    private string _eligibility = CampaignWorkerHealth.Eligible;
    private DateTimeOffset _lastHeartbeatAt;
    private string _workerKind;
    private Dictionary<string, ObservedCouplingRelationship> _couplingRelationships =
        new(StringComparer.Ordinal);

    public CampaignCompanionSession(
        CompanionHello hello,
        CampaignTacticalLeasePolicy tacticalLeasePolicy)
    {
        Hello = hello.Clone();
        _tacticalLeasePolicy = tacticalLeasePolicy;
        SessionId = Guid.NewGuid().ToString("N");
        ConnectedAt = DateTimeOffset.UtcNow;
        _lastHeartbeatAt = ConnectedAt;
        _workerKind = CampaignWorkerKind.TryValidate(hello.WorkerKind, out var kind)
            ? kind
            : CampaignWorkerKind.SeaPower;
        _gameSessionId = hello.GameSessionId;
        _runtimeFingerprintSha256 = hello.RuntimeFingerprintSha256;
        _catalogFingerprintSha256 = hello.CatalogFingerprintSha256;
        _playerTaskforceId = hello.PlayerTaskforceId;
        _playerCoalitionId = hello.PlayerCoalitionId;
        _materializationProfiles = hello.MaterializationProfiles.Select(item => item.Clone()).ToArray();
        if (CampaignWorkerKind.IsOperational(_workerKind))
        {
            ObserveOperationalCapacity(
                hello.OperationalEntityCapacity,
                hello.OperationalReserveCapacity);
        }
        else
        {
            ObserveCapacity(hello.TacticalEntityCapacity, hello.TacticalReserveCapacity);
        }
        ObserveHealth(hello.Health, hello.Eligibility);
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
            var operationalHeartbeat = CampaignWorkerKind.IsOperational(_workerKind)
                || CampaignWorkerKind.IsOperational(frame.Heartbeat.WorkerKind);
            lock (_capabilityGate)
            {
                _lastHeartbeatAt = DateTimeOffset.UtcNow;
                var mixedCapacityNamespace = operationalHeartbeat
                    ? frame.Heartbeat.TacticalEntityCapacity != 0
                        || frame.Heartbeat.TacticalReserveCapacity != 0
                        || frame.Heartbeat.OwnedMaterializedEntityCount != 0
                        || frame.Heartbeat.OwnedActiveEntityCount != 0
                        || frame.Heartbeat.OwnedRetainedSuspendedEntityCount != 0
                        || frame.Heartbeat.OwnedRecoveryRequiredEntityCount != 0
                    : frame.Heartbeat.OperationalEntityCapacity != 0
                        || frame.Heartbeat.OperationalReserveCapacity != 0
                        || frame.Heartbeat.OwnedOperationalEntityCount != 0;
                if (mixedCapacityNamespace)
                {
                    ObserveHealth(CampaignWorkerHealth.Quarantined, CampaignWorkerHealth.Quarantined);
                    return;
                }
                if (operationalHeartbeat
                    && !string.IsNullOrWhiteSpace(frame.Heartbeat.PlayerCoalitionId))
                {
                    ObserveHealth(CampaignWorkerHealth.Quarantined, CampaignWorkerHealth.Quarantined);
                    return;
                }
                if (operationalHeartbeat
                    || (!string.IsNullOrWhiteSpace(frame.Heartbeat.GameSessionId)
                        && !string.IsNullOrWhiteSpace(frame.Heartbeat.RuntimeFingerprintSha256)
                        && !string.IsNullOrWhiteSpace(frame.Heartbeat.CatalogFingerprintSha256)))
                {
                    if (!string.IsNullOrWhiteSpace(frame.Heartbeat.WorkerKind)
                        && CampaignWorkerKind.TryValidate(frame.Heartbeat.WorkerKind, out var heartbeatKind)
                        && !string.Equals(heartbeatKind, _workerKind, StringComparison.Ordinal))
                    {
                        ObserveHealth(CampaignWorkerHealth.Quarantined, CampaignWorkerHealth.Quarantined);
                        return;
                    }

                    if (!operationalHeartbeat)
                    {
                        if (!string.IsNullOrWhiteSpace(frame.Heartbeat.PlayerCoalitionId)
                            && (!Guid.TryParseExact(frame.Heartbeat.PlayerCoalitionId, "N", out _)
                                || (!string.IsNullOrWhiteSpace(_playerCoalitionId)
                                    && !string.Equals(
                                        frame.Heartbeat.PlayerCoalitionId,
                                        _playerCoalitionId,
                                        StringComparison.Ordinal))))
                        {
                            ObserveHealth(CampaignWorkerHealth.Quarantined, CampaignWorkerHealth.Quarantined);
                            return;
                        }
                        _gameSessionId = frame.Heartbeat.GameSessionId;
                        _runtimeFingerprintSha256 = frame.Heartbeat.RuntimeFingerprintSha256;
                        _playerTaskforceId = frame.Heartbeat.PlayerTaskforceId;
                        if (!string.IsNullOrWhiteSpace(frame.Heartbeat.PlayerCoalitionId))
                        {
                            _playerCoalitionId = frame.Heartbeat.PlayerCoalitionId;
                        }
                        _materializationProfiles = frame.Heartbeat.MaterializationProfiles
                            .Select(item => item.Clone()).ToArray();
                        ObserveCouplingRelationships(frame.Heartbeat.CouplingRelationships);
                    }
                    else
                    {
                        _ownedOperationalEntityCount = Math.Max(
                            0,
                            frame.Heartbeat.OwnedOperationalEntityCount);
                    }

                    if (!string.IsNullOrWhiteSpace(frame.Heartbeat.CatalogFingerprintSha256))
                    {
                        _catalogFingerprintSha256 = frame.Heartbeat.CatalogFingerprintSha256;
                    }

                    _ownedActiveEntityCount = Math.Max(0, frame.Heartbeat.OwnedActiveEntityCount);
                    _ownedRetainedSuspendedEntityCount = Math.Max(
                        0,
                        frame.Heartbeat.OwnedRetainedSuspendedEntityCount);
                    _ownedMaterializedEntityCount = Math.Max(
                        Math.Max(0, frame.Heartbeat.OwnedMaterializedEntityCount),
                        _ownedActiveEntityCount + _ownedRetainedSuspendedEntityCount);
                    _ownedRecoveryRequiredEntityCount = Math.Max(
                        0,
                        frame.Heartbeat.OwnedRecoveryRequiredEntityCount);
                    if (operationalHeartbeat)
                    {
                        ObserveOperationalCapacity(
                            frame.Heartbeat.OperationalEntityCapacity,
                            frame.Heartbeat.OperationalReserveCapacity);
                    }
                    else
                    {
                        ObserveCapacity(
                            frame.Heartbeat.TacticalEntityCapacity,
                            frame.Heartbeat.TacticalReserveCapacity);
                    }
                }

                ObserveHealth(frame.Heartbeat.Health, frame.Heartbeat.Eligibility, replaceEmptyWithHealthy: false);
            }
        }
    }

    public CampaignSessionSnapshot Snapshot()
    {
        lock (_capabilityGate)
        {
            var health = _health;
            var eligibility = _eligibility;
            if (_lastHeartbeatAt != default
                && DateTimeOffset.UtcNow - _lastHeartbeatAt > _tacticalLeasePolicy.HeartbeatStaleAfter)
            {
                health = CampaignWorkerHealth.Offline;
                eligibility = CampaignWorkerHealth.Disabled;
            }

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
                _playerCoalitionId,
                _materializationProfiles.Select(profile => new CampaignMaterializationProfile(
                    profile.UnitTypeId,
                    profile.VariantId,
                    profile.LoadoutId)).ToArray(),
                _ownedMaterializedEntityCount,
                _ownedActiveEntityCount,
                _ownedRetainedSuspendedEntityCount,
                _ownedRecoveryRequiredEntityCount,
                _ownedOperationalEntityCount,
                _tacticalEntityCapacity,
                _tacticalReserveCapacity,
                _operationalEntityCapacity,
                _operationalReserveCapacity,
                health,
                eligibility,
                _couplingRelationships.Values
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new CampaignCouplingRelationship(
                        item.Relationship.Kind,
                        item.Relationship.SourceEntityId,
                        item.Relationship.TargetEntityId,
                        item.Relationship.EvidenceId,
                        item.Relationship.GuidanceType,
                        item.Relationship.UnsafeToSplit,
                        item.MissingSamples)).ToArray(),
                _workerKind,
                CampaignWorkerKind.PlayerSessionBound(_workerKind));
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

    private void ObserveCapacity(int capacity, int reserve)
    {
        if (capacity is < 1 or > 10_000 || reserve < 0 || reserve > capacity)
        {
            _tacticalEntityCapacity = 0;
            _tacticalReserveCapacity = 0;
            return;
        }
        _tacticalEntityCapacity = capacity;
        _tacticalReserveCapacity = reserve;
    }

    private void ObserveOperationalCapacity(int capacity, int reserve)
    {
        if (capacity is < 1 or > 10_000 || reserve < 0 || reserve > capacity)
        {
            _operationalEntityCapacity = 0;
            _operationalReserveCapacity = 0;
            return;
        }

        _operationalEntityCapacity = capacity;
        _operationalReserveCapacity = reserve;
    }

    private void ObserveHealth(string health, string eligibility, bool replaceEmptyWithHealthy = true)
    {
        if (string.IsNullOrWhiteSpace(health)
            && string.IsNullOrWhiteSpace(eligibility)
            && !replaceEmptyWithHealthy)
        {
            return;
        }

        var normalizedHealth = CampaignWorkerHealth.NormalizeHealth(health);
        var normalizedEligibility = CampaignWorkerHealth.NormalizeEligibility(eligibility);
        if (!string.IsNullOrWhiteSpace(health)
            && !string.Equals(normalizedHealth, health.Trim(), StringComparison.Ordinal))
        {
            normalizedHealth = CampaignWorkerHealth.Quarantined;
            normalizedEligibility = CampaignWorkerHealth.Quarantined;
        }
        if (!string.IsNullOrWhiteSpace(eligibility)
            && !string.Equals(normalizedEligibility, eligibility.Trim(), StringComparison.Ordinal))
        {
            normalizedHealth = CampaignWorkerHealth.Quarantined;
            normalizedEligibility = CampaignWorkerHealth.Quarantined;
        }
        _health = normalizedHealth;
        _eligibility = normalizedEligibility;
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
    string PlayerCoalitionId,
    IReadOnlyList<CampaignMaterializationProfile> MaterializationProfiles,
    int OwnedMaterializedEntityCount,
    int OwnedActiveEntityCount,
    int OwnedRetainedSuspendedEntityCount,
    int OwnedRecoveryRequiredEntityCount,
    int OwnedOperationalEntityCount,
    int TacticalEntityCapacity,
    int TacticalReserveCapacity,
    int OperationalEntityCapacity,
    int OperationalReserveCapacity,
    string Health,
    string Eligibility,
    IReadOnlyList<CampaignCouplingRelationship> CouplingRelationships,
    string WorkerKind,
    bool PlayerSessionBound);

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
