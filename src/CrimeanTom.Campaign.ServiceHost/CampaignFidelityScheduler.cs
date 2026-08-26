using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Engine;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.Transport.V1;

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
    int BacklogDepth,
    int TacticalReserveCapacity = 0,
    string? PreferredTacticalWorkerClientInstanceId = null);

public sealed record CampaignSchedulerView(
    string Health,
    string Eligibility,
    int TacticalEntityCapacity,
    int TacticalReserveCapacity,
    int OccupiedTacticalEntityCount,
    int ActiveTacticalEntityCount,
    int RetainedSuspendedTacticalEntityCount,
    int RecoveryRequiredTacticalEntityCount,
    int AvailableTacticalEntityCapacity,
    int BacklogDepth,
    int StableSampleCount,
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
    bool UnsafeToSplit,
    IReadOnlyList<string> CouplingReasons,
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
        var islands = BuildIslands(state, []).Select(island => new SchedulerIslandPlan(
            island.IslandId,
            island.EntityIds,
            island.GroupIds,
            CurrentFidelity(state, island.EntityIds),
            CurrentFidelity(state, island.EntityIds),
            island.Engaged,
            island.CouplingScoreMilliunits,
            island.UnsafeToSplit,
            island.CouplingReasons,
            "current-canonical-state")).ToArray();
        return new SchedulerEvaluation(
            new CampaignSchedulerRequest([], [], SchedulerHealthState.Offline,
                SimulationSourceEligibility.Disabled, 0, 0, 0),
            0,
            0,
            "scheduler-not-evaluated",
            islands,
            []);
    }

    public static SchedulerEvaluation Evaluate(
        CampaignState state,
        CampaignSchedulerRequest request,
        IReadOnlyList<CampaignSessionSnapshot> workers,
        int stableSampleCount)
    {
        if (!Enum.IsDefined(request.Health) || !Enum.IsDefined(request.Eligibility)
            || request.TacticalEntityCapacity is < 0 or > 10_000
            || request.TacticalReserveCapacity is < 0 or > 10_000
            || request.TacticalReserveCapacity > request.TacticalEntityCapacity
            || request.BacklogDepth is < 0 or > 1_000_000
            || stableSampleCount is < 1 or > 1_000_000)
        {
            throw new CampaignStoreConflict("invalid-scheduler-input", "Scheduler health, eligibility, capacity, or backlog is invalid.");
        }

        var observed = ParseEntities(state, request.ObservedEntityIds, "observed");
        var controlled = ParseEntities(state, request.ControlledEntityIds, "controlled");
        var interested = observed.Concat(controlled).ToHashSet();
        var campaignWorkers = (workers ?? [])
            .Where(worker => string.Equals(
                worker.CampaignId,
                state.Metadata.CampaignId.ToString(),
                StringComparison.Ordinal))
            .OrderBy(worker => worker.ClientInstanceId, StringComparer.Ordinal)
            .ToArray();
        var tacticalWorkers = campaignWorkers
            .Where(worker => !CampaignWorkerKind.IsOperational(worker.WorkerKind))
            .ToArray();
        var workerConnected = tacticalWorkers.Length > 0;
        var islands = BuildIslands(state, campaignWorkers)
            .SelectMany(island => SplitSafePlayerInterest(island, interested))
            .ToArray();
        var campaignOccupiedEntities = state.Assignments.Values
            .Where(item => item.Lifecycle is AssignmentLifecycle.Preparing
                or AssignmentLifecycle.Prepared or AssignmentLifecycle.Active
                or AssignmentLifecycle.Quiescing
                && !CampaignAssignmentKinds.IsOperationalSim(item))
            .Select(item => item.EntityId)
            .Distinct()
            .ToHashSet();
        var campaignActiveEntities = state.Assignments.Values
            .Where(item => item.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing
                && !CampaignAssignmentKinds.IsOperationalSim(item))
            .Select(item => item.EntityId)
            .Distinct()
            .ToHashSet();
        var occupiedTacticalEntityCount = Math.Max(
            campaignOccupiedEntities.Count,
            tacticalWorkers.Sum(worker => worker.OwnedMaterializedEntityCount));
        var activeTacticalEntityCount = Math.Max(
            campaignActiveEntities.Count,
            tacticalWorkers.Sum(worker => worker.OwnedActiveEntityCount));
        var retainedSuspendedTacticalEntityCount = tacticalWorkers.Sum(
            worker => worker.OwnedRetainedSuspendedEntityCount);
        var recoveryRequiredTacticalEntityCount = tacticalWorkers.Sum(
            worker => worker.OwnedRecoveryRequiredEntityCount);
        var safeTacticalCapacity = Math.Max(
            0,
            request.TacticalEntityCapacity - request.TacticalReserveCapacity);
        var remainingCapacity = Math.Max(0, safeTacticalCapacity - occupiedTacticalEntityCount);
        var excessOccupiedCapacity = Math.Max(0, occupiedTacticalEntityCount - safeTacticalCapacity);
        var capacityVictims = new HashSet<string>(StringComparer.Ordinal);
        var plannedReleaseCapacity = 0;
        if (excessOccupiedCapacity > 0)
        {
            foreach (var candidate in islands
                .Where(item => !item.UnsafeToSplit && !item.Engaged
                    && !item.EntityIds.Any(id =>
                        CampaignResidentEntityPolicy.RequiresContinuousTacticalPresence(state.Entities[id]))
                    && state.Assignments.Values.Any(assignment =>
                        item.EntityIds.Contains(assignment.EntityId)
                        && assignment.Lifecycle == AssignmentLifecycle.Active
                        && !CampaignAssignmentKinds.IsOperationalSim(assignment)))
                .OrderBy(item => item.EntityIds.Any(controlled.Contains) ? 2
                    : item.EntityIds.Any(observed.Contains) ? 1 : 0)
                .ThenBy(item => item.IslandId, StringComparer.Ordinal))
            {
                capacityVictims.Add(candidate.IslandId);
                plannedReleaseCapacity += candidate.EntityIds.Count(campaignActiveEntities.Contains);
                if (plannedReleaseCapacity >= excessOccupiedCapacity)
                {
                    break;
                }
            }
        }
        var plans = new List<SchedulerIslandPlan>();
        var transitions = new List<CampaignFidelityTransitionView>();
        foreach (var island in islands
            .OrderByDescending(item => item.EntityIds.Any(id =>
                CampaignResidentEntityPolicy.RequiresContinuousTacticalPresence(state.Entities[id])))
            .ThenByDescending(item => item.EntityIds.Any(interested.Contains))
            .ThenBy(item => item.IslandId, StringComparer.Ordinal))
        {
            var current = CurrentFidelity(state, island.EntityIds);
            var hasInterest = island.EntityIds.Any(interested.Contains);
            var hasResidentEntity = island.EntityIds.Any(id =>
                CampaignResidentEntityPolicy.RequiresContinuousTacticalPresence(state.Entities[id]));
            var islandAssignments = state.Assignments.Values
                .Where(item => island.EntityIds.Contains(item.EntityId))
                .ToArray();
            var hasPreparingAssignment = islandAssignments.Any(item =>
                !CampaignAssignmentKinds.IsOperationalSim(item)
                && item.Lifecycle is AssignmentLifecycle.Preparing or AssignmentLifecycle.Prepared);
            var hasActiveAssignment = islandAssignments.Any(item =>
                !CampaignAssignmentKinds.IsOperationalSim(item)
                && item.Lifecycle == AssignmentLifecycle.Active);
            var hasQuiescingAssignment = islandAssignments.Any(item =>
                !CampaignAssignmentKinds.IsOperationalSim(item)
                && item.Lifecycle is AssignmentLifecycle.Quiescing or AssignmentLifecycle.Quiesced);
            var hasLiveOperationalAssignment = islandAssignments.Any(item =>
                CampaignAssignmentKinds.IsOperationalSim(item)
                && item.Lifecycle is AssignmentLifecycle.Preparing or AssignmentLifecycle.Prepared
                    or AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing);
            var tacticalCapable = island.EntityIds.All(id => state.Entities[id].MaximumFidelity == FidelityState.Tactical);
            var workerHasTaskforce = campaignWorkers.Any(worker =>
                !CampaignWorkerKind.IsOperational(worker.WorkerKind)
                && !string.IsNullOrWhiteSpace(worker.PlayerTaskforceId));
            var compatibleWorkers = campaignWorkers.Where(worker =>
                !CampaignWorkerKind.IsOperational(worker.WorkerKind)
                && CampaignWorkerHealth.CanReceiveNewAssignments(worker)
                && !string.IsNullOrWhiteSpace(worker.PlayerTaskforceId)
                && island.EntityIds.All(id => CampaignMaterializationProfileResolver.Supports(
                    worker,
                    state.Entities[id]))).ToArray();
            var workerCanMaterializeIsland = compatibleWorkers.Length > 0;
            var workerCanAdmitIsland = compatibleWorkers.Any(worker =>
                CampaignWorkerAdmissionPolicy.Calculate(state, worker, safeTacticalCapacity)
                    .CanAdmitPromotion(island.EntityIds.Count));
            var owningWorkers = islandAssignments
                .Where(item => item.Lifecycle != AssignmentLifecycle.Released
                    && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary)
                .Select(item => item.WorkerClientInstanceId)
                .Where(owner => !string.IsNullOrWhiteSpace(owner))
                .Distinct(StringComparer.Ordinal)
                .Select(owner => campaignWorkers.FirstOrDefault(worker => string.Equals(
                    worker.ClientInstanceId,
                    owner,
                    StringComparison.Ordinal)))
                .Where(worker => worker is not null)
                .Cast<CampaignSessionSnapshot>()
                .ToArray();
            var workerRequiresEvacuation = request.Health is SchedulerHealthState.Draining
                    or SchedulerHealthState.Quarantined or SchedulerHealthState.Offline
                || request.Eligibility != SimulationSourceEligibility.Eligible
                || owningWorkers.Any(CampaignWorkerHealth.MustEvacuate);
            var loadRequiresFallback = request.Health is SchedulerHealthState.Overloaded or SchedulerHealthState.Draining
                or SchedulerHealthState.Quarantined or SchedulerHealthState.Offline
                || request.Eligibility != SimulationSourceEligibility.Eligible
                || request.BacklogDepth >= 192
                || owningWorkers.Any(CampaignWorkerHealth.MustLoadShed);
            FidelityState desired;
            string reason;
            if (hasQuiescingAssignment)
            {
                desired = FidelityState.Tactical;
                reason = "authority-quiescing";
            }
            else if (hasPreparingAssignment)
            {
                desired = FidelityState.Tactical;
                reason = "authority-preparing";
            }
            else if (hasActiveAssignment && workerConnected && workerRequiresEvacuation)
            {
                desired = FidelityState.Operational;
                reason = "active-island-worker-evacuation";
            }
            else if (hasActiveAssignment && island.UnsafeToSplit)
            {
                desired = FidelityState.Tactical;
                reason = "active-coupling-fence";
            }
            else if (hasActiveAssignment && island.Engaged)
            {
                desired = FidelityState.Tactical;
                reason = "active-interaction-fence";
            }
            else if (hasActiveAssignment && !hasResidentEntity && capacityVictims.Contains(island.IslandId))
            {
                desired = FidelityState.Operational;
                reason = "tactical-reserve-load-shed";
            }
            else if (hasActiveAssignment && !hasResidentEntity && workerConnected && loadRequiresFallback)
            {
                desired = FidelityState.Operational;
                reason = "active-island-load-shed";
            }
            else if (hasActiveAssignment && !hasResidentEntity && workerConnected && !hasInterest && !island.Engaged
                && stableSampleCount >= 2)
            {
                desired = FidelityState.Operational;
                reason = "active-island-outside-player-interest";
            }
            else if (hasActiveAssignment && !hasResidentEntity && workerConnected && !hasInterest && !island.Engaged)
            {
                desired = FidelityState.Tactical;
                reason = "scheduler-hysteresis-hold";
            }
            else if (hasActiveAssignment)
            {
                desired = FidelityState.Tactical;
                reason = workerConnected ? "active-authority-fence" : "active-authority-worker-offline";
            }
            else if (hasLiveOperationalAssignment && hasInterest && tacticalCapable
                && workerCanMaterializeIsland && workerCanAdmitIsland)
            {
                desired = FidelityState.Tactical;
                reason = "player-interest-preempts-operational-worker";
            }
            else if (current == FidelityState.Tactical && island.Engaged)
            {
                desired = FidelityState.Tactical;
                reason = "active-interaction-fence";
            }
            else if (hasResidentEntity && !tacticalCapable)
            {
                desired = FidelityState.Operational;
                reason = "resident-system-domain-ceiling";
            }
            else if (hasResidentEntity && !workerConnected)
            {
                desired = FidelityState.Operational;
                reason = "resident-system-worker-offline";
            }
            else if (hasResidentEntity && !workerHasTaskforce)
            {
                desired = FidelityState.Operational;
                reason = "resident-system-taskforce-unavailable";
            }
            else if (hasResidentEntity && !workerCanMaterializeIsland)
            {
                desired = FidelityState.Operational;
                reason = "resident-system-materialization-ceiling";
            }
            else if (hasResidentEntity && !workerCanAdmitIsland)
            {
                desired = FidelityState.Operational;
                reason = "resident-system-capacity-exhausted";
            }
            else if (hasResidentEntity && island.EntityIds.Count > remainingCapacity)
            {
                desired = FidelityState.Operational;
                reason = "resident-system-capacity-exhausted";
            }
            else if (hasResidentEntity)
            {
                desired = FidelityState.Tactical;
                remainingCapacity -= island.EntityIds.Count;
                reason = "resident-strategic-system";
            }
            else if (loadRequiresFallback)
            {
                desired = current == FidelityState.Strategic
                    ? FidelityState.Strategic
                    : FidelityState.Operational;
                reason = request.Eligibility != SimulationSourceEligibility.Eligible
                    ? $"source-{request.Eligibility.ToString().ToLowerInvariant()}"
                    : request.BacklogDepth >= 192 ? "transport-backlog-load-shed"
                    : $"client-{request.Health.ToString().ToLowerInvariant()}";
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
            else if (hasInterest && !workerCanAdmitIsland)
            {
                desired = FidelityState.Operational;
                reason = "tactical-destination-capacity-exhausted";
            }
            else if (hasInterest && island.EntityIds.Count <= remainingCapacity)
            {
                desired = FidelityState.Tactical;
                remainingCapacity -= island.EntityIds.Count;
                reason = controlled.Overlaps(island.EntityIds) ? "player-control" : "player-observation";
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
                current, desired, island.Engaged, island.CouplingScoreMilliunits,
                island.UnsafeToSplit, island.CouplingReasons, reason));
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

        var unresolvedCapacityPressure = excessOccupiedCapacity > plannedReleaseCapacity;
        var tempo = request.Health switch
        {
            SchedulerHealthState.Healthy when unresolvedCapacityPressure => 20,
            SchedulerHealthState.Healthy when request.BacklogDepth < 96 => 30,
            SchedulerHealthState.Pressured or SchedulerHealthState.Healthy => 20,
            _ => 10,
        };
        var maximumTicks = checked(
            CampaignCalibration.HealthyTicksPerWallSecond * CampaignCalibration.MaximumTacticalCompressionTimes);
        tempo = Math.Clamp(tempo, 0, maximumTicks);
        var capacityPressure = excessOccupiedCapacity > 0;
        var action = request.Health == SchedulerHealthState.Healthy && request.BacklogDepth < 96
            && !capacityPressure
            ? "normal"
            : transitions.Any(item => item.ToFidelity != FidelityState.Tactical.ToString())
                ? "demote-whole-islands-before-tempo"
                : unresolvedCapacityPressure
                    ? "global-tempo-reduced-reserve-fence"
                    : "global-tempo-reduced";
        return new SchedulerEvaluation(
            request,
            stableSampleCount,
            tempo,
            action,
            plans,
            transitions,
            occupiedTacticalEntityCount,
            activeTacticalEntityCount,
            retainedSuspendedTacticalEntityCount,
            recoveryRequiredTacticalEntityCount,
            Math.Max(0, safeTacticalCapacity - occupiedTacticalEntityCount));
    }

    public static CampaignSchedulerView ToView(CampaignState state, SchedulerEvaluation evaluation) => new(
        evaluation.Request.Health.ToString(),
        evaluation.Request.Eligibility.ToString(),
        evaluation.Request.TacticalEntityCapacity,
        evaluation.Request.TacticalReserveCapacity,
        evaluation.OccupiedTacticalEntityCount,
        evaluation.ActiveTacticalEntityCount,
        evaluation.RetainedSuspendedTacticalEntityCount,
        evaluation.RecoveryRequiredTacticalEntityCount,
        evaluation.AvailableTacticalEntityCapacity,
        evaluation.Request.BacklogDepth,
        evaluation.StableSampleCount,
        FixedStepMicroseconds,
        evaluation.TargetTicksPerWallSecond,
        state.SchedulerPolicy?.EffectiveAt.Quanta ?? state.Time.Quanta,
        evaluation.LoadAction,
        evaluation.Islands.Select(item => new CampaignInteractionIslandView(
            item.IslandId,
            item.EntityIds.Select(id => id.ToString()).ToArray(),
            item.GroupIds.Select(id => id.ToString()).ToArray(),
            item.CurrentFidelity.ToString(),
            item.DesiredFidelity.ToString(),
            item.Engaged,
            item.CouplingScoreMilliunits,
            item.UnsafeToSplit,
            item.CouplingReasons,
            item.Reason)).ToArray(),
        evaluation.Transitions);

    private static IReadOnlyList<Island> BuildIslands(
        CampaignState state,
        IReadOnlyList<CampaignSessionSnapshot> workers)
    {
        // Terminal entities remain in canonical history, but they no longer consume
        // simulation fidelity or participate in interaction islands. Keeping them in
        // scheduler islands lets an unrelated policy update try to demote a destroyed
        // Operational entity to Strategic, which the campaign kernel correctly rejects.
        var entityNodes = state.Entities.Values
            .Where(entity => entity.Lifecycle is not EntityLifecycle.Destroyed
                and not EntityLifecycle.Retired)
            .Select(entity => entity.EntityId)
            .OrderBy(item => item)
            .ToArray();
        if (entityNodes.Length == 0)
        {
            return [];
        }
        var entityIndexes = entityNodes
            .Select((entityId, index) => (entityId, index))
            .ToDictionary(item => item.entityId, item => item.index);
        var groupNodes = state.Groups.Values.OrderBy(item => item.GroupId).ToArray();
        var parents = Enumerable.Range(0, entityNodes.Length).ToArray();
        int Find(int value) => parents[value] == value ? value : parents[value] = Find(parents[value]);
        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left == right) return;
            if (left < right) parents[right] = left; else parents[left] = right;
        }

        foreach (var group in groupNodes)
        {
            var liveMembers = group.Members.Where(entityIndexes.ContainsKey).ToArray();
            if (liveMembers.Length == 0)
            {
                continue;
            }
            var first = entityIndexes[liveMembers[0]];
            foreach (var member in liveMembers)
            {
                Union(first, entityIndexes[member]);
            }
        }

        var recentCombatOutcomes = state.RecentOperationalOutcomes
            .Where(item => state.Time.Quanta - item.OccurredAt.Quanta <= InteractionFenceQuanta
                && item.Kind is OperationalOutcomeKind.WeaponExpenditure or OperationalOutcomeKind.Damage
                    or OperationalOutcomeKind.Destruction)
            .ToArray();
        var recentCombatEntities = recentCombatOutcomes
            .SelectMany(item => item.TargetEntityId is { } targetId
                ? new[] { item.ActorEntityId, targetId }
                : [item.ActorEntityId])
            .ToHashSet();
        foreach (var outcome in recentCombatOutcomes)
        {
            if (outcome.TargetEntityId is { } targetId
                && entityIndexes.TryGetValue(outcome.ActorEntityId, out var actorIndex)
                && entityIndexes.TryGetValue(targetId, out var targetIndex))
            {
                Union(actorIndex, targetIndex);
            }
        }

        var relationships = new List<ResolvedCouplingRelationship>();
        foreach (var worker in workers.Where(worker => string.Equals(
            worker.CampaignId,
            state.Metadata.CampaignId.ToString(),
            StringComparison.Ordinal)))
        {
            foreach (var relationship in worker.CouplingRelationships)
            {
                if (!Guid.TryParseExact(relationship.SourceEntityId, "N", out var sourceValue)
                    || !Guid.TryParseExact(relationship.TargetEntityId, "N", out var targetValue))
                {
                    continue;
                }
                var source = new EntityId(sourceValue);
                var target = new EntityId(targetValue);
                if (!entityIndexes.TryGetValue(source, out var sourceIndex)
                    || !entityIndexes.TryGetValue(target, out var targetIndex))
                {
                    continue;
                }
                relationships.Add(new ResolvedCouplingRelationship(source, target, relationship));
                if (relationship.UnsafeToSplit)
                {
                    Union(sourceIndex, targetIndex);
                }
            }
        }

        return entityNodes.Select((entityId, index) => (entityId, root: Find(index)))
            .GroupBy(item => item.root)
            .Select(items =>
            {
                var entityIds = items.Select(item => item.entityId).OrderBy(item => item).ToArray();
                var members = entityIds.ToHashSet();
                var groupIds = groupNodes
                    .Where(group => group.Members.Any(members.Contains))
                    .Select(group => group.GroupId)
                    .OrderBy(item => item)
                    .ToArray();
                var coupling = relationships
                    .Where(item => members.Contains(item.SourceEntityId)
                        && members.Contains(item.TargetEntityId))
                    .ToArray();
                return CreateIsland(state, entityIds, groupIds, recentCombatEntities, coupling);
            })
            .OrderBy(item => item.IslandId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<Island> SplitSafePlayerInterest(
        Island island,
        IReadOnlySet<EntityId> interested)
    {
        if (interested.Count == 0 || island.UnsafeToSplit || island.Engaged)
        {
            yield return island;
            yield break;
        }
        var focused = island.EntityIds.Where(interested.Contains).OrderBy(item => item).ToArray();
        if (focused.Length == 0 || focused.Length == island.EntityIds.Count)
        {
            yield return island;
            yield break;
        }

        // Administrative group membership contributes a soft coupling score but is
        // not an authority fence. A latency-sensitive player selection may therefore
        // materialize only the selected members. Explicit runtime relationships and
        // recent combat set UnsafeToSplit/Engaged and remain atomic above.
        yield return island with
        {
            IslandId = $"{island.IslandId}:player-interest",
            EntityIds = focused,
            GroupIds = [],
            CouplingScoreMilliunits = 100,
        };
        yield return island with
        {
            IslandId = $"{island.IslandId}:remainder",
            EntityIds = island.EntityIds.Where(id => !interested.Contains(id)).ToArray(),
        };
    }

    private static Island CreateIsland(
        CampaignState state,
        IReadOnlyList<EntityId> entityIds,
        IReadOnlyList<GroupId> groupIds,
        IReadOnlySet<EntityId> combatEntities,
        IReadOnlyList<ResolvedCouplingRelationship> relationships)
    {
        var unsafeToSplit = relationships.Any(item => item.Relationship.UnsafeToSplit);
        var engaged = unsafeToSplit
            || entityIds.Any(combatEntities.Contains);
        var grouped = groupIds.SelectMany(id => state.Groups[id].Members).ToHashSet();
        var material = string.Join(':', groupIds.Select(id => id.Value.ToString("N"))
            .Concat(entityIds.Where(id => !grouped.Contains(id))
                .Select(id => id.Value.ToString("N"))));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{state.Metadata.CampaignId.Value:N}:island:{material}"));
        var couplingReasons = relationships
            .Select(item => string.IsNullOrWhiteSpace(item.Relationship.GuidanceType)
                ? item.Relationship.Kind
                : $"{item.Relationship.Kind}:{item.Relationship.GuidanceType}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return new Island(
            Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant(),
            entityIds,
            groupIds,
            engaged,
            engaged ? 1_000 : groupIds.Count > 0 ? 600 : 100,
            unsafeToSplit,
            couplingReasons);
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
        int StableSampleCount,
        int TargetTicksPerWallSecond,
        string LoadAction,
        IReadOnlyList<SchedulerIslandPlan> Islands,
        IReadOnlyList<CampaignFidelityTransitionView> Transitions,
        int OccupiedTacticalEntityCount = 0,
        int ActiveTacticalEntityCount = 0,
        int RetainedSuspendedTacticalEntityCount = 0,
        int RecoveryRequiredTacticalEntityCount = 0,
        int AvailableTacticalEntityCapacity = 0);

    internal sealed record SchedulerIslandPlan(
        string IslandId,
        IReadOnlyList<EntityId> EntityIds,
        IReadOnlyList<GroupId> GroupIds,
        FidelityState CurrentFidelity,
        FidelityState DesiredFidelity,
        bool Engaged,
        int CouplingScoreMilliunits,
        bool UnsafeToSplit,
        IReadOnlyList<string> CouplingReasons,
        string Reason);

    private sealed record Island(
        string IslandId,
        IReadOnlyList<EntityId> EntityIds,
        IReadOnlyList<GroupId> GroupIds,
        bool Engaged,
        int CouplingScoreMilliunits,
        bool UnsafeToSplit,
        IReadOnlyList<string> CouplingReasons);

    private sealed record ResolvedCouplingRelationship(
        EntityId SourceEntityId,
        EntityId TargetEntityId,
        CampaignCouplingRelationship Relationship);
}
