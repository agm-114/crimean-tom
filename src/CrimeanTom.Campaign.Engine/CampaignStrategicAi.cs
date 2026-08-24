using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

/// <summary>
/// Deterministic whole-war direction. The planner only receives a coalition projection:
/// friendly readiness, public base ownership, geography, and that coalition's intelligence.
/// It cannot inspect hostile entity truth. Decisions persist as objectives and commitments;
/// domain controllers translate those commitments into ordinary canonical orders.
/// </summary>
internal static class CampaignStrategicAi
{
    private const long DirectorCadenceQuanta = 40;
    private const long CommitmentQuanta = 80;
    private const long RecoveryLatchQuanta = 40;
    private const long OrderQuanta = 30;

    public static CampaignState ResolveIntent(CampaignState state, CampaignTime dueAt)
    {
        var coalitions = state.Groups.Values.Select(item => item.CoalitionId)
            .Concat(state.Bases.Values.Select(item => item.CoalitionId))
            .Concat(state.Entities.Values.Select(item => item.CoalitionId))
            .Distinct()
            .OrderBy(item => item)
            .ToArray();

        var result = state;
        for (var index = 0; index < coalitions.Length; index++)
        {
            var coalitionId = coalitions[index];
            var view = CoalitionStrategicView.Create(result, coalitionId);
            result = RefreshRecoveryLatches(result, view, dueAt);

            if (!result.CoalitionStrategies.TryGetValue(coalitionId, out var strategy)
                || strategy.NextDirectorAt.Quanta <= dueAt.Quanta)
            {
                result = DirectCoalition(result, view, dueAt, index, strategy);
            }
        }

        return IssueOperationalOrders(result, dueAt);
    }

    private static CampaignState DirectCoalition(
        CampaignState state,
        CoalitionStrategicView view,
        CampaignTime dueAt,
        int coalitionIndex,
        CoalitionStrategyState? priorStrategy)
    {
        var epoch = checked((priorStrategy?.Epoch ?? 0) + 1);
        var planned = BuildObjectives(view, state.Metadata.Seed, dueAt, epoch);
        var objectives = state.StrategicObjectives;
        foreach (var objective in planned)
        {
            if (objectives.TryGetValue(objective.ObjectiveId, out var prior))
            {
                objectives = objectives.SetItem(objective.ObjectiveId, objective with
                {
                    CreatedAt = prior.CreatedAt,
                    Revision = prior.Revision.Next(),
                });
            }
            else
            {
                objectives = objectives.Add(objective.ObjectiveId, objective);
            }
        }

        var currentObjectiveIds = planned.Select(item => item.ObjectiveId).ToHashSet();
        var assignedCounts = new Dictionary<StrategicObjectiveId, int>();
        var assignedResponseCounts = new Dictionary<
            (StrategicObjectiveId ObjectiveId, ContactClassification Classification), int>();
        var commitments = state.GroupCommitments;
        foreach (var group in view.Groups.Values)
        {
            commitments.TryGetValue(group.GroupId, out var prior);
            StrategicObjectiveId? selected = null;
            var retainedCommitment = false;
            if (prior?.ObjectiveId is { } retainedId
                && currentObjectiveIds.Contains(retainedId)
                && prior.ExpiresAt.Quanta > dueAt.Quanta
                && objectives.TryGetValue(retainedId, out var retained)
                && IsCompatible(group, retained))
            {
                selected = retainedId;
                retainedCommitment = true;
            }
            else
            {
                selected = SelectObjective(
                    view,
                    group,
                    planned,
                    assignedCounts,
                    assignedResponseCounts,
                    state.Metadata.Seed,
                    dueAt);
            }

            if (selected is { } objectiveId)
            {
                assignedCounts[objectiveId] = assignedCounts.GetValueOrDefault(objectiveId) + 1;
                var selectedObjective = planned.First(item => item.ObjectiveId == objectiveId);
                foreach (var classification in ResponsiveClasses(view, group, selectedObjective))
                {
                    var key = (objectiveId, classification);
                    assignedResponseCounts[key] = assignedResponseCounts.GetValueOrDefault(key) + 1;
                }
            }

            var updated = new GroupCommitmentState(
                group.GroupId,
                selected,
                retainedCommitment
                    ? prior!.ExpiresAt
                    : new CampaignTime(checked(dueAt.Quanta + CommitmentQuanta)),
                prior?.WithdrawLatched ?? group.RequiresRecovery,
                prior?.LatchReleaseAt ?? new CampaignTime(
                    group.RequiresRecovery ? checked(dueAt.Quanta + RecoveryLatchQuanta) : dueAt.Quanta),
                prior is null ? new EntityRevision(1) : prior.Revision.Next());
            commitments = commitments.SetItem(group.GroupId, updated);
        }

        var nextAt = new CampaignTime(checked(
            dueAt.Quanta + DirectorCadenceQuanta + coalitionIndex * 10L));
        var strategy = new CoalitionStrategyState(
            view.CoalitionId,
            nextAt,
            epoch,
            priorStrategy is null ? new EntityRevision(1) : priorStrategy.Revision.Next());
        return state with
        {
            StrategicObjectives = objectives,
            GroupCommitments = commitments,
            CoalitionStrategies = state.CoalitionStrategies.SetItem(view.CoalitionId, strategy),
        };
    }

