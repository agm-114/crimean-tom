using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.ServiceHost;

/// <summary>
/// Reconstructs the complete native-simulation interaction islands from durable
/// assignments plus the runtime's observed unsafe-to-split relationships.
/// Assignment batches remain handoff transaction boundaries; they are not used
/// as a substitute for locality membership.
/// </summary>
public sealed class CampaignInteractionIslandRegistry(CampaignSessionRegistry sessions)
{
    public IReadOnlyList<CampaignInteractionIsland> Resolve(CampaignState state)
    {
        var assignments = state.Assignments.Values
            .Where(item => !CampaignAssignmentKinds.IsOperationalSim(item)
                && item.Lifecycle != AssignmentLifecycle.Released)
            .OrderBy(item => item.AssignmentId.Value)
            .ToArray();
        if (assignments.Length == 0)
        {
            return [];
        }

        var result = new List<CampaignInteractionIsland>();
        foreach (var workerAssignments in assignments
            .GroupBy(item => item.WorkerClientInstanceId, StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var worker = workerAssignments.ToArray();
            var components = new DisjointAssignmentSet(worker);

            foreach (var batch in worker
                .Where(item => !string.IsNullOrEmpty(item.BatchId))
                .GroupBy(item => item.BatchId, StringComparer.Ordinal))
            {
                components.UnionAll(batch.Select(item => item.EntityId));
            }

            foreach (var entity in worker.Select(item => state.Entities[item.EntityId]))
            {
                if (entity.HostEntityId is { } hostId)
                {
                    components.Union(entity.EntityId, hostId);
                }
            }

            var snapshot = sessions.SnapshotForClient(
                state.Metadata.CampaignId.ToString(),
                workerAssignments.Key);
            if (snapshot is not null)
            {
                foreach (var relationship in snapshot.CouplingRelationships
                    .Where(item => item.UnsafeToSplit && item.MissingSamples <= 1))
                {
                    if (TryParseEntityId(relationship.SourceEntityId, out var source)
                        && TryParseEntityId(relationship.TargetEntityId, out var target))
                    {
                        components.Union(source, target);
                    }
                }
            }

            foreach (var component in components.Components())
            {
                var componentAssignments = worker
                    .Where(item => component.Contains(item.EntityId))
                    .OrderBy(item => item.AssignmentId.Value)
                    .ToArray();
                var complete = HasCompleteHandoffTransactions(componentAssignments);
                var islandId = $"tactical-island:{componentAssignments[0].AssignmentId}";
                result.Add(new CampaignInteractionIsland(
                    islandId,
                    workerAssignments.Key,
                    componentAssignments,
                    complete));
            }
        }

        return result
            .OrderBy(item => item.WorkerClientInstanceId, StringComparer.Ordinal)
            .ThenBy(item => item.IslandId, StringComparer.Ordinal)
            .ToArray();
    }

    public AssignmentState[] ExpandFromEntities(
        CampaignState state,
        IEnumerable<EntityId> anchorEntityIds)
    {
        var anchors = anchorEntityIds.ToHashSet();
        if (anchors.Count == 0)
        {
            return [];
        }

        return Resolve(state)
            .Where(island => island.Assignments.Any(item => anchors.Contains(item.EntityId)))
            .SelectMany(island => island.Assignments)
            .DistinctBy(item => item.AssignmentId)
            .OrderBy(item => item.AssignmentId.Value)
            .ToArray();
    }

    public AssignmentState[] ExpandReleasedFromEntities(
        CampaignState state,
        IEnumerable<EntityId> anchorEntityIds)
    {
        var anchors = anchorEntityIds.ToHashSet();
        if (anchors.Count == 0)
        {
            return [];
        }

        var released = state.Assignments.Values
            .Where(item => !CampaignAssignmentKinds.IsOperationalSim(item)
                && item.Lifecycle == AssignmentLifecycle.Released)
            .OrderBy(item => item.AssignmentId.Value)
            .ToArray();
        var included = released
            .Where(item => anchors.Contains(item.EntityId))
            .Select(item => item.AssignmentId)
            .ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            var includedAssignments = released
                .Where(item => included.Contains(item.AssignmentId))
                .ToArray();
            var batchIds = includedAssignments
                .Select(item => item.BatchId)
                .Where(item => !string.IsNullOrEmpty(item))
                .ToHashSet(StringComparer.Ordinal);
            var entityIds = includedAssignments
                .Select(item => item.EntityId)
                .ToHashSet();
            var linkedEntityIds = entityIds
                .SelectMany(entityId =>
                {
                    var entity = state.Entities[entityId];
                    return entity.HostEntityId is { } hostId
                        ? new[] { entityId, hostId }
                        : new[] { entityId };
                })
                .ToHashSet();
            foreach (var entity in state.Entities.Values.Where(item =>
                item.HostEntityId is { } hostId && entityIds.Contains(hostId)))
            {
                linkedEntityIds.Add(entity.EntityId);
            }

            foreach (var assignment in released.Where(item =>
                batchIds.Contains(item.BatchId) || linkedEntityIds.Contains(item.EntityId)))
            {
                changed |= included.Add(assignment.AssignmentId);
            }
        }

        return released
            .Where(item => included.Contains(item.AssignmentId))
            .ToArray();
    }

