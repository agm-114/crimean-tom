using System.Collections.Concurrent;
using System.Diagnostics;

using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.ServiceHost;

public enum CampaignOperationKind
{
    Recovery,
    Advance,
    Schedule,
}

public sealed record CampaignOperationTelemetryView(
    long CompletedCount,
    long LastDurationMicroseconds,
    long MaximumDurationMicroseconds,
    long TotalDurationMicroseconds,
    long LastWorkUnits,
    long TotalWorkUnits,
    long LastWorkUnitsPerSecondMilliunits);

public sealed record CampaignPerformanceView(
    DateTimeOffset ServiceStartedAtUtc,
    long ServiceUptimeMilliseconds,
    long ManagedMemoryBytes,
    long WorkingSetBytes,
    CampaignOperationTelemetryView Recovery,
    CampaignOperationTelemetryView Advance,
    CampaignOperationTelemetryView Schedule);

public sealed class CampaignPerformanceTelemetry
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly ConcurrentDictionary<(CampaignId CampaignId, CampaignOperationKind Kind), Accumulator> _metrics = new();

    public void Record(
        CampaignId campaignId,
        CampaignOperationKind kind,
        TimeSpan duration,
        long workUnits = 0)
    {
        var durationMicroseconds = Math.Max(1, (long)Math.Ceiling(duration.TotalMicroseconds));
        _metrics.GetOrAdd((campaignId, kind), _ => new Accumulator())
            .Record(durationMicroseconds, Math.Max(0, workUnits));
    }

    public CampaignPerformanceView Snapshot(CampaignId campaignId) => new(
        _startedAt,
        (long)Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
        GC.GetTotalMemory(forceFullCollection: false),
        Environment.WorkingSet,
        Snapshot(campaignId, CampaignOperationKind.Recovery),
        Snapshot(campaignId, CampaignOperationKind.Advance),
        Snapshot(campaignId, CampaignOperationKind.Schedule));

    private CampaignOperationTelemetryView Snapshot(CampaignId campaignId, CampaignOperationKind kind) =>
        _metrics.TryGetValue((campaignId, kind), out var metric)
            ? metric.Snapshot()
            : new CampaignOperationTelemetryView(0, 0, 0, 0, 0, 0, 0);

    private sealed class Accumulator
    {
        private readonly object _gate = new();
        private long _completedCount;
        private long _lastDurationMicroseconds;
        private long _maximumDurationMicroseconds;
        private long _totalDurationMicroseconds;
        private long _lastWorkUnits;
        private long _totalWorkUnits;

        public void Record(long durationMicroseconds, long workUnits)
        {
            lock (_gate)
            {
                _completedCount++;
                _lastDurationMicroseconds = durationMicroseconds;
                _maximumDurationMicroseconds = Math.Max(_maximumDurationMicroseconds, durationMicroseconds);
                _totalDurationMicroseconds = checked(_totalDurationMicroseconds + durationMicroseconds);
                _lastWorkUnits = workUnits;
                _totalWorkUnits = checked(_totalWorkUnits + workUnits);
            }
        }

        public CampaignOperationTelemetryView Snapshot()
        {
            lock (_gate)
            {
                var rate = _lastWorkUnits == 0 || _lastDurationMicroseconds == 0
                    ? 0
                    : checked(_lastWorkUnits * 1_000_000_000L / _lastDurationMicroseconds);
                return new CampaignOperationTelemetryView(
                    _completedCount,
                    _lastDurationMicroseconds,
                    _maximumDurationMicroseconds,
                    _totalDurationMicroseconds,
                    _lastWorkUnits,
                    _totalWorkUnits,
                    rate);
            }
        }
    }
}