    private static CampaignState RefreshRecoveryLatches(
        CampaignState state,
        CoalitionStrategicView view,
        CampaignTime dueAt)
    {
        var commitments = state.GroupCommitments;
        foreach (var group in view.Groups.Values)
        {
            commitments.TryGetValue(group.GroupId, out var prior);
            if (group.RequiresRecovery)
            {
                var releaseAt = new CampaignTime(checked(dueAt.Quanta + RecoveryLatchQuanta));
                if (prior is null)
                {
                    commitments = commitments.SetItem(group.GroupId, new GroupCommitmentState(
                        group.GroupId, null,
                        new CampaignTime(checked(dueAt.Quanta + CommitmentQuanta)),
                        WithdrawLatched: true,
                        releaseAt,
                        new EntityRevision(1)));
                }
                else if (!prior.WithdrawLatched || prior.LatchReleaseAt.Quanta < releaseAt.Quanta)
                {
                    commitments = commitments.SetItem(group.GroupId, prior with
                    {
                        WithdrawLatched = true,
                        LatchReleaseAt = releaseAt,
                        ExpiresAt = Later(prior.ExpiresAt, new CampaignTime(checked(dueAt.Quanta + CommitmentQuanta))),
                        Revision = prior.Revision.Next(),
                    });
                }
            }
            else if (prior is { WithdrawLatched: true }
                && group.RecoveryReady
                && prior.LatchReleaseAt.Quanta <= dueAt.Quanta)
            {
                commitments = commitments.SetItem(group.GroupId, prior with
                {
                    WithdrawLatched = false,
                    ExpiresAt = Later(prior.ExpiresAt, new CampaignTime(checked(dueAt.Quanta + DirectorCadenceQuanta))),
                    Revision = prior.Revision.Next(),
                });
            }
        }
        return state with { GroupCommitments = commitments };
    }

    private static ImmutableArray<StrategicObjectiveState> BuildObjectives(
        CoalitionStrategicView view,
        long seed,
        CampaignTime dueAt,
        long epoch)
    {
        var candidates = new Dictionary<(RegionId, StrategicObjectiveKind), int>();
        void Add(RegionId regionId, StrategicObjectiveKind kind, int priority) =>
            candidates[(regionId, kind)] = Math.Max(priority, candidates.GetValueOrDefault((regionId, kind)));

        foreach (var contact in view.Intelligence)
        {
            if (contact.Confidence < IntelligenceConfidence.Probable)
            {
                continue;
            }
            var agePenalty = (int)Math.Min(250, Math.Max(0, dueAt.Quanta - contact.ObservedAt.Quanta));
            var uncertaintyPenalty = Math.Min(200, contact.UncertaintyRadiusMeters / 10_000);
            Add(contact.EstimatedRegionId, StrategicObjectiveKind.Interdict,
                Math.Max(500, 1_000 - agePenalty - uncertaintyPenalty));
        }

        foreach (var campaignBase in view.Bases)
        {
            if (campaignBase.IsFriendly)
            {
                var nearbyThreat = view.Intelligence.Any(contact =>
                    contact.Confidence >= IntelligenceConfidence.Probable
                    && (contact.EstimatedRegionId == campaignBase.RegionId
                        || view.Neighbors[campaignBase.RegionId].Contains(contact.EstimatedRegionId)));
                Add(campaignBase.RegionId, StrategicObjectiveKind.Defend, nearbyThreat ? 950 : 700);
                if (campaignBase.SupplyMilliUnits is { } supply && supply < 1_500_000)
                {
                    Add(campaignBase.RegionId, StrategicObjectiveKind.Sustain,
                        Math.Min(950, 650 + (int)((1_500_000 - supply) / 5_000)));
                }
            }
            else
            {
                Add(campaignBase.RegionId, StrategicObjectiveKind.ProjectForce, 500);
            }
        }

        foreach (var regionId in view.Groups.Values.Select(item => item.RegionId).Distinct())
        {
            Add(regionId, StrategicObjectiveKind.Defend, 350);
        }

        return candidates.OrderBy(item => item.Key.Item1).ThenBy(item => item.Key.Item2)
            .Select(item => new StrategicObjectiveState(
                ObjectiveId(seed, view.CoalitionId, item.Key.Item1, item.Key.Item2),
                view.CoalitionId,
                item.Key.Item1,
                item.Key.Item2,
                item.Value,
                dueAt,
                epoch,
                new EntityRevision(1)))
            .ToImmutableArray();
    }

