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
    private readonly object _gate = new();
    private readonly Dictionary<CampaignId, MigrationOrchestrationLease> _active = new();

    public bool TryAcquire(MigrationOrchestrationLease requested)
    {
        lock (_gate)
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

            _active[requested.CampaignId] = requested;
            return true;
        }
    }

    public bool IsHeld(MigrationOrchestrationLease requested)
    {
        lock (_gate)
        {
            return _active.TryGetValue(requested.CampaignId, out var active)
                && active.SourceWorkerClientInstanceId == requested.SourceWorkerClientInstanceId
                && active.DestinationWorkerClientInstanceId == requested.DestinationWorkerClientInstanceId;
        }
    }

    public void Release(MigrationOrchestrationLease requested)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(requested.CampaignId, out var active)
                && active.SourceWorkerClientInstanceId == requested.SourceWorkerClientInstanceId
                && active.DestinationWorkerClientInstanceId == requested.DestinationWorkerClientInstanceId)
            {
                _active.Remove(requested.CampaignId);
            }
        }
    }

    public void ReleaseForCampaign(CampaignId campaignId)
    {
        lock (_gate)
        {
            _active.Remove(campaignId);
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
        int cooldownQuanta,
        CancellationToken cancellationToken)
    {
        if (cooldownQuanta <= 0)
        {
            return null;
        }

        var events = await journal.ReadEventsAsync(
            state.Metadata.CampaignId,
            CommitPosition.Origin,
            cancellationToken);
        if (events
                .Where(item => item.Data is TacticalIslandMigratedEvent)
                .OrderByDescending(item => item.CommitPosition.Value)
                .FirstOrDefault()?.Data is not TacticalIslandMigratedEvent migrated)
        {
            return null;
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
}
