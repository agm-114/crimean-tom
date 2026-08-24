using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Engine;
using CrimeanTom.Campaign.Service;
using CrimeanTom.Campaign.Transport.V1;
using CrimeanTom.Protocol;

using Google.Protobuf;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignCoordinator(
    SqliteCampaignJournal journal,
    CampaignSessionRegistry sessions,
    CampaignPerformanceTelemetry telemetry)
{
    private static readonly JsonSerializerOptions CheckpointJsonOptions = new(JsonSerializerDefaults.Web);
    private const long SnapshotIntervalCommits = 100;
    private static readonly CoordinatorId ServiceCoordinator = new(
        Guid.Parse("c4000000-0000-0000-0000-000000000001"));
    private static readonly PlayerId ServicePlayer = new(
        Guid.Parse("c4000000-0000-0000-0000-000000000002"));
    private readonly ConcurrentDictionary<CampaignId, CampaignWriterLease> _leases = new();
    private readonly ConcurrentDictionary<CampaignId, CachedCampaignState> _stateCache = new();
    // Pre-commit staged readiness only. Companion outbound StageFinalCheckpoint
    // frames are durable and resent, so a coordinator restart before commit
    // recovers by re-queueing staging. After TacticalIslandMigratedEvent,
    // grants reconstruct from the journal and durable source checkpoints.
    private readonly ConcurrentDictionary<string, StagedMigrationCheckpoint> _stagedCheckpoints = new(StringComparer.Ordinal);
    private readonly CampaignMigrationPolicyOptions _migrationPolicy = CampaignMigrationPolicyOptions.Load();
    private readonly CampaignMigrationAdmissionRegistry _migrationAdmission = new();
    private readonly CampaignReplicaHashRegistry _replicaHashes = new();
    private readonly ConcurrentDictionary<CampaignId, byte> _pausedCampaigns = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CampaignBootstrapResult> BootstrapAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await RecoverStateAsync(campaignId, cancellationToken);
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
                CanonicalHash(state));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FuldaGapBootstrapResult> BootstrapFuldaGapAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken,
        int forceMultiplier = 1,
        int? maximumEntities = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            if (state.Head != CommitPosition.Origin)
            {
                throw new CampaignStoreConflict("campaign-not-empty", "Fulda Gap bootstrap requires an empty campaign.");
            }

            var seed = FuldaGapCampaignSeed.Create(campaignId, forceMultiplier, maximumEntities);
            var resultMetrics = (
                seed.ForceMultiplier,
                BlueCoalitionId: seed.BlueCoalitionId.ToString(),
                RedCoalitionId: seed.RedCoalitionId.ToString(),
                RegionCount: seed.Regions.Count,
                BaseCount: seed.Bases.Count,
                EntityCount: seed.Entities.Count,
                TaskForceCount: seed.Groups.Count,
                OpeningOrderCount: seed.Orders.Count,
                MaritimeEntityCount: seed.Entities.Count(item => item.SimulationDomain == SimulationDomain.Maritime),
                AirEntityCount: seed.Entities.Count(item => item.SimulationDomain == SimulationDomain.Air),
                RotaryWingEntityCount: seed.Entities.Count(item => item.SimulationDomain == SimulationDomain.RotaryWing),
                LogisticsEntityCount: seed.Entities.Count(item => item.SimulationDomain == SimulationDomain.Logistics),
                LandEntityCount: seed.Entities.Count(item => item.SimulationDomain is SimulationDomain.LandAbstract
                    or SimulationDomain.Ground or SimulationDomain.Installation),
                GroundUnitCount: seed.Entities.Count(item => item.SimulationDomain == SimulationDomain.Ground),
                InstallationEntityCount: seed.Entities.Count(item => item.SimulationDomain == SimulationDomain.Installation),
                AbstractLandFormationCount: seed.Entities.Count(item => item.CatalogUnitId == "abstract_land_formation"),
                CivilianTrafficCount: seed.Entities.Count(FuldaGapCampaignSeed.IsCivilianTraffic),
                FixedSiteCount: seed.Entities.Count(FuldaGapCampaignSeed.IsFixedSite),
                ResidentStrategicSystemCount: seed.Entities.Count(FuldaGapCampaignSeed.IsResidentStrategicSystem),
                TacticalEligibleEntityCount: seed.Entities.Count(item => item.MaximumFidelity == FidelityState.Tactical),
                FuldaRegionId: seed.RegionIds["fulda"].ToString(),
                GiukRegionId: seed.RegionIds["giuk"].ToString(),
                BalticApproachesRegionId: seed.RegionIds["baltap"].ToString(),
                TacticalDemoEntityId: seed.EntityIds["pharris"].ToString());
            var staged = new List<CampaignAppendEntry>(
                seed.Regions.Count + seed.Bases.Count + seed.Entities.Count + seed.Groups.Count + seed.Orders.Count);
            CampaignState Stage(CampaignCommand command)
            {
                var campaignEvent = CampaignKernel.Decide(state, command, state.Head.Next());
                staged.Add(new CampaignAppendEntry(command, campaignEvent));
                return CampaignKernel.Apply(state, campaignEvent);
            }

            foreach (var region in seed.Regions)
            {
                var neighbors = region.NeighborKeys.Select(key => seed.RegionIds[key]).ToArray();
                state = Stage(Command(
                    state,
                    CampaignSubject.Region(region.RegionId),
                    EntityRevision.None,
                    new RegisterRegionCommand(region.RegionId, region.Name, neighbors),
                    $"fulda-region-{region.Key}"));
            }

            foreach (var campaignBase in seed.Bases)
            {
                state = Stage(Command(
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
                    $"fulda-base-{campaignBase.Key}"));
            }

            foreach (var entity in seed.Entities)
            {
                state = Stage(Command(
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
                        entity.MaximumFidelity,
                        entity.CatalogLoadoutId,
                        entity.OperationalWeapons),
                    $"fulda-entity-{entity.Key}"));
            }

            foreach (var group in seed.Groups)
            {
                state = Stage(Command(
                    state,
                    CampaignSubject.Group(group.GroupId),
                    EntityRevision.None,
                    new FormGroupCommand(group.GroupId, group.Name, group.CoalitionId, group.Members),
                    $"fulda-group-{group.Key}"));
            }

            foreach (var order in seed.Orders)
            {
                var group = state.Groups[order.GroupId];
                state = Stage(Command(
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
                    $"fulda-order-{order.OrderId}"));
            }

            var lease = await RequireLeaseAsync(campaignId, cancellationToken);
            seed = null!;
            state = null!;
            var append = await journal.AppendBatchAsync(lease, staged, DateTimeOffset.UtcNow, cancellationToken);
            if (sessions.HasSessionsForCampaign(campaignId.ToString()))
            {
                foreach (var entry in staged)
                {
                    BroadcastCommittedEvent(entry.CampaignEvent);
                }
            }
            staged.Clear();

            return new FuldaGapBootstrapResult(
                campaignId.ToString(),
                "Fulda Gap Gone Hot — Global War, September 1989",
                resultMetrics.ForceMultiplier,
                resultMetrics.BlueCoalitionId,
                resultMetrics.RedCoalitionId,
                resultMetrics.RegionCount,
                resultMetrics.BaseCount,
                resultMetrics.EntityCount,
                resultMetrics.TaskForceCount,
                resultMetrics.OpeningOrderCount,
                resultMetrics.MaritimeEntityCount,
                resultMetrics.AirEntityCount,
                resultMetrics.RotaryWingEntityCount,
                resultMetrics.LogisticsEntityCount,
                resultMetrics.LandEntityCount,
                resultMetrics.GroundUnitCount,
                resultMetrics.InstallationEntityCount,
                resultMetrics.AbstractLandFormationCount,
                resultMetrics.CivilianTrafficCount,
                resultMetrics.FixedSiteCount,
                resultMetrics.ResidentStrategicSystemCount,
                resultMetrics.TacticalEligibleEntityCount,
                resultMetrics.FuldaRegionId,
                resultMetrics.GiukRegionId,
                resultMetrics.BalticApproachesRegionId,
                resultMetrics.TacticalDemoEntityId,
                append.LastCommitPosition.Value,
                append.CanonicalHash);
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
        if (request.Role != CrimeanTom.Campaign.Contracts.AssignmentRole.Primary)
        {
            throw new CampaignStoreConflict(
                "handoff-role-unsupported",
                "The general handoff operation creates only Primary assignments; Shadow preparation uses its dedicated coordinator operation.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var state = await RecoverStateAsync(campaignId, cancellationToken);
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
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            if (!state.Assignments.TryGetValue(assignmentId, out var assignment)
                || (assignment.Lifecycle != AssignmentLifecycle.Preparing
                    && !(assignment.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow
                        && assignment.Lifecycle == AssignmentLifecycle.Prepared)))
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

    public async Task<CampaignShadowPreparationResult> PrepareShadowDestinationAsync(
        CampaignShadowPreparationRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var entityId = ParseId(request.CanonicalEntityId, value => new EntityId(value));
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            if (campaignId != state.Metadata.CampaignId)
            {
                throw new CampaignStoreConflict("campaign-mismatch", "The Shadow campaign does not match canonical state.");
            }
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                throw new CampaignStoreConflict("invalid-idempotency-key", "Shadow destination preparation requires an idempotency key.");
            }
            var workers = sessions.Snapshots(campaignId.ToString());
            var island = CampaignFidelityScheduler.Inspect(state).Islands
                .FirstOrDefault(item => item.EntityIds.Contains(entityId))
                ?? throw new CampaignStoreConflict("shadow-island-missing", "The entity does not belong to a tactical interaction island.");
            var sourceAssignments = island.EntityIds
                .Select(id => state.Assignments.Values
                    .Where(item => item.EntityId == id
                        && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                        && item.Lifecycle is AssignmentLifecycle.Active
                            or AssignmentLifecycle.Quiescing
                            or AssignmentLifecycle.Quiesced)
                    .OrderByDescending(item => item.AuthorityEpoch.Value)
                    .ThenByDescending(item => item.AssignmentId.Value)
                    .FirstOrDefault())
                .ToArray();
            if (sourceAssignments.Any(item => item is null)
                || sourceAssignments.Length != island.EntityIds.Count)
            {
                throw new CampaignStoreConflict(
                    "shadow-island-incomplete",
                    "Shadow preparation requires a complete currently-active, quiescing, or quiesced tactical island.");
            }
            var sourceOwners = sourceAssignments
                .Select(item => item!.WorkerClientInstanceId)
                .Where(owner => !string.IsNullOrWhiteSpace(owner))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sourceOwners.Length != 1)
            {
                throw new CampaignStoreConflict(
                    "island-worker-affinity-conflict",
                    "A tactical interaction island has live Primary assignments on multiple workers.");
            }

            var liveShadows = island.EntityIds
                .Select(id => state.Assignments.Values
                    .Where(item => item.EntityId == id
                        && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow
                        && item.Lifecycle != AssignmentLifecycle.Released)
                    .OrderByDescending(item => item.AuthorityEpoch.Value)
                    .ThenByDescending(item => item.AssignmentId.Value)
                    .FirstOrDefault())
                .ToArray();
            if (liveShadows.All(item => item is not null))
            {
                var destinationIds = liveShadows
                    .Select(item => item!.WorkerClientInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var batchIds = liveShadows.Select(item => item!.BatchId).Distinct(StringComparer.Ordinal).ToArray();
                var batchSizes = liveShadows.Select(item => item!.BatchSize).Distinct().ToArray();
                if (destinationIds.Length != 1
                    || batchIds.Length != 1
                    || batchSizes.Length != 1
                    || batchSizes[0] != island.EntityIds.Count
                    || (island.EntityIds.Count == 1 && !string.IsNullOrEmpty(batchIds[0]))
                    || (island.EntityIds.Count > 1 && string.IsNullOrEmpty(batchIds[0])))
                {
                    throw new CampaignStoreConflict(
                        "shadow-already-assigned",
                        "Live Shadow assignments for this island are incomplete or inconsistent.");
                }
                var destination = workers.FirstOrDefault(worker => string.Equals(
                    worker.ClientInstanceId,
                    destinationIds[0],
                    StringComparison.Ordinal))
                    ?? throw new CampaignStoreConflict(
                        "shadow-destination-unavailable",
                        "The prepared Shadow destination worker is no longer connected.");
                var assignments = new List<TacticalAssignment>();
                foreach (var shadow in liveShadows
                    .Select(item => item!)
                    .OrderBy(item => item.AssignmentId.Value))
                {
                    var sourceCheckpoint = await FindLatestEntityCheckpointAsync(
                        state,
                        shadow.EntityId,
                        cancellationToken);
                    assignments.Add(ToTacticalAssignment(
                        state,
                        shadow,
                        destination,
                        $"{request.IdempotencyKey}-{shadow.EntityId}",
                        sourceCheckpoint));
                }
                foreach (var assignment in assignments)
                {
                    if (!sessions.TryQueueForClient(
                        campaignId.ToString(),
                        destination.ClientInstanceId,
                        new ServiceFrame { Assignment = assignment }))
                    {
                        throw new CampaignStoreConflict(
                            "companion-session-unavailable-or-backpressured",
                            "The Shadow destination session could not accept the assignment frame.");
                    }
                }
                return new CampaignShadowPreparationResult(destination.ClientInstanceId, assignments);
            }

            if (liveShadows.Any(item => item is not null))
            {
                throw new CampaignStoreConflict(
                    "shadow-already-assigned",
                    "Live Shadow assignments for this island are incomplete or inconsistent.");
            }

            var plannedWorkerLoads = workers.ToDictionary(
                worker => worker.ClientInstanceId,
                worker => Math.Max(
                    worker.OwnedMaterializedEntityCount,
                    state.Assignments.Values
                        .Where(assignment => assignment.Lifecycle != AssignmentLifecycle.Released
                            && string.Equals(
                                assignment.WorkerClientInstanceId,
                                worker.ClientInstanceId,
                                StringComparison.Ordinal))
                        .Select(assignment => assignment.EntityId)
                        .Distinct()
                        .Count()),
                StringComparer.Ordinal);
            var destinationCapacity = Math.Max(
                0,
                workers.Select(worker => Math.Max(0, worker.TacticalEntityCapacity - worker.TacticalReserveCapacity))
                    .DefaultIfEmpty(0)
                    .Max());
            var selectedDestination = ResolveShadowDestinationWorker(
                state,
                island.EntityIds,
                sourceOwners[0],
                workers,
                plannedWorkerLoads,
                destinationCapacity,
                request.PreferredDestinationClientInstanceId);
            var batchSize = island.EntityIds.Count;
            var batchId = batchSize == 1
                ? string.Empty
                : $"shadow:{island.IslandId}:{state.Assignments.Values.Count(item => island.EntityIds.Contains(item.EntityId)) + 1}";
            var created = new List<(CampaignHandoffRequest Request, TacticalAssignment Assignment)>();
            foreach (var memberId in island.EntityIds.OrderBy(item => item))
            {
                var entity = state.Entities[memberId];
                var profile = selectedDestination.MaterializationProfiles.First(item => string.Equals(
                    item.UnitTypeId,
                    entity.CatalogUnitId,
                    StringComparison.Ordinal));
                var attempt = state.Assignments.Values.Count(item => item.EntityId == memberId) + 1;
                var assignmentGuid = KeyedGuid(
                    campaignId.Value,
                    $"shadow-assignment-{island.IslandId}-{memberId}-{attempt}");
                var authorityEpoch = state.Assignments.Values
                    .Where(item => item.EntityId == memberId)
                    .Select(item => item.AuthorityEpoch.Value)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
                var handoffRequest = new CampaignHandoffRequest(
                    campaignId.ToString(),
                    memberId.ToString(),
                    $"unit:crimean-tom:{memberId}",
                    $"assignment:{assignmentGuid:N}",
                    authorityEpoch,
                    $"{request.IdempotencyKey}-{memberId}-{attempt}",
                    selectedDestination.RuntimeFingerprintSha256,
                    selectedDestination.CatalogFingerprintSha256,
                    profile.UnitTypeId,
                    profile.VariantId,
                    profile.LoadoutId,
                    selectedDestination.PlayerTaskforceId,
                    batchId,
                    batchSize,
                    selectedDestination.ClientInstanceId,
                    CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow);
                var prepared = await PrepareHandoffCoreAsync(state, handoffRequest, cancellationToken);
                state = prepared.State;
                created.Add((handoffRequest, prepared.Assignment));
            }

            foreach (var item in created)
            {
                if (sessions.TryQueueForClient(
                    campaignId.ToString(),
                    selectedDestination.ClientInstanceId,
                    new ServiceFrame { Assignment = item.Assignment }))
                {
                    continue;
                }
                foreach (var queued in created)
                {
                    var assignmentId = ParseScopedId(
                        queued.Request.AssignmentId,
                        "assignment:",
                        value => new AssignmentId(value));
                    var assignment = state.Assignments[assignmentId];
                    if (assignment.Lifecycle == AssignmentLifecycle.Released)
                    {
                        continue;
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
                        $"cancel-{queued.Request.IdempotencyKey}-session-lost-before-queue"), cancellationToken);
                }
                throw new CampaignStoreConflict(
                    "companion-session-unavailable-or-backpressured",
                    "The Shadow destination session could not accept the assignment frame.");
            }

            return new CampaignShadowPreparationResult(
                selectedDestination.ClientInstanceId,
                created.Select(item => item.Assignment).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CampaignSourcePreparationResult> PrepareSourceWorkerAsync(
        CampaignSourcePreparationRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var entityId = ParseId(request.CanonicalEntityId, value => new EntityId(value));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                throw new CampaignStoreConflict(
                    "invalid-idempotency-key",
                    "Source worker preparation requires an idempotency key.");
            }
            if (string.IsNullOrWhiteSpace(request.PreferredWorkerClientInstanceId))
            {
                throw new CampaignStoreConflict(
                    "invalid-preferred-worker",
                    "Source worker preparation requires a preferred connected worker client instance ID.");
            }
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            if (campaignId != state.Metadata.CampaignId)
            {
                throw new CampaignStoreConflict("campaign-mismatch", "The source campaign does not match canonical state.");
            }
            var workers = sessions.Snapshots(campaignId.ToString());
            var preferredWorker = workers.FirstOrDefault(worker => string.Equals(
                worker.ClientInstanceId,
                request.PreferredWorkerClientInstanceId,
                StringComparison.Ordinal))
                ?? throw new CampaignStoreConflict(
                    "source-worker-unavailable",
                    "The preferred source worker is not connected to the campaign.");
            var island = CampaignFidelityScheduler.Inspect(state).Islands
                .FirstOrDefault(item => item.EntityIds.Contains(entityId))
                ?? throw new CampaignStoreConflict(
                    "source-island-missing",
                    "The entity does not belong to a tactical interaction island.");
            var livePrimaries = island.EntityIds
                .Select(id => state.Assignments.Values
                    .Where(item => item.EntityId == id
                        && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                        && item.Lifecycle is AssignmentLifecycle.Active
                            or AssignmentLifecycle.Quiescing
                            or AssignmentLifecycle.Quiesced
                            or AssignmentLifecycle.Preparing
                            or AssignmentLifecycle.Prepared)
                    .OrderByDescending(item => item.AuthorityEpoch.Value)
                    .ThenByDescending(item => item.AssignmentId.Value)
                    .FirstOrDefault())
                .ToArray();
            var livePrimaryOwners = livePrimaries
                .Where(item => item is not null)
                .Select(item => item!.WorkerClientInstanceId)
                .Where(owner => !string.IsNullOrWhiteSpace(owner))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (livePrimaryOwners.Length > 1)
            {
                throw new CampaignStoreConflict(
                    "island-worker-affinity-conflict",
                    "Primary assignments for the island are split across multiple workers.");
            }
            if (livePrimaryOwners.Length == 1
                && !string.Equals(
                    livePrimaryOwners[0],
                    preferredWorker.ClientInstanceId,
                    StringComparison.Ordinal))
            {
                var ownerConnected = workers.Any(worker => string.Equals(
                    worker.ClientInstanceId,
                    livePrimaryOwners[0],
                    StringComparison.Ordinal));
                throw new CampaignStoreConflict(
                    ownerConnected ? "source-worker-mismatch" : "source-worker-offline",
                    ownerConnected
                        ? "The live Primary assignment is not owned by the preferred source worker."
                        : "The live Primary assignment owner is not a currently connected campaign worker.");
            }
            if (livePrimaries.Any(item => item is not null
                && item!.Lifecycle is AssignmentLifecycle.Quiescing or AssignmentLifecycle.Quiesced))
            {
                throw new CampaignStoreConflict(
                    "migration-source-not-ready",
                    "Source preparation cannot proceed while a Primary assignment is quiescing.");
            }
            var activePrimaries = livePrimaries
                .Where(item => item is not null && item!.Lifecycle == AssignmentLifecycle.Active)
                .Select(item => item!)
                .ToArray();
            if (activePrimaries.Length == island.EntityIds.Count)
            {
                var assignments = activePrimaries
                    .OrderBy(item => item.AssignmentId.Value)
                    .Select(item => ToTacticalAssignment(
                        state,
                        item,
                        preferredWorker,
                        $"{request.IdempotencyKey}-{item.EntityId}",
                        null))
                    .ToArray();
                return new CampaignSourcePreparationResult(
                    preferredWorker.ClientInstanceId,
                    assignments,
                    "source-already-active");
            }
            if (livePrimaries.Any(item => item is not null
                && item!.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing))
            {
                throw new CampaignStoreConflict(
                    "migration-source-not-ready",
                    "Source preparation requires a complete Primary assignment set on the preferred worker.");
            }
            var preparingPrimaries = livePrimaries
                .Where(item => item is not null
                    && item!.Lifecycle is AssignmentLifecycle.Preparing or AssignmentLifecycle.Prepared)
                .Select(item => item!)
                .ToArray();
            if (preparingPrimaries.Length > 0)
            {
                if (preparingPrimaries.Length != island.EntityIds.Count)
                {
                    throw new CampaignStoreConflict(
                        "source-island-incomplete",
                        "Source preparation found incomplete Primary handoffs for the island.");
                }
                var assignments = new List<TacticalAssignment>();
                foreach (var assignment in preparingPrimaries.OrderBy(item => item.AssignmentId.Value))
                {
                    assignments.Add(ToTacticalAssignment(
                        state,
                        assignment,
                        preferredWorker,
                        $"{request.IdempotencyKey}-{assignment.EntityId}",
                        await FindLatestEntityCheckpointAsync(state, assignment.EntityId, cancellationToken)));
                }
                if (!TryQueueHandoffAssignments(
                    campaignId,
                    preferredWorker.ClientInstanceId,
                    assignments,
                    out _))
                {
                    throw new CampaignStoreConflict(
                        "companion-session-unavailable-or-backpressured",
                        "The preferred source worker session could not accept the assignment frame.");
                }
                return new CampaignSourcePreparationResult(
                    preferredWorker.ClientInstanceId,
                    assignments,
                    "source-handoff-requeued");
            }
            var destinationCapacity = Math.Max(
                0,
                workers.Select(worker => Math.Max(0, worker.TacticalEntityCapacity - worker.TacticalReserveCapacity))
                    .DefaultIfEmpty(0)
                    .Max());
            var plannedWorkerLoads = workers.ToDictionary(
                worker => worker.ClientInstanceId,
                worker => Math.Max(
                    worker.OwnedMaterializedEntityCount,
                    state.Assignments.Values
                        .Where(assignment => assignment.Lifecycle != AssignmentLifecycle.Released
                            && string.Equals(
                                assignment.WorkerClientInstanceId,
                                worker.ClientInstanceId,
                                StringComparison.Ordinal))
                        .Select(assignment => assignment.EntityId)
                        .Distinct()
                        .Count()),
                StringComparer.Ordinal);
            RequirePreferredSourceWorker(
                state,
                island.EntityIds,
                preferredWorker,
                plannedWorkerLoads,
                destinationCapacity);
            var batchSize = island.EntityIds.Count;
            var batchId = batchSize == 1
                ? string.Empty
                : $"island:{island.IslandId}:{state.Assignments.Values.Count(item => island.EntityIds.Contains(item.EntityId)) + 1}";
            var created = new List<(CampaignHandoffRequest Request, TacticalAssignment Assignment)>();
            foreach (var memberId in island.EntityIds.OrderBy(item => item))
            {
                var memberEntity = state.Entities[memberId];
                var profile = preferredWorker.MaterializationProfiles.First(item => string.Equals(
                    item.UnitTypeId,
                    memberEntity.CatalogUnitId,
                    StringComparison.Ordinal));
                var attempt = state.Assignments.Values.Count(item => item.EntityId == memberId) + 1;
                var assignmentGuid = KeyedGuid(
                    campaignId.Value,
                    $"source-assignment-{island.IslandId}-{memberId}-{attempt}");
                var authorityEpoch = state.Assignments.Values
                    .Where(item => item.EntityId == memberId)
                    .Select(item => item.AuthorityEpoch.Value)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
                var handoffRequest = new CampaignHandoffRequest(
                    campaignId.ToString(),
                    memberId.ToString(),
                    $"unit:crimean-tom:{memberId}",
                    $"assignment:{assignmentGuid:N}",
                    authorityEpoch,
                    $"{request.IdempotencyKey}-{memberId}-{attempt}",
                    preferredWorker.RuntimeFingerprintSha256,
                    preferredWorker.CatalogFingerprintSha256,
                    profile.UnitTypeId,
                    profile.VariantId,
                    profile.LoadoutId,
                    preferredWorker.PlayerTaskforceId,
                    batchId,
                    batchSize,
                    preferredWorker.ClientInstanceId);
                var prepared = await PrepareHandoffCoreAsync(state, handoffRequest, cancellationToken);
                state = prepared.State;
                created.Add((handoffRequest, prepared.Assignment));
            }
            if (!TryQueueHandoffAssignments(
                campaignId,
                preferredWorker.ClientInstanceId,
                created.Select(item => item.Assignment).ToArray(),
                out _))
            {
                state = await ReleaseQueuedHandoffAssignmentsAsync(
                    state,
                    created.Select(queued => queued.Assignment).ToArray(),
                    cancellationToken);
                throw new CampaignStoreConflict(
                    "companion-session-unavailable-or-backpressured",
                    "The preferred source worker session could not accept the assignment frame.");
            }
            return new CampaignSourcePreparationResult(
                preferredWorker.ClientInstanceId,
                created.Select(item => item.Assignment).ToArray(),
                "source-handoff-queued");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CampaignIslandMigrationResult> MigrateTacticalIslandAsync(
        CampaignIslandMigrationRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        MigrationOrchestrationLease? orchestration = null;
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var entityId = ParseId(request.CanonicalEntityId, value => new EntityId(value));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                throw new CampaignStoreConflict("invalid-idempotency-key", "Island migration requires an idempotency key.");
            }
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            var committedReplay = await TryReplayCommittedMigrationAsync(
                state,
                entityId,
                request.IdempotencyKey,
                cancellationToken);
            if (committedReplay is not null)
            {
                return committedReplay;
            }
            var plan = await RequireMigrationPlanAsync(state, entityId, cancellationToken);
            if (plan.AlreadyMigrated)
            {
                var grantsQueued = QueueActivationGrants(state, plan, request.IdempotencyKey);
                return new CampaignIslandMigrationResult(
                    plan.DestinationClientInstanceId,
                    plan.MigrationCommitPosition,
                    grantsQueued ? "activation-granted" : "migration-committed-grants-pending",
                    plan.Members.Select(item => item.Destination.AssignmentId.ToString()).ToArray());
            }

            RequireLiveDestinationWorker(campaignId.ToString(), plan.DestinationClientInstanceId);
            _replicaHashes.ThrowIfUnsafeDivergence(
                state,
                plan.Members.Select(item => item.Source.EntityId));

            orchestration = CreateMigrationOrchestrationLease(
                campaignId,
                plan,
                request.IdempotencyKey);
            await EnsureMigrationCooldownAsync(state, cancellationToken);
            if (!TryBeginMigrationOrchestration(orchestration))
            {
                throw CreateMigrationOrchestrationConflict(orchestration);
            }

            if (!QueueStagingFrames(state, plan, request.IdempotencyKey))
            {
                EndMigrationOrchestration(orchestration);
                throw new CampaignStoreConflict(
                    "companion-session-unavailable-or-backpressured",
                    "The destination session could not accept final-checkpoint staging frames.");
            }

            if (!AllMembersStaged(plan))
            {
                return new CampaignIslandMigrationResult(
                    plan.DestinationClientInstanceId,
                    null,
                    "staging-queued",
                    plan.Members.Select(item => item.Destination.AssignmentId.ToString()).ToArray());
            }

            return await CommitMigrationAndGrantAsync(
                state,
                plan,
                orchestration,
                request.IdempotencyKey,
                cancellationToken);
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
            or CompanionFrame.PayloadOneofCase.CheckpointPublished
            or CompanionFrame.PayloadOneofCase.FinalCheckpointStaged))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(hello.CampaignId, value => new CampaignId(value));
            var scopedAssignmentId = frame.PayloadCase switch
            {
                CompanionFrame.PayloadOneofCase.AssignmentPrepared => frame.AssignmentPrepared.AssignmentId,
                CompanionFrame.PayloadOneofCase.CheckpointPublished => frame.CheckpointPublished.AssignmentId,
                CompanionFrame.PayloadOneofCase.FinalCheckpointStaged => frame.FinalCheckpointStaged.AssignmentId,
                _ => frame.TacticalEvent.AssignmentId,
            };
            var assignmentId = ParseScopedId(
                scopedAssignmentId,
                "assignment:",
                value => new AssignmentId(value));
            var ownershipState = await RecoverStateAsync(campaignId, cancellationToken);
            if (ownershipState.Assignments.TryGetValue(assignmentId, out var ownedAssignment)
                && !string.IsNullOrWhiteSpace(ownedAssignment.WorkerClientInstanceId)
                && !string.Equals(
                    ownedAssignment.WorkerClientInstanceId,
                    hello.ClientInstanceId,
                    StringComparison.Ordinal))
            {
                throw new CampaignStoreConflict(
                    "assignment-worker-fenced",
                    "The companion client does not own this tactical assignment.");
            }
            if (CampaignWorkerKind.IsOperational(hello.WorkerKind)
                && frame.PayloadCase is CompanionFrame.PayloadOneofCase.TacticalEvent
                    or CompanionFrame.PayloadOneofCase.CheckpointPublished
                    or CompanionFrame.PayloadOneofCase.FinalCheckpointStaged)
            {
                throw new CampaignStoreConflict(
                    "operational-worker-rejects-tactical",
                    "Track A operational workers cannot publish Sea Power tactical checkpoints or events.");
            }
            if (frame.PayloadCase == CompanionFrame.PayloadOneofCase.AssignmentPrepared)
            {
                if (ownershipState.Assignments.TryGetValue(assignmentId, out var currentAssignment)
                    && currentAssignment.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow)
                {
                    if (currentAssignment.Lifecycle != AssignmentLifecycle.Preparing)
                    {
                        return;
                    }
                    if (frame.AssignmentPrepared.Ready)
                    {
                        await CommitAsync(ownershipState, Command(
                            ownershipState,
                            CampaignSubject.Assignment(assignmentId),
                            currentAssignment.Revision,
                            new SetAssignmentLifecycleCommand(
                                assignmentId,
                                currentAssignment.AuthorityEpoch,
                                AssignmentLifecycle.Prepared,
                                ownershipState.Time),
                            $"prepared-shadow-{frame.AssignmentPrepared.AssignmentId}-{frame.AssignmentPrepared.AuthorityEpoch}"),
                            cancellationToken);
                        return;
                    }

                    var state = ownershipState;
                    var rejectedBatch = string.IsNullOrEmpty(currentAssignment.BatchId)
                        ? new[] { currentAssignment }
                        : state.Assignments.Values
                            .Where(item => item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow
                                && item.Lifecycle != AssignmentLifecycle.Released
                                && string.Equals(item.BatchId, currentAssignment.BatchId, StringComparison.Ordinal))
                            .OrderBy(item => item.AssignmentId.Value)
                            .ToArray();
                    foreach (var rejectedCandidate in rejectedBatch)
                    {
                        var rejected = state.Assignments[rejectedCandidate.AssignmentId];
                        state = await CommitAsync(state, Command(
                            state,
                            CampaignSubject.Assignment(rejected.AssignmentId),
                            rejected.Revision,
                            new SetAssignmentLifecycleCommand(
                                rejected.AssignmentId,
                                rejected.AuthorityEpoch,
                                AssignmentLifecycle.Released,
                                state.Time),
                            $"reject-shadow-{rejected.AssignmentId}-{rejected.AuthorityEpoch.Value}"),
                            cancellationToken);
                    }
                    return;
                }
                await ActivateAsync(campaignId, frame.AssignmentPrepared, cancellationToken);
            }
            else if (frame.PayloadCase == CompanionFrame.PayloadOneofCase.FinalCheckpointStaged)
            {
                await AcceptStagedCheckpointAsync(
                    campaignId,
                    hello.ClientInstanceId,
                    frame.FinalCheckpointStaged,
                    cancellationToken);
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
            if (_pausedCampaigns.ContainsKey(campaignId))
            {
                throw new CampaignStoreConflict(
                    "campaign-paused",
                    "Operator pause is in effect; campaign time does not advance until resume.");
            }
            var state = await RecoverStateAsync(campaignId, cancellationToken);
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
                .Where(item => item.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing
                    && !CampaignAssignmentKinds.IsOperationalSim(item))
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
                CanonicalHash(state),
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
        var state = await RecoverStateAsync(campaignId, cancellationToken);
        telemetry.Record(campaignId, CampaignOperationKind.Recovery, stopwatch.Elapsed);
        var workers = sessions.Snapshots(campaignId.ToString());
        var scheduler = state.SchedulerPolicy is { } policy
            ? CampaignFidelityScheduler.Evaluate(
                state,
                SchedulerRequest(policy),
                workers,
                policy.StableSampleCount)
            : CampaignFidelityScheduler.Inspect(state);
        return new CampaignDiagnosticView(
            state.Metadata.CampaignId.ToString(),
            state.Metadata.Name,
            state.Head.Value,
            state.Time.Quanta,
            CanonicalHash(state),
            state.Regions.Values.Select(item => new CampaignRegionView(
                item.RegionId.ToString(),
                item.Name,
                item.Neighbors.Select(neighbor => neighbor.ToString()).ToArray())).ToArray(),
            state.Bases.Values.Select(item => new CampaignBaseView(
                item.BaseId.ToString(),
                item.Name,
                item.CoalitionId.ToString(),
                item.RegionId.ToString(),
                item.Coordinate.LatitudeMicrodegrees,
                item.Coordinate.LongitudeMicrodegrees,
                item.Resources.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.MilliUnits))).ToArray(),
            state.Entities.Values.Select(item => new CampaignEntityView(
                item.EntityId.ToString(),
                item.Name,
                item.CatalogUnitId,
                item.CatalogLoadoutId,
                item.OperationalWeapons.Select(weapon => new CampaignOperationalWeaponView(
                    weapon.AmmunitionId,
                    weapon.TargetClassification.ToString(),
                    weapon.MaximumRange.Value)).ToArray(),
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
            state.Groups.Values.Select(item => new CampaignGroupView(
                item.GroupId.ToString(),
                item.Name,
                item.CoalitionId.ToString(),
                item.Members.Select(member => member.ToString()).ToArray(),
                item.ActiveOrderId?.ToString(),
                item.ActiveOrderId is { } activeOrderId ? state.Orders[activeOrderId].Kind.ToString() : null,
                item.ActiveOrderId is { } destinationOrderId
                    ? state.Orders[destinationOrderId].DestinationRegionId.ToString()
                    : null,
                item.Revision.Value)).ToArray(),
            state.StrategicObjectives.Values.Select(item => new CampaignStrategicObjectiveView(
                item.ObjectiveId.ToString(),
                item.CoalitionId.ToString(),
                item.RegionId.ToString(),
                item.Kind.ToString(),
                item.PriorityMilliUnits,
                item.CreatedAt.Quanta,
                item.Epoch,
                item.Revision.Value)).ToArray(),
            state.GroupCommitments.Values.Select(item => new CampaignGroupCommitmentView(
                item.GroupId.ToString(),
                item.ObjectiveId?.ToString(),
                item.ExpiresAt.Quanta,
                item.WithdrawLatched,
                item.LatchReleaseAt.Quanta,
                item.Revision.Value)).ToArray(),
            state.OperationalPackages.Values.Select(item => new CampaignOperationalPackageView(
                item.PackageId.ToString(),
                item.CoalitionId.ToString(),
                item.ObjectiveId.ToString(),
                item.RegionId.ToString(),
                item.AssemblyAt.Quanta,
                item.ExecuteAt.Quanta,
                item.ExpiresAt.Quanta,
                item.Members.Values.Select(member => new CampaignOperationalPackageMemberView(
                    member.GroupId.ToString(),
                    member.Role.ToString(),
                    member.StartsAt.Quanta)).ToArray(),
                item.Revision.Value)).ToArray(),
            state.CoalitionStrategies.Values.Select(item => new CampaignCoalitionStrategyView(
                item.CoalitionId.ToString(),
                item.NextDirectorAt.Quanta,
                item.Epoch,
                item.Revision.Value)).ToArray(),
            state.Assignments.Values.Select(item => new CampaignAssignmentView(
                item.AssignmentId.ToString(),
                item.EntityId.ToString(),
                item.AuthorityEpoch.Value,
                item.Role.ToString(),
                item.Lifecycle.ToString(),
                item.EffectiveAt.Quanta + CampaignSchema.MaximumTacticalAssignmentQuanta,
                item.Revision.Value,
                item.BatchId,
                item.BatchSize,
                item.WorkerClientInstanceId,
                sessions.HasLiveTacticalLease(
                    state.Metadata.CampaignId.ToString(),
                    item.WorkerClientInstanceId))).ToArray(),
            state.RecentOperationalOutcomes.Select(item => new CampaignOperationalOutcomeView(
                item.OutcomeId,
                item.OccurredAt.Quanta,
                item.Kind.ToString(),
                item.RegionId.ToString(),
                item.ActorEntityId.ToString(),
                item.TargetEntityId?.ToString(),
                item.MunitionsExpendedMilliUnits,
                item.DamageInflictedMilliUnits,
                item.Detail)).ToArray(),
            CampaignFidelityScheduler.ToView(state, scheduler),
            telemetry.Snapshot(campaignId),
            workers.Select(worker => ToWorkerView(
                state,
                worker,
                Math.Max(0, scheduler.Request.TacticalEntityCapacity
                    - scheduler.Request.TacticalReserveCapacity))).ToArray(),
            ToCalibrationView(state, scheduler),
            _replicaHashes.Diagnose(state));
    }

    public async Task<long> ReadTimeQuantaAsync(CampaignId campaignId, CancellationToken cancellationToken)
    {
        var state = await RecoverStateAsync(campaignId, cancellationToken);
        return state.Time.Quanta;
    }

    internal async Task<(CampaignTime Time, int EntityCount)> ReadUnattendedSimulationStateAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        var state = await RecoverStateAsync(campaignId, cancellationToken);
        return (state.Time, state.Entities.Count);
    }

    public async Task<CampaignCoalitionProjectionView> ProjectCoalitionAsync(
        CampaignId campaignId,
        CoalitionId coalitionId,
        CancellationToken cancellationToken)
    {
        var state = await RecoverStateAsync(campaignId, cancellationToken);
        if (!state.Entities.Values.Any(item => item.CoalitionId == coalitionId)
            && !state.Bases.Values.Any(item => item.CoalitionId == coalitionId))
        {
            throw new CampaignStoreConflict("coalition-missing", "The coalition is not present in this campaign.");
        }

        return CampaignCoalitionProjectionHttp.From(state, coalitionId);
    }

    public async Task<CampaignTacticalWorkerProjectionView> ProjectTacticalWorkerAsync(
        CampaignId campaignId,
        string clientInstanceId,
        string? advertisedCoalitionId,
        CancellationToken cancellationToken)
    {
        // The coordinator cache contains immutable states and is replaced only
        // after a durable commit. Tactical projection is a high-frequency,
        // eventually consistent read, so serving the last committed cached
        // state avoids contending with the large SQLite append currently in
        // progress. A cold process still performs normal durable recovery.
        if (_stateCache.TryGetValue(campaignId, out var cached))
        {
            return CampaignTacticalWorkerProjectionHttp.From(
                cached.State,
                clientInstanceId,
                advertisedCoalitionId);
        }

        var state = await RecoverStateAsync(campaignId, cancellationToken);
        return CampaignTacticalWorkerProjectionHttp.From(
            state,
            clientInstanceId,
            advertisedCoalitionId);
    }

    public async Task<CampaignPauseView> PauseAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = await RecoverStateAsync(campaignId, cancellationToken);
            _pausedCampaigns[campaignId] = 1;
            return new CampaignPauseView(campaignId.ToString(), Paused: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CampaignPauseView> ResumeAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = await RecoverStateAsync(campaignId, cancellationToken);
            _pausedCampaigns.TryRemove(campaignId, out _);
            return new CampaignPauseView(campaignId.ToString(), Paused: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool IsPaused(CampaignId campaignId) => _pausedCampaigns.ContainsKey(campaignId);

    public async Task<CampaignSchedulerView> DrainAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            var request = state.SchedulerPolicy is { } policy
                ? SchedulerRequest(policy) with
                {
                    Health = SchedulerHealthState.Draining,
                    Eligibility = SimulationSourceEligibility.DrainOnly,
                }
                : new CampaignSchedulerRequest(
                    [],
                    [],
                    SchedulerHealthState.Draining,
                    SimulationSourceEligibility.DrainOnly,
                    TacticalEntityCapacity: 4,
                    BacklogDepth: 0,
                    TacticalReserveCapacity: 1);
            return await ScheduleFidelityCoreAsync(campaignId, request, gateAlreadyHeld: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CampaignSchedulerView> ScheduleFidelityAsync(
        CampaignId campaignId,
        CampaignSchedulerRequest request,
        CancellationToken cancellationToken) =>
        ScheduleFidelityCoreAsync(campaignId, request, gateAlreadyHeld: false, cancellationToken);

    public async Task<CampaignTacticalAssignmentRecoveryView> RecoverMissingTacticalAssignmentsAsync(
        CampaignId campaignId,
        string workerClientInstanceId,
        CampaignTacticalAssignmentRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerClientInstanceId))
        {
            throw new CampaignStoreConflict(
                "invalid-worker-client-instance-id",
                "A tactical worker client instance id is required.");
        }
        if (request.MissingAssignmentIds is null
            || request.ControlledAssignmentIds is null
            || request.MissingAssignmentIds.Count is 0 or > 128)
        {
            throw new CampaignStoreConflict(
                "invalid-missing-assignment-count",
                "Recovery requires between one and 128 missing tactical assignments.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            var worker = sessions.Snapshots(campaignId.ToString())
                .SingleOrDefault(item => string.Equals(
                    item.ClientInstanceId,
                    workerClientInstanceId,
                    StringComparison.Ordinal))
                ?? throw new CampaignStoreConflict(
                    "tactical-worker-session-unavailable",
                    "The reporting tactical worker does not have a live campaign session.");
            if (CampaignWorkerKind.IsOperational(worker.WorkerKind))
            {
                throw new CampaignStoreConflict(
                    "invalid-tactical-worker-kind",
                    "Operational simulation workers cannot recover Sea Power tactical assignments.");
            }

            var assignmentIds = request.MissingAssignmentIds
                .Select(value => ParseScopedId(value, "assignment:", id => new AssignmentId(id)))
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var controlledAssignmentIds = request.ControlledAssignmentIds
                .Select(value => ParseScopedId(value, "assignment:", id => new AssignmentId(id)))
                .ToHashSet();
            if (!controlledAssignmentIds.IsSubsetOf(assignmentIds))
            {
                throw new CampaignStoreConflict(
                    "invalid-controlled-recovery-assignment",
                    "Controlled recovery assignments must be included in the missing assignment set.");
            }

            var recoveredEntities = new List<(EntityId EntityId, bool Controlled)>();
            var releasedAssignmentIds = new List<string>();
            foreach (var assignmentId in assignmentIds)
            {
                if (!state.Assignments.TryGetValue(assignmentId, out var assignment))
                {
                    throw new CampaignStoreConflict(
                        "tactical-assignment-not-found",
                        $"Tactical assignment {assignmentId} does not exist in the campaign.");
                }
                if (!string.Equals(
                    assignment.WorkerClientInstanceId,
                    workerClientInstanceId,
                    StringComparison.Ordinal))
                {
                    throw new CampaignStoreConflict(
                        "tactical-assignment-worker-mismatch",
                        $"Tactical assignment {assignmentId} belongs to another worker.");
                }
                if (CampaignAssignmentKinds.IsOperationalSim(assignment)
                    || assignment.Lifecycle != AssignmentLifecycle.Active)
                {
                    continue;
                }

                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Assignment(assignmentId),
                    assignment.Revision,
                    new SetAssignmentLifecycleCommand(
                        assignmentId,
                        assignment.AuthorityEpoch,
                        AssignmentLifecycle.Quiescing,
                        state.Time),
                    $"recover-missing-local-quiesce-{assignmentId}-{assignment.AuthorityEpoch.Value}"),
                    cancellationToken);
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
                    $"recover-missing-local-release-{assignmentId}-{assignment.AuthorityEpoch.Value}"),
                    cancellationToken);
                releasedAssignmentIds.Add($"assignment:{assignmentId}");
                recoveredEntities.Add((
                    assignment.EntityId,
                    controlledAssignmentIds.Contains(assignmentId)));

                var entity = state.Entities[assignment.EntityId];
                var stillFenced = state.Assignments.Values.Any(item =>
                    item.EntityId == entity.EntityId
                    && !CampaignAssignmentKinds.IsOperationalSim(item)
                    && item.Lifecycle is AssignmentLifecycle.Active
                        or AssignmentLifecycle.Quiescing
                        or AssignmentLifecycle.Quiesced);
                if (entity.Fidelity == FidelityState.Tactical && !stillFenced)
                {
                    state = await CommitAsync(state, Command(
                        state,
                        CampaignSubject.Entity(entity.EntityId),
                        entity.Revision,
                        new ChangeFidelityCommand(
                            entity.EntityId,
                            FidelityState.Operational,
                            "local-tactical-object-missing"),
                        $"recover-missing-local-entity-{assignmentId}-{assignment.AuthorityEpoch.Value}"),
                        cancellationToken);
                }
            }

            CampaignSchedulerView? replacementSchedule = null;
            if (recoveredEntities.Count > 0)
            {
                var tacticalWorkers = sessions.Snapshots(campaignId.ToString())
                    .Where(item => !CampaignWorkerKind.IsOperational(item.WorkerKind))
                    .ToArray();
                var campaignTacticalCapacity = Math.Min(
                    10_000,
                    tacticalWorkers.Sum(item => Math.Max(0, item.TacticalEntityCapacity)));
                var campaignTacticalReserve = Math.Min(
                    campaignTacticalCapacity,
                    tacticalWorkers.Sum(item => Math.Clamp(
                        item.TacticalReserveCapacity,
                        0,
                        Math.Max(0, item.TacticalEntityCapacity))));
                var observed = new HashSet<EntityId>();
                var controlled = new HashSet<EntityId>();
                foreach (var recovery in recoveredEntities)
                {
                    if (recovery.Controlled)
                    {
                        observed.Remove(recovery.EntityId);
                        controlled.Add(recovery.EntityId);
                    }
                    else if (!controlled.Contains(recovery.EntityId))
                    {
                        observed.Add(recovery.EntityId);
                    }
                }

                var health = Enum.TryParse<SchedulerHealthState>(worker.Health, out var parsedHealth)
                    ? parsedHealth
                    : SchedulerHealthState.Offline;
                var eligibility = Enum.TryParse<SimulationSourceEligibility>(worker.Eligibility, out var parsedEligibility)
                    ? parsedEligibility
                    : SimulationSourceEligibility.Disabled;
                replacementSchedule = await ScheduleFidelityCoreAsync(
                    campaignId,
                    new CampaignSchedulerRequest(
                        observed.Select(id => id.ToString()).ToArray(),
                        controlled.Select(id => id.ToString()).ToArray(),
                        health,
                        eligibility,
                        campaignTacticalCapacity,
                        state.SchedulerPolicy?.BacklogDepth ?? 0,
                        campaignTacticalReserve,
                        workerClientInstanceId),
                    gateAlreadyHeld: true,
                    cancellationToken);
            }

            return new CampaignTacticalAssignmentRecoveryView(
                releasedAssignmentIds.Count,
                releasedAssignmentIds,
                replacementSchedule);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CampaignSchedulerView> ScheduleFidelityCoreAsync(
        CampaignId campaignId,
        CampaignSchedulerRequest request,
        bool gateAlreadyHeld,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!gateAlreadyHeld)
        {
            await _gate.WaitAsync(cancellationToken);
        }
        try
        {
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            var workers = sessions.Snapshots(campaignId.ToString());
            var observed = (request.ObservedEntityIds ?? []).Select(value => ParseId(value, id => new EntityId(id)))
                .Distinct().OrderBy(id => id).ToArray();
            var controlled = (request.ControlledEntityIds ?? []).Select(value => ParseId(value, id => new EntityId(id)))
                .Distinct().OrderBy(id => id).ToArray();
            var playerInterest = observed.Concat(controlled).ToHashSet();
            var priorPlayerInterest = state.SchedulerPolicy is { } priorPolicy
                ? priorPolicy.ObservedEntityIds.Concat(priorPolicy.ControlledEntityIds).ToHashSet()
                : [];
            var schedulingFocus = playerInterest.Count > 0 ? playerInterest : priorPlayerInterest;
            var schedulingFocusStrings = schedulingFocus.Select(item => item.ToString())
                .ToHashSet(StringComparer.Ordinal);
            var requestHash = SchedulerRequestHash(request, observed, controlled, workers);
            var stableSampleCount = state.SchedulerPolicy?.RequestHash == requestHash
                ? Math.Min(state.SchedulerPolicy.StableSampleCount + 1, 1_000_000)
                : 1;
            var evaluation = CampaignFidelityScheduler.Evaluate(state, request, workers, stableSampleCount);
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
                    state.Time,
                    request.TacticalReserveCapacity),
                $"scheduler-policy-{state.CampaignRevision.Value + 1}-{requestHash}-{stableSampleCount}"), cancellationToken);
            foreach (var island in evaluation.Islands.OrderBy(item => item.IslandId, StringComparer.Ordinal))
            {
                if (schedulingFocus.Count > 0 && !island.EntityIds.Any(schedulingFocus.Contains))
                {
                    // A player selection is latency-sensitive. Unrelated Strategic/Operational
                    // convergence remains canonical in CampaignSimulation and is handled by a
                    // later no-interest maintenance pass instead of delaying this handoff.
                    continue;
                }
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
            state = await YieldOperationalAssignmentsAsync(
                state,
                campaignId,
                evaluation,
                workers,
                cancellationToken);
            if (workers.Count > 0)
            {
                var destinationCapacity = Math.Max(
                    0,
                    request.TacticalEntityCapacity - request.TacticalReserveCapacity);
                var plannedWorkerLoads = workers.ToDictionary(
                    worker => worker.ClientInstanceId,
                    worker => Math.Max(
                        worker.OwnedMaterializedEntityCount,
                        state.Assignments.Values
                            .Where(assignment => assignment.Lifecycle != AssignmentLifecycle.Released
                                && string.Equals(
                                    assignment.WorkerClientInstanceId,
                                    worker.ClientInstanceId,
                                    StringComparison.Ordinal))
                            .Select(assignment => assignment.EntityId)
                            .Distinct()
                            .Count()),
                    StringComparer.Ordinal);
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
                    var islandWorker = SelectPlacementWorker(
                        state,
                        island.EntityIds,
                        liveAssignments,
                        workers,
                        plannedWorkerLoads,
                        destinationCapacity,
                        request.PreferredTacticalWorkerClientInstanceId);
                    if (islandWorker is null)
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
                        var profile = islandWorker.MaterializationProfiles.FirstOrDefault(item => string.Equals(
                            item.UnitTypeId,
                            entity.CatalogUnitId,
                            StringComparison.Ordinal));
                        if (profile is null)
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
                            islandWorker.RuntimeFingerprintSha256,
                            islandWorker.CatalogFingerprintSha256,
                            profile.UnitTypeId,
                            profile.VariantId,
                            profile.LoadoutId,
                            islandWorker.PlayerTaskforceId,
                            batchId,
                            batchSize,
                            islandWorker.ClientInstanceId,
                            FocusAfterActivation: (request.ControlledEntityIds ?? []).Contains(
                                entityId.ToString(),
                                StringComparer.Ordinal));
                        var prepared = await PrepareHandoffCoreAsync(state, handoffRequest, cancellationToken);
                        state = prepared.State;
                        if (sessions.TryQueueForClient(
                            campaignId.ToString(),
                            islandWorker.ClientInstanceId,
                            new ServiceFrame
                            {
                                Assignment = prepared.Assignment,
                            }))
                        {
                            plannedWorkerLoads[islandWorker.ClientInstanceId]++;
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
            if (workers.Count > 0)
            {
                foreach (var island in evaluation.Islands
                    .Where(item => item.DesiredFidelity != FidelityState.Tactical)
                    .OrderBy(item => item.IslandId, StringComparer.Ordinal))
                {
                    var activeAssignments = state.Assignments.Values
                        .Where(item => island.EntityIds.Contains(item.EntityId)
                            && item.Lifecycle == AssignmentLifecycle.Active
                            && !CampaignAssignmentKinds.IsOperationalSim(item))
                        .OrderBy(item => item.AssignmentId.Value)
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
                        var fenceFrame = new ServiceFrame { Fence = quiesced.Fence };
                        _ = string.IsNullOrWhiteSpace(assignment.WorkerClientInstanceId)
                            ? sessions.TryQueueForCampaign(campaignId.ToString(), fenceFrame)
                            : sessions.TryQueueForClient(
                                campaignId.ToString(),
                                assignment.WorkerClientInstanceId,
                                fenceFrame);
                    }
                }
            }
            if (workers.Count > 0 && schedulingFocus.Count == 0)
            {
                state = await PlaceOperationalAssignmentsAsync(
                    state,
                    campaignId,
                    evaluation,
                    workers,
                    cancellationToken);
            }
            var applied = CampaignFidelityScheduler.Evaluate(state, request, workers, stableSampleCount) with
            {
                LoadAction = evaluation.LoadAction,
                Islands = evaluation.Islands,
                Transitions = schedulingFocus.Count == 0
                    ? evaluation.Transitions
                    : evaluation.Transitions
                        .Where(item => schedulingFocusStrings.Contains(item.EntityId))
                        .ToArray(),
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
            if (!gateAlreadyHeld)
            {
                _gate.Release();
            }
        }
    }

    private bool TryQueueHandoffAssignments(
        CampaignId campaignId,
        string clientInstanceId,
        IReadOnlyList<TacticalAssignment> assignments,
        out IReadOnlyList<TacticalAssignment> queued)
    {
        var accepted = new List<TacticalAssignment>();
        foreach (var assignment in assignments)
        {
            if (!sessions.TryQueueForClient(
                campaignId.ToString(),
                clientInstanceId,
                new ServiceFrame { Assignment = assignment }))
            {
                queued = accepted;
                return false;
            }
            accepted.Add(assignment);
        }
        queued = accepted;
        return true;
    }

    private async Task<CampaignState> ReleaseQueuedHandoffAssignmentsAsync(
        CampaignState state,
        IReadOnlyList<TacticalAssignment> assignments,
        CancellationToken cancellationToken)
    {
        foreach (var assignment in assignments)
        {
            var assignmentId = ParseScopedId(
                assignment.AssignmentId,
                "assignment:",
                value => new AssignmentId(value));
            if (!state.Assignments.TryGetValue(assignmentId, out var current)
                || current.Lifecycle == AssignmentLifecycle.Released)
            {
                continue;
            }
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(assignmentId),
                current.Revision,
                new SetAssignmentLifecycleCommand(
                    assignmentId,
                    current.AuthorityEpoch,
                    AssignmentLifecycle.Released,
                    state.Time),
                $"cancel-{assignment.AssignmentId}-session-lost-before-queue"), cancellationToken);
        }
        return state;
    }

    private async Task<(CampaignState State, TacticalAssignment Assignment)> PrepareHandoffCoreAsync(
        CampaignState state,
        CampaignHandoffRequest request,
        CancellationToken cancellationToken)
    {
        if (CampaignAssignmentKinds.IsOperationalSimBatch(request.BatchId))
        {
            throw new CampaignStoreConflict(
                "reserved-operational-assignment",
                "The general tactical handoff path cannot create Operational worker assignments.");
        }

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
        var role = request.Role;
        if (role == CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow)
        {
            if (entity.Fidelity != FidelityState.Tactical)
            {
                throw new CampaignStoreConflict(
                    "shadow-source-fidelity",
                    "A Shadow destination must cover an island that is already Tactical.");
            }
        }
        else
        {
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
        }
        var sourceCheckpoint = await FindLatestEntityCheckpointAsync(
            state,
            entityId,
            cancellationToken);
        state = await CommitAsync(state, Command(
            state,
            CampaignSubject.Assignment(assignmentId),
            EntityRevision.None,
            new AssignAuthorityCommand(
                assignmentId,
                entityId,
                role,
                new AuthorityEpoch(request.AuthorityEpoch),
                state.Time,
                request.BatchId,
                request.BatchSize,
                request.WorkerClientInstanceId),
            $"{request.IdempotencyKey}-assignment"), cancellationToken);
        entity = state.Entities[entityId];
        return (state, ToTacticalAssignment(
            campaignId,
            request,
            entity,
            sourceCheckpoint,
            state.Head,
            state.Time));
    }

    private async Task<CampaignBlobReference?> FindLatestEntityCheckpointAsync(
        CampaignState state,
        EntityId entityId,
        CancellationToken cancellationToken)
    {
        foreach (var assignment in state.Assignments.Values
            .Where(assignment => assignment.EntityId == entityId)
            .OrderByDescending(assignment => assignment.AuthorityEpoch.Value)
            .ThenByDescending(assignment => assignment.AssignmentId.Value))
        {
            var checkpoint = await journal.ReadLatestBlobReferenceAsync(
                state.Metadata.CampaignId,
                CampaignBlobPurpose.TacticalCheckpoint,
                $"assignment:{assignment.AssignmentId}/",
                cancellationToken);
            if (checkpoint is not null
                && await IsUsableTacticalCheckpointAsync(
                    checkpoint,
                    assignment,
                    cancellationToken))
            {
                return checkpoint;
            }
        }
        return null;
    }

    private async Task<bool> IsUsableTacticalCheckpointAsync(
        CampaignBlobReference checkpoint,
        AssignmentState assignment,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await journal.ReadBlobAsync(checkpoint.Sha256, cancellationToken);
            return JsonSerializer.Deserialize<OwnedSurfaceCheckpointPayload>(
                content,
                CheckpointJsonOptions) is { Unit: not null } parsed
                && string.Equals(
                    parsed.AssignmentId,
                    $"assignment:{assignment.AssignmentId}",
                    StringComparison.Ordinal)
                && parsed.AuthorityEpoch == assignment.AuthorityEpoch.Value
                && string.Equals(
                    parsed.Unit.EntityId,
                    $"unit:crimean-tom:{assignment.EntityId}",
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private async Task<CampaignBlobReference?> FindLatestAssignmentCheckpointAsync(
        CampaignState state,
        AssignmentId assignmentId,
        CancellationToken cancellationToken) =>
        await journal.ReadLatestBlobReferenceAsync(
            state.Metadata.CampaignId,
            CampaignBlobPurpose.TacticalCheckpoint,
            $"assignment:{assignmentId}/",
            cancellationToken);

    private static bool HasPreparedShadowDestination(
        CampaignState state,
        EntityId entityId,
        string batchId)
    {
        var sources = string.IsNullOrEmpty(batchId)
            ? state.Assignments.Values.Where(item => item.EntityId == entityId
                && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                && item.Lifecycle != AssignmentLifecycle.Released)
            : state.Assignments.Values.Where(item =>
                string.Equals(item.BatchId, batchId, StringComparison.Ordinal)
                && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                && item.Lifecycle != AssignmentLifecycle.Released);
        var sourceEntities = sources.Select(item => item.EntityId).Distinct().ToArray();
        if (sourceEntities.Length == 0)
        {
            return false;
        }
        return sourceEntities.All(id => state.Assignments.Values.Any(item =>
            item.EntityId == id
            && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow
            && item.Lifecycle == AssignmentLifecycle.Prepared));
    }

    private async Task<IslandMigrationPlan> RequireMigrationPlanAsync(
        CampaignState state,
        EntityId entityId,
        CancellationToken cancellationToken)
    {
        if (entityId.Value == Guid.Empty)
        {
            throw new CampaignStoreConflict("invalid-id", "A canonical identifier is invalid.");
        }
        var island = CampaignFidelityScheduler.Inspect(state).Islands
            .FirstOrDefault(item => item.EntityIds.Contains(entityId))
            ?? throw new CampaignStoreConflict("migration-island-missing", "The entity does not belong to a tactical interaction island.");
        var sources = island.EntityIds
            .Select(id => state.Assignments.Values
                .Where(item => item.EntityId == id
                    && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                    && item.Lifecycle != AssignmentLifecycle.Released)
                .OrderByDescending(item => item.AuthorityEpoch.Value)
                .ThenByDescending(item => item.AssignmentId.Value)
                .FirstOrDefault())
            .ToArray();
        var destinations = island.EntityIds
            .Select(id => state.Assignments.Values
                .Where(item => item.EntityId == id
                    && item.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow
                    && item.Lifecycle != AssignmentLifecycle.Released)
                .OrderByDescending(item => item.AuthorityEpoch.Value)
                .ThenByDescending(item => item.AssignmentId.Value)
                .FirstOrDefault())
            .ToArray();
        if (destinations.All(item => item is null)
            && sources.All(item => item is { Lifecycle: AssignmentLifecycle.Active }))
        {
            var reconstructed = await TryReconstructMigratedPlanAsync(
                state,
                island.EntityIds,
                sources.Cast<AssignmentState>().ToArray(),
                cancellationToken);
            if (reconstructed is not null)
            {
                return reconstructed;
            }
        }

        if (sources.Any(item => item is null) || sources.Length != island.EntityIds.Count)
        {
            throw new CampaignStoreConflict(
                "migration-source-incomplete",
                "Whole-island migration requires a complete Primary source island.");
        }
        if (sources.Any(item => item!.Lifecycle != AssignmentLifecycle.Quiesced))
        {
            throw new CampaignStoreConflict(
                "migration-source-not-quiesced",
                "Whole-island migration requires every source Primary to be Quiesced.");
        }
        if (destinations.Any(item => item is null)
            || destinations.Any(item => item!.Lifecycle != AssignmentLifecycle.Prepared)
            || destinations.Length != island.EntityIds.Count)
        {
            throw new CampaignStoreConflict(
                "migration-destination-not-prepared",
                "Whole-island migration requires a complete Shadow/Prepared destination.");
        }

        var sourceWorkers = sources.Select(item => item!.WorkerClientInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var destinationWorkers = destinations.Select(item => item!.WorkerClientInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sourceWorkers.Length != 1
            || destinationWorkers.Length != 1
            || string.IsNullOrWhiteSpace(sourceWorkers[0])
            || string.IsNullOrWhiteSpace(destinationWorkers[0]))
        {
            throw new CampaignStoreConflict(
                "island-worker-affinity-conflict",
                "Source and destination island members must each share one worker affinity.");
        }
        if (string.Equals(sourceWorkers[0], destinationWorkers[0], StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict(
                "shadow-destination-same-worker",
                "A migration destination must be a different worker than the source.");
        }

        var sourceBatchIds = sources.Select(item => item!.BatchId).Distinct(StringComparer.Ordinal).ToArray();
        var destinationBatchIds = destinations.Select(item => item!.BatchId).Distinct(StringComparer.Ordinal).ToArray();
        var sourceBatchSizes = sources.Select(item => item!.BatchSize).Distinct().ToArray();
        var destinationBatchSizes = destinations.Select(item => item!.BatchSize).Distinct().ToArray();
        if (sourceBatchIds.Length != 1
            || destinationBatchIds.Length != 1
            || sourceBatchSizes.Length != 1
            || destinationBatchSizes.Length != 1
            || sourceBatchSizes[0] != island.EntityIds.Count
            || destinationBatchSizes[0] != island.EntityIds.Count
            || (island.EntityIds.Count == 1 && (!string.IsNullOrEmpty(sourceBatchIds[0]) || !string.IsNullOrEmpty(destinationBatchIds[0])))
            || (island.EntityIds.Count > 1 && (string.IsNullOrEmpty(sourceBatchIds[0])
                || string.IsNullOrEmpty(destinationBatchIds[0]))))
        {
            throw new CampaignStoreConflict(
                "migration-batch-inconsistent",
                "Whole-island migration requires consistent source and destination batch membership.");
        }

        var members = new List<IslandMigrationMemberPlan>();
        foreach (var source in sources.Select(item => item!).OrderBy(item => item.AssignmentId.Value))
        {
            var destination = destinations.First(item => item!.EntityId == source.EntityId)!;
            if (destination.AuthorityEpoch.Value <= source.AuthorityEpoch.Value)
            {
                throw new CampaignStoreConflict(
                    "island-migration-fenced",
                    "A destination epoch must be strictly higher than the source epoch.");
            }
            var checkpoint = await FindLatestAssignmentCheckpointAsync(state, source.AssignmentId, cancellationToken)
                ?? throw new CampaignStoreConflict(
                    "migration-checkpoint-missing",
                    "Every source member must have a durable final checkpoint before staging.");
            members.Add(new IslandMigrationMemberPlan(source, destination, checkpoint));
        }

        return new IslandMigrationPlan(
            sourceWorkers[0],
            destinationWorkers[0],
            NormalizeIslandAnchorEntityId(island.EntityIds),
            false,
            null,
            members);
    }

    private void RequireLiveDestinationWorker(string campaignId, string destinationClientInstanceId)
    {
        var destination = sessions.Snapshots(campaignId)
            .FirstOrDefault(worker => string.Equals(
                worker.ClientInstanceId,
                destinationClientInstanceId,
                StringComparison.Ordinal));
        if (destination is null || !CampaignWorkerHealth.CanReceiveNewAssignments(destination))
        {
            throw new CampaignStoreConflict(
                "migration-destination-ineligible",
                "The destination worker is no longer healthy enough to receive the island.");
        }
    }

    private async Task<IslandMigrationPlan?> TryReconstructMigratedPlanAsync(
        CampaignState state,
        IReadOnlyList<EntityId> islandEntityIds,
        IReadOnlyList<AssignmentState> livePrimaries,
        CancellationToken cancellationToken)
    {
        var destinationWorkers = livePrimaries
            .Select(item => item.WorkerClientInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (livePrimaries.Count != islandEntityIds.Count
            || destinationWorkers.Length != 1
            || string.IsNullOrWhiteSpace(destinationWorkers[0]))
        {
            return null;
        }

        var liveByEntity = livePrimaries.ToDictionary(item => item.EntityId);
        var events = await journal.ReadEventsAsync(
            state.Metadata.CampaignId,
            CommitPosition.Origin,
            cancellationToken);
        var matching = events
            .Where(item => item.Data is TacticalIslandMigratedEvent migrated
                && MatchesPromotedDestinations(migrated.Migration.Members, liveByEntity))
            .OrderByDescending(item => item.CommitPosition.Value)
            .FirstOrDefault();
        if (matching?.Data is not TacticalIslandMigratedEvent migratedEvent)
        {
            return null;
        }

        var members = new List<IslandMigrationMemberPlan>();
        foreach (var member in migratedEvent.Migration.Members.OrderBy(item => item.DestinationAssignmentId.Value))
        {
            if (!state.Assignments.TryGetValue(member.SourceAssignmentId, out var source)
                || !state.Assignments.TryGetValue(member.DestinationAssignmentId, out var destination)
                || source.EntityId != member.EntityId
                || destination.EntityId != member.EntityId
                || source.AuthorityEpoch != member.SourceAuthorityEpoch
                || destination.AuthorityEpoch != member.DestinationAuthorityEpoch
                || source.Lifecycle != AssignmentLifecycle.Released
                || destination.Role != CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                || destination.Lifecycle != AssignmentLifecycle.Active)
            {
                throw new CampaignStoreConflict(
                    "island-migration-incomplete",
                    "A previously migrated island no longer has consistent destination ownership.");
            }

            var checkpoint = await FindLatestAssignmentCheckpointAsync(state, member.SourceAssignmentId, cancellationToken)
                ?? throw new CampaignStoreConflict(
                    "migration-checkpoint-missing",
                    "Every source member must have a durable final checkpoint before activation grants.");
            members.Add(new IslandMigrationMemberPlan(source, destination, checkpoint));
        }

        return new IslandMigrationPlan(
            members[0].Source.WorkerClientInstanceId,
            destinationWorkers[0],
            NormalizeIslandAnchorEntityId(islandEntityIds),
            true,
            matching.CommitPosition.Value,
            members);
    }

    private async Task<CampaignIslandMigrationResult?> TryReplayCommittedMigrationAsync(
        CampaignState state,
        EntityId requestedEntityId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var causalCommandId = new CommandId(KeyedGuid(
            state.Metadata.CampaignId.Value,
            $"migrate-island-{idempotencyKey}"));
        var events = await journal.ReadEventsAsync(
            state.Metadata.CampaignId,
            CommitPosition.Origin,
            cancellationToken);
        var committed = events.SingleOrDefault(item => item.CausalCommandId == causalCommandId);
        if (committed is null)
        {
            return null;
        }
        if (committed.Data is not TacticalIslandMigratedEvent migrated
            || migrated.Migration.Members.All(item => item.EntityId != requestedEntityId))
        {
            throw new CampaignStoreConflict(
                "idempotency-conflict",
                "The migration idempotency key was already committed for another operation or island.");
        }

        var destinationAssignments = migrated.Migration.Members
            .Select(member => state.Assignments.TryGetValue(member.DestinationAssignmentId, out var assignment)
                ? assignment
                : null)
            .ToArray();
        if (destinationAssignments.Any(item => item is null))
        {
            throw new CampaignStoreConflict(
                "island-migration-incomplete",
                "A committed migration references a destination assignment that is no longer canonical.");
        }
        var destinationWorkers = destinationAssignments
            .Select(item => item!.WorkerClientInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (destinationWorkers.Length != 1 || string.IsNullOrWhiteSpace(destinationWorkers[0]))
        {
            throw new CampaignStoreConflict(
                "island-worker-affinity-conflict",
                "A committed migration does not retain one destination worker affinity.");
        }

        var destinationStillCurrent = migrated.Migration.Members.All(member =>
            state.Assignments.TryGetValue(member.SourceAssignmentId, out var source)
            && source.Lifecycle == AssignmentLifecycle.Released
            && state.Assignments.TryGetValue(member.DestinationAssignmentId, out var destination)
            && destination.EntityId == member.EntityId
            && destination.AuthorityEpoch == member.DestinationAuthorityEpoch
            && destination.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
            && destination.Lifecycle == AssignmentLifecycle.Active
            && !state.Assignments.Values.Any(candidate =>
                candidate.EntityId == member.EntityId
                && candidate.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                && candidate.Lifecycle != AssignmentLifecycle.Released
                && candidate.AuthorityEpoch.Value > member.DestinationAuthorityEpoch.Value));
        var phase = "migration-superseded";
        if (destinationStillCurrent)
        {
            var plan = await TryReconstructMigratedPlanAsync(
                state,
                migrated.Migration.Members.Select(item => item.EntityId).OrderBy(item => item).ToArray(),
                destinationAssignments.Select(item => item!).OrderBy(item => item.EntityId).ToArray(),
                cancellationToken)
                ?? throw new CampaignStoreConflict(
                    "island-migration-incomplete",
                    "A committed migration could not reconstruct its current destination authority.");
            phase = QueueActivationGrants(state, plan, idempotencyKey)
                ? "activation-granted"
                : "migration-committed-grants-pending";
        }

        return new CampaignIslandMigrationResult(
            destinationWorkers[0],
            committed.CommitPosition.Value,
            phase,
            migrated.Migration.Members
                .Select(item => item.DestinationAssignmentId.ToString())
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool MatchesPromotedDestinations(
        IReadOnlyList<TacticalIslandMigrationMember> members,
        IReadOnlyDictionary<EntityId, AssignmentState> liveByEntity)
    {
        if (members.Count != liveByEntity.Count)
        {
            return false;
        }

        return members.All(member =>
            liveByEntity.TryGetValue(member.EntityId, out var live)
            && live.AssignmentId == member.DestinationAssignmentId
            && live.AuthorityEpoch == member.DestinationAuthorityEpoch);
    }

    private bool QueueStagingFrames(CampaignState state, IslandMigrationPlan plan, string idempotencyKey)
    {
        var queued = true;
        foreach (var member in plan.Members)
        {
            var frame = new ServiceFrame
            {
                StageFinalCheckpoint = new StageFinalCheckpoint
                {
                    CampaignId = state.Metadata.CampaignId.ToString(),
                    DestinationAssignmentId = $"assignment:{member.Destination.AssignmentId}",
                    EntityId = $"unit:crimean-tom:{member.Destination.EntityId}",
                    DestinationAuthorityEpoch = member.Destination.AuthorityEpoch.Value,
                    SourceAssignmentId = $"assignment:{member.Source.AssignmentId}",
                    SourceAuthorityEpoch = member.Source.AuthorityEpoch.Value,
                    ExpectedDestinationAssignmentRevision = member.Destination.Revision.Value,
                    ExpectedEntityRevision = state.Entities[member.Source.EntityId].Revision.Value,
                    CheckpointSha256 = member.SourceCheckpoint.Sha256,
                    CheckpointUri = $"/content/{member.SourceCheckpoint.Sha256}",
                    CheckpointLength = member.SourceCheckpoint.Length,
                    IdempotencyKey = $"{idempotencyKey}-{member.Destination.EntityId}-stage",
                    BatchId = member.Destination.BatchId,
                    BatchSize = member.Destination.BatchSize,
                },
            };
            if (!sessions.TryQueueForClient(
                state.Metadata.CampaignId.ToString(),
                plan.DestinationClientInstanceId,
                frame))
            {
                queued = false;
            }
        }
        return queued;
    }

    private bool QueueActivationGrants(CampaignState state, IslandMigrationPlan plan, string idempotencyKey)
    {
        var queued = true;
        if (plan.MigrationCommitPosition is not { } commitPosition)
        {
            return false;
        }
        foreach (var member in plan.Members)
        {
            var hash = member.SourceCheckpoint.Sha256;
            if (string.IsNullOrWhiteSpace(hash) || member.SourceCheckpoint.Length <= 0)
            {
                queued = false;
                continue;
            }
            var frame = new ServiceFrame
            {
                ActivationGrant = new ActivationGrant
                {
                    AssignmentId = $"assignment:{member.Destination.AssignmentId}",
                    EntityId = $"unit:crimean-tom:{member.Destination.EntityId}",
                    AuthorityEpoch = member.Destination.AuthorityEpoch.Value,
                    ExpectedEntityRevision = state.Entities[member.Destination.EntityId].Revision.Value,
                    StagedCheckpointSha256 = hash,
                    MigrationCommitPosition = commitPosition,
                    IdempotencyKey = OwnedMutationContracts.ResolveActivationIdempotencyKey(
                        idempotencyKey,
                        member.Destination.EntityId.ToString()),
                    BatchId = member.Destination.BatchId,
                    BatchSize = member.Destination.BatchSize,
                },
            };
            if (!sessions.TryQueueForClient(
                state.Metadata.CampaignId.ToString(),
                plan.DestinationClientInstanceId,
                frame))
            {
                queued = false;
            }
        }
        return queued;
    }

    // Process-local only until migrate-tactical-island commits. Companion outbound
    // staging frames are durable and resent after a pre-commit coordinator restart.
    private bool AllMembersStaged(IslandMigrationPlan plan) =>
        plan.Members.All(member =>
        {
            var staged = _stagedCheckpoints.GetValueOrDefault(
                StagedKey(member.Destination.AssignmentId, member.Destination.AuthorityEpoch.Value));
            return staged is { Ready: true }
                && string.Equals(staged.CheckpointSha256, member.SourceCheckpoint.Sha256, StringComparison.OrdinalIgnoreCase);
        });

    private static string StagedKey(AssignmentId assignmentId, long epoch) =>
        $"{assignmentId}:{epoch}";

    private async Task AcceptStagedCheckpointAsync(
        CampaignId campaignId,
        string clientInstanceId,
        FinalCheckpointStaged staged,
        CancellationToken cancellationToken)
    {
        var assignmentId = ParseScopedId(staged.AssignmentId, "assignment:", value => new AssignmentId(value));
        var state = await RecoverStateAsync(campaignId, cancellationToken);
        if (!state.Assignments.TryGetValue(assignmentId, out var assignment))
        {
            throw new CampaignStoreConflict("assignment-missing", "A staged assignment does not exist.");
        }
        if (assignment.AuthorityEpoch.Value != staged.AuthorityEpoch)
        {
            throw new CampaignStoreConflict("assignment-fenced", "Staged assignment epoch is stale.");
        }
        if (assignment.Role != CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow
            || assignment.Lifecycle != AssignmentLifecycle.Prepared)
        {
            if (assignment.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                && assignment.Lifecycle == AssignmentLifecycle.Active)
            {
                IslandMigrationPlan migrated;
                try
                {
                    migrated = await RequireMigrationPlanAsync(state, assignment.EntityId, cancellationToken);
                }
                catch (CampaignStoreConflict exception) when (
                    exception.Code is not "island-migration-incomplete"
                        and not "migration-checkpoint-missing")
                {
                    throw new CampaignStoreConflict(
                        "final-checkpoint-precondition",
                        "Final-checkpoint staging is only accepted for a Prepared Shadow.");
                }
                if (!migrated.AlreadyMigrated)
                {
                    throw new CampaignStoreConflict(
                        "final-checkpoint-precondition",
                        "Final-checkpoint staging is only accepted for a Prepared Shadow.");
                }
                _ = QueueActivationGrants(
                    state,
                    migrated,
                    ResolveMigrationOperationIdempotencyKey(
                        staged.IdempotencyKey,
                        assignment.EntityId,
                        $"activate-{assignmentId}-{staged.AuthorityEpoch}"));
                return;
            }
            throw new CampaignStoreConflict(
                "final-checkpoint-precondition",
                "Final-checkpoint staging is only accepted for a Prepared Shadow.");
        }
        if (!string.Equals(assignment.WorkerClientInstanceId, clientInstanceId, StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict(
                "assignment-worker-fenced",
                "The companion client does not own this tactical assignment.");
        }

        IslandMigrationPlan? plan = null;
        try
        {
            plan = await RequireMigrationPlanAsync(state, assignment.EntityId, cancellationToken);
        }
        catch (CampaignStoreConflict)
        {
            return;
        }

        if (plan.AlreadyMigrated)
        {
            if (staged.Ready)
            {
                _ = QueueActivationGrants(
                    state,
                    plan,
                    ResolveMigrationOperationIdempotencyKey(
                        staged.IdempotencyKey,
                        assignment.EntityId,
                        $"activate-{assignmentId}-{staged.AuthorityEpoch}"));
            }

            return;
        }

        if (!staged.Ready)
        {
            TryReleaseMigrationOrchestrationForPlan(campaignId, plan);
            return;
        }

        var member = plan.Members.FirstOrDefault(item => item.Destination.AssignmentId == assignmentId)
            ?? throw new CampaignStoreConflict("migration-batch-inconsistent", "Staged assignment is not part of the island plan.");
        if (!string.Equals(staged.StagedCheckpointSha256, member.SourceCheckpoint.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryReleaseMigrationOrchestrationForPlan(campaignId, plan);
            throw new CampaignStoreConflict(
                "checkpoint-hash-mismatch",
                "The staged checkpoint hash does not match the source final checkpoint.");
        }

        _stagedCheckpoints[StagedKey(assignmentId, staged.AuthorityEpoch)] = new StagedMigrationCheckpoint(
            assignmentId,
            staged.AuthorityEpoch,
            staged.StagedCheckpointSha256,
            staged.RealizedRevision,
            true);

        if (!AllMembersStaged(plan))
        {
            return;
        }

        var operationIdempotencyKey = ResolveMigrationOperationIdempotencyKey(
            staged.IdempotencyKey,
            assignment.EntityId,
            $"migrate-{assignmentId}-{staged.AuthorityEpoch}");
        var orchestration = CreateMigrationOrchestrationLease(
            campaignId,
            plan,
            operationIdempotencyKey);
        await EnsureMigrationCooldownAsync(state, cancellationToken);
        if (!_migrationAdmission.IsHeld(orchestration) && !TryBeginMigrationOrchestration(orchestration))
        {
            throw CreateMigrationOrchestrationConflict(orchestration);
        }

        await CommitMigrationAndGrantAsync(
            state,
            plan,
            orchestration,
            operationIdempotencyKey,
            cancellationToken);
    }

    private static string ResolveMigrationOperationIdempotencyKey(
        string stagedIdempotencyKey,
        EntityId entityId,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(stagedIdempotencyKey))
        {
            return fallback;
        }

        var stagingSuffix = $"-{entityId}-stage";
        return stagedIdempotencyKey.EndsWith(stagingSuffix, StringComparison.Ordinal)
            ? stagedIdempotencyKey[..^stagingSuffix.Length]
            : stagedIdempotencyKey;
    }

    private async Task<CampaignIslandMigrationResult> CommitMigrationAndGrantAsync(
        CampaignState state,
        IslandMigrationPlan plan,
        MigrationOrchestrationLease orchestration,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var campaignId = state.Metadata.CampaignId;
            RequireLiveDestinationWorker(campaignId.ToString(), plan.DestinationClientInstanceId);
            _replicaHashes.ThrowIfUnsafeDivergence(
                state,
                plan.Members.Select(item => item.Source.EntityId));
            var batchId = plan.Members[0].Destination.BatchId;
            if (string.IsNullOrEmpty(batchId))
            {
                batchId = $"migration:{plan.Members[0].Source.EntityId}";
            }
            var commandMembers = plan.Members.Select(member => new TacticalIslandMigrationMember(
                member.Source.AssignmentId,
                member.Source.AuthorityEpoch,
                member.Source.Revision,
                member.Destination.AssignmentId,
                member.Destination.AuthorityEpoch,
                member.Destination.Revision,
                member.Source.EntityId,
                state.Entities[member.Source.EntityId].Revision)).ToArray();
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Campaign(campaignId),
                state.CampaignRevision,
                new MigrateTacticalIslandCommand(
                    batchId,
                    commandMembers,
                    state.Time,
                    "stage-final-checkpoints-and-promote-prepared-shadow"),
                $"migrate-island-{idempotencyKey}"), cancellationToken);
            var migrated = await RequireMigrationPlanAsync(state, plan.Members[0].Source.EntityId, cancellationToken);
            var grantsQueued = QueueActivationGrants(state, migrated, idempotencyKey);
            return new CampaignIslandMigrationResult(
                plan.DestinationClientInstanceId,
                state.Head.Value,
                grantsQueued ? "activation-granted" : "migration-committed-grants-pending",
                plan.Members.Select(item => item.Destination.AssignmentId.ToString()).ToArray());
        }
        finally
        {
            EndMigrationOrchestration(orchestration);
        }
    }

    private bool TryBeginMigrationOrchestration(MigrationOrchestrationLease orchestration) =>
        _migrationAdmission.TryAcquire(orchestration);

    private void EndMigrationOrchestration(MigrationOrchestrationLease orchestration) =>
        _migrationAdmission.Release(orchestration);

    private void TryReleaseMigrationOrchestrationForPlan(CampaignId campaignId, IslandMigrationPlan plan) =>
        EndMigrationOrchestration(CreateMigrationOrchestrationLease(campaignId, plan));

    private static string NormalizeIslandAnchorEntityId(IReadOnlyList<EntityId> islandEntityIds) =>
        islandEntityIds.OrderBy(id => id.Value).First().ToString();

    private static MigrationOrchestrationLease CreateMigrationOrchestrationLease(
        CampaignId campaignId,
        IslandMigrationPlan plan,
        string idempotencyKey = "") =>
        new(
            campaignId,
            plan.IslandAnchorEntityId,
            plan.SourceClientInstanceId,
            plan.DestinationClientInstanceId,
            idempotencyKey);

    private async Task EnsureMigrationCooldownAsync(
        CampaignState state,
        CancellationToken cancellationToken)
    {
        var cooldown = await CampaignMigrationCooldown.FindActiveCooldownAsync(
            state,
            journal,
            _migrationPolicy.CooldownQuanta,
            cancellationToken);
        if (cooldown is null)
        {
            return;
        }

        throw new CampaignStoreConflict(
            "migration-cooldown-active",
            $"Tactical-island migration is cooling down until quanta {cooldown.CooldownEndsAtQuanta} "
            + $"(last migration at {cooldown.LastMigrationEffectiveAtQuanta}, now {cooldown.CurrentTimeQuanta}).");
    }

    private static CampaignStoreConflict CreateMigrationOrchestrationConflict(
        MigrationOrchestrationLease orchestration) =>
        new(
            "migration-orchestration-busy",
            $"Migration orchestration is already active for campaign {orchestration.CampaignId} "
            + $"or worker set {orchestration.SourceWorkerClientInstanceId}->{orchestration.DestinationWorkerClientInstanceId}.");

    public async Task<CampaignFallbackResult> ResolveExpiredAssignmentsAsync(
        CampaignId campaignId,
        CampaignTime target,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_pausedCampaigns.ContainsKey(campaignId))
            {
                throw new CampaignStoreConflict(
                    "campaign-paused",
                    "Operator pause is in effect; campaign time does not advance until resume.");
            }
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            if (target.Quanta < state.Time.Quanta)
            {
                throw new CampaignStoreConflict("fallback-time-stale", "Fallback cannot run before campaign time.");
            }
            var reports = new List<CampaignFallbackAssignment>();
            while (true)
            {
                var pending = state.Assignments.Values
                    .Where(item => item.Lifecycle != AssignmentLifecycle.Released
                        && !CampaignAssignmentKinds.IsOperationalSim(item)
                        && !sessions.HasLiveTacticalLease(
                            campaignId.ToString(),
                            item.WorkerClientInstanceId))
                    .Select(item => new
                    {
                        Assignment = item,
                        Deadline = item.EffectiveAt.Quanta + CampaignSchema.MaximumTacticalAssignmentQuanta,
                    })
                    .Where(item => item.Deadline <= target.Quanta)
                    .OrderBy(item => item.Deadline)
                    .ThenBy(item => item.Assignment.AssignmentId.Value)
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
                    var stillFenced = state.Assignments.Values.Any(item =>
                        item.EntityId == entity.EntityId
                        && !CampaignAssignmentKinds.IsOperationalSim(item)
                        && item.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing
                            or AssignmentLifecycle.Quiesced);
                    if (entity.Fidelity != FidelityState.Operational && !stillFenced)
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
                CanonicalHash(state),
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
        var state = await RecoverStateAsync(campaignId, cancellationToken);
        var assignment = state.Assignments[assignmentId];
        if (assignment.AuthorityEpoch.Value != prepared.AuthorityEpoch)
        {
            throw new CampaignStoreConflict("assignment-fenced", "Prepared assignment epoch is stale.");
        }
        if (assignment.Lifecycle != AssignmentLifecycle.Preparing)
        {
            return;
        }
        if (CampaignAssignmentKinds.IsOperationalSim(assignment))
        {
            if (!prepared.Ready)
            {
                var rejectedBatch = state.Assignments.Values
                    .Where(item => CampaignAssignmentKinds.IsOperationalSim(item)
                        && string.Equals(item.BatchId, assignment.BatchId, StringComparison.Ordinal)
                        && item.Lifecycle != AssignmentLifecycle.Released)
                    .OrderBy(item => item.AssignmentId.Value)
                    .ToArray();
                foreach (var rejectedCandidate in rejectedBatch)
                {
                    var rejected = state.Assignments[rejectedCandidate.AssignmentId];
                    state = await CommitAsync(state, Command(
                        state,
                        CampaignSubject.Assignment(rejected.AssignmentId),
                        rejected.Revision,
                        new SetAssignmentLifecycleCommand(
                            rejected.AssignmentId,
                            rejected.AuthorityEpoch,
                            AssignmentLifecycle.Released,
                            state.Time),
                        $"reject-operational-{rejected.AssignmentId}-{rejected.AuthorityEpoch.Value}"), cancellationToken);
                }
                return;
            }

            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(assignmentId),
                assignment.Revision,
                new SetAssignmentLifecycleCommand(
                    assignmentId,
                    assignment.AuthorityEpoch,
                    AssignmentLifecycle.Prepared,
                    state.Time),
                $"prepared-operational-{prepared.AssignmentId}-{prepared.AuthorityEpoch}"), cancellationToken);
            assignment = state.Assignments[assignmentId];
            var batch = state.Assignments.Values
                .Where(item => string.Equals(item.BatchId, assignment.BatchId, StringComparison.Ordinal)
                    && item.Lifecycle != AssignmentLifecycle.Released)
                .OrderBy(item => item.AssignmentId.Value)
                .ToArray();
            if (batch.Length != assignment.BatchSize
                || batch.Any(item => item.BatchSize != assignment.BatchSize
                    || item.Lifecycle != AssignmentLifecycle.Prepared))
            {
                return;
            }

            foreach (var member in batch)
            {
                var current = state.Assignments[member.AssignmentId];
                state = await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Assignment(current.AssignmentId),
                    current.Revision,
                    new SetAssignmentLifecycleCommand(
                        current.AssignmentId,
                        current.AuthorityEpoch,
                        AssignmentLifecycle.Active,
                        state.Time),
                    $"activate-operational-{current.AssignmentId}-{current.AuthorityEpoch.Value}"), cancellationToken);
            }

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
                .OrderBy(item => item.AssignmentId.Value)
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
        var state = await RecoverStateAsync(campaignId, cancellationToken);
        var assignment = state.Assignments[assignmentId];
        if (assignment.AuthorityEpoch.Value != checkpoint.AuthorityEpoch)
        {
            throw new CampaignStoreConflict("checkpoint-fenced", "Checkpoint assignment epoch is stale.");
        }
        if (checkpoint.EntityRevision < 0)
        {
            throw new CampaignStoreConflict(
                "checkpoint-revision-conflict",
                "Checkpoint entity revision cannot be negative.");
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
        _replicaHashes.Observe(campaignId, assignment, checkpoint.CheckpointSha256, checkpoint.EntityRevision);
    }

    private async Task CommitTacticalEventAsync(
        CampaignId campaignId,
        TacticalEventProposal proposal,
        CancellationToken cancellationToken)
    {
        var assignmentId = ParseScopedId(proposal.AssignmentId, "assignment:", value => new AssignmentId(value));
        var state = await RecoverStateAsync(campaignId, cancellationToken);
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
        var applyKinematics = proposal.Kind is TacticalEventKind.Movement
            or TacticalEventKind.Checkpoint
            or TacticalEventKind.StateSync;
        var coordinate = applyKinematics
            ? GeoCoordinate.FromMicrodegrees(proposal.LatitudeMicrodegrees, proposal.LongitudeMicrodegrees)
            : current.Coordinate;
        var heading = applyKinematics ? proposal.HeadingMillidegrees : current.Heading.Value;
        var telegraph = applyKinematics ? proposal.Telegraph : current.Telegraph;
        var resources = current.Resources;
        if (proposal.Kind is TacticalEventKind.Resources
            or TacticalEventKind.Checkpoint
            or TacticalEventKind.StateSync)
        {
            resources = resources
                .SetItem(ResourceKind.Fuel, ResourceAmount.FromMilliUnits(proposal.FuelMilliunits))
                .SetItem(ResourceKind.Stores, ResourceAmount.FromMilliUnits(proposal.StoresMilliunits));
        }
        var damage = proposal.Kind is TacticalEventKind.Damage
            or TacticalEventKind.Destroyed
            or TacticalEventKind.Checkpoint
            or TacticalEventKind.StateSync
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
            var holdForMigration = HasPreparedShadowDestination(state, assignment.EntityId, assignment.BatchId);
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
                    .OrderBy(item => item.AssignmentId.Value)
                    .ToArray();
                if (batch.Length != assignment.BatchSize
                    || batch.Any(item => item.BatchSize != assignment.BatchSize
                        || item.Lifecycle != AssignmentLifecycle.Quiesced))
                {
                    return;
                }
                if (holdForMigration)
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
            if (holdForMigration)
            {
                await CommitAsync(state, Command(
                    state,
                    CampaignSubject.Assignment(assignmentId),
                    assignment.Revision,
                    new SetAssignmentLifecycleCommand(
                        assignmentId,
                        assignment.AuthorityEpoch,
                        AssignmentLifecycle.Quiesced,
                        state.Time),
                    $"quiesced-{proposal.IdempotencyKey}"), cancellationToken);
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

    public async Task<(AuthorityFence Fence, string WorkerClientInstanceId)> BeginQuiesceAsync(
        CampaignQuiesceRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var campaignId = ParseId(request.CampaignId, value => new CampaignId(value));
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            var assignmentId = ParseScopedId(request.AssignmentId, "assignment:", value => new AssignmentId(value));
            var worker = state.Assignments.TryGetValue(assignmentId, out var assignment)
                ? assignment.WorkerClientInstanceId
                : string.Empty;
            var quiesced = await BeginQuiesceCoreAsync(
                state,
                request,
                resolveExpectedRevisionFromWorker: false,
                cancellationToken);
            return (quiesced.Fence, worker);
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
        var result = await journal.AppendPreparedAsync(
            lease,
            state,
            command,
            campaignEvent,
            DateTimeOffset.UtcNow,
            cancellationToken);
        BroadcastCommittedEvent(result.CampaignEvent);
        var committed = CampaignKernel.Apply(state, result.CampaignEvent);
        _stateCache[committed.Metadata.CampaignId] = new CachedCampaignState(committed, CanonicalHash: null);
        if (committed.Head.Value > 0 && committed.Head.Value % SnapshotIntervalCommits == 0)
        {
            _ = await journal.SavePreparedSnapshotAsync(
                lease,
                committed,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        return committed;
    }

    private async Task<CampaignState> RecoverStateAsync(
        CampaignId campaignId,
        CancellationToken cancellationToken)
    {
        if (_stateCache.TryGetValue(campaignId, out var cached)
            && await journal.ReadHeadAsync(campaignId, cancellationToken) == cached.State.Head)
        {
            return cached.State;
        }

        var recovery = await journal.RecoverAsync(campaignId, cancellationToken);
        _stateCache[campaignId] = new CachedCampaignState(recovery.State, recovery.CanonicalHash);
        return recovery.State;
    }

    private string CanonicalHash(CampaignState state)
    {
        if (_stateCache.TryGetValue(state.Metadata.CampaignId, out var cached)
            && ReferenceEquals(cached.State, state)
            && cached.CanonicalHash is { } existing)
        {
            return existing;
        }

        var computed = CampaignCanonicalSerializer.ComputeHash(state);
        _stateCache.AddOrUpdate(
            state.Metadata.CampaignId,
            _ => new CachedCampaignState(state, computed),
            (_, current) => ReferenceEquals(current.State, state)
                ? current with { CanonicalHash = computed }
                : current);
        return computed;
    }

    private void BroadcastCommittedEvent(CrimeanTom.Campaign.Contracts.CommittedCampaignEvent campaignEvent)
    {
        var json = CampaignContractJson.Serialize(campaignEvent);
        _ = sessions.TryBroadcastForCampaign(
            campaignEvent.CampaignId.ToString(),
            new ServiceFrame
            {
                CommittedEvent = new CrimeanTom.Campaign.Transport.V1.CommittedCampaignEvent
                {
                    CommitPosition = campaignEvent.CommitPosition.Value,
                    EventId = campaignEvent.EventId.ToString(),
                    EventJson = ByteString.CopyFrom(json),
                    EventSha256 = Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant(),
                },
            });
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
        policy.BacklogDepth,
        policy.TacticalReserveCapacity);

    private static CampaignWorkerView ToWorkerView(
        CampaignState state,
        CampaignSessionSnapshot worker,
        int campaignSafeCapacity)
    {
        var admission = CampaignWorkerAdmissionPolicy.Calculate(
            state,
            worker,
            campaignSafeCapacity);
        return new(
            worker.SessionId,
            worker.ClientInstanceId,
            worker.GameSessionId,
            worker.ConnectedAt,
            worker.LastAppliedCommitPosition,
            worker.ReceivedFrameCount,
            worker.RuntimeFingerprintSha256,
            worker.CatalogFingerprintSha256,
            worker.PlayerTaskforceId,
            worker.PlayerCoalitionId,
            worker.OwnedMaterializedEntityCount,
            worker.OwnedActiveEntityCount,
            worker.OwnedRetainedSuspendedEntityCount,
            worker.OwnedRecoveryRequiredEntityCount,
            worker.OwnedOperationalEntityCount,
            worker.TacticalEntityCapacity,
            worker.TacticalReserveCapacity,
            worker.OperationalEntityCapacity,
            worker.OperationalReserveCapacity,
            worker.Health,
            worker.Eligibility,
            worker.WorkerKind,
            worker.PlayerSessionBound,
            admission.OccupiedTacticalEntityCount,
            admission.PromotionHeadroom,
            admission.MigrationOverlapHeadroom,
            worker.MaterializationProfiles.Count,
            worker.CouplingRelationships.Count);
    }

    private static CampaignCalibrationReport ToCalibrationView(
        CampaignState state,
        CampaignFidelityScheduler.SchedulerEvaluation evaluation)
    {
        var fenced = evaluation.Islands.Count(island => island.UnsafeToSplit || island.Engaged);
        var demoted = evaluation.Islands.Count(island =>
            (island.UnsafeToSplit || island.Engaged)
            && island.EntityIds.Any(entityId =>
                state.Entities[entityId].Fidelity != FidelityState.Tactical)
            && state.Assignments.Values.Any(assignment =>
                island.EntityIds.Contains(assignment.EntityId)
                && assignment.Role == CrimeanTom.Campaign.Contracts.AssignmentRole.Primary
                && assignment.Lifecycle is AssignmentLifecycle.Active or AssignmentLifecycle.Quiescing));
        return CampaignCalibration.Evaluate(
            state.RecentOperationalOutcomes,
            evaluation.TargetTicksPerWallSecond,
            fenced,
            demoted);
    }

    private async Task<CampaignState> YieldOperationalAssignmentsAsync(
        CampaignState state,
        CampaignId campaignId,
        CampaignFidelityScheduler.SchedulerEvaluation evaluation,
        IReadOnlyList<CampaignSessionSnapshot> workers,
        CancellationToken cancellationToken)
    {
        var workerByClient = workers.ToDictionary(
            worker => worker.ClientInstanceId,
            StringComparer.Ordinal);
        foreach (var island in evaluation.Islands.OrderBy(item => item.IslandId, StringComparer.Ordinal))
        {
            var live = state.Assignments.Values
                .Where(item => island.EntityIds.Contains(item.EntityId)
                    && CampaignAssignmentKinds.IsOperationalSim(item)
                    && item.Lifecycle is AssignmentLifecycle.Preparing
                        or AssignmentLifecycle.Prepared
                        or AssignmentLifecycle.Active
                        or AssignmentLifecycle.Quiescing)
                .OrderBy(item => item.AssignmentId.Value)
                .ToArray();
            if (live.Length == 0)
            {
                continue;
            }

            var owners = live
                .Select(item => workerByClient.GetValueOrDefault(item.WorkerClientInstanceId))
                .Where(worker => worker is not null)
                .Cast<CampaignSessionSnapshot>()
                .ToArray();
            var yieldForPlayer = island.DesiredFidelity == FidelityState.Tactical;
            var yieldForDrain = owners.Any(CampaignWorkerHealth.MustEvacuate);
            var yieldForMissingOwner = live.Any(item => !workerByClient.ContainsKey(item.WorkerClientInstanceId));
            var yieldForCampaignDrain = evaluation.Request.Health == SchedulerHealthState.Draining
                || evaluation.Request.Eligibility == SimulationSourceEligibility.DrainOnly;
            if (!yieldForPlayer && !yieldForDrain && !yieldForMissingOwner && !yieldForCampaignDrain)
            {
                continue;
            }

            foreach (var assignment in live)
            {
                state = await ReleaseOperationalAssignmentAsync(state, assignment, cancellationToken);
                var fence = new ServiceFrame
                {
                    Fence = new AuthorityFence
                    {
                        AssignmentId = $"assignment:{assignment.AssignmentId}",
                        EntityId = $"unit:crimean-tom:{assignment.EntityId}",
                        FencedAuthorityEpoch = assignment.AuthorityEpoch.Value,
                        EffectiveTimeQuanta = state.Time.Quanta,
                        IdempotencyKey = $"operational-yield-{assignment.AssignmentId}-{assignment.AuthorityEpoch.Value}",
                        Reason = yieldForPlayer
                            ? "player-interest-preempts-operational-worker"
                            : yieldForDrain
                                ? "operational-worker-evacuation"
                                : yieldForMissingOwner
                                    ? "operational-worker-disconnected"
                                    : "campaign-drain",
                    },
                };
                _ = sessions.TryQueueForClient(
                    campaignId.ToString(),
                    assignment.WorkerClientInstanceId,
                    fence);
            }
        }

        return state;
    }

    private async Task<CampaignState> PlaceOperationalAssignmentsAsync(
        CampaignState state,
        CampaignId campaignId,
        CampaignFidelityScheduler.SchedulerEvaluation evaluation,
        IReadOnlyList<CampaignSessionSnapshot> workers,
        CancellationToken cancellationToken)
    {
        if (evaluation.Request.Health == SchedulerHealthState.Draining
            || evaluation.Request.Eligibility != SimulationSourceEligibility.Eligible)
        {
            return state;
        }

        var plannedLoads = workers
            .Where(worker => CampaignWorkerKind.IsOperational(worker.WorkerKind))
            .ToDictionary(
                worker => worker.ClientInstanceId,
                worker => Math.Max(
                    worker.OwnedOperationalEntityCount,
                    state.Assignments.Values
                        .Where(assignment => assignment.Lifecycle != AssignmentLifecycle.Released
                            && CampaignAssignmentKinds.IsOperationalSim(assignment)
                            && string.Equals(
                                assignment.WorkerClientInstanceId,
                                worker.ClientInstanceId,
                                StringComparison.Ordinal))
                        .Select(assignment => assignment.EntityId)
                        .Distinct()
                        .Count()),
                StringComparer.Ordinal);
        foreach (var island in evaluation.Islands
            .Where(item => item.DesiredFidelity != FidelityState.Tactical)
            .OrderBy(item => item.IslandId, StringComparer.Ordinal))
        {
            var liveAssignments = state.Assignments.Values
                .Where(item => island.EntityIds.Contains(item.EntityId)
                    && item.Lifecycle != AssignmentLifecycle.Released)
                .ToArray();
            if (liveAssignments.Any(item => !CampaignAssignmentKinds.IsOperationalSim(item)))
            {
                continue;
            }

            if (liveAssignments.Any(item => item.Lifecycle is AssignmentLifecycle.Active
                or AssignmentLifecycle.Quiescing or AssignmentLifecycle.Preparing
                or AssignmentLifecycle.Prepared))
            {
                continue;
            }

            var eligibleEntityIds = island.EntityIds
                .Where(entityId =>
                {
                    var entity = state.Entities[entityId];
                    return entity.Fidelity != FidelityState.Tactical
                        && entity.Lifecycle is not (EntityLifecycle.Destroyed or EntityLifecycle.Retired);
                })
                .OrderBy(item => item)
                .ToArray();
            if (eligibleEntityIds.Length == 0)
            {
                continue;
            }

            var islandWorker = SelectOperationalPlacementWorker(
                eligibleEntityIds,
                liveAssignments,
                workers,
                plannedLoads);
            if (islandWorker is null)
            {
                continue;
            }

            var generation = state.Assignments.Values.Count(item => island.EntityIds.Contains(item.EntityId)) + 1;
            var batchId = CampaignAssignmentKinds.OperationalSimBatchId($"{island.IslandId}:{generation}");
            var batchSize = eligibleEntityIds.Length;
            var createdAssignmentIds = new List<AssignmentId>();
            foreach (var entityId in eligibleEntityIds)
            {
                var entity = state.Entities[entityId];
                var attempt = state.Assignments.Values.Count(item => item.EntityId == entityId) + 1;
                var assignmentGuid = KeyedGuid(
                    campaignId.Value,
                    $"operational-assignment-{island.IslandId}-{entityId}-{attempt}");
                var authorityEpoch = state.Assignments.Values
                    .Where(item => item.EntityId == entityId)
                    .Select(item => item.AuthorityEpoch.Value)
                    .DefaultIfEmpty(0)
                    .Max() + 1;
                var idempotencyKey = $"operational-handoff-{island.IslandId}-{entityId}-{attempt}";
                var handoffRequest = new CampaignHandoffRequest(
                    campaignId.ToString(),
                    entityId.ToString(),
                    $"unit:crimean-tom:{entityId}",
                    $"assignment:{assignmentGuid:N}",
                    authorityEpoch,
                    idempotencyKey,
                    string.Empty,
                    islandWorker.CatalogFingerprintSha256,
                    entity.CatalogUnitId,
                    string.Empty,
                    entity.CatalogLoadoutId,
                    string.Empty,
                    batchId,
                    batchSize,
                    islandWorker.ClientInstanceId);
                var prepared = await PrepareOperationalHandoffCoreAsync(state, handoffRequest, cancellationToken);
                state = prepared.State;
                var assignmentId = ParseScopedId(
                    handoffRequest.AssignmentId,
                    "assignment:",
                    value => new AssignmentId(value));
                createdAssignmentIds.Add(assignmentId);
                if (sessions.TryQueueForClient(
                    campaignId.ToString(),
                    islandWorker.ClientInstanceId,
                    new ServiceFrame
                    {
                        OperationalAssignment = prepared.Assignment,
                    }))
                {
                    plannedLoads[islandWorker.ClientInstanceId]++;
                    continue;
                }

                foreach (var createdId in createdAssignmentIds)
                {
                    var assignment = state.Assignments[createdId];
                    if (assignment.Lifecycle == AssignmentLifecycle.Released)
                    {
                        continue;
                    }

                    state = await CommitAsync(state, Command(
                        state,
                        CampaignSubject.Assignment(createdId),
                        assignment.Revision,
                        new SetAssignmentLifecycleCommand(
                            createdId,
                            assignment.AuthorityEpoch,
                            AssignmentLifecycle.Released,
                            state.Time),
                        $"cancel-{idempotencyKey}-{createdId}-session-lost-before-queue"), cancellationToken);
                }
                break;
            }
        }

        return state;
    }

    public async Task ReleaseOperationalWorkerAssignmentsAsync(
        CampaignId campaignId,
        string clientInstanceId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await RecoverStateAsync(campaignId, cancellationToken);
            var live = state.Assignments.Values
                .Where(item => CampaignAssignmentKinds.IsOperationalSim(item)
                    && item.Lifecycle != AssignmentLifecycle.Released
                    && string.Equals(
                        item.WorkerClientInstanceId,
                        clientInstanceId,
                        StringComparison.Ordinal))
                .OrderBy(item => item.AssignmentId.Value)
                .ToArray();
            foreach (var assignment in live)
            {
                var current = state.Assignments[assignment.AssignmentId];
                if (current.Lifecycle == AssignmentLifecycle.Released)
                {
                    continue;
                }

                state = await ReleaseOperationalAssignmentAsync(state, current, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CampaignState> ReleaseOperationalAssignmentAsync(
        CampaignState state,
        AssignmentState assignment,
        CancellationToken cancellationToken)
    {
        var current = state.Assignments[assignment.AssignmentId];
        if (current.Lifecycle == AssignmentLifecycle.Released)
        {
            return state;
        }

        if (current.Lifecycle == AssignmentLifecycle.Active)
        {
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(current.AssignmentId),
                current.Revision,
                new SetAssignmentLifecycleCommand(
                    current.AssignmentId,
                    current.AuthorityEpoch,
                    AssignmentLifecycle.Quiescing,
                    state.Time),
                $"operational-quiesce-{current.AssignmentId}-{current.AuthorityEpoch.Value}"), cancellationToken);
            current = state.Assignments[current.AssignmentId];
        }

        if (current.Lifecycle is AssignmentLifecycle.Preparing
            or AssignmentLifecycle.Prepared
            or AssignmentLifecycle.Quiescing)
        {
            state = await CommitAsync(state, Command(
                state,
                CampaignSubject.Assignment(current.AssignmentId),
                current.Revision,
                new SetAssignmentLifecycleCommand(
                    current.AssignmentId,
                    current.AuthorityEpoch,
                    AssignmentLifecycle.Released,
                    state.Time),
                $"operational-release-{current.AssignmentId}-{current.AuthorityEpoch.Value}"), cancellationToken);
        }

        return state;
    }

    private async Task<(CampaignState State, CrimeanTom.Campaign.Transport.V1.OperationalAssignment Assignment)>
        PrepareOperationalHandoffCoreAsync(
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
        if (entity.Fidelity == FidelityState.Tactical
            || entity.Lifecycle is EntityLifecycle.Destroyed or EntityLifecycle.Retired)
        {
            throw new CampaignStoreConflict(
                "operational-handoff-source-fidelity",
                "An operational worker assignment cannot cover a Tactical or retired entity.");
        }

        state = await CommitAsync(state, Command(
            state,
            CampaignSubject.Assignment(assignmentId),
            EntityRevision.None,
            new AssignAuthorityCommand(
                assignmentId,
                entityId,
                request.Role,
                new AuthorityEpoch(request.AuthorityEpoch),
                state.Time,
                request.BatchId,
                request.BatchSize,
                request.WorkerClientInstanceId),
            $"{request.IdempotencyKey}-assignment"), cancellationToken);
        entity = state.Entities[entityId];
        return (state, new CrimeanTom.Campaign.Transport.V1.OperationalAssignment
        {
            CampaignId = campaignId.ToString(),
            AssignmentId = request.AssignmentId,
            EntityId = request.GameEntityId,
            AuthorityEpoch = request.AuthorityEpoch,
            HandoffCommitPosition = state.Head.Value,
            HandoffTimeQuanta = state.Time.Quanta,
            ExpectedEntityRevision = entity.Revision.Value,
            IdempotencyKey = request.IdempotencyKey,
            BatchId = request.BatchId,
            BatchSize = request.BatchSize,
        });
    }

    private static CampaignSessionSnapshot? SelectOperationalPlacementWorker(
        IReadOnlyList<EntityId> islandEntityIds,
        IReadOnlyList<AssignmentState> liveAssignments,
        IReadOnlyList<CampaignSessionSnapshot> workers,
        IReadOnlyDictionary<string, int> plannedWorkerLoads)
    {
        var existingOwners = liveAssignments
            .Select(assignment => assignment.WorkerClientInstanceId)
            .Where(owner => !string.IsNullOrWhiteSpace(owner))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (existingOwners.Length > 1)
        {
            throw new CampaignStoreConflict(
                "island-worker-affinity-conflict",
                "An operational island has live assignments on multiple workers.");
        }

        var missingEntityCount = islandEntityIds.Count - liveAssignments
            .Select(assignment => assignment.EntityId)
            .Distinct()
            .Count();
        return workers
            .Where(worker => CampaignWorkerKind.IsOperational(worker.WorkerKind))
            .Where(worker => existingOwners.Length == 0 || string.Equals(
                worker.ClientInstanceId,
                existingOwners[0],
                StringComparison.Ordinal))
            .Where(CampaignWorkerHealth.CanReceiveNewAssignments)
            .Select(worker => new
            {
                Worker = worker,
                Capacity = Math.Max(
                    0,
                    worker.OperationalEntityCapacity - worker.OperationalReserveCapacity),
                Occupied = Math.Max(0, plannedWorkerLoads.GetValueOrDefault(worker.ClientInstanceId)),
            })
            .Where(candidate => missingEntityCount <= candidate.Capacity - candidate.Occupied)
            .OrderBy(candidate => checked(
                candidate.Occupied * 1_000_000L)
                / Math.Max(1, candidate.Capacity))
            .ThenBy(candidate => candidate.Occupied)
            .ThenBy(candidate => candidate.Worker.ClientInstanceId, StringComparer.Ordinal)
            .Select(candidate => candidate.Worker)
            .FirstOrDefault();
    }

    private static CampaignSessionSnapshot? SelectPlacementWorker(
        CampaignState state,
        IReadOnlyList<EntityId> islandEntityIds,
        IReadOnlyList<AssignmentState> liveAssignments,
        IReadOnlyList<CampaignSessionSnapshot> workers,
        IReadOnlyDictionary<string, int> plannedWorkerLoads,
        int destinationCapacity,
        string? preferredWorkerClientInstanceId = null)
    {
        var existingOwners = liveAssignments
            .Select(assignment => assignment.WorkerClientInstanceId)
            .Where(owner => !string.IsNullOrWhiteSpace(owner))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (existingOwners.Length > 1)
        {
            throw new CampaignStoreConflict(
                "island-worker-affinity-conflict",
                "A tactical interaction island has live assignments on multiple workers.");
        }
        var missingEntityCount = islandEntityIds.Count - liveAssignments
            .Select(assignment => assignment.EntityId)
            .Distinct()
            .Count();
        return workers
            .Where(worker => string.IsNullOrWhiteSpace(preferredWorkerClientInstanceId)
                || string.Equals(
                    worker.ClientInstanceId,
                    preferredWorkerClientInstanceId,
                    StringComparison.Ordinal))
            .Where(worker => existingOwners.Length == 0 || string.Equals(
                worker.ClientInstanceId,
                existingOwners[0],
                StringComparison.Ordinal))
            .Where(worker =>
                (existingOwners.Length == 0
                    ? CampaignWorkerHealth.CanReceiveNewAssignments(worker)
                    : !CampaignWorkerHealth.MustEvacuate(worker))
                && !string.IsNullOrWhiteSpace(worker.PlayerTaskforceId)
                && islandEntityIds.All(entityId => worker.MaterializationProfiles.Any(profile => string.Equals(
                    profile.UnitTypeId,
                    state.Entities[entityId].CatalogUnitId,
                    StringComparison.Ordinal))))
            .Select(worker => new
            {
                Worker = worker,
                Admission = CampaignWorkerAdmissionPolicy.Calculate(
                    worker,
                    destinationCapacity,
                    plannedWorkerLoads.GetValueOrDefault(worker.ClientInstanceId)),
            })
            .Where(candidate => candidate.Admission.CanAdmitPromotion(missingEntityCount))
            .OrderBy(candidate => checked(
                candidate.Admission.OccupiedTacticalEntityCount * 1_000_000L)
                / Math.Max(1, candidate.Admission.PromotionCapacityLimit))
            .ThenBy(candidate => candidate.Worker.OwnedRecoveryRequiredEntityCount)
            .ThenBy(candidate => candidate.Admission.OccupiedTacticalEntityCount)
            .ThenBy(candidate => candidate.Worker.ClientInstanceId, StringComparer.Ordinal)
            .Select(candidate => candidate.Worker)
            .FirstOrDefault();
    }

    private static void RequirePreferredSourceWorker(
        CampaignState state,
        IReadOnlyList<EntityId> islandEntityIds,
        CampaignSessionSnapshot preferredWorker,
        IReadOnlyDictionary<string, int> plannedWorkerLoads,
        int destinationCapacity)
    {
        if (string.IsNullOrWhiteSpace(preferredWorker.PlayerTaskforceId)
            || preferredWorker.TacticalEntityCapacity <= 0
            || preferredWorker.OwnedRecoveryRequiredEntityCount > 0
            || !CampaignWorkerHealth.CanReceiveNewAssignments(preferredWorker)
            || !islandEntityIds.All(entityId => preferredWorker.MaterializationProfiles.Any(profile => string.Equals(
                profile.UnitTypeId,
                state.Entities[entityId].CatalogUnitId,
                StringComparison.Ordinal))))
        {
            throw new CampaignStoreConflict(
                "source-worker-ineligible",
                "The preferred source worker cannot materialize the complete island.");
        }
        var admission = CampaignWorkerAdmissionPolicy.Calculate(
            preferredWorker,
            destinationCapacity,
            plannedWorkerLoads.GetValueOrDefault(preferredWorker.ClientInstanceId));
        if (!admission.CanAdmitPromotion(islandEntityIds.Count))
        {
            throw new CampaignStoreConflict(
                "source-worker-capacity",
                "The preferred source worker does not have promotion headroom for the island.");
        }
    }

    private static CampaignSessionSnapshot ResolveShadowDestinationWorker(
        CampaignState state,
        IReadOnlyList<EntityId> islandEntityIds,
        string sourceClientInstanceId,
        IReadOnlyList<CampaignSessionSnapshot> workers,
        IReadOnlyDictionary<string, int> plannedWorkerLoads,
        int destinationCapacity,
        string? preferredDestinationClientInstanceId)
    {
        var selected = SelectShadowDestinationWorker(
            state,
            islandEntityIds,
            sourceClientInstanceId,
            workers,
            plannedWorkerLoads,
            destinationCapacity);
        if (string.IsNullOrWhiteSpace(preferredDestinationClientInstanceId))
        {
            return selected;
        }
        if (!string.Equals(
            preferredDestinationClientInstanceId,
            selected.ClientInstanceId,
            StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict(
                "shadow-destination-mismatch",
                "The preferred Shadow destination worker does not match deterministic admission ordering.");
        }
        var preferred = workers.FirstOrDefault(worker => string.Equals(
            worker.ClientInstanceId,
            preferredDestinationClientInstanceId,
            StringComparison.Ordinal))
            ?? throw new CampaignStoreConflict(
                "shadow-destination-unavailable",
                "The preferred Shadow destination worker is not connected.");
        RequireShadowDestinationCandidate(
            state,
            islandEntityIds,
            sourceClientInstanceId,
            preferred,
            plannedWorkerLoads,
            destinationCapacity);
        return preferred;
    }

    private static void RequireShadowDestinationCandidate(
        CampaignState state,
        IReadOnlyList<EntityId> islandEntityIds,
        string sourceClientInstanceId,
        CampaignSessionSnapshot worker,
        IReadOnlyDictionary<string, int> plannedWorkerLoads,
        int destinationCapacity)
    {
        if (string.Equals(worker.ClientInstanceId, sourceClientInstanceId, StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict(
                "shadow-destination-same-worker",
                "Shadow preparation requires a distinct connected destination worker.");
        }
        if (string.IsNullOrWhiteSpace(worker.PlayerTaskforceId)
            || !CampaignWorkerHealth.CanReceiveShadow(worker)
            || worker.TacticalEntityCapacity <= 0
            || worker.OwnedRecoveryRequiredEntityCount > 0
            || !islandEntityIds.All(entityId => worker.MaterializationProfiles.Any(profile => string.Equals(
                profile.UnitTypeId,
                state.Entities[entityId].CatalogUnitId,
                StringComparison.Ordinal))))
        {
            throw new CampaignStoreConflict(
                "shadow-destination-ineligible",
                "The preferred Shadow destination worker cannot materialize the complete island.");
        }
        var admission = CampaignWorkerAdmissionPolicy.Calculate(
            worker,
            destinationCapacity,
            plannedWorkerLoads.GetValueOrDefault(worker.ClientInstanceId));
        if (!admission.CanAdmitMigrationOverlap(islandEntityIds.Count))
        {
            throw new CampaignStoreConflict(
                "shadow-destination-headroom",
                "The preferred Shadow destination worker does not have migration-overlap reserve for the island.");
        }
    }

    private static CampaignSessionSnapshot SelectShadowDestinationWorker(
        CampaignState state,
        IReadOnlyList<EntityId> islandEntityIds,
        string sourceClientInstanceId,
        IReadOnlyList<CampaignSessionSnapshot> workers,
        IReadOnlyDictionary<string, int> plannedWorkerLoads,
        int destinationCapacity)
    {
        var connected = workers
            .Where(worker => !string.Equals(
                worker.ClientInstanceId,
                sourceClientInstanceId,
                StringComparison.Ordinal))
            .ToArray();
        if (connected.Length == 0)
        {
            throw new CampaignStoreConflict(
                "shadow-destination-same-worker",
                "Shadow preparation requires a distinct connected destination worker.");
        }

        var compatible = connected.Where(worker =>
            !string.IsNullOrWhiteSpace(worker.PlayerTaskforceId)
            && islandEntityIds.All(entityId => worker.MaterializationProfiles.Any(profile => string.Equals(
                profile.UnitTypeId,
                state.Entities[entityId].CatalogUnitId,
                StringComparison.Ordinal)))).ToArray();
        if (compatible.Length == 0)
        {
            throw new CampaignStoreConflict(
                "shadow-destination-incompatible",
                "No distinct connected worker can materialize the complete island.");
        }

        var eligible = compatible
            .Where(CampaignWorkerHealth.CanReceiveShadow)
            .Where(worker => worker.TacticalEntityCapacity > 0
                && worker.OwnedRecoveryRequiredEntityCount == 0)
            .Select(worker => new
            {
                Worker = worker,
                Admission = CampaignWorkerAdmissionPolicy.Calculate(
                    worker,
                    destinationCapacity,
                    plannedWorkerLoads.GetValueOrDefault(worker.ClientInstanceId)),
            })
            .ToArray();
        if (eligible.Length == 0)
        {
            throw new CampaignStoreConflict(
                "shadow-destination-ineligible",
                "No distinct compatible destination worker is healthy and eligible.");
        }

        var admitted = eligible
            .Where(candidate => candidate.Admission.CanAdmitMigrationOverlap(islandEntityIds.Count))
            .OrderBy(candidate => candidate.Admission.OccupiedTacticalEntityCount)
            .ThenBy(candidate => candidate.Worker.OwnedRecoveryRequiredEntityCount)
            .ThenBy(candidate => candidate.Worker.ClientInstanceId, StringComparer.Ordinal)
            .Select(candidate => candidate.Worker)
            .FirstOrDefault();
        return admitted ?? throw new CampaignStoreConflict(
            "shadow-destination-headroom",
            "The destination worker does not have enough migration-overlap reserve for the island.");
    }

    private static TacticalAssignment ToTacticalAssignment(
        CampaignState state,
        AssignmentState assignment,
        CampaignSessionSnapshot destination,
        string idempotencyKey,
        CampaignBlobReference? sourceCheckpoint)
    {
        var entity = state.Entities[assignment.EntityId];
        var profile = destination.MaterializationProfiles.First(item => string.Equals(
            item.UnitTypeId,
            entity.CatalogUnitId,
            StringComparison.Ordinal));
        return ToTacticalAssignment(
            state.Metadata.CampaignId,
            new CampaignHandoffRequest(
                state.Metadata.CampaignId.ToString(),
                assignment.EntityId.ToString(),
                $"unit:crimean-tom:{assignment.EntityId}",
                $"assignment:{assignment.AssignmentId:N}",
                assignment.AuthorityEpoch.Value,
                idempotencyKey,
                destination.RuntimeFingerprintSha256,
                destination.CatalogFingerprintSha256,
                profile.UnitTypeId,
                profile.VariantId,
                profile.LoadoutId,
                destination.PlayerTaskforceId,
                assignment.BatchId,
                assignment.BatchSize,
                destination.ClientInstanceId,
                assignment.Role),
            entity,
            sourceCheckpoint,
            state.Head,
            state.Time);
    }

    private static TacticalAssignment ToTacticalAssignment(
        CampaignId campaignId,
        CampaignHandoffRequest request,
        EntityState entity,
        CampaignBlobReference? sourceCheckpoint,
        CommitPosition head,
        CampaignTime time) => new()
    {
        CampaignId = campaignId.ToString(),
        AssignmentId = request.AssignmentId,
        EntityId = request.GameEntityId,
        CanonicalEntityId = entity.EntityId.ToString(),
        AuthorityEpoch = request.AuthorityEpoch,
        Role = ToTransportRole(request.Role),
        HandoffCommitPosition = head.Value,
        HandoffTimeQuanta = time.Quanta,
        ExpectedEntityRevision = 0,
        IdempotencyKey = request.IdempotencyKey,
        RequiredRuntimeFingerprintSha256 = request.RequiredRuntimeFingerprintSha256,
        RequiredCatalogFingerprintSha256 = request.RequiredCatalogFingerprintSha256,
        CheckpointSha256 = sourceCheckpoint?.Sha256 ?? string.Empty,
        CheckpointUri = sourceCheckpoint is null
            ? string.Empty
            : $"/content/{sourceCheckpoint.Sha256}",
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
        InitialDamageMilliunits = entity.DamageMilliUnits,
        FocusAfterActivation = request.FocusAfterActivation,
    };

    private static CrimeanTom.Campaign.Transport.V1.AssignmentRole ToTransportRole(
        CrimeanTom.Campaign.Contracts.AssignmentRole role) => role switch
    {
        CrimeanTom.Campaign.Contracts.AssignmentRole.Primary =>
            CrimeanTom.Campaign.Transport.V1.AssignmentRole.Primary,
        CrimeanTom.Campaign.Contracts.AssignmentRole.Shadow =>
            CrimeanTom.Campaign.Transport.V1.AssignmentRole.Shadow,
        CrimeanTom.Campaign.Contracts.AssignmentRole.Observer =>
            CrimeanTom.Campaign.Transport.V1.AssignmentRole.Observer,
        _ => CrimeanTom.Campaign.Transport.V1.AssignmentRole.Unspecified,
    };

    private static string SchedulerRequestHash(
        CampaignSchedulerRequest request,
        IReadOnlyList<EntityId> observedEntityIds,
        IReadOnlyList<EntityId> controlledEntityIds,
        IReadOnlyList<CampaignSessionSnapshot> workers)
    {
        var observed = string.Join(',', observedEntityIds.Select(id => id.ToString()));
        var controlled = string.Join(',', controlledEntityIds.Select(id => id.ToString()));
        var coupling = string.Join('|', workers
            .OrderBy(worker => worker.ClientInstanceId, StringComparer.Ordinal)
            .SelectMany(worker => worker.CouplingRelationships.Select(relationship =>
                (worker.ClientInstanceId, Relationship: relationship)))
            .Where(item => item.Relationship.UnsafeToSplit)
            .OrderBy(item => item.ClientInstanceId, StringComparer.Ordinal)
            .ThenBy(item => item.Relationship.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Relationship.SourceEntityId, StringComparer.Ordinal)
            .ThenBy(item => item.Relationship.TargetEntityId, StringComparer.Ordinal)
            .ThenBy(item => item.Relationship.EvidenceId, StringComparer.Ordinal)
            .Select(item => string.Join(':',
                item.ClientInstanceId,
                item.Relationship.Kind,
                item.Relationship.SourceEntityId,
                item.Relationship.TargetEntityId,
                item.Relationship.EvidenceId,
                item.Relationship.GuidanceType,
                item.Relationship.MissingSamples)));
        var resources = string.Join('|', workers
            .OrderBy(worker => worker.ClientInstanceId, StringComparer.Ordinal)
            .Select(worker => $"{worker.ClientInstanceId}:{worker.WorkerKind},{worker.PlayerSessionBound},{worker.OwnedMaterializedEntityCount},{worker.OwnedActiveEntityCount},{worker.OwnedRetainedSuspendedEntityCount},{worker.OwnedRecoveryRequiredEntityCount},{worker.OwnedOperationalEntityCount},{worker.TacticalEntityCapacity},{worker.TacticalReserveCapacity},{worker.OperationalEntityCapacity},{worker.OperationalReserveCapacity},{worker.Health},{worker.Eligibility}"));
        var material = $"observed={observed}|controlled={controlled}|health={request.Health}|eligibility={request.Eligibility}|capacity={request.TacticalEntityCapacity}|reserve={request.TacticalReserveCapacity}|backlog={request.BacklogDepth}|preferred-worker={request.PreferredTacticalWorkerClientInstanceId}|resources={resources}|coupling={coupling}";
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

    private sealed record CachedCampaignState(CampaignState State, string? CanonicalHash);
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
    int BatchSize = 1,
    string WorkerClientInstanceId = "",
    CrimeanTom.Campaign.Contracts.AssignmentRole Role = CrimeanTom.Campaign.Contracts.AssignmentRole.Primary,
    bool FocusAfterActivation = false);

public sealed record CampaignShadowPreparationRequest(
    string CampaignId,
    string CanonicalEntityId,
    string IdempotencyKey,
    string? PreferredDestinationClientInstanceId = null);

public sealed record CampaignShadowPreparationResult(
    string DestinationClientInstanceId,
    IReadOnlyList<TacticalAssignment> Assignments);

public sealed record CampaignSourcePreparationRequest(
    string CampaignId,
    string CanonicalEntityId,
    string IdempotencyKey,
    string PreferredWorkerClientInstanceId);

public sealed record CampaignSourcePreparationResult(
    string PreferredWorkerClientInstanceId,
    IReadOnlyList<TacticalAssignment> Assignments,
    string Phase);

public sealed record CampaignIslandMigrationRequest(
    string CampaignId,
    string CanonicalEntityId,
    string IdempotencyKey);

public sealed record CampaignIslandMigrationResult(
    string DestinationClientInstanceId,
    long? MigrationCommitPosition,
    string Phase,
    IReadOnlyList<string> DestinationAssignmentIds);

internal sealed record StagedMigrationCheckpoint(
    AssignmentId AssignmentId,
    long AuthorityEpoch,
    string CheckpointSha256,
    long RealizedRevision,
    bool Ready);

internal sealed record IslandMigrationMemberPlan(
    AssignmentState Source,
    AssignmentState Destination,
    CampaignBlobReference SourceCheckpoint);

internal sealed record IslandMigrationPlan(
    string SourceClientInstanceId,
    string DestinationClientInstanceId,
    string IslandAnchorEntityId,
    bool AlreadyMigrated,
    long? MigrationCommitPosition,
    IReadOnlyList<IslandMigrationMemberPlan> Members);

public sealed record CampaignQuiesceRequest(
    string CampaignId,
    string AssignmentId,
    string GameEntityId,
    long ExpectedGameRevision,
    string IdempotencyKey);

public sealed record CampaignTacticalAssignmentRecoveryRequest(
    IReadOnlyList<string> MissingAssignmentIds,
    IReadOnlyList<string> ControlledAssignmentIds);

public sealed record CampaignTacticalAssignmentRecoveryView(
    int ReleasedAssignmentCount,
    IReadOnlyList<string> ReleasedAssignmentIds,
    CampaignSchedulerView? ReplacementSchedule);

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
    int OpeningOrderCount,
    int MaritimeEntityCount,
    int AirEntityCount,
    int RotaryWingEntityCount,
    int LogisticsEntityCount,
    int LandEntityCount,
    int GroundUnitCount,
    int InstallationEntityCount,
    int AbstractLandFormationCount,
    int CivilianTrafficCount,
    int FixedSiteCount,
    int ResidentStrategicSystemCount,
    int TacticalEligibleEntityCount,
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
    IReadOnlyList<CampaignStrategicObjectiveView> StrategicObjectives,
    IReadOnlyList<CampaignGroupCommitmentView> GroupCommitments,
    IReadOnlyList<CampaignOperationalPackageView> OperationalPackages,
    IReadOnlyList<CampaignCoalitionStrategyView> CoalitionStrategies,
    IReadOnlyList<CampaignAssignmentView> Assignments,
    IReadOnlyList<CampaignOperationalOutcomeView> RecentOperationalOutcomes,
    CampaignSchedulerView Scheduler,
    CampaignPerformanceView Performance,
    IReadOnlyList<CampaignWorkerView> Workers,
    CampaignCalibrationReport Calibration,
    CampaignReplicaConvergenceReport ReplicaConvergence);

public sealed record CampaignWorkerView(
    string SessionId,
    string ClientInstanceId,
    string GameSessionId,
    DateTimeOffset ConnectedAt,
    long LastAppliedCommitPosition,
    long ReceivedFrameCount,
    string RuntimeFingerprintSha256,
    string CatalogFingerprintSha256,
    string PlayerTaskforceId,
    string PlayerCoalitionId,
    int OwnedMaterializedEntityCount,
    int OwnedActiveEntityCount,
    int OwnedRetainedSuspendedEntityCount,
    int OwnedRecoveryRequiredEntityCount,
    int OwnedOperationalEntityCount,
    int TacticalEntityCapacity,
    int TacticalReserveCapacity,
    int OperationalEntityCapacity,
    int OperationalReserveCapacity,
    string Health,
    string Eligibility,
    string WorkerKind,
    bool PlayerSessionBound,
    int OccupiedTacticalEntityCount,
    int PromotionHeadroom,
    int MigrationOverlapHeadroom,
    int MaterializationProfileCount,
    int CouplingRelationshipCount);

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
    string? ActiveOrderKind,
    string? ActiveOrderDestinationRegionId,
    long Revision);

public sealed record CampaignStrategicObjectiveView(
    string ObjectiveId,
    string CoalitionId,
    string RegionId,
    string Kind,
    int PriorityMilliUnits,
    long CreatedAtQuanta,
    long Epoch,
    long Revision);

public sealed record CampaignGroupCommitmentView(
    string GroupId,
    string? ObjectiveId,
    long ExpiresAtQuanta,
    bool WithdrawLatched,
    long LatchReleaseAtQuanta,
    long Revision);

public sealed record CampaignOperationalPackageView(
    string PackageId,
    string CoalitionId,
    string ObjectiveId,
    string RegionId,
    long AssemblyAtQuanta,
    long ExecuteAtQuanta,
    long ExpiresAtQuanta,
    IReadOnlyList<CampaignOperationalPackageMemberView> Members,
    long Revision);

public sealed record CampaignOperationalPackageMemberView(
    string GroupId,
    string Role,
    long StartsAtQuanta);

public sealed record CampaignCoalitionStrategyView(
    string CoalitionId,
    long NextDirectorAtQuanta,
    long Epoch,
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
    string CatalogLoadoutId,
    IReadOnlyList<CampaignOperationalWeaponView> OperationalWeapons,
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

public sealed record CampaignOperationalWeaponView(
    string AmmunitionId,
    string TargetClassification,
    long MaximumRangeMeters);

public sealed record CampaignAssignmentView(
    string AssignmentId,
    string EntityId,
    long AuthorityEpoch,
    string Role,
    string Lifecycle,
    long ResolveByQuanta,
    long Revision,
    string BatchId,
    int BatchSize,
    string WorkerClientInstanceId,
    bool LeaseLive);

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

public sealed record CampaignPauseView(string CampaignId, bool Paused);