    private static StrategicObjectiveId? SelectObjective(
        CoalitionStrategicView view,
        StrategicGroupView group,
        IReadOnlyList<StrategicObjectiveState> objectives,
        IReadOnlyDictionary<StrategicObjectiveId, int> assignedCounts,
        IReadOnlyDictionary<(StrategicObjectiveId, ContactClassification), int> assignedResponseCounts,
        long seed,
        CampaignTime dueAt)
    {
        StrategicObjectiveState? best = null;
        long bestScore = long.MinValue;
        foreach (var objective in objectives)
        {
            if (!IsCompatible(group, objective))
            {
                continue;
            }
            var distance = Distance(view.Neighbors, group.RegionId, objective.RegionId);
            if (distance == int.MaxValue)
            {
                continue;
            }
            var saturation = assignedCounts.GetValueOrDefault(objective.ObjectiveId);
            var score = objective.PriorityMilliUnits * 1_000L
                - distance * 150_000L
                - saturation * 100_000L
                + Affinity(group, objective.Kind)
                + ThreatResponseAffinity(view, group, objective, assignedResponseCounts)
                + StableJitter(seed, group.GroupId.Value, dueAt, objective.ObjectiveId.Value);
            if (score > bestScore || score == bestScore && objective.ObjectiveId.CompareTo(best!.ObjectiveId) < 0)
            {
                best = objective;
                bestScore = score;
            }
        }
        return best?.ObjectiveId;
    }

    private static CampaignState IssueOperationalOrders(CampaignState state, CampaignTime dueAt)
    {
        var groups = state.Groups;
        var orders = state.Orders;
        foreach (var group in state.Groups.Values)
        {
            if (group.ActiveOrderId is not null
                || !state.GroupCommitments.TryGetValue(group.GroupId, out var commitment)
                || commitment.ExpiresAt.Quanta <= dueAt.Quanta)
            {
                continue;
            }
            var members = group.Members.Select(id => state.Entities[id])
                .Where(item => IsOperational(state, item))
                .ToArray();
            // A canonical group containing a fixed installation is one defensive
            // complex. Moving only its fuel farm or support vehicles would split
            // authority from the runway/radar that makes the group meaningful,
            // while including the installation creates an order that can never
            // arrive because installations have zero movement. Static complexes
            // defend and engage in place without a movement order.
            if (members.Length == 0
                || members.Any(item => item.SimulationDomain == SimulationDomain.Installation))
            {
                continue;
            }

            var first = members[0];
            RegionId destination;
            OrderKind kind;
            if (commitment.WithdrawLatched)
            {
                var home = members.Select(item => item.HomeBaseId)
                    .Where(item => item is not null)
                    .Select(item => state.Bases[item!.Value].RegionId)
                    .Distinct()
                    .OrderBy(item => item)
                    .FirstOrDefault();
                if (home == default || home == first.RegionId)
                {
                    continue;
                }
                destination = home;
                kind = OrderKind.Withdraw;
            }
            else if (commitment.ObjectiveId is { } objectiveId
                && state.StrategicObjectives.TryGetValue(objectiveId, out var objective))
            {
                destination = objective.RegionId;
                kind = OperationalOrderKind(first, objective.Kind);
            }
            else
            {
                continue;
            }

            var orderId = new OrderId(KeyedGuid(
                state.Metadata.Seed, group.GroupId.Value, dueAt,
                $"strategic-{kind}-{destination.Value:N}"));
            var order = new OrderState(
                orderId,
                group.GroupId,
                kind,
                destination,
                dueAt,
                new CampaignTime(checked(dueAt.Quanta + OrderQuanta)),
                CompletedAt: null,
                new EntityRevision(1));
            orders = orders.SetItem(orderId, order);
            groups = groups.SetItem(group.GroupId, group with
            {
                ActiveOrderId = orderId,
                Revision = group.Revision.Next(),
            });
        }
        return state with { Groups = groups, Orders = orders };
    }

