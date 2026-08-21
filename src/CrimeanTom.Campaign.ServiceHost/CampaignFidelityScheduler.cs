using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

public enum SchedulerHealthState
{
    Healthy,
    Pressured,
    Overloaded,
    Draining,
    Quarantined,
    Offline,
}

public enum SimulationSourceEligibility
{
    Eligible,
    DrainOnly,
    Disabled,
    Quarantined,
}

public sealed record CampaignSchedulerRequest(
    IReadOnlyList<string> ObservedEntityIds,
    IReadOnlyList<string> ControlledEntityIds,
    SchedulerHealthState Health,
    SimulationSourceEligibility Eligibility,
    int TacticalEntityCapacity,
    int BacklogDepth);

public sealed record CampaignSchedulerView(
    string Health,
    string Eligibility,
    int TacticalEntityCapacity,
    int BacklogDepth,
    int FixedStepMicroseconds,
    int TargetTicksPerWallSecond,
    long EffectiveAtQuanta,
    string LoadAction,
    IReadOnlyList<CampaignInteractionIslandView> Islands,
    IReadOnlyList<CampaignFidelityTransitionView> Transitions);

public sealed record CampaignInteractionIslandView(
    string IslandId,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<string> GroupIds,
    string CurrentFidelity,
    string DesiredFidelity,
    bool Engaged,
    int CouplingScoreMilliunits,
    string Reason);

public sealed record CampaignFidelityTransitionView(
    string IslandId,
    string EntityId,
    string FromFidelity,
    string ToFidelity,
    string Reason);

internal static class CampaignFidelityScheduler
{
    private const long InteractionFenceQuanta = 20;
    private const int FixedStepMicroseconds = 33_333;

    public static SchedulerEvaluation Inspect(CampaignState state)
    {
        var islands = BuildIslands(state).Select(island => new SchedulerIslandPlan(
            island.IslandId,
            island.EntityIds,
            island.GroupIds,
            CurrentFidelity(state, island.EntityIds),
            CurrentFidelity(state, island.EntityIds),
            island.Engaged,
            island.CouplingScoreMilliunits,
            "current-canonical-state")).ToArray();
        return new SchedulerEvaluation(
            new CampaignSchedulerRequest([], [], SchedulerHealthState.Offline,
                SimulationSourceEligibility.Disabled, 0, 0),
            0,
            "scheduler-not-evaluated",
            islands,
            []);
    }