    public bool AreCompleteIslands(CampaignState state, IReadOnlyCollection<AssignmentState> assignments)
    {
        if (assignments.Count == 0)
        {
            return false;
        }

        var requested = assignments.Select(item => item.AssignmentId).ToHashSet();
        var islands = Resolve(state)
            .Where(island => island.Assignments.Any(item => requested.Contains(item.AssignmentId)))
            .ToArray();
        return islands.Length > 0
            && islands.All(item => item.Complete)
            && islands.SelectMany(item => item.Assignments)
                .Select(item => item.AssignmentId)
                .ToHashSet()
                .SetEquals(requested);
    }

    private static bool HasCompleteHandoffTransactions(
        IReadOnlyCollection<AssignmentState> assignments) =>
        assignments.Count > 0
        && assignments
            .GroupBy(HandoffTransactionKey, StringComparer.Ordinal)
            .All(batch => batch.Count() == batch.First().BatchSize
                && batch.All(item => item.BatchSize == batch.First().BatchSize));

    private static string HandoffTransactionKey(AssignmentState assignment) =>
        string.IsNullOrEmpty(assignment.BatchId)
            ? $"assignment:{assignment.AssignmentId}"
            : assignment.BatchId;

    private static bool TryParseEntityId(string value, out EntityId entityId)
    {
        const string canonicalPrefix = "unit:crimean-tom:";
        const string stablePrefix = "campaign-unit:";
        entityId = default;
        var candidate = value.StartsWith(stablePrefix, StringComparison.Ordinal)
            ? value[stablePrefix.Length..]
            : value;
        if (candidate.StartsWith(canonicalPrefix, StringComparison.Ordinal))
        {
            candidate = candidate[canonicalPrefix.Length..];
        }
        if (!Guid.TryParseExact(candidate, "N", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }
        entityId = new EntityId(parsed);
        return true;
    }

    private sealed class DisjointAssignmentSet
    {
        private readonly Dictionary<EntityId, EntityId> _parents;

        public DisjointAssignmentSet(IEnumerable<AssignmentState> assignments)
        {
            _parents = assignments
                .Select(item => item.EntityId)
                .Distinct()
                .ToDictionary(item => item, item => item);
        }

        public void UnionAll(IEnumerable<EntityId> entityIds)
        {
            using var iterator = entityIds.Distinct().GetEnumerator();
            if (!iterator.MoveNext())
            {
                return;
            }
            var first = iterator.Current;
            while (iterator.MoveNext())
            {
                Union(first, iterator.Current);
            }
        }

        public void Union(EntityId left, EntityId right)
        {
            if (!_parents.ContainsKey(left) || !_parents.ContainsKey(right))
            {
                return;
            }
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                var first = leftRoot.CompareTo(rightRoot) <= 0 ? leftRoot : rightRoot;
                var second = leftRoot.CompareTo(rightRoot) <= 0 ? rightRoot : leftRoot;
                _parents[second] = first;
            }
        }

        public IReadOnlyList<HashSet<EntityId>> Components() => _parents.Keys
            .GroupBy(Find)
            .Select(group => group.ToHashSet())
            .OrderBy(group => group.Min())
            .ToArray();

        private EntityId Find(EntityId entityId)
        {
            var parent = _parents[entityId];
            if (parent == entityId)
            {
                return entityId;
            }
            var root = Find(parent);
            _parents[entityId] = root;
            return root;
        }
    }
}

public sealed record CampaignInteractionIsland(
    string IslandId,
    string WorkerClientInstanceId,
    IReadOnlyList<AssignmentState> Assignments,
    bool Complete);
