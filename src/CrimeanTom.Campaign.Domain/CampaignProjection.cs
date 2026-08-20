using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.Domain;

public sealed record FriendlyEntityProjection(
    EntityId EntityId,
    string Name,
    string CatalogUnitId,
    RegionId RegionId,
    GeoCoordinate Coordinate,
    BaseId? HomeBaseId,
    EntityLifecycle Lifecycle,
    FidelityState Fidelity,
    GroupId? GroupId,
    IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources,
    HeadingMilliDegrees Heading,
    int Telegraph,
    int ReadinessMilliUnits,
    int DamageMilliUnits,
    EntityRevision Revision);

public sealed record ContactProjection(
    EntityId ContactEntityId,
    RegionId EstimatedRegionId,
    GeoCoordinate EstimatedCoordinate,
    IntelligenceConfidence Confidence,
    CampaignTime ObservedAt,
    EntityRevision Revision);

public sealed record CoalitionCampaignProjection(
    CampaignId CampaignId,
    CoalitionId CoalitionId,
    CampaignTime Time,
    CommitPosition Head,
    IReadOnlyList<FriendlyEntityProjection> FriendlyEntities,
    IReadOnlyList<ContactProjection> Contacts,
    IReadOnlyList<WeatherState> Weather);

public static class CampaignProjectionBuilder
{
    public static CoalitionCampaignProjection ForCoalition(CampaignState state, CoalitionId coalitionId)
    {
        ArgumentNullException.ThrowIfNull(state);
        CampaignInvariants.RequireId(coalitionId);

        var friendly = state.Entities.Values
            .Where(entity => entity.CoalitionId == coalitionId)
            .Select(entity => new FriendlyEntityProjection(
                entity.EntityId,
                entity.Name,
                entity.CatalogUnitId,
                entity.RegionId,
                entity.Coordinate,
                entity.HomeBaseId,
                entity.Lifecycle,
                entity.Fidelity,
                state.Groups.Values.FirstOrDefault(group => group.Members.Contains(entity.EntityId))?.GroupId,
                entity.Resources,
                entity.Heading,
                entity.Telegraph,
                entity.ReadinessMilliUnits,
                entity.DamageMilliUnits,
                entity.Revision))
            .ToArray();

        var contacts = state.Intelligence.Values
            .Where(estimate => estimate.Key.ObserverCoalitionId == coalitionId)
            .Select(estimate => new ContactProjection(
                estimate.Key.ContactEntityId,
                estimate.EstimatedRegionId,
                estimate.EstimatedCoordinate,
                estimate.Confidence,
                estimate.ObservedAt,
                estimate.Revision))
            .ToArray();

        return new CoalitionCampaignProjection(
            state.Metadata.CampaignId,
            coalitionId,
            state.Time,
            state.Head,
            friendly,
            contacts,
            state.Weather.Values.ToArray());
    }
}