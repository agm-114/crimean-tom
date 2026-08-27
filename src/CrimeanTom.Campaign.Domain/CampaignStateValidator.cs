using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.Domain;

public static class CampaignStateValidator
{
    public static void Validate(CampaignState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = CampaignState.Create(state.Metadata);
        if (state.Time.Quanta < 0 || state.Head.Value < 0 || state.CampaignRevision.Value != state.Head.Value)
        {
            throw Invalid("campaign-cursor", "Campaign time, head, or revision is invalid.");
        }

        foreach (var (key, region) in state.Regions)
        {
            if (key != region.RegionId || region.Revision.Value <= 0)
            {
                throw Invalid("region-identity", "A region key, identity, or revision is invalid.");
            }
            CampaignInvariants.RequireText(region.Name, nameof(region.Name));
            if (region.Neighbors.Any(id => id == key || !state.Regions.ContainsKey(id)))
            {
                throw Invalid("region-neighbor", "A region neighbor is missing or self-referential.");
            }
        }

        foreach (var (key, campaignBase) in state.Bases)
        {
            if (key != campaignBase.BaseId || campaignBase.Revision.Value <= 0
                || !state.Regions.ContainsKey(campaignBase.RegionId))
            {
                throw Invalid("base-identity", "A base key, revision, or region is invalid.");
            }
            CampaignInvariants.RequireId(campaignBase.CoalitionId);
            CampaignInvariants.RequireText(campaignBase.Name, nameof(campaignBase.Name));
            ValidateCoordinate(campaignBase.Coordinate);
            ValidateResources(campaignBase.Resources);
        }

        foreach (var (key, entity) in state.Entities)
        {
            if (key != entity.EntityId || entity.Revision.Value <= 0 || !state.Regions.ContainsKey(entity.RegionId)
                || !Enum.IsDefined(entity.Lifecycle) || !Enum.IsDefined(entity.Fidelity)
                || !Enum.IsDefined(entity.SimulationDomain) || !Enum.IsDefined(entity.MaximumFidelity)
                || entity.MaximumFidelity == FidelityState.Suspended
                || entity.Fidelity != FidelityState.Suspended && entity.Fidelity > entity.MaximumFidelity)
            {
                throw Invalid("entity-identity", "An entity key, revision, region, lifecycle, or fidelity is invalid.");
            }
            CampaignInvariants.RequireId(entity.CoalitionId);
            CampaignInvariants.RequireText(entity.Name, nameof(entity.Name));
            CampaignInvariants.RequireText(entity.CatalogUnitId, nameof(entity.CatalogUnitId));
            CampaignInvariants.RequireText(entity.CatalogLoadoutId, nameof(entity.CatalogLoadoutId));
            CampaignInvariants.RequireText(entity.ClassifiedTruthTag, nameof(entity.ClassifiedTruthTag));
            ValidateCoordinate(entity.Coordinate);
            ValidateResources(entity.Resources);
            foreach (var weapon in entity.OperationalWeapons)
            {
                CampaignInvariants.RequireText(weapon.AmmunitionId, nameof(weapon.AmmunitionId));
                if (!Enum.IsDefined(weapon.TargetClassification)
                    || weapon.TargetClassification is ContactClassification.Unknown or ContactClassification.Logistics
                    || weapon.MaximumRange.Value <= 0
                    || weapon.MinimumRange.Value < 0
                    || weapon.MinimumRange.Value > weapon.MaximumRange.Value
                    || !Enum.IsDefined(weapon.Kind)
                    || !Enum.IsDefined(weapon.Guidance)
                    || !Enum.IsDefined(weapon.MechanicsSource)
                    || weapon.SpeedMetersPerSecond < 0
                    || weapon.SeekerRange.Value < 0
                    || weapon.LaunchReliabilityMilliUnits is < 0 or > 1_000
                    || weapon.KillProbabilityMilliUnits is < 0 or > 1_000
                    || weapon.WarheadPower < 0
                    || weapon.MechanicsSource == WeaponMechanicsSource.SeaPowerCatalog
                        && weapon.Kind != OrdnanceKind.Projectile
                        && weapon.SpeedMetersPerSecond <= 0)
                {
                    throw Invalid("entity-operational-weapon", "An entity operational weapon capability is invalid.");
                }
            }
            ValidateMagazines(entity);
            _ = HeadingMilliDegrees.FromMilliDegrees(entity.Heading.Value);
            if (entity.Telegraph is < -1 or > 5
                || entity.ReadinessMilliUnits is < 0 or > 1_000
                || entity.DamageMilliUnits is < 0 or > 1_000_000
                || entity.Analogue.AltitudeOrDepthMeters is < -400 or > 20_000
                || entity.Analogue.FireMilliUnits is < 0 or > 1_000_000
                || entity.Analogue.FloodMilliUnits is < 0 or > 1_000_000
                || entity.Analogue.PropulsionIntegrityMilliUnits is < 0 or > 1_000
                || entity.Analogue.SensorIntegrityMilliUnits is < 0 or > 1_000
                || entity.Analogue.WeaponIntegrityMilliUnits is < 0 or > 1_000
                || !Enum.IsDefined(entity.Analogue.Emcon)
                || !Enum.IsDefined(entity.Analogue.Roe)
                || !Enum.IsDefined(entity.Analogue.Alert)
                || entity.Analogue.MagazineIntegrityMilliUnits is < 0 or > 1_000
                || entity.Analogue.FlightDeckIntegrityMilliUnits is < 0 or > 1_000
                || entity.Analogue.CrewMilliUnits is < 0 or > 1_000
                || entity.Analogue.PortIntegrityMilliUnits is < 0 or > 1_000
                || entity.Analogue.StarboardIntegrityMilliUnits is < 0 or > 1_000
                || entity.Analogue.EcmMilliUnits is < 0 or > 1_000
                || entity.Analogue.CountermeasureMilliUnits is < 0 or > 1_000
                || entity.Analogue.WeaponReadyMilliUnits is < 0 or > 1_000
                || entity.Analogue.HostedAircraft is < 0 or > 64
                || entity.Analogue.SnorkelCycle is < 0 or > 9)
            {
                throw Invalid("entity-condition", "An entity condition is outside its canonical range.");
            }
            if (entity.HomeBaseId is { } homeBaseId
                && (!state.Bases.TryGetValue(homeBaseId, out var homeBase) || homeBase.CoalitionId != entity.CoalitionId))
            {
                throw Invalid("entity-home-base", "An entity home base is missing or belongs to another coalition.");
            }
            if (entity.HostEntityId is { } hostEntityId
                && (hostEntityId == entity.EntityId
                    || !state.Entities.TryGetValue(hostEntityId, out var host)
                    || host.CoalitionId != entity.CoalitionId
                    || host.SimulationDomain != SimulationDomain.Maritime
                    || host.HostEntityId is not null
                    || entity.SimulationDomain is not (SimulationDomain.Air or SimulationDomain.RotaryWing)))
            {
                throw Invalid(
                    "entity-host",
                    "A hosted aircraft requires a top-level maritime host in the same coalition.");
            }
        }

        var groupedMembers = new HashSet<EntityId>();
        foreach (var (key, group) in state.Groups)
        {
            if (key != group.GroupId || group.Revision.Value <= 0 || group.Members.Count == 0)
            {
                throw Invalid("group-identity", "A group key, revision, or membership is invalid.");
            }
            CampaignInvariants.RequireId(group.CoalitionId);
            CampaignInvariants.RequireText(group.Name, nameof(group.Name));
            foreach (var memberId in group.Members)
            {
                if (!groupedMembers.Add(memberId)
                    || !state.Entities.TryGetValue(memberId, out var member)
                    || member.CoalitionId != group.CoalitionId)
                {
                    throw Invalid("group-member", "A group member is missing, duplicated, or belongs to another coalition.");
                }
            }
            if (group.ActiveOrderId is { } activeOrderId
                && (!state.Orders.TryGetValue(activeOrderId, out var activeOrder) || activeOrder.GroupId != group.GroupId))
            {
                throw Invalid("group-order", "A group's active order does not belong to that group.");
            }
        }
        foreach (var entity in state.Entities.Values.Where(item => item.HostEntityId is not null))
        {
            var childGroup = state.Groups.Values.FirstOrDefault(group => group.Members.Contains(entity.EntityId));
            var hostGroup = state.Groups.Values.FirstOrDefault(group => group.Members.Contains(entity.HostEntityId!.Value));
            if (childGroup?.GroupId != hostGroup?.GroupId)
            {
                throw Invalid(
                    "entity-host-group",
                    "A hosted aircraft and its maritime host must share one canonical group owner.");
            }
        }

        foreach (var (key, order) in state.Orders)
        {
            if (key != order.OrderId || order.Revision.Value <= 0 || !state.Groups.ContainsKey(order.GroupId)
                || !state.Regions.ContainsKey(order.DestinationRegionId) || !Enum.IsDefined(order.Kind)
                || order.StartsAt.Quanta < 0
                || order.MustCompleteBy is { } deadline && deadline.Quanta < order.StartsAt.Quanta)
            {
                throw Invalid("order", "An order identity, relationship, time, kind, or revision is invalid.");
            }
            if (order.CompletedAt is { } completed
                && (completed.Quanta < order.StartsAt.Quanta || completed.Quanta > state.Time.Quanta))
            {
                throw Invalid("order-completion", "An order completion time is outside the canonical campaign interval.");
            }
        }

        foreach (var (key, objective) in state.StrategicObjectives)
        {
            if (key != objective.ObjectiveId
                || objective.Revision.Value <= 0
                || objective.Epoch <= 0
                || objective.CreatedAt.Quanta < 0
                || objective.CreatedAt.Quanta > state.Time.Quanta
                || objective.PriorityMilliUnits is < 0 or > 1_000_000
                || !Enum.IsDefined(objective.Kind)
                || !state.Regions.ContainsKey(objective.RegionId))
            {
                throw Invalid("strategic-objective", "A strategic objective is invalid.");
            }
            CampaignInvariants.RequireId(objective.CoalitionId);
        }

        foreach (var (key, commitment) in state.GroupCommitments)
        {
            if (key != commitment.GroupId
                || commitment.Revision.Value <= 0
                || !state.Groups.ContainsKey(key)
                || commitment.ExpiresAt.Quanta < 0
                || commitment.LatchReleaseAt.Quanta < 0
                || commitment.ObjectiveId is { } objectiveId
                    && (!state.StrategicObjectives.TryGetValue(objectiveId, out var objective)
                        || objective.CoalitionId != state.Groups[key].CoalitionId))
            {
                throw Invalid("group-commitment", "A strategic group commitment is invalid.");
            }
        }

        var packagedGroups = new HashSet<GroupId>();
        foreach (var (key, package) in state.OperationalPackages)
        {
            if (key != package.PackageId
                || package.Revision.Value <= 0
                || package.Members.Count < 2
                || !state.StrategicObjectives.TryGetValue(package.ObjectiveId, out var objective)
                || objective.CoalitionId != package.CoalitionId
                || objective.RegionId != package.RegionId
                || package.AssemblyAt.Quanta < 0
                || package.AssemblyAt.Quanta > state.Time.Quanta
                || package.ExecuteAt.Quanta < package.AssemblyAt.Quanta
                || package.ExpiresAt.Quanta < package.ExecuteAt.Quanta)
            {
                throw Invalid("operational-package", "An operational package identity, objective, timing, or membership is invalid.");
            }
            CampaignInvariants.RequireId(package.CoalitionId);
            foreach (var (groupId, member) in package.Members)
            {
                if (groupId != member.GroupId
                    || !packagedGroups.Add(groupId)
                    || !Enum.IsDefined(member.Role)
                    || member.StartsAt.Quanta < package.AssemblyAt.Quanta
                    || member.StartsAt.Quanta > package.ExecuteAt.Quanta
                    || !state.Groups.TryGetValue(groupId, out var group)
                    || group.CoalitionId != package.CoalitionId
                    || !group.Members.Any(entityId => state.Entities[entityId].Lifecycle
                        is not (EntityLifecycle.Disabled or EntityLifecycle.Destroyed or EntityLifecycle.Retired))
                    || group.Members.Any(entityId => state.Entities[entityId].SimulationDomain == SimulationDomain.Installation)
                    || !state.GroupCommitments.TryGetValue(groupId, out var commitment)
                    || commitment.WithdrawLatched
                    || commitment.ObjectiveId != package.ObjectiveId
                    || commitment.ExpiresAt.Quanta < package.ExpiresAt.Quanta)
                {
                    throw Invalid("operational-package-member", "An operational package member is invalid, duplicated, fixed, withdrawing, or not committed to its objective.");
                }
            }
        }

        foreach (var (key, strategy) in state.CoalitionStrategies)
        {
            if (key != strategy.CoalitionId
                || strategy.Revision.Value <= 0
                || strategy.Epoch <= 0
                || strategy.NextDirectorAt.Quanta <= state.Time.Quanta)
            {
                throw Invalid("coalition-strategy", "A coalition strategy cursor is invalid.");
            }
            CampaignInvariants.RequireId(key);
        }

        foreach (var (key, estimate) in state.Intelligence)
        {
            if (key != estimate.Key || estimate.Revision.Value <= 0 || !state.Entities.ContainsKey(key.ContactEntityId)
                || !state.Regions.ContainsKey(estimate.EstimatedRegionId) || !Enum.IsDefined(estimate.Confidence)
                || !Enum.IsDefined(estimate.Classification)
                || !Enum.IsDefined(estimate.Channel)
                || !Enum.IsDefined(estimate.Quality)
                || estimate.ObservedHeadingMilliDegrees is < 0 or >= 360_000
                || estimate.ObservedSpeedMetersPerTick < 0
                || estimate.UncertaintyRadiusMeters is < 0 or > 2_000_000
                || estimate.ObservedAt.Quanta < 0 || estimate.ObservedAt.Quanta > state.Time.Quanta)
            {
                throw Invalid("intelligence", "An intelligence estimate is invalid.");
            }
            CampaignInvariants.RequireId(key.ObserverCoalitionId);
            ValidateCoordinate(estimate.EstimatedCoordinate);
        }

        foreach (var (key, weather) in state.Weather)
        {
            if (key != weather.RegionId || weather.Revision.Value <= 0 || !state.Regions.ContainsKey(key)
                || weather.SeaStateMilliUnits is < 0 or > 9_000)
            {
                throw Invalid("weather", "A weather record is invalid.");
            }
            _ = HeadingMilliDegrees.FromMilliDegrees(weather.WindDirection.Value);
            _ = SpeedMillimetersPerSecond.FromMillimetersPerSecond(weather.WindSpeed.Value);
            _ = DistanceMeters.FromMeters(weather.Visibility.Value);
        }

        var primaryEntities = new HashSet<EntityId>();
        foreach (var (key, assignment) in state.Assignments)
        {
            if (key != assignment.AssignmentId || assignment.Revision.Value <= 0
                || assignment.AuthorityEpoch.Value <= 0 || assignment.EffectiveAt.Quanta < 0
                || !state.Entities.ContainsKey(assignment.EntityId) || !Enum.IsDefined(assignment.Role)
                || !Enum.IsDefined(assignment.Lifecycle) || assignment.BatchSize <= 0
                || (string.IsNullOrEmpty(assignment.BatchId) && assignment.BatchSize != 1)
                || (!string.IsNullOrEmpty(assignment.BatchId)
                    && assignment.BatchSize < 2
                    && !CampaignAssignmentKinds.IsOperationalSim(assignment))
                || assignment.WorkerClientInstanceId is null
                || assignment.WorkerClientInstanceId.Length > 256
                || assignment.WorkerClientInstanceId.Any(char.IsControl))
            {
                throw Invalid("assignment", "An assignment identity, relationship, role, time, epoch, or revision is invalid.");
            }
            if (assignment.Role == AssignmentRole.Primary
                && assignment.Lifecycle != AssignmentLifecycle.Released
                && !primaryEntities.Add(assignment.EntityId))
            {
                throw Invalid("duplicate-primary", "An entity has multiple primary assignments.");
            }
            if (CampaignAssignmentKinds.IsOperationalSim(assignment)
                && (assignment.Role != AssignmentRole.Primary
                    || state.Entities[assignment.EntityId].Fidelity == FidelityState.Tactical))
            {
                throw Invalid(
                    "operational-assignment",
                    "An Operational worker assignment must be Primary and cannot cover Tactical fidelity.");
            }
            if (assignment.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing
                    or AssignmentLifecycle.Quiesced
                && !CampaignAssignmentKinds.IsOperationalSim(assignment)
                && state.Entities[assignment.EntityId].Fidelity != FidelityState.Tactical)
            {
                throw Invalid("active-assignment-fidelity", "An active tactical assignment requires tactical entity fidelity.");
            }
            if (assignment.Lifecycle == AssignmentLifecycle.Prepared)
            {
                var entityFidelity = state.Entities[assignment.EntityId].Fidelity;
                var shadowDestinationWithTactical =
                    assignment.Role == AssignmentRole.Shadow && entityFidelity == FidelityState.Tactical;
                if (!shadowDestinationWithTactical
                    && (string.IsNullOrWhiteSpace(assignment.BatchId) || entityFidelity == FidelityState.Tactical))
                {
                    throw Invalid("prepared-assignment", "A prepared batch assignment requires a batch ID and a non-tactical canonical entity.");
                }
            }
        }

        if (state.SchedulerPolicy is { } policy)
        {
            CampaignInvariants.RequireText(policy.Health, nameof(policy.Health));
            CampaignInvariants.RequireText(policy.Eligibility, nameof(policy.Eligibility));
            CampaignInvariants.RequireText(policy.RequestHash, nameof(policy.RequestHash));
            if (policy.Health is not ("Healthy" or "Pressured" or "Overloaded" or "Draining" or "Quarantined" or "Offline")
                || policy.Eligibility is not ("Eligible" or "DrainOnly" or "Disabled" or "Quarantined")
                || policy.Revision.Value <= 0 || policy.Revision.Value > state.CampaignRevision.Value
                || policy.EffectiveAt.Quanta < 0 || policy.EffectiveAt.Quanta > state.Time.Quanta
                || policy.TacticalEntityCapacity is < 0 or > 10_000
                || policy.TacticalReserveCapacity is < 0 or > 10_000
                || policy.TacticalReserveCapacity > policy.TacticalEntityCapacity
                || policy.BacklogDepth is < 0 or > 1_000_000
                || policy.StableSampleCount is < 1 or > 1_000_000
                || policy.RequestHash.Length != 64 || policy.RequestHash.Any(character => !Uri.IsHexDigit(character))
                || policy.ObservedEntityIds.Concat(policy.ControlledEntityIds).Any(id => !state.Entities.ContainsKey(id)))
            {
                throw Invalid("scheduler-policy", "The persisted scheduler policy is invalid or references missing entities.");
            }
        }

        foreach (var (key, activity) in state.ScheduledActivities)
        {
            if (key != activity.ActivityId || activity.Revision.Value <= 0
                || !Enum.IsDefined(activity.Kind) || !Enum.IsDefined(activity.Subject.Kind)
                || activity.Subject.Value == Guid.Empty || activity.DueAt.Quanta <= state.Time.Quanta)
            {
                throw Invalid("scheduled-activity", "A scheduled activity identity, kind, subject, revision, or time is invalid.");
            }
            var subjectExists = activity.Subject.Kind switch
            {
                CampaignSubjectKind.Campaign => activity.Subject.Value == state.Metadata.CampaignId.Value,
                CampaignSubjectKind.Region => state.Regions.ContainsKey(new RegionId(activity.Subject.Value)),
                CampaignSubjectKind.Base => state.Bases.ContainsKey(new BaseId(activity.Subject.Value)),
                CampaignSubjectKind.Entity => state.Entities.ContainsKey(new EntityId(activity.Subject.Value)),
                CampaignSubjectKind.Group => state.Groups.ContainsKey(new GroupId(activity.Subject.Value)),
                CampaignSubjectKind.Assignment => state.Assignments.ContainsKey(new AssignmentId(activity.Subject.Value)),
                CampaignSubjectKind.Order => state.Orders.ContainsKey(new OrderId(activity.Subject.Value)),
                CampaignSubjectKind.ScheduledActivity => state.ScheduledActivities.ContainsKey(new ScheduledActivityId(activity.Subject.Value)),
                CampaignSubjectKind.Intelligence or CampaignSubjectKind.Weather => true,
                _ => false,
            };
            if (!subjectExists)
            {
                throw Invalid("scheduled-subject", "A scheduled activity references a missing canonical subject.");
            }
        }

        foreach (var outcome in state.RecentOperationalOutcomes)
        {
            if (string.IsNullOrWhiteSpace(outcome.OutcomeId)
                || outcome.OccurredAt.Quanta < 0 || outcome.OccurredAt.Quanta > state.Time.Quanta
                || !Enum.IsDefined(outcome.Kind)
                || !state.Regions.ContainsKey(outcome.RegionId)
                || !state.Entities.ContainsKey(outcome.ActorEntityId)
                || outcome.TargetEntityId is { } target && !state.Entities.ContainsKey(target)
                || outcome.MunitionsExpendedMilliUnits < 0
                || outcome.DamageInflictedMilliUnits is < 0 or > 1_000_000
                || string.IsNullOrWhiteSpace(outcome.Detail))
            {
                throw Invalid("operational-outcome", "An operational outcome is invalid.");
            }
        }

        var ordnanceIds = new HashSet<string>(StringComparer.Ordinal);
        var coveredTargets = new HashSet<EntityId>();
        var guidanceLoad = new Dictionary<EntityId, int>();
        foreach (var ordnance in state.InFlight)
        {
            var shooterExists = state.Entities.TryGetValue(ordnance.ShooterEntityId, out var shooter);
            var targetExists = state.Entities.TryGetValue(ordnance.TargetEntityId, out var target);
            var shooterLoad = guidanceLoad.GetValueOrDefault(ordnance.ShooterEntityId) + 1;
            if (!ordnanceIds.Add(ordnance.OrdnanceId)
                || !coveredTargets.Add(ordnance.TargetEntityId)
                || shooterLoad > 2
                || string.IsNullOrWhiteSpace(ordnance.OrdnanceId)
                || ordnance.OrdnanceId.Length > 128
                || string.IsNullOrWhiteSpace(ordnance.AmmunitionId)
                || ordnance.AmmunitionId.Length > 256
                || !shooterExists
                || !targetExists
                || shooter!.CoalitionId != ordnance.ShooterCoalitionId
                || target!.CoalitionId == ordnance.ShooterCoalitionId
                || !Enum.IsDefined(ordnance.Stage)
                || !Enum.IsDefined(ordnance.Guidance)
                || !Enum.IsDefined(ordnance.TargetClassification)
                || ordnance.TargetClassification is ContactClassification.Unknown or ContactClassification.Logistics
                || !Enum.IsDefined(ordnance.Kind)
                || ordnance.RemainingMeters <= 0
                || ordnance.SpeedMetersPerTick is <= 0 or > 1_000_000
                || ordnance.InsDriftMilliUnits is < 0 or > 1_000
                || ordnance.HeadingMilliDegrees is < 0 or >= 360_000
                || ordnance.LaunchedAt.Quanta < 0
                || ordnance.LaunchedAt.Quanta > state.Time.Quanta
                || ordnance.SeekerDelayTicks is < 0 or > 1_000
                || ordnance.LostGuidanceTicks is < 0 or > 1_000)
            {
                throw Invalid("in-flight-ordnance", "In-flight analogue ordnance is invalid.");
            }
            guidanceLoad[ordnance.ShooterEntityId] = shooterLoad;
            ValidateCoordinate(ordnance.Coordinate);
        }
    }

