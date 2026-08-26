using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed record CampaignUnattendedSimulationOptions(
    bool Enabled,
    TimeSpan WallInterval,
    long QuantaPerTick)
{
    public double TargetTempoMultiplier =>
        QuantaPerTick * CampaignTime.SimulatedSecondsPerQuantum / WallInterval.TotalSeconds;

    public static CampaignUnattendedSimulationOptions Load()
    {
        var enabled = ParseEnabled(
            Environment.GetEnvironmentVariable("CRIMEAN_TOM_UNATTENDED_SIMULATION"));
        var wallMs = ParsePositiveInt(
            Environment.GetEnvironmentVariable("CRIMEAN_TOM_UNATTENDED_TICK_WALL_MS"),
            fallback: 1_000);
        var quanta = ParsePositiveInt(
            Environment.GetEnvironmentVariable("CRIMEAN_TOM_UNATTENDED_QUANTA_PER_TICK"),
            fallback: 10);
        return new CampaignUnattendedSimulationOptions(
            enabled,
            TimeSpan.FromMilliseconds(wallMs),
            quanta);
    }

    private static bool ParseEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value == "1"
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || bool.TryParse(value, out var enabled) && enabled;
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}

internal sealed class CampaignUnattendedSimulationHostedService(
    SqliteCampaignJournal journal,
    CampaignCoordinator coordinator,
    CampaignUnattendedSimulationOptions options,
    ILogger<CampaignUnattendedSimulationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTimeOffset> _failureRetryAfter =
        new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Unattended kernel simulation is disabled.");
            return;
        }

        logger.LogInformation(
            "Unattended kernel simulation enabled: {Quanta} quanta every {Interval}.",
            options.QuantaPerTick,
            options.WallInterval);
        using var timer = new PeriodicTimer(options.WallInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            IReadOnlyList<CampaignSummary> campaigns;
            try
            {
                campaigns = await journal.ListCampaignsAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Unattended simulation failed to list campaigns.");
                continue;
            }

            foreach (var campaign in campaigns)
            {
                if (!Guid.TryParse(campaign.CampaignId, out var parsed))
                {
                    continue;
                }
                if (_failureRetryAfter.TryGetValue(campaign.CampaignId, out var retryAfter)
                    && DateTimeOffset.UtcNow < retryAfter)
                {
                    continue;
                }

                var campaignId = new CampaignId(parsed);
                try
                {
                    var paused = await coordinator.IsPausedAsync(campaignId, stoppingToken);
                    var simulation = await coordinator.ReadUnattendedSimulationStateAsync(
                        campaignId,
                        stoppingToken);
                    _failureRetryAfter.Remove(campaign.CampaignId);
                    if (simulation.EntityCount == 0)
                    {
                        continue;
                    }
                    if (paused && !simulation.RequiresExpiredTacticalLeaseRecovery)
                    {
                        continue;
                    }

                    // Tactical leases expire on wall clock, independently of campaign
                    // time. A paused campaign must therefore release expired authority
                    // at its current quantum without advancing unrelated simulation.
                    var target = paused
                        ? simulation.Time
                        : new CampaignTime(
                            checked(simulation.Time.Quanta + options.QuantaPerTick));
                    _ = await coordinator.ResolveExpiredAssignmentsAsync(
                        campaignId,
                        target,
                        stoppingToken);
                }
                catch (CampaignStoreConflict exception)
                {
                    logger.LogDebug(
                        exception,
                        "Unattended simulation deferred campaign {CampaignId}.",
                        campaign.CampaignId);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var retryAt = DateTimeOffset.UtcNow + FailureRetryDelay;
                    _failureRetryAfter[campaign.CampaignId] = retryAt;
                    logger.LogWarning(
                        exception,
                        "Unattended simulation failed for campaign {CampaignId}; retry deferred until {RetryAt}.",
                        campaign.CampaignId,
                        retryAt);
                }
            }
        }
    }
}