    private static OrderKind OperationalOrderKind(EntityState lead, StrategicObjectiveKind objective) =>
        lead.SimulationDomain switch
        {
            SimulationDomain.Air or SimulationDomain.RotaryWing => objective switch
            {
                StrategicObjectiveKind.Defend => OrderKind.Cap,
                StrategicObjectiveKind.Sustain => OrderKind.Escort,
                _ => OrderKind.Intercept,
            },
            SimulationDomain.Logistics => objective == StrategicObjectiveKind.Sustain
                ? OrderKind.Resupply : OrderKind.Escort,
            SimulationDomain.Ground or SimulationDomain.LandAbstract => objective == StrategicObjectiveKind.Defend
                ? OrderKind.Hold : OrderKind.Reinforce,
            _ when IsSubsurface(lead) => objective == StrategicObjectiveKind.Interdict
                ? OrderKind.Intercept : OrderKind.Asw,
            _ => objective switch
            {
                StrategicObjectiveKind.Sustain => OrderKind.Escort,
                StrategicObjectiveKind.Interdict or StrategicObjectiveKind.ProjectForce => OrderKind.Intercept,
                _ => OrderKind.Patrol,
            },
        };

    private static bool IsCompatible(StrategicGroupView group, StrategicObjectiveState objective)
    {
        if (group.ContainsInstallation)
        {
            return objective.Kind == StrategicObjectiveKind.Defend && objective.RegionId == group.RegionId;
        }
        return group.Domain switch
        {
            SimulationDomain.Logistics => objective.Kind is StrategicObjectiveKind.Sustain or StrategicObjectiveKind.Defend,
            SimulationDomain.Ground or SimulationDomain.LandAbstract => objective.Kind is not StrategicObjectiveKind.Sustain,
            _ => true,
        };
    }

    private static long Affinity(StrategicGroupView group, StrategicObjectiveKind objective) =>
        group.Domain switch
        {
            SimulationDomain.Logistics when objective == StrategicObjectiveKind.Sustain => 300_000,
            SimulationDomain.Air or SimulationDomain.RotaryWing when objective == StrategicObjectiveKind.Interdict => 220_000,
            SimulationDomain.Ground or SimulationDomain.LandAbstract when objective == StrategicObjectiveKind.ProjectForce => 180_000,
            SimulationDomain.Maritime when objective == StrategicObjectiveKind.Interdict => 160_000,
            _ when objective == StrategicObjectiveKind.Defend => 100_000,
            _ => 0,
        };

    private static long ThreatResponseAffinity(
        CoalitionStrategicView view,
        StrategicGroupView group,
        StrategicObjectiveState objective,
        IReadOnlyDictionary<(StrategicObjectiveId, ContactClassification), int> assignedResponseCounts)
    {
        if (group.ContainsInstallation)
        {
            return 0;
        }
        var threats = ThreatClasses(view, objective);
        if (threats.Count == 0)
        {
            return 0;
        }
        var responsive = threats.Where(group.EngagementClasses.Contains).ToArray();
        if (responsive.Length == 0)
        {
            // Keep unarmed transports, armor, or the wrong specialist from
            // pre-empting an equally close group that can service the contact.
            // This is a preference rather than an absolute fence: a group still
            // receives a defensible fallback objective when no perfect responder
            // exists.
            return -400_000;
        }
        var leastCoveredRole = responsive.Min(classification =>
            assignedResponseCounts.GetValueOrDefault((objective.ObjectiveId, classification)));
        return Math.Max(0L, 350_000L - leastCoveredRole * 100_000L);
    }

