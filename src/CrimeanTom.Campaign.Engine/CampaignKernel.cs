using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

public static class CampaignKernel
{
    public static CampaignState Commit(CampaignState state, CampaignCommand command) =>
        Apply(state, Decide(state, command, state.Head.Next()));

    public static CommittedCampaignEvent Decide(
        CampaignState state,
        CampaignCommand command,
        CommitPosition commitPosition) =>
        Decide(state, command, commitPosition, workerCount: 1);

    public static CommittedCampaignEvent Decide(
        CampaignState state,
        CampaignCommand command,
        CommitPosition commitPosition,
        int workerCount)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        RequireEnvelope(state, command, commitPosition);

        var decision = command.Data switch
        {
            RegisterRegionCommand value => DecideRegion(state, command, value),
            RegisterBaseCommand value => DecideBase(state, command, value),
            RegisterEntityCommand value => DecideEntity(state, command, value),
            FormGroupCommand value => DecideGroup(state, command, value),
            IssueOrderCommand value => DecideOrder(state, command, value),
            ChangeFidelityCommand value => DecideFidelity(state, command, value),
            AssignAuthorityCommand value => DecideAssignment(state, command, value),
            ReportIntelligenceCommand value => DecideIntelligence(state, command, value),
            SetWeatherCommand value => DecideWeather(state, command, value),
            AdvanceClockCommand value => DecideClock(state, command, value),
            SimulateCampaignCommand value => DecideSimulation(state, command, value, workerCount),
            SetAssignmentLifecycleCommand value => DecideAssignmentLifecycle(state, command, value),
            ActivateTacticalAssignmentCommand value => DecideTacticalAssignmentActivation(state, command, value),
            ActivateTacticalIslandCommand value => DecideTacticalIslandActivation(state, command, value),
            ReleaseTacticalIslandCommand value => DecideTacticalIslandRelease(state, command, value),
            ApplyTacticalStateCommand value => DecideTacticalState(state, command, value),
            _ => throw Violation("unknown-command", "The command payload is not supported."),
        };

