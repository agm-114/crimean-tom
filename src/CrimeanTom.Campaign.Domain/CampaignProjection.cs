using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.Domain;

public sealed record FriendlyEntityProjection(
    EntityId EntityId,
    string Name,
    string CatalogUnitId,
    string CatalogLoadoutId,
    RegionId RegionId,
    GeoCoordinate Coordinate,
    BaseId? HomeBaseId,
    EntityLifecycle Lifecycle,
    FidelityState Fidelity,
    SimulationDomain SimulationDomain,
    FidelityState MaximumFidelity,
    GroupId? GroupId,
    IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources,
    HeadingMilliDegrees Heading,
    int Telegraph,
    int ReadinessMilliUnits,
    int DamageMilliUnits,
    EntityRevision Revision,
    EntityId? HostEntityId = null);

public sealed record ContactProjection(
    string TrackId,
    RegionId EstimatedRegionId,
    GeoCoordinate EstimatedCoordinate,
    IntelligenceConfidence Confidence,
    int UncertaintyRadiusMeters,
    ContactClassification Classification,
    CampaignTime ObservedAt,
    EntityRevision Revision,
    SensorChannel Channel,
    TrackQuality Quality);

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
                entity.CatalogLoadoutId,
                entity.RegionId,
                entity.Coordinate,
                entity.HomeBaseId,
                entity.Lifecycle,
                entity.Fidelity,
                entity.SimulationDomain,
                entity.MaximumFidelity,
                state.Groups.Values.FirstOrDefault(group => group.Members.Contains(entity.EntityId))?.GroupId,
                entity.Resources,
                entity.Heading,
                entity.Telegraph,
                entity.ReadinessMilliUnits,
                entity.DamageMilliUnits,
                entity.Revision,
                entity.HostEntityId))
            .ToArray();

        var contacts = state.Intelligence.Values
            .Where(estimate => estimate.Key.ObserverCoalitionId == coalitionId)
            .Select(estimate => new ContactProjection(
                TrackId(state.Metadata.CampaignId, estimate.Key),
                estimate.EstimatedRegionId,
                estimate.EstimatedCoordinate,
                estimate.Confidence,
                estimate.UncertaintyRadiusMeters,
                estimate.Classification,
                estimate.ObservedAt,
                estimate.Revision,
                estimate.Channel,
                estimate.Quality))
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

    private static string TrackId(CampaignId campaignId, IntelligenceKey key)
    {
        var material = $"{campaignId.Value:N}:{key.ObserverCoalitionId.Value:N}:{key.ContactEntityId.Value:N}:contact-track";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"track:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
