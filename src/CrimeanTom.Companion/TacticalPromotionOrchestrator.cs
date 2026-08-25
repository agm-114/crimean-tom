using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

/// <summary>
/// Owns the retry and idempotency state for map-selection-driven promotion.
/// The campaign client performs I/O; this class is the single state machine
/// deciding whether a selection should inspect, hand off, retry, or stop.
/// </summary>
public sealed class TacticalPromotionOrchestrator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastAttempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _localityHandoffs = new(StringComparer.Ordinal);
    private string? _selectedEntityId;
    private string? _attemptId;

    public TacticalPromotionAttempt Observe(
        SurfaceRenderProxyStatePayload proxy,
        string selectedEntityId)
    {
        lock (_gate)
        {
            if (!string.Equals(_selectedEntityId, selectedEntityId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(_attemptId))
            {
                _selectedEntityId = selectedEntityId;
                _attemptId = Guid.NewGuid().ToString("N");
                TrimCompletedState();
            }
            var requestKey =
                $"{proxy.GroupId}:{proxy.GroupRevision}:{selectedEntityId}:{_attemptId}";
            return new TacticalPromotionAttempt(selectedEntityId, _attemptId, requestKey);
        }
    }

    public void ResetSelection()
    {
        lock (_gate)
        {
            _selectedEntityId = null;
            _attemptId = null;
        }
    }

    public bool ShouldAttempt(string requestKey, long nowUnixMilliseconds, long retryAfterMilliseconds)
    {
        lock (_gate)
        {
            if (_accepted.Contains(requestKey))
            {
                return false;
            }
            if (_lastAttempts.TryGetValue(requestKey, out var lastAttempt)
                && nowUnixMilliseconds - lastAttempt < retryAfterMilliseconds)
            {
                return false;
            }
            _lastAttempts[requestKey] = nowUnixMilliseconds;
            return true;
        }
    }

    public void MarkAccepted(string requestKey)
    {
        lock (_gate)
        {
            _accepted.Add(requestKey);
            _lastAttempts.Remove(requestKey);
        }
    }

    public bool TryBeginLocalityHandoff(string handoffKey)
    {
        lock (_gate)
        {
            return _localityHandoffs.Add(handoffKey);
        }
    }

    public void AbandonLocalityHandoff(string handoffKey)
    {
        lock (_gate)
        {
            _localityHandoffs.Remove(handoffKey);
        }
    }

    private void TrimCompletedState()
    {
        if (_accepted.Count <= 1_024 && _lastAttempts.Count <= 1_024)
        {
            return;
        }
        _accepted.Clear();
        _lastAttempts.Clear();
    }
}

public sealed record TacticalPromotionAttempt(
    string EntityId,
    string AttemptId,
    string RequestKey);