    public static SchedulerEvaluation Evaluate(
        CampaignState state,
        CampaignSchedulerRequest request,
        CampaignSessionSnapshot? worker)
    {
        if (!Enum.IsDefined(request.Health) || !Enum.IsDefined(request.Eligibility)
            || request.TacticalEntityCapacity is < 0 or > 10_000
            || request.BacklogDepth is < 0 or > 1_000_000)
        {
            throw new CampaignStoreConflict("invalid-scheduler-input", "Scheduler health, eligibility, capacity, or backlog is invalid.");
        }

        var observed = ParseEntities(state, request.ObservedEntityIds, "observed");
        var controlled = ParseEntities(state, request.ControlledEntityIds, "controlled");
        var interested = observed.Concat(controlled).ToHashSet();
        var remainingCapacity = request.TacticalEntityCapacity;
        var plans = new List<SchedulerIslandPlan>();
        var transitions = new List<CampaignFidelityTransitionView>();
        foreach (var island in BuildIslands(state)
            .OrderByDescending(item => item.EntityIds.Any(interested.Contains))
            .ThenBy(item => item.IslandId, StringComparer.Ordinal))
        {
            var current = CurrentFidelity(state, island.EntityIds);
            var hasInterest = island.EntityIds.Any(interested.Contains);
            var hasActiveAssignment = state.Assignments.Values.Any(item => island.EntityIds.Contains(item.EntityId)
                && item.Lifecycle is AssignmentLifecycle.Preparing or AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing);
            var tacticalCapable = island.EntityIds.All(id => state.Entities[id].MaximumFidelity == FidelityState.Tactical);
            var workerConnected = worker is not null
                && string.Equals(worker.CampaignId, state.Metadata.CampaignId.ToString(), StringComparison.Ordinal);
            var workerHasTaskforce = workerConnected && !string.IsNullOrWhiteSpace(worker!.PlayerTaskforceId);
            var workerCanMaterializeIsland = workerConnected && island.EntityIds.All(id =>
                worker!.MaterializationProfiles.Any(profile => string.Equals(
                    profile.UnitTypeId,
                    state.Entities[id].CatalogUnitId,
                    StringComparison.Ordinal)));
            FidelityState desired;
            string reason;
            if (hasActiveAssignment)
            {
                desired = FidelityState.Tactical;
                reason = "active-authority-fence";
            }
            else if (current == FidelityState.Tactical && island.Engaged)
            {
                desired = FidelityState.Tactical;
                reason = "active-interaction-fence";
            }
            else if (request.Health is SchedulerHealthState.Overloaded or SchedulerHealthState.Draining
                or SchedulerHealthState.Quarantined or SchedulerHealthState.Offline
                || request.Eligibility != SimulationSourceEligibility.Eligible
                || request.BacklogDepth >= 192)
            {
                desired = current == FidelityState.Strategic
                    ? FidelityState.Strategic
                    : FidelityState.Operational;
                reason = request.Eligibility != SimulationSourceEligibility.Eligible
                    ? $"source-{request.Eligibility.ToString().ToLowerInvariant()}"
                    : request.BacklogDepth >= 192 ? "transport-backlog-load-shed"
                    : $"client-{request.Health.ToString().ToLowerInvariant()}";
            }
            else if (hasInterest && tacticalCapable && island.EntityIds.Count <= remainingCapacity)
            {
                desired = FidelityState.Tactical;
                remainingCapacity -= island.EntityIds.Count;
                reason = controlled.Overlaps(island.EntityIds) ? "player-control" : "player-observation";
            }
            else if (hasInterest && !tacticalCapable)
            {
                desired = FidelityState.Operational;
                reason = "simulation-domain-ceiling";
            }
            else if (hasInterest && !workerConnected)
            {
                desired = FidelityState.Operational;
                reason = "tactical-worker-offline";
            }
            else if (hasInterest && !workerHasTaskforce)
            {
                desired = FidelityState.Operational;
                reason = "player-taskforce-unavailable";
            }
            else if (hasInterest && !workerCanMaterializeIsland)
            {
                desired = FidelityState.Operational;
                reason = "worker-materialization-ceiling";
            }
            else if (hasInterest)
            {
                desired = FidelityState.Operational;
                reason = "tactical-capacity-exhausted";
            }
            else if (island.Engaged || island.EntityIds.Any(id => state.Entities[id].SimulationDomain == SimulationDomain.LandAbstract))
            {
                desired = FidelityState.Operational;
                reason = island.Engaged ? "operational-engagement" : "land-operational-model";
            }
            else
            {
                desired = FidelityState.Strategic;
                reason = "outside-player-interest";
            }

            plans.Add(new SchedulerIslandPlan(island.IslandId, island.EntityIds, island.GroupIds,
                current, desired, island.Engaged, island.CouplingScoreMilliunits, reason));
            foreach (var entityId in island.EntityIds.Where(id => state.Entities[id].Fidelity != desired
                && state.Entities[id].Fidelity != FidelityState.Suspended))
            {
                transitions.Add(new CampaignFidelityTransitionView(
                    island.IslandId,
                    entityId.ToString(),
                    state.Entities[entityId].Fidelity.ToString(),
                    desired.ToString(),
                    reason));
            }
        }

        var tempo = request.Health switch
        {
            SchedulerHealthState.Healthy when request.BacklogDepth < 96 => 30,
            SchedulerHealthState.Pressured or SchedulerHealthState.Healthy => 20,
            _ => 10,
        };
        var action = request.Health == SchedulerHealthState.Healthy && request.BacklogDepth < 96
            ? "normal"
            : transitions.Any(item => item.ToFidelity != FidelityState.Tactical.ToString())
                ? "demote-whole-islands-before-tempo"
                : "global-tempo-reduced";
        return new SchedulerEvaluation(request, tempo, action, plans, transitions);
    }

    public static CampaignSchedulerView ToView(CampaignState state, SchedulerEvaluation evaluation) => new(
        evaluation.Request.Health.ToString(),
        evaluation.Request.Eligibility.ToString(),
        evaluation.Request.TacticalEntityCapacity,
        evaluation.Request.BacklogDepth,
        FixedStepMicroseconds,
        evaluation.TargetTicksPerWallSecond,
        state.Time.Quanta,
        evaluation.LoadAction,
        evaluation.Islands.Select(item => new CampaignInteractionIslandView(
            item.IslandId,
            item.EntityIds.Select(id => id.ToString()).ToArray(),
            item.GroupIds.Select(id => id.ToString()).ToArray(),
            item.CurrentFidelity.ToString(),
            item.DesiredFidelity.ToString(),
            item.Engaged,
            item.CouplingScoreMilliunits,
            item.Reason)).ToArray(),
        evaluation.Transitions);

