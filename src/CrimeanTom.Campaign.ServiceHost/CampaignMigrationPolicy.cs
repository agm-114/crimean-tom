using System.Collections.Concurrent;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal sealed record CampaignMigrationPolicyOptions(int CooldownQuanta)
{
    public static CampaignMigrationPolicyOptions Load()
    {
        var configured = Environment.GetEnvironmentVariable("CRIMEAN_TOM_MIGRATION_COOLDOWN_QUANTA");
        if (!int.TryParse(configured, out var cooldownQuanta) || cooldownQuanta < 0)
        {
            cooldownQuanta = 30;
        }

        return new CampaignMigrationPolicyOptions(cooldownQuanta);
    }
}

internal sealed record MigrationOrchestrationLease(
    CampaignId CampaignId,
    string SourceWorkerClientInstanceId,
    string DestinationWorkerClientInstanceId,
    string IdempotencyKey);

internal sealed class CampaignMigrationAdmissionRegistry
{
    private readonly ConcurrentDictionary<CampaignId, MigrationOrchestrationLease> _active = new();

    public bool TryAcquire(MigrationOrchestrationLease requested)
    {
        if (_active.TryGetValue(requested.CampaignId, out var sameCampaign))
        {
            if (sameCampaign.SourceWorkerClientInstanceId != requested.SourceWorkerClientInstanceId
                || sameCampaign.DestinationWorkerClientInstanceId != requested.DestinationWorkerClientInstanceId)
            {
                return false;
            }

            return string.Equals(sameCampaign.IdempotencyKey, requested.IdempotencyKey, StringComparison.Ordinal);
        }

        foreach (var active in _active.Values)
        {
            if (SharesWorker(active, requested))
            {
                return false;
            }
        }

        return _active.TryAdd(requested.CampaignId, requested);
    }

    public bool IsHeld(MigrationOrchestrationLease requested) =>
        _active.TryGetValue(requested.CampaignId, out var active)
        && active.SourceWorkerClientInstanceId == requested.SourceWorkerClientInstanceId
        && active.DestinationWorkerClientInstanceId == requested.DestinationWorkerClientInstanceId;

    public void Release(MigrationOrchestrationLease requested)
    {
        if (_active.TryGetValue(requested.CampaignId, out var active)
            && active.SourceWorkerClientInstanceId == requested.SourceWorkerClientInstanceId
            && active.DestinationWorkerClientInstanceId == requested.DestinationWorkerClientInstanceId)
        {
            _active.TryRemove(requested.CampaignId, out _);
        }
    }

    private static bool SharesWorker(
        MigrationOrchestrationLease left,
        MigrationOrchestrationLease right) =>
        string.Equals(left.SourceWorkerClientInstanceId, right.SourceWorkerClientInstanceId, StringComparison.Ordinal)
        || string.Equals(left.SourceWorkerClientInstanceId, right.DestinationWorkerClientInstanceId, StringComparison.Ordinal)
        || string.Equals(left.DestinationWorkerClientInstanceId, right.SourceWorkerClientInstanceId, StringComparison.Ordinal)
        || string.Equals(left.DestinationWorkerClientInstanceId, right.DestinationWorkerClientInstanceId, StringComparison.Ordinal);
}

internal sealed record CampaignMigrationCooldownStatus(
    long LastMigrationEffectiveAtQuanta,
    long CooldownEndsAtQuanta,
    long CurrentTimeQuanta);

internal static class CampaignMigrationCooldown
{
    public static async Task<CampaignMigrationCooldownStatus?> FindActiveCooldownAsync(
        CampaignState state,
        SqliteCampaignJournal journal,
        string sourceWorkerClientInstanceId,
        string destinationWorkerClientInstanceId,
        int cooldownQuanta,
        CancellationToken cancellationToken)
    {
        if (cooldownQuanta <= 0
            || string.IsNullOrWhiteSpace(sourceWorkerClientInstanceId)
            || string.IsNullOrWhiteSpace(destinationWorkerClientInstanceId))
        {
            return null;
        }

        var events = await journal.ReadEventsAsync(
            state.Metadata.CampaignId,
            CommitPosition.Origin,
            cancellationToken);
        foreach (var committed in events
            .Where(item => item.Data is TacticalIslandMigratedEvent)
            .OrderByDescending(item => item.CommitPosition.Value))
        {
            if (committed.Data is not TacticalIslandMigratedEvent migrated
                || !TryResolveWorkerSet(state, migrated, out var sourceWorker, out var destinationWorker)
                || !string.Equals(sourceWorker, sourceWorkerClientInstanceId, StringComparison.Ordinal)
                || !string.Equals(destinationWorker, destinationWorkerClientInstanceId, StringComparison.Ordinal))
            {
                continue;
            }

            var cooldownEndsAt = migrated.Migration.EffectiveAt.Quanta + cooldownQuanta;
            if (state.Time.Quanta >= cooldownEndsAt)
            {
                return null;
            }

            return new CampaignMigrationCooldownStatus(
                migrated.Migration.EffectiveAt.Quanta,
                cooldownEndsAt,
                state.Time.Quanta);
        }

        return null;
    }

    private static bool TryResolveWorkerSet(
        CampaignState state,
        TacticalIslandMigratedEvent migrated,
        out string sourceWorkerClientInstanceId,
        out string destinationWorkerClientInstanceId)
    {
        sourceWorkerClientInstanceId = string.Empty;
        destinationWorkerClientInstanceId = string.Empty;
        if (migrated.Migration.Members.Count < 1)
        {
            return false;
        }

        var first = migrated.Migration.Members[0];
        if (!state.Assignments.TryGetValue(first.SourceAssignmentId, out var source)
            || !state.Assignments.TryGetValue(first.DestinationAssignmentId, out var destination)
            || string.IsNullOrWhiteSpace(source.WorkerClientInstanceId)
            || string.IsNullOrWhiteSpace(destination.WorkerClientInstanceId))
        {
            return false;
        }

        sourceWorkerClientInstanceId = source.WorkerClientInstanceId;
        destinationWorkerClientInstanceId = destination.WorkerClientInstanceId;
        foreach (var member in migrated.Migration.Members)
        {
            if (!state.Assignments.TryGetValue(member.SourceAssignmentId, out var memberSource)
                || !state.Assignments.TryGetValue(member.DestinationAssignmentId, out var memberDestination)
                || !string.Equals(memberSource.WorkerClientInstanceId, sourceWorkerClientInstanceId, StringComparison.Ordinal)
                || !string.Equals(memberDestination.WorkerClientInstanceId, destinationWorkerClientInstanceId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