    private static void ValidateCoordinate(GeoCoordinate coordinate) =>
        _ = GeoCoordinate.FromMicrodegrees(coordinate.LatitudeMicrodegrees, coordinate.LongitudeMicrodegrees);

    private static void ValidateResources(IEnumerable<KeyValuePair<ResourceKind, ResourceAmount>> resources)
    {
        if (resources.Any(item => !Enum.IsDefined(item.Key) || item.Value.MilliUnits < 0))
        {
            throw Invalid("resource", "A resource kind or amount is invalid.");
        }
    }

    private static void ValidateMagazines(EntityState entity)
    {
        var capacities = entity.OperationalWeapons
            .Where(weapon => weapon.MagazineCapacityRounds > 0)
            .GroupBy(weapon => weapon.AmmunitionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Max(weapon => weapon.MagazineCapacityRounds),
                StringComparer.Ordinal);
        if (entity.Magazines.Count != capacities.Count
            || entity.Magazines.Any(item =>
                string.IsNullOrWhiteSpace(item.Key)
                || item.Value < 0
                || !capacities.TryGetValue(item.Key, out var capacity)
                || item.Value > capacity))
        {
            throw Invalid(
                "entity-magazine",
                "Entity magazines must exactly match the catalog-counted ammunition and remain within capacity.");
        }
    }

    private static InvalidDataException Invalid(string code, string message) => new($"{code}: {message}");

}
