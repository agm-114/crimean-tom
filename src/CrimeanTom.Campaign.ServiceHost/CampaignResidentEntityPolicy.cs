using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignResidentEntityPolicy
{
    private const long StrategicAirDefenseRangeMeters = 100_000;

    public static bool RequiresContinuousTacticalPresence(EntityState entity)
    {
        if (entity.MaximumFidelity != FidelityState.Tactical
            || entity.Lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Retired
            || entity.SimulationDomain is not (SimulationDomain.Ground or SimulationDomain.Installation))
        {
            return false;
        }

        return entity.OperationalWeapons.Any(weapon =>
                weapon.TargetClassification == ContactClassification.Air
                && weapon.MaximumRange.Value >= StrategicAirDefenseRangeMeters)
            || entity.OperationalWeapons.Any(weapon =>
                weapon.AmmunitionId.Contains("ballistic", StringComparison.OrdinalIgnoreCase))
            || entity.SimulationDomain == SimulationDomain.Installation
                && (entity.CatalogUnitId.Contains("radar", StringComparison.OrdinalIgnoreCase)
                    || entity.CatalogUnitId.Contains("ewr", StringComparison.OrdinalIgnoreCase)
                    || entity.Name.Contains("early-warning radar", StringComparison.OrdinalIgnoreCase));
    }
}
