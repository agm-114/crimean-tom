using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal sealed record CampaignUnattendedSimulationOptions(
    bool Enabled,
    TimeSpan WallInterval,
    long QuantaPerTick)
{
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
    ILogger<CampaignUnattendedSimulationHostedService> logger) : BackgroundService
{
    private readonly CampaignUnattendedSimulationOptions _options = CampaignUnattendedSimulationOptions.Load();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Unattended kernel simulation is disabled.");
            return;
        }

        logger.LogInformation(
            "Unattended kernel simulation enabled: {Quanta} quanta every {Interval}.",
            _options.QuantaPerTick,
            _options.WallInterval);
        using var timer = new PeriodicTimer(_options.WallInterval);
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

                var campaignId = new CampaignId(parsed);
                if (coordinator.IsPaused(campaignId))
                {
                    continue;
                }

                try
                {
                    var recovered = await journal.RecoverAsync(campaignId, stoppingToken);
                    if (recovered.State.Entities.Count == 0)
                    {
                        continue;
                    }

                    var target = new CampaignTime(
                        checked(recovered.State.Time.Quanta + _options.QuantaPerTick));
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
                    logger.LogWarning(
                        exception,
                        "Unattended simulation failed for campaign {CampaignId}.",
                        campaign.CampaignId);
                }
            }
        }
    }
}
