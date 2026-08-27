using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

/// <summary>
/// Domain controllers. <see cref="CampaignStrategicAi"/> decides what a coalition
/// wants: objectives, group commitments, and operational packages. It never
/// writes an order. This file is the only place that turns that intent into
/// canonical orders, and it is split by simulation domain on purpose. A surface
/// group, a fighter package, a helicopter flight, a motor-rifle regiment, and a
/// supply convoy do not plan alike, and one switch over <see cref="OrderKind"/>
/// would bury the differences that matter.
///
/// Three fences hold for every controller:
/// <list type="bullet">
/// <item>Player authority. A controller owns exactly one order slot per group and
/// supersedes nothing else, so an indefinite player order and an emergency
/// withdrawal raised by the operational resolver both survive untouched.</item>
/// <item>Tactical authority. A group holding any entity that a live Sea Power
/// island owns receives no campaign order at all.</item>
/// <item>Fixed installations defend in place and never receive a movement
/// order.</item>
/// </list>
///
/// Orders are idempotent: an unchanged intent leaves the existing order alone, so
/// a group that already sits where it was told to go holds without churn and can
/// replenish from the base underneath it.
/// </summary>
internal static class CampaignOperationalControllers
{
    public static CampaignState IssueOrders(CampaignState state, CampaignTime dueAt)
    {
        var theater = OperationalTheater.Create(state, dueAt);
        var groups = state.Groups;
        var orders = state.Orders;
        foreach (var group in state.Groups.Values)
        {
            var orderId = ControllerOrderId(state.Metadata.Seed, group.GroupId);
            if (group.ActiveOrderId is { } activeId && activeId != orderId)
            {
                // A player order or an operational withdrawal owns this group.
                continue;
            }

            var owned = orders.TryGetValue(orderId, out var found) ? found : null;
            var active = group.ActiveOrderId is null ? null : owned;
            if (active is { MustCompleteBy: null })
            {
                continue;
            }

            var decision = theater.Decide(group);
            if (decision.Retain)
            {
                continue;
            }

            if (decision.Intent is not { } intent)
            {
                if (active is null)
                {
                    continue;
                }
                orders = Close(orders, active, dueAt);
                groups = groups.SetItem(group.GroupId, group with
                {
                    ActiveOrderId = null,
                    Revision = group.Revision.Next(),
                });
                continue;
            }

            if (active is not null
                && active.Kind == intent.Kind
                && active.DestinationRegionId == intent.DestinationRegionId)
            {
                continue;
            }

            orders = orders.SetItem(orderId, new OrderState(
                orderId,
                group.GroupId,
                intent.Kind,
                intent.DestinationRegionId,
                dueAt,
                intent.MustCompleteBy,
                CompletedAt: null,
                owned is null ? new EntityRevision(1) : owned.Revision.Next()));
            if (group.ActiveOrderId != orderId)
            {
                groups = groups.SetItem(group.GroupId, group with
                {
                    ActiveOrderId = orderId,
                    Revision = group.Revision.Next(),
                });
            }
        }

        return state with { Groups = groups, Orders = orders };
    }

    private static ImmutableSortedDictionary<OrderId, OrderState> Close(
        ImmutableSortedDictionary<OrderId, OrderState> orders,
        OrderState order,
        CampaignTime dueAt) =>
        order.StartsAt.Quanta <= dueAt.Quanta
            ? orders.SetItem(order.OrderId, order with
            {
                CompletedAt = dueAt,
                Revision = order.Revision.Next(),
            })
            : orders.Remove(order.OrderId);

    private static OrderId ControllerOrderId(long seed, GroupId groupId) =>
        new(KeyedGuid(seed, groupId.Value, "operational-controller-order"));

