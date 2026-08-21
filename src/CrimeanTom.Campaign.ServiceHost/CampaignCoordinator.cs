using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Engine;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.Transport.V1;

using Google.Protobuf;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignCoordinator(
    SqliteCampaignJournal journal,
    CampaignSessionRegistry sessions,
    CampaignPerformanceTelemetry telemetry)
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
            var batchLead = new EntityId(KeyedGuid(campaignId.Value, "batch-lead"));
            var batchWing = new EntityId(KeyedGuid(campaignId.Value, "batch-wing"));
            var unrelated = new EntityId(KeyedGuid(campaignId.Value, "unrelated"));
            var batchGroup = new GroupId(KeyedGuid(campaignId.Value, "batch-group"));
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
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(batchLead), EntityRevision.None,
                new RegisterEntityCommand(batchLead, "USS Kirk", "usn_ff_knox_84", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_280_000, 28_900_000), campaignBase, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE5-BATCH-LEAD-TRUE"),
                "bootstrap-batch-lead"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(batchWing), EntityRevision.None,
                new RegisterEntityCommand(batchWing, "USS Reasoner", "usn_ff_knox_84", blue, harbor,
                    GeoCoordinate.FromMicrodegrees(44_300_000, 28_950_000), campaignBase, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE5-BATCH-WING-TRUE"),
                "bootstrap-batch-wing"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Entity(unrelated), EntityRevision.None,
                new RegisterEntityCommand(unrelated, "Unrelated patrol", "vmf_krivak", red, offshore,
                    GeoCoordinate.FromMicrodegrees(44_000_000, 34_000_000), null, EntityLifecycle.Deployed,
                    FidelityState.Operational, resources, "PHASE4-UNRELATED-TRUE"), "bootstrap-unrelated"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Group(unrelatedGroup), EntityRevision.None,
                new FormGroupCommand(unrelatedGroup, "Unrelated patrol group", red, [unrelated]),
                "bootstrap-unrelated-group"), cancellationToken);
            state = await CommitAsync(state, Command(state, CampaignSubject.Group(batchGroup), EntityRevision.None,
                new FormGroupCommand(batchGroup, "Phase 5 atomic handoff group", blue, [batchLead, batchWing]),
                "bootstrap-batch-group"), cancellationToken);
            return new CampaignBootstrapResult(
                campaignId.ToString(),
                primary.ToString(),
                fallback.ToString(),
                combat.ToString(),
                mismatch.ToString(),
                crashBeforeCheckpoint.ToString(),
                batchLead.ToString(),
                batchWing.ToString(),
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
        CancellationToken cancellationToken,
        int forceMultiplier = 1)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            if (state.Head != CommitPosition.Origin)
            {
                throw new CampaignStoreConflict("campaign-not-empty", "Fulda Gap bootstrap requires an empty campaign.");
            }

            var seed = FuldaGapCampaignSeed.Create(campaignId, forceMultiplier);
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
                seed.ForceMultiplier,
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
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            return (await PrepareHandoffCoreAsync(state, request, cancellationToken)).Assignment;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelPreparingHandoffAsync(
        CampaignHandoffRequest request,
        string reason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var assignmentId = ParseScopedId(request.AssignmentId, "assignment:", value => new AssignmentId(value));
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            if (!state.Assignments.TryGetValue(assignmentId, out var assignment)
                || assignment.Lifecycle != AssignmentLifecycle.Preparing)
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
                    AssignmentLifecycle.Released,
                    state.Time),
                $"cancel-{request.IdempotencyKey}-{reason}"), cancellationToken);
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
        var stopwatch = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            var startingTime = state.Time;
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
            telemetry.Record(
                campaignId,
                CampaignOperationKind.Advance,
                stopwatch.Elapsed,
                state.Time.Quanta - startingTime.Quanta);
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
        var stopwatch = Stopwatch.StartNew();
        var recovery = await journal.RecoverAsync(campaignId, cancellationToken);
        telemetry.Record(campaignId, CampaignOperationKind.Recovery, stopwatch.Elapsed);
        var worker = sessions.Snapshot();
        var scheduler = recovery.State.SchedulerPolicy is { } policy
            ? CampaignFidelityScheduler.Evaluate(
                recovery.State,
                SchedulerRequest(policy),
                worker,
                policy.StableSampleCount)
            : CampaignFidelityScheduler.Inspect(recovery.State);
        return new CampaignDiagnosticView(
            recovery.State.Metadata.CampaignId.ToString(),
            recovery.State.Metadata.Name,
            recovery.State.Head.Value,
            recovery.State.Time.Quanta,
            recovery.CanonicalHash,
            recovery.State.Regions.Values.Select(item => new CampaignRegionView(
                item.RegionId.ToString(),
                item.Name,
                item.Neighbors.Select(neighbor => neighbor.ToString()).ToArray())).ToArray(),
            recovery.State.Bases.Values.Select(item => new CampaignBaseView(
                item.BaseId.ToString(),
                item.Name,
                item.CoalitionId.ToString(),
                item.RegionId.ToString(),
                item.Coordinate.LatitudeMicrodegrees,
                item.Coordinate.LongitudeMicrodegrees,
                item.Resources.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.MilliUnits))).ToArray(),
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
            recovery.State.Groups.Values.Select(item => new CampaignGroupView(
                item.GroupId.ToString(),
                item.Name,
                item.CoalitionId.ToString(),
                item.Members.Select(member => member.ToString()).ToArray(),
                item.ActiveOrderId?.ToString(),
                item.Revision.Value)).ToArray(),
            recovery.State.Assignments.Values.Select(item => new CampaignAssignmentView(
                item.AssignmentId.ToString(),
                item.EntityId.ToString(),
                item.AuthorityEpoch.Value,
                item.Lifecycle.ToString(),
                item.EffectiveAt.Quanta + CampaignSchema.MaximumTacticalAssignmentQuanta,
                item.Revision.Value,
                item.BatchId,
                item.BatchSize)).ToArray(),
            recovery.State.RecentOperationalOutcomes.Select(item => new CampaignOperationalOutcomeView(
                item.OutcomeId,
                item.OccurredAt.Quanta,
                item.Kind.ToString(),
                item.RegionId.ToString(),
                item.ActorEntityId.ToString(),
                item.TargetEntityId?.ToString(),
                item.MunitionsExpendedMilliUnits,
                item.DamageInflictedMilliUnits,
                item.Detail)).ToArray(),
            CampaignFidelityScheduler.ToView(recovery.State, scheduler),
            telemetry.Snapshot(campaignId));
    }

    public async Task<CampaignSchedulerView> ScheduleFidelityAsync(
        CampaignId campaignId,
        CampaignSchedulerRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            var worker = sessions.Snapshot();
            var observed = (request.ObservedEntityIds ?? []).Select(value => ParseId(value, id => new EntityId(id)))
                .Distinct().OrderBy(id => id).ToArray();
            var controlled = (request.ControlledEntityIds ?? []).Select(value => ParseId(value, id => new EntityId(id)))
                .Distinct().OrderBy(id => id).ToArray();
            var requestHash = SchedulerRequestHash(request, observed, controlled);
            var stableSampleCount = state.SchedulerPolicy?.RequestHash == requestHash
                ? Math.Min(state.SchedulerPolicy.StableSampleCount + 1, 1_000_000)
                : 1;
            var evaluation = CampaignFidelityScheduler.Evaluate(state, request, worker, stableSampleCount);
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Campaign(campaignId),
                state.CampaignRevision,
                new UpdateSchedulerPolicyCommand(
                    observed,
                    controlled,
                    request.Health.ToString(),
                    request.Eligibility.ToString(),
                    request.TacticalEntityCapacity,
                    request.BacklogDepth,
                    requestHash,
                    stableSampleCount,
                    state.Time),
                $"scheduler-policy-{state.CampaignRevision.Value + 1}-{requestHash}-{stableSampleCount}"), cancellationToken);
            foreach (var island in evaluation.Islands.OrderBy(item => item.IslandId, StringComparer.Ordinal))
            {
                foreach (var entityId in island.EntityIds.OrderBy(item => item))
                {
                    var entity = state.Entities[entityId];
                    if (entity.Fidelity == island.DesiredFidelity || entity.Fidelity == FidelityState.Suspended)
                    {
                        continue;
                    }
                    if (island.DesiredFidelity == FidelityState.Tactical)
                    {
                        // Tactical is an achieved state, not a scheduling wish. The entity
                        // remains at its canonical lower fidelity until a worker reports a
                        // successful materialization and checkpoint through ActivateAsync.
                        continue;
                    }
                    if (state.Assignments.Values.Any(item => item.EntityId == entityId
                        && item.Lifecycle is AssignmentLifecycle.Preparing
                            or AssignmentLifecycle.Prepared or AssignmentLifecycle.Active
                            or AssignmentLifecycle.Quiescing))
                    {
                        // Authority must checkpoint and release before the canonical
                        // entity can leave Tactical fidelity.
                        continue;
                    }
                    state = await CommitAsync(state, Command(
                        state,
                        CampaignSubject.Entity(entityId),
                        entity.Revision,
                        new ChangeFidelityCommand(entityId, island.DesiredFidelity, island.Reason),
                        $"scheduler-{state.Time.Quanta}-{island.IslandId}-{entityId}-{entity.Revision.Value}-{island.DesiredFidelity}"), cancellationToken);
                }
            }
            if (worker is not null
                && string.Equals(worker.CampaignId, campaignId.ToString(), StringComparison.Ordinal))
            {
                foreach (var island in evaluation.Islands
                    .Where(item => item.DesiredFidelity == FidelityState.Tactical)
                    .OrderBy(item => item.IslandId, StringComparer.Ordinal))
                {
                    var liveAssignments = state.Assignments.Values
                        .Where(item => island.EntityIds.Contains(item.EntityId)
                            && item.Lifecycle != AssignmentLifecycle.Released)
                        .ToArray();
                    if (liveAssignments.Any(item => item.Lifecycle is AssignmentLifecycle.Active
                        or AssignmentLifecycle.Quiescing))
                    {
                        continue;
                    }
                    var batchSize = island.EntityIds.Count;
                    var batchId = batchSize == 1
                        ? string.Empty
                        : liveAssignments.Select(item => item.BatchId)
                            .FirstOrDefault(item => !string.IsNullOrEmpty(item))
                            ?? $"island:{island.IslandId}:{state.Assignments.Values.Count(item => island.EntityIds.Contains(item.EntityId)) + 1}";
                    foreach (var entityId in island.EntityIds.OrderBy(item => item))
                    {
                        if (liveAssignments.Any(item => item.EntityId == entityId))
                        {
                            continue;
                        }
                        var entity = state.Entities[entityId];
                        var profile = worker.MaterializationProfiles.FirstOrDefault(item => string.Equals(
                            item.UnitTypeId,
                            entity.CatalogUnitId,
                            StringComparison.Ordinal));
                        if (profile is null || string.IsNullOrWhiteSpace(worker.PlayerTaskforceId))
                        {
                            continue;
                        }
                        var attempt = state.Assignments.Values.Count(item => item.EntityId == entityId) + 1;
                        var assignmentGuid = KeyedGuid(
                            campaignId.Value,
                            $"scheduler-assignment-{island.IslandId}-{entityId}-{attempt}");
                        var authorityEpoch = state.Assignments.Values
                            .Where(item => item.EntityId == entityId)
                            .Select(item => item.AuthorityEpoch.Value)
                            .DefaultIfEmpty(0)
                            .Max() + 1;
                        var idempotencyKey = $"scheduler-handoff-{island.IslandId}-{entityId}-{attempt}";
                        var handoffRequest = new CampaignHandoffRequest(
                            campaignId.ToString(),
                            entityId.ToString(),
                            $"unit:crimean-tom:{entityId}",
                            $"assignment:{assignmentGuid:N}",
                            authorityEpoch,
                            idempotencyKey,
                            worker.RuntimeFingerprintSha256,
                            worker.CatalogFingerprintSha256,
                            profile.UnitTypeId,
                            profile.VariantId,
                            profile.LoadoutId,
                            worker.PlayerTaskforceId,
                            batchId,
                            batchSize);
                        var prepared = await PrepareHandoffCoreAsync(state, handoffRequest, cancellationToken);
                        state = prepared.State;
                        if (sessions.TryQueueForCampaign(campaignId.ToString(), new ServiceFrame
                            {
                                Assignment = prepared.Assignment,
                            }))
                        {
                            continue;
                        }
                        var assignmentId = ParseScopedId(
                            handoffRequest.AssignmentId,
                            "assignment:",
                            value => new AssignmentId(value));
                        var assignment = state.Assignments[assignmentId];
                        state = await CommitAsync(state, Command(
                            state,
                            CampaignSubject.Assignment(assignmentId),
                            assignment.Revision,
                            new SetAssignmentLifecycleCommand(
                                assignmentId,
                                assignment.AuthorityEpoch,
                                AssignmentLifecycle.Released,
                                state.Time),
                            $"cancel-{idempotencyKey}-session-lost-before-queue"), cancellationToken);
                    }
                }
            }
            if (worker is not null
                && string.Equals(worker.CampaignId, campaignId.ToString(), StringComparison.Ordinal))
            {
                foreach (var island in evaluation.Islands
                    .Where(item => item.DesiredFidelity != FidelityState.Tactical)
                    .OrderBy(item => item.IslandId, StringComparer.Ordinal))
                {
                    var activeAssignments = state.Assignments.Values
                        .Where(item => island.EntityIds.Contains(item.EntityId)
                            && item.Lifecycle == AssignmentLifecycle.Active)
                        .OrderBy(item => item.AssignmentId)
                        .ToArray();
                    if (activeAssignments.Length == 0)
                    {
                        continue;
                    }
                    foreach (var assignment in activeAssignments)
                    {
                        var quiesceKey = $"scheduler-quiesce-{island.IslandId}-{assignment.AssignmentId}-{assignment.AuthorityEpoch.Value}";
                        var quiesceRequest = new CampaignQuiesceRequest(
                            campaignId.ToString(),
                            $"assignment:{assignment.AssignmentId}",
                            $"unit:crimean-tom:{assignment.EntityId}",
                            0,
                            quiesceKey);
                        var quiesced = await BeginQuiesceCoreAsync(
                            state,
                            quiesceRequest,
                            resolveExpectedRevisionFromWorker: true,
                            cancellationToken);
                        state = quiesced.State;
                        _ = sessions.TryQueueForCampaign(campaignId.ToString(), new ServiceFrame
                        {
                            Fence = quiesced.Fence,
                        });
                    }
                }
            }
            var applied = CampaignFidelityScheduler.Evaluate(state, request, worker, stableSampleCount) with
            {
                LoadAction = evaluation.LoadAction,
                Transitions = evaluation.Transitions,
            };
            var view = CampaignFidelityScheduler.ToView(state, applied);
            telemetry.Record(
                campaignId,
                CampaignOperationKind.Schedule,
                stopwatch.Elapsed,
                applied.Islands.Count);
            return view;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(CampaignState State, TacticalAssignment Assignment)> PrepareHandoffCoreAsync(
        CampaignState state,
        CampaignHandoffRequest request,
        CancellationToken cancellationToken)
    {
        var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
        if (campaignId != state.Metadata.CampaignId)
        {
            throw new CampaignStoreConflict("campaign-mismatch", "The handoff campaign does not match canonical state.");
        }
        var entityId = ParseId(request.CanonicalEntityId, value => new EntityId(value));
        var assignmentId = ParseScopedId(request.AssignmentId, "assignment:", value => new AssignmentId(value));
        var entity = state.Entities.TryGetValue(entityId, out var found)
            ? found
            : throw new CampaignStoreConflict("entity-missing", "The handoff entity does not exist.");
        if (entity.MaximumFidelity != FidelityState.Tactical)
        {
            throw new CampaignStoreConflict(
                "fidelity-ceiling",
                "The entity's simulation domain cannot be assigned to a Sea Power tactical worker.");
        }
        if (entity.Fidelity == FidelityState.Strategic)
        {
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Entity(entityId),
                entity.Revision,
                new ChangeFidelityCommand(entityId, FidelityState.Operational, "tactical-handoff-preparation"),
                $"{request.IdempotencyKey}-operational-preparation"), cancellationToken);
            entity = state.Entities[entityId];
        }
        if (entity.Fidelity is FidelityState.Suspended or FidelityState.Tactical)
        {
            throw new CampaignStoreConflict(
                "handoff-source-fidelity",
                "A new tactical handoff must start from Strategic or Operational fidelity.");
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
                state.Time,
                request.BatchId,
                request.BatchSize),
            $"{request.IdempotencyKey}-assignment"), cancellationToken);
        entity = state.Entities[entityId];
        return (state, new TacticalAssignment
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
        });
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
        if (!prepared.Ready)
        {
            await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(assignmentId),
                assignment.Revision,
                new SetAssignmentLifecycleCommand(
                    assignmentId,
                    assignment.AuthorityEpoch,
                    AssignmentLifecycle.Released,
                    state.Time),
                $"reject-{prepared.AssignmentId}-{prepared.AuthorityEpoch}"), cancellationToken);
            return;
        }
        if (!string.IsNullOrEmpty(assignment.BatchId))
        {
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(assignmentId),
                assignment.Revision,
                new SetAssignmentLifecycleCommand(
                    assignmentId,
                    assignment.AuthorityEpoch,
                    AssignmentLifecycle.Prepared,
                    state.Time),
                $"prepared-{prepared.AssignmentId}-{prepared.AuthorityEpoch}"), cancellationToken);
            assignment = state.Assignments[assignmentId];
            var batch = state.Assignments.Values
                .Where(item => string.Equals(item.BatchId, assignment.BatchId, StringComparison.Ordinal)
                    && item.Lifecycle != AssignmentLifecycle.Released)
                .OrderBy(item => item.AssignmentId)
                .ToArray();
            if (batch.Length != assignment.BatchSize
                || batch.Any(item => item.BatchSize != assignment.BatchSize
                    || item.Lifecycle != AssignmentLifecycle.Prepared))
            {
                return;
            }
            var members = batch.Select(item => new TacticalIslandActivationMember(
                item.AssignmentId,
                item.AuthorityEpoch,
                item.Revision,
                item.EntityId,
                state.Entities[item.EntityId].Revision)).ToArray();
            await CommitAsync(state, Command(
                state,
                CampaignSubject.Campaign(campaignId),
                state.CampaignRevision,
                new ActivateTacticalIslandCommand(
                    assignment.BatchId,
                    members,
                    state.Time,
                    "complete-island-materialized-and-checkpointed"),
                $"activate-island-{assignment.BatchId}"), cancellationToken);
            return;
        }
        var entity = state.Entities[assignment.EntityId];
        await CommitAsync(state, Command(
            state,
            CampaignSubject.Assignment(assignmentId),
            assignment.Revision,
            new ActivateTacticalAssignmentCommand(
                assignmentId,
                assignment.AuthorityEpoch,
                entity.EntityId,
                entity.Revision,
                state.Time,
                "worker-materialized-and-checkpointed"),
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
            if (!string.IsNullOrEmpty(assignment.BatchId))
            {
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Assignment(assignmentId),
                    assignment.Revision,
                    new SetAssignmentLifecycleCommand(
                        assignmentId,
                        assignment.AuthorityEpoch,
                        AssignmentLifecycle.Quiesced,
                        state.Time),
                    $"quiesced-{proposal.IdempotencyKey}"), cancellationToken);
                assignment = state.Assignments[assignmentId];
                var batch = state.Assignments.Values
                    .Where(item => string.Equals(item.BatchId, assignment.BatchId, StringComparison.Ordinal)
                        && item.Lifecycle != AssignmentLifecycle.Released)
                    .OrderBy(item => item.AssignmentId)
                    .ToArray();
                if (batch.Length != assignment.BatchSize
                    || batch.Any(item => item.BatchSize != assignment.BatchSize
                        || item.Lifecycle != AssignmentLifecycle.Quiesced))
                {
                    return;
                }
                var members = batch.Select(item => new TacticalIslandReleaseMember(
                    item.AssignmentId,
                    item.AuthorityEpoch,
                    item.Revision,
                    item.EntityId,
                    state.Entities[item.EntityId].Revision)).ToArray();
                await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Campaign(campaignId),
                    state.CampaignRevision,
                    new ReleaseTacticalIslandCommand(
                        assignment.BatchId,
                        members,
                        state.Time,
                        "complete-island-checkpointed-and-suspended"),
                    $"release-island-{assignment.BatchId}"), cancellationToken);
                return;
            }
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
            var state = (await journal.RecoverAsync(campaignId, cancellationToken)).State;
            return (await BeginQuiesceCoreAsync(
                state,
                request,
                resolveExpectedRevisionFromWorker: false,
                cancellationToken)).Fence;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(CampaignState State, AuthorityFence Fence)> BeginQuiesceCoreAsync(
        CampaignState state,
        CampaignQuiesceRequest request,
        bool resolveExpectedRevisionFromWorker,
        CancellationToken cancellationToken)
    {
        var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
        if (campaignId != state.Metadata.CampaignId)
        {
            throw new CampaignStoreConflict("campaign-mismatch", "The quiesce campaign does not match canonical state.");
        }
        var assignmentId = ParseScopedId(request.AssignmentId, "assignment:", value => new AssignmentId(value));
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
        return (state, new AuthorityFence
        {
            AssignmentId = request.AssignmentId,
            EntityId = request.GameEntityId,
            FencedAuthorityEpoch = assignment.AuthorityEpoch.Value,
            ExpectedEntityRevision = request.ExpectedGameRevision,
            EffectiveTimeQuanta = state.Time.Quanta,
            IdempotencyKey = request.IdempotencyKey,
            Reason = "quiesce-for-operational-dematerialization",
            ResolveExpectedRevisionFromWorker = resolveExpectedRevisionFromWorker,
        });
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

    private static CampaignSchedulerRequest SchedulerRequest(SchedulerPolicyState policy) => new(
        policy.ObservedEntityIds.Select(id => id.ToString()).ToArray(),
        policy.ControlledEntityIds.Select(id => id.ToString()).ToArray(),
        Enum.Parse<SchedulerHealthState>(policy.Health),
        Enum.Parse<SimulationSourceEligibility>(policy.Eligibility),
        policy.TacticalEntityCapacity,
        policy.BacklogDepth);

    private static string SchedulerRequestHash(
        CampaignSchedulerRequest request,
        IReadOnlyList<EntityId> observedEntityIds,
        IReadOnlyList<EntityId> controlledEntityIds)
    {
        var observed = string.Join(',', observedEntityIds.Select(id => id.ToString()));
        var controlled = string.Join(',', controlledEntityIds.Select(id => id.ToString()));
        var material = $"observed={observed}|controlled={controlled}|health={request.Health}|eligibility={request.Eligibility}|capacity={request.TacticalEntityCapacity}|backlog={request.BacklogDepth}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
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
    string TaskforceId,
    string BatchId = "",
    int BatchSize = 1);

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
    string BatchLeadEntityId,
    string BatchWingEntityId,
    string UnrelatedEntityId,
    string HarborRegionId,
    string OffshoreRegionId,
    long CommitPosition,
    string CanonicalHash);

