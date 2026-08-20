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
        Assert.Single(first.State.Groups);
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

        await ProveDurableJournalAsync(first);
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

            for (var index = 0; index < theater.Commands.Count; index++)
            {
                var append = await journal.AppendAsync(lease, theater.Commands[index], theater.Events[index], now);
                Assert.False(append.Duplicate);
            }

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
        public static readonly OrderId TransitOrder = new(Guid.Parse("80000000-0000-0000-0000-000000000001"));
        public static readonly AssignmentId Assignment = new(Guid.Parse("90000000-0000-0000-0000-000000000001"));
        public const string BlueTruth = "BLUE-TRUE-STATE-SENTINEL";
        public const string RedTruth = "RED-TRUE-STATE-SENTINEL";
    }
}
