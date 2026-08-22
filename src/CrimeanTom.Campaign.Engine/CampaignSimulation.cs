using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

/// <summary>
/// Deterministic Operational analogue of tactical employment: geodetic weapon range,
/// domain-aware sensors, fire doctrine, heading/telegraph, analogue loadouts,
/// task-force weapon allocation, in-flight missiles/torpedoes/bombs/decoys,
/// analogue propulsion/sensor/weapon/magazine/crew/compartment damage,
/// EMCON/ROE/alert, depth/altitude, formation station-keeping,
/// CAP/AEW/MPA orbits, sprint-drift, snorkel, baffle turns, collision
/// avoidance, and bingo/winchester withdrawal.
/// Units stay in this resolver unless a live Sea Power assignment is Active or
/// Quiescing. This is not Sea Power <c>ObjectBase</c> physics, per-sensor arcs,
/// or projectile simulation.
/// </summary>
internal static class CampaignSimulation
{
    private const long ActivityInterval = 10;
    private const long FuelPerMovement = 25_000;
    private const long AirFuelPerTick = 20_000;
    private const long StoresPerActivity = 5_000;
    private const long RepairPerActivity = 50_000;
    private const long MeanEarthRadiusMeters = 6_371_000;
    private const long MetersPerRegionHop = 160_000;
    private const long VisualContactMeters = 20_000;
    private const long StandoffMeters = 8_000;
    private const int TelegraphStop = 0;
    private const int TelegraphCruise = 2;
    private const int TelegraphIntercept = 3;
    private const int TelegraphFlank = 4;

    public static CampaignSimulatedEvent Resolve(CampaignState state, CampaignTime target, int workerCount)
    {
        if (workerCount is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker count must be between 1 and 64.");
        }

        var world = state;
        var activities = state.ScheduledActivities.Count == 0
            ? CreateActivities(state, new CampaignTime(checked(state.Time.Quanta + ActivityInterval)))
            : state.ScheduledActivities;
        var outcomes = new List<OperationalOutcome>();
        while (activities.Count > 0)
        {
            var dueAt = activities.Values.Min(item => item.DueAt);
            if (dueAt.Quanta > target.Quanta)
            {
                break;
            }

            var tick = ResolveTick(world with { Time = dueAt }, dueAt, workerCount);
            world = tick.State;
            outcomes.AddRange(tick.Outcomes);
            activities = CreateActivities(world, new CampaignTime(checked(dueAt.Quanta + ActivityInterval)));
        }

        return new CampaignSimulatedEvent(
            target,
            world.Entities.Values.Select(ToUpdate).ToArray(),
            world.Bases.Values.Select(ToUpdate).ToArray(),
            world.Groups.Values.Select(ToUpdate).ToArray(),
            world.Orders.Values.Select(ToUpdate).ToArray(),
            world.Intelligence.Values.Select(ToUpdate).ToArray(),
            outcomes,
            activities.Values.Select(ToContract).ToArray(),
            world.InFlight.ToArray());
    }

    private static TickResult ResolveTick(CampaignState state, CampaignTime dueAt, int workerCount)
    {
        var withIntent = ResolveAiIntent(state, dueAt);
        var movementResults = new ConcurrentBag<GroupMovementResult>();
        Parallel.ForEach(
            withIntent.Groups.Values,
            new ParallelOptions { MaxDegreeOfParallelism = workerCount },
            group => movementResults.Add(ResolveMovement(withIntent, group, dueAt)));

        var entities = withIntent.Entities;
        var groups = withIntent.Groups;
        var orders = withIntent.Orders;
        foreach (var result in movementResults.OrderBy(item => item.GroupId))
        {
            foreach (var entity in result.Entities.OrderBy(item => item.EntityId))
            {
                entities = entities.SetItem(entity.EntityId, entity);
            }
            groups = groups.SetItem(result.Group.GroupId, result.Group);
            if (result.Order is { } order)
            {
                orders = orders.SetItem(order.OrderId, order);
            }
        }

        var movedState = withIntent with { Entities = entities, Groups = groups, Orders = orders };
        movedState = ApplyCollisionAvoidance(movedState);
        var sustained = ResolveSustainmentAndAttrition(movedState, dueAt);
        var engagement = ResolveOperationalEngagements(sustained, dueAt);
        var ordnance = ResolveInFlightOrdnance(engagement.State, dueAt, engagement.Outcomes);
        var intelligence = ResolveIntelligence(ordnance.State, dueAt, ordnance.Outcomes);
        return new TickResult(intelligence.State, [.. ordnance.Outcomes, .. intelligence.Outcomes]);
    }