    private static ImmutableSortedSet<ContactClassification> ResponsiveClasses(
        CoalitionStrategicView view,
        StrategicGroupView group,
        StrategicObjectiveState objective) =>
        ThreatClasses(view, objective)
            .Where(group.EngagementClasses.Contains)
            .ToImmutableSortedSet();

    private static ImmutableSortedSet<ContactClassification> ThreatClasses(
        CoalitionStrategicView view,
        StrategicObjectiveState objective)
    {
        if (objective.Kind is not (StrategicObjectiveKind.Interdict or StrategicObjectiveKind.Defend))
        {
            return ImmutableSortedSet<ContactClassification>.Empty;
        }
        var includeNeighbors = objective.Kind == StrategicObjectiveKind.Defend;
        return view.Intelligence
            .Where(contact => contact.Confidence >= IntelligenceConfidence.Probable
                && contact.Classification != ContactClassification.Unknown
                && (contact.EstimatedRegionId == objective.RegionId
                    || includeNeighbors
                        && view.Neighbors[objective.RegionId].Contains(contact.EstimatedRegionId)))
            .Select(contact => contact.Classification == ContactClassification.Logistics
                ? ContactClassification.Surface
                : contact.Classification)
            .ToImmutableSortedSet();
    }

    private static int Distance(
        IReadOnlyDictionary<RegionId, ImmutableSortedSet<RegionId>> neighbors,
        RegionId origin,
        RegionId destination)
    {
        if (origin == destination)
        {
            return 0;
        }
        var visited = new HashSet<RegionId> { origin };
        var queue = new Queue<(RegionId Region, int Distance)>();
        foreach (var neighbor in neighbors[origin])
        {
            visited.Add(neighbor);
            queue.Enqueue((neighbor, 1));
        }
        while (queue.TryDequeue(out var item))
        {
            if (item.Region == destination)
            {
                return item.Distance;
            }
            foreach (var neighbor in neighbors[item.Region])
            {
                if (visited.Add(neighbor))
                {
                    queue.Enqueue((neighbor, item.Distance + 1));
                }
            }
        }
        return int.MaxValue;
    }

    private static bool IsOperational(CampaignState state, EntityState entity) =>
        entity.Lifecycle is not (EntityLifecycle.Disabled or EntityLifecycle.Destroyed or EntityLifecycle.Retired)
        && entity.Fidelity != FidelityState.Suspended
        && !state.Assignments.Values.Any(item => item.EntityId == entity.EntityId
            && !CampaignAssignmentKinds.IsOperationalSim(item)
            && item.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing
                or AssignmentLifecycle.Quiesced);

    private static bool RequiresRecovery(IReadOnlyList<EntityState> members) =>
        members.Max(item => item.DamageMilliUnits) >= 400_000
        || members.Any(item => item.SimulationDomain is SimulationDomain.Air or SimulationDomain.RotaryWing
            && Resource(item, ResourceKind.Fuel) < 150_000)
        || members.Any(item => item.SimulationDomain is SimulationDomain.Air or SimulationDomain.RotaryWing
            && item.OperationalWeapons.Length > 0 && Resource(item, ResourceKind.Munitions) == 0)
        || members.Any(item => IsSubsurface(item) && item.DamageMilliUnits >= 250_000)
        || members.Any(item => Resource(item, ResourceKind.Fuel) < 80_000
            && item.SimulationDomain != SimulationDomain.Logistics);

    private static bool RecoveryReady(IReadOnlyList<EntityState> members, IReadOnlySet<RegionId> homeRegions) =>
        members.All(item => homeRegions.Contains(item.RegionId))
        && members.Max(item => item.DamageMilliUnits) <= 200_000
        && members.Min(item => item.ReadinessMilliUnits) >= 700
        && members.All(item => !item.Resources.ContainsKey(ResourceKind.Fuel)
            || Resource(item, ResourceKind.Fuel) >= 500_000)
        && members.All(item => item.OperationalWeapons.Length == 0
            || Resource(item, ResourceKind.Munitions) > 0);

    private static long Resource(EntityState entity, ResourceKind kind) =>
        entity.Resources.TryGetValue(kind, out var value) ? value.MilliUnits : 0;

    private static bool IsSubsurface(EntityState entity) =>
        entity.CatalogUnitId.Contains("ss", StringComparison.OrdinalIgnoreCase)
        || entity.Name.Contains("submarine", StringComparison.OrdinalIgnoreCase);

