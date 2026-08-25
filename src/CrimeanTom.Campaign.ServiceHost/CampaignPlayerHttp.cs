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
        var tacticallyOwnedIds = state.Assignments.Values
            .Where(item => item.Lifecycle is not AssignmentLifecycle.Released)
            .Select(item => item.EntityId)
            .ToHashSet();
        var groups = state.Groups.Values
            .Where(item => item.CoalitionId == coalitionId)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.GroupId.Value)
            .Select(item =>
            {
                var memberIds = item.Members.Where(friendlyIds.Contains).ToArray();
                var members = memberIds.Select(id => state.Entities[id]).ToArray();
                var currentRegionId = members
                    .GroupBy(member => member.RegionId)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .Select(group => group.Key)
                    .FirstOrDefault();
                var activeOrder = item.ActiveOrderId is { } activeOrderId
                    && state.Orders.TryGetValue(activeOrderId, out var found)
                        ? found
                        : null;
                var playerControlled = activeOrder?.MustCompleteBy is null;
                var atDestination = activeOrder is not null
                    && members.Length > 0
                    && members.All(member => member.RegionId == activeOrder.DestinationRegionId);
                var orderStatus = activeOrder is null ? "Awaiting orders"
                    : playerControlled && activeOrder.Kind == OrderKind.Hold ? "Holding"
                    : playerControlled && atDestination ? "Holding at destination"
                    : playerControlled ? "Moving"
                    : $"AI {activeOrder.Kind}";
                var canMove = members.Length > 0
                    && members.All(member => member.SimulationDomain != SimulationDomain.Installation)
                    && members.All(member => !tacticallyOwnedIds.Contains(member.EntityId));
                return new CampaignPlayerGroupView(
                    item.GroupId.ToString(),
                    item.Name,
                    memberIds.Select(id => id.ToString()).ToArray(),
                    currentRegionId.ToString(),
                    item.ActiveOrderId?.ToString(),
                    activeOrder?.Kind.ToString(),
                    activeOrder?.DestinationRegionId.ToString(),
                    playerControlled,
                    orderStatus,
                    canMove,
                    item.Revision.Value);
            })
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

        var objectives = state.StrategicObjectives.Values
            .Where(item => item.CoalitionId == coalitionId)
            .OrderByDescending(item => item.PriorityMilliUnits)
            .ThenBy(item => item.ObjectiveId)
            .Select(item =>
            {
                var assigned = groups.Where(group =>
                    string.Equals(
                        group.ActiveOrderDestinationRegionId,
                        item.RegionId.ToString(),
                        StringComparison.Ordinal)).ToArray();
                return new CampaignPlayerObjectiveView(
                    item.ObjectiveId.ToString(),
                    item.Kind.ToString(),
                    item.RegionId.ToString(),
                    state.Regions[item.RegionId].Name,
                    item.PriorityMilliUnits,
                    assigned.Length,
                    assigned.Count(group => group.PlayerControlled));
            })
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
                item.Revision.Value,
                item.HostEntityId?.ToString())).ToArray(),
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
                item.Revision.Value)).ToArray(),
            objectives);
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
    IReadOnlyList<CampaignContactProjectionView> Contacts,
    IReadOnlyList<CampaignPlayerObjectiveView> Objectives);

public sealed record CampaignPlayerGroupView(
    string GroupId,
    string Name,
    IReadOnlyList<string> MemberEntityIds,
    string CurrentRegionId,
    string? ActiveOrderId,
    string? ActiveOrderKind,
    string? ActiveOrderDestinationRegionId,
    bool PlayerControlled,
    string OrderStatus,
    bool CanMove,
    long Revision);

public sealed record CampaignPlayerBaseView(
    string BaseId,
    string Name,
    string RegionId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees);

public sealed record CampaignPlayerObjectiveView(
    string ObjectiveId,
    string Kind,
    string RegionId,
    string RegionName,
    int PriorityMilliUnits,
    int AssignedGroupCount,
    int PlayerAssignedGroupCount);

public sealed record CampaignPlayerOrderResult(
    string Status,
    string OrderId,
    CampaignPlayerView Campaign);

public sealed record CampaignPlayerTacticalEntryView(
    string CampaignId,
    string GroupId,
    string GroupName,
    long GroupRevision,
    string State,
    string Message,
    bool WorkerConnected,
    bool CanEnter,
    int RequiredMemberCount,
    int SupportedMemberCount,
    int AvailableTacticalCapacity,
    string? WorkerClientInstanceId,
    string? PlayerTaskforceId,
    IReadOnlyList<CampaignPlayerTacticalMemberView> Members);

public sealed record CampaignPlayerTacticalMemberView(
    string EntityId,
    string Name,
    string CatalogUnitId,
    string Status,
    string Message,
    string? HostEntityId = null);

public sealed record CampaignPlayerTacticalEntryResult(
    string Status,
    string BatchId,
    CampaignPlayerTacticalEntryView Entry);

public sealed record CampaignPlayerTacticalExitView(
    string CampaignId,
    string GroupId,
    string GroupName,
    long GroupRevision,
    string State,
    string Message,
    bool CanExit,
    string? BatchId,
    long CampaignTimeQuanta,
    IReadOnlyList<CampaignPlayerTacticalResultMemberView> Members,
    IReadOnlyList<string> UnsupportedFields);

public sealed record CampaignPlayerTacticalResultMemberView(
    string EntityId,
    string Name,
    string Lifecycle,
    string Fidelity,
    int DamageMilliunits,
    long FuelMilliunits,
    long StoresMilliunits,
    string AssignmentLifecycle,
    string? CheckpointSha256);

public sealed record CampaignPlayerTacticalExitResult(
    string Status,
    CampaignPlayerTacticalExitView Exit);
