using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignCoalitionProjectionHttp
{
    public static CampaignCoalitionProjectionView From(CampaignState state, CoalitionId coalitionId)
    {
        var projection = CampaignProjectionBuilder.ForCoalition(state, coalitionId);
        return new CampaignCoalitionProjectionView(
            projection.CampaignId.ToString(),
            projection.CoalitionId.ToString(),
            projection.Time.Quanta,
            projection.Head.Value,
            "coalition-filtered; no-classified-truth; render-proxy-is-not-a-contact",
            projection.FriendlyEntities.Select(item => new CampaignFriendlyProjectionView(
                item.EntityId.ToString(),
                item.Name,
                item.CatalogUnitId,
                item.RegionId.ToString(),
                item.Coordinate.LatitudeMicrodegrees,
                item.Coordinate.LongitudeMicrodegrees,
                item.Lifecycle.ToString(),
                item.Fidelity.ToString(),
                item.Revision.Value)).ToArray(),
            projection.Contacts.Select(item => new CampaignContactProjectionView(
                item.TrackId,
                item.EstimatedRegionId.ToString(),
                item.EstimatedCoordinate.LatitudeMicrodegrees,
                item.EstimatedCoordinate.LongitudeMicrodegrees,
                item.Confidence.ToString(),
                item.UncertaintyRadiusMeters,
                item.Classification.ToString(),
                item.ObservedAt.Quanta,
                item.Revision.Value)).ToArray());
    }
}

public sealed record CampaignCoalitionProjectionView(
    string CampaignId,
    string CoalitionId,
    long TimeQuanta,
    long Head,
    string TrustBoundary,
    IReadOnlyList<CampaignFriendlyProjectionView> FriendlyEntities,
    IReadOnlyList<CampaignContactProjectionView> Contacts);

public sealed record CampaignFriendlyProjectionView(
    string EntityId,
    string Name,
    string CatalogUnitId,
    string RegionId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    string Lifecycle,
    string Fidelity,
    long Revision);

public sealed record CampaignContactProjectionView(
    string TrackId,
    string EstimatedRegionId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    string Confidence,
    int UncertaintyRadiusMeters,
    string Classification,
    long ObservedAtQuanta,
    long Revision);
