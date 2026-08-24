using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignTacticalWorkerProjectionHttp
{
    public static CampaignTacticalWorkerProjectionView From(
        CampaignState state,
        string clientInstanceId,
        string? advertisedCoalitionId)
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
        // tactical assignments. Released assignment history is durable and a
        // worker is forbidden from crossing coalition boundaries below.
        var assignmentCoalitions = workerAssignments
            .Select(item => state.Entities[item.EntityId].CoalitionId)
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
        if (assignmentCoalitions.Length > 1)
        {
            throw new CampaignStoreConflict(
                "worker-multiple-coalitions",
                "One tactical worker cannot hold live assignments for multiple coalitions.");
        }

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
        if (advertised is { } claimed
            && assignmentCoalitions.Length == 1
            && assignmentCoalitions[0] != claimed)
        {
            throw new CampaignStoreConflict(
                "worker-coalition-conflict",
                "The tactical worker cannot cross its advertised coalition boundary.");
        }
        var coalition = assignmentCoalitions.Length == 1
            ? assignmentCoalitions[0]
            : advertised;

        var friendly = coalition is null
            ? []
            : state.Entities.Values
                .Where(item => item.CoalitionId == coalition.Value)
                .OrderBy(item => item.EntityId)
                .Select(item => new CampaignTacticalFriendlyEntityView(
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
                    item.HostEntityId?.ToString()))
                .ToArray();
        var contacts = coalition is null
            ? []
            : CampaignProjectionBuilder.ForCoalition(state, coalition.Value).Contacts
                .Select(item => new CampaignTacticalContactView(
                    item.TrackId,
                    item.EstimatedCoordinate.LatitudeMicrodegrees,
                    item.EstimatedCoordinate.LongitudeMicrodegrees,
                    item.Confidence.ToString(),
                    item.UncertaintyRadiusMeters,
                    item.Classification.ToString(),
                    item.ObservedAt.Quanta,
                    item.Revision.Value))
                .OrderBy(item => item.TrackId, StringComparer.Ordinal)
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
            contacts);
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
    IReadOnlyList<CampaignTacticalContactView> Contacts);

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
    string? HostEntityId);

public sealed record CampaignTacticalContactView(
    string TrackId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    string Confidence,
    int UncertaintyRadiusMeters,
    string Classification,
    long ObservedAtQuanta,
    long Revision);
