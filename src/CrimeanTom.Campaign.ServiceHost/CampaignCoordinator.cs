using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Engine;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.Transport.V1;

using Google.Protobuf;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignCoordinator(SqliteCampaignJournal journal, CampaignSessionRegistry sessions)
{
    private static readonly CoordinatorId ServiceCoordinator = new(
        Guid.Parse("c4000000-0000-0000-0000-000000000001"));
    private static readonly PlayerId ServicePlayer = new(
        Guid.Parse("c4000000-0000-0000-0000-000000000002"));
    private readonly ConcurrentDictionary<CampaignId, CampaignWriterLease> _leases = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CampaignBootstrapResult> BootstrapAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            if (state.Head != CommitPosition.Origin)
            {
                throw new CampaignStoreConflict("campaign-not-empty", "Bootstrap requires an empty campaign.");
            }
            var blue = new CoalitionId(KeyedGuid(campaignId.Value, "blue"));
            var red = new CoalitionId(KeyedGuid(campaignId.Value, "red"));
            var harbor = new RegionId(KeyedGuid(campaignId.Value, "harbor"));
            var offshore = new RegionId(KeyedGuid(campaignId.Value, "offshore"));
            var campaignBase = new BaseId(KeyedGuid(campaignId.Value, "base"));
            var primary = new EntityId(KeyedGuid(campaignId.Value, "primary"));
            var fallback = new EntityId(KeyedGuid(campaignId.Value, "fallback"));
            var combat = new EntityId(KeyedGuid(campaignId.Value, "combat"));
            var mismatch = new EntityId(KeyedGuid(campaignId.Value, "mismatch"));
            var crashBeforeCheckpoint = new EntityId(KeyedGuid(campaignId.Value, "crash-before-checkpoint"));
            var unrelated = new EntityId(KeyedGuid(campaignId.Value, "unrelated"));
            var unrelatedGroup = new GroupId(KeyedGuid(campaignId.Value, "unrelated-group"));
            var resources = new Dictionary<ResourceKind, ResourceAmount>
            {
                [ResourceKind.Fuel] = ResourceAmount.FromMilliUnits(1_000_000),
                [ResourceKind.Stores] = ResourceAmount.FromMilliUnits(500_000),
                [ResourceKind.Munitions] = ResourceAmount.FromMilliUnits(500_000),
            };

            state = await CommitAsync(state, Command(state, CampaignSubject.Region(harbor), EntityRevision.None,
                new RegisterRegionCommand(harbor, "Handoff harbor", []), "bootstrap-region-harbor"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Region(offshore), EntityRevision.None,
                new RegisterRegionCommand(offshore, "Unrelated offshore region", [harbor]), "bootstrap-region-offshore"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Base(campaignBase), EntityRevision.None,
                new RegisterBaseCommand(campaignBase, "Handoff port", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_180_000, 28_650_000), resources), "bootstrap-base"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(primary), EntityRevision.None,
                new RegisterEntityCommand(primary, "USS Pharris", "usn_ff_knox_84", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_180_000, 28_650_000), campaignBase, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE4-PRIMARY-TRUE"), "bootstrap-primary"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(fallback), EntityRevision.None,
                new RegisterEntityCommand(fallback, "USS Ainsworth", "usn_ff_knox_84", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_200_000, 28_700_000), campaignBase, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE4-FALLBACK-TRUE"), "bootstrap-fallback"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(combat), EntityRevision.None,
                new RegisterEntityCommand(combat, "USS Miller", "usn_ff_knox_84", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_220_000, 28_750_000), campaignBase, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE4-COMBAT-TRUE"), "bootstrap-combat"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(mismatch), EntityRevision.None,
                new RegisterEntityCommand(mismatch, "USS Ouellet", "usn_ff_knox_84", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_240_000, 28_800_000), campaignBase, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE4-MISMATCH-TRUE"), "bootstrap-mismatch"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(crashBeforeCheckpoint), EntityRevision.None,
                new RegisterEntityCommand(crashBeforeCheckpoint, "USS Cook", "usn_ff_knox_84", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_260_000, 28_850_000), campaignBase, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE4-CRASH-BEFORE-CHECKPOINT-TRUE"),
                "bootstrap-crash-before-checkpoint"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(unrelated), EntityRevision.None,
                new RegisterEntityCommand(unrelated, "Unrelated patrol", "vmf_krivak", red, offshore,
                    GeoCoordinate.FromMicrodegrees(44_000_000, 34_000_000), null, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE4-UNRELATED-TRUE"), "bootstrap-unrelated"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Group(unrelatedGroup), EntityRevision.None,
                new FormGroupCommand(unrelatedGroup, "Unrelated patrol group", red, [unrelated]),
                "bootstrap-unrelated-group"), cancellationToken);
            return new CampaignBootstrapResult(
                campaignId.ToString(),
                primary.ToString(),
                fallback.ToString(),
                combat.ToString(),
                mismatch.ToString(),
                crashBeforeCheckpoint.ToString(),
                unrelated.ToString(),
                harbor.ToString(),
                offshore.ToString(),
                state.Head.Value,
                CampaignCanonicalSerializer.ComputeHash(state));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FuldaGapBootstrapResult> BootstrapFuldaGapAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            if (state.Head != CommitPosition.Origin)
            {
                throw new CampaignStoreConflict("campaign-not-empty", "Fulda Gap bootstrap requires an empty campaign.");
            }

            var seed = FuldaGapCampaignSeed.Create(campaignId);
            foreach (var region in seed.Regions)
            {
                var neighbors = region.NeighborKeys.Select(key => seed.RegionIds[key]).ToArray();
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Region(region.RegionId),
                    EntityRevision.None,
                    new RegisterRegionCommand(region.RegionId, region.Name, neighbors),
                    $"fulda-region-{region.Key}"), cancellationToken);
            }

            foreach (var campaignBase in seed.Bases)
            {
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Base(campaignBase.BaseId),
                    EntityRevision.None,
                    new RegisterBaseCommand(
                        campaignBase.BaseId,
                        campaignBase.Name,
                        campaignBase.CoalitionId,
                        seed.RegionIds[campaignBase.RegionKey],
                        campaignBase.Coordinate,
                        FuldaResources(baseSupply: true)),
                    $"fulda-base-{campaignBase.Key}"), cancellationToken);
            }

            foreach (var entity in seed.Entities)
            {
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Entity(entity.EntityId),
                    EntityRevision.None,
                    new RegisterEntityCommand(
                        entity.EntityId,
                        entity.Name,
                        entity.CatalogUnitId,
                        entity.CoalitionId,
                        seed.RegionIds[entity.RegionKey],
                        entity.Coordinate,
                        entity.HomeBaseKey is null ? null : seed.BaseIds[entity.HomeBaseKey],
                        EntityLifecycle.Deployed,
                        FidelityState.Operational,
                        FuldaResources(baseSupply: false),
                        $"FULDA-89:{entity.CoalitionId}:{entity.Key}",
                        entity.SimulationDomain,
                        entity.MaximumFidelity),
                    $"fulda-entity-{entity.Key}"), cancellationToken);
            }

            foreach (var group in seed.Groups)
            {
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Group(group.GroupId),
                    EntityRevision.None,
                    new FormGroupCommand(group.GroupId, group.Name, group.CoalitionId, group.Members),
                    $"fulda-group-{group.Key}"), cancellationToken);
            }

            foreach (var order in seed.Orders)
            {
                var group = state.Groups[order.GroupId];
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Group(order.GroupId),
                    group.Revision,
                    new IssueOrderCommand(
                        order.OrderId,
                        order.GroupId,
                        order.Kind,
                        order.DestinationRegionId,
                        state.Time,
                        new CampaignTime(720)),
                    $"fulda-order-{order.OrderId}"), cancellationToken);
            }

            return new FuldaGapBootstrapResult(
                campaignId.ToString(),
                "Fulda Gap Gone Hot — September 1989",
                seed.BlueCoalitionId.ToString(),
                seed.RedCoalitionId.ToString(),
                seed.Regions.Count,
                seed.Bases.Count,
                seed.Entities.Count,
                seed.Groups.Count,
                seed.Entities.Count(item => item.SimulationDomain == SimulationDomain.LandAbstract),
                seed.RegionIds["fulda"].ToString(),
                seed.RegionIds["giuk"].ToString(),
                seed.RegionIds["baltap"].ToString(),
                seed.EntityIds["pharris"].ToString(),
                state.Head.Value,
                CampaignCanonicalSerializer.ComputeHash(state));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TacticalAssignment> PrepareHandoffAsync(
        CampaignHandoffRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var entityId = ParseId(request.CanonicalEntityId, value => new EntityId(value));
            var assignmentId = ParseScopedId(request.AssignmentId, "assignment:", value => new AssignmentId(value));
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            var entity = state.Entities.TryGetValue(entityId, out var found)
                ? found
                : throw new CampaignStoreConflict("entity-missing", "The handoff entity does not exist.");
            if (entity.MaximumFidelity != FidelityState.Tactical)
            {
                throw new CampaignStoreConflict(
                    "fidelity-ceiling",
                    "The entity's simulation domain cannot be assigned to a Sea Power tactical worker.");
            }
            if (entity.Fidelity != FidelityState.Tactical)
            {
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Entity(entityId),
                    entity.Revision,
                    new ChangeFidelityCommand(entityId, FidelityState.Tactical),
                    $"{request.IdempotencyKey}-fidelity"), cancellationToken);
                entity = state.Entities[entityId];
            }
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(assignmentId),
                EntityRevision.None,
                new AssignAuthorityCommand(
                    assignmentId,
                    entityId,
                    CrimeanTom.Campaign.Contracts.AssignmentRole.Primary,
                    new AuthorityEpoch(request.AuthorityEpoch),
                    state.Time),
                $"{request.IdempotencyKey}-assignment"), cancellationToken);
            entity = state.Entities[entityId];
            return new TacticalAssignment
            {
                CampaignId = campaignId.ToString(),
                AssignmentId = request.AssignmentId,
                EntityId = request.GameEntityId,
                CanonicalEntityId = entityId.ToString(),
                AuthorityEpoch = request.AuthorityEpoch,
                Role = CrimeanTom.Campaign.Transport.V1.AssignmentRole.Primary,
                HandoffCommitPosition = state.Head.Value,
                HandoffTimeQuanta = state.Time.Quanta,
                ExpectedEntityRevision = 0,
                IdempotencyKey = request.IdempotencyKey,
                RequiredRuntimeFingerprintSha256 = request.RequiredRuntimeFingerprintSha256,
                RequiredCatalogFingerprintSha256 = request.RequiredCatalogFingerprintSha256,
                UnitTypeId = request.UnitTypeId,
                VariantId = request.VariantId,
                LoadoutId = request.LoadoutId,
                TaskforceId = request.TaskforceId,
                LatitudeMicrodegrees = entity.Coordinate.LatitudeMicrodegrees,
                LongitudeMicrodegrees = entity.Coordinate.LongitudeMicrodegrees,
                HeadingMillidegrees = entity.Heading.Value,
                Telegraph = entity.Telegraph,
                GloballyPaused = true,
                InitialFuelMilliunits = entity.Resources.GetValueOrDefault(ResourceKind.Fuel).MilliUnits,
                InitialStoresMilliunits = entity.Resources.GetValueOrDefault(ResourceKind.Stores).MilliUnits,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ProcessCompanionFrameAsync(
        CompanionHello hello,
        CompanionFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.PayloadCase is not (
            CompanionFrame.PayloadOneofCase.AssignmentPrepared
            or CompanionFrame.PayloadOneofCase.TacticalEvent
            or CompanionFrame.PayloadOneofCase.CheckpointPublished))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(hello.CampaignId, value => new CampaignId(value));
            if (frame.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared)
            {
                await ActivateAsync(campaignId, frame.AssignmentPrepared, cancellationToken);
            }
            else if (frame.PayloadCase == CompanionFrame.PayloadOneofCase.CheckpointPublished)
            {
                await AttachCheckpointAsync(campaignId, frame.CheckpointPublished, cancellationToken);
            }
            else
            {
                await CommitTacticalEventAsync(campaignId, frame.TacticalEvent, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CampaignAdvanceResult> AdvanceAsync(
        CampaignId campaignId,
        CampaignTime target,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            var before = state.Entities.ToDictionary(item => item.Key, item => item.Value);
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Campaign(campaignId),
                state.CampaignRevision,
                new SimulateCampaignCommand(target),
                $"advance-{target.Quanta}",
                target), cancellationToken);
            var frozen = state.Assignments.Values
                .Where(item => item.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing)
                .Select(item => item.EntityId)
                .All(id => before[id] == state.Entities[id]);
            return new CampaignAdvanceResult(
                state.Head.Value,
                state.Time.Quanta,
                CampaignCanonicalSerializer.ComputeHash(state),
                frozen);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CampaignDiagnosticView> ViewAsync(CampaignId campaignId, CancellationToken cancellationToken)
    {
        var recovery = await journal.RecoverAsync(campaignId, cancellationToken);
        return new CampaignDiagnosticView(
            recovery.State.Head.Value,
            recovery.State.Time.Quanta,
            recovery.CanonicalHash,
            recovery.State.Entities.Values.Select(item => new CampaignEntityView(
                item.EntityId.ToString(),
                item.Name,
                item.CatalogUnitId,
                item.CoalitionId.ToString(),
                item.Fidelity.ToString(),
                item.SimulationDomain.ToString(),
                item.MaximumFidelity.ToString(),
                item.RegionId.ToString(),
                item.Coordinate.LatitudeMicrodegrees,
                item.Coordinate.LongitudeMicrodegrees,
                item.Heading.Value,
                item.Telegraph,
                item.Resources.GetValueOrDefault(ResourceKind.Fuel).MilliUnits,
                item.Resources.GetValueOrDefault(ResourceKind.Stores).MilliUnits,
                item.DamageMilliUnits,
                item.Lifecycle.ToString(),
                item.Revision.Value)).ToArray(),
            recovery.State.Assignments.Values.Select(item => new CampaignAssignmentView(
                item.AssignmentId.ToString(),
                item.EntityId.ToString(),
                item.AuthorityEpoch.Value,
                item.Lifecycle.ToString(),
                item.EffectiveAt.Quanta + CampaignSchema.MaximumTacticalAssignmentQuanta,
                item.Revision.Value)).ToArray());
    }

    public async Task<CampaignFallbackResult> ResolveExpiredAssignmentsAsync(
        CampaignId campaignId,
        CampaignTime target,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            if (target.Quanta < state.Time.Quanta)
            {
                throw new CampaignStoreConflict("fallback-time-stale", "Fallback cannot run before campaign time.");
            }
            var reports = new List<CampaignFallbackAssignment>();
            while (true)
            {
                var pending = state.Assignments.Values
                    .Where(item => item.Lifecycle != AssignmentLifecycle.Released)
                    .Select(item => new
                    {
                        Assignment = item,
                        Deadline = item.EffectiveAt.Quanta + CampaignSchema.MaximumTacticalAssignmentQuanta,
                    })
                    .Where(item => item.Deadline <= target.Quanta)
                    .OrderBy(item => item.Deadline)
                    .ThenBy(item => item.Assignment.AssignmentId)
                    .ToArray();
                if (pending.Length == 0)
                {
                    break;
                }
                var deadline = pending[0].Deadline;
                if (state.Time.Quanta < deadline)
                {
                    state = await CommitAsync(state, Command(
                        state,
                        CampaignSubject.Campaign(campaignId),
                        state.CampaignRevision,
                        new SimulateCampaignCommand(new CampaignTime(deadline)),
                        $"fallback-advance-{deadline}",
                        new CampaignTime(deadline)), cancellationToken);
                }
                foreach (var due in pending.Where(item => item.Deadline == deadline))
                {
                    var assignment = state.Assignments[due.Assignment.AssignmentId];
                    state = await CommitAsync(state, Command(
                        state,
                        CampaignSubject.Assignment(assignment.AssignmentId),
                        assignment.Revision,
                        new SetAssignmentLifecycleCommand(
                            assignment.AssignmentId,
                            assignment.AuthorityEpoch,
                            AssignmentLifecycle.Released,
                            state.Time),
                        $"fallback-release-{assignment.AssignmentId}-{deadline}"), cancellationToken);
                    var entity = state.Entities[assignment.EntityId];
                    if (entity.Fidelity != FidelityState.Operational)
                    {
                        state = await CommitAsync(state, Command(
                            state,
                            CampaignSubject.Entity(entity.EntityId),
                            entity.Revision,
                            new ChangeFidelityCommand(entity.EntityId, FidelityState.Operational),
                            $"fallback-operational-{assignment.AssignmentId}-{deadline}"), cancellationToken);
                    }
                    var checkpoint = await journal.ReadLatestBlobReferenceAsync(
                        campaignId,
                        CampaignBlobPurpose.TacticalCheckpoint,
                        $"assignment:{assignment.AssignmentId}/",
                        cancellationToken);
                    reports.Add(new CampaignFallbackAssignment(
                        $"assignment:{assignment.AssignmentId}",
                        assignment.EntityId.ToString(),
                        deadline,
                        CampaignSchema.MaximumTacticalAssignmentQuanta,
                        checkpoint?.CommitPosition.Value,
                        checkpoint?.Sha256,
                        checkpoint is null
                            ? "operational-resolution-without-worker-checkpoint"
                            : "operational-resolution-from-last-committed-checkpoint"));
                }
            }
            if (state.Time.Quanta < target.Quanta)
            {
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Campaign(campaignId),
                    state.CampaignRevision,
                    new SimulateCampaignCommand(target),
                    $"fallback-final-advance-{target.Quanta}",
                    target), cancellationToken);
            }
            return new CampaignFallbackResult(
                state.Head.Value,
                state.Time.Quanta,
                CampaignCanonicalSerializer.ComputeHash(state),
                reports);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ActivateAsync(
        CampaignId campaignId,
        AssignmentPrepared prepared,
        CancellationToken cancellationToken)
    {
        if (!prepared.Ready)
        {
            return;
        }
        var assignmentId = ParseScopedId(prepared.AssignmentId, "assignment:", value => new AssignmentId(value));
        var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
        var assignment = state.Assignments[assignmentId];
        if (assignment.AuthorityEpoch.Value != prepared.AuthorityEpoch)
        {
            throw new CampaignStoreConflict("assignment-fenced", "Prepared assignment epoch is stale.");
        }
        if (assignment.Lifecycle != AssignmentLifecycle.Preparing)
        {
            return;
        }
        await CommitAsync(state, Command(
            state,
            CampaignSubject.Assignment(assignmentId),
            assignment.Revision,
            new SetAssignmentLifecycleCommand(
                assignmentId,
                assignment.AuthorityEpoch,
                AssignmentLifecycle.Active,
                state.Time),
            $"activate-{prepared.AssignmentId}-{prepared.AuthorityEpoch}"), cancellationToken);
    }

    private async Task AttachCheckpointAsync(
        CampaignId campaignId,
        CheckpointPublished checkpoint,
        CancellationToken cancellationToken)
    {
        var assignmentId = ParseScopedId(checkpoint.AssignmentId, "assignment:", value => new AssignmentId(value));
        var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
        var assignment = state.Assignments[assignmentId];
        if (assignment.AuthorityEpoch.Value != checkpoint.AuthorityEpoch)
        {
            throw new CampaignStoreConflict("checkpoint-fenced", "Checkpoint assignment epoch is stale.");
        }
        var content = await journal.ReadBlobAsync(checkpoint.CheckpointSha256, cancellationToken);
        if (content.LongLength != checkpoint.CheckpointLength)
        {
            throw new CampaignStoreConflict("checkpoint-length-conflict", "Checkpoint length disagrees with uploaded content.");
        }
        var lease = await RequireLeaseAsync(campaignId, cancellationToken);
        await journal.PutBlobAsync(
            lease,
            CampaignBlobPurpose.TacticalCheckpoint,
            $"{checkpoint.AssignmentId}/revision-{checkpoint.EntityRevision}/{checkpoint.CheckpointSha256}",
            state.Head,
            content,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private async Task CommitTacticalEventAsync(
        CampaignId campaignId,
        TacticalEventProposal proposal,
        CancellationToken cancellationToken)
    {
        var assignmentId = ParseScopedId(proposal.AssignmentId, "assignment:", value => new AssignmentId(value));
        var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
        var assignment = state.Assignments[assignmentId];
        if (assignment.AuthorityEpoch.Value != proposal.AuthorityEpoch)
        {
            throw new CampaignStoreConflict("tactical-event-fenced", "Tactical proposal epoch is stale.");
        }
        if (assignment.Lifecycle == AssignmentLifecycle.Released)
        {
            return;
        }
        var current = state.Entities[assignment.EntityId];
        var applyKinematics = proposal.Kind is TacticalEventKind.Movement or TacticalEventKind.Checkpoint;
        var coordinate = applyKinematics
            ? GeoCoordinate.FromMicrodegrees(proposal.LatitudeMicrodegrees, proposal.LongitudeMicrodegrees)
            : current.Coordinate;
        var heading = applyKinematics ? proposal.HeadingMillidegrees : current.Heading.Value;
        var telegraph = applyKinematics ? proposal.Telegraph : current.Telegraph;
        var resources = current.Resources;
        if (proposal.Kind is TacticalEventKind.Resources or TacticalEventKind.Checkpoint)
        {
            resources = resources
                .SetItem(ResourceKind.Fuel, ResourceAmount.FromMilliUnits(proposal.FuelMilliunits))
                .SetItem(ResourceKind.Stores, ResourceAmount.FromMilliUnits(proposal.StoresMilliunits));
        }
        var damage = proposal.Kind is TacticalEventKind.Damage or TacticalEventKind.Destroyed or TacticalEventKind.Checkpoint
            ? checked((int)proposal.DamageMilliunits)
            : current.DamageMilliUnits;
        var lifecycle = proposal.Destroyed || proposal.Kind == TacticalEventKind.Destroyed
            ? EntityLifecycle.Destroyed
            : damage >= 800_000 ? EntityLifecycle.Disabled : current.Lifecycle;
        var stateChanged = current.Coordinate != coordinate
            || current.Heading.Value != heading
            || current.Telegraph != telegraph
            || !current.Resources.SequenceEqual(resources)
            || current.DamageMilliUnits != damage
            || current.Lifecycle != lifecycle;
        if (stateChanged)
        {
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Entity(current.EntityId),
                current.Revision,
                new ApplyTacticalStateCommand(
                    assignmentId,
                    assignment.AuthorityEpoch,
                    current.EntityId,
                    coordinate,
                    heading,
                    telegraph,
                    resources,
                    damage,
                    lifecycle),
                proposal.Kind == TacticalEventKind.Checkpoint
                    ? $"{proposal.IdempotencyKey}-state"
                    : proposal.IdempotencyKey), cancellationToken);
        }
        if (assignment.Lifecycle == AssignmentLifecycle.Quiescing
            && proposal.Kind == TacticalEventKind.Checkpoint)
        {
            assignment = state.Assignments[assignmentId];
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(assignmentId),
                assignment.Revision,
                new SetAssignmentLifecycleCommand(
                    assignmentId,
                    assignment.AuthorityEpoch,
                    AssignmentLifecycle.Released,
                    state.Time),
                $"release-{proposal.IdempotencyKey}"), cancellationToken);
            var entity = state.Entities[assignment.EntityId];
            await CommitAsync(state, Command(
                state,
                CampaignSubject.Entity(entity.EntityId),
                entity.Revision,
                new ChangeFidelityCommand(entity.EntityId, FidelityState.Operational),
                $"operational-{proposal.IdempotencyKey}"), cancellationToken);
        }
    }

    public async Task<AuthorityFence> BeginQuiesceAsync(
        CampaignQuiesceRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var assignmentId = ParseScopedId(request.AssignmentId, "assignment:", value => new AssignmentId(value));
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            var assignment = state.Assignments[assignmentId];
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(assignmentId),
                assignment.Revision,
                new SetAssignmentLifecycleCommand(
                    assignmentId,
                    assignment.AuthorityEpoch,
                    AssignmentLifecycle.Quiescing,
                    state.Time),
                request.IdempotencyKey), cancellationToken);
            var entity = state.Entities[assignment.EntityId];
            return new AuthorityFence
            {
                AssignmentId = request.AssignmentId,
                EntityId = request.GameEntityId,
                FencedAuthorityEpoch = assignment.AuthorityEpoch.Value,
                ExpectedEntityRevision = request.ExpectedGameRevision,
                EffectiveTimeQuanta = state.Time.Quanta,
                IdempotencyKey = request.IdempotencyKey,
                Reason = "quiesce-for-operational-dematerialization",
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CampaignState> CommitAsync(
        CampaignState state,
        CampaignCommand command,
        CancellationToken cancellationToken)
    {
        var campaignEvent = CampaignKernel.Decide(state, command, state.Head.Next());
        var lease = await RequireLeaseAsync(state.Metadata.CampaignId, cancellationToken);
        var result = await journal.AppendAsync(lease, command, campaignEvent, DateTimeOffset.UtcNow, cancellationToken);
        var json = CampaignContractJson.Serialize(result.CampaignEvent);
        var snapshot = sessions.Snapshot();
        if (snapshot is not null
            && string.Equals(snapshot.CampaignId, state.Metadata.CampaignId.ToString(), StringComparison.Ordinal)
            && !sessions.TryQueue(new ServiceFrame
            {
                CommittedEvent = new CrimeanTom.Campaign.Transport.V1.CommittedCampaignEvent
                {
                    CommitPosition = result.CampaignEvent.CommitPosition.Value,
                    EventId = result.CampaignEvent.EventId.ToString(),
                    EventJson = ByteString.CopyFrom(json),
                    EventSha256 = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant(),
                },
            }))
        {
            throw new CampaignStoreConflict(
                "companion-stream-backpressured",
                "The campaign event committed but could not enter the companion stream; reconnect catch-up is required.");
        }
        return CampaignKernel.Apply(state, result.CampaignEvent);
    }

    private async Task<CampaignWriterLease> RequireLeaseAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_leases.TryGetValue(campaignId, out var current) && current.ExpiresAt > now.AddSeconds(30))
        {
            return current;
        }
        var lease = current is not null && current.ExpiresAt > now
            ? await journal.RenewWriterAsync(current, now, TimeSpan.FromMinutes(5), cancellationToken)
            : await journal.AcquireWriterAsync(campaignId, ServiceCoordinator, now, TimeSpan.FromMinutes(5), cancellationToken);
        _leases[campaignId] = lease;
        return lease;
    }

    private static CampaignCommand Command(
        CampaignState state,
        CampaignSubject subject,
        EntityRevision revision,
        CampaignCommandData data,
        string idempotencyKey,
        CampaignTime? effectiveAt = null) => new(
            new CommandId(KeyedGuid(state.Metadata.CampaignId.Value, idempotencyKey)),
            new IdempotencyKey(idempotencyKey),
            state.Metadata.CampaignId,
            ServicePlayer,
            subject,
            revision,
            state.Time,
            effectiveAt ?? state.Time,
            new InformationScope(ResolveCoalition(state, subject), MayCommand: true, MaySeeClassifiedTruth: true),
            data);

    private static CoalitionId ResolveCoalition(CampaignState state, CampaignSubject subject) => subject.Kind switch
    {
        CampaignSubjectKind.Entity => state.Entities.TryGetValue(new EntityId(subject.Value), out var entity)
            ? entity.CoalitionId
            : new CoalitionId(KeyedGuid(state.Metadata.CampaignId.Value, "blue")),
        CampaignSubjectKind.Group => state.Groups.TryGetValue(new GroupId(subject.Value), out var group)
            ? group.CoalitionId
            : new CoalitionId(KeyedGuid(state.Metadata.CampaignId.Value, "blue")),
        CampaignSubjectKind.Assignment => state.Assignments.TryGetValue(new AssignmentId(subject.Value), out var assignment)
            ? state.Entities[assignment.EntityId].CoalitionId
            : new CoalitionId(KeyedGuid(state.Metadata.CampaignId.Value, "blue")),
        _ => new CoalitionId(KeyedGuid(state.Metadata.CampaignId.Value, "blue")),
    };

    private static Guid KeyedGuid(Guid campaignId, string purpose)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{campaignId:N}:{purpose}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static IReadOnlyDictionary<ResourceKind, ResourceAmount> FuldaResources(bool baseSupply) =>
        new Dictionary<ResourceKind, ResourceAmount>
        {
            [ResourceKind.Fuel] = ResourceAmount.FromMilliUnits(baseSupply ? 20_000_000 : 1_000_000),
            [ResourceKind.Stores] = ResourceAmount.FromMilliUnits(baseSupply ? 10_000_000 : 750_000),
            [ResourceKind.Munitions] = ResourceAmount.FromMilliUnits(baseSupply ? 12_000_000 : 900_000),
            [ResourceKind.SpareParts] = ResourceAmount.FromMilliUnits(baseSupply ? 5_000_000 : 300_000),
            [ResourceKind.Personnel] = ResourceAmount.FromMilliUnits(baseSupply ? 8_000_000 : 1_000_000),
        };

    private static T ParseId<T>(string value, Func<Guid, T> factory)
    {
        if (!Guid.TryParseExact(value, "N", out var parsed) || parsed == Guid.Empty)
        {
            throw new CampaignStoreConflict("invalid-id", "A canonical identifier is invalid.");
        }
        return factory(parsed);
    }

    private static T ParseScopedId<T>(string value, string prefix, Func<Guid, T> factory)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict("invalid-scoped-id", $"Identifier must start with {prefix}.");
        }
        return ParseId(value[prefix.Length..], factory);
    }
}