public sealed record FuldaGapBootstrapResult(
    string CampaignId,
    string Title,
    int ForceMultiplier,
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
    string CampaignId,
    string Name,
    long CommitPosition,
    long TimeQuanta,
    string CanonicalHash,
    IReadOnlyList<CampaignRegionView> Regions,
    IReadOnlyList<CampaignBaseView> Bases,
    IReadOnlyList<CampaignEntityView> Entities,
    IReadOnlyList<CampaignGroupView> Groups,
    IReadOnlyList<CampaignAssignmentView> Assignments,
    IReadOnlyList<CampaignOperationalOutcomeView> RecentOperationalOutcomes,
    CampaignSchedulerView Scheduler,
    CampaignPerformanceView Performance);

public sealed record CampaignRegionView(
    string RegionId,
    string Name,
    IReadOnlyList<string> NeighborRegionIds);

public sealed record CampaignBaseView(
    string BaseId,
    string Name,
    string CoalitionId,
    string RegionId,
    int LatitudeMicrodegrees,
    int LongitudeMicrodegrees,
    IReadOnlyDictionary<string, long> Resources);

public sealed record CampaignGroupView(
    string GroupId,
    string Name,
    string CoalitionId,
    IReadOnlyList<string> MemberEntityIds,
    string? ActiveOrderId,
    long Revision);

public sealed record CampaignOperationalOutcomeView(
    string OutcomeId,
    long OccurredAtQuanta,
    string Kind,
    string RegionId,
    string ActorEntityId,
    string? TargetEntityId,
    long MunitionsExpendedMilliunits,
    int DamageInflictedMilliunits,
    string Detail);

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
    long Revision,
    string BatchId,
    int BatchSize);

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