    private static CampaignTime Later(CampaignTime first, CampaignTime second) =>
        first.Quanta >= second.Quanta ? first : second;

    private static StrategicObjectiveId ObjectiveId(
        long seed,
        CoalitionId coalitionId,
        RegionId regionId,
        StrategicObjectiveKind kind) =>
        new(KeyedGuid(seed, coalitionId.Value, CampaignTime.Origin,
            $"objective-{regionId.Value:N}-{kind}"));

    private static int StableJitter(
        long seed,
        Guid groupId,
        CampaignTime dueAt,
        Guid objectiveId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{seed}:{groupId:N}:{dueAt.Quanta}:{objectiveId:N}:allocation"));
        return (int)(BitConverter.ToUInt32(bytes, 0) % 1_000);
    }

    private static Guid KeyedGuid(long seed, Guid subject, CampaignTime time, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{subject:N}:{time.Quanta}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record StrategicBaseView(
        BaseId BaseId,
        CoalitionId CoalitionId,
        RegionId RegionId,
        bool IsFriendly,
        long? SupplyMilliUnits);

    private sealed record StrategicGroupView(
        GroupId GroupId,
        RegionId RegionId,
        SimulationDomain Domain,
        bool ContainsInstallation,
        ImmutableSortedSet<ContactClassification> EngagementClasses,
        bool RequiresRecovery,
        bool RecoveryReady);

    private sealed record CoalitionStrategicView(
        CoalitionId CoalitionId,
        ImmutableSortedDictionary<RegionId, ImmutableSortedSet<RegionId>> Neighbors,
        ImmutableArray<StrategicBaseView> Bases,
        ImmutableSortedDictionary<GroupId, StrategicGroupView> Groups,
        ImmutableArray<IntelligenceEstimate> Intelligence)
    {
        public static CoalitionStrategicView Create(CampaignState state, CoalitionId coalitionId)
        {
            var neighbors = state.Regions.Values.ToImmutableSortedDictionary(
                item => item.RegionId,
                item => state.Regions.Values
                    .Where(region => region.RegionId == item.RegionId || region.Neighbors.Contains(item.RegionId))
                    .SelectMany(region => region.RegionId == item.RegionId ? region.Neighbors : [region.RegionId])
                    .Distinct()
                    .ToImmutableSortedSet());
            var bases = state.Bases.Values.Select(item => new StrategicBaseView(
                    item.BaseId,
                    item.CoalitionId,
                    item.RegionId,
                    item.CoalitionId == coalitionId,
                    item.CoalitionId == coalitionId
                        ? new[] { ResourceKind.Fuel, ResourceKind.Stores, ResourceKind.Munitions }
                            .Sum(kind => item.Resources.TryGetValue(kind, out var amount) ? amount.MilliUnits : 0)
                        : null))
                .OrderBy(item => item.BaseId)
                .ToImmutableArray();
            var groups = ImmutableSortedDictionary<GroupId, StrategicGroupView>.Empty;
            foreach (var group in state.Groups.Values.Where(item => item.CoalitionId == coalitionId))
            {
                var members = group.Members.Select(id => state.Entities[id])
                    .Where(item => IsOperational(state, item))
                    .ToArray();
                if (members.Length == 0)
                {
                    continue;
                }
                var homeRegions = members.Select(item => item.HomeBaseId)
                    .Where(item => item is not null)
                    .Select(item => state.Bases[item!.Value].RegionId)
                    .ToHashSet();
                var view = new StrategicGroupView(
                    group.GroupId,
                    members[0].RegionId,
                    members[0].SimulationDomain,
                    members.Any(item => item.SimulationDomain == SimulationDomain.Installation),
                    members.SelectMany(item => item.OperationalWeapons)
                        .Select(item => item.TargetClassification)
                        .ToImmutableSortedSet(),
                    RequiresRecovery(members),
                    RecoveryReady(members, homeRegions));
                groups = groups.Add(group.GroupId, view);
            }
            var intelligence = state.Intelligence.Values
                .Where(item => item.Key.ObserverCoalitionId == coalitionId)
                .OrderBy(item => item.Key)
                .ToImmutableArray();
            return new CoalitionStrategicView(coalitionId, neighbors, bases, groups, intelligence);
        }
    }
}
