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
    private const long CommitmentQuanta = 300;
    private const long RecoveryLatchQuanta = 60;
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
            result = PruneOperationalPackages(result, coalitionId, dueAt);

            if (!result.CoalitionStrategies.TryGetValue(coalitionId, out var strategy)
                || strategy.NextDirectorAt.Quanta <= dueAt.Quanta)
            {
                result = DirectCoalition(result, view, dueAt, index, strategy);
            }
        }

        return CampaignOperationalControllers.IssueOrders(result, dueAt);
    }

    private static CampaignState PruneOperationalPackages(
        CampaignState state,
        CoalitionId coalitionId,
        CampaignTime dueAt)
    {
        var packages = state.OperationalPackages;
        foreach (var package in state.OperationalPackages.Values.Where(
            item => item.CoalitionId == coalitionId))
        {
            var retainedMembers = package.Members.Values.Where(member =>
                    state.Groups.TryGetValue(member.GroupId, out var group)
                    && group.CoalitionId == coalitionId
                    && group.Members.Any(entityId => IsOperational(state, state.Entities[entityId]))
                    && !group.Members.Any(entityId =>
                        state.Entities[entityId].SimulationDomain == SimulationDomain.Installation)
                    && state.GroupCommitments.TryGetValue(member.GroupId, out var commitment)
                    && !commitment.WithdrawLatched
                    && commitment.ObjectiveId == package.ObjectiveId
                    && commitment.ExpiresAt.Quanta >= package.ExpiresAt.Quanta)
                .ToImmutableSortedDictionary(member => member.GroupId, member => member);
            if (package.ExpiresAt.Quanta <= dueAt.Quanta || retainedMembers.Count < 2)
            {
                packages = packages.Remove(package.PackageId);
            }
            else if (retainedMembers.Count != package.Members.Count)
            {
                packages = packages.SetItem(package.PackageId, package with
                {
                    Members = retainedMembers,
                    Revision = package.Revision.Next(),
                });
            }
        }
        return state with { OperationalPackages = packages };
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
        var assignedPackageRoleCounts = new Dictionary<
            (StrategicObjectiveId ObjectiveId, OperationalPackageRole Role), int>();
        var assignedDomainCounts = new Dictionary<
            (StrategicObjectiveId ObjectiveId, SimulationDomain Domain), int>();
        var assignedRouteCounts = new Dictionary<
            (RegionId Origin, RegionId Next, SimulationDomain Domain), int>();
        var replannedGroups = new List<GroupId>();
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
                    assignedPackageRoleCounts,
                    assignedDomainCounts,
                    assignedRouteCounts,
                    state.Metadata.Seed,
                    dueAt);
            }

            if (selected is { } objectiveId)
            {
                assignedCounts[objectiveId] = assignedCounts.GetValueOrDefault(objectiveId) + 1;
                var selectedObjective = planned.First(item => item.ObjectiveId == objectiveId);
                if (!group.ContainsInstallation)
                {
                    var domainKey = (objectiveId, group.Domain);
                    assignedDomainCounts[domainKey] = assignedDomainCounts.GetValueOrDefault(domainKey) + 1;
                    var next = NextHop(view.Neighbors, group.RegionId, selectedObjective.RegionId);
                    if (next != group.RegionId)
                    {
                        var routeKey = (group.RegionId, next, group.Domain);
                        assignedRouteCounts[routeKey] = assignedRouteCounts.GetValueOrDefault(routeKey) + 1;
                    }
                }
                foreach (var classification in ResponsiveClasses(view, group, selectedObjective))
                {
                    var key = (objectiveId, classification);
                    assignedResponseCounts[key] = assignedResponseCounts.GetValueOrDefault(key) + 1;
                }
                if (!group.ContainsInstallation
                    && !(prior?.WithdrawLatched ?? group.RequiresRecovery))
                {
                    var packageRole = SelectPackageRole(
                        view,
                        group,
                        selectedObjective,
                        localRoleCounts: null,
                        assignedPackageRoleCounts);
                    var roleKey = (objectiveId, packageRole);
                    assignedPackageRoleCounts[roleKey] = assignedPackageRoleCounts.GetValueOrDefault(roleKey) + 1;
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
            if (prior is not null && !retainedCommitment)
            {
                replannedGroups.Add(group.GroupId);
            }
        }

        var groups = state.Groups;
        var orders = state.Orders;
        foreach (var groupId in replannedGroups)
        {
            var group = groups[groupId];
            if (group.ActiveOrderId is not { } orderId
                || !orders.TryGetValue(orderId, out var order)
                || order.MustCompleteBy is null)
            {
                continue;
            }
            orders = orders.SetItem(orderId, order with
            {
                CompletedAt = dueAt,
                Revision = order.Revision.Next(),
            });
            groups = groups.SetItem(groupId, group with
            {
                ActiveOrderId = null,
                Revision = group.Revision.Next(),
            });
        }

        var nextAt = new CampaignTime(checked(
            dueAt.Quanta + DirectorCadenceQuanta + coalitionIndex * 10L));
        var strategy = new CoalitionStrategyState(
            view.CoalitionId,
            nextAt,
            epoch,
            priorStrategy is null ? new EntityRevision(1) : priorStrategy.Revision.Next());
        var operationalPackages = BuildOperationalPackages(
            state,
            view,
            commitments,
            objectives,
            dueAt,
            epoch);
        return state with
        {
            StrategicObjectives = objectives,
            GroupCommitments = commitments,
            OperationalPackages = operationalPackages,
            Groups = groups,
            Orders = orders,
            CoalitionStrategies = state.CoalitionStrategies.SetItem(view.CoalitionId, strategy),
        };
    }

    private static ImmutableSortedDictionary<OperationalPackageId, OperationalPackageState> BuildOperationalPackages(
        CampaignState state,
        CoalitionStrategicView view,
        ImmutableSortedDictionary<GroupId, GroupCommitmentState> commitments,
        ImmutableSortedDictionary<StrategicObjectiveId, StrategicObjectiveState> objectives,
        CampaignTime dueAt,
        long epoch)
    {
        var packages = state.OperationalPackages
            .Where(item => item.Value.CoalitionId != view.CoalitionId
                || item.Value.ExpiresAt.Quanta > dueAt.Quanta
                    && item.Value.Members.Keys.All(groupId =>
                        commitments.TryGetValue(groupId, out var commitment)
                        && !commitment.WithdrawLatched
                        && commitment.ObjectiveId == item.Value.ObjectiveId
                        && commitment.ExpiresAt.Quanta >= item.Value.ExpiresAt.Quanta))
            .ToImmutableSortedDictionary();
        var alreadyPackaged = packages.Values
            .Where(package => package.CoalitionId == view.CoalitionId)
            .SelectMany(package => package.Members.Keys)
            .ToHashSet();
        var eligible = view.Groups.Values
            .Where(group => !group.ContainsInstallation
                && !alreadyPackaged.Contains(group.GroupId)
                && commitments.TryGetValue(group.GroupId, out var commitment)
                && !commitment.WithdrawLatched
                && commitment.ObjectiveId is not null
                && commitment.ExpiresAt.Quanta > dueAt.Quanta)
            .GroupBy(group => commitments[group.GroupId].ObjectiveId!.Value)
            .OrderBy(group => group.Key);

        foreach (var objectiveGroups in eligible)
        {
            if (!objectives.TryGetValue(objectiveGroups.Key, out var objective))
            {
                continue;
            }
            var remaining = objectiveGroups.OrderBy(group => group.GroupId).ToList();
            var packageIndex = 0;
            while (remaining.Count >= 2)
            {
                var anchor = remaining[0];
                var roleCounts = new Dictionary<OperationalPackageRole, int>();
                var selected = new List<StrategicGroupView>();
                while (selected.Count < 6 && remaining.Count > 0)
                {
                    var candidate = remaining
                        .Select(group => new
                        {
                            Group = group,
                            Role = SelectPackageRole(view, group, objective, roleCounts),
                            Distance = Distance(view.Neighbors, anchor.RegionId, group.RegionId),
                        })
                        .OrderBy(item => roleCounts.GetValueOrDefault(item.Role))
                        .ThenBy(item => item.Distance)
                        .ThenBy(item => item.Role)
                        .ThenBy(item => item.Group.GroupId)
                        .First();
                    selected.Add(candidate.Group);
                    roleCounts[candidate.Role] = roleCounts.GetValueOrDefault(candidate.Role) + 1;
                    remaining.Remove(candidate.Group);
                }
                if (selected.Count < 2)
                {
                    break;
                }

                var maximumTransit = selected.Max(group =>
                    Distance(view.Neighbors, group.RegionId, objective.RegionId));
                var executeAt = new CampaignTime(checked(
                    dueAt.Quanta + OrderQuanta + Math.Min(120, maximumTransit * 15L)));
                var expiresAt = new CampaignTime(selected.Min(group =>
                    commitments[group.GroupId].ExpiresAt.Quanta));
                if (executeAt.Quanta >= expiresAt.Quanta)
                {
                    executeAt = new CampaignTime(Math.Max(dueAt.Quanta, expiresAt.Quanta - OrderQuanta));
                }
                var members = ImmutableSortedDictionary<GroupId, OperationalPackageMemberState>.Empty;
                roleCounts.Clear();
                foreach (var group in selected)
                {
                    var role = SelectPackageRole(view, group, objective, roleCounts);
                    roleCounts[role] = roleCounts.GetValueOrDefault(role) + 1;
                    members = members.Add(group.GroupId, new OperationalPackageMemberState(
                        group.GroupId,
                        role,
                        PackageStartsAt(group.Domain, dueAt, executeAt)));
                }
                var packageId = new OperationalPackageId(KeyedGuid(
                    state.Metadata.Seed,
                    objective.ObjectiveId.Value,
                    dueAt,
                    $"operational-package-{epoch}-{packageIndex++}"));
                packages = packages.Add(packageId, new OperationalPackageState(
                    packageId,
                    view.CoalitionId,
                    objective.ObjectiveId,
                    objective.RegionId,
                    dueAt,
                    executeAt,
                    expiresAt,
                    members,
                    new EntityRevision(1)));
            }
        }
        return packages;
    }

    private static OperationalPackageRole SelectPackageRole(
        CoalitionStrategicView view,
        StrategicGroupView group,
        StrategicObjectiveState objective,
        IReadOnlyDictionary<OperationalPackageRole, int>? localRoleCounts,
        IReadOnlyDictionary<(StrategicObjectiveId, OperationalPackageRole), int>? assignedRoleCounts = null)
    {
        int Count(OperationalPackageRole role) => localRoleCounts?.GetValueOrDefault(role)
            ?? assignedRoleCounts?.GetValueOrDefault((objective.ObjectiveId, role))
            ?? 0;
        if (group.Domain == SimulationDomain.Logistics)
        {
            return OperationalPackageRole.Sustainment;
        }
        if (group.Domain is SimulationDomain.Ground or SimulationDomain.LandAbstract)
        {
            return objective.Kind == StrategicObjectiveKind.Interdict
                && group.EngagementClasses.Contains(ContactClassification.LandFormation)
                    ? OperationalPackageRole.LandAttack
                    : OperationalPackageRole.GroundSupport;
        }

        var responsive = ThreatClasses(view, objective)
            .Where(group.EngagementClasses.Contains)
            .Select(classification => classification switch
            {
                ContactClassification.Air => OperationalPackageRole.AirCover,
                ContactClassification.Subsurface => OperationalPackageRole.AntiSubmarine,
                ContactClassification.LandFormation => OperationalPackageRole.LandAttack,
                _ => OperationalPackageRole.SurfaceEscort,
            })
            .Distinct()
            .OrderBy(Count)
            .ThenBy(role => role)
            .ToArray();
        if (objective.Kind is StrategicObjectiveKind.Interdict or StrategicObjectiveKind.ProjectForce
            && Count(OperationalPackageRole.Strike) == 0
            && responsive.Any(role => role is OperationalPackageRole.SurfaceEscort
                or OperationalPackageRole.LandAttack))
        {
            return OperationalPackageRole.Strike;
        }
        if (group.Domain is SimulationDomain.Air or SimulationDomain.RotaryWing
            && group.EngagementClasses.Contains(ContactClassification.Air))
        {
            return OperationalPackageRole.AirCover;
        }
        if (responsive.Length > 0)
        {
            return responsive[0];
        }
        if (group.ContainsSubsurface)
        {
            return OperationalPackageRole.AntiSubmarine;
        }
        return OperationalPackageRole.Escort;
    }

    private static CampaignTime PackageStartsAt(
        SimulationDomain domain,
        CampaignTime assemblyAt,
        CampaignTime executeAt)
    {
        var leadQuanta = domain switch
        {
            SimulationDomain.Air => 10,
            SimulationDomain.RotaryWing => 20,
            _ => OrderQuanta,
        };
        return new CampaignTime(Math.Max(assemblyAt.Quanta, executeAt.Quanta - leadQuanta));
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
        IReadOnlyDictionary<(StrategicObjectiveId, OperationalPackageRole), int> assignedPackageRoleCounts,
        IReadOnlyDictionary<(StrategicObjectiveId, SimulationDomain), int> assignedDomainCounts,
        IReadOnlyDictionary<(RegionId, RegionId, SimulationDomain), int> assignedRouteCounts,
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
            if (distance == int.MaxValue || distance > MaximumObjectiveDistance(group.Domain))
            {
                continue;
            }
            if (!group.ContainsInstallation
                && assignedDomainCounts.GetValueOrDefault((objective.ObjectiveId, group.Domain))
                    >= ObjectiveDomainCapacity(objective.Kind, group.Domain))
            {
                continue;
            }
            var saturation = assignedCounts.GetValueOrDefault(objective.ObjectiveId);
            var next = NextHop(view.Neighbors, group.RegionId, objective.RegionId);
            var routeLoad = next == group.RegionId
                ? 0
                : assignedRouteCounts.GetValueOrDefault((group.RegionId, next, group.Domain));
            var score = objective.PriorityMilliUnits * 1_000L
                - distance * 150_000L
                - saturation * 100_000L
                - routeLoad * 140_000L
                + Affinity(group, objective.Kind)
                + ThreatResponseAffinity(view, group, objective, assignedResponseCounts)
                + PackageRoleAffinity(view, group, objective, assignedPackageRoleCounts)
                + StableJitter(seed, group.GroupId.Value, dueAt, objective.ObjectiveId.Value);
            if (score > bestScore || score == bestScore && objective.ObjectiveId.CompareTo(best!.ObjectiveId) < 0)
            {
                best = objective;
                bestScore = score;
            }
        }
        return best?.ObjectiveId;
    }

    private static int MaximumObjectiveDistance(SimulationDomain domain) => domain switch
    {
        SimulationDomain.Installation => 0,
        SimulationDomain.Ground or SimulationDomain.LandAbstract => 1,
        SimulationDomain.RotaryWing => 2,
        SimulationDomain.Air => 5,
        SimulationDomain.Maritime => 7,
        SimulationDomain.Logistics => 8,
        _ => 4,
    };

    private static int ObjectiveDomainCapacity(StrategicObjectiveKind kind, SimulationDomain domain) => domain switch
    {
        SimulationDomain.Installation => int.MaxValue,
        SimulationDomain.Ground => kind == StrategicObjectiveKind.Defend ? 10 : 8,
        SimulationDomain.LandAbstract => 5,
        SimulationDomain.Air => kind == StrategicObjectiveKind.Interdict ? 8 : 5,
        SimulationDomain.RotaryWing => 4,
        SimulationDomain.Maritime => kind == StrategicObjectiveKind.Interdict ? 7 : 5,
        SimulationDomain.Logistics => kind == StrategicObjectiveKind.Sustain ? 4 : 2,
        _ => 4,
    };

    private static RegionId NextHop(
        IReadOnlyDictionary<RegionId, ImmutableSortedSet<RegionId>> neighbors,
        RegionId origin,
        RegionId destination)
    {
        if (origin == destination)
        {
            return origin;
        }
        return neighbors[origin]
            .Select(candidate => (Region: candidate, Distance: Distance(neighbors, candidate, destination)))
            .Where(item => item.Distance != int.MaxValue)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Region)
            .Select(item => item.Region)
            .FirstOrDefault(origin);
    }

    private static long PackageRoleAffinity(
        CoalitionStrategicView view,
        StrategicGroupView group,
        StrategicObjectiveState objective,
        IReadOnlyDictionary<(StrategicObjectiveId, OperationalPackageRole), int> assignedPackageRoleCounts)
    {
        if (group.ContainsInstallation || objective.Kind == StrategicObjectiveKind.Sustain)
        {
            return 0;
        }
        var role = SelectPackageRole(
            view,
            group,
            objective,
            localRoleCounts: null,
            assignedPackageRoleCounts);
        var assigned = assignedPackageRoleCounts.GetValueOrDefault((objective.ObjectiveId, role));
        return role switch
        {
            OperationalPackageRole.AirCover when assigned == 0 => 500_000,
            OperationalPackageRole.Sustainment when assigned == 0 => 500_000,
            OperationalPackageRole.Strike when assigned == 0 => 350_000,
            OperationalPackageRole.AntiSubmarine when assigned == 0 => 300_000,
            OperationalPackageRole.SurfaceEscort when assigned == 0 => 250_000,
            OperationalPackageRole.LandAttack when assigned == 0 => 250_000,
            OperationalPackageRole.GroundSupport when assigned == 0 => 200_000,
            OperationalPackageRole.AirCover or OperationalPackageRole.Sustainment when assigned == 1 => 125_000,
            _ => 0,
        };
    }

    internal static OrderKind PackageOrderKind(OrderKind fallback, OperationalPackageRole role) =>
        role switch
        {
            OperationalPackageRole.AirCover => OrderKind.Cap,
            OperationalPackageRole.AntiSubmarine => OrderKind.Asw,
            OperationalPackageRole.SurfaceEscort or OperationalPackageRole.Escort => OrderKind.Escort,
            OperationalPackageRole.Sustainment => OrderKind.Resupply,
            OperationalPackageRole.GroundSupport => OrderKind.Reinforce,
            _ => fallback,
        };

    internal static OrderKind OperationalOrderKind(EntityState lead, StrategicObjectiveKind objective) =>
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
        if (group.IsNonCombatant)
        {
            return false;
        }
        if (group.ContainsInstallation)
        {
            return objective.Kind == StrategicObjectiveKind.Defend && objective.RegionId == group.RegionId;
        }
        return group.Domain switch
        {
            SimulationDomain.Logistics => true,
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
        bool ContainsSubsurface,
        bool IsNonCombatant,
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
                    members.Any(IsFixedOperationalSite),
                    members.Any(IsSubsurface),
                    members.All(IsCivilianCatalog)
                        && group.Name.StartsWith("Civilian ", StringComparison.Ordinal),
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

    internal static bool IsFixedOperationalSite(EntityState entity)
    {
        var catalog = entity.CatalogUnitId;
        return entity.SimulationDomain == SimulationDomain.Installation
            || catalog.StartsWith("tgt_", StringComparison.Ordinal)
            || catalog.StartsWith("warehouses_", StringComparison.Ordinal)
            || catalog.StartsWith("oil_terminal_", StringComparison.Ordinal)
            || catalog.StartsWith("civ_comm_", StringComparison.Ordinal)
            || catalog.StartsWith("civ_radio", StringComparison.Ordinal)
            || catalog.StartsWith("civ_spar_rig", StringComparison.Ordinal)
            || catalog.StartsWith("civ_tgt_bridge", StringComparison.Ordinal)
            || catalog.StartsWith("port_", StringComparison.Ordinal);
    }

    private static bool IsCivilianCatalog(EntityState entity) =>
        entity.CatalogUnitId.StartsWith("civ_", StringComparison.Ordinal)
        || entity.CatalogUnitId.StartsWith("merchant_", StringComparison.Ordinal);
}