public sealed record CampaignHandoffRequest(
    string CampaignId,
    string CanonicalEntityId,
    string GameEntityId,
    string AssignmentId,
    long AuthorityEpoch,
    string IdempotencyKey,
    string RequiredRuntimeFingerprintSha256,
    string RequiredCatalogFingerprintSha256,
    string UnitTypeId,
    string VariantId,
    string LoadoutId,
    string TaskforceId);

public sealed record CampaignQuiesceRequest(
    string CampaignId,
    string AssignmentId,
    string GameEntityId,
    long ExpectedGameRevision,
    string IdempotencyKey);

public sealed record CampaignBootstrapResult(
    string CampaignId,
    string PrimaryEntityId,
    string FallbackEntityId,
    string CombatEntityId,
    string MismatchEntityId,
    string CrashBeforeCheckpointEntityId,
    string UnrelatedEntityId,
    string HarborRegionId,
    string OffshoreRegionId,
    long CommitPosition,
    string CanonicalHash);

public sealed record FuldaGapBootstrapResult(
    string CampaignId,
    string Title,
    string BlueCoalitionId,
    string RedCoalitionId,
    int RegionCount,
    int BaseCount,
    int EntityCount,
    int TaskForceCount,
    int AbstractLandFormationCount,
    string FuldaRegionId,
    string GiukRegionId,
    string BalticApproachesRegionId,
    string TacticalDemoEntityId,
    long CommitPosition,
    string CanonicalHash);

