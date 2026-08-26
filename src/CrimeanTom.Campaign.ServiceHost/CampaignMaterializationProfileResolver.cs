using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignMaterializationProfileResolver
{
    public static CampaignMaterializationProfile? Resolve(
        IReadOnlyList<CampaignMaterializationProfile> profiles,
        EntityState entity)
    {
        // Capability order carries the runtime's canonical profile preference.
        // Re-sorting variants can make a saved checkpoint impossible to import.
        var exact = profiles.FirstOrDefault(profile =>
            string.Equals(profile.UnitTypeId, entity.CatalogUnitId, StringComparison.Ordinal)
            && string.Equals(profile.LoadoutId, entity.CatalogLoadoutId, StringComparison.Ordinal));
        if (exact is not null
            || !IsUnspecifiedLoadout(entity.CatalogLoadoutId))
        {
            return exact;
        }

        // Campaign seeds use "Default" to mean "use Sea Power's canonical
        // loadout". Many native aircraft and helicopters have no loadout with
        // that literal identifier, so preserve the runtime's advertised order
        // and select its first profile for the catalog unit.
        return profiles.FirstOrDefault(profile => string.Equals(
            profile.UnitTypeId,
            entity.CatalogUnitId,
            StringComparison.Ordinal));
    }

    public static bool Supports(
        CampaignSessionSnapshot worker,
        EntityState entity) =>
        Resolve(worker.MaterializationProfiles, entity) is not null;

    private static bool IsUnspecifiedLoadout(string loadoutId) =>
        string.IsNullOrWhiteSpace(loadoutId)
        || string.Equals(loadoutId, "Default", StringComparison.Ordinal);
}