    private static Guid KeyedGuid(long seed, Guid subject, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{subject:N}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    /// <summary>What a controller wants a group to be doing right now.</summary>
    private sealed record OperationalIntent(
        OrderKind Kind,
        RegionId DestinationRegionId,
        CampaignTime MustCompleteBy);

    private readonly record struct ControlDecision(bool Retain, OperationalIntent? Intent)
    {
        /// <summary>Leave whatever the group already has in place.</summary>
        public static ControlDecision Keep => new(true, null);

        /// <summary>Hold: give up the controller's own order and issue nothing.</summary>
        public static ControlDecision Hold => new(false, null);

        public static ControlDecision Issue(OperationalIntent? intent) =>
            intent is null ? Hold : new(false, intent);
    }

    private enum OperationalPhase
    {
        /// <summary>Committed to a package, but this member's release time has not arrived.</summary>
        Ready,

        /// <summary>Moving to the package staging region before the execution time.</summary>
        Assembly,

        /// <summary>Prosecuting the objective.</summary>
        Execution,

        /// <summary>Returning to a base region to repair, rearm, and refuel.</summary>
        Recovery,
    }

    /// <summary>
    /// One tick of coalition-blind bookkeeping: region graph, package membership,
    /// and the staging region each package marshals in. Built once per tick so
    /// every controller sees the same geography.
    /// </summary>
    private sealed class OperationalTheater
    {
        private readonly CampaignState state;
        private readonly CampaignTime dueAt;
        private readonly OperationalGeography geography;
        private readonly Dictionary<GroupId, (OperationalPackageState Package, OperationalPackageMemberState Member)> packages;
        private readonly Dictionary<OperationalPackageId, RegionId> stagingRegions = [];
        private readonly HashSet<EntityId> tacticallyHeld;

        private OperationalTheater(
            CampaignState state,
            CampaignTime dueAt,
            OperationalGeography geography,
            Dictionary<GroupId, (OperationalPackageState, OperationalPackageMemberState)> packages,
            HashSet<EntityId> tacticallyHeld)
        {
            this.state = state;
            this.dueAt = dueAt;
            this.geography = geography;
            this.packages = packages;
            this.tacticallyHeld = tacticallyHeld;
        }

        public static OperationalTheater Create(CampaignState state, CampaignTime dueAt)
        {
            var packages = new Dictionary<GroupId, (OperationalPackageState, OperationalPackageMemberState)>();
            foreach (var package in state.OperationalPackages.Values)
            {
                foreach (var member in package.Members.Values)
                {
                    if (!packages.TryGetValue(member.GroupId, out var existing)
                        || package.PackageId.CompareTo(existing.Item1.PackageId) < 0)
                    {
                        packages[member.GroupId] = (package, member);
                    }
                }
            }

            var tacticallyHeld = state.Assignments.Values
                .Where(item => !CampaignAssignmentKinds.IsOperationalSim(item)
                    && item.Lifecycle is AssignmentLifecycle.Active
                        or AssignmentLifecycle.Quiescing
                        or AssignmentLifecycle.Quiesced)
                .Select(item => item.EntityId)
                .ToHashSet();

            return new OperationalTheater(
                state,
                dueAt,
                OperationalGeography.Create(state),
                packages,
                tacticallyHeld);
        }

        public ControlDecision Decide(GroupState group)
        {
            var members = group.Members.Select(id => state.Entities[id]).ToImmutableArray();
            if (members.Length == 0)
            {
                return ControlDecision.Keep;
            }

            // A canonical group containing a fixed installation is one defensive
            // complex. Moving only its fuel farm would split authority from the
            // runway that makes the group meaningful, and including the runway
            // would create an order that can never arrive.
            if (members.Any(CampaignStrategicAi.IsFixedOperationalSite))
            {
                return ControlDecision.Hold;
            }

            // A live tactical island owns these entities. The campaign does not
            // compete with it for the group.
            if (members.Any(item => tacticallyHeld.Contains(item.EntityId)))
            {
                return ControlDecision.Hold;
            }

            var operational = members.Where(IsOperational).ToImmutableArray();
            if (operational.Length == 0)
            {
                return ControlDecision.Keep;
            }

            // Without live intent the controller keeps whatever the group is
            // already doing rather than stalling it between director passes.
            if (!state.GroupCommitments.TryGetValue(group.GroupId, out var commitment)
                || commitment.ExpiresAt.Quanta <= dueAt.Quanta)
            {
                return ControlDecision.Keep;
            }

            StrategicObjectiveState? objective = null;
            if (commitment.ObjectiveId is { } objectiveId)
            {
                state.StrategicObjectives.TryGetValue(objectiveId, out objective);
            }
            if (objective is null && !commitment.WithdrawLatched)
            {
                return ControlDecision.Hold;
            }

            var context = CreateContext(group, members, operational, commitment, objective);
            return ControlDecision.Issue(Controller(context.Domain).Plan(context));
        }

        private OperationalControlContext CreateContext(
            GroupState group,
            ImmutableArray<EntityState> members,
            ImmutableArray<EntityState> operational,
            GroupCommitmentState commitment,
            StrategicObjectiveState? objective)
        {
            var lead = operational[0];
            (OperationalPackageState Package, OperationalPackageMemberState Member)? assigned = null;
            if (packages.TryGetValue(group.GroupId, out var entry)
                && objective is not null
                && entry.Package.ObjectiveId == objective.ObjectiveId)
            {
                assigned = entry;
            }

            var recoveryRegionId = RecoveryRegion(operational, lead.RegionId, lead.CoalitionId);
            var phase = Phase(operational, commitment, objective, assigned);
            var stagingRegionId = assigned is { } package ? StagingRegion(package.Package) : (RegionId?)null;
            return new OperationalControlContext(
                geography,
                lead,
                operational,
                members.Select(item => item.RegionId).Distinct().ToImmutableArray(),
                commitment,
                objective,
                assigned?.Package,
                assigned?.Member,
                stagingRegionId,
                recoveryRegionId,
                phase,
                dueAt);
        }

        private OperationalPhase Phase(
            ImmutableArray<EntityState> operational,
            GroupCommitmentState commitment,
            StrategicObjectiveState? objective,
            (OperationalPackageState Package, OperationalPackageMemberState Member)? assigned)
        {
            if (commitment.WithdrawLatched || objective is null)
            {
                return OperationalPhase.Recovery;
            }
            if (assigned is not { } package)
            {
                return OperationalPhase.Execution;
            }
            // A group that already occupies the objective does not vacate it to
            // stage somewhere else; it is the position the package is forming on.
            if (operational.All(item => item.RegionId == objective.RegionId))
            {
                return OperationalPhase.Execution;
            }
            if (dueAt.Quanta < package.Member.StartsAt.Quanta)
            {
                return OperationalPhase.Ready;
            }
            return dueAt.Quanta < package.Package.ExecuteAt.Quanta
                ? OperationalPhase.Assembly
                : OperationalPhase.Execution;
        }

        /// <summary>
        /// The one region a package marshals in before it executes. Chosen from the
        /// regions adjacent to the objective, preferring ground the coalition
        /// already owns and the shortest total approach, so every member of the
        /// package converges on the same place.
        /// </summary>
        private RegionId StagingRegion(OperationalPackageState package)
        {
            if (stagingRegions.TryGetValue(package.PackageId, out var cached))
            {
                return cached;
            }

            var approaches = package.Members.Keys
                .Where(state.Groups.ContainsKey)
                .SelectMany(groupId => state.Groups[groupId].Members)
                .Select(entityId => state.Entities[entityId].RegionId)
                .Distinct()
                .ToArray();
            var staging = geography.Neighbors(package.RegionId)
                .Where(candidate => approaches.All(origin => geography.IsReachable(origin, candidate)))
                .Select(candidate => new
                {
                    Region = candidate,
                    Hostile = state.Bases.Values.Any(item =>
                        item.RegionId == candidate && item.CoalitionId != package.CoalitionId) ? 1 : 0,
                    Friendly = state.Bases.Values.Any(item =>
                        item.RegionId == candidate && item.CoalitionId == package.CoalitionId) ? 0 : 1,
                    Approach = approaches.Sum(origin => (long)geography.Distance(origin, candidate)),
                })
                .OrderBy(item => item.Hostile)
                .ThenBy(item => item.Friendly)
                .ThenBy(item => item.Approach)
                .ThenBy(item => item.Region)
                .Select(item => item.Region)
                .FirstOrDefault(package.RegionId);
            stagingRegions[package.PackageId] = staging;
            return staging;
        }

        /// <summary>
        /// A credible recovery region: the base region most of the group calls
        /// home, then the nearest friendly base region if no member has one.
        /// </summary>
        private RegionId? RecoveryRegion(
            ImmutableArray<EntityState> operational,
            RegionId from,
            CoalitionId coalitionId)
        {
            var home = operational
                .Select(item => item.HomeBaseId)
                .Where(item => item is not null && state.Bases.ContainsKey(item.Value))
                .Select(item => state.Bases[item!.Value].RegionId)
                .Where(region => geography.IsReachable(from, region))
                .GroupBy(region => region)
                .OrderByDescending(item => item.Count())
                .ThenBy(item => geography.Distance(from, item.Key))
                .ThenBy(item => item.Key)
                .Select(item => (RegionId?)item.Key)
                .FirstOrDefault();
            return home ?? state.Bases.Values
                .Where(item => item.CoalitionId == coalitionId && geography.IsReachable(from, item.RegionId))
                .OrderBy(item => geography.Distance(from, item.RegionId))
                .ThenBy(item => item.RegionId)
                .Select(item => (RegionId?)item.RegionId)
                .FirstOrDefault();
        }

        private static bool IsOperational(EntityState entity) =>
            entity.Lifecycle is not (EntityLifecycle.Disabled or EntityLifecycle.Destroyed or EntityLifecycle.Retired)
            && entity.Fidelity != FidelityState.Suspended;
    }

    private sealed record OperationalControlContext(
        OperationalGeography Geography,
        EntityState Lead,
        ImmutableArray<EntityState> Members,
        ImmutableArray<RegionId> MemberRegions,
        GroupCommitmentState Commitment,
        StrategicObjectiveState? Objective,
        OperationalPackageState? Package,
        OperationalPackageMemberState? PackageMember,
        RegionId? StagingRegionId,
        RegionId? RecoveryRegionId,
        OperationalPhase Phase,
        CampaignTime DueAt)
    {
        public SimulationDomain Domain => Lead.SimulationDomain;

        public RegionId RegionId => Lead.RegionId;

        public RegionId ObjectiveRegionId => Objective!.RegionId;

        public StrategicObjectiveKind ObjectiveKind => Objective!.Kind;

        /// <summary>When the current commitment or package stops paying for this group.</summary>
        public CampaignTime StationDeadline => Package is null
            ? Commitment.ExpiresAt
            : new CampaignTime(Math.Min(Commitment.ExpiresAt.Quanta, Package.ExpiresAt.Quanta));

        public CampaignTime AssemblyDeadline => Package?.ExecuteAt ?? StationDeadline;

        public long Fuel => Members.Min(item => Resource(item, ResourceKind.Fuel));

        public long Stores => Members.Min(item => Resource(item, ResourceKind.Stores));

        public int HopsFromHome => RecoveryRegionId is { } home
            ? Geography.Distance(RegionId, home)
            : 0;

        public bool AllMembersIn(RegionId region) => Members.All(item => item.RegionId == region);

        /// <summary>
        /// Take up station in a region. Issued even when the group is already
        /// there, because the patrol legs, orbits, and sprint-and-drift the
        /// resolver runs for these order kinds are the work itself.
        /// </summary>
        public OperationalIntent? Station(RegionId region, OrderKind kind, CampaignTime deadline) =>
            MemberRegions.All(origin => Geography.IsReachable(origin, region))
                ? new OperationalIntent(kind, region, Deadline(deadline))
                : null;

        /// <summary>
        /// Move to a region, or hold silently once the group is there. Holding
        /// without an order is what lets a group dig in, reload from the base it
        /// is sitting on, and stop churning the canonical order table.
        /// </summary>
        public OperationalIntent? MoveTo(RegionId region, OrderKind kind, CampaignTime deadline) =>
            AllMembersIn(region) ? null : Station(region, kind, deadline);

        /// <summary>The canonical order kind for this group's objective and package role.</summary>
        public OrderKind ObjectiveOrderKind()
        {
            var kind = CampaignStrategicAi.OperationalOrderKind(Lead, ObjectiveKind);
            return PackageMember is { } member
                ? CampaignStrategicAi.PackageOrderKind(kind, member.Role)
                : kind;
        }

        private CampaignTime Deadline(CampaignTime candidate) =>
            new(Math.Max(candidate.Quanta, DueAt.Quanta));

        private static long Resource(EntityState entity, ResourceKind kind) =>
            entity.Resources.TryGetValue(kind, out var value) ? value.MilliUnits : 0;
    }

    private static OperationalDomainController Controller(SimulationDomain domain) => domain switch
    {
        SimulationDomain.Air => AirController,
        SimulationDomain.RotaryWing => RotaryWingController,
        SimulationDomain.Ground or SimulationDomain.LandAbstract => GroundController,
        SimulationDomain.Logistics => LogisticsController,
        _ => MaritimeController,
    };

    private static readonly OperationalDomainController MaritimeController = new MaritimeDomainController();
    private static readonly OperationalDomainController AirController = new AirDomainController();
    private static readonly OperationalDomainController RotaryWingController = new RotaryWingDomainController();
    private static readonly OperationalDomainController GroundController = new GroundDomainController();
    private static readonly OperationalDomainController LogisticsController = new LogisticsDomainController();

    private abstract class OperationalDomainController
    {
        public OperationalIntent? Plan(OperationalControlContext context)
        {
            if (context.Phase == OperationalPhase.Recovery || MustRecover(context))
            {
                return Recover(context);
            }
            return context.Phase switch
            {
                // Released to the package but not yet due out: sit tight so the
                // faster members do not arrive at the staging region first.
                OperationalPhase.Ready => null,
                OperationalPhase.Assembly => Assemble(context),
                _ => Execute(context),
            };
        }

        /// <summary>Marshal at the package staging region before the execution time.</summary>
        protected virtual OperationalIntent? Assemble(OperationalControlContext context) =>
            context.StagingRegionId is { } staging
                ? context.MoveTo(staging, OrderKind.Transit, context.AssemblyDeadline)
                : null;

        protected abstract OperationalIntent? Execute(OperationalControlContext context);

        /// <summary>Whether the domain has to break station before the commitment lapses.</summary>
        protected virtual bool MustRecover(OperationalControlContext context) => false;

        protected virtual OperationalIntent? Recover(OperationalControlContext context) =>
            context.RecoveryRegionId is { } home
                ? context.MoveTo(
                    home,
                    context.Commitment.WithdrawLatched ? OrderKind.Withdraw : OrderKind.Transit,
                    context.Commitment.ExpiresAt)
                : null;
    }

    /// <summary>
    /// Surface and subsurface groups. Endurance is measured in weeks, so a
    /// maritime group holds station on its objective until the commitment
    /// changes or damage latches a withdrawal; it never breaks off on a clock.
    /// </summary>
    private sealed class MaritimeDomainController : OperationalDomainController
    {
        protected override OperationalIntent? Execute(OperationalControlContext context) =>
            context.Station(context.ObjectiveRegionId, context.ObjectiveOrderKind(), context.StationDeadline);
    }

    /// <summary>
    /// Fixed-wing groups fly sorties, not deployments. Every sortie is planned to
    /// end at a base: the controller breaks station early enough to fly the
    /// recovery leg before the commitment expires, and refuses to press on when
    /// the tanks can no longer pay for the trip home.
    /// </summary>
    private class AirDomainController : OperationalDomainController
    {
        protected virtual long RecoveryLeadQuanta => 40;

        protected virtual long RecoveryLeadPerHopQuanta => 40;

        protected virtual long ReserveFuelMilliUnits => 250_000;

        protected virtual long ReserveFuelPerHopMilliUnits => 50_000;

        protected override OperationalIntent? Execute(OperationalControlContext context) =>
            context.Station(context.ObjectiveRegionId, context.ObjectiveOrderKind(), context.StationDeadline);

        protected override bool MustRecover(OperationalControlContext context)
        {
            var hops = context.HopsFromHome;
            if (context.Fuel <= ReserveFuelMilliUnits + ReserveFuelPerHopMilliUnits * hops)
            {
                return true;
            }
            var lead = RecoveryLeadQuanta + RecoveryLeadPerHopQuanta * hops;
            return checked(context.DueAt.Quanta + lead) >= context.StationDeadline.Quanta;
        }
    }

    /// <summary>
    /// Rotary-wing groups fly the same sortie discipline with far less of
    /// everything, and they will not operate outside a short radius of the base
    /// that recovers them: past that they turn for home instead of pressing on.
    /// </summary>
    private sealed class RotaryWingDomainController : AirDomainController
    {
        private const int MaximumRadiusHops = 2;

        protected override long RecoveryLeadQuanta => 60;

        protected override long RecoveryLeadPerHopQuanta => 60;

        protected override long ReserveFuelMilliUnits => 300_000;

        protected override long ReserveFuelPerHopMilliUnits => 75_000;

        protected override bool MustRecover(OperationalControlContext context) =>
            base.MustRecover(context)
            || context.RecoveryRegionId is { } home
                && context.Objective is { } objective
                && context.Geography.Distance(home, objective.RegionId) > MaximumRadiusHops;
    }

    /// <summary>
    /// Land formations hold ground. They march rather than transit, they never
    /// break off on a clock, and once they stand on the objective they dig in
    /// with no order at all so the position is held and resupplied instead of
    /// being walked back and forth.
    /// </summary>
    private sealed class GroundDomainController : OperationalDomainController
    {
        protected override OperationalIntent? Assemble(OperationalControlContext context)
        {
            if (context.StagingRegionId is not { } staging)
            {
                return null;
            }
            // A formation does not march away from its objective to form up. If
            // the marshalling area is further off than the objective itself, it
            // waits where it stands until the package executes.
            return context.Geography.Distance(context.RegionId, staging)
                > context.Geography.Distance(context.RegionId, context.ObjectiveRegionId)
                ? null
                : context.MoveTo(staging, OrderKind.Reinforce, context.AssemblyDeadline);
        }

        protected override OperationalIntent? Execute(OperationalControlContext context)
        {
            var kind = context.ObjectiveOrderKind();
            // Holding is a posture, not a movement. A formation already on its
            // objective needs no order to defend it.
            return kind == OrderKind.Hold
                ? context.MoveTo(context.ObjectiveRegionId, OrderKind.Reinforce, context.StationDeadline)
                : context.MoveTo(context.ObjectiveRegionId, kind, context.StationDeadline);
        }
    }

    /// <summary>
    /// Supply groups run a route. Loaded, they run to the objective they are
    /// sustaining; delivered, they turn straight back to a depot region and hold
    /// there without an order until the base has topped them back up. A convoy
    /// parked forward is a target that carries nothing.
    /// </summary>
    private sealed class LogisticsDomainController : OperationalDomainController
    {
        private const long LoadedStoresMilliUnits = 400_000;
        private const long LoadedFuelMilliUnits = 500_000;

        protected override OperationalIntent? Execute(OperationalControlContext context)
        {
            var depot = context.RecoveryRegionId;
            if (context.Stores < LoadedStoresMilliUnits || context.Fuel < LoadedFuelMilliUnits)
            {
                return depot is { } reload
                    ? context.MoveTo(reload, OrderKind.Resupply, context.StationDeadline)
                    : null;
            }
            if (context.AllMembersIn(context.ObjectiveRegionId) && depot is { } home)
            {
                return context.MoveTo(home, OrderKind.Resupply, context.StationDeadline);
            }
            return context.MoveTo(
                context.ObjectiveRegionId,
                context.ObjectiveOrderKind(),
                context.StationDeadline);
        }
    }

    /// <summary>Region adjacency and distance for one tick.</summary>
    private sealed class OperationalGeography
    {
        private readonly ImmutableSortedDictionary<RegionId, ImmutableSortedSet<RegionId>> neighbors;
        private readonly Dictionary<RegionId, Dictionary<RegionId, int>> distances = [];

        private OperationalGeography(ImmutableSortedDictionary<RegionId, ImmutableSortedSet<RegionId>> neighbors) =>
            this.neighbors = neighbors;

        public static OperationalGeography Create(CampaignState state)
        {
            var builder = new Dictionary<RegionId, ImmutableSortedSet<RegionId>.Builder>();
            foreach (var region in state.Regions.Values)
            {
                builder[region.RegionId] = ImmutableSortedSet.CreateBuilder<RegionId>();
            }
            foreach (var region in state.Regions.Values)
            {
                foreach (var neighbor in region.Neighbors)
                {
                    if (!builder.TryGetValue(neighbor, out var reverse))
                    {
                        continue;
                    }
                    builder[region.RegionId].Add(neighbor);
                    reverse.Add(region.RegionId);
                }
            }
            return new OperationalGeography(builder.ToImmutableSortedDictionary(
                item => item.Key,
                item => item.Value.ToImmutable()));
        }

        public ImmutableSortedSet<RegionId> Neighbors(RegionId region) =>
            neighbors.TryGetValue(region, out var found) ? found : [];

        public bool IsReachable(RegionId origin, RegionId destination) =>
            Distance(origin, destination) != int.MaxValue;

        public int Distance(RegionId origin, RegionId destination)
        {
            if (origin == destination)
            {
                return 0;
            }
            if (!distances.TryGetValue(origin, out var reachable))
            {
                reachable = Explore(origin);
                distances[origin] = reachable;
            }
            return reachable.TryGetValue(destination, out var distance) ? distance : int.MaxValue;
        }

        private Dictionary<RegionId, int> Explore(RegionId origin)
        {
            var reachable = new Dictionary<RegionId, int> { [origin] = 0 };
            var queue = new Queue<RegionId>();
            queue.Enqueue(origin);
            while (queue.TryDequeue(out var current))
            {
                foreach (var neighbor in Neighbors(current))
                {
                    if (reachable.TryAdd(neighbor, reachable[current] + 1))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
            return reachable;
        }
    }
}
