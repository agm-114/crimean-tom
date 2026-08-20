using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Engine;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.IntegrationTests;

public sealed class DeterministicCampaignKernelIntegrationTests
{
    [Fact]
    public async Task Multi_region_theater_commits_deterministically_and_projects_only_coalition_knowledge()
    {
        var first = BuildTheater(reverseCollectionInputs: false);
        var second = BuildTheater(reverseCollectionInputs: true);

        Assert.Equal(3, first.State.Regions.Count);
        Assert.Equal(2, first.State.Bases.Count);
        Assert.Equal(3, first.State.Entities.Count);
        Assert.Equal(2, first.State.Groups.Count);
        Assert.Single(first.State.Orders);
        Assert.Single(first.State.Assignments);
        Assert.Equal(new CampaignTime(10), first.State.Time);
        Assert.Equal(first.State.Head.Value, first.State.CampaignRevision.Value);
        Assert.Equal(FidelityState.Tactical, first.State.Entities[Ids.BlueKnox].Fidelity);
        Assert.Equal(AssignmentRole.Primary, first.State.Assignments[Ids.Assignment].Role);

        var firstBytes = CampaignCanonicalSerializer.Serialize(first.State);
        var secondBytes = CampaignCanonicalSerializer.Serialize(second.State);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(CampaignCanonicalSerializer.ComputeHash(first.State), CampaignCanonicalSerializer.ComputeHash(second.State));
        Assert.Equal(first.Events.Select(item => item.EventId), second.Events.Select(item => item.EventId));
        var decoded = CampaignCanonicalSerializer.Deserialize(firstBytes);
        Assert.Equal(firstBytes, CampaignCanonicalSerializer.Serialize(decoded));

        var canonicalJson = Encoding.UTF8.GetString(firstBytes);
        Assert.Contains(Ids.RedTruth, canonicalJson, StringComparison.Ordinal);
        Assert.Contains(Ids.BlueTruth, canonicalJson, StringComparison.Ordinal);

        var projection = CampaignProjectionBuilder.ForCoalition(first.State, Ids.BlueCoalition);
        Assert.Equal(2, projection.FriendlyEntities.Count);
        Assert.Single(projection.Contacts);
        Assert.Equal(Ids.RedKrivak, projection.Contacts[0].ContactEntityId);
        Assert.Equal(Ids.BlackSea, projection.Contacts[0].EstimatedRegionId);
        Assert.DoesNotContain(projection.FriendlyEntities, item => item.EntityId == Ids.RedKrivak);

        var projectionJson = JsonSerializer.Serialize(projection);
        Assert.DoesNotContain("ClassifiedTruth", projectionJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Ids.RedTruth, projectionJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Ids.BlueTruth, projectionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("44000000", projectionJson, StringComparison.Ordinal);

        var stale = Command(
            90,
            CampaignSubject.Entity(Ids.BlueKnox),
            EntityRevision.None,
            new ChangeFidelityCommand(Ids.BlueKnox, FidelityState.Operational),
            effectiveAt: first.State.Time);
        var conflict = Assert.Throws<CampaignRuleViolation>(() => CampaignKernel.Commit(first.State, stale));
        Assert.Equal("revision-conflict", conflict.Code);
        Assert.Equal(first.Hash, CampaignCanonicalSerializer.ComputeHash(first.State));

        var simulate = Command(
            17,
            CampaignSubject.Campaign(Ids.Campaign),
            first.State.CampaignRevision,
            new SimulateCampaignCommand(new CampaignTime(30)),
            effectiveAt: new CampaignTime(30));
        var singleWorkerEvent = CampaignKernel.Decide(first.State, simulate, first.State.Head.Next(), workerCount: 1);
        var parallelWorkerEvent = CampaignKernel.Decide(first.State, simulate, first.State.Head.Next(), workerCount: 4);
        Assert.Equal(
            CampaignContractJson.Serialize(singleWorkerEvent),
            CampaignContractJson.Serialize(parallelWorkerEvent));
        var simulated = CampaignKernel.Apply(first.State, singleWorkerEvent);
        var parallel = CampaignKernel.Apply(second.State, parallelWorkerEvent);
        Assert.Equal(CampaignCanonicalSerializer.Serialize(simulated), CampaignCanonicalSerializer.Serialize(parallel));
        Assert.Equal(new CampaignTime(30), simulated.Time);
        Assert.Equal(Ids.Bosporus, simulated.Entities[Ids.BlueKnox].RegionId);
        Assert.Equal(Ids.Bosporus, simulated.Entities[Ids.BlueOiler].RegionId);
        Assert.Null(simulated.Groups[Ids.BlueGroup].ActiveOrderId);
        Assert.Equal(new CampaignTime(30), simulated.Orders[Ids.TransitOrder].CompletedAt);
        Assert.True(simulated.Entities[Ids.BlueKnox].Resources[ResourceKind.Fuel].MilliUnits < 800_000);
        Assert.True(simulated.Entities[Ids.BlueKnox].ReadinessMilliUnits < 1_000);
        Assert.True(simulated.Bases[Ids.RedBase].Resources[ResourceKind.Fuel].MilliUnits < 4_000_000);
        Assert.True(simulated.Bases[Ids.RedBase].Resources[ResourceKind.SpareParts].MilliUnits < 900_000);
        Assert.Equal(15, simulated.ScheduledActivities.Count);
        Assert.Equal(3, simulated.Intelligence.Count);

        var simulatedProjection = CampaignProjectionBuilder.ForCoalition(simulated, Ids.BlueCoalition);
        var simulatedProjectionJson = JsonSerializer.Serialize(simulatedProjection);
        Assert.DoesNotContain("ClassifiedTruth", simulatedProjectionJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Ids.RedTruth, simulatedProjectionJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Ids.BlueTruth, simulatedProjectionJson, StringComparison.Ordinal);

        var simulatedTheater = new TheaterResult(
            simulated,
            [.. first.Commands, simulate],
            [.. first.Events, singleWorkerEvent],
            CampaignCanonicalSerializer.ComputeHash(simulated));
        await ProveDurableJournalAsync(simulatedTheater);
        await ProvePersistenceBoundariesAsync(simulatedTheater);
        await ProveProcessCrashRecoveryAsync(simulatedTheater);
        await ProveIndependentWriterFencingAsync(simulatedTheater);
    }

    private static TheaterResult BuildTheater(bool reverseCollectionInputs)
    {
        var state = CampaignState.Create(new CampaignMetadata(
            Ids.Campaign,
            "Black Sea synthetic theater",
            0x4352494D45414E,
            "sha256:synthetic-catalog-v1",
            CampaignSchema.Version,
            CampaignSchema.AlgorithmVersion));
        var commands = new List<CampaignCommand>();
        var events = new List<CommittedCampaignEvent>();

        state = Commit(state, Command(1, CampaignSubject.Region(Ids.Odessa), EntityRevision.None,
            new RegisterRegionCommand(Ids.Odessa, "Odessa approaches", [])), commands, events);
        state = Commit(state, Command(2, CampaignSubject.Region(Ids.BlackSea), EntityRevision.None,
            new RegisterRegionCommand(Ids.BlackSea, "Central Black Sea", [Ids.Odessa])), commands, events);
        state = Commit(state, Command(3, CampaignSubject.Region(Ids.Bosporus), EntityRevision.None,
            new RegisterRegionCommand(Ids.Bosporus, "Bosporus approaches", [Ids.BlackSea])), commands, events);

        state = Commit(state, Command(4, CampaignSubject.Base(Ids.BlueBase), EntityRevision.None,
            new RegisterBaseCommand(Ids.BlueBase, "Constanta", Ids.BlueCoalition, Ids.Odessa,
                GeoCoordinate.FromMicrodegrees(44_180_000, 28_650_000),
                Resources(reverseCollectionInputs, (ResourceKind.Fuel, 5_000_000), (ResourceKind.Stores, 2_000_000)))), commands, events);
        state = Commit(state, Command(5, CampaignSubject.Base(Ids.RedBase), EntityRevision.None,
            new RegisterBaseCommand(Ids.RedBase, "Sevastopol", Ids.RedCoalition, Ids.BlackSea,
                GeoCoordinate.FromMicrodegrees(44_600_000, 33_530_000),
                Resources(reverseCollectionInputs, (ResourceKind.SpareParts, 900_000), (ResourceKind.Fuel, 4_000_000)))), commands, events);

        state = Commit(state, Command(6, CampaignSubject.Entity(Ids.BlueKnox), EntityRevision.None,
            new RegisterEntityCommand(Ids.BlueKnox, "USS Pharris", "usn_knox", Ids.BlueCoalition, Ids.Odessa,
                GeoCoordinate.FromMicrodegrees(44_100_000, 30_200_000), Ids.BlueBase, EntityLifecycle.Deployed,
                FidelityState.Strategic,
                Resources(reverseCollectionInputs, (ResourceKind.Fuel, 800_000), (ResourceKind.Munitions, 600_000)),
                Ids.BlueTruth)), commands, events);
        state = Commit(state, Command(7, CampaignSubject.Entity(Ids.BlueOiler), EntityRevision.None,
            new RegisterEntityCommand(Ids.BlueOiler, "USNS Neosho", "usn_neosho", Ids.BlueCoalition, Ids.Odessa,
                GeoCoordinate.FromMicrodegrees(44_090_000, 30_180_000), Ids.BlueBase, EntityLifecycle.Deployed,
                FidelityState.Strategic,
                Resources(reverseCollectionInputs, (ResourceKind.Stores, 1_200_000), (ResourceKind.Fuel, 8_000_000)),
                "BLUE-LOGISTICS-EXACT")), commands, events);
        state = Commit(state, Command(8, CampaignSubject.Entity(Ids.RedKrivak), EntityRevision.None,
            new RegisterEntityCommand(Ids.RedKrivak, "Rezkiy", "vmf_krivak", Ids.RedCoalition, Ids.Bosporus,
                GeoCoordinate.FromMicrodegrees(44_000_000, 34_000_000), Ids.RedBase, EntityLifecycle.Deployed,
                FidelityState.Operational,
                Resources(reverseCollectionInputs, (ResourceKind.Munitions, 700_000), (ResourceKind.Fuel, 750_000)),
                Ids.RedTruth)), commands, events);

        var members = reverseCollectionInputs
            ? new[] { Ids.BlueOiler, Ids.BlueKnox }
            : new[] { Ids.BlueKnox, Ids.BlueOiler };
        state = Commit(state, Command(9, CampaignSubject.Group(Ids.BlueGroup), EntityRevision.None,
            new FormGroupCommand(Ids.BlueGroup, "Task Group 20.1", Ids.BlueCoalition, members)), commands, events);
        state = Commit(state, Command(10, CampaignSubject.Group(Ids.BlueGroup), new EntityRevision(1),
            new IssueOrderCommand(Ids.TransitOrder, Ids.BlueGroup, OrderKind.Transit, Ids.Bosporus,
                new CampaignTime(5), new CampaignTime(100))), commands, events);

        state = Commit(state, Command(11,
            new CampaignSubject(CampaignSubjectKind.Weather, Ids.BlackSea.Value), EntityRevision.None,
            new SetWeatherCommand(Ids.BlackSea, 225_000, 12_500, 18_000, 4_250)), commands, events);
        state = Commit(state, Command(12,
            new CampaignSubject(CampaignSubjectKind.Intelligence, Ids.RedKrivak.Value), EntityRevision.None,
            new ReportIntelligenceCommand(Ids.BlueCoalition, Ids.RedKrivak, Ids.BlackSea,
                GeoCoordinate.FromMicrodegrees(43_500_000, 32_500_000), IntelligenceConfidence.Probable,
                CampaignTime.Origin)), commands, events);

        state = Commit(state, Command(13, CampaignSubject.Entity(Ids.BlueKnox), new EntityRevision(1),
            new ChangeFidelityCommand(Ids.BlueKnox, FidelityState.Tactical)), commands, events);
        state = Commit(state, Command(14, CampaignSubject.Assignment(Ids.Assignment), EntityRevision.None,
            new AssignAuthorityCommand(Ids.Assignment, Ids.BlueKnox, AssignmentRole.Primary,
                new AuthorityEpoch(7), CampaignTime.Origin)), commands, events);
        state = Commit(state, Command(15, CampaignSubject.Campaign(Ids.Campaign), state.CampaignRevision,
            new AdvanceClockCommand(new CampaignTime(10)), effectiveAt: new CampaignTime(10)), commands, events);
        state = Commit(state, Command(16, CampaignSubject.Group(Ids.RedGroup), EntityRevision.None,
            new FormGroupCommand(Ids.RedGroup, "Black Sea patrol", Ids.RedCoalition, [Ids.RedKrivak]),
            effectiveAt: state.Time), commands, events);

        return new TheaterResult(state, commands, events, CampaignCanonicalSerializer.ComputeHash(state));
    }

    private static CampaignState Commit(
        CampaignState state,
        CampaignCommand command,
        List<CampaignCommand> commands,
        List<CommittedCampaignEvent> events)
    {
        var commandCopy = CampaignContractJson.DeserializeCommand(CampaignContractJson.Serialize(command));
        Assert.Equal(command.Data.GetType(), commandCopy.Data.GetType());
        var campaignEvent = CampaignKernel.Decide(state, commandCopy, state.Head.Next());
        var eventCopy = CampaignContractJson.DeserializeEvent(CampaignContractJson.Serialize(campaignEvent));
        Assert.Equal(campaignEvent.EventId, eventCopy.EventId);
        Assert.Equal(campaignEvent.Data.GetType(), eventCopy.Data.GetType());
        commands.Add(commandCopy);
        events.Add(eventCopy);
        return CampaignKernel.Apply(state, eventCopy);
    }

    private static async Task ProveDurableJournalAsync(TheaterResult theater)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"crimean-tom-campaign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var journal = new SqliteCampaignJournal(Path.Combine(directory, "campaign.db"));
            await journal.InitializeAsync();
            await journal.CreateCampaignAsync(theater.State.Metadata);
            var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
            var coordinatorA = new CoordinatorId(Guid.Parse("a0000000-0000-0000-0000-000000000001"));
            var coordinatorB = new CoordinatorId(Guid.Parse("b0000000-0000-0000-0000-000000000001"));
            var lease = await journal.AcquireWriterAsync(Ids.Campaign, coordinatorA, now, TimeSpan.FromMinutes(2));

            var unavailable = await Assert.ThrowsAsync<CampaignStoreConflict>(() =>
                journal.AcquireWriterAsync(Ids.Campaign, coordinatorB, now, TimeSpan.FromMinutes(2)));
            Assert.Equal("writer-lease-unavailable", unavailable.Code);

            var durableState = CampaignState.Create(theater.State.Metadata);
            CampaignSnapshotInfo? snapshot = null;
            CampaignBlobReference? checkpoint = null;
            for (var index = 0; index < theater.Commands.Count; index++)
            {
                if (theater.Events[index].Data is CampaignSimulatedEvent simulation)
                {
                    var changedEntities = simulation.Entities.ToArray();
                    changedEntities[0] = changedEntities[0] with
                    {
                        ReadinessMilliUnits = Math.Max(0, changedEntities[0].ReadinessMilliUnits - 1),
                    };
                    var fabricated = theater.Events[index] with
                    {
                        Data = simulation with { Entities = changedEntities },
                    };
                    var rejected = await Assert.ThrowsAsync<CampaignStoreConflict>(() =>
                        journal.AppendAsync(lease, theater.Commands[index], fabricated, now));
                    Assert.Equal("event-decision-conflict", rejected.Code);
                    Assert.Equal(durableState.Head, await journal.ReadHeadAsync(Ids.Campaign));
                }
                var append = await journal.AppendAsync(lease, theater.Commands[index], theater.Events[index], now);
                Assert.False(append.Duplicate);
                durableState = CampaignKernel.Apply(durableState, append.CampaignEvent);
                if (index == 9)
                {
                    snapshot = await journal.SaveSnapshotAsync(lease, durableState, now);
                    Assert.False(snapshot.Duplicate);
                    var duplicateSnapshot = await journal.SaveSnapshotAsync(lease, durableState, now);
                    Assert.True(duplicateSnapshot.Duplicate);

                    var checkpointBytes = Encoding.UTF8.GetBytes("portable-checkpoint-at-commit-10");
                    checkpoint = await journal.PutBlobAsync(lease, CampaignBlobPurpose.TacticalCheckpoint,
                        "assignment-7/checkpoint-10", durableState.Head, checkpointBytes, now);
                    Assert.False(checkpoint.Duplicate);
                    Assert.Equal(checkpointBytes, await journal.ReadBlobAsync(checkpoint.Sha256));
                    var duplicateCheckpoint = await journal.PutBlobAsync(lease, CampaignBlobPurpose.TacticalCheckpoint,
                        "assignment-7/checkpoint-10", durableState.Head, checkpointBytes, now);
                    Assert.True(duplicateCheckpoint.Duplicate);
                    var blobConflict = await Assert.ThrowsAsync<CampaignStoreConflict>(() => journal.PutBlobAsync(
                        lease, CampaignBlobPurpose.TacticalCheckpoint, "assignment-7/checkpoint-10",
                        durableState.Head, Encoding.UTF8.GetBytes("different-checkpoint"), now));
                    Assert.Equal("blob-reference-conflict", blobConflict.Code);
                }
            }

            Assert.NotNull(snapshot);
            Assert.Equal(new CommitPosition(10), snapshot.CommitPosition);
            Assert.Equal(theater.State.Head, await journal.ReadHeadAsync(Ids.Campaign));
            var duplicate = await journal.AppendAsync(lease, theater.Commands[0], theater.Events[0], now);
            Assert.True(duplicate.Duplicate);
            Assert.Equal(theater.Events[0].EventId, duplicate.CampaignEvent.EventId);

            var conflictingCommand = theater.Commands[0] with { RequestedAt = new CampaignTime(1) };
            var idempotencyConflict = await Assert.ThrowsAsync<CampaignStoreConflict>(() =>
                journal.AppendAsync(lease, conflictingCommand, theater.Events[0], now));
            Assert.Equal("idempotency-conflict", idempotencyConflict.Code);

            var replay = CampaignState.Create(theater.State.Metadata);
            var durableEvents = await journal.ReadEventsAsync(Ids.Campaign, CommitPosition.Origin);
            foreach (var campaignEvent in durableEvents)
            {
                replay = CampaignKernel.Apply(replay, campaignEvent);
            }
            Assert.Equal(theater.Hash, CampaignCanonicalSerializer.ComputeHash(replay));
            var recovered = await journal.RecoverAsync(Ids.Campaign);
            Assert.Equal(snapshot.CommitPosition, recovered.SnapshotPosition);
            Assert.Equal(theater.Hash, recovered.CanonicalHash);
            Assert.Equal(CampaignCanonicalSerializer.Serialize(theater.State),
                CampaignCanonicalSerializer.Serialize(recovered.State));
            var integrity = await journal.VerifyIntegrityAsync();
            Assert.Single(integrity.Campaigns);
            Assert.Equal(theater.Events.Count, integrity.EventCount);
            Assert.Equal(1, integrity.SnapshotCount);
            Assert.Equal(1, integrity.ReferencedBlobCount);

            var backup = await journal.CreateBackupAsync(Path.Combine(directory, "backup"));
            Assert.Equal(theater.Hash, Assert.Single(backup.Integrity.Campaigns).CanonicalHash);
            var restore = await SqliteCampaignJournal.RestoreBackupAsync(
                backup.BackupDirectory, Path.Combine(directory, "restored"));
            var restoredJournal = new SqliteCampaignJournal(restore.DatabasePath, restore.BlobDirectory);
            var restored = await restoredJournal.RecoverAsync(Ids.Campaign);
            Assert.Equal(theater.Hash, restored.CanonicalHash);
            Assert.NotNull(checkpoint);
            Assert.Equal(Encoding.UTF8.GetBytes("portable-checkpoint-at-commit-10"),
                await restoredJournal.ReadBlobAsync(checkpoint.Sha256));
            var restoredFenced = await Assert.ThrowsAsync<CampaignStoreConflict>(() =>
                restoredJournal.AppendAsync(lease, theater.Commands[0], theater.Events[0], now));
            Assert.Equal("writer-fenced", restoredFenced.Code);
            var restoredLease = await restoredJournal.AcquireWriterAsync(
                Ids.Campaign, coordinatorB, now, TimeSpan.FromMinutes(2));
            var restoredDuplicate = await restoredJournal.AppendAsync(
                restoredLease, theater.Commands[0], theater.Events[0], now);
            Assert.True(restoredDuplicate.Duplicate);
            await restoredJournal.ReleaseWriterAsync(restoredLease);

            var replacement = await journal.AcquireWriterAsync(Ids.Campaign, coordinatorA, now, TimeSpan.FromMinutes(2));
            Assert.True(replacement.WriterEpoch.Value > lease.WriterEpoch.Value);
            var fenced = await Assert.ThrowsAsync<CampaignStoreConflict>(() =>
                journal.AppendAsync(lease, theater.Commands[0], theater.Events[0], now));
            Assert.Equal("writer-fenced", fenced.Code);

            await journal.ReleaseWriterAsync(replacement);
            var successor = await journal.AcquireWriterAsync(Ids.Campaign, coordinatorB, now, TimeSpan.FromMinutes(2));
            Assert.True(successor.WriterEpoch.Value > replacement.WriterEpoch.Value);
            await journal.ReleaseWriterAsync(successor);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ProvePersistenceBoundariesAsync(TheaterResult theater)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"crimean-tom-faults-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var injector = new ControlledCampaignPersistenceFaultInjector();
            var databasePath = Path.Combine(directory, "campaign.db");
            var journal = new SqliteCampaignJournal(databasePath, faultInjector: injector);
            await journal.InitializeAsync();
            await journal.CreateCampaignAsync(theater.State.Metadata);
            var now = DateTimeOffset.Parse("2026-08-20T13:00:00Z");
            var coordinator = new CoordinatorId(Guid.Parse("c0000000-0000-0000-0000-000000000001"));
            var lease = await journal.AcquireWriterAsync(Ids.Campaign, coordinator, now, TimeSpan.FromMinutes(2));

            foreach (var boundary in new[]
            {
                CampaignPersistenceBoundary.AppendAfterValidation,
                CampaignPersistenceBoundary.AppendAfterCommandInsert,
                CampaignPersistenceBoundary.AppendAfterEventInsert,
                CampaignPersistenceBoundary.AppendAfterRevisionUpdate,
                CampaignPersistenceBoundary.AppendAfterHeadUpdate,
            })
            {
                injector.Arm(boundary);
                var fault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                    journal.AppendAsync(lease, theater.Commands[0], theater.Events[0], now));
                Assert.Equal(boundary, fault.Boundary);
                Assert.Equal(CommitPosition.Origin, await journal.ReadHeadAsync(Ids.Campaign));
            }