        return new CommittedCampaignEvent(
            CreateEventId(command.CampaignId, command.CommandId, commitPosition),
            command.CampaignId,
            commitPosition,
            decision.Subject,
            decision.Revision,
            state.Time,
            command.EffectiveAt,
            command.CommandId,
            decision.AssignmentId,
            decision.AuthorityEpoch,
            decision.Data);
    }

    public static CampaignState Apply(CampaignState state, CommittedCampaignEvent campaignEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(campaignEvent);
        if (campaignEvent.CampaignId != state.Metadata.CampaignId
            || campaignEvent.SchemaVersion != state.Metadata.SchemaVersion
            || campaignEvent.AlgorithmVersion != state.Metadata.AlgorithmVersion)
        {
            throw Violation("event-incompatible", "The event is not compatible with this campaign.");
        }

        if (campaignEvent.CommitPosition != state.Head.Next())
        {
            throw Violation("event-gap", "Events must be applied in contiguous commit order.");
        }

        var updated = campaignEvent.Data switch
        {
            RegionRegisteredEvent value => ApplyRegion(state, campaignEvent, value),
            BaseRegisteredEvent value => ApplyBase(state, campaignEvent, value),
            EntityRegisteredEvent value => ApplyEntity(state, campaignEvent, value),
            GroupFormedEvent value => ApplyGroup(state, campaignEvent, value),
            OrderIssuedEvent value => ApplyOrder(state, campaignEvent, value),
            FidelityChangedEvent value => ApplyFidelity(state, campaignEvent, value),
            AuthorityAssignedEvent value => ApplyAssignment(state, campaignEvent, value),
            IntelligenceReportedEvent value => ApplyIntelligence(state, campaignEvent, value),
            WeatherSetEvent value => ApplyWeather(state, campaignEvent, value),
            ClockAdvancedEvent value => state with { Time = value.Clock.NewTime },
            CampaignSimulatedEvent value => ApplySimulation(state, value),
            AssignmentLifecycleChangedEvent value => ApplyAssignmentLifecycle(state, campaignEvent, value),
            TacticalAssignmentActivatedEvent value => ApplyTacticalAssignmentActivation(state, campaignEvent, value),
            TacticalIslandActivatedEvent value => ApplyTacticalIslandActivation(state, campaignEvent, value),
            TacticalIslandReleasedEvent value => ApplyTacticalIslandRelease(state, campaignEvent, value),
            TacticalStateAppliedEvent value => ApplyTacticalState(state, campaignEvent, value),
            _ => throw Violation("unknown-event", "The event payload is not supported."),
        };

        return updated with
        {
            Head = campaignEvent.CommitPosition,
            CampaignRevision = state.CampaignRevision.Next(),
        };
    }

    private static Decision DecideRegion(CampaignState state, CampaignCommand command, RegisterRegionCommand value)
    {
        RequireSubject(command, CampaignSubject.Region(value.RegionId));
        RequireNew(command, state.Regions.ContainsKey(value.RegionId), "region-exists");
        CampaignInvariants.RequireId(value.RegionId);
        CampaignInvariants.RequireText(value.Name, nameof(value.Name));
        if (value.Neighbors.Distinct().Count() != value.Neighbors.Count
            || value.Neighbors.Any(id => id == value.RegionId || !state.Regions.ContainsKey(id)))
        {
            throw Violation("invalid-region-neighbor", "Every neighbor must already exist and differ from the new region.");
        }

        return Decision.Create(command.Subject, new RegionRegisteredEvent(value));
    }

    private static Decision DecideBase(CampaignState state, CampaignCommand command, RegisterBaseCommand value)
    {
        RequireSubject(command, CampaignSubject.Base(value.BaseId));
        RequireNew(command, state.Bases.ContainsKey(value.BaseId), "base-exists");
        RequireRegion(state, value.RegionId);
        RequireCoalitionOrAdministrator(command, value.CoalitionId);
        CampaignInvariants.RequireId(value.BaseId);
        CampaignInvariants.RequireId(value.CoalitionId);
        CampaignInvariants.RequireText(value.Name, nameof(value.Name));
        ValidateCoordinate(value.Coordinate);
        ValidateResources(value.Resources);
        return Decision.Create(command.Subject, new BaseRegisteredEvent(value));
    }

    private static Decision DecideEntity(CampaignState state, CampaignCommand command, RegisterEntityCommand value)
    {
        RequireSubject(command, CampaignSubject.Entity(value.EntityId));
        RequireNew(command, state.Entities.ContainsKey(value.EntityId), "entity-exists");
        RequireRegion(state, value.RegionId);
        RequireCoalitionOrAdministrator(command, value.CoalitionId);
        CampaignInvariants.RequireId(value.EntityId);
        CampaignInvariants.RequireId(value.CoalitionId);
        CampaignInvariants.RequireText(value.Name, nameof(value.Name));
        CampaignInvariants.RequireText(value.CatalogUnitId, nameof(value.CatalogUnitId));
        CampaignInvariants.RequireText(value.ClassifiedTruthTag, nameof(value.ClassifiedTruthTag));
        ValidateCoordinate(value.Coordinate);
        ValidateResources(value.Resources);
        if (!Enum.IsDefined(value.SimulationDomain)
            || !Enum.IsDefined(value.MaximumFidelity)
            || value.MaximumFidelity == FidelityState.Suspended
            || !CanUseFidelity(value.Fidelity, value.MaximumFidelity))
        {
            throw Violation("invalid-fidelity-ceiling", "An entity's simulation domain, fidelity, or fidelity ceiling is invalid.");
        }
        if (value.HomeBaseId is { } homeBaseId
            && (!state.Bases.TryGetValue(homeBaseId, out var homeBase) || homeBase.CoalitionId != value.CoalitionId))
        {
            throw Violation("invalid-home-base", "An entity's home base must exist in its coalition.");
        }
        return Decision.Create(command.Subject, new EntityRegisteredEvent(value));
    }

    private static Decision DecideGroup(CampaignState state, CampaignCommand command, FormGroupCommand value)
    {
        RequireSubject(command, CampaignSubject.Group(value.GroupId));
        RequireNew(command, state.Groups.ContainsKey(value.GroupId), "group-exists");
        RequireCoalitionOrAdministrator(command, value.CoalitionId);
        CampaignInvariants.RequireId(value.GroupId);
        CampaignInvariants.RequireText(value.Name, nameof(value.Name));
        if (value.Members.Count == 0 || value.Members.Distinct().Count() != value.Members.Count)
        {
            throw Violation("invalid-group-members", "A group needs distinct members.");
        }

        foreach (var memberId in value.Members)
        {
            if (!state.Entities.TryGetValue(memberId, out var member) || member.CoalitionId != value.CoalitionId)
            {
                throw Violation("invalid-group-member", "Every member must exist in the group's coalition.");
            }

            if (state.Groups.Values.Any(group => group.Members.Contains(memberId)))
            {
                throw Violation("member-already-grouped", "An entity can have only one canonical group owner.");
            }
        }

        return Decision.Create(command.Subject, new GroupFormedEvent(value));
    }

    private static Decision DecideOrder(CampaignState state, CampaignCommand command, IssueOrderCommand value)
    {
        RequireSubject(command, CampaignSubject.Group(value.GroupId));
        if (!state.Groups.TryGetValue(value.GroupId, out var group))
        {
            throw Violation("group-missing", "The ordered group does not exist.");
        }

        RequireRevision(command, group.Revision);
        RequireCoalitionOrAdministrator(command, group.CoalitionId);
        RequireRegion(state, value.DestinationRegionId);
        if (state.Orders.ContainsKey(value.OrderId)
            || value.StartsAt.Quanta < state.Time.Quanta
            || value.MustCompleteBy is { } deadline && deadline.Quanta < value.StartsAt.Quanta)
        {
            throw Violation("invalid-order", "The order ID or time window is invalid.");
        }

        return Decision.Create(command.Subject, new OrderIssuedEvent(value), group.Revision.Next());
    }

    private static Decision DecideFidelity(CampaignState state, CampaignCommand command, ChangeFidelityCommand value)
    {
        RequireSubject(command, CampaignSubject.Entity(value.EntityId));
        var entity = RequireEntity(state, value.EntityId);
        RequireRevision(command, entity.Revision);
        RequireCoalitionOrAdministrator(command, entity.CoalitionId);
        if ((entity.Lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Retired)
            && value.Fidelity != FidelityState.Operational)
        {
            throw Violation("terminal-entity", "A terminal entity may only return to operational fidelity.");
        }
        if (!CanUseFidelity(value.Fidelity, entity.MaximumFidelity))
        {
            throw Violation("fidelity-ceiling", "The requested fidelity exceeds the entity's simulation-domain ceiling.");
        }
        CampaignInvariants.RequireText(value.Reason, nameof(value.Reason));

        return Decision.Create(command.Subject, new FidelityChangedEvent(value), entity.Revision.Next());
    }

    private static Decision DecideAssignment(CampaignState state, CampaignCommand command, AssignAuthorityCommand value)
    {
        RequireSubject(command, CampaignSubject.Assignment(value.AssignmentId));
        RequireNew(command, state.Assignments.ContainsKey(value.AssignmentId), "assignment-exists");
        var entity = RequireEntity(state, value.EntityId);
        RequireCoalitionOrAdministrator(command, entity.CoalitionId);
        if (entity.MaximumFidelity != FidelityState.Tactical || value.AuthorityEpoch.Value <= 0)
        {
            throw Violation("invalid-assignment", "Authority preparation requires a tactically representable entity and a positive epoch.");
        }
        if (value.EffectiveAt != command.EffectiveAt)
        {
            throw Violation("assignment-time-mismatch", "Assignment and command effective times must match.");
        }
        if (!string.IsNullOrEmpty(value.BatchId))
        {
            CampaignInvariants.RequireText(value.BatchId, nameof(value.BatchId));
        }
        if (value.BatchSize <= 0 || (string.IsNullOrEmpty(value.BatchId) && value.BatchSize != 1)
            || (!string.IsNullOrEmpty(value.BatchId) && value.BatchSize < 2))
        {
            throw Violation("invalid-assignment-batch", "Assignment batch size and identity are inconsistent.");
        }

        if (value.Role == AssignmentRole.Primary
            && state.Assignments.Values.Any(item => item.EntityId == value.EntityId
                && item.Role == AssignmentRole.Primary
                && item.Lifecycle != AssignmentLifecycle.Released))
        {
            throw Violation("primary-already-assigned", "An entity can have only one canonical primary assignment.");
        }

        return Decision.Create(
            command.Subject,
            new AuthorityAssignedEvent(value),
            assignmentId: value.AssignmentId,
            authorityEpoch: value.AuthorityEpoch);
    }

    private static Decision DecideIntelligence(CampaignState state, CampaignCommand command, ReportIntelligenceCommand value)
    {
        RequireSubject(command, new CampaignSubject(CampaignSubjectKind.Intelligence, value.ContactEntityId.Value));
        if (command.Scope.CoalitionId != value.ObserverCoalitionId || !state.Entities.ContainsKey(value.ContactEntityId))
        {
            throw Violation("invalid-intelligence-scope", "Intelligence may only be reported to the command scope.");
        }

        RequireRegion(state, value.EstimatedRegionId);
        ValidateCoordinate(value.EstimatedCoordinate);
        var key = new IntelligenceKey(value.ObserverCoalitionId, value.ContactEntityId);
        var current = state.Intelligence.TryGetValue(key, out var estimate) ? estimate.Revision : EntityRevision.None;
        RequireRevision(command, current);
        if (value.ObservedAt.Quanta > command.EffectiveAt.Quanta)
        {
            throw Violation("future-observation", "An observation cannot originate after its effective time.");
        }
        if (value.UncertaintyRadiusMeters is < 0 or > 2_000_000
            || !Enum.IsDefined(value.Classification))
        {
            throw Violation("invalid-contact-uncertainty", "Contact uncertainty or classification is invalid.");
        }

        return Decision.Create(command.Subject, new IntelligenceReportedEvent(value), current.Next());
    }

    private static Decision DecideWeather(CampaignState state, CampaignCommand command, SetWeatherCommand value)
    {
        RequireSubject(command, new CampaignSubject(CampaignSubjectKind.Weather, value.RegionId.Value));
        RequireRegion(state, value.RegionId);
        var current = state.Weather.TryGetValue(value.RegionId, out var weather) ? weather.Revision : EntityRevision.None;
        RequireRevision(command, current);
        _ = HeadingMilliDegrees.FromMilliDegrees(value.WindDirectionMilliDegrees);
        _ = SpeedMillimetersPerSecond.FromMillimetersPerSecond(value.WindMillimetersPerSecond);
        _ = DistanceMeters.FromMeters(value.VisibilityMeters);
        if (value.SeaStateMilliUnits is < 0 or > 9_000)
        {
            throw Violation("invalid-sea-state", "Sea state must be between 0 and 9 fixed-point units.");
        }

        return Decision.Create(command.Subject, new WeatherSetEvent(value), current.Next());
    }

    private static Decision DecideClock(CampaignState state, CampaignCommand command, AdvanceClockCommand value)
    {
        RequireSubject(command, CampaignSubject.Campaign(state.Metadata.CampaignId));
        RequireRevision(command, state.CampaignRevision);
        if (value.NewTime.Quanta <= state.Time.Quanta || command.EffectiveAt != value.NewTime)
        {
            throw Violation("invalid-clock-advance", "Campaign time must advance and equal the event effective time.");
        }

        return Decision.Create(command.Subject, new ClockAdvancedEvent(value), state.CampaignRevision.Next());
    }

    private static Decision DecideSimulation(
        CampaignState state,
        CampaignCommand command,
        SimulateCampaignCommand value,
        int workerCount)
    {
        RequireSubject(command, CampaignSubject.Campaign(state.Metadata.CampaignId));
        RequireRevision(command, state.CampaignRevision);
        if (value.NewTime.Quanta <= state.Time.Quanta || command.EffectiveAt != value.NewTime)
        {
            throw Violation("invalid-simulation-advance", "Simulation time must advance and equal the event effective time.");
        }

        return Decision.Create(
            command.Subject,
            CampaignSimulation.Resolve(state, value.NewTime, workerCount),
            state.CampaignRevision.Next());
    }

    private static Decision DecideAssignmentLifecycle(
        CampaignState state,
        CampaignCommand command,
        SetAssignmentLifecycleCommand value)
    {
        RequireSubject(command, CampaignSubject.Assignment(value.AssignmentId));
        if (!state.Assignments.TryGetValue(value.AssignmentId, out var assignment))
        {
            throw Violation("assignment-missing", "The tactical assignment does not exist.");
        }
        RequireRevision(command, assignment.Revision);
        if (assignment.AuthorityEpoch != value.AuthorityEpoch || value.EffectiveAt != command.EffectiveAt)
        {
            throw Violation("assignment-fenced", "The assignment epoch or effective time is stale.");
        }
        var validTransition = (assignment.Lifecycle, value.Lifecycle) switch
        {
            (AssignmentLifecycle.Preparing, AssignmentLifecycle.Prepared) => true,
            (AssignmentLifecycle.Preparing, AssignmentLifecycle.Active) => true,
            (AssignmentLifecycle.Active, AssignmentLifecycle.Quiescing) => true,
            (AssignmentLifecycle.Quiescing, AssignmentLifecycle.Released) => true,
            (AssignmentLifecycle.Quiescing, AssignmentLifecycle.Quiesced) => true,
            (AssignmentLifecycle.Preparing, AssignmentLifecycle.Released) => true,
            (AssignmentLifecycle.Prepared, AssignmentLifecycle.Released) => true,
            (AssignmentLifecycle.Quiesced, AssignmentLifecycle.Released)
                when value.EffectiveAt.Quanta >= assignment.EffectiveAt.Quanta
                    + CampaignSchema.MaximumTacticalAssignmentQuanta => true,
            (AssignmentLifecycle.Active, AssignmentLifecycle.Released)
                when value.EffectiveAt.Quanta >= assignment.EffectiveAt.Quanta
                    + CampaignSchema.MaximumTacticalAssignmentQuanta => true,
            _ => false,
        };
        if (!validTransition)
        {
            throw Violation("assignment-transition", "The assignment lifecycle transition is invalid.");
        }
        return Decision.Create(
            command.Subject,
            new AssignmentLifecycleChangedEvent(value),
            assignment.Revision.Next(),
            value.AssignmentId,
            value.AuthorityEpoch);
    }

    private static Decision DecideTacticalAssignmentActivation(
        CampaignState state,
        CampaignCommand command,
        ActivateTacticalAssignmentCommand value)
    {
        RequireSubject(command, CampaignSubject.Assignment(value.AssignmentId));
        var assignment = state.Assignments.TryGetValue(value.AssignmentId, out var found)
            ? found
            : throw Violation("assignment-missing", "The tactical assignment does not exist.");
        RequireRevision(command, assignment.Revision);
        var entity = RequireEntity(state, value.EntityId);
        if (assignment.EntityId != value.EntityId
            || assignment.AuthorityEpoch != value.AuthorityEpoch
            || assignment.Lifecycle != AssignmentLifecycle.Preparing
            || value.EffectiveAt != command.EffectiveAt
            || entity.Revision != value.ExpectedEntityRevision
            || entity.MaximumFidelity != FidelityState.Tactical
            || entity.Lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Retired)
        {
            throw Violation("assignment-activation-fenced", "The prepared assignment or entity is stale or cannot become tactical.");
        }
        CampaignInvariants.RequireText(value.Reason, nameof(value.Reason));
        return Decision.Create(
            command.Subject,
            new TacticalAssignmentActivatedEvent(value),
            assignment.Revision.Next(),
            value.AssignmentId,
            value.AuthorityEpoch);
    }

    private static Decision DecideTacticalIslandActivation(
        CampaignState state,
        CampaignCommand command,
        ActivateTacticalIslandCommand value)
    {
        RequireSubject(command, CampaignSubject.Campaign(state.Metadata.CampaignId));
        RequireRevision(command, state.CampaignRevision);
        CampaignInvariants.RequireText(value.BatchId, nameof(value.BatchId));
        CampaignInvariants.RequireText(value.Reason, nameof(value.Reason));
        if (value.EffectiveAt != command.EffectiveAt || value.Members.Count < 2
            || value.Members.Select(item => item.AssignmentId).Distinct().Count() != value.Members.Count
            || value.Members.Select(item => item.EntityId).Distinct().Count() != value.Members.Count)
        {
            throw Violation("invalid-island-activation", "An island activation requires distinct members and matching effective time.");
        }
        foreach (var member in value.Members)
        {
            var assignment = state.Assignments.TryGetValue(member.AssignmentId, out var found)
                ? found
                : throw Violation("assignment-missing", "An island assignment does not exist.");
            var entity = RequireEntity(state, member.EntityId);
            if (assignment.EntityId != member.EntityId
                || assignment.AuthorityEpoch != member.AuthorityEpoch
                || assignment.Revision != member.ExpectedAssignmentRevision
                || assignment.Lifecycle != AssignmentLifecycle.Prepared
                || !string.Equals(assignment.BatchId, value.BatchId, StringComparison.Ordinal)
                || entity.Revision != member.ExpectedEntityRevision
                || entity.MaximumFidelity != FidelityState.Tactical
                || entity.Lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Retired)
            {
                throw Violation("island-activation-fenced", "A prepared island member is stale or cannot become tactical.");
            }
        }
        return Decision.Create(
            command.Subject,
            new TacticalIslandActivatedEvent(value),
            state.CampaignRevision.Next());
    }

    private static Decision DecideTacticalIslandRelease(
        CampaignState state,
        CampaignCommand command,
        ReleaseTacticalIslandCommand value)
    {
        RequireSubject(command, CampaignSubject.Campaign(state.Metadata.CampaignId));
        RequireRevision(command, state.CampaignRevision);
        CampaignInvariants.RequireText(value.BatchId, nameof(value.BatchId));
        CampaignInvariants.RequireText(value.Reason, nameof(value.Reason));
        if (value.EffectiveAt != command.EffectiveAt || value.Members.Count < 2
            || value.Members.Select(item => item.AssignmentId).Distinct().Count() != value.Members.Count
            || value.Members.Select(item => item.EntityId).Distinct().Count() != value.Members.Count)
        {
            throw Violation("invalid-island-release", "An island release requires distinct members and matching effective time.");
        }
        foreach (var member in value.Members)
        {
            var assignment = state.Assignments.TryGetValue(member.AssignmentId, out var found)
                ? found
                : throw Violation("assignment-missing", "An island assignment does not exist.");
            var entity = RequireEntity(state, member.EntityId);
            if (assignment.EntityId != member.EntityId
                || assignment.AuthorityEpoch != member.AuthorityEpoch
                || assignment.Revision != member.ExpectedAssignmentRevision
                || assignment.Lifecycle != AssignmentLifecycle.Quiesced
                || !string.Equals(assignment.BatchId, value.BatchId, StringComparison.Ordinal)
                || entity.Revision != member.ExpectedEntityRevision
                || entity.Fidelity != FidelityState.Tactical)
            {
                throw Violation("island-release-fenced", "A quiesced island member is stale or cannot be released.");
            }
        }
        return Decision.Create(
            command.Subject,
            new TacticalIslandReleasedEvent(value),
            state.CampaignRevision.Next());
    }

    private static Decision DecideTacticalState(
        CampaignState state,
        CampaignCommand command,
        ApplyTacticalStateCommand value)
    {
        RequireSubject(command, CampaignSubject.Entity(value.EntityId));
        var entity = RequireEntity(state, value.EntityId);
        RequireRevision(command, entity.Revision);
        var assignment = state.Assignments.TryGetValue(value.AssignmentId, out var found)
            ? found
            : throw Violation("assignment-missing", "The tactical assignment does not exist.");
        if (assignment.EntityId != value.EntityId
            || assignment.AuthorityEpoch != value.AuthorityEpoch
            || assignment.Lifecycle is not (AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing)
            || entity.Fidelity != FidelityState.Tactical)
        {
            throw Violation("tactical-authority-fenced", "The tactical state does not have current active authority.");
        }
        ValidateCoordinate(value.Coordinate);
        ValidateResources(value.Resources);
        _ = HeadingMilliDegrees.FromMilliDegrees(value.HeadingMilliDegrees);
        if (value.Telegraph is < -1 or > 5
            || value.DamageMilliUnits is < 0 or > 1_000_000
            || !Enum.IsDefined(value.Lifecycle))
        {
            throw Violation("invalid-tactical-state", "Tactical heading, telegraph, damage, or lifecycle is invalid.");
        }
        return Decision.Create(
            command.Subject,
            new TacticalStateAppliedEvent(value),
            entity.Revision.Next(),
            value.AssignmentId,
            value.AuthorityEpoch);
    }

    private static CampaignState ApplyRegion(CampaignState state, CommittedCampaignEvent item, RegionRegisteredEvent value)
    {
        var region = value.Region;
        var regions = state.Regions.Add(region.RegionId, new RegionState(
            region.RegionId,
            region.Name,
            region.Neighbors.ToImmutableSortedSet(),
            item.NewSubjectRevision));
        return state with { Regions = regions };
    }

    private static CampaignState ApplyBase(CampaignState state, CommittedCampaignEvent item, BaseRegisteredEvent value)
    {
        var data = value.Base;
        var entry = new BaseState(data.BaseId, data.Name, data.CoalitionId, data.RegionId, data.Coordinate,
            data.Resources.ToImmutableSortedDictionary(), item.NewSubjectRevision);
        return state with { Bases = state.Bases.Add(entry.BaseId, entry) };
    }

    private static CampaignState ApplyEntity(CampaignState state, CommittedCampaignEvent item, EntityRegisteredEvent value)
    {
        var data = value.Entity;
        var entry = new EntityState(data.EntityId, data.Name, data.CatalogUnitId, data.CoalitionId, data.RegionId,
            data.Coordinate, data.HomeBaseId, data.Lifecycle, data.Fidelity, data.SimulationDomain,
            data.MaximumFidelity, data.Resources.ToImmutableSortedDictionary(),
            HeadingMilliDegrees.FromMilliDegrees(0), Telegraph: 0,
            ReadinessMilliUnits: 1_000, DamageMilliUnits: 0, data.ClassifiedTruthTag, item.NewSubjectRevision);
        return state with { Entities = state.Entities.Add(entry.EntityId, entry) };
    }

    private static CampaignState ApplyGroup(CampaignState state, CommittedCampaignEvent item, GroupFormedEvent value)
    {
        var data = value.Group;
        var entry = new GroupState(data.GroupId, data.Name, data.CoalitionId, data.Members.ToImmutableSortedSet(), null,
            item.NewSubjectRevision);
        return state with { Groups = state.Groups.Add(entry.GroupId, entry) };
    }

    private static CampaignState ApplyOrder(CampaignState state, CommittedCampaignEvent item, OrderIssuedEvent value)
    {
        var data = value.Order;
        var order = new OrderState(data.OrderId, data.GroupId, data.Kind, data.DestinationRegionId, data.StartsAt,
            data.MustCompleteBy, CompletedAt: null, new EntityRevision(1));
        var group = state.Groups[data.GroupId] with { ActiveOrderId = data.OrderId, Revision = item.NewSubjectRevision };
        return state with { Orders = state.Orders.Add(order.OrderId, order), Groups = state.Groups.SetItem(group.GroupId, group) };
    }

    private static CampaignState ApplyFidelity(CampaignState state, CommittedCampaignEvent item, FidelityChangedEvent value)
    {
        var entity = state.Entities[value.Change.EntityId] with { Fidelity = value.Change.Fidelity, Revision = item.NewSubjectRevision };
        return state with { Entities = state.Entities.SetItem(entity.EntityId, entity) };
    }

    private static CampaignState ApplyAssignment(CampaignState state, CommittedCampaignEvent item, AuthorityAssignedEvent value)
    {
        var data = value.Assignment;
        var entry = new AssignmentState(data.AssignmentId, data.EntityId, data.Role, data.AuthorityEpoch,
            data.EffectiveAt, AssignmentLifecycle.Preparing, item.NewSubjectRevision, data.BatchId, data.BatchSize);
        return state with { Assignments = state.Assignments.Add(entry.AssignmentId, entry) };
    }

    private static CampaignState ApplyAssignmentLifecycle(
        CampaignState state,
        CommittedCampaignEvent item,
        AssignmentLifecycleChangedEvent value)
    {
        var assignment = state.Assignments[value.Change.AssignmentId] with
        {
            Lifecycle = value.Change.Lifecycle,
            Revision = item.NewSubjectRevision,
        };
        return state with { Assignments = state.Assignments.SetItem(assignment.AssignmentId, assignment) };
    }

    private static CampaignState ApplyTacticalAssignmentActivation(
        CampaignState state,
        CommittedCampaignEvent item,
        TacticalAssignmentActivatedEvent value)
    {
        var activation = value.Activation;
        var assignment = state.Assignments[activation.AssignmentId] with
        {
            Lifecycle = AssignmentLifecycle.Active,
            Revision = item.NewSubjectRevision,
        };
        var entity = state.Entities[activation.EntityId] with
        {
            Fidelity = FidelityState.Tactical,
            Revision = activation.ExpectedEntityRevision.Next(),
        };
        return state with
        {
            Assignments = state.Assignments.SetItem(assignment.AssignmentId, assignment),
            Entities = state.Entities.SetItem(entity.EntityId, entity),
        };
    }

    private static CampaignState ApplyTacticalIslandActivation(
        CampaignState state,
        CommittedCampaignEvent item,
        TacticalIslandActivatedEvent value)
    {
        var assignments = state.Assignments;
        var entities = state.Entities;
        foreach (var member in value.Activation.Members)
        {
            assignments = assignments.SetItem(member.AssignmentId, assignments[member.AssignmentId] with
            {
                Lifecycle = AssignmentLifecycle.Active,
                Revision = member.ExpectedAssignmentRevision.Next(),
            });
            entities = entities.SetItem(member.EntityId, entities[member.EntityId] with
            {
                Fidelity = FidelityState.Tactical,
                Revision = member.ExpectedEntityRevision.Next(),
            });
        }
        return state with { Assignments = assignments, Entities = entities };
    }

    private static CampaignState ApplyTacticalIslandRelease(
        CampaignState state,
        CommittedCampaignEvent item,
        TacticalIslandReleasedEvent value)
    {
        var assignments = state.Assignments;
        var entities = state.Entities;
        foreach (var member in value.Release.Members)
        {
            assignments = assignments.SetItem(member.AssignmentId, assignments[member.AssignmentId] with
            {
                Lifecycle = AssignmentLifecycle.Released,
                Revision = member.ExpectedAssignmentRevision.Next(),
            });
            entities = entities.SetItem(member.EntityId, entities[member.EntityId] with
            {
                Fidelity = FidelityState.Operational,
                Revision = member.ExpectedEntityRevision.Next(),
            });
        }
        return state with { Assignments = assignments, Entities = entities };
    }

    private static CampaignState ApplyTacticalState(
        CampaignState state,
        CommittedCampaignEvent item,
        TacticalStateAppliedEvent value)
    {
        var data = value.State;
        var entity = state.Entities[data.EntityId] with
        {
            Coordinate = data.Coordinate,
            Heading = HeadingMilliDegrees.FromMilliDegrees(data.HeadingMilliDegrees),
            Telegraph = data.Telegraph,
            Resources = data.Resources.ToImmutableSortedDictionary(),
            DamageMilliUnits = data.DamageMilliUnits,
            ReadinessMilliUnits = Math.Clamp(1_000 - data.DamageMilliUnits / 1_000, 0, 1_000),
            Lifecycle = data.Lifecycle,
            Revision = item.NewSubjectRevision,
        };
        return state with { Entities = state.Entities.SetItem(entity.EntityId, entity) };
    }

    private static CampaignState ApplyIntelligence(CampaignState state, CommittedCampaignEvent item, IntelligenceReportedEvent value)
    {
        var data = value.Report;
        var key = new IntelligenceKey(data.ObserverCoalitionId, data.ContactEntityId);
        var entry = new IntelligenceEstimate(key, data.EstimatedRegionId, data.EstimatedCoordinate, data.Confidence,
            data.ObservedAt, data.UncertaintyRadiusMeters, data.Classification, item.NewSubjectRevision);
        return state with { Intelligence = state.Intelligence.SetItem(key, entry) };
    }

    private static CampaignState ApplyWeather(CampaignState state, CommittedCampaignEvent item, WeatherSetEvent value)
    {
        var data = value.Weather;
        var entry = new WeatherState(data.RegionId,
            HeadingMilliDegrees.FromMilliDegrees(data.WindDirectionMilliDegrees),
            SpeedMillimetersPerSecond.FromMillimetersPerSecond(data.WindMillimetersPerSecond),
            DistanceMeters.FromMeters(data.VisibilityMeters), data.SeaStateMilliUnits, item.NewSubjectRevision);
        return state with { Weather = state.Weather.SetItem(entry.RegionId, entry) };
    }

    private static void RequireEnvelope(CampaignState state, CampaignCommand command, CommitPosition position)
    {
        CampaignInvariants.RequireId(command.CommandId);
        CampaignInvariants.RequireId(command.PlayerId);
        CampaignInvariants.RequireId(command.Scope.CoalitionId);
        CampaignInvariants.RequireText(command.IdempotencyKey.Value, nameof(command.IdempotencyKey));
        if (command.IdempotencyKey.Value.Length > 128)
        {
            throw Violation("idempotency-key-too-long", "Idempotency keys are limited to 128 characters.");
        }
        if (command.CampaignId != state.Metadata.CampaignId)
        {
            throw Violation("wrong-campaign", "The command targets another campaign.");
        }
        if (command.SchemaVersion != CampaignSchema.Version)
        {
            throw Violation("unsupported-command-schema", "The command schema is not supported.");
        }
        if (!command.Scope.MayCommand)
        {
            throw Violation("scope-read-only", "The information scope cannot issue commands.");
        }
        if (command.RequestedAt.Quanta < 0 || command.EffectiveAt.Quanta < state.Time.Quanta)
        {
            throw Violation("invalid-command-time", "The command cannot take effect before the durable campaign head.");
        }
        if (command.Data is not (AdvanceClockCommand or SimulateCampaignCommand) && command.EffectiveAt != state.Time)
        {
            throw Violation("future-command-not-scheduled", "Immediate reducers only accept commands effective at the current campaign time.");
        }
        if (position != state.Head.Next())
        {
            throw Violation("commit-gap", "The event position must immediately follow the durable head.");
        }
    }

    private static CampaignState ApplySimulation(CampaignState state, CampaignSimulatedEvent value)
    {
        var entities = state.Entities;
        foreach (var update in value.Entities)
        {
            var current = entities[update.EntityId];
            entities = entities.SetItem(update.EntityId, current with
            {
                RegionId = update.RegionId,
                Coordinate = update.Coordinate,
                Lifecycle = update.Lifecycle,
                Resources = update.Resources.ToImmutableSortedDictionary(),
                ReadinessMilliUnits = update.ReadinessMilliUnits,
                DamageMilliUnits = update.DamageMilliUnits,
                Revision = update.Revision,
            });
        }

        var bases = state.Bases;
        foreach (var update in value.Bases)
        {
            bases = bases.SetItem(update.BaseId, bases[update.BaseId] with
            {
                Resources = update.Resources.ToImmutableSortedDictionary(),
                Revision = update.Revision,
            });
        }

        var groups = state.Groups;
        foreach (var update in value.Groups)
        {
            groups = groups.SetItem(update.GroupId, groups[update.GroupId] with
            {
                ActiveOrderId = update.ActiveOrderId,
                Revision = update.Revision,
            });
        }

        var orders = state.Orders;
        foreach (var update in value.Orders)
        {
            orders = orders.SetItem(update.OrderId, new OrderState(
                update.OrderId,
                update.GroupId,
                update.Kind,
                update.DestinationRegionId,
                update.StartsAt,
                update.MustCompleteBy,
                update.CompletedAt,
                update.Revision));
        }

        var intelligence = state.Intelligence;
        foreach (var update in value.Intelligence)
        {
            var key = new IntelligenceKey(update.ObserverCoalitionId, update.ContactEntityId);
            intelligence = intelligence.SetItem(key, new IntelligenceEstimate(
                key,
                update.EstimatedRegionId,
                update.EstimatedCoordinate,
                update.Confidence,
                update.ObservedAt,
                update.UncertaintyRadiusMeters,
                update.Classification,
                update.Revision));
        }

        var scheduled = value.PendingActivities.Select(item => new ScheduledActivityState(
                item.ActivityId,
                item.Kind,
                item.Subject,
                item.DueAt,
                item.Revision))
            .ToImmutableSortedDictionary(item => item.ActivityId, item => item);
        var updated = state with
        {
            Time = value.NewTime,
            Entities = entities,
            Bases = bases,
            Groups = groups,
            Orders = orders,
            Intelligence = intelligence,
            ScheduledActivities = scheduled,
            RecentOperationalOutcomes = [.. value.Outcomes],
        };
        CampaignStateValidator.Validate(updated);
        return updated;
    }

    private static void RequireSubject(CampaignCommand command, CampaignSubject expected)
    {
        if (command.Subject != expected)
        {
            throw Violation("subject-mismatch", "The typed subject does not match the command payload.");
        }
    }

    private static bool CanUseFidelity(FidelityState requested, FidelityState maximum) =>
        requested == FidelityState.Suspended || requested <= maximum;

    private static void RequireNew(CampaignCommand command, bool exists, string code)
    {
        RequireRevision(command, EntityRevision.None);
        if (exists)
        {
            throw Violation(code, "The requested identifier already exists.");
        }
    }

    private static void RequireRevision(CampaignCommand command, EntityRevision current)
    {
        if (command.ExpectedSubjectRevision != current)
        {
            throw Violation("revision-conflict", $"Expected revision {command.ExpectedSubjectRevision.Value}, current revision {current.Value}.");
        }
    }

    private static void RequireRegion(CampaignState state, RegionId regionId)
    {
        if (!state.Regions.ContainsKey(regionId))
        {
            throw Violation("region-missing", "The referenced region does not exist.");
        }
    }

    private static EntityState RequireEntity(CampaignState state, EntityId entityId) =>
        state.Entities.TryGetValue(entityId, out var entity)
            ? entity
            : throw Violation("entity-missing", "The referenced entity does not exist.");

    private static void RequireCoalitionOrAdministrator(CampaignCommand command, CoalitionId coalitionId)
    {
        if (command.Scope.CoalitionId != coalitionId && !command.Scope.MaySeeClassifiedTruth)
        {
            throw Violation("coalition-scope", "The scope cannot mutate another coalition's state.");
        }
    }

    private static void ValidateResources(IReadOnlyDictionary<ResourceKind, ResourceAmount> resources)
    {
        if (resources.Any(item => item.Value.MilliUnits < 0))
        {
            throw Violation("negative-resource", "Canonical resources cannot be negative.");
        }
    }

    private static void ValidateCoordinate(GeoCoordinate coordinate) =>
        _ = GeoCoordinate.FromMicrodegrees(coordinate.LatitudeMicrodegrees, coordinate.LongitudeMicrodegrees);

    private static EventId CreateEventId(CampaignId campaignId, CommandId commandId, CommitPosition position)
    {
        var input = Encoding.UTF8.GetBytes($"{campaignId}:{commandId}:{position.Value}");
        var hash = SHA256.HashData(input);
        return new EventId(new Guid(hash.AsSpan(0, 16)));
    }

    private static CampaignRuleViolation Violation(string code, string message) => new(code, message);

    private sealed record Decision(
        CampaignSubject Subject,
        EntityRevision Revision,
        CampaignEventData Data,
        AssignmentId? AssignmentId,
        AuthorityEpoch AuthorityEpoch)
    {
        public static Decision Create(
            CampaignSubject subject,
            CampaignEventData data,
            EntityRevision? revision = null,
            AssignmentId? assignmentId = null,
            AuthorityEpoch? authorityEpoch = null) =>
            new(subject, revision ?? new EntityRevision(1), data, assignmentId, authorityEpoch ?? AuthorityEpoch.None);
    }
}

public sealed class CampaignRuleViolation(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