    private static CampaignState ResolveAiIntent(CampaignState state, CampaignTime dueAt)
    {
        var groups = state.Groups;
        var orders = state.Orders;
        foreach (var group in state.Groups.Values)
        {
            if (group.ActiveOrderId is not null)
            {
                continue;
            }

            var activeMembers = group.Members
                .Select(id => state.Entities[id])
                .Where(item => IsOperational(state, item))
                .ToArray();
            if (activeMembers.Length == 0)
            {
                continue;
            }

            var first = activeMembers[0];
            var requiresRecovery = ShouldWithdraw(activeMembers)
                || activeMembers.Max(item => item.DamageMilliUnits) >= 400_000;
            var homeRegions = activeMembers
                .Select(item => item.HomeBaseId)
                .Where(item => item is not null)
                .Select(item => state.Bases[item!.Value].RegionId)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();
            if (requiresRecovery && homeRegions.Contains(first.RegionId))
            {
                // Leave the group idle so sustainment can repair and replenish it this tick.
                continue;
            }
            var intercepts = state.Intelligence.Values
                .Where(item => item.Key.ObserverCoalitionId == group.CoalitionId
                    && item.Confidence >= IntelligenceConfidence.Probable
                    && item.EstimatedRegionId != first.RegionId)
                .Select(item => item.EstimatedRegionId)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();
            var neighbors = Neighbors(state, first.RegionId);
            var home = homeRegions.FirstOrDefault(item => item != first.RegionId);
            var safeRecoveryNeighbors = neighbors
                .Where(candidate => !state.Entities.Values.Any(item => item.RegionId == candidate
                    && item.CoalitionId != group.CoalitionId && CanBeAttacked(state, item)))
                .ToArray();
            if ((requiresRecovery && home == default && safeRecoveryNeighbors.Length == 0)
                || (!requiresRecovery && neighbors.Count == 0 && intercepts.Length == 0))
            {
                continue;
            }

            var destination = requiresRecovery
                ? home != default
                    ? home
                    : safeRecoveryNeighbors[
                        KeyedValue(state.Metadata.Seed, group.GroupId.Value, dueAt, "ai-recovery")
                        % safeRecoveryNeighbors.Length]
                : !requiresRecovery && intercepts.Length > 0
                    ? intercepts[KeyedValue(state.Metadata.Seed, group.GroupId.Value, dueAt, "ai-intercept") % intercepts.Length]
                    : neighbors.Count > 0
                        ? neighbors[KeyedValue(state.Metadata.Seed, group.GroupId.Value, dueAt, "ai-destination") % neighbors.Count]
                        : intercepts[0];
            var kind = requiresRecovery ? OrderKind.Withdraw
                : intercepts.Length > 0 ? OrderKind.Intercept
                : first.SimulationDomain == SimulationDomain.Air ? OrderKind.Cap
                : Classify(first) == ContactClassification.Subsurface ? OrderKind.Asw
                : OrderKind.Patrol;
            var orderId = new OrderId(KeyedGuid(state.Metadata.Seed, group.GroupId.Value, dueAt, "ai-order"));
            var order = new OrderState(
                orderId,
                group.GroupId,
                kind,
                destination,
                dueAt,
                new CampaignTime(checked(dueAt.Quanta + 30)),
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

    private static GroupMovementResult ResolveMovement(CampaignState state, GroupState group, CampaignTime dueAt)
    {
        if (group.ActiveOrderId is not { } orderId
            || !state.Orders.TryGetValue(orderId, out var order)
            || order.StartsAt.Quanta > dueAt.Quanta
            || order.CompletedAt is not null)
        {
            var stopped = group.Members.Select(id =>
            {
                var entity = state.Entities[id];
                if (!IsOperational(state, entity) || entity.Telegraph == TelegraphStop)
                {
                    return entity;
                }
                return entity with { Telegraph = TelegraphStop, Revision = entity.Revision.Next() };
            }).ToArray();
            return new GroupMovementResult(group.GroupId, group, null, stopped);
        }

        var activeMembers = group.Members
            .Select(id => state.Entities[id])
            .Where(item => IsOperational(state, item))
            .ToArray();
        if (activeMembers.Length == 0)
        {
            return new GroupMovementResult(
                group.GroupId,
                group with { ActiveOrderId = null, Revision = group.Revision.Next() },
                order with { CompletedAt = dueAt, Revision = order.Revision.Next() },
                []);
        }

        var destination = RegionAnchor(state, order.DestinationRegionId, activeMembers[0].CoalitionId, activeMembers[0].Coordinate);
        var entityUpdates = group.Members.Select(id =>
        {
            var entity = state.Entities[id];
            if (!IsOperational(state, entity))
            {
                return entity;
            }
            var next = NextRegion(state, entity.RegionId, order.DestinationRegionId);
            var inbound = state.InFlight
                .Where(item => item.TargetEntityId == entity.EntityId)
                .OrderBy(item => item.OrdnanceId, StringComparer.Ordinal)
                .ToArray();
            var inboundTorpedoes = inbound
                .Where(item => item.Kind == OrdnanceKind.Torpedo
                    || ContainsToken(item.AmmunitionId, "torpedo"))
                .ToArray();
            var inboundTorpedo = inboundTorpedoes.Length > 0;
            var inboundMissile = inbound.Any(item => item.Kind is OrdnanceKind.Missile or OrdnanceKind.Bomb);
            var effectiveNext = inboundTorpedo ? entity.RegionId : next;
            var moved = effectiveNext != entity.RegionId;
            var remaining = RoundedGreatCircleDistanceMeters(entity.Coordinate, destination);
            var hostileAhead = HasHostileInRegion(state, next, entity.CoalitionId);
            var desired = hostileAhead && remaining > StandoffMeters
                ? remaining - StandoffMeters
                : hostileAhead && remaining <= StandoffMeters ? 0 : remaining;
            var telegraph = inboundTorpedo || order.Kind == OrderKind.Withdraw ? TelegraphFlank
                : inboundMissile || order.Kind == OrderKind.Intercept ? TelegraphIntercept
                : TelegraphCruise;
            if ((order.Kind is OrderKind.Patrol or OrderKind.Asw)
                && Classify(entity) == ContactClassification.Subsurface)
            {
                telegraph = KeyedValue(state.Metadata.Seed, entity.EntityId.Value, dueAt, "sprint-drift") % 1_000 < 400
                    ? TelegraphStop
                    : TelegraphFlank;
            }
            var hopLimit = HopMeters(entity, telegraph)
                * Math.Max(100, entity.Analogue.PropulsionIntegrityMilliUnits) / 1_000;
            var hop = Math.Min(hopLimit, inboundTorpedo ? hopLimit : desired);
            var resources = entity.Resources;
            if (hop > 0)
            {
                resources = Consume(resources, ResourceKind.Fuel, FuelPerMovement);
            }
            if (entity.SimulationDomain == SimulationDomain.Air)
            {
                resources = Consume(resources, ResourceKind.Fuel, AirFuelPerTick);
            }
            resources = Consume(resources, ResourceKind.Stores, StoresPerActivity);
            var bearingMilli = BearingMilliDegrees(entity.Coordinate, destination);
            if (inboundTorpedo)
            {
                bearingMilli = BearingMilliDegrees(entity.Coordinate, inboundTorpedoes[0].Coordinate);
                if (bearingMilli >= 0)
                {
                    bearingMilli = (bearingMilli + 180_000) % 360_000;
                }
            }
            var coordinate = hop > 0 && bearingMilli >= 0
                ? DisplaceAlongBearing(entity.Coordinate, bearingMilli, hop)
                : entity.Coordinate;
            var heading = bearingMilli >= 0
                ? HeadingMilliDegrees.FromMilliDegrees(bearingMilli)
                : entity.Heading;
            if (entity.SimulationDomain == SimulationDomain.Air
                && order.Kind is OrderKind.Patrol or OrderKind.Cap)
            {
                var turn = order.Kind == OrderKind.Cap ? 70_000 : 45_000;
                heading = HeadingMilliDegrees.FromMilliDegrees((heading.Value + turn) % 360_000);
                if (hop == 0)
                {
                    coordinate = DisplaceAlongBearing(
                        coordinate, heading.Value, order.Kind == OrderKind.Cap ? 18_000 : 12_000);
                }
            }
            else if (Classify(entity) == ContactClassification.Subsurface
                && order.Kind is OrderKind.Patrol or OrderKind.Asw
                && KeyedValue(state.Metadata.Seed, entity.EntityId.Value, dueAt, "baffles") % 1_000 < 200)
            {
                heading = HeadingMilliDegrees.FromMilliDegrees((heading.Value + 60_000) % 360_000);
            }
            var snorkel = Classify(entity) == ContactClassification.Subsurface
                ? (entity.Analogue.SnorkelCycle + 1) % 10
                : 0;
            var snorkeling = Classify(entity) == ContactClassification.Subsurface && snorkel == 0;
            var analogue = entity.Analogue with
            {
                AltitudeOrDepthMeters = entity.SimulationDomain == SimulationDomain.Air
                    ? (order.Kind == OrderKind.Cap ? 9_000 : order.Kind == OrderKind.Patrol ? 8_000 : 5_500)
                    : Classify(entity) == ContactClassification.Subsurface
                        ? (snorkeling ? 12 : telegraph == TelegraphFlank ? 50 : 180)
                        : 0,
                Emcon = Classify(entity) == ContactClassification.Subsurface
                    ? (snorkeling ? EmconState.Radiate : EmconState.Passive)
                    : inboundMissile ? EmconState.Radiate : entity.Analogue.Emcon,
                Alert = inbound.Length > 0 ? AlertState.General
                    : hostileAhead ? AlertState.Warning : AlertState.Routine,
                EcmMilliUnits = inboundMissile
                    ? Math.Min(1_000, entity.Analogue.EcmMilliUnits + 250)
                    : Math.Max(0, entity.Analogue.EcmMilliUnits - 80),
                SnorkelCycle = snorkel,
            };
            var changed = moved
                || hop > 0
                || heading != entity.Heading
                || telegraph != entity.Telegraph
                || analogue != entity.Analogue
                || !resources.SequenceEqual(entity.Resources);
            return changed
                ? entity with
                {
                    RegionId = effectiveNext,
                    Coordinate = coordinate,
                    Heading = heading,
                    Telegraph = telegraph,
                    Resources = resources,
                    Analogue = analogue,
                    DamageMilliUnits = hop > 0 ? Math.Min(1_000_000, entity.DamageMilliUnits + 10_000) : entity.DamageMilliUnits,
                    Revision = entity.Revision.Next(),
                }
                : entity;
        }).ToArray();
        entityUpdates = ApplyFormationStations(state, entityUpdates);

        var arrived = activeMembers.All(entity =>
            {
                if (state.InFlight.Any(item => item.TargetEntityId == entity.EntityId
                    && (item.Kind == OrdnanceKind.Torpedo
                        || ContainsToken(item.AmmunitionId, "torpedo"))))
                {
                    return false;
                }
                var next = NextRegion(state, entity.RegionId, order.DestinationRegionId);
                if (next != order.DestinationRegionId)
                {
                    return false;
                }
                var remaining = RoundedGreatCircleDistanceMeters(entity.Coordinate, destination);
                var hostileAhead = HasHostileInRegion(state, next, entity.CoalitionId);
                var desired = hostileAhead && remaining > StandoffMeters
                    ? remaining - StandoffMeters
                    : hostileAhead && remaining <= StandoffMeters ? 0 : remaining;
                var movementTelegraph = order.Kind == OrderKind.Withdraw ? TelegraphFlank
                    : order.Kind == OrderKind.Intercept ? TelegraphIntercept
                    : TelegraphCruise;
                var hopLimit = HopMeters(entity, movementTelegraph)
                    * Math.Max(100, entity.Analogue.PropulsionIntegrityMilliUnits) / 1_000;
                return desired <= hopLimit;
            });
        if (!arrived)
        {
            return new GroupMovementResult(group.GroupId, group, order, entityUpdates);
        }

        var completed = order with { CompletedAt = dueAt, Revision = order.Revision.Next() };
        var released = group with { ActiveOrderId = null, Revision = group.Revision.Next() };
        return new GroupMovementResult(group.GroupId, released, completed, entityUpdates);
    }

    private static EntityState[] ApplyFormationStations(CampaignState state, EntityState[] members)
    {
        var lead = members.FirstOrDefault(item => IsOperational(state, item));
        if (lead is null)
        {
            return members;
        }

        var station = 0;
        return members.Select(entity =>
        {
            if (entity.EntityId == lead.EntityId
                || !IsOperational(state, entity)
                || entity.RegionId != lead.RegionId
                || entity.Coordinate == state.Entities[entity.EntityId].Coordinate
                || RoundedGreatCircleDistanceMeters(entity.Coordinate, lead.Coordinate) > 10_000)
            {
                return entity;
            }

            station++;
            var offsetHeading = (lead.Heading.Value + 90_000) % 360_000;
            var coordinate = DisplaceAlongBearing(lead.Coordinate, offsetHeading, 450L * station);
            if (coordinate == entity.Coordinate)
            {
                return entity;
            }

            var original = state.Entities[entity.EntityId];
            return entity with
            {
                Coordinate = coordinate,
                Revision = entity.Revision.Value == original.Revision.Value
                    ? entity.Revision.Next()
                    : entity.Revision,
            };
        }).ToArray();
    }

    private static CampaignState ApplyCollisionAvoidance(CampaignState state)
    {
        var entities = state.Entities;
        var ordered = entities.Values
            .Where(item => IsOperational(state, item))
            .OrderBy(item => item.EntityId)
            .ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var entity = entities[ordered[index].EntityId];
            for (var prior = 0; prior < index; prior++)
            {
                var other = entities[ordered[prior].EntityId];
                var distance = RoundedGreatCircleDistanceMeters(entity.Coordinate, other.Coordinate);
                if (distance == 0 || distance >= 400)
                {
                    continue;
                }

                var heading = (entity.Heading.Value + 90_000) % 360_000;
                var coordinate = DisplaceAlongBearing(entity.Coordinate, heading, 180);
                if (coordinate != entity.Coordinate)
                {
                    entity = entity with { Coordinate = coordinate, Revision = entity.Revision.Next() };
                    entities = entities.SetItem(entity.EntityId, entity);
                }
                break;
            }
        }
        return state with { Entities = entities };
    }

    private static CampaignState ResolveSustainmentAndAttrition(CampaignState state, CampaignTime dueAt)
    {
        var bases = state.Bases;
        var entities = state.Entities;
        foreach (var original in state.Entities.Values)
        {
            var entity = entities[original.EntityId];
            if (entity.Lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Retired)
            {
                continue;
            }
            if (entity.Fidelity == FidelityState.Suspended || HasActiveTacticalAuthority(state, entity.EntityId))
            {
                continue;
            }
            var resources = entity.Resources;
            var damage = entity.DamageMilliUnits;
            var lifecycle = entity.Lifecycle;
            var analogue = entity.Analogue;
            var baseChanged = false;
            BaseState? home = null;
            if (entity.HomeBaseId is { } homeBaseId
                && bases.TryGetValue(homeBaseId, out var found)
                && found.RegionId == entity.RegionId
                && !IsActivelyOrdered(state, entity.EntityId))
            {
                home = found;
                (resources, home, baseChanged) = Replenish(resources, home);
                var available = Get(home.Resources, ResourceKind.SpareParts);
                var repairBudget = Math.Min(available, RepairPerActivity);
                var hullRepair = Math.Min(repairBudget, damage);
                if (hullRepair > 0)
                {
                    damage -= checked((int)hullRepair);
                }
                var conditionBudget = repairBudget - hullRepair;
                var conditionNeed = new long[]
                {
                    (long)analogue.FireMilliUnits * 2,
                    (long)analogue.FloodMilliUnits * 3,
                    (long)(1_000 - analogue.PropulsionIntegrityMilliUnits) * 1_250,
                    (long)(1_000 - analogue.SensorIntegrityMilliUnits) * 1_667,
                    (long)(1_000 - analogue.WeaponIntegrityMilliUnits) * 1_667,
                    (long)(1_000 - analogue.MagazineIntegrityMilliUnits) * 2_500,
                    (long)(1_000 - analogue.FlightDeckIntegrityMilliUnits) * 2_500,
                    (long)(1_000 - analogue.CrewMilliUnits) * 2_000,
                    (long)(1_000 - analogue.PortIntegrityMilliUnits) * 2_500,
                    (long)(1_000 - analogue.StarboardIntegrityMilliUnits) * 2_500,
                    (long)(1_000 - analogue.CountermeasureMilliUnits) * 1_000,
                }.Max();
                var conditionRepair = Math.Min(conditionBudget, conditionNeed);
                if (conditionRepair > 0)
                {
                    analogue = analogue with
                    {
                        FireMilliUnits = Math.Max(0, analogue.FireMilliUnits - checked((int)(conditionRepair / 2))),
                        FloodMilliUnits = Math.Max(0, analogue.FloodMilliUnits - checked((int)(conditionRepair / 3))),
                        PropulsionIntegrityMilliUnits = Math.Min(1_000,
                            analogue.PropulsionIntegrityMilliUnits + checked((int)(conditionRepair / 1_250))),
                        SensorIntegrityMilliUnits = Math.Min(1_000,
                            analogue.SensorIntegrityMilliUnits + checked((int)(conditionRepair / 1_667))),
                        WeaponIntegrityMilliUnits = Math.Min(1_000,
                            analogue.WeaponIntegrityMilliUnits + checked((int)(conditionRepair / 1_667))),
                        MagazineIntegrityMilliUnits = Math.Min(1_000,
                            analogue.MagazineIntegrityMilliUnits + checked((int)(conditionRepair / 2_500))),
                        FlightDeckIntegrityMilliUnits = Math.Min(1_000,
                            analogue.FlightDeckIntegrityMilliUnits + checked((int)(conditionRepair / 2_500))),
                        CrewMilliUnits = Math.Min(1_000,
                            analogue.CrewMilliUnits + checked((int)(conditionRepair / 2_000))),
                        PortIntegrityMilliUnits = Math.Min(1_000,
                            analogue.PortIntegrityMilliUnits + checked((int)(conditionRepair / 2_500))),
                        StarboardIntegrityMilliUnits = Math.Min(1_000,
                            analogue.StarboardIntegrityMilliUnits + checked((int)(conditionRepair / 2_500))),
                        CountermeasureMilliUnits = Math.Min(1_000,
                            analogue.CountermeasureMilliUnits + checked((int)(conditionRepair / 1_000))),
                    };
                }
                var spent = hullRepair + conditionRepair;
                if (spent > 0)
                {
                    home = home with
                    {
                        Resources = Set(home.Resources, ResourceKind.SpareParts, available - spent),
                    };
                    baseChanged = true;
                }
            }

            var shortageDamage = 0;
            if (Get(resources, ResourceKind.Fuel) == 0)
            {
                shortageDamage += 100_000;
            }
            if (Get(resources, ResourceKind.Stores) == 0)
            {
                shortageDamage += 50_000;
            }
            var randomDamage = KeyedValue(state.Metadata.Seed, entity.EntityId.Value, dueAt, "attrition") % 1_000 < 25
                ? 25_000
                : 0;
            var flooding = damage >= 200_000
                && damage < 800_000
                && home is null
                && lifecycle is not (EntityLifecycle.Disabled or EntityLifecycle.Destroyed)
                ? 8_000
                : 0;
            damage = Math.Min(1_000_000, checked(damage + shortageDamage + randomDamage + flooding));
            if (home is not null)
            {
                analogue = analogue with
                {
                    WeaponReadyMilliUnits = 1_000,
                    EcmMilliUnits = 0,
                };
            }
            else if (home is null)
            {
                var weaponReadyRecovery = lifecycle == EntityLifecycle.Disabled ? 150 : 350;
                analogue = analogue with
                {
                    FireMilliUnits = analogue.FireMilliUnits == 0
                        ? 0
                        : Math.Min(1_000_000, analogue.FireMilliUnits + 6_000 - analogue.CrewMilliUnits / 20),
                    FloodMilliUnits = analogue.FloodMilliUnits == 0
                        ? 0
                        : Math.Min(1_000_000, analogue.FloodMilliUnits + 4_000),
                    WeaponReadyMilliUnits = Math.Min(1_000,
                        analogue.WeaponReadyMilliUnits + weaponReadyRecovery),
                    EcmMilliUnits = Math.Max(0, analogue.EcmMilliUnits - 40),
                };
                analogue = analogue with
                {
                    FireMilliUnits = Math.Clamp(analogue.FireMilliUnits, 0, 1_000_000),
                };
                damage = Math.Min(1_000_000,
                    checked(damage + analogue.FireMilliUnits / 80 + analogue.FloodMilliUnits / 100));
                if (analogue.MagazineIntegrityMilliUnits < 500
                    && analogue.FireMilliUnits >= 80_000
                    && KeyedValue(state.Metadata.Seed, entity.EntityId.Value, dueAt, "secondary") % 1_000 < 40)
                {
                    damage = Math.Min(1_000_000, damage + 35_000);
                    analogue = analogue with
                    {
                        MagazineIntegrityMilliUnits = Math.Max(0, analogue.MagazineIntegrityMilliUnits - 80),
                    };
                }
            }
            if (damage >= 1_000_000)
            {
                lifecycle = EntityLifecycle.Destroyed;
            }
            else if (damage >= 800_000)
            {
                lifecycle = EntityLifecycle.Disabled;
            }
            else if (lifecycle == EntityLifecycle.Disabled && home is not null)
            {
                lifecycle = EntityLifecycle.Available;
            }

            var readiness = Math.Clamp(
                1_000 - damage / 1_000
                - (Get(resources, ResourceKind.Fuel) == 0 ? 300 : 0)
                - (Get(resources, ResourceKind.Stores) == 0 ? 200 : 0)
                - (1_000 - analogue.PropulsionIntegrityMilliUnits) / 4,
                0,
                1_000);
            var telegraph = lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Disabled
                ? TelegraphStop
                : entity.Telegraph;
            var changed = damage != entity.DamageMilliUnits
                || readiness != entity.ReadinessMilliUnits
                || lifecycle != entity.Lifecycle
                || telegraph != entity.Telegraph
                || analogue != entity.Analogue
                || !resources.SequenceEqual(entity.Resources);
            if (changed)
            {
                entities = entities.SetItem(entity.EntityId, entity with
                {
                    Resources = resources,
                    DamageMilliUnits = damage,
                    ReadinessMilliUnits = readiness,
                    Lifecycle = lifecycle,
                    Telegraph = telegraph,
                    Analogue = analogue,
                    Revision = entity.Revision.Next(),
                });
            }
            if (home is not null && baseChanged)
            {
                bases = bases.SetItem(home.BaseId, home with { Revision = home.Revision.Next() });
            }
        }
        return state with { Bases = bases, Entities = entities };
    }

    private static TickResult ResolveOperationalEngagements(CampaignState state, CampaignTime dueAt)
    {
        var entities = state.Entities;
        var groups = state.Groups;
        var orders = state.Orders;
        var inflight = state.InFlight.ToList();
        var outcomes = new List<OperationalOutcome>();

        var liveState = state with { Entities = entities };
        var coveredTargets = state.InFlight.Select(item => item.TargetEntityId).ToHashSet();
        var guidanceLoad = state.InFlight.GroupBy(item => item.ShooterEntityId)
            .ToDictionary(item => item.Key, item => item.Count());
        var bids = new List<(
            EntityState Actor,
            EntityState Entity,
            OperationalWeaponCapability Weapon,
            long ActualDistanceMeters,
            long TargetingDistanceMeters,
            GeoCoordinate TargetingCoordinate,
            int Priority)>();
        foreach (var originalActor in entities.Values.OrderBy(item => item.EntityId))
        {
            var actor = entities[originalActor.EntityId];
            if (!CanAttack(liveState, actor))
            {
                continue;
            }

            var selected = entities.Values
                .Where(item => item.CoalitionId != actor.CoalitionId
                    && CanBeAttacked(liveState, item)
                    && !coveredTargets.Contains(item.EntityId))
                .Select(item => SelectOperationalWeapon(liveState, actor, item))
                .Where(item => item is not null)
                .Select(item => item!.Value)
                .Where(item => HasFireSolution(liveState, actor, item.Entity, item.ActualDistanceMeters))
                .Select(item => (
                    Actor: actor,
                    item.Entity,
                    item.Weapon,
                    item.ActualDistanceMeters,
                    item.TargetingDistanceMeters,
                    item.TargetingCoordinate,
                    Priority: TargetPriority(
                        liveState, actor, item.Entity, item.TargetingDistanceMeters, item.Weapon)))
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Entity.EntityId)
                .FirstOrDefault();
            if (selected.Entity is not null)
            {
                bids.Add(selected);
            }
        }

        var claimedActors = new HashSet<EntityId>();
        var claimedTargets = new HashSet<EntityId>();
        foreach (var bid in bids
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Actor.EntityId)
            .ThenBy(item => item.Entity.EntityId))
        {
            if (claimedActors.Contains(bid.Actor.EntityId) || claimedTargets.Contains(bid.Entity.EntityId))
            {
                continue;
            }

            var actor = entities[bid.Actor.EntityId];
            var target = entities[bid.Entity.EntityId];
            if (!CanAttack(state with { Entities = entities }, actor) || !CanBeAttacked(state with { Entities = entities }, target))
            {
                claimedActors.Remove(actor.EntityId);
                claimedTargets.Remove(target.EntityId);
                continue;
            }

            claimedActors.Add(actor.EntityId);
            claimedTargets.Add(target.EntityId);
            if (!IsBallisticGun(bid.Weapon)
                && guidanceLoad.GetValueOrDefault(actor.EntityId) >= 2)
            {
                claimedActors.Remove(actor.EntityId);
                claimedTargets.Remove(target.EntityId);
                continue;
            }

            var available = Get(actor.Resources, ResourceKind.Munitions);
            var expenditure = Math.Min(available, WeaponExpenditure(bid.Weapon, actor.SimulationDomain));
            if (expenditure <= 0)
            {
                claimedActors.Remove(actor.EntityId);
                claimedTargets.Remove(target.EntityId);
                continue;
            }

            var interceptHeading = HeadingToward(actor.Coordinate, bid.TargetingCoordinate, actor.Heading);
            actor = actor with
            {
                Resources = Set(actor.Resources, ResourceKind.Munitions, available - expenditure),
                Heading = interceptHeading,
                Telegraph = TelegraphIntercept,
                Analogue = actor.Analogue with
                {
                    Alert = AlertState.General,
                    WeaponReadyMilliUnits = IsBallisticGun(bid.Weapon) ? actor.Analogue.WeaponReadyMilliUnits : 0,
                },
                Revision = actor.Revision.Next(),
            };
            entities = entities.SetItem(actor.EntityId, actor);
            outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.WeaponExpenditure, actor.RegionId,
                actor.EntityId, target.EntityId, expenditure, 0,
                $"{bid.Weapon.AmmunitionId} employment at {bid.ActualDistanceMeters} m "
                + $"(catalog max {bid.Weapon.MaximumRange.Value} m)"));