public sealed record CampaignAdvanceResult(long CommitPosition, long TimeQuanta, string CanonicalHash, bool TacticalEntitiesFrozen);

public sealed record CampaignDiagnosticView(
    long CommitPosition,
    long TimeQuanta,
    string CanonicalHash,
    IReadOnlyList<CampaignEntityView> Entities,
    IReadOnlyList<CampaignAssignmentView> Assignments);

public sealed record CampaignEntityView(
    string EntityId,
    string Name,
    string CatalogUnitId,
    string CoalitionId,
    string Fidelity,
    string SimulationDomain,
    string MaximumFidelity,
    string RegionId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    int HeadingMilliDegrees,
    int Telegraph,
    long FuelMilliunits,
    long StoresMilliunits,
    int DamageMilliunits,
    string Lifecycle,
    long Revision);

public sealed record CampaignAssignmentView(
    string AssignmentId,
    string EntityId,
    long AuthorityEpoch,
    string Lifecycle,
    long ResolveByQuanta,
    long Revision);

public sealed record CampaignFallbackResult(
    long CommitPosition,
    long TimeQuanta,
    string CanonicalHash,
    IReadOnlyList<CampaignFallbackAssignment> Assignments);

public sealed record CampaignFallbackAssignment(
    string AssignmentId,
    string EntityId,
    long ResolvedAtQuanta,
    long MaximumLossQuanta,
    long? LastCheckpointCommitPosition,
    string? LastCheckpointSha256,
    string Resolution);