            injector.Arm(CampaignPersistenceBoundary.AppendAfterCommitBeforeAcknowledgement);
            var appendAcknowledgementFault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                journal.AppendAsync(lease, theater.Commands[0], theater.Events[0], now));
            Assert.Equal(CampaignPersistenceBoundary.AppendAfterCommitBeforeAcknowledgement,
                appendAcknowledgementFault.Boundary);
            Assert.Equal(new CommitPosition(1), await journal.ReadHeadAsync(Ids.Campaign));
            Assert.True((await journal.AppendAsync(lease, theater.Commands[0], theater.Events[0], now)).Duplicate);

            var state = CampaignKernel.Apply(CampaignState.Create(theater.State.Metadata), theater.Events[0]);
            foreach (var boundary in new[]
            {
                CampaignPersistenceBoundary.SnapshotAfterValidation,
                CampaignPersistenceBoundary.SnapshotAfterInsert,
            })
            {
                injector.Arm(boundary);
                var fault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                    journal.SaveSnapshotAsync(lease, state, now));
                Assert.Equal(boundary, fault.Boundary);
                Assert.Equal(0, (await journal.VerifyIntegrityAsync()).SnapshotCount);
            }

            injector.Arm(CampaignPersistenceBoundary.SnapshotAfterCommitBeforeAcknowledgement);
            var snapshotAcknowledgementFault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                journal.SaveSnapshotAsync(lease, state, now));
            Assert.Equal(CampaignPersistenceBoundary.SnapshotAfterCommitBeforeAcknowledgement,
                snapshotAcknowledgementFault.Boundary);
            Assert.True((await journal.SaveSnapshotAsync(lease, state, now)).Duplicate);

            var blobBytes = Encoding.UTF8.GetBytes("fault-boundary-checkpoint");
            foreach (var boundary in new[]
            {
                CampaignPersistenceBoundary.BlobAfterFileWrite,
                CampaignPersistenceBoundary.BlobAfterReferenceInsert,
            })
            {
                injector.Arm(boundary);
                var fault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() => journal.PutBlobAsync(
                    lease, CampaignBlobPurpose.TacticalCheckpoint, "fault-checkpoint", state.Head, blobBytes, now));
                Assert.Equal(boundary, fault.Boundary);
                Assert.Equal(0, (await journal.VerifyIntegrityAsync()).ReferencedBlobCount);
            }

            injector.Arm(CampaignPersistenceBoundary.BlobAfterCommitBeforeAcknowledgement);
            var blobAcknowledgementFault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                journal.PutBlobAsync(lease, CampaignBlobPurpose.TacticalCheckpoint,
                    "fault-checkpoint", state.Head, blobBytes, now));
            Assert.Equal(CampaignPersistenceBoundary.BlobAfterCommitBeforeAcknowledgement,
                blobAcknowledgementFault.Boundary);
            Assert.True((await journal.PutBlobAsync(lease, CampaignBlobPurpose.TacticalCheckpoint,
                "fault-checkpoint", state.Head, blobBytes, now)).Duplicate);

            var beforePublish = Path.Combine(directory, "backup-before-publication");
            injector.Arm(CampaignPersistenceBoundary.BackupBeforePublication);
            var backupBeforeFault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                journal.CreateBackupAsync(beforePublish));
            Assert.Equal(CampaignPersistenceBoundary.BackupBeforePublication, backupBeforeFault.Boundary);
            Assert.False(Directory.Exists(beforePublish));

            var afterPublish = Path.Combine(directory, "backup-after-publication");
            injector.Arm(CampaignPersistenceBoundary.BackupAfterPublicationBeforeAcknowledgement);
            var backupAfterFault = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                journal.CreateBackupAsync(afterPublish));
            Assert.Equal(CampaignPersistenceBoundary.BackupAfterPublicationBeforeAcknowledgement, backupAfterFault.Boundary);
            Assert.True(Directory.Exists(afterPublish));
            var publishedBackup = new SqliteCampaignJournal(
                Path.Combine(afterPublish, "campaign.db"), Path.Combine(afterPublish, "blobs"));
            Assert.Equal(1, (await publishedBackup.VerifyIntegrityAsync()).EventCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ProveProcessCrashRecoveryAsync(TheaterResult theater)
    {
        var appendBoundaries = new[]
        {
            CampaignPersistenceBoundary.AppendAfterValidation,
            CampaignPersistenceBoundary.AppendAfterCommandInsert,
            CampaignPersistenceBoundary.AppendAfterEventInsert,
            CampaignPersistenceBoundary.AppendAfterRevisionUpdate,
            CampaignPersistenceBoundary.AppendAfterHeadUpdate,
            CampaignPersistenceBoundary.AppendAfterCommitBeforeAcknowledgement,
        };
        foreach (var boundary in appendBoundaries)
        {
            await ProveProcessCrashAsync(theater, "append", boundary);
        }

        var snapshotBoundaries = new[]
        {
            CampaignPersistenceBoundary.SnapshotAfterValidation,
            CampaignPersistenceBoundary.SnapshotAfterInsert,
            CampaignPersistenceBoundary.SnapshotAfterCommitBeforeAcknowledgement,
        };
        foreach (var boundary in snapshotBoundaries)
        {
            await ProveProcessCrashAsync(theater, "snapshot", boundary);
        }

        var blobBoundaries = new[]
        {
            CampaignPersistenceBoundary.BlobAfterFileWrite,
            CampaignPersistenceBoundary.BlobAfterReferenceInsert,
            CampaignPersistenceBoundary.BlobAfterCommitBeforeAcknowledgement,
        };
        foreach (var boundary in blobBoundaries)
        {
            await ProveProcessCrashAsync(theater, "blob", boundary);
        }

        await ProveProcessCrashAsync(theater, "backup", CampaignPersistenceBoundary.BackupBeforePublication);
        await ProveProcessCrashAsync(
            theater,
            "backup",
            CampaignPersistenceBoundary.BackupAfterPublicationBeforeAcknowledgement);
    }

    private static async Task ProveProcessCrashAsync(
        TheaterResult theater,
        string operation,
        CampaignPersistenceBoundary boundary)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"crimean-tom-process-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "campaign.db");
            var blobDirectory = Path.Combine(directory, "blobs");
            var payloadPath = Path.Combine(directory, "payload.json");
            var backupDirectory = Path.Combine(directory, "backup");
            var journal = new SqliteCampaignJournal(databasePath, blobDirectory);
            await journal.InitializeAsync();
            await journal.CreateCampaignAsync(theater.State.Metadata);
            var setupNow = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
            var crashNow = DateTimeOffset.Parse("2026-08-20T13:00:00Z");
            var recoveryNow = DateTimeOffset.Parse("2026-08-20T14:00:00Z");
            CampaignState state;
            if (operation == "append")
            {
                state = CampaignState.Create(theater.State.Metadata);
                await File.WriteAllBytesAsync(payloadPath, CampaignContractJson.Serialize(theater.Commands[0]));
            }
            else
            {
                var setupLease = await journal.AcquireWriterAsync(
                    Ids.Campaign,
                    new CoordinatorId(Guid.Parse("e0000000-0000-0000-0000-000000000001")),
                    setupNow,
                    TimeSpan.FromMinutes(2));
                await journal.AppendAsync(setupLease, theater.Commands[0], theater.Events[0], setupNow);
                await journal.ReleaseWriterAsync(setupLease);
                state = CampaignKernel.Apply(CampaignState.Create(theater.State.Metadata), theater.Events[0]);
                await File.WriteAllBytesAsync(payloadPath, CampaignCanonicalSerializer.Serialize(state));
            }

            var exitCode = await RunPersistenceProbeAsync(
                operation,
                boundary,
                databasePath,
                blobDirectory,
                payloadPath,
                crashNow,
                backupDirectory);
            Assert.NotEqual(0, exitCode);

            var reopened = new SqliteCampaignJournal(databasePath, blobDirectory);
            await reopened.InitializeAsync();
            var committed = boundary is
                CampaignPersistenceBoundary.AppendAfterCommitBeforeAcknowledgement or
                CampaignPersistenceBoundary.SnapshotAfterCommitBeforeAcknowledgement or
                CampaignPersistenceBoundary.BlobAfterCommitBeforeAcknowledgement or
                CampaignPersistenceBoundary.BackupAfterPublicationBeforeAcknowledgement;

            if (operation == "append")
            {
                Assert.Equal(committed ? new CommitPosition(1) : CommitPosition.Origin,
                    await reopened.ReadHeadAsync(Ids.Campaign));
                var recoveryLease = await reopened.AcquireWriterAsync(
                    Ids.Campaign,
                    new CoordinatorId(Guid.Parse("f0000000-0000-0000-0000-000000000001")),
                    recoveryNow,
                    TimeSpan.FromMinutes(2));
                var retry = await reopened.AppendAsync(
                    recoveryLease,
                    theater.Commands[0],
                    theater.Events[0],
                    recoveryNow);
                Assert.Equal(committed, retry.Duplicate);
                Assert.Equal(new CommitPosition(1), await reopened.ReadHeadAsync(Ids.Campaign));
            }
            else if (operation == "snapshot")
            {
                Assert.Equal(committed ? 1 : 0, (await reopened.VerifyIntegrityAsync()).SnapshotCount);
                var recoveryLease = await reopened.AcquireWriterAsync(
                    Ids.Campaign,
                    new CoordinatorId(Guid.Parse("f0000000-0000-0000-0000-000000000002")),
                    recoveryNow,
                    TimeSpan.FromMinutes(2));
                var retry = await reopened.SaveSnapshotAsync(recoveryLease, state, recoveryNow);
                Assert.Equal(committed, retry.Duplicate);
            }
            else if (operation == "blob")
            {
                Assert.Equal(committed ? 1 : 0, (await reopened.VerifyIntegrityAsync()).ReferencedBlobCount);
                var recoveryLease = await reopened.AcquireWriterAsync(
                    Ids.Campaign,
                    new CoordinatorId(Guid.Parse("f0000000-0000-0000-0000-000000000003")),
                    recoveryNow,
                    TimeSpan.FromMinutes(2));
                var retry = await reopened.PutBlobAsync(
                    recoveryLease,
                    CampaignBlobPurpose.TacticalCheckpoint,
                    "process-crash-checkpoint",
                    state.Head,
                    "process-crash-checkpoint-payload"u8.ToArray(),
                    recoveryNow);
                Assert.Equal(committed, retry.Duplicate);
            }
            else
            {
                Assert.Equal(committed, Directory.Exists(backupDirectory));
                if (committed)
                {
                    var backup = new SqliteCampaignJournal(
                        Path.Combine(backupDirectory, "campaign.db"),
                        Path.Combine(backupDirectory, "blobs"));
                    Assert.Equal(1, (await backup.VerifyIntegrityAsync()).EventCount);
                }
            }

            Assert.Equal(1, (await reopened.VerifyIntegrityAsync()).EventCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<int> RunPersistenceProbeAsync(
        string operation,
        CampaignPersistenceBoundary boundary,
        string databasePath,
        string blobDirectory,
        string payloadPath,
        DateTimeOffset now,
        string backupDirectory)
    {
        var probePath = Path.Combine(AppContext.BaseDirectory, "CrimeanTom.Campaign.PersistenceProbe.dll");
        Assert.True(File.Exists(probePath), $"Persistence probe was not built at {probePath}.");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(probePath);
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(boundary.ToString());
        start.ArgumentList.Add(databasePath);
        start.ArgumentList.Add(blobDirectory);
        start.ArgumentList.Add(payloadPath);
        start.ArgumentList.Add(now.ToString("O"));
        if (operation == "backup")
        {
            start.ArgumentList.Add(backupDirectory);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start persistence probe.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode;
    }

    private static async Task ProveIndependentWriterFencingAsync(TheaterResult theater)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"crimean-tom-writer-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Process? holder = null;
        try
        {
            var databasePath = Path.Combine(directory, "campaign.db");
            var blobDirectory = Path.Combine(directory, "blobs");
            var readyPath = Path.Combine(directory, "writer.ready");
            var journal = new SqliteCampaignJournal(databasePath, blobDirectory);
            await journal.InitializeAsync();
            await journal.CreateCampaignAsync(theater.State.Metadata);
            var holderNow = DateTimeOffset.Parse("2026-08-20T15:00:00Z");
            var probePath = Path.Combine(AppContext.BaseDirectory, "CrimeanTom.Campaign.PersistenceProbe.dll");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(probePath);
            start.ArgumentList.Add("hold-writer");
            start.ArgumentList.Add(databasePath);
            start.ArgumentList.Add(blobDirectory);
            start.ArgumentList.Add(Ids.Campaign.Value.ToString("D"));
            start.ArgumentList.Add(holderNow.ToString("O"));
            start.ArgumentList.Add(readyPath);
            holder = Process.Start(start) ?? throw new InvalidOperationException("Could not start writer holder.");

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (!File.Exists(readyPath))
                {
                    Assert.False(holder.HasExited, "Independent writer exited before acquiring its lease.");
                    await Task.Delay(25, timeout.Token);
                }
            }

            var holderEpoch = new WriterEpoch(long.Parse(await File.ReadAllTextAsync(readyPath)));
            var holderLease = new CampaignWriterLease(
                Ids.Campaign,
                new CoordinatorId(Guid.Parse("d0000000-0000-0000-0000-000000000001")),
                holderEpoch,
                holderNow.AddMinutes(2));
            var unavailable = await Assert.ThrowsAsync<CampaignStoreConflict>(() =>
                journal.AcquireWriterAsync(
                    Ids.Campaign,
                    new CoordinatorId(Guid.Parse("f0000000-0000-0000-0000-000000000004")),
                    holderNow,
                    TimeSpan.FromMinutes(2)));
            Assert.Equal("writer-lease-unavailable", unavailable.Code);

            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync();
            var successor = await journal.AcquireWriterAsync(
                Ids.Campaign,
                new CoordinatorId(Guid.Parse("f0000000-0000-0000-0000-000000000004")),
                holderNow.AddHours(1),
                TimeSpan.FromMinutes(2));
            Assert.True(successor.WriterEpoch.Value > holderEpoch.Value);
            var fenced = await Assert.ThrowsAsync<CampaignStoreConflict>(() =>
                journal.AppendAsync(holderLease, theater.Commands[0], theater.Events[0], holderNow.AddHours(1)));
            Assert.Equal("writer-fenced", fenced.Code);
            Assert.False((await journal.AppendAsync(
                successor,
                theater.Commands[0],
                theater.Events[0],
                holderNow.AddHours(1))).Duplicate);
            await journal.ReleaseWriterAsync(successor);
        }
        finally
        {
            if (holder is { HasExited: false })
            {
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync();
            }

            holder?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CampaignCommand Command(
        int sequence,
        CampaignSubject subject,
        EntityRevision expectedRevision,
        CampaignCommandData data,
        CampaignTime? effectiveAt = null) =>
        new(
            new CommandId(Guid.Parse($"00000000-0000-0000-0000-{sequence:D12}")),
            new IdempotencyKey($"synthetic-theater-{sequence:D3}"),
            Ids.Campaign,
            Ids.Player,
            subject,
            expectedRevision,
            CampaignTime.Origin,
            effectiveAt ?? CampaignTime.Origin,
            new InformationScope(Ids.BlueCoalition, MayCommand: true, MaySeeClassifiedTruth: true),
            data);

    private static IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources(
        bool reverse,
        params (ResourceKind Kind, long Amount)[] items)
    {
        var result = new Dictionary<ResourceKind, ResourceAmount>();
        foreach (var item in reverse ? items.Reverse() : items)
        {
            result.Add(item.Kind, ResourceAmount.FromMilliUnits(item.Amount));
        }
        return result;
    }

    private sealed record TheaterResult(
        CampaignState State,
        IReadOnlyList<CampaignCommand> Commands,
        IReadOnlyList<CommittedCampaignEvent> Events,
        string Hash);

    private static class Ids
    {
        public static readonly CampaignId Campaign = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        public static readonly CoalitionId BlueCoalition = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
        public static readonly CoalitionId RedCoalition = new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
        public static readonly PlayerId Player = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));
        public static readonly RegionId Odessa = new(Guid.Parse("40000000-0000-0000-0000-000000000001"));
        public static readonly RegionId BlackSea = new(Guid.Parse("40000000-0000-0000-0000-000000000002"));
        public static readonly RegionId Bosporus = new(Guid.Parse("40000000-0000-0000-0000-000000000003"));
        public static readonly BaseId BlueBase = new(Guid.Parse("50000000-0000-0000-0000-000000000001"));
        public static readonly BaseId RedBase = new(Guid.Parse("50000000-0000-0000-0000-000000000002"));
        public static readonly EntityId BlueKnox = new(Guid.Parse("60000000-0000-0000-0000-000000000001"));
        public static readonly EntityId BlueOiler = new(Guid.Parse("60000000-0000-0000-0000-000000000002"));
        public static readonly EntityId RedKrivak = new(Guid.Parse("60000000-0000-0000-0000-000000000003"));
        public static readonly GroupId BlueGroup = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
        public static readonly GroupId RedGroup = new(Guid.Parse("70000000-0000-0000-0000-000000000002"));
        public static readonly OrderId TransitOrder = new(Guid.Parse("80000000-0000-0000-0000-000000000001"));
        public static readonly AssignmentId Assignment = new(Guid.Parse("90000000-0000-0000-0000-000000000001"));
        public const string BlueTruth = "BLUE-TRUE-STATE-SENTINEL";
        public const string RedTruth = "RED-TRUE-STATE-SENTINEL";
    }
}