            if (!IsBallisticGun(bid.Weapon))
            {
                var headingMilli = interceptHeading.Value;
                inflight.Add(new InFlightOrdnance(
                    KeyedGuid(state.Metadata.Seed, actor.EntityId.Value, dueAt, $"ordnance-{target.EntityId}").ToString("N"),
                    actor.EntityId,
                    target.EntityId,
                    actor.CoalitionId,
                    bid.Weapon.AmmunitionId,
                    bid.Weapon.TargetClassification,
                    actor.Coordinate,
                    headingMilli,
                    OrdnanceFlightStage.Launch,
                    OrdnanceGuidanceFor(bid.Weapon),
                    bid.TargetingDistanceMeters,
                    OrdnanceSpeedMetersPerTick(bid.Weapon),
                    KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt, $"ins-{target.EntityId}") % 80,
                    dueAt,
                    OrdnanceKindFor(bid.Weapon),
                    OrdnanceGuidanceFor(bid.Weapon) is OrdnanceGuidance.SemiActive or OrdnanceGuidance.Command ? 1 : 0,
                    0));
                guidanceLoad[actor.EntityId] = guidanceLoad.GetValueOrDefault(actor.EntityId) + 1;
                coveredTargets.Add(target.EntityId);
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Launch, actor.RegionId,
                    actor.EntityId, target.EntityId, 0, 0,
                    $"{bid.Weapon.AmmunitionId} launched, {bid.TargetingDistanceMeters} m to go"));
                continue;
            }

            var hit = ResolveTerminalHit(
                state, dueAt, actor, target, bid.Weapon, bid.ActualDistanceMeters, actor.Coordinate);
            outcomes.AddRange(hit.Outcomes);
            if (hit.Target is { } updatedTarget)
            {
                entities = entities.SetItem(updatedTarget.EntityId, updatedTarget);
            }
        }

        var combatState = state with
        {
            Entities = entities,
            Groups = groups,
            Orders = orders,
            InFlight = [.. inflight.OrderBy(item => item.OrdnanceId, StringComparer.Ordinal)],
        };
        foreach (var group in groups.Values.OrderBy(item => item.GroupId))
        {
            var activeMembers = group.Members.Select(id => entities[id])
                .Where(item => IsOperational(combatState, item))
                .ToArray();
            if (activeMembers.Length == 0
                || !ShouldWithdraw(activeMembers)
                || IsAlreadyWithdrawing(group, orders))
            {
                continue;
            }

            var origin = activeMembers[0].RegionId;
            var destination = activeMembers.Select(item => item.HomeBaseId)
                .Where(item => item is not null)
                .Select(item => state.Bases[item!.Value].RegionId)
                .FirstOrDefault();
            if (destination == origin)
            {
                orders = CompleteActiveOrder(group, orders, dueAt);
                groups = groups.SetItem(group.GroupId, group with
                {
                    ActiveOrderId = null,
                    Revision = group.Revision.Next(),
                });
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Withdrawal, origin,
                    activeMembers.OrderBy(item => item.EntityId).First().EntityId, null, 0, 0,
                    $"{WithdrawalReason(activeMembers)}; holding at home base"));
                continue;
            }
            if (destination == default)
            {
                destination = Neighbors(state, origin)
                    .FirstOrDefault(candidate => !entities.Values.Any(item => item.RegionId == candidate
                        && item.CoalitionId != group.CoalitionId && CanBeAttacked(state, item)));
            }
            if (destination == default || destination == origin)
            {
                continue;
            }

            var orderId = new OrderId(KeyedGuid(state.Metadata.Seed, group.GroupId.Value, dueAt, "withdrawal-order"));
            orders = CompleteActiveOrder(group, orders, dueAt);
            orders = orders.SetItem(orderId, new OrderState(orderId, group.GroupId, OrderKind.Withdraw, destination,
                dueAt, new CampaignTime(checked(dueAt.Quanta + 60)), null, new EntityRevision(1)));
            groups = groups.SetItem(group.GroupId, group with { ActiveOrderId = orderId, Revision = group.Revision.Next() });
            var reason = WithdrawalReason(activeMembers);
            outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Withdrawal, origin,
                activeMembers.OrderBy(item => item.EntityId).First().EntityId, null, 0, 0, reason));
        }

        return new TickResult(combatState with { Groups = groups, Orders = orders }, outcomes);
    }

    private static TickResult ResolveIntelligence(
        CampaignState state,
        CampaignTime dueAt,
        IReadOnlyList<OperationalOutcome> engagementOutcomes)
    {
        var intelligence = state.Intelligence;
        var outcomes = new List<OperationalOutcome>();
        var coalitions = state.Entities.Values.Select(item => item.CoalitionId)
            .Concat(state.Bases.Values.Select(item => item.CoalitionId))
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
        foreach (var observer in coalitions)
        {
            var sensors = state.Entities.Values
                .Where(item => item.CoalitionId == observer && CanSense(state, item))
                .ToArray();
            foreach (var contact in state.Entities.Values.Where(item => item.CoalitionId != observer))
            {
                var key = new IntelligenceKey(observer, contact.EntityId);
                var scoredSensors = CanBeAttacked(state, contact)
                    ? sensors.Select(sensor => (Sensor: sensor, Score: DetectionScore(state, sensor, contact)))
                        .Where(item => item.Score > 0)
                        .ToArray()
                    : [];
                var score = scoredSensors.Select(item => item.Score).DefaultIfEmpty(0).Max();
                var combatObserved = engagementOutcomes.Any(item =>
                    (item.ActorEntityId == contact.EntityId || item.TargetEntityId == contact.EntityId)
                    && CoalitionParticipated(state, observer, item));
                if (combatObserved)
                {
                    score = Math.Max(score, 950);
                }
                var detected = combatObserved || score > 0
                    && KeyedValue(state.Metadata.Seed, contact.EntityId.Value, dueAt, $"detection-{observer}") % 1_000 < score;
                if (!detected)
                {
                    if (intelligence.TryGetValue(key, out var stale))
                    {
                        var age = dueAt.Quanta - stale.ObservedAt.Quanta;
                        var staleConfidence = age >= 60 ? IntelligenceConfidence.Unknown
                            : age >= 30 && stale.Confidence > IntelligenceConfidence.Possible
                                ? IntelligenceConfidence.Possible : stale.Confidence;
                        var staleRadius = Math.Min(2_000_000, checked(stale.UncertaintyRadiusMeters + 5_000));
                        if (staleConfidence != stale.Confidence || staleRadius != stale.UncertaintyRadiusMeters)
                        {
                            intelligence = intelligence.SetItem(key, stale with
                            {
                                Confidence = staleConfidence,
                                UncertaintyRadiusMeters = staleRadius,
                                Revision = stale.Revision.Next(),
                            });
                        }
                    }
                    continue;
                }

                var confidence = score >= 900 ? IntelligenceConfidence.Confirmed
                    : score >= 600 ? IntelligenceConfidence.Probable : IntelligenceConfidence.Possible;
                var radius = confidence switch
                {
                    IntelligenceConfidence.Confirmed => 2_000,
                    IntelligenceConfidence.Probable => 25_000,
                    _ => 100_000,
                };
                var jitterLimit = checked(radius * 9);
                var latitudeJitter = KeyedValue(state.Metadata.Seed, contact.EntityId.Value, dueAt, $"intel-lat-{observer}") % (jitterLimit * 2 + 1) - jitterLimit;
                var longitudeJitter = KeyedValue(state.Metadata.Seed, contact.EntityId.Value, dueAt, $"intel-lon-{observer}") % (jitterLimit * 2 + 1) - jitterLimit;
                var coordinate = GeoCoordinate.FromMicrodegrees(
                    Math.Clamp(contact.Coordinate.LatitudeMicrodegrees + latitudeJitter, -90_000_000, 90_000_000),
                    NormalizeLongitudeMicrodegrees((long)contact.Coordinate.LongitudeMicrodegrees + longitudeJitter));
                var classification = ClassifyObserved(
                    contact,
                    confidence,
                    scoredSensors.Select(item => item.Sensor).ToArray(),
                    score);
                var reportingSensor = scoredSensors
                    .OrderByDescending(item => item.Score)
                    .ThenBy(item => item.Sensor.EntityId)
                    .Select(item => item.Sensor)
                    .FirstOrDefault()
                    ?? ParticipatingObserver(state, observer, engagementOutcomes, contact.EntityId);
                var revision = intelligence.TryGetValue(key, out var current)
                    ? current.Revision.Next() : new EntityRevision(1);
                var quality = score >= 900 ? TrackQuality.Identified
                    : score >= 600 ? TrackQuality.Classified : TrackQuality.Detected;
                var observedHeading = quality == TrackQuality.Identified ? contact.Heading.Value : 0;
                var observedSpeed = quality == TrackQuality.Identified
                    ? checked((int)Math.Min(int.MaxValue, HopMeters(contact, contact.Telegraph)))
                    : 0;
                intelligence = intelligence.SetItem(key, new IntelligenceEstimate(
                    key, contact.RegionId, coordinate, confidence, dueAt, radius, classification, revision,
                    DetectionChannel(reportingSensor, contact), quality, observedHeading, observedSpeed));
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Detection, contact.RegionId,
                    reportingSensor?.EntityId ?? contact.EntityId,
                    contact.EntityId, 0, 0, $"{confidence} {quality} {classification} contact"));
            }
        }
        return new TickResult(state with { Intelligence = intelligence }, outcomes);
    }

    private static ImmutableSortedDictionary<ScheduledActivityId, ScheduledActivityState> CreateActivities(
        CampaignState state,
        CampaignTime dueAt)
    {
        var result = ImmutableSortedDictionary<ScheduledActivityId, ScheduledActivityState>.Empty;
        foreach (var group in state.Groups.Values)
        {
            foreach (var kind in new[] { CampaignActivityKind.AiIntent, CampaignActivityKind.Movement, CampaignActivityKind.Sustainment })
            {
                var subject = CampaignSubject.Group(group.GroupId);
                var id = ActivityId(state.Metadata.Seed, subject, dueAt, kind);
                result = result.Add(id, new ScheduledActivityState(id, kind, subject, dueAt, new EntityRevision(1)));
            }
        }
        foreach (var entity in state.Entities.Values)
        {
            foreach (var kind in new[] { CampaignActivityKind.Repair, CampaignActivityKind.Attrition, CampaignActivityKind.Intelligence })
            {
                var subject = CampaignSubject.Entity(entity.EntityId);
                var id = ActivityId(state.Metadata.Seed, subject, dueAt, kind);
                result = result.Add(id, new ScheduledActivityState(id, kind, subject, dueAt, new EntityRevision(1)));
            }
        }
        return result;
    }

    private static IReadOnlyList<RegionId> Neighbors(CampaignState state, RegionId regionId) =>
        state.Regions.Values
            .Where(item => item.RegionId == regionId || item.Neighbors.Contains(regionId))
            .SelectMany(item => item.RegionId == regionId ? item.Neighbors : [item.RegionId])
            .Distinct()
            .OrderBy(item => item)
            .ToArray();

    private static RegionId NextRegion(CampaignState state, RegionId origin, RegionId destination)
    {
        if (origin == destination)
        {
            return origin;
        }

        var visited = new HashSet<RegionId> { origin };
        var queue = new Queue<(RegionId Region, RegionId First)>();
        foreach (var neighbor in Neighbors(state, origin))
        {
            visited.Add(neighbor);
            queue.Enqueue((neighbor, neighbor));
        }
        while (queue.TryDequeue(out var item))
        {
            if (item.Region == destination)
            {
                return item.First;
            }
            foreach (var neighbor in Neighbors(state, item.Region))
            {
                if (visited.Add(neighbor))
                {
                    queue.Enqueue((neighbor, item.First));
                }
            }
        }
        return origin;
    }

    private static bool IsActivelyOrdered(CampaignState state, EntityId entityId) =>
        state.Groups.Values.Any(item => item.Members.Contains(entityId) && item.ActiveOrderId is not null);

    private static bool IsOperational(CampaignState state, EntityState entity) =>
        entity.Lifecycle is not (EntityLifecycle.Disabled or EntityLifecycle.Destroyed or EntityLifecycle.Retired)
        && entity.Fidelity != FidelityState.Suspended
        && !HasActiveTacticalAuthority(state, entity.EntityId);

    private static TickResult ResolveInFlightOrdnance(
        CampaignState state,
        CampaignTime dueAt,
        IReadOnlyList<OperationalOutcome> priorOutcomes)
    {
        var entities = state.Entities;
        var remaining = new List<InFlightOrdnance>();
        var outcomes = priorOutcomes.ToList();
        foreach (var round in state.InFlight.OrderBy(item => item.OrdnanceId, StringComparer.Ordinal))
        {
            if (!entities.TryGetValue(round.TargetEntityId, out var target)
                || !CanBeAttacked(state with { Entities = entities }, target))
            {
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.SeekerLost, round.TargetEntityId == default
                    ? state.Regions.Keys.First()
                    : target?.RegionId ?? state.Regions.Keys.First(),
                    round.ShooterEntityId, round.TargetEntityId, 0, 0,
                    $"Target lost; analogue round {round.OrdnanceId} discarded"));
                continue;
            }

            if (!entities.TryGetValue(round.ShooterEntityId, out var shooter)
                || shooter.CoalitionId != round.ShooterCoalitionId)
            {
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.SeekerLost, target.RegionId,
                    round.ShooterEntityId, target.EntityId, 0, 0,
                    $"Shooter state lost; analogue round {round.OrdnanceId} discarded"));
                continue;
            }

            var distanceBeforeStep = RoundedGreatCircleDistanceMeters(round.Coordinate, target.Coordinate);
            var terminalApproach = round.Stage == OrdnanceFlightStage.Terminal
                || round.RemainingMeters <= round.SpeedMetersPerTick;
            var seekerDelay = Math.Max(0, round.SeekerDelayTicks - 1);
            var externalGuidance = round.Guidance is OrdnanceGuidance.SemiActive or OrdnanceGuidance.Command;
            var hasExternalGuidance = !externalGuidance
                || CanProvideExternalGuidance(state with { Entities = entities }, shooter, target);
            var lostGuidanceTicks = hasExternalGuidance ? 0 : round.LostGuidanceTicks + 1;
            var headingMilli = round.HeadingMilliDegrees;
            if (terminalApproach && round.Guidance == OrdnanceGuidance.Active
                && seekerDelay == 0
                && distanceBeforeStep <= ActiveSeekerRangeMeters(round))
            {
                headingMilli = HeadingToward(round.Coordinate, target.Coordinate,
                    HeadingMilliDegrees.FromMilliDegrees(headingMilli)).Value;
            }
            else if (terminalApproach && externalGuidance && hasExternalGuidance && seekerDelay == 0)
            {
                headingMilli = HeadingToward(round.Coordinate, target.Coordinate,
                    HeadingMilliDegrees.FromMilliDegrees(headingMilli)).Value;
            }
            else
            {
                var driftRight = KeyedValue(state.Metadata.Seed, round.ShooterEntityId.Value,
                    round.LaunchedAt, $"ins-direction-{round.OrdnanceId}") % 2 == 0;
                headingMilli = NormalizeHeadingMilliDegrees(
                    headingMilli + (driftRight ? round.InsDriftMilliUnits : -round.InsDriftMilliUnits));
            }

            var remainingMeters = Math.Max(0, round.RemainingMeters - round.SpeedMetersPerTick);
            var step = Math.Min(round.SpeedMetersPerTick, round.RemainingMeters);
            var coordinate = step > 0
                ? DisplaceAlongBearing(round.Coordinate, headingMilli, step)
                : round.Coordinate;
            var stage = remainingMeters <= round.SpeedMetersPerTick
                ? OrdnanceFlightStage.Terminal
                : remainingMeters < round.RemainingMeters
                    ? OrdnanceFlightStage.Cruise
                    : round.Stage;
            var advanced = round with
            {
                Coordinate = coordinate,
                HeadingMilliDegrees = headingMilli,
                RemainingMeters = remainingMeters,
                Stage = stage,
                SeekerDelayTicks = seekerDelay,
                LostGuidanceTicks = lostGuidanceTicks,
            };
            if (round.Kind == OrdnanceKind.Decoy)
            {
                if (remainingMeters > 0)
                {
                    remaining.Add(advanced);
                }
                continue;
            }

            if (externalGuidance && !hasExternalGuidance
                && (lostGuidanceTicks >= 2 || remainingMeters == 0))
            {
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.SeekerLost, target.RegionId,
                    round.ShooterEntityId, target.EntityId, 0, 0,
                    $"External guidance lost for analogue round {round.OrdnanceId}"));
                continue;
            }

            if (remainingMeters > 0)
            {
                remaining.Add(advanced);
                continue;
            }

            var distance = RoundedGreatCircleDistanceMeters(coordinate, target.Coordinate);
            if (distance > TerminalHitRadiusMeters(round))
            {
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.SeekerLost, target.RegionId,
                    round.ShooterEntityId, target.EntityId, 0, 0,
                    $"Terminal seeker missed by {distance} m"));
                continue;
            }

            if (round.Kind == OrdnanceKind.Torpedo
                && target.Analogue.CountermeasureMilliUnits >= 100)
            {
                target = target with
                {
                    Analogue = target.Analogue with
                    {
                        CountermeasureMilliUnits = target.Analogue.CountermeasureMilliUnits - 100,
                    },
                    Revision = target.Revision.Next(),
                };
                entities = entities.SetItem(target.EntityId, target);
                var defeated = KeyedValue(state.Metadata.Seed, round.ShooterEntityId.Value, dueAt,
                    $"noisemaker-{round.OrdnanceId}") % 1_000 < 280;
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Countermeasure, target.RegionId,
                    target.EntityId, round.ShooterEntityId, 0, 0,
                    defeated
                        ? $"Noisemaker defeated analogue torpedo {round.OrdnanceId}"
                        : $"Noisemaker failed against analogue torpedo {round.OrdnanceId}"));
                if (defeated)
                {
                    continue;
                }
            }

            var weapon = new OperationalWeaponCapability(
                round.AmmunitionId,
                round.TargetClassification,
                DistanceMeters.FromMeters(Math.Max(1, ActiveSeekerRangeMeters(round))));
            var pd = PointDefensePenalty(target, distance);
            if (pd > 0)
            {
                const long pointDefenseExpenditure = 8_000;
                var available = Get(target.Resources, ResourceKind.Munitions);
                var expenditure = Math.Min(available, pointDefenseExpenditure);
                target = target with
                {
                    Resources = Set(target.Resources, ResourceKind.Munitions, available - expenditure),
                    Revision = target.Revision.Next(),
                };
                entities = entities.SetItem(target.EntityId, target);
                if (KeyedValue(state.Metadata.Seed, round.ShooterEntityId.Value, dueAt,
                        $"pd-{round.OrdnanceId}") % 1_000 < pd)
                {
                    outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.PointDefense, target.RegionId,
                        target.EntityId, round.ShooterEntityId, expenditure, 0,
                        $"Point defense defeated analogue round {round.OrdnanceId}"));
                    continue;
                }
            }

            var hit = ResolveTerminalHit(state, dueAt, shooter, target, weapon, distance, coordinate);
            outcomes.AddRange(hit.Outcomes);
            if (hit.Target is { } updated)
            {
                entities = entities.SetItem(updated.EntityId, updated);
            }
        }

        return new TickResult(
            state with
            {
                Entities = entities,
                InFlight = remaining.OrderBy(item => item.OrdnanceId, StringComparer.Ordinal).ToImmutableArray(),
            },
            outcomes);
    }

    private readonly record struct TerminalHit(EntityState? Target, IReadOnlyList<OperationalOutcome> Outcomes);

    private static TerminalHit ResolveTerminalHit(
        CampaignState state,
        CampaignTime dueAt,
        EntityState actor,
        EntityState target,
        OperationalWeaponCapability weapon,
        long distanceMeters,
        GeoCoordinate threatCoordinate)
    {
        var rangeRatio = weapon.MaximumRange.Value <= 0
            ? 1_000
            : (int)Math.Min(1_000, distanceMeters * 1_000L / weapon.MaximumRange.Value);
        var ecm = target.SimulationDomain == SimulationDomain.Air
            || target.Analogue.SensorIntegrityMilliUnits < 700
            ? 80 : 0;
        ecm += target.Analogue.EcmMilliUnits / 8;
        var hitChance = Math.Clamp(
            280 + actor.ReadinessMilliUnits / 2
            - target.ReadinessMilliUnits / 8
            + (actor.SimulationDomain == SimulationDomain.Air ? 80 : 0)
            - rangeRatio / 4
            - HitWeatherPenalty(state, target.RegionId)
            - (Classify(target) == ContactClassification.Subsurface
                && Classify(actor) != ContactClassification.Subsurface ? 120 : 0)
            - ecm
            - (1_000 - target.Analogue.PropulsionIntegrityMilliUnits) / 20,
            80,
            900);
        var roll = KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt, $"engagement-hit-{target.EntityId}") % 1_000;
        if (roll >= hitChance)
        {
            return new TerminalHit(null, []);
        }

        var rangeDamage = 40_000 + (1_000 - rangeRatio) * 80
            + KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt, $"engagement-damage-{target.EntityId}") % 90_001;
        var applied = Math.Min(rangeDamage, 1_000_000 - target.DamageMilliUnits);
        if (applied <= 0)
        {
            return new TerminalHit(null, []);
        }

        var portHit = KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt, $"side-{target.EntityId}") % 2 == 0;
        var analogue = target.Analogue with
        {
            FireMilliUnits = Math.Min(1_000_000, target.Analogue.FireMilliUnits + applied / 4),
            FloodMilliUnits = Math.Min(1_000_000, target.Analogue.FloodMilliUnits + applied / 5),
            PropulsionIntegrityMilliUnits = Math.Max(0, target.Analogue.PropulsionIntegrityMilliUnits - applied / 8_000),
            SensorIntegrityMilliUnits = Math.Max(0, target.Analogue.SensorIntegrityMilliUnits - applied / 10_000),
            WeaponIntegrityMilliUnits = Math.Max(0, target.Analogue.WeaponIntegrityMilliUnits - applied / 9_000),
            MagazineIntegrityMilliUnits = Math.Max(0, target.Analogue.MagazineIntegrityMilliUnits - applied / 12_000),
            FlightDeckIntegrityMilliUnits = Math.Max(0, target.Analogue.FlightDeckIntegrityMilliUnits - applied / 15_000),
            CrewMilliUnits = Math.Max(0, target.Analogue.CrewMilliUnits - applied / 11_000),
            PortIntegrityMilliUnits = Math.Max(0, target.Analogue.PortIntegrityMilliUnits - (portHit ? applied / 7_000 : applied / 20_000)),
            StarboardIntegrityMilliUnits = Math.Max(0, target.Analogue.StarboardIntegrityMilliUnits - (portHit ? applied / 20_000 : applied / 7_000)),
            Alert = AlertState.General,
            AltitudeOrDepthMeters = Classify(target) == ContactClassification.Subsurface
                ? Math.Max(target.Analogue.AltitudeOrDepthMeters, 180)
                : target.Analogue.AltitudeOrDepthMeters,
        };
        var extra = 0;
        if (analogue.MagazineIntegrityMilliUnits < 400
            && analogue.FireMilliUnits >= 50_000
            && KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt, $"secondary-{target.EntityId}") % 1_000 < 120)
        {
            extra = Math.Max(0,
                Math.Min(40_000, 1_000_000 - (target.DamageMilliUnits + applied)));
            analogue = analogue with
            {
                MagazineIntegrityMilliUnits = Math.Max(0, analogue.MagazineIntegrityMilliUnits - 100),
                FireMilliUnits = Math.Min(1_000_000, analogue.FireMilliUnits + 20_000),
            };
        }
        var totalDamage = target.DamageMilliUnits + applied + extra;
        var lifecycle = totalDamage >= 1_000_000
            ? EntityLifecycle.Destroyed
            : totalDamage >= 800_000 ? EntityLifecycle.Disabled : target.Lifecycle;
        var updated = target with
        {
            DamageMilliUnits = totalDamage,
            ReadinessMilliUnits = Math.Clamp(1_000 - totalDamage / 1_000, 0, 1_000),
            Lifecycle = lifecycle,
            Heading = HeadingAway(target.Coordinate, threatCoordinate, target.Heading),
            Telegraph = lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Disabled
                ? TelegraphStop
                : TelegraphFlank,
            Analogue = analogue,
            Revision = target.Revision.Next(),
        };
        var outcomes = new List<OperationalOutcome>
        {
            Outcome(state, dueAt, OperationalOutcomeKind.Damage, target.RegionId,
                actor.EntityId, target.EntityId, 0, applied,
                $"Operational combat damage at {distanceMeters} m"),
        };
        if (extra > 0)
        {
            outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.SecondaryExplosion, target.RegionId,
                actor.EntityId, target.EntityId, 0, extra, "Analogue magazine secondary explosion"));
        }
        if (lifecycle == EntityLifecycle.Destroyed)
        {
            outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Destruction, target.RegionId,
                actor.EntityId, target.EntityId, 0, applied, "Entity destroyed by operational resolution"));
        }
        return new TerminalHit(updated, outcomes);
    }

    private static bool IsBallisticGun(OperationalWeaponCapability weapon) =>
        weapon.MaximumRange.Value < 40_000
        || ContainsToken(weapon.AmmunitionId, "gun")
        || ContainsToken(weapon.AmmunitionId, "shell")
        || ContainsToken(weapon.AmmunitionId, "ciws");

    private static long OrdnanceSpeedMetersPerTick(OperationalWeaponCapability weapon) =>
        ContainsToken(weapon.AmmunitionId, "torpedo") ? 25_000
        : ContainsToken(weapon.AmmunitionId, "bomb") ? 40_000
        : ContainsToken(weapon.AmmunitionId, "decoy") || ContainsToken(weapon.AmmunitionId, "chaff") ? 8_000
        : 100_000;

    private static OrdnanceKind OrdnanceKindFor(OperationalWeaponCapability weapon) =>
        ContainsToken(weapon.AmmunitionId, "torpedo") ? OrdnanceKind.Torpedo
        : ContainsToken(weapon.AmmunitionId, "bomb") ? OrdnanceKind.Bomb
        : ContainsToken(weapon.AmmunitionId, "decoy") || ContainsToken(weapon.AmmunitionId, "chaff") ? OrdnanceKind.Decoy
        : OrdnanceKind.Missile;

    private static long ActiveSeekerRangeMeters(InFlightOrdnance round) =>
        ContainsToken(round.AmmunitionId, "torpedo") ? 40_000 : 120_000;

    private static long TerminalHitRadiusMeters(InFlightOrdnance round) =>
        ContainsToken(round.AmmunitionId, "torpedo") ? 5_000 : 15_000;

    private static bool CanIlluminate(CampaignState state, EntityState shooter, EntityState target) =>
        CanSense(state, shooter)
        && (RoundedGreatCircleDistanceMeters(shooter.Coordinate, target.Coordinate) <= VisualContactMeters
            || DetectionScore(state, shooter, target) > 0);

    private static bool CanProvideExternalGuidance(
        CampaignState state,
        EntityState shooter,
        EntityState target) =>
        shooter.Analogue.Emcon == EmconState.Radiate
        && CanIlluminate(state, shooter, target);

    private static OrdnanceGuidance OrdnanceGuidanceFor(OperationalWeaponCapability weapon) =>
        ContainsToken(weapon.AmmunitionId, "bomb") || ContainsToken(weapon.AmmunitionId, "decoy")
            ? OrdnanceGuidance.Inertial
            : ContainsToken(weapon.AmmunitionId, "sam") || ContainsToken(weapon.AmmunitionId, "aa")
                ? OrdnanceGuidance.SemiActive
                : ContainsToken(weapon.AmmunitionId, "command")
                    ? OrdnanceGuidance.Command
                    : OrdnanceGuidance.Active;

    private static bool CanAttack(CampaignState state, EntityState entity) =>
        IsOperational(state, entity)
        && entity.SimulationDomain != SimulationDomain.Logistics
        && entity.Analogue.WeaponIntegrityMilliUnits >= 200
        && entity.Analogue.MagazineIntegrityMilliUnits >= 80
        && entity.Analogue.WeaponReadyMilliUnits >= 400
        && EffectiveWeapons(entity).Length > 0
        && Get(entity.Resources, ResourceKind.Munitions) > 0;

    private static ImmutableArray<OperationalWeaponCapability> EffectiveWeapons(EntityState entity)
    {
        if (entity.OperationalWeapons.Length > 0)
        {
            return entity.OperationalWeapons;
        }

        return entity.SimulationDomain switch
        {
            SimulationDomain.LandAbstract =>
            [
                new OperationalWeaponCapability(
                    "abstract-corps-fires",
                    ContactClassification.LandFormation,
                    DistanceMeters.FromMeters(80_000)),
            ],
            _ => [],
        };
    }

    private static (
        EntityState Entity,
        OperationalWeaponCapability Weapon,
        long ActualDistanceMeters,
        long TargetingDistanceMeters,
        GeoCoordinate TargetingCoordinate)?
        SelectOperationalWeapon(CampaignState state, EntityState actor, EntityState target)
    {
        var actualDistanceMeters = RoundedGreatCircleDistanceMeters(actor.Coordinate, target.Coordinate);
        var visual = actualDistanceMeters <= VisualContactMeters;
        IntelligenceEstimate? estimate = null;
        if (!visual
            && !state.Intelligence.TryGetValue(new IntelligenceKey(actor.CoalitionId, target.EntityId), out estimate))
        {
            return null;
        }

        var classification = visual ? Classify(target) : estimate!.Classification;
        if (classification == ContactClassification.Unknown)
        {
            return null;
        }
        var targetingCoordinate = visual ? target.Coordinate : estimate!.EstimatedCoordinate;
        var targetingDistanceMeters = RoundedGreatCircleDistanceMeters(actor.Coordinate, targetingCoordinate);
        var weapon = EffectiveWeapons(actor)
            .Where(item => OperationalWeaponTargets(item.TargetClassification, classification))
            .Where(item => actualDistanceMeters <= item.MaximumRange.Value)
            .Where(item => targetingDistanceMeters <= item.MaximumRange.Value)
            .Where(item => visual
                || targetingDistanceMeters + estimate!.UncertaintyRadiusMeters <= item.MaximumRange.Value)
            .OrderBy(item => item.MaximumRange.Value)
            .ThenBy(item => item.AmmunitionId, StringComparer.Ordinal)
            .FirstOrDefault();
        return weapon is null
            ? null
            : (target, weapon, actualDistanceMeters, targetingDistanceMeters, targetingCoordinate);
    }

    private static bool OperationalWeaponTargets(
        ContactClassification weaponTarget,
        ContactClassification target) =>
        weaponTarget == target
        || weaponTarget == ContactClassification.Surface
            && target is ContactClassification.Logistics
        || weaponTarget == ContactClassification.Air
            && target == ContactClassification.Air;

    private static bool HasFireSolution(
        CampaignState state,
        EntityState actor,
        EntityState target,
        long distanceMeters)
    {
        if (distanceMeters <= VisualContactMeters)
        {
            return actor.Analogue.Roe != RulesOfEngagement.WeaponsHold || IsDesperate(actor);
        }

        if (actor.Analogue.Roe == RulesOfEngagement.WeaponsHold)
        {
            return false;
        }

        var key = new IntelligenceKey(actor.CoalitionId, target.EntityId);
        if (!state.Intelligence.TryGetValue(key, out var estimate))
        {
            return actor.Analogue.Roe == RulesOfEngagement.WeaponsFree;
        }

        if (estimate.Classification == ContactClassification.Unknown
            && estimate.Confidence < IntelligenceConfidence.Probable
            && !IsDesperate(actor)
            && actor.Analogue.Roe != RulesOfEngagement.WeaponsFree)
        {
            return false;
        }

        return estimate.Confidence switch
        {
            IntelligenceConfidence.Confirmed or IntelligenceConfidence.Probable => true,
            IntelligenceConfidence.Possible => IsDesperate(actor) || actor.Analogue.Roe == RulesOfEngagement.WeaponsFree,
            _ => actor.Analogue.Roe == RulesOfEngagement.WeaponsFree,
        };
    }

    private static int TargetPriority(
        CampaignState state,
        EntityState actor,
        EntityState target,
        long distanceMeters,
        OperationalWeaponCapability weapon)
    {
        var visual = distanceMeters <= VisualContactMeters;
        var confidence = visual
            ? IntelligenceConfidence.Confirmed
            : state.Intelligence.TryGetValue(new IntelligenceKey(actor.CoalitionId, target.EntityId), out var estimate)
                ? estimate.Confidence
                : IntelligenceConfidence.Unknown;
        var classification = visual
            ? Classify(target)
            : state.Intelligence.TryGetValue(new IntelligenceKey(actor.CoalitionId, target.EntityId), out var classifiedEstimate)
                ? classifiedEstimate.Classification
                : ContactClassification.Unknown;
        var military = classification is ContactClassification.Logistics ? 0 : 120;
        var rangeRemaining = weapon.MaximumRange.Value <= 0
            ? 0
            : (int)Math.Min(1_000, (weapon.MaximumRange.Value - distanceMeters) * 1_000L / weapon.MaximumRange.Value);
        return (int)confidence * 1_000 + military + rangeRemaining
            + (state.InFlight.Any(item => item.TargetEntityId == actor.EntityId && item.ShooterEntityId == target.EntityId)
                ? 400 : 0);
    }

    private static long WeaponExpenditure(OperationalWeaponCapability weapon, SimulationDomain domain)
    {
        if (domain == SimulationDomain.LandAbstract)
        {
            return 15_000;
        }

        var ammunition = weapon.AmmunitionId;
        if (ContainsToken(ammunition, "gun")
            || ContainsToken(ammunition, "shell")
            || ContainsToken(ammunition, "ciws")
            || weapon.MaximumRange.Value < 40_000)
        {
            return 8_000;
        }
        if (ContainsToken(ammunition, "torpedo"))
        {
            return 22_000;
        }
        if (ContainsToken(ammunition, "bomb"))
        {
            return 18_000;
        }
        return 30_000;
    }

    private static bool ContainsToken(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static int HitWeatherPenalty(CampaignState state, RegionId regionId)
    {
        if (!state.Weather.TryGetValue(regionId, out var weather))
        {
            return 0;
        }

        var penalty = weather.Visibility.Value < 10_000 ? 80 : weather.Visibility.Value < 25_000 ? 30 : 0;
        return penalty + (weather.SeaStateMilliUnits >= 6_000 ? 40 : 0);
    }

    private static bool ShouldWithdraw(IReadOnlyList<EntityState> members) =>
        members.Max(item => item.DamageMilliUnits) >= 600_000
        || members.Any(item => item.SimulationDomain == SimulationDomain.Air
            && Get(item.Resources, ResourceKind.Fuel) < 150_000)
        || members.Any(item => item.SimulationDomain == SimulationDomain.Air
            && EffectiveWeapons(item).Length > 0
            && Get(item.Resources, ResourceKind.Munitions) == 0)
        || members.Any(item => Classify(item) == ContactClassification.Subsurface
            && item.DamageMilliUnits >= 250_000)
        || members.Any(item => Get(item.Resources, ResourceKind.Fuel) < 80_000
            && item.SimulationDomain != SimulationDomain.Logistics);

    private static string WithdrawalReason(IReadOnlyList<EntityState> members)
    {
        if (members.Any(item => item.SimulationDomain == SimulationDomain.Air
            && Get(item.Resources, ResourceKind.Fuel) < 150_000))
        {
            return "Bingo fuel; task force ordered to withdraw";
        }
        if (members.Any(item => item.SimulationDomain == SimulationDomain.Air
            && EffectiveWeapons(item).Length > 0
            && Get(item.Resources, ResourceKind.Munitions) == 0))
        {
            return "Winchester; task force ordered to withdraw";
        }
        if (members.Any(item => Classify(item) == ContactClassification.Subsurface
            && item.DamageMilliUnits >= 250_000))
        {
            return "Subsurface damage; task force ordered to withdraw";
        }
        if (members.Any(item => Get(item.Resources, ResourceKind.Fuel) < 80_000))
        {
            return "Fuel state; task force ordered to withdraw";
        }
        return "Whole task force ordered to withdraw";
    }

    private static bool IsAlreadyWithdrawing(GroupState group, ImmutableSortedDictionary<OrderId, OrderState> orders) =>
        group.ActiveOrderId is { } orderId
        && orders.TryGetValue(orderId, out var order)
        && order.Kind == OrderKind.Withdraw
        && order.CompletedAt is null;

    private static ImmutableSortedDictionary<OrderId, OrderState> CompleteActiveOrder(
        GroupState group,
        ImmutableSortedDictionary<OrderId, OrderState> orders,
        CampaignTime dueAt)
    {
        if (group.ActiveOrderId is not { } previousOrderId
            || !orders.TryGetValue(previousOrderId, out var previousOrder)
            || previousOrder.CompletedAt is not null)
        {
            return orders;
        }
        return orders.SetItem(previousOrderId, previousOrder with
        {
            CompletedAt = dueAt,
            Revision = previousOrder.Revision.Next(),
        });
    }

    private static bool IsDesperate(EntityState actor) =>
        actor.DamageMilliUnits >= 400_000 || actor.ReadinessMilliUnits < 400;

    private static bool CanSense(CampaignState state, EntityState entity) =>
        IsOperational(state, entity)
        && entity.Analogue.SensorIntegrityMilliUnits >= 150;

    private static bool CoalitionParticipated(
        CampaignState state,
        CoalitionId observer,
        OperationalOutcome outcome) =>
        state.Entities.TryGetValue(outcome.ActorEntityId, out var actor) && actor.CoalitionId == observer
        || outcome.TargetEntityId is { } targetId
            && state.Entities.TryGetValue(targetId, out var target) && target.CoalitionId == observer;

    private static EntityState? ParticipatingObserver(
        CampaignState state,
        CoalitionId observer,
        IReadOnlyList<OperationalOutcome> outcomes,
        EntityId contactId)
    {
        foreach (var outcome in outcomes.Where(item =>
                     item.ActorEntityId == contactId || item.TargetEntityId == contactId))
        {
            if (state.Entities.TryGetValue(outcome.ActorEntityId, out var actor)
                && actor.CoalitionId == observer)
            {
                return actor;
            }
            if (outcome.TargetEntityId is { } targetId
                && state.Entities.TryGetValue(targetId, out var target)
                && target.CoalitionId == observer)
            {
                return target;
            }
        }
        return null;
    }

    private static SensorChannel DetectionChannel(EntityState? sensor, EntityState contact)
    {
        if (sensor is not null
            && RoundedGreatCircleDistanceMeters(sensor.Coordinate, contact.Coordinate) <= VisualContactMeters)
        {
            return SensorChannel.Visual;
        }
        return Classify(contact) == ContactClassification.Subsurface
            ? SensorChannel.Sonar
            : SensorChannel.Radar;
    }

    private static GeoCoordinate RegionAnchor(
        CampaignState state,
        RegionId region,
        CoalitionId coalition,
        GeoCoordinate fallback)
    {
        var friendlyBase = state.Bases.Values
            .Where(item => item.RegionId == region && item.CoalitionId == coalition)
            .OrderBy(item => item.BaseId)
            .FirstOrDefault();
        if (friendlyBase is not null)
        {
            return friendlyBase.Coordinate;
        }

        var anyBase = state.Bases.Values
            .Where(item => item.RegionId == region)
            .OrderBy(item => item.BaseId)
            .FirstOrDefault();
        if (anyBase is not null)
        {
            return anyBase.Coordinate;
        }

        var friendly = state.Entities.Values
            .Where(item => item.RegionId == region && item.CoalitionId == coalition)
            .OrderBy(item => item.EntityId)
            .FirstOrDefault();
        if (friendly is not null)
        {
            return friendly.Coordinate;
        }

        var occupant = state.Entities.Values
            .Where(item => item.RegionId == region)
            .OrderBy(item => item.EntityId)
            .FirstOrDefault();
        return occupant?.Coordinate ?? fallback;
    }

    private static bool HasHostileInRegion(CampaignState state, RegionId region, CoalitionId coalition) =>
        state.Entities.Values.Any(item => item.RegionId == region
            && item.CoalitionId != coalition
            && CanBeAttacked(state, item));

    private static HeadingMilliDegrees HeadingToward(
        GeoCoordinate origin,
        GeoCoordinate destination,
        HeadingMilliDegrees current)
    {
        var bearing = BearingMilliDegrees(origin, destination);
        return bearing >= 0 ? HeadingMilliDegrees.FromMilliDegrees(bearing) : current;
    }

    private static HeadingMilliDegrees HeadingAway(
        GeoCoordinate origin,
        GeoCoordinate destination,
        HeadingMilliDegrees current)
    {
        var bearing = BearingMilliDegrees(origin, destination);
        if (bearing < 0)
        {
            return current;
        }

        return HeadingMilliDegrees.FromMilliDegrees((bearing + 180_000) % 360_000);
    }

    private static int BearingMilliDegrees(GeoCoordinate origin, GeoCoordinate destination)
    {
        if (origin.LatitudeMicrodegrees == destination.LatitudeMicrodegrees
            && origin.LongitudeMicrodegrees == destination.LongitudeMicrodegrees)
        {
            return -1;
        }

        const double microdegreesPerDegree = 1_000_000d;
        var originLatitude = origin.LatitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var originLongitude = origin.LongitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var destinationLatitude = destination.LatitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var destinationLongitude = destination.LongitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var longitudeDelta = destinationLongitude - originLongitude;
        var y = Math.Sin(longitudeDelta) * Math.Cos(destinationLatitude);
        var x = Math.Cos(originLatitude) * Math.Sin(destinationLatitude)
            - Math.Sin(originLatitude) * Math.Cos(destinationLatitude) * Math.Cos(longitudeDelta);
        var degrees = Math.Atan2(y, x) * (180d / Math.PI);
        var milli = (int)Math.Round(degrees * 1_000d, MidpointRounding.AwayFromZero);
        milli %= 360_000;
        if (milli < 0)
        {
            milli += 360_000;
        }
        return milli;
    }

    private static GeoCoordinate DisplaceAlongBearing(GeoCoordinate origin, int bearingMilli, long distanceMeters)
    {
        if (distanceMeters <= 0)
        {
            return origin;
        }

        const double microdegreesPerDegree = 1_000_000d;
        var latitude = origin.LatitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var longitude = origin.LongitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var bearing = bearingMilli / 1_000d * (Math.PI / 180d);
        var angularDistance = distanceMeters / (double)MeanEarthRadiusMeters;
        var destinationLatitudeSine = Math.Sin(latitude) * Math.Cos(angularDistance)
            + Math.Cos(latitude) * Math.Sin(angularDistance) * Math.Cos(bearing);
        var destinationLatitude = Math.Asin(Math.Clamp(destinationLatitudeSine, -1d, 1d));
        var destinationLongitude = longitude + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitude),
            Math.Cos(angularDistance) - Math.Sin(latitude) * Math.Sin(destinationLatitude));
        var latitudeMicro = (int)Math.Round(destinationLatitude * (180d / Math.PI) * microdegreesPerDegree, MidpointRounding.AwayFromZero);
        var longitudeMicro = (int)Math.Round(destinationLongitude * (180d / Math.PI) * microdegreesPerDegree, MidpointRounding.AwayFromZero);
        latitudeMicro = Math.Clamp(latitudeMicro, -90_000_000, 90_000_000);
        longitudeMicro = NormalizeLongitudeMicrodegrees(longitudeMicro);
        return GeoCoordinate.FromMicrodegrees(latitudeMicro, longitudeMicro);
    }

    private static int NormalizeLongitudeMicrodegrees(long longitudeMicrodegrees)
    {
        longitudeMicrodegrees %= 360_000_000;
        if (longitudeMicrodegrees >= 180_000_000)
        {
            longitudeMicrodegrees -= 360_000_000;
        }
        else if (longitudeMicrodegrees < -180_000_000)
        {
            longitudeMicrodegrees += 360_000_000;
        }
        return checked((int)longitudeMicrodegrees);
    }

    private static int NormalizeHeadingMilliDegrees(int headingMilliDegrees)
    {
        headingMilliDegrees %= 360_000;
        return headingMilliDegrees < 0 ? headingMilliDegrees + 360_000 : headingMilliDegrees;
    }

    private static long HopMeters(EntityState entity, int telegraph)
    {
        var cruise = entity.SimulationDomain switch
        {
            SimulationDomain.Air => 280_000L,
            SimulationDomain.LandAbstract => 80_000L,
            SimulationDomain.Logistics => 140_000L,
            _ when Classify(entity) == ContactClassification.Subsurface => 90_000L,
            _ => MetersPerRegionHop,
        };
        return telegraph == TelegraphFlank ? cruise + cruise / 5 : cruise;
    }

    private static int PointDefensePenalty(EntityState target, long distanceMeters)
    {
        if (distanceMeters > 25_000
            || target.Analogue.WeaponIntegrityMilliUnits < 200
            || Get(target.Resources, ResourceKind.Munitions) <= 0)
        {
            return 0;
        }

        return EffectiveWeapons(target).Any(item => item.MaximumRange.Value <= 40_000) ? 140 : 0;
    }

    private static long RoundedGreatCircleDistanceMeters(GeoCoordinate origin, GeoCoordinate destination)
    {
        const double microdegreesPerDegree = 1_000_000d;
        var originLatitude = origin.LatitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var originLongitude = origin.LongitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var destinationLatitude = destination.LatitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var destinationLongitude = destination.LongitudeMicrodegrees / microdegreesPerDegree * (Math.PI / 180d);
        var latitudeDelta = destinationLatitude - originLatitude;
        var longitudeDelta = destinationLongitude - originLongitude;
        var chord = Math.Pow(Math.Sin(latitudeDelta / 2d), 2d)
            + Math.Cos(originLatitude) * Math.Cos(destinationLatitude)
            * Math.Pow(Math.Sin(longitudeDelta / 2d), 2d);
        chord = Math.Clamp(chord, 0d, 1d);
        var angularDistance = 2d * Math.Atan2(Math.Sqrt(chord), Math.Sqrt(1d - chord));
        return (long)Math.Round(MeanEarthRadiusMeters * angularDistance, MidpointRounding.AwayFromZero);
    }

    private static bool CanBeAttacked(CampaignState state, EntityState entity) =>
        entity.Lifecycle is not (EntityLifecycle.Disabled or EntityLifecycle.Destroyed or EntityLifecycle.Retired)
        && entity.Fidelity != FidelityState.Suspended
        && !HasActiveTacticalAuthority(state, entity.EntityId);

    private static int DetectionScore(CampaignState state, EntityState sensor, EntityState target)
    {
        var distance = RoundedGreatCircleDistanceMeters(sensor.Coordinate, target.Coordinate);
        var horizon = EffectiveSensorHorizonMeters(sensor, target);
        if (horizon <= 0)
        {
            return 0;
        }

        var score = distance >= horizon
            ? 0
            : (int)Math.Min(900, (horizon - distance) * 900L / horizon);
        if (score == 0)
        {
            return 0;
        }
        if (sensor.Telegraph == TelegraphStop)
        {
            score -= 90;
        }
        if (target.Telegraph is TelegraphFlank or TelegraphIntercept)
        {
            score += 70;
        }
        if (sensor.SimulationDomain == SimulationDomain.Air)
        {
            score += 80;
        }
        if (target.SimulationDomain == SimulationDomain.LandAbstract)
        {
            score += 60;
        }
        if (Classify(target) == ContactClassification.Subsurface
            && Classify(sensor) != ContactClassification.Subsurface)
        {
            score -= 180 + Math.Min(200, target.Analogue.AltitudeOrDepthMeters);
        }
        score = score * sensor.Analogue.SensorIntegrityMilliUnits / 1_000;
        if (sensor.Analogue.Emcon == EmconState.Silent)
        {
            score -= 220;
        }
        else if (sensor.Analogue.Emcon == EmconState.Passive)
        {
            score -= 90;
        }
        if (target.Analogue.Emcon == EmconState.Radiate)
        {
            score += 70;
        }
        if (Classify(target) == ContactClassification.Subsurface && target.Analogue.AltitudeOrDepthMeters <= 20)
        {
            score += 140;
        }
        if (sensor.Analogue.HostedAircraft > 0 || target.Analogue.HostedAircraft > 0)
        {
            score += 40;
        }
        if (state.Weather.TryGetValue(target.RegionId, out var weather))
        {
            if (Classify(target) != ContactClassification.Subsurface)
            {
                score -= weather.Visibility.Value < 10_000 ? 200 : weather.Visibility.Value < 25_000 ? 75 : 0;
            }
            score -= weather.SeaStateMilliUnits >= 6_000 ? 75 : 0;
        }
        return Math.Clamp(score, 0, 950);
    }

    private static long SensorHorizonMeters(EntityState sensor, EntityState target)
    {
        var targetClass = Classify(target);
        var sensorClass = Classify(sensor);
        if (sensor.SimulationDomain == SimulationDomain.Air)
        {
            return targetClass == ContactClassification.Subsurface ? 25_000 : 350_000;
        }
        if (sensorClass == ContactClassification.Subsurface)
        {
            return targetClass == ContactClassification.Subsurface ? 40_000 : 50_000;
        }
        if (sensor.SimulationDomain == SimulationDomain.LandAbstract)
        {
            return 60_000;
        }
        return targetClass == ContactClassification.Subsurface ? 18_000
            : targetClass == ContactClassification.Air ? 120_000
            : 80_000;
    }

    private static long EffectiveSensorHorizonMeters(EntityState sensor, EntityState target)
    {
        var horizon = SensorHorizonMeters(sensor, target);
        return sensor.Analogue.Emcon switch
        {
            EmconState.Silent => Math.Min(horizon, VisualContactMeters),
            EmconState.Passive when Classify(target) != ContactClassification.Subsurface => Math.Min(horizon, 45_000),
            _ => horizon,
        };
    }

    private static ContactClassification ClassifyObserved(
        EntityState contact,
        IntelligenceConfidence confidence,
        IReadOnlyList<EntityState> sensors,
        int score)
    {
        if (confidence == IntelligenceConfidence.Possible)
        {
            return ContactClassification.Unknown;
        }

        var actual = Classify(contact);
        if (actual != ContactClassification.Subsurface)
        {
            return actual;
        }

        var sonar = sensors.Any(sensor => Classify(sensor) == ContactClassification.Subsurface
            || RoundedGreatCircleDistanceMeters(sensor.Coordinate, contact.Coordinate) <= 18_000);
        return sonar || score >= 900 ? actual : ContactClassification.Unknown;
    }

    private static ContactClassification Classify(EntityState entity) => entity.SimulationDomain switch
    {
        SimulationDomain.Air => ContactClassification.Air,
        SimulationDomain.Logistics => ContactClassification.Logistics,
        SimulationDomain.LandAbstract => ContactClassification.LandFormation,
        _ when entity.CatalogUnitId.Contains("ss", StringComparison.OrdinalIgnoreCase)
            || entity.Name.Contains("submarine", StringComparison.OrdinalIgnoreCase) => ContactClassification.Subsurface,
        _ => ContactClassification.Surface,
    };

    private static OperationalOutcome Outcome(
        CampaignState state,
        CampaignTime dueAt,
        OperationalOutcomeKind kind,
        RegionId regionId,
        EntityId actor,
        EntityId? target,
        long expenditure,
        int damage,
        string detail)
    {
        var targetKey = target?.Value.ToString("N") ?? "none";
        var material = $"outcome-{kind}-{regionId.Value:N}-{targetKey}-{detail}";
        return new OperationalOutcome(
            KeyedGuid(state.Metadata.Seed, actor.Value, dueAt, material).ToString("N"),
            dueAt, kind, regionId, actor, target, expenditure, damage, detail);
    }

    private static bool HasActiveTacticalAuthority(CampaignState state, EntityId entityId) =>
        state.Assignments.Values.Any(item => item.EntityId == entityId
            && !CampaignAssignmentKinds.IsOperationalSim(item)
            && item.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing
                or AssignmentLifecycle.Quiesced);

    private static (ImmutableSortedDictionary<ResourceKind, ResourceAmount> Entity, BaseState Base, bool Changed)
        Replenish(ImmutableSortedDictionary<ResourceKind, ResourceAmount> entity, BaseState campaignBase)
    {
        var result = entity;
        var home = campaignBase;
        var changed = false;
        foreach (var (kind, target) in new[]
        {
            (ResourceKind.Fuel, 1_000_000L),
            (ResourceKind.Stores, 500_000L),
            (ResourceKind.Munitions, 500_000L),
        })
        {
            var current = Get(result, kind);
            var available = Get(home.Resources, kind);
            var transfer = Math.Min(Math.Max(0, target - current), available);
            if (transfer == 0)
            {
                continue;
            }
            result = Set(result, kind, checked(current + transfer));
            home = home with { Resources = Set(home.Resources, kind, available - transfer) };
            changed = true;
        }
        return (result, home, changed);
    }

    private static ImmutableSortedDictionary<ResourceKind, ResourceAmount> Consume(
        ImmutableSortedDictionary<ResourceKind, ResourceAmount> resources,
        ResourceKind kind,
        long amount) =>
        Set(resources, kind, Math.Max(0, Get(resources, kind) - amount));

    private static long Get(ImmutableSortedDictionary<ResourceKind, ResourceAmount> resources, ResourceKind kind) =>
        resources.TryGetValue(kind, out var value) ? value.MilliUnits : 0;

    private static ImmutableSortedDictionary<ResourceKind, ResourceAmount> Set(
        ImmutableSortedDictionary<ResourceKind, ResourceAmount> resources,
        ResourceKind kind,
        long value) => resources.SetItem(kind, ResourceAmount.FromMilliUnits(value));

    private static ScheduledActivityId ActivityId(
        long seed,
        CampaignSubject subject,
        CampaignTime dueAt,
        CampaignActivityKind kind) =>
        new(KeyedGuid(seed, subject.Value, dueAt, $"activity-{subject.Kind}-{kind}"));

    private static int KeyedValue(long seed, Guid subject, CampaignTime time, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{subject:N}:{time.Quanta}:{purpose}"));
        return (int)(BitConverter.ToUInt32(bytes, 0) & 0x7FFFFFFF);
    }

    private static Guid KeyedGuid(long seed, Guid subject, CampaignTime time, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{subject:N}:{time.Quanta}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static SimulatedEntityUpdate ToUpdate(EntityState item) => new(
        item.EntityId, item.RegionId, item.Coordinate, item.Lifecycle, item.Resources,
        item.ReadinessMilliUnits, item.DamageMilliUnits, item.Revision,
        item.Heading.Value, item.Telegraph, item.Analogue);

    private static SimulatedBaseUpdate ToUpdate(BaseState item) =>
        new(item.BaseId, item.Resources, item.Revision);

    private static SimulatedGroupUpdate ToUpdate(GroupState item) =>
        new(item.GroupId, item.ActiveOrderId, item.Revision);

    private static SimulatedOrderUpdate ToUpdate(OrderState item) => new(
        item.OrderId, item.GroupId, item.Kind, item.DestinationRegionId, item.StartsAt,
        item.MustCompleteBy, item.CompletedAt, item.Revision);

    private static SimulatedIntelligenceUpdate ToUpdate(IntelligenceEstimate item) => new(
        item.Key.ObserverCoalitionId, item.Key.ContactEntityId, item.EstimatedRegionId,
        item.EstimatedCoordinate, item.Confidence, item.ObservedAt, item.UncertaintyRadiusMeters,
        item.Classification, item.Revision, item.Channel, item.Quality,
        item.ObservedHeadingMilliDegrees, item.ObservedSpeedMetersPerTick);

    private static ScheduledCampaignActivity ToContract(ScheduledActivityState item) => new(
        item.ActivityId, item.Kind, item.Subject, item.DueAt, item.Revision);

    private sealed record GroupMovementResult(
        GroupId GroupId,
        GroupState Group,
        OrderState? Order,
        IReadOnlyList<EntityState> Entities);

    private sealed record TickResult(CampaignState State, IReadOnlyList<OperationalOutcome> Outcomes);
}
