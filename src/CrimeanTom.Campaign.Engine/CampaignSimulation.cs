using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

internal static class CampaignSimulation
{
    private const long ActivityInterval = 10;
    private const long FuelPerMovement = 25_000;
    private const long StoresPerActivity = 5_000;
    private const long RepairPerActivity = 50_000;

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
            activities.Values.Select(ToContract).ToArray());
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

        var sustained = ResolveSustainmentAndAttrition(
            withIntent with { Entities = entities, Groups = groups, Orders = orders },
            dueAt);
        var engagement = ResolveOperationalEngagements(sustained, dueAt);
        var intelligence = ResolveIntelligence(engagement.State, dueAt, engagement.Outcomes);
        return new TickResult(intelligence.State, [.. engagement.Outcomes, .. intelligence.Outcomes]);
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
            var neighbors = Neighbors(state, first.RegionId);
            if (neighbors.Count == 0)
            {
                continue;
            }

            var destination = neighbors[KeyedValue(state.Metadata.Seed, group.GroupId.Value, dueAt, "ai-destination") % neighbors.Count];
            var orderId = new OrderId(KeyedGuid(state.Metadata.Seed, group.GroupId.Value, dueAt, "ai-order"));
            var order = new OrderState(
                orderId,
                group.GroupId,
                OrderKind.Patrol,
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
            return new GroupMovementResult(group.GroupId, group, null, []);
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

        var origin = activeMembers[0].RegionId;
        var next = NextRegion(state, origin, order.DestinationRegionId);
        var moved = next != origin;
        var entityUpdates = group.Members.Select(id =>
        {
            var entity = state.Entities[id];
            if (!IsOperational(state, entity))
            {
                return entity;
            }
            var resources = entity.Resources;
            if (moved)
            {
                resources = Consume(resources, ResourceKind.Fuel, FuelPerMovement);
            }
            resources = Consume(resources, ResourceKind.Stores, StoresPerActivity);
            var changed = moved || !resources.SequenceEqual(entity.Resources);
            return changed
                ? entity with
                {
                    RegionId = next,
                    Resources = resources,
                    DamageMilliUnits = moved ? Math.Min(1_000_000, entity.DamageMilliUnits + 10_000) : entity.DamageMilliUnits,
                    Revision = entity.Revision.Next(),
                }
                : entity;
        }).ToArray();

        if (next != order.DestinationRegionId)
        {
            return new GroupMovementResult(group.GroupId, group, order, entityUpdates);
        }

        var completed = order with { CompletedAt = dueAt, Revision = order.Revision.Next() };
        var released = group with { ActiveOrderId = null, Revision = group.Revision.Next() };
        return new GroupMovementResult(group.GroupId, released, completed, entityUpdates);
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
            var baseChanged = false;
            BaseState? home = null;
            if (entity.HomeBaseId is { } homeBaseId
                && bases.TryGetValue(homeBaseId, out var found)
                && found.RegionId == entity.RegionId
                && !IsActivelyOrdered(state, entity.EntityId))
            {
                home = found;
                (resources, home, baseChanged) = Replenish(resources, home);
                if (damage > 0)
                {
                    var available = Get(home.Resources, ResourceKind.SpareParts);
                    var repaired = Math.Min(Math.Min(available, RepairPerActivity), damage);
                    if (repaired > 0)
                    {
                        damage -= checked((int)repaired);
                        home = home with
                        {
                            Resources = Set(home.Resources, ResourceKind.SpareParts, available - repaired),
                        };
                        baseChanged = true;
                    }
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
            damage = Math.Min(1_000_000, checked(damage + shortageDamage + randomDamage));
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
                - (Get(resources, ResourceKind.Stores) == 0 ? 200 : 0),
                0,
                1_000);
            var changed = damage != entity.DamageMilliUnits
                || readiness != entity.ReadinessMilliUnits
                || lifecycle != entity.Lifecycle
                || !resources.SequenceEqual(entity.Resources);
            if (changed)
            {
                entities = entities.SetItem(entity.EntityId, entity with
                {
                    Resources = resources,
                    DamageMilliUnits = damage,
                    ReadinessMilliUnits = readiness,
                    Lifecycle = lifecycle,
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
        var outcomes = new List<OperationalOutcome>();

        foreach (var region in state.Regions.Keys)
        {
            var participants = entities.Values
                .Where(item => item.RegionId == region && IsOperational(state with { Entities = entities }, item))
                .OrderBy(item => item.EntityId)
                .ToArray();
            if (participants.Select(item => item.CoalitionId).Distinct().Count() < 2)
            {
                continue;
            }

            foreach (var originalActor in participants)
            {
                var actor = entities[originalActor.EntityId];
                if (!CanAttack(state, actor))
                {
                    continue;
                }

                var targets = participants
                    .Select(item => entities[item.EntityId])
                    .Where(item => item.CoalitionId != actor.CoalitionId && CanBeAttacked(state, item))
                    .OrderBy(item => item.EntityId)
                    .ToArray();
                if (targets.Length == 0)
                {
                    continue;
                }

                var target = targets[KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt, "engagement-target") % targets.Length];
                var available = Get(actor.Resources, ResourceKind.Munitions);
                var expenditure = Math.Min(available, actor.SimulationDomain == SimulationDomain.LandAbstract ? 15_000 : 30_000);
                if (expenditure <= 0)
                {
                    continue;
                }

                actor = actor with
                {
                    Resources = Set(actor.Resources, ResourceKind.Munitions, available - expenditure),
                    Revision = actor.Revision.Next(),
                };
                entities = entities.SetItem(actor.EntityId, actor);
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.WeaponExpenditure, region,
                    actor.EntityId, target.EntityId, expenditure, 0, "Operational weapons employment"));

                var hitChance = Math.Clamp(250 + actor.ReadinessMilliUnits / 2
                    - target.ReadinessMilliUnits / 8
                    + (actor.SimulationDomain == SimulationDomain.Air ? 100 : 0), 100, 850);
                var roll = KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt, $"engagement-hit-{target.EntityId}") % 1_000;
                if (roll >= hitChance)
                {
                    continue;
                }

                var damage = 45_000 + KeyedValue(state.Metadata.Seed, actor.EntityId.Value, dueAt,
                    $"engagement-damage-{target.EntityId}") % 115_001;
                var applied = Math.Min(damage, 1_000_000 - target.DamageMilliUnits);
                var totalDamage = target.DamageMilliUnits + applied;
                var lifecycle = totalDamage >= 1_000_000
                    ? EntityLifecycle.Destroyed
                    : totalDamage >= 800_000 ? EntityLifecycle.Disabled : target.Lifecycle;
                target = target with
                {
                    DamageMilliUnits = totalDamage,
                    ReadinessMilliUnits = Math.Clamp(1_000 - totalDamage / 1_000, 0, 1_000),
                    Lifecycle = lifecycle,
                    Revision = target.Revision.Next(),
                };
                entities = entities.SetItem(target.EntityId, target);
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Damage, region,
                    actor.EntityId, target.EntityId, 0, applied, "Operational combat damage"));
                if (lifecycle == EntityLifecycle.Destroyed)
                {
                    outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Destruction, region,
                        actor.EntityId, target.EntityId, 0, applied, "Entity destroyed by operational resolution"));
                }
            }
        }

        var combatState = state with { Entities = entities, Groups = groups, Orders = orders };
        foreach (var group in groups.Values.OrderBy(item => item.GroupId))
        {
            var activeMembers = group.Members.Select(id => entities[id])
                .Where(item => item.Lifecycle is not (EntityLifecycle.Destroyed or EntityLifecycle.Retired))
                .ToArray();
            if (activeMembers.Length == 0 || activeMembers.Max(item => item.DamageMilliUnits) < 600_000
                || activeMembers.Any(item => HasActiveTacticalAuthority(state, item.EntityId)))
            {
                continue;
            }

            var origin = activeMembers[0].RegionId;
            var destination = activeMembers.Select(item => item.HomeBaseId)
                .Where(item => item is not null)
                .Select(item => state.Bases[item!.Value].RegionId)
                .FirstOrDefault(item => item != origin);
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
            orders = orders.SetItem(orderId, new OrderState(orderId, group.GroupId, OrderKind.Withdraw, destination,
                dueAt, new CampaignTime(checked(dueAt.Quanta + 60)), null, new EntityRevision(1)));
            groups = groups.SetItem(group.GroupId, group with { ActiveOrderId = orderId, Revision = group.Revision.Next() });
            outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Withdrawal, origin,
                activeMembers.OrderBy(item => item.EntityId).First().EntityId, null, 0, 0,
                "Whole task force ordered to withdraw"));
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
                .Where(item => item.CoalitionId == observer && CanBeAttacked(state, item))
                .ToArray();
            foreach (var contact in state.Entities.Values.Where(item => item.CoalitionId != observer))
            {
                var key = new IntelligenceKey(observer, contact.EntityId);
                var score = sensors.Select(sensor => DetectionScore(state, sensor, contact)).DefaultIfEmpty(0).Max();
                var combatObserved = engagementOutcomes.Any(item => item.RegionId == contact.RegionId
                    && (item.ActorEntityId == contact.EntityId || item.TargetEntityId == contact.EntityId));
                if (combatObserved)
                {
                    score = Math.Max(score, 950);
                }
                var detected = score > 0
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
                    Math.Clamp(contact.Coordinate.LongitudeMicrodegrees + longitudeJitter, -180_000_000, 180_000_000));
                var classification = confidence == IntelligenceConfidence.Possible
                    ? ContactClassification.Unknown : Classify(contact);
                var revision = intelligence.TryGetValue(key, out var current)
                    ? current.Revision.Next() : new EntityRevision(1);
                intelligence = intelligence.SetItem(key, new IntelligenceEstimate(
                    key, contact.RegionId, coordinate, confidence, dueAt, radius, classification, revision));
                outcomes.Add(Outcome(state, dueAt, OperationalOutcomeKind.Detection, contact.RegionId,
                    sensors.OrderBy(item => item.EntityId).FirstOrDefault()?.EntityId ?? contact.EntityId,
                    contact.EntityId, 0, 0, $"{confidence} {classification} contact"));
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

    private static bool CanAttack(CampaignState state, EntityState entity) =>
        IsOperational(state, entity)
        && entity.SimulationDomain != SimulationDomain.Logistics
        && Get(entity.Resources, ResourceKind.Munitions) > 0;

    private static bool CanBeAttacked(CampaignState state, EntityState entity) =>
        entity.Lifecycle is not (EntityLifecycle.Destroyed or EntityLifecycle.Retired)
        && entity.Fidelity != FidelityState.Suspended
        && !HasActiveTacticalAuthority(state, entity.EntityId);

    private static int DetectionScore(CampaignState state, EntityState sensor, EntityState target)
    {
        var score = sensor.RegionId == target.RegionId
            ? 800
            : Neighbors(state, sensor.RegionId).Contains(target.RegionId) ? 250 : 0;
        if (score == 0)
        {
            return 0;
        }
        if (sensor.SimulationDomain == SimulationDomain.Air)
        {
            score += 150;
        }
        if (target.SimulationDomain == SimulationDomain.LandAbstract)
        {
            score += 100;
        }
        if (state.Weather.TryGetValue(target.RegionId, out var weather))
        {
            score -= weather.Visibility.Value < 10_000 ? 200 : weather.Visibility.Value < 25_000 ? 75 : 0;
            score -= weather.SeaStateMilliUnits >= 6_000 ? 75 : 0;
        }
        return Math.Clamp(score, 0, 950);
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
            && item.Lifecycle is AssignmentLifecycle.Preparing or AssignmentLifecycle.Prepared
                or AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing or AssignmentLifecycle.Quiesced);

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
        item.ReadinessMilliUnits, item.DamageMilliUnits, item.Revision);

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
        item.Classification, item.Revision);

    private static ScheduledCampaignActivity ToContract(ScheduledActivityState item) => new(
        item.ActivityId, item.Kind, item.Subject, item.DueAt, item.Revision);

    private sealed record GroupMovementResult(
        GroupId GroupId,
        GroupState Group,
        OrderState? Order,
        IReadOnlyList<EntityState> Entities);

    private sealed record TickResult(CampaignState State, IReadOnlyList<OperationalOutcome> Outcomes);
}
