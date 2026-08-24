using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignPlayerHttp
{
    public static CampaignPlayerView From(CampaignState state, CampaignPlayerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.CoalitionId)
            || !Guid.TryParseExact(profile.CoalitionId, "N", out var coalitionValue))
        {
            throw new CampaignStoreConflict(
                "player-coalition-unassigned",
                "The local player campaign has no coalition assignment.");
        }

        var coalitionId = new CoalitionId(coalitionValue);
        var projection = CampaignProjectionBuilder.ForCoalition(state, coalitionId);
        var friendlyIds = projection.FriendlyEntities
            .Select(item => item.EntityId)
            .ToHashSet();
        var groups = state.Groups.Values
            .Where(item => item.CoalitionId == coalitionId)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GroupId.Value)
            .Select(item => new CampaignPlayerGroupView(
                item.GroupId.ToString(),
                item.Name,
                item.Members.Where(friendlyIds.Contains).Select(id => id.ToString()).ToArray(),
                item.ActiveOrderId?.ToString(),
                item.Revision.Value))
            .ToArray();
        var bases = state.Bases.Values
            .Where(item => item.CoalitionId == coalitionId)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new CampaignPlayerBaseView(
                item.BaseId.ToString(),
                item.Name,
                item.RegionId.ToString(),
                item.Coordinate.LatitudeMicrodegrees,
                item.Coordinate.LongitudeMicrodegrees))
            .ToArray();

        return new CampaignPlayerView(
            state.Metadata.CampaignId.ToString(),
            state.Metadata.Name,
            "ww3-plus-1989",
            state.Metadata.SchemaVersion,
            state.Metadata.AlgorithmVersion,
            state.Metadata.CatalogFingerprint,
            profile.PlayerId,
            coalitionId.ToString(),
            state.Time.Quanta,
            state.Head.Value,
            profile.Paused,
            profile.LastCursor,
            profile.LastSavedHead,
            profile.LastSavedAt,
            "coalition-filtered; enemy canonical state is not serialized",
            state.Entities.Count,
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
            groups,
            bases,
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

public sealed record CampaignPlayerView(
    string CampaignId,
    string Name,
    string ScenarioId,
    int SaveSchemaVersion,
    int AlgorithmVersion,
    string CatalogFingerprint,
    string PlayerId,
    string CoalitionId,
    long TimeQuanta,
    long Head,
    bool Paused,
    long LastCursor,
    long? LastSavedHead,
    DateTimeOffset? LastSavedAt,
    string TrustBoundary,
    int ScenarioEntityCount,
    IReadOnlyList<CampaignFriendlyProjectionView> FriendlyEntities,
    IReadOnlyList<CampaignPlayerGroupView> FriendlyGroups,
    IReadOnlyList<CampaignPlayerBaseView> FriendlyBases,
    IReadOnlyList<CampaignContactProjectionView> Contacts);

public sealed record CampaignPlayerGroupView(
    string GroupId,
    string Name,
    IReadOnlyList<string> MemberEntityIds,
    string? ActiveOrderId,
    long Revision);

public sealed record CampaignPlayerBaseView(
    string BaseId,
    string Name,
    string RegionId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees);
