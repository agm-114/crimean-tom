using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignTacticalWorkerProjectionHttp
{
    public static CampaignTacticalWorkerProjectionView From(
        CampaignState state,
        string clientInstanceId,
        string? advertisedCoalitionId,
        bool debugRevealAllEnemies = false)
    {
        var workerAssignments = state.Assignments.Values
            .Where(item => string.Equals(
                    item.WorkerClientInstanceId,
                    clientInstanceId,
                    StringComparison.Ordinal)
                && item.Role is AssignmentRole.Primary or AssignmentRole.Shadow)
            .OrderBy(item => item.AssignmentId.Value)
            .ToArray();
        var assignments = workerAssignments
            .Where(item => item.Lifecycle != AssignmentLifecycle.Released)
            .ToArray();
        // Keep the player's coalition-scoped visual theater stable between
        // tactical assignments. Released assignment history is durable; an
        // interaction island may also contain opposing units, but its advertised
        // player coalition must remain represented in the assignment history.
        var assignmentCoalitions = workerAssignments
            .Select(item => state.Entities[item.EntityId].CoalitionId)
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
        CoalitionId? advertised = null;
        if (!string.IsNullOrWhiteSpace(advertisedCoalitionId))
        {
            if (!Guid.TryParseExact(advertisedCoalitionId, "N", out var parsed)
                || !state.Entities.Values.Any(item => item.CoalitionId.Value == parsed)
                    && !state.Bases.Values.Any(item => item.CoalitionId.Value == parsed))
            {
                throw new CampaignStoreConflict(
                    "worker-coalition-missing",
                    "The tactical worker advertised a coalition that is absent from the campaign.");
            }
            advertised = new CoalitionId(parsed);
        }
        if (advertised is null && assignmentCoalitions.Length > 1)
        {
            throw new CampaignStoreConflict(
                "worker-player-coalition-required",
                "A multi-coalition tactical island requires an explicit player coalition.");
        }
        if (advertised is { } claimed
            && assignmentCoalitions.Length > 0
            && !assignmentCoalitions.Contains(claimed))
        {
            throw new CampaignStoreConflict(
                "worker-coalition-conflict",
                "The tactical worker does not hold a player-side assignment for its advertised coalition.");
        }
        var coalition = advertised ?? (assignmentCoalitions.Length == 1
            ? assignmentCoalitions[0]
            : (CoalitionId?)null);

        var groupByEntity = state.Groups.Values
            .OrderBy(group => group.GroupId)
            .SelectMany(group => group.Members.Select(entityId => (entityId, group)))
            .GroupBy(item => item.entityId)
            .ToDictionary(group => group.Key, group => group.First().group);
        var friendly = coalition is null
            ? []
            : state.Entities.Values
                .Where(item => item.CoalitionId == coalition.Value)
                .OrderBy(item => item.EntityId)
                .Select(item =>
                {
                    groupByEntity.TryGetValue(item.EntityId, out var group);
                    return new CampaignTacticalFriendlyEntityView(
                        item.EntityId.ToString(),
                        item.Name,
                        item.CatalogUnitId,
                        item.CatalogLoadoutId,
                        item.SimulationDomain.ToString(),
                        item.Lifecycle.ToString(),
                        item.Coordinate.LatitudeMicrodegrees,
                        item.Coordinate.LongitudeMicrodegrees,
                        item.Heading.Value,
                        item.Telegraph,
                        item.HostEntityId?.ToString(),
                        group?.GroupId.ToString(),
                        group?.Name,
                        group?.Revision.Value ?? 0);
                })
                .ToArray();
        var localTacticalEntityIds = assignments
            .Select(item => item.EntityId)
            .ToHashSet();
        var contacts = coalition is null
            ? []
            : state.Intelligence.Values
                .Where(item => item.Key.ObserverCoalitionId == coalition.Value)
                .Select(item => new CampaignTacticalContactView(
                    CampaignProjectionBuilder.ContactTrackId(state.Metadata.CampaignId, item.Key),
                    item.EstimatedCoordinate.LatitudeMicrodegrees,
                    item.EstimatedCoordinate.LongitudeMicrodegrees,
                    item.Confidence.ToString(),
                    item.UncertaintyRadiusMeters,
                    item.Classification.ToString(),
                    item.ObservedAt.Quanta,
                    item.Revision.Value,
                    localTacticalEntityIds.Contains(item.Key.ContactEntityId)))
                .OrderBy(item => item.TrackId, StringComparer.Ordinal)
                .ToArray();
        var debugHostileEntities = !debugRevealAllEnemies || coalition is null
            ? []
            : state.Entities.Values
                .Where(item => item.CoalitionId != coalition.Value)
                .OrderBy(item => item.EntityId)
                .Select(item => new CampaignTacticalDebugHostileEntityView(
                    item.EntityId.ToString(),
                    item.Name,
                    item.CatalogUnitId,
                    item.SimulationDomain.ToString(),
                    item.Lifecycle.ToString(),
                    item.Coordinate.LatitudeMicrodegrees,
                    item.Coordinate.LongitudeMicrodegrees,
                    item.Heading.Value))
                .ToArray();

        return new CampaignTacticalWorkerProjectionView(
            state.Metadata.CampaignId.ToString(),
            clientInstanceId,
            coalition?.ToString(),
            state.Time.Quanta,
            state.Head.Value,
            assignments.Select(item => new CampaignTacticalAssignmentView(
                item.AssignmentId.ToString(),
                item.EntityId.ToString(),
                item.AuthorityEpoch.Value,
                item.Role.ToString(),
                item.Lifecycle.ToString())).ToArray(),
            friendly,
            contacts,
            debugHostileEntities);
    }
}

public sealed record CampaignTacticalWorkerProjectionView(
    string CampaignId,
    string ClientInstanceId,
    string? CoalitionId,
    long TimeQuanta,
    long Head,
    IReadOnlyList<CampaignTacticalAssignmentView> Assignments,
    IReadOnlyList<CampaignTacticalFriendlyEntityView> FriendlyEntities,
    IReadOnlyList<CampaignTacticalContactView> Contacts,
    IReadOnlyList<CampaignTacticalDebugHostileEntityView> DebugHostileEntities);

public sealed record CampaignTacticalAssignmentView(
    string AssignmentId,
    string EntityId,
    long AuthorityEpoch,
    string Role,
    string Lifecycle);

public sealed record CampaignTacticalFriendlyEntityView(
    string EntityId,
    string Name,
    string CatalogUnitId,
    string CatalogLoadoutId,
    string SimulationDomain,
    string Lifecycle,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    int HeadingMilliDegrees,
    int Telegraph,
    string? HostEntityId,
    string? GroupId,
    string? GroupName,
    long GroupRevision);

public sealed record CampaignTacticalContactView(
    string TrackId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    string Confidence,
    int UncertaintyRadiusMeters,
    string Classification,
    long ObservedAtQuanta,
    long Revision,
    bool IsLocallyTactical);

public sealed record CampaignTacticalDebugHostileEntityView(
    string EntityId,
    string Name,
    string CatalogUnitId,
    string SimulationDomain,
    string Lifecycle,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    int HeadingMilliDegrees);
