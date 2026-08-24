using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class CampaignTacticalWorkerProjectionHttp
{
    public static CampaignTacticalWorkerProjectionView From(
        CampaignState state,
        string clientInstanceId)
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
        var coalitions = workerAssignments
            .Select(item => state.Entities[item.EntityId].CoalitionId)
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
        if (coalitions.Length > 1)
        {
            throw new CampaignStoreConflict(
                "worker-multiple-coalitions",
                "One tactical worker cannot hold live assignments for multiple coalitions.");
        }

        var friendly = coalitions.Length == 0
            ? []
            : state.Entities.Values
                .Where(item => item.CoalitionId == coalitions[0])
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
                    item.Telegraph))
                .ToArray();

        return new CampaignTacticalWorkerProjectionView(
            state.Metadata.CampaignId.ToString(),
            clientInstanceId,
            coalitions.Length == 0 ? null : coalitions[0].ToString(),
            state.Time.Quanta,
            state.Head.Value,
            assignments.Select(item => new CampaignTacticalAssignmentView(
                item.AssignmentId.ToString(),
                item.EntityId.ToString(),
                item.AuthorityEpoch.Value,
                item.Role.ToString(),
                item.Lifecycle.ToString())).ToArray(),
            friendly);
    }
}

public sealed record CampaignTacticalWorkerProjectionView(
    string CampaignId,
    string ClientInstanceId,
    string? CoalitionId,
    long TimeQuanta,
    long Head,
    IReadOnlyList<CampaignTacticalAssignmentView> Assignments,
    IReadOnlyList<CampaignTacticalFriendlyEntityView> FriendlyEntities);

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
    int Telegraph);