    private static IReadOnlyList<Island> BuildIslands(CampaignState state)
    {
        var groupNodes = state.Groups.Values.OrderBy(item => item.GroupId).ToArray();
        var parents = Enumerable.Range(0, groupNodes.Length).ToArray();
        int Find(int value) => parents[value] == value ? value : parents[value] = Find(parents[value]);
        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left == right) return;
            if (left < right) parents[right] = left; else parents[left] = right;
        }

        var recentCombatRegions = state.RecentOperationalOutcomes
            .Where(item => state.Time.Quanta - item.OccurredAt.Quanta <= InteractionFenceQuanta
                && item.Kind is OperationalOutcomeKind.WeaponExpenditure or OperationalOutcomeKind.Damage
                    or OperationalOutcomeKind.Destruction)
            .Select(item => item.RegionId)
            .ToHashSet();
        for (var left = 0; left < groupNodes.Length; left++)
        {
            for (var right = left + 1; right < groupNodes.Length; right++)
            {
                if (groupNodes[left].CoalitionId == groupNodes[right].CoalitionId) continue;
                var leftRegions = groupNodes[left].Members.Select(id => state.Entities[id].RegionId).ToHashSet();
                var rightRegions = groupNodes[right].Members.Select(id => state.Entities[id].RegionId).ToHashSet();
                if (leftRegions.Intersect(rightRegions).Any(recentCombatRegions.Contains)) Union(left, right);
            }
        }

        var islands = groupNodes.Select((group, index) => (group, root: Find(index)))
            .GroupBy(item => item.root)
            .Select(items => CreateIsland(state,
                items.SelectMany(item => item.group.Members).Distinct().OrderBy(id => id).ToArray(),
                items.Select(item => item.group.GroupId).OrderBy(id => id).ToArray(),
                recentCombatRegions))
            .ToList();
        var grouped = state.Groups.Values.SelectMany(item => item.Members).ToHashSet();
        islands.AddRange(state.Entities.Keys.Where(id => !grouped.Contains(id))
            .Select(id => CreateIsland(state, [id], [], recentCombatRegions)));
        return islands.OrderBy(item => item.IslandId, StringComparer.Ordinal).ToArray();
    }

    private static Island CreateIsland(
        CampaignState state,
        IReadOnlyList<EntityId> entityIds,
        IReadOnlyList<GroupId> groupIds,
        IReadOnlySet<RegionId> combatRegions)
    {
        var engaged = entityIds.Any(id => combatRegions.Contains(state.Entities[id].RegionId));
        var material = string.Join(':', groupIds.Count > 0
            ? groupIds.Select(id => id.Value.ToString("N"))
            : entityIds.Select(id => id.Value.ToString("N")));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{state.Metadata.CampaignId.Value:N}:island:{material}"));
        return new Island(
            Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant(),
            entityIds,
            groupIds,
            engaged,
            engaged ? 1_000 : groupIds.Count > 0 ? 600 : 100);
    }

    private static FidelityState CurrentFidelity(CampaignState state, IReadOnlyList<EntityId> entityIds) =>
        entityIds.Select(id => state.Entities[id].Fidelity).Max();

    private static HashSet<EntityId> ParseEntities(CampaignState state, IReadOnlyList<string> values, string kind)
    {
        var result = new HashSet<EntityId>();
        foreach (var value in values ?? [])
        {
            if (!Guid.TryParseExact(value, "N", out var parsed) || !state.Entities.ContainsKey(new EntityId(parsed)))
            {
                throw new CampaignStoreConflict("scheduler-entity-missing", $"A {kind} scheduler entity does not exist.");
            }
            result.Add(new EntityId(parsed));
        }
        return result;
    }

    internal sealed record SchedulerEvaluation(
        CampaignSchedulerRequest Request,
        int TargetTicksPerWallSecond,
        string LoadAction,
        IReadOnlyList<SchedulerIslandPlan> Islands,
        IReadOnlyList<CampaignFidelityTransitionView> Transitions);

    internal sealed record SchedulerIslandPlan(
        string IslandId,
        IReadOnlyList<EntityId> EntityIds,
        IReadOnlyList<GroupId> GroupIds,
        FidelityState CurrentFidelity,
        FidelityState DesiredFidelity,
        bool Engaged,
        int CouplingScoreMilliunits,
        string Reason);

    private sealed record Island(
        string IslandId,
        IReadOnlyList<EntityId> EntityIds,
        IReadOnlyList<GroupId> GroupIds,
        bool Engaged,
        int CouplingScoreMilliunits);
}
