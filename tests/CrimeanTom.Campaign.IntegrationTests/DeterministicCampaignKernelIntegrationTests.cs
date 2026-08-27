using System.Diagnostics;
using System.Collections.Immutable;
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
    public void Combined_arms_operational_emulator_resolves_physical_land_installation_and_rotary_targets()
    {
        var surfaceStrike = SimulateOperationalExchange(
            OperationalEntity(Ids.ExchangeActor, Ids.BlueCoalition, SimulationDomain.Maritime,
                "Blue surface strike ship", "synthetic-surface-strike",
                [LandWeapon("surface-land-attack-missile", 200_000)]),
            OperationalEntity(Ids.ExchangeTarget, Ids.RedCoalition, SimulationDomain.Installation,
                "Red hardened installation", "synthetic-installation", []));
        AssertOperationalExpenditure(surfaceStrike, Ids.ExchangeActor, Ids.ExchangeTarget);
        Assert.Equal(0, surfaceStrike.Entities[Ids.ExchangeTarget].Telegraph);
        Assert.Equal(0, surfaceStrike.Entities[Ids.ExchangeTarget].Analogue.FloodMilliUnits);

        var bomberStrike = SimulateOperationalExchange(
            OperationalEntity(Ids.ExchangeActor, Ids.BlueCoalition, SimulationDomain.Air,
                "Blue bomber", "synthetic-bomber", [LandWeapon("air-delivered-bomb", 80_000)]),
            OperationalEntity(Ids.ExchangeTarget, Ids.RedCoalition, SimulationDomain.Ground,
                "Red armored target", "synthetic-tank", []));
        AssertOperationalExpenditure(bomberStrike, Ids.ExchangeActor, Ids.ExchangeTarget);

        var helicopterStrike = SimulateOperationalExchange(
            OperationalEntity(Ids.ExchangeActor, Ids.BlueCoalition, SimulationDomain.RotaryWing,
                "Blue attack helicopter", "synthetic-attack-helicopter",
                [LandWeapon("helicopter-rocket", 20_000)]),
            OperationalEntity(Ids.ExchangeTarget, Ids.RedCoalition, SimulationDomain.Installation,
                "Red radar installation", "synthetic-radar", []),
            patrolActor: true);
        AssertOperationalExpenditure(helicopterStrike, Ids.ExchangeActor, Ids.ExchangeTarget);
        Assert.Equal(900, helicopterStrike.Entities[Ids.ExchangeActor].Analogue.AltitudeOrDepthMeters);
        Assert.True(helicopterStrike.Entities[Ids.ExchangeActor].Resources[ResourceKind.Fuel].MilliUnits < 1_000_000);

        var shoradExchange = SimulateOperationalExchange(
            OperationalEntity(Ids.ExchangeActor, Ids.RedCoalition, SimulationDomain.Ground,
                "Red low-altitude GBAD", "synthetic-shorad",
                [new OperationalWeaponCapability("short-range-sam", ContactClassification.Air,
                    DistanceMeters.FromMeters(12_000))]),
            OperationalEntity(Ids.ExchangeTarget, Ids.BlueCoalition, SimulationDomain.RotaryWing,
                "Blue utility helicopter", "synthetic-utility-helicopter", []));
        AssertOperationalExpenditure(shoradExchange, Ids.ExchangeActor, Ids.ExchangeTarget);

        var ballisticStrike = SimulateOperationalExchange(
            OperationalEntity(Ids.ExchangeActor, Ids.RedCoalition, SimulationDomain.Ground,
                "Red ballistic-missile TEL", "synthetic-ballistic-tel",
                [LandWeapon("ballistic-missile", 650_000)]),
            OperationalEntity(Ids.ExchangeTarget, Ids.BlueCoalition, SimulationDomain.Installation,
                "Blue airbase installation", "synthetic-airbase", []));
        AssertOperationalExpenditure(ballisticStrike, Ids.ExchangeActor, Ids.ExchangeTarget);

        var abstractAgainstPhysical = SimulateOperationalExchange(
            OperationalEntity(Ids.ExchangeActor, Ids.RedCoalition, SimulationDomain.LandAbstract,
                "Red abstract army", "abstract_land_formation",
                [LandWeapon("abstract-corps-fires", 80_000)]),
            OperationalEntity(Ids.ExchangeTarget, Ids.BlueCoalition, SimulationDomain.Ground,
                "Blue physical tank target", "synthetic-tank", []));
        Assert.DoesNotContain(abstractAgainstPhysical.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.WeaponExpenditure
            && item.ActorEntityId == Ids.ExchangeActor
            && item.TargetEntityId == Ids.ExchangeTarget);
    }

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
        ProveSimulationRoleFences(first.State);

        var firstBytes = CampaignCanonicalSerializer.Serialize(first.State);
        var secondBytes = CampaignCanonicalSerializer.Serialize(second.State);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(CampaignCanonicalSerializer.ComputeHash(first.State), CampaignCanonicalSerializer.ComputeHash(second.State));
        Assert.Equal(first.Events.Select(item => item.EventId), second.Events.Select(item => item.EventId));
        var decoded = CampaignCanonicalSerializer.Deserialize(firstBytes);
        Assert.Equal(firstBytes, CampaignCanonicalSerializer.Serialize(decoded));
        Assert.True(first.State.Entities[Ids.RedKrivak].OperationalWeapons.SequenceEqual(
            decoded.Entities[Ids.RedKrivak].OperationalWeapons));

        var canonicalJson = Encoding.UTF8.GetString(firstBytes);
        Assert.Contains(Ids.RedTruth, canonicalJson, StringComparison.Ordinal);
        Assert.Contains(Ids.BlueTruth, canonicalJson, StringComparison.Ordinal);

        var projection = CampaignProjectionBuilder.ForCoalition(first.State, Ids.BlueCoalition);
        Assert.Equal(2, projection.FriendlyEntities.Count);
        Assert.Single(projection.Contacts);
        Assert.StartsWith("track:", projection.Contacts[0].TrackId, StringComparison.Ordinal);
        Assert.Equal(Ids.BlackSea, projection.Contacts[0].EstimatedRegionId);
        var projectedEstimate = first.State.Intelligence[new IntelligenceKey(Ids.BlueCoalition, Ids.RedKrivak)];
        Assert.Equal(projectedEstimate.Channel, projection.Contacts[0].Channel);
        Assert.Equal(projectedEstimate.Quality, projection.Contacts[0].Quality);
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
        ProveOperationalAnalogueFences(first.State);

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
        var simulationData = Assert.IsType<CampaignSimulatedEvent>(singleWorkerEvent.Data);
        var invalidHeadingEvent = singleWorkerEvent with
        {
            Data = simulationData with
            {
                Entities =
                [
                    simulationData.Entities[0] with { HeadingMilliDegrees = 360_000 },
                    .. simulationData.Entities.Skip(1),
                ],
            },
        };
        var invalidHeading = Assert.Throws<CampaignRuleViolation>(() =>
            CampaignKernel.Apply(first.State, invalidHeadingEvent));
        Assert.Equal("invalid-simulated-entity", invalidHeading.Code);
        var simulated = CampaignKernel.Apply(first.State, singleWorkerEvent);
        var parallel = CampaignKernel.Apply(second.State, parallelWorkerEvent);
        Assert.Equal(CampaignCanonicalSerializer.Serialize(simulated), CampaignCanonicalSerializer.Serialize(parallel));
        Assert.Equal(new CampaignTime(30), simulated.Time);
        Assert.Equal(first.State.Entities[Ids.BlueOiler].RegionId, simulated.Entities[Ids.BlueOiler].RegionId);
        Assert.NotEqual(first.State.Entities[Ids.BlueOiler].Coordinate, simulated.Entities[Ids.BlueOiler].Coordinate);
        Assert.Equal(first.State.Entities[Ids.BlueKnox].RegionId, simulated.Entities[Ids.BlueKnox].RegionId);
        Assert.True(simulated.Entities[Ids.BlueKnox].Resources[ResourceKind.Fuel].MilliUnits < 800_000);
        Assert.True(simulated.Entities[Ids.BlueKnox].ReadinessMilliUnits <= 1_000);
        Assert.NotEqual(0, simulated.Entities[Ids.BlueOiler].Heading.Value);
        Assert.True(simulated.Entities[Ids.BlueOiler].Telegraph is 2 or 3 or 4);
        Assert.Equal(Ids.TransitOrder, simulated.Groups[Ids.BlueGroup].ActiveOrderId);
        Assert.Null(simulated.Orders[Ids.TransitOrder].CompletedAt);
        Assert.NotEmpty(simulated.Intelligence);
        Assert.Contains(simulated.Intelligence.Values, item => Enum.IsDefined(item.Channel));
        Assert.Contains(simulated.Intelligence.Values, item => Enum.IsDefined(item.Quality));
        Assert.Equal(RulesOfEngagement.WeaponsTight, simulated.Entities[Ids.RedKrivak].Analogue.Roe);
        Assert.InRange(simulated.Entities[Ids.RedKrivak].Analogue.WeaponReadyMilliUnits, 0, 1_000);
        Assert.True(simulated.Entities[Ids.BlueKnox].Analogue.WeaponIntegrityMilliUnits <= 1_000);
        Assert.True(simulated.Entities[Ids.BlueKnox].Analogue.CrewMilliUnits <= 1_000);
        Assert.True(simulated.Bases[Ids.RedBase].Resources[ResourceKind.Fuel].MilliUnits < 4_000_000);
        Assert.InRange(simulated.Bases[Ids.RedBase].Resources[ResourceKind.SpareParts].MilliUnits, 0, 900_000);
        Assert.Equal(15, simulated.ScheduledActivities.Count);
        Assert.Equal(2, simulated.CoalitionStrategies.Count);
        Assert.Contains(simulated.StrategicObjectives.Values, item =>
            item.CoalitionId == Ids.BlueCoalition && item.Kind == StrategicObjectiveKind.Interdict);
        Assert.Contains(simulated.StrategicObjectives.Values, item =>
            item.CoalitionId == Ids.RedCoalition && item.Kind == StrategicObjectiveKind.Defend);
        Assert.Contains(Ids.BlueGroup, simulated.GroupCommitments.Keys);
        Assert.Contains(Ids.RedGroup, simulated.GroupCommitments.Keys);
        var calibration = CampaignCalibration.Evaluate(
            simulated.RecentOperationalOutcomes,
            CampaignCalibration.HealthyTicksPerWallSecond,
            observedFencedIslandCount: 0,
            demotedFencedIslandCount: 0);
        Assert.True(calibration.InEnvelope);
        Assert.Equal(0, calibration.ObservedRangeViolationCount);
        Assert.True(calibration.MaximumRangeRatioMilliunits <= CampaignCalibration.EnvelopeMaximumRangeRatioMilliunits);
        var boundaryOutcome = new OperationalOutcome(
            "boundary-range-violation",
            simulated.Time,
            OperationalOutcomeKind.WeaponExpenditure,
            Ids.BlackSea,
            Ids.RedKrivak,
            Ids.BlueOiler,
            1_000,
            0,
            "test employment at 1000001 m (catalog max 1000000 m)");
        var boundaryViolation = CampaignCalibration.Evaluate(
            [boundaryOutcome],
            CampaignCalibration.HealthyTicksPerWallSecond
                * CampaignCalibration.MaximumTacticalCompressionTimes + 1,
            observedFencedIslandCount: 1,
            demotedFencedIslandCount: 1);
        Assert.False(boundaryViolation.InEnvelope);
        Assert.Equal(1, boundaryViolation.ObservedRangeViolationCount);
        Assert.Equal(1_001, boundaryViolation.MaximumRangeRatioMilliunits);
        Assert.Contains(boundaryViolation.Disagreements,
            item => item.StartsWith("tactical-compression-exceeds-10x:", StringComparison.Ordinal));
        Assert.Contains("fenced-island-demoted:1", boundaryViolation.Disagreements);

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
        var longHorizon = AdvanceLongHorizon(simulatedTheater);
        Assert.True(
            longHorizon.Hash == "9e992e1bf58001a5b333fdc74db2d7e437d8998d56fbdf86440fc5584aada194",
            $"Long-horizon canonical hash: {longHorizon.Hash}");
        var migrated = ProveFencedShadowMigration(longHorizon);
        Assert.True(
            migrated.Hash == "716bad81c979365f8c83459ac09460e2844123dfbd73accfde934b1139ce25f4",
            $"Migrated canonical hash: {migrated.Hash}");
        await ProveDurableJournalAsync(migrated);
        await ProveAtomicBatchJournalAsync(migrated);
        await ProvePersistenceBoundariesAsync(migrated);
        await ProveProcessCrashRecoveryAsync(migrated);
        await ProveIndependentWriterFencingAsync(migrated);
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
                GeoCoordinate.FromMicrodegrees(44_440_000, 32_470_000),
                Resources(reverseCollectionInputs, (ResourceKind.SpareParts, 900_000), (ResourceKind.Fuel, 4_000_000)))), commands, events);

        state = Commit(state, Command(6, CampaignSubject.Entity(Ids.BlueKnox), EntityRevision.None,
            new RegisterEntityCommand(Ids.BlueKnox, "USS Pharris", "usn_knox", Ids.BlueCoalition, Ids.Odessa,
                GeoCoordinate.FromMicrodegrees(44_100_000, 30_200_000), Ids.BlueBase, EntityLifecycle.Deployed,
                FidelityState.Strategic,
                Resources(reverseCollectionInputs, (ResourceKind.Fuel, 800_000), (ResourceKind.Munitions, 600_000)),
                Ids.BlueTruth)), commands, events);
        state = Commit(state, Command(7, CampaignSubject.Entity(Ids.BlueOiler), EntityRevision.None,
            new RegisterEntityCommand(Ids.BlueOiler, "USNS Neosho", "usn_neosho", Ids.BlueCoalition, Ids.Odessa,
                GeoCoordinate.FromMicrodegrees(44_090_000, 30_500_000), Ids.BlueBase, EntityLifecycle.Deployed,
                FidelityState.Strategic,
                Resources(reverseCollectionInputs, (ResourceKind.Stores, 1_200_000), (ResourceKind.Fuel, 8_000_000)),
                "BLUE-LOGISTICS-EXACT")), commands, events);
        state = Commit(state, Command(8, CampaignSubject.Entity(Ids.RedKrivak), EntityRevision.None,
            new RegisterEntityCommand(Ids.RedKrivak, "Rezkiy", "vmf_krivak", Ids.RedCoalition, Ids.BlackSea,
                GeoCoordinate.FromMicrodegrees(44_440_000, 32_470_000), Ids.RedBase, EntityLifecycle.Deployed,
                FidelityState.Operational,
                Resources(reverseCollectionInputs, (ResourceKind.Munitions, 700_000), (ResourceKind.Fuel, 750_000)),
                Ids.RedTruth,
                OperationalWeapons:
                [
                    new OperationalWeaponCapability(
                        "synthetic-catalog-anti-surface-missile",
                        ContactClassification.Surface,
                        DistanceMeters.FromMeters(300_000),
                        DistanceMeters.FromMeters(2_000),
                        OrdnanceKind.Missile,
                        OrdnanceGuidance.Active,
                        SpeedMetersPerSecond: 300,
                        SeekerRange: DistanceMeters.FromMeters(35_000),
                        RequiresExternalGuidance: false,
                        LaunchReliabilityMilliUnits: 1_000,
                        KillProbabilityMilliUnits: 850,
                        WarheadPower: 90,
                        MechanicsSource: WeaponMechanicsSource.SeaPowerCatalog),
                ])), commands, events);

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

    private static void ProveOperationalAnalogueFences(CampaignState state)
    {
        var oiler = state.Entities[Ids.BlueOiler];
        var red = state.Entities[Ids.RedKrivak];
        var fogBaseline = state with
        {
            Intelligence = state.Intelligence.Clear(),
            Orders = state.Orders.Clear(),
            Groups = state.Groups
                .SetItem(Ids.BlueGroup, state.Groups[Ids.BlueGroup] with { ActiveOrderId = null })
                .SetItem(Ids.RedGroup, state.Groups[Ids.RedGroup] with { ActiveOrderId = null }),
        };
        var hiddenTruthChanged = fogBaseline with
        {
            Entities = fogBaseline.Entities.SetItem(Ids.RedKrivak, red with
            {
                RegionId = Ids.Odessa,
                Coordinate = GeoCoordinate.FromMicrodegrees(-40_000_000, -150_000_000),
                DamageMilliUnits = 399_999,
                ClassifiedTruthTag = "MUTATED-HIDDEN-TRUTH",
            }),
        };
        var fogPlan = SimulateForTest(fogBaseline, 107, new CampaignTime(20));
        var changedTruthPlan = SimulateForTest(hiddenTruthChanged, 108, new CampaignTime(20));
        var blueCommitment = fogPlan.GroupCommitments[Ids.BlueGroup];
        var changedBlueCommitment = changedTruthPlan.GroupCommitments[Ids.BlueGroup];
        Assert.Equal(blueCommitment.ObjectiveId, changedBlueCommitment.ObjectiveId);
        var blueObjective = fogPlan.StrategicObjectives[Assert.IsType<StrategicObjectiveId>(blueCommitment.ObjectiveId)];
        var changedBlueObjective = changedTruthPlan.StrategicObjectives[
            Assert.IsType<StrategicObjectiveId>(changedBlueCommitment.ObjectiveId)];
        Assert.Equal(blueObjective.RegionId, changedBlueObjective.RegionId);
        Assert.Equal(blueObjective.Kind, changedBlueObjective.Kind);

        var airThreat = red with
        {
            SimulationDomain = SimulationDomain.Air,
            RegionId = Ids.Odessa,
            Coordinate = GeoCoordinate.FromMicrodegrees(44_300_000, 31_000_000),
            OperationalWeapons = [],
        };
        var surfaceThreat = red with
        {
            EntityId = Ids.ExchangeTarget,
            Name = "Second known surface threat",
            RegionId = Ids.BlackSea,
            Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 33_000_000),
            OperationalWeapons = [],
        };
        var airKey = new IntelligenceKey(Ids.BlueCoalition, Ids.RedKrivak);
        var surfaceKey = new IntelligenceKey(Ids.BlueCoalition, Ids.ExchangeTarget);
        var capabilityBaseline = fogBaseline with
        {
            Assignments = fogBaseline.Assignments.Clear(),
            StrategicObjectives = fogBaseline.StrategicObjectives.Clear(),
            GroupCommitments = fogBaseline.GroupCommitments.Clear(),
            CoalitionStrategies = fogBaseline.CoalitionStrategies.Clear(),
            Bases = fogBaseline.Bases.SetItem(
                Ids.BlueBase,
                fogBaseline.Bases[Ids.BlueBase] with { RegionId = Ids.Bosporus }),
            Entities = fogBaseline.Entities
                .SetItem(Ids.BlueKnox, fogBaseline.Entities[Ids.BlueKnox] with
                {
                    Fidelity = FidelityState.Operational,
                    OperationalWeapons =
                    [
                        new OperationalWeaponCapability(
                            "surface-response-missile",
                            ContactClassification.Surface,
                            DistanceMeters.FromMeters(300_000)),
                    ],
                })
                .SetItem(Ids.RedKrivak, airThreat)
                .Add(Ids.ExchangeTarget, surfaceThreat),
            Intelligence = ImmutableSortedDictionary<IntelligenceKey, IntelligenceEstimate>.Empty
                .Add(airKey, new IntelligenceEstimate(
                    airKey,
                    Ids.Odessa,
                    airThreat.Coordinate,
                    IntelligenceConfidence.Confirmed,
                    CampaignTime.Origin,
                    0,
                    ContactClassification.Air,
                    new EntityRevision(1)))
                .Add(surfaceKey, new IntelligenceEstimate(
                    surfaceKey,
                    Ids.BlackSea,
                    surfaceThreat.Coordinate,
                    IntelligenceConfidence.Confirmed,
                    CampaignTime.Origin,
                    0,
                    ContactClassification.Surface,
                    new EntityRevision(1))),
        };
        var surfaceResponse = SimulateForTest(capabilityBaseline, 110, new CampaignTime(20));
        var surfaceObjective = surfaceResponse.StrategicObjectives[
            Assert.IsType<StrategicObjectiveId>(
                surfaceResponse.GroupCommitments[Ids.BlueGroup].ObjectiveId)];
        Assert.Equal(StrategicObjectiveKind.Interdict, surfaceObjective.Kind);
        Assert.Equal(Ids.BlackSea, surfaceObjective.RegionId);

        var airCapability = capabilityBaseline with
        {
            Entities = capabilityBaseline.Entities.SetItem(
                Ids.BlueKnox,
                capabilityBaseline.Entities[Ids.BlueKnox] with
                {
                    OperationalWeapons =
                    [
                        new OperationalWeaponCapability(
                            "air-response-missile",
                            ContactClassification.Air,
                            DistanceMeters.FromMeters(300_000)),
                    ],
                }),
        };
        var airResponse = SimulateForTest(airCapability, 111, new CampaignTime(20));
        var airObjective = airResponse.StrategicObjectives[
            Assert.IsType<StrategicObjectiveId>(airResponse.GroupCommitments[Ids.BlueGroup].ObjectiveId)];
        Assert.Equal(StrategicObjectiveKind.Interdict, airObjective.Kind);
        Assert.Equal(Ids.Odessa, airObjective.RegionId);

        var changedCapability = surfaceResponse with
        {
            Entities = surfaceResponse.Entities.SetItem(
                Ids.BlueKnox,
                surfaceResponse.Entities[Ids.BlueKnox] with
                {
                    OperationalWeapons = airCapability.Entities[Ids.BlueKnox].OperationalWeapons,
                }),
        };
        var originalExpiry = surfaceResponse.GroupCommitments[Ids.BlueGroup].ExpiresAt;
        var stickyResponse = SimulateForTest(changedCapability, 112, new CampaignTime(60));
        var stickyCommitment = stickyResponse.GroupCommitments[Ids.BlueGroup];
        var stickyObjective = stickyResponse.StrategicObjectives[
            Assert.IsType<StrategicObjectiveId>(stickyCommitment.ObjectiveId)];
        Assert.Equal(Ids.BlackSea, stickyObjective.RegionId);
        Assert.Equal(originalExpiry, stickyCommitment.ExpiresAt);

        var adaptiveResponse = SimulateForTest(
            stickyResponse with { Intelligence = capabilityBaseline.Intelligence },
            113,
            originalExpiry);
        var adaptiveCommitment = adaptiveResponse.GroupCommitments[Ids.BlueGroup];
        Assert.NotEqual(stickyCommitment.ObjectiveId, adaptiveCommitment.ObjectiveId);
        Assert.True(adaptiveCommitment.ExpiresAt.Quanta > originalExpiry.Quanta);

        var packageAir = OperationalEntity(
            Ids.PackageAir,
            Ids.BlueCoalition,
            SimulationDomain.Air,
            "Blue package fighter",
            "synthetic-package-fighter",
            [new OperationalWeaponCapability(
                "package-air-to-air",
                ContactClassification.Air,
                DistanceMeters.FromMeters(200_000))]) with
        {
            RegionId = Ids.Odessa,
            Coordinate = GeoCoordinate.FromMicrodegrees(44_200_000, 29_000_000),
            HomeBaseId = Ids.BlueBase,
        };
        var packageSubmarine = OperationalEntity(
            Ids.PackageSubmarine,
            Ids.BlueCoalition,
            SimulationDomain.Maritime,
            "Blue package submarine",
            "synthetic-package-ssn",
            [new OperationalWeaponCapability(
                "package-asw-torpedo",
                ContactClassification.Subsurface,
                DistanceMeters.FromMeters(80_000))]) with
        {
            RegionId = Ids.Odessa,
            Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 29_100_000),
            HomeBaseId = Ids.BlueBase,
        };
        var packageSubsurfaceThreat = OperationalEntity(
            Ids.PackageSubsurfaceThreat,
            Ids.RedCoalition,
            SimulationDomain.Maritime,
            "Red known submarine",
            "synthetic-package-red-ssn",
            []) with
        {
            RegionId = Ids.BlackSea,
            Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 32_800_000),
            HomeBaseId = Ids.RedBase,
        };
        var packageBaseline = capabilityBaseline with
        {
            Entities = capabilityBaseline.Entities
                .SetItem(Ids.RedKrivak, airThreat with
                {
                    RegionId = Ids.BlackSea,
                    Coordinate = GeoCoordinate.FromMicrodegrees(44_100_000, 32_900_000),
                })
                .Add(packageAir.EntityId, packageAir)
                .Add(packageSubmarine.EntityId, packageSubmarine)
                .Add(packageSubsurfaceThreat.EntityId, packageSubsurfaceThreat),
            Groups = capabilityBaseline.Groups
                .Add(Ids.PackageAirGroup, new GroupState(
                    Ids.PackageAirGroup,
                    "Blue package fighter group",
                    Ids.BlueCoalition,
                    ImmutableSortedSet.Create(packageAir.EntityId),
                    null,
                    new EntityRevision(1)))
                .Add(Ids.PackageSubmarineGroup, new GroupState(
                    Ids.PackageSubmarineGroup,
                    "Blue package submarine group",
                    Ids.BlueCoalition,
                    ImmutableSortedSet.Create(packageSubmarine.EntityId),
                    null,
                    new EntityRevision(1))),
            Intelligence = capabilityBaseline.Intelligence
                .SetItem(airKey, capabilityBaseline.Intelligence[airKey] with
                {
                    EstimatedRegionId = Ids.BlackSea,
                    EstimatedCoordinate = GeoCoordinate.FromMicrodegrees(44_100_000, 32_900_000),
                })
                .Add(new IntelligenceKey(Ids.BlueCoalition, packageSubsurfaceThreat.EntityId),
                    new IntelligenceEstimate(
                        new IntelligenceKey(Ids.BlueCoalition, packageSubsurfaceThreat.EntityId),
                        Ids.BlackSea,
                        packageSubsurfaceThreat.Coordinate,
                        IntelligenceConfidence.Confirmed,
                        CampaignTime.Origin,
                        0,
                        ContactClassification.Subsurface,
                        new EntityRevision(1))),
        };
        var packagedResponse = SimulateForTest(packageBaseline, 114, new CampaignTime(20));
        var package = Assert.Single(
            packagedResponse.OperationalPackages.Values,
            item => item.CoalitionId == Ids.BlueCoalition && item.RegionId == Ids.BlackSea);
        Assert.Equal(3, package.Members.Count);
        Assert.Equal(package.ExecuteAt.Quanta - 30, package.Members[Ids.BlueGroup].StartsAt.Quanta);
        Assert.Equal(package.ExecuteAt.Quanta - 30, package.Members[Ids.PackageSubmarineGroup].StartsAt.Quanta);
        Assert.Equal(package.ExecuteAt.Quanta - 10, package.Members[Ids.PackageAirGroup].StartsAt.Quanta);
        Assert.Contains(package.Members.Values, item => item.Role == OperationalPackageRole.Strike);
        Assert.Contains(package.Members.Values, item => item.Role == OperationalPackageRole.AirCover);
        Assert.Contains(package.Members.Values, item => item.Role == OperationalPackageRole.AntiSubmarine);
        Assert.Null(packagedResponse.Groups[Ids.PackageAirGroup].ActiveOrderId);
        Assert.Null(packagedResponse.Groups[Ids.PackageSubmarineGroup].ActiveOrderId);
        var assembledResponse = SimulateForTest(
            packagedResponse,
            116,
            package.Members[Ids.PackageSubmarineGroup].StartsAt);
        if (assembledResponse.Groups[Ids.PackageSubmarineGroup].ActiveOrderId is { } assemblyOrderId)
        {
            Assert.Equal(OrderKind.Transit, assembledResponse.Orders[assemblyOrderId].Kind);
        }
        Assert.Null(assembledResponse.Groups[Ids.PackageAirGroup].ActiveOrderId);
        var executingResponse = SimulateForTest(
            assembledResponse,
            117,
            new CampaignTime(package.ExecuteAt.Quanta + 10));
        Assert.Equal(OrderKind.Cap, executingResponse.Orders[
            Assert.IsType<OrderId>(executingResponse.Groups[Ids.PackageAirGroup].ActiveOrderId)].Kind);
        Assert.Equal(OrderKind.Asw, executingResponse.Orders[
            Assert.IsType<OrderId>(executingResponse.Groups[Ids.PackageSubmarineGroup].ActiveOrderId)].Kind);
        var packageRecovery = SimulateForTest(packagedResponse with
        {
            Entities = packagedResponse.Entities.SetItem(
                Ids.PackageAir,
                packagedResponse.Entities[Ids.PackageAir] with { DamageMilliUnits = 500_000 }),
        }, 115, new CampaignTime(30));
        var reducedPackage = Assert.Single(
            packageRecovery.OperationalPackages.Values,
            item => item.CoalitionId == Ids.BlueCoalition && item.RegionId == Ids.BlackSea);
        Assert.Equal(2, reducedPackage.Members.Count);
        Assert.DoesNotContain(Ids.PackageAirGroup, reducedPackage.Members.Keys);
        Assert.Equal(
            CampaignCanonicalSerializer.Serialize(packagedResponse),
            CampaignCanonicalSerializer.Serialize(CampaignCanonicalSerializer.Deserialize(
                CampaignCanonicalSerializer.Serialize(packagedResponse))));

        var fixedDefense = fogBaseline with
        {
            Assignments = fogBaseline.Assignments.Clear(),
            Entities = fogBaseline.Entities
                .SetItem(Ids.BlueKnox, fogBaseline.Entities[Ids.BlueKnox] with
                {
                    Fidelity = FidelityState.Operational,
                    SimulationDomain = SimulationDomain.Installation,
                    RegionId = Ids.Odessa,
                    Coordinate = fogBaseline.Bases[Ids.BlueBase].Coordinate,
                    Telegraph = 0,
                })
                .SetItem(Ids.BlueOiler, fogBaseline.Entities[Ids.BlueOiler] with
                {
                    RegionId = Ids.Odessa,
                    Coordinate = GeoCoordinate.FromMicrodegrees(44_181_000, 28_651_000),
                    Telegraph = 0,
                }),
        };
        var fixedMovement = Assert.Throws<CampaignRuleViolation>(() => CampaignKernel.Commit(
            fixedDefense,
            Command(
                109,
                CampaignSubject.Group(Ids.BlueGroup),
                fixedDefense.Groups[Ids.BlueGroup].Revision,
                new IssueOrderCommand(
                    Ids.TransitOrder,
                    Ids.BlueGroup,
                    OrderKind.Transit,
                    Ids.BlackSea,
                    fixedDefense.Time,
                    new CampaignTime(40)),
                effectiveAt: fixedDefense.Time)));
        Assert.Equal("fixed-installation-group-movement", fixedMovement.Code);
        var fixedDefensePlan = SimulateForTest(fixedDefense, 109, new CampaignTime(20));
        Assert.Null(fixedDefensePlan.Groups[Ids.BlueGroup].ActiveOrderId);
        Assert.Equal(
            fixedDefense.Entities[Ids.BlueKnox].Coordinate,
            fixedDefensePlan.Entities[Ids.BlueKnox].Coordinate);
        Assert.Equal(
            fixedDefense.Entities[Ids.BlueOiler].Coordinate,
            fixedDefensePlan.Entities[Ids.BlueOiler].Coordinate);
        var fixedCommitment = fixedDefensePlan.GroupCommitments[Ids.BlueGroup];
        var fixedObjective = fixedDefensePlan.StrategicObjectives[
            Assert.IsType<StrategicObjectiveId>(fixedCommitment.ObjectiveId)];
        Assert.Equal(StrategicObjectiveKind.Defend, fixedObjective.Kind);
        Assert.Equal(Ids.Odessa, fixedObjective.RegionId);

        var sameRegionWithoutIntel = state with
        {
            Entities = state.Entities
                .SetItem(Ids.BlueOiler, oiler with
                {
                    RegionId = Ids.BlackSea,
                    Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 33_300_000),
                })
                .SetItem(Ids.RedKrivak, red with
                {
                    RegionId = Ids.BlackSea,
                    Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 32_000_000),
                }),
            Groups = state.Groups.Clear(),
            Orders = state.Orders.Clear(),
        };
        var heldFire = SimulateForTest(sameRegionWithoutIntel, 94, new CampaignTime(20));
        Assert.DoesNotContain(heldFire.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.WeaponExpenditure
            && item.ActorEntityId == Ids.RedKrivak);

        var badEstimateKey = new IntelligenceKey(Ids.RedCoalition, Ids.BlueOiler);
        var badEstimate = sameRegionWithoutIntel with
        {
            Intelligence = sameRegionWithoutIntel.Intelligence.Add(
                badEstimateKey,
                new IntelligenceEstimate(
                    badEstimateKey,
                    Ids.Odessa,
                    GeoCoordinate.FromMicrodegrees(-40_000_000, -150_000_000),
                    IntelligenceConfidence.Probable,
                    CampaignTime.Origin,
                    25_000,
                    ContactClassification.Surface,
                    new EntityRevision(1))),
        };
        var heldForEstimate = SimulateForTest(badEstimate, 95, new CampaignTime(20));
        Assert.DoesNotContain(heldForEstimate.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.WeaponExpenditure
            && item.ActorEntityId == Ids.RedKrivak);

        var missingCatalogWeapon = sameRegionWithoutIntel with
        {
            Entities = sameRegionWithoutIntel.Entities.SetItem(Ids.BlueKnox,
                sameRegionWithoutIntel.Entities[Ids.BlueKnox] with
                {
                    RegionId = Ids.BlackSea,
                    Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 32_000_001),
                    OperationalWeapons = [],
                }),
        };
        var missingCatalogHeldFire = SimulateForTest(missingCatalogWeapon, 93, new CampaignTime(20));
        Assert.DoesNotContain(missingCatalogHeldFire.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.WeaponExpenditure
            && item.ActorEntityId == Ids.BlueKnox);

        var abstractLand = sameRegionWithoutIntel with
        {
            Assignments = sameRegionWithoutIntel.Assignments.Clear(),
            Entities = sameRegionWithoutIntel.Entities
                .SetItem(Ids.BlueKnox, sameRegionWithoutIntel.Entities[Ids.BlueKnox] with
                {
                    RegionId = Ids.BlackSea,
                    Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 32_000_001),
                    Fidelity = FidelityState.Operational,
                    SimulationDomain = SimulationDomain.LandAbstract,
                    MaximumFidelity = FidelityState.Operational,
                    OperationalWeapons = [],
                })
                .SetItem(Ids.RedKrivak, sameRegionWithoutIntel.Entities[Ids.RedKrivak] with
                {
                    SimulationDomain = SimulationDomain.LandAbstract,
                    MaximumFidelity = FidelityState.Operational,
                    OperationalWeapons = [],
                }),
        };
        var abstractLandFired = SimulateForTest(abstractLand, 90, new CampaignTime(20));
        Assert.Contains(abstractLandFired.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.WeaponExpenditure
            && item.Detail is not null
            && item.Detail.StartsWith("abstract-corps-fires employment", StringComparison.Ordinal));

        var weaponsHold = abstractLand with
        {
            Entities = abstractLand.Entities.SetItem(Ids.BlueKnox,
                abstractLand.Entities[Ids.BlueKnox] with
                {
                    Analogue = abstractLand.Entities[Ids.BlueKnox].Analogue with
                    {
                        Roe = RulesOfEngagement.WeaponsHold,
                    },
                }),
        };
        var heldByDoctrine = SimulateForTest(weaponsHold, 103, new CampaignTime(20));
        Assert.DoesNotContain(heldByDoctrine.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.WeaponExpenditure
            && item.ActorEntityId == Ids.BlueKnox);

        var splitGroup = state with
        {
            Entities = state.Entities.SetItem(Ids.BlueOiler, oiler with { RegionId = Ids.Bosporus }),
        };
        var splitMoved = SimulateForTest(splitGroup, 91, new CampaignTime(20));
        Assert.Equal(state.Entities[Ids.BlueKnox].RegionId, splitMoved.Entities[Ids.BlueKnox].RegionId);
        Assert.Equal(Ids.Bosporus, splitMoved.Entities[Ids.BlueOiler].RegionId);

        var withinRegion = state with
        {
            Entities = state.Entities.SetItem(Ids.BlueOiler, oiler with
            {
                RegionId = Ids.Odessa,
                Coordinate = GeoCoordinate.FromMicrodegrees(44_000_000, 34_000_000),
            }),
            Orders = state.Orders.SetItem(Ids.TransitOrder, state.Orders[Ids.TransitOrder] with
            {
                DestinationRegionId = Ids.Odessa,
            }),
        };
        var moved = SimulateForTest(withinRegion, 96, new CampaignTime(20));
        Assert.Equal(Ids.TransitOrder, moved.Groups[Ids.BlueGroup].ActiveOrderId);
        Assert.Null(moved.Orders[Ids.TransitOrder].CompletedAt);
        Assert.Equal(7_999_980, moved.Entities[Ids.BlueOiler].Resources[ResourceKind.Fuel].MilliUnits);
        Assert.Equal(oiler.DamageMilliUnits, moved.Entities[Ids.BlueOiler].DamageMilliUnits);
        var coordinateDeltaMicrodegrees =
            Math.Abs(moved.Entities[Ids.BlueOiler].Coordinate.LatitudeMicrodegrees
                - withinRegion.Entities[Ids.BlueOiler].Coordinate.LatitudeMicrodegrees)
            + Math.Abs(moved.Entities[Ids.BlueOiler].Coordinate.LongitudeMicrodegrees
                - withinRegion.Entities[Ids.BlueOiler].Coordinate.LongitudeMicrodegrees);
        Assert.InRange(coordinateDeltaMicrodegrees, 1, 2_000);

        var damaged = state with
        {
            Entities = state.Entities
                .SetItem(Ids.BlueOiler, oiler with
                {
                    RegionId = Ids.Bosporus,
                    DamageMilliUnits = 650_000,
                })
                .SetItem(Ids.BlueKnox, state.Entities[Ids.BlueKnox] with
                {
                    RegionId = Ids.Bosporus,
                }),
            Groups = state.Groups.SetItem(Ids.BlueGroup, state.Groups[Ids.BlueGroup] with
            {
                ActiveOrderId = null,
            }),
            Orders = ImmutableSortedDictionary<OrderId, OrderState>.Empty,
        };
        var withdrawing = SimulateForTest(damaged, 97, new CampaignTime(20));
        Assert.True(withdrawing.GroupCommitments[Ids.BlueGroup].WithdrawLatched);
        var withdrawalOrderId = Assert.IsType<OrderId>(withdrawing.Groups[Ids.BlueGroup].ActiveOrderId);
        Assert.NotEqual(Ids.TransitOrder, withdrawalOrderId);
        Assert.Equal(OrderKind.Withdraw, withdrawing.Orders[withdrawalOrderId].Kind);

        var homeDamaged = state with
        {
            Entities = state.Entities.SetItem(Ids.BlueOiler, oiler with
            {
                RegionId = Ids.Odessa,
                Coordinate = state.Bases[Ids.BlueBase].Coordinate,
                DamageMilliUnits = 650_000,
            }),
            Groups = state.Groups.SetItem(Ids.BlueGroup, state.Groups[Ids.BlueGroup] with
            {
                ActiveOrderId = null,
            }),
            Orders = ImmutableSortedDictionary<OrderId, OrderState>.Empty,
        };
        var holdingAtHome = SimulateForTest(homeDamaged, 100, new CampaignTime(20));
        Assert.Null(holdingAtHome.Groups[Ids.BlueGroup].ActiveOrderId);
        Assert.Contains(holdingAtHome.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.Withdrawal
            && item.Detail is not null
            && item.Detail.Contains("holding at home base", StringComparison.Ordinal));

        var batteredAtHome = homeDamaged with
        {
            Entities = homeDamaged.Entities.SetItem(Ids.BlueOiler,
                homeDamaged.Entities[Ids.BlueOiler] with { DamageMilliUnits = 450_000 }),
        };
        var batteredHeld = SimulateForTest(batteredAtHome, 92, new CampaignTime(20));
        Assert.Null(batteredHeld.Groups[Ids.BlueGroup].ActiveOrderId);
        Assert.Equal(state.Bases[Ids.BlueBase].Coordinate, batteredHeld.Entities[Ids.BlueOiler].Coordinate);

        var strandedBattered = state with
        {
            Entities = state.Entities
                .SetItem(Ids.BlueOiler, oiler with
                {
                    HomeBaseId = null,
                    DamageMilliUnits = 450_000,
                })
                .SetItem(Ids.RedKrivak, state.Entities[Ids.RedKrivak] with { RegionId = Ids.BlackSea }),
            Groups = state.Groups.SetItem(Ids.BlueGroup, state.Groups[Ids.BlueGroup] with
            {
                Members = state.Groups[Ids.BlueGroup].Members.Clear().Add(Ids.BlueOiler),
                ActiveOrderId = null,
            }),
            Orders = state.Orders.Clear(),
        };
        var strandedHeld = SimulateForTest(strandedBattered, 89, new CampaignTime(20));
        Assert.Null(strandedHeld.Groups[Ids.BlueGroup].ActiveOrderId);

        var destroyed = state with
        {
            Entities = state.Entities.SetItem(Ids.BlueOiler, oiler with
            {
                RegionId = Ids.Bosporus,
                Coordinate = GeoCoordinate.FromMicrodegrees(44_440_000, 32_470_001),
                Lifecycle = EntityLifecycle.Destroyed,
            }),
        };
        var destroyedObserved = SimulateForTest(destroyed, 98, new CampaignTime(20));
        Assert.DoesNotContain(destroyedObserved.Intelligence.Keys, item =>
            item.ObserverCoalitionId == Ids.RedCoalition && item.ContactEntityId == Ids.BlueOiler);

        var neutralObserver = oiler with
        {
            EntityId = Ids.NeutralObserver,
            Name = "Distant neutral observer",
            CoalitionId = Ids.NeutralCoalition,
            RegionId = Ids.Odessa,
            Coordinate = GeoCoordinate.FromMicrodegrees(-40_000_000, -150_000_000),
            HomeBaseId = null,
            OperationalWeapons = [],
            ClassifiedTruthTag = "NEUTRAL-TRUE-STATE-SENTINEL",
        };
        var observed = SimulateForTest(state with
        {
            Entities = state.Entities.Add(neutralObserver.EntityId, neutralObserver),
        }, 99, new CampaignTime(30));
        Assert.DoesNotContain(observed.Intelligence.Keys, item =>
            item.ObserverCoalitionId == Ids.NeutralCoalition);

        var polarMovement = state with
        {
            Bases = state.Bases.SetItem(Ids.BlueBase, state.Bases[Ids.BlueBase] with
            {
                Coordinate = GeoCoordinate.FromMicrodegrees(89_900_000, -179_900_000),
            }),
            Entities = state.Entities.SetItem(Ids.BlueOiler, oiler with
            {
                RegionId = Ids.Odessa,
                Coordinate = GeoCoordinate.FromMicrodegrees(89_900_000, 179_900_000),
            }),
            Orders = state.Orders.SetItem(Ids.TransitOrder, state.Orders[Ids.TransitOrder] with
            {
                DestinationRegionId = Ids.Odessa,
            }),
        };
        var polarMoved = SimulateForTest(polarMovement, 101, new CampaignTime(20));
        var polarCoordinate = polarMoved.Entities[Ids.BlueOiler].Coordinate;
        Assert.InRange(polarCoordinate.LatitudeMicrodegrees, -90_000_000, 90_000_000);
        Assert.InRange(polarCoordinate.LongitudeMicrodegrees, -180_000_000, 179_999_999);

        var tacticalRound = new InFlightOrdnance(
            "tactical-fence-round",
            Ids.RedKrivak,
            Ids.BlueKnox,
            Ids.RedCoalition,
            "synthetic-catalog-anti-surface-missile",
            ContactClassification.Surface,
            state.Entities[Ids.RedKrivak].Coordinate,
            0,
            OrdnanceFlightStage.Cruise,
            OrdnanceGuidance.Active,
            50_000,
            100_000,
            0,
            state.Time);
        var tacticallyOwned = state with
        {
            Assignments = state.Assignments.SetItem(Ids.Assignment,
                state.Assignments[Ids.Assignment] with { Lifecycle = AssignmentLifecycle.Active }),
            InFlight = [tacticalRound],
        };
        CampaignStateValidator.Validate(tacticallyOwned);
        var roundTrip = CampaignCanonicalSerializer.Deserialize(
            CampaignCanonicalSerializer.Serialize(tacticallyOwned));
        Assert.Equal(tacticalRound, Assert.Single(roundTrip.InFlight));
        var tacticalDamage = tacticallyOwned.Entities[Ids.BlueKnox].DamageMilliUnits;
        var fencedRound = SimulateForTest(tacticallyOwned, 102, new CampaignTime(20));
        Assert.Empty(fencedRound.InFlight);
        Assert.Equal(tacticalDamage, fencedRound.Entities[Ids.BlueKnox].DamageMilliUnits);
        Assert.Contains(fencedRound.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.SeekerLost
            && item.TargetEntityId == Ids.BlueKnox);

        var spoofedRound = tacticallyOwned with
        {
            InFlight = [tacticalRound with { ShooterCoalitionId = Ids.BlueCoalition }],
        };
        Assert.Throws<InvalidDataException>(() => CampaignStateValidator.Validate(spoofedRound));

        var torpedoTarget = sameRegionWithoutIntel.Entities[Ids.BlueOiler] with
        {
            Coordinate = sameRegionWithoutIntel.Entities[Ids.RedKrivak].Coordinate,
            Analogue = sameRegionWithoutIntel.Entities[Ids.BlueOiler].Analogue with
            {
                CountermeasureMilliUnits = 1_000,
            },
        };
        var torpedoState = sameRegionWithoutIntel with
        {
            Entities = sameRegionWithoutIntel.Entities.SetItem(Ids.BlueOiler, torpedoTarget),
            InFlight =
            [
                new InFlightOrdnance(
                    "countermeasure-round",
                    Ids.RedKrivak,
                    Ids.BlueOiler,
                    Ids.RedCoalition,
                    "synthetic-torpedo",
                    ContactClassification.Surface,
                    torpedoTarget.Coordinate,
                    0,
                    OrdnanceFlightStage.Terminal,
                    OrdnanceGuidance.Active,
                    100,
                    25_000,
                    0,
                    state.Time,
                    OrdnanceKind.Torpedo),
            ],
        };
        var countered = SimulateForTest(torpedoState, 104, new CampaignTime(20));
        Assert.Equal(900, countered.Entities[Ids.BlueOiler].Analogue.CountermeasureMilliUnits);
        Assert.Contains(countered.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.Countermeasure
            && item.ActorEntityId == Ids.BlueOiler);

        var passiveShooter = torpedoState.Entities[Ids.RedKrivak] with
        {
            Analogue = torpedoState.Entities[Ids.RedKrivak].Analogue with { Emcon = EmconState.Passive },
        };
        var guidanceState = torpedoState with
        {
            Entities = torpedoState.Entities.SetItem(Ids.RedKrivak, passiveShooter),
            InFlight =
            [
                torpedoState.InFlight[0] with
                {
                    OrdnanceId = "external-guidance-round",
                    AmmunitionId = "synthetic-command-missile",
                    Kind = OrdnanceKind.Missile,
                    Guidance = OrdnanceGuidance.Command,
                    RemainingMeters = 150_000,
                    SpeedMetersPerTick = 100_000,
                },
            ],
        };
        var guidanceInterrupted = SimulateForTest(guidanceState, 105, new CampaignTime(20));
        Assert.Equal(1, Assert.Single(guidanceInterrupted.InFlight).LostGuidanceTicks);
        var guidanceLost = SimulateForTest(guidanceInterrupted, 106, new CampaignTime(30));
        Assert.Empty(guidanceLost.InFlight);
        Assert.Contains(guidanceLost.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.SeekerLost
            && item.Detail.Contains("External guidance lost", StringComparison.Ordinal));
    }

    private static CampaignState SimulateForTest(CampaignState state, int sequence, CampaignTime target)
    {
        var command = Command(
            sequence,
            CampaignSubject.Campaign(Ids.Campaign),
            state.CampaignRevision,
            new SimulateCampaignCommand(target),
            effectiveAt: target);
        return CampaignKernel.Apply(
            state,
            CampaignKernel.Decide(state, command, state.Head.Next(), workerCount: 4));
    }

    private static CampaignState SimulateOperationalExchange(
        EntityState actor,
        EntityState target,
        bool patrolActor = false)
    {
        var state = CampaignState.Create(new CampaignMetadata(
            Ids.Campaign,
            "Combined-arms operational exchange",
            0x47524F554E44,
            "sha256:combined-arms-catalog-v1",
            CampaignSchema.Version,
            CampaignSchema.AlgorithmVersion)) with
        {
            Regions = new[]
            {
                new RegionState(Ids.ExchangeRegion, "Combined-arms target area",
                    ImmutableSortedSet<RegionId>.Empty, new EntityRevision(1)),
            }.ToImmutableSortedDictionary(item => item.RegionId, item => item),
            Entities = new[] { actor, target }.ToImmutableSortedDictionary(item => item.EntityId, item => item),
        };
        if (patrolActor)
        {
            state = state with
            {
                Groups = new[]
                {
                    new GroupState(Ids.ExchangeGroup, "Rotary-wing patrol", actor.CoalitionId,
                        ImmutableSortedSet.Create(actor.EntityId), Ids.ExchangeOrder, new EntityRevision(1)),
                }.ToImmutableSortedDictionary(item => item.GroupId, item => item),
                Orders = new[]
                {
                    new OrderState(Ids.ExchangeOrder, Ids.ExchangeGroup, OrderKind.Patrol,
                        Ids.ExchangeRegion, CampaignTime.Origin, new CampaignTime(60), null,
                        new EntityRevision(1)),
                }.ToImmutableSortedDictionary(item => item.OrderId, item => item),
            };
        }
        CampaignStateValidator.Validate(state);
        return SimulateForTest(state, 120, new CampaignTime(10));
    }

    private static EntityState OperationalEntity(
        EntityId entityId,
        CoalitionId coalitionId,
        SimulationDomain domain,
        string name,
        string catalogUnitId,
        IReadOnlyList<OperationalWeaponCapability> weapons) =>
        new(
            entityId,
            name,
            catalogUnitId,
            "Default",
            coalitionId,
            Ids.ExchangeRegion,
            entityId == Ids.ExchangeActor
                ? GeoCoordinate.FromMicrodegrees(50_000_000, 10_000_000)
                : GeoCoordinate.FromMicrodegrees(50_020_000, 10_020_000),
            HomeBaseId: null,
            EntityLifecycle.Deployed,
            FidelityState.Operational,
            domain,
            FidelityState.Tactical,
            Resources(reverse: false,
                (ResourceKind.Fuel, 1_000_000),
                (ResourceKind.Stores, 500_000),
                (ResourceKind.Munitions, 500_000)).ToImmutableSortedDictionary(),
            HeadingMilliDegrees.FromMilliDegrees(0),
            Telegraph: 0,
            ReadinessMilliUnits: 1_000,
            DamageMilliUnits: 0,
            $"{name}-truth",
            weapons.ToImmutableArray(),
            new EntityRevision(1),
            AnalogueTacticalCondition.Healthy);

    private static OperationalWeaponCapability LandWeapon(string ammunitionId, long rangeMeters) =>
        new(ammunitionId, ContactClassification.LandFormation, DistanceMeters.FromMeters(rangeMeters));

    private static void AssertOperationalExpenditure(
        CampaignState state,
        EntityId actor,
        EntityId target) =>
        Assert.Contains(state.RecentOperationalOutcomes, item =>
            item.Kind == OperationalOutcomeKind.WeaponExpenditure
            && item.ActorEntityId == actor
            && item.TargetEntityId == target);

    private static TheaterResult AdvanceLongHorizon(TheaterResult theater)
    {
        Assert.Equal(12, CampaignSchema.Version);
        Assert.Equal(22, CampaignSchema.AlgorithmVersion);
        var single = theater.State;
        var parallel = theater.State;
        var commands = theater.Commands.ToList();
        var events = theater.Events.ToList();
        var previousResources = ResourceTotals(single);
        var sequence = 18;
        for (var targetQuanta = 60L; targetQuanta <= 600; targetQuanta += 30)
        {
            var target = new CampaignTime(targetQuanta);
            var command = Command(
                sequence++,
                CampaignSubject.Campaign(Ids.Campaign),
                single.CampaignRevision,
                new SimulateCampaignCommand(target),
                effectiveAt: target);
            var singleEvent = CampaignKernel.Decide(single, command, single.Head.Next(), workerCount: 1);
            var parallelEvent = CampaignKernel.Decide(parallel, command, parallel.Head.Next(), workerCount: 8);
            Assert.Equal(CampaignContractJson.Serialize(singleEvent), CampaignContractJson.Serialize(parallelEvent));
            single = CampaignKernel.Apply(single, singleEvent);
            parallel = CampaignKernel.Apply(parallel, parallelEvent);
            Assert.Equal(CampaignCanonicalSerializer.Serialize(single), CampaignCanonicalSerializer.Serialize(parallel));
            CampaignStateValidator.Validate(single);
            AssertCampaignInvariants(single);
            Assert.True(single.Orders.Count <= single.Groups.Count,
                $"Retained order count {single.Orders.Count} exceeded group count {single.Groups.Count}.");

            var resources = ResourceTotals(single);
            foreach (var kind in Enum.GetValues<ResourceKind>())
            {
                Assert.True(resources[kind] <= previousResources[kind],
                    $"Canonical {kind} increased from {previousResources[kind]} to {resources[kind]}.");
            }
            previousResources = resources;

            var projectionJson = JsonSerializer.Serialize(CampaignProjectionBuilder.ForCoalition(single, Ids.BlueCoalition));
            Assert.DoesNotContain(Ids.RedTruth, projectionJson, StringComparison.Ordinal);
            Assert.DoesNotContain(Ids.BlueTruth, projectionJson, StringComparison.Ordinal);
            commands.Add(command);
            events.Add(singleEvent);
        }

        return new TheaterResult(single, commands, events, CampaignCanonicalSerializer.ComputeHash(single));
    }

    private static TheaterResult ProveFencedShadowMigration(TheaterResult theater)
    {
        var state = theater.State;
        var commands = theater.Commands.ToList();
        var events = theater.Events.ToList();
        var sequence = theater.Commands.Count + 1;
        var source = state.Assignments[Ids.Assignment];
        var entity = state.Entities[Ids.BlueKnox];
        Assert.Equal(AssignmentRole.Primary, source.Role);
        Assert.Equal(AssignmentLifecycle.Preparing, source.Lifecycle);
        Assert.Equal(FidelityState.Tactical, entity.Fidelity);
        Assert.NotEqual(EntityLifecycle.Destroyed, entity.Lifecycle);

        state = Commit(state, Command(sequence++, CampaignSubject.Assignment(Ids.Assignment), source.Revision,
            new ActivateTacticalAssignmentCommand(Ids.Assignment, source.AuthorityEpoch, Ids.BlueKnox, entity.Revision,
                state.Time, "activate-source-for-migration"),
            effectiveAt: state.Time), commands, events);
        source = state.Assignments[Ids.Assignment];
        state = Commit(state, Command(sequence++, CampaignSubject.Assignment(Ids.Assignment), source.Revision,
            new SetAssignmentLifecycleCommand(Ids.Assignment, source.AuthorityEpoch, AssignmentLifecycle.Quiescing, state.Time),
            effectiveAt: state.Time), commands, events);
        source = state.Assignments[Ids.Assignment];
        state = Commit(state, Command(sequence++, CampaignSubject.Assignment(Ids.Assignment), source.Revision,
            new SetAssignmentLifecycleCommand(Ids.Assignment, source.AuthorityEpoch, AssignmentLifecycle.Quiesced, state.Time),
            effectiveAt: state.Time), commands, events);
        var quiescedEntity = state.Entities[Ids.BlueKnox];
        var quiescedProbe = SimulateForTest(state, 102, new CampaignTime(state.Time.Quanta + 10));
        Assert.Equal(quiescedEntity, quiescedProbe.Entities[Ids.BlueKnox]);

        state = Commit(state, Command(sequence++, CampaignSubject.Assignment(Ids.ShadowAssignment), EntityRevision.None,
            new AssignAuthorityCommand(Ids.ShadowAssignment, Ids.BlueKnox, AssignmentRole.Shadow, new AuthorityEpoch(8),
                state.Time, WorkerClientInstanceId: "destination-worker"),
            effectiveAt: state.Time), commands, events);
        var destination = state.Assignments[Ids.ShadowAssignment];
        state = Commit(state, Command(sequence++, CampaignSubject.Assignment(Ids.ShadowAssignment), destination.Revision,
            new SetAssignmentLifecycleCommand(Ids.ShadowAssignment, destination.AuthorityEpoch, AssignmentLifecycle.Prepared,
                state.Time),
            effectiveAt: state.Time), commands, events);

        source = state.Assignments[Ids.Assignment];
        destination = state.Assignments[Ids.ShadowAssignment];
        entity = state.Entities[Ids.BlueKnox];
        Assert.Equal(AssignmentLifecycle.Quiesced, source.Lifecycle);
        Assert.Equal(AssignmentRole.Shadow, destination.Role);
        Assert.Equal(AssignmentLifecycle.Prepared, destination.Lifecycle);
        Assert.Equal(FidelityState.Tactical, entity.Fidelity);
        CampaignStateValidator.Validate(state);

        var member = new TacticalIslandMigrationMember(
            Ids.Assignment, source.AuthorityEpoch, source.Revision,
            Ids.ShadowAssignment, destination.AuthorityEpoch, destination.Revision,
            Ids.BlueKnox, entity.Revision);
        var stale = Command(sequence, CampaignSubject.Campaign(Ids.Campaign), state.CampaignRevision,
            new MigrateTacticalIslandCommand("migration-batch-pharris",
                [member with { ExpectedEntityRevision = entity.Revision.Next() }],
                state.Time, "stale-entity-revision"),
            effectiveAt: state.Time);
        var fenced = Assert.Throws<CampaignRuleViolation>(() => CampaignKernel.Commit(state, stale));
        Assert.Equal("island-migration-fenced", fenced.Code);

        state = Commit(state, Command(sequence++, CampaignSubject.Campaign(Ids.Campaign), state.CampaignRevision,
            new MigrateTacticalIslandCommand("migration-batch-pharris", [member], state.Time, "promote-prepared-shadow"),
            effectiveAt: state.Time), commands, events);

        source = state.Assignments[Ids.Assignment];
        destination = state.Assignments[Ids.ShadowAssignment];
        entity = state.Entities[Ids.BlueKnox];
        Assert.Equal(AssignmentRole.Primary, source.Role);
        Assert.Equal(AssignmentLifecycle.Released, source.Lifecycle);
        Assert.Equal(member.ExpectedSourceAssignmentRevision.Next(), source.Revision);
        Assert.Equal(AssignmentRole.Primary, destination.Role);
        Assert.Equal(AssignmentLifecycle.Active, destination.Lifecycle);
        Assert.Equal(member.ExpectedDestinationAssignmentRevision.Next(), destination.Revision);
        Assert.Equal(new AuthorityEpoch(8), destination.AuthorityEpoch);
        Assert.Equal(FidelityState.Tactical, entity.Fidelity);
        Assert.Equal(member.ExpectedEntityRevision.Next(), entity.Revision);
        Assert.Equal("destination-worker", destination.WorkerClientInstanceId);
        Assert.Equal(1, state.Assignments.Values.Count(item =>
            item.EntityId == Ids.BlueKnox
            && item.Role == AssignmentRole.Primary
            && item.Lifecycle != AssignmentLifecycle.Released));
        CampaignStateValidator.Validate(state);
        AssertCampaignInvariants(state);

        var replay = CampaignState.Create(theater.State.Metadata);
        foreach (var campaignEvent in events)
        {
            replay = CampaignKernel.Apply(replay, campaignEvent);
        }
        Assert.Equal(CampaignCanonicalSerializer.Serialize(state), CampaignCanonicalSerializer.Serialize(replay));
        return new TheaterResult(state, commands, events, CampaignCanonicalSerializer.ComputeHash(state));
    }

    private static IReadOnlyDictionary<ResourceKind, long> ResourceTotals(CampaignState state) =>
        Enum.GetValues<ResourceKind>().ToDictionary(
            kind => kind,
            kind => state.Entities.Values.Sum(item =>
                    item.Resources.TryGetValue(kind, out var amount) ? amount.MilliUnits : 0)
                + state.Bases.Values.Sum(item =>
                    item.Resources.TryGetValue(kind, out var amount) ? amount.MilliUnits : 0));

    private static void AssertCampaignInvariants(CampaignState state)
    {
        Assert.Equal(state.Head.Value, state.CampaignRevision.Value);
        Assert.All(state.Entities.Values, entity =>
        {
            Assert.InRange(entity.ReadinessMilliUnits, 0, 1_000);
            Assert.InRange(entity.DamageMilliUnits, 0, 1_000_000);
            Assert.All(entity.Resources.Values, amount => Assert.True(amount.MilliUnits >= 0));
        });
        var members = state.Groups.Values.SelectMany(item => item.Members).ToArray();
        Assert.Equal(members.Length, members.Distinct().Count());
        Assert.All(state.ScheduledActivities.Values, activity => Assert.True(activity.DueAt.Quanta > state.Time.Quanta));
        foreach (var group in state.Groups.Values.Where(item => item.ActiveOrderId is not null))
        {
            var order = state.Orders[group.ActiveOrderId!.Value];
            var origin = state.Entities[group.Members.Min].RegionId;
            Assert.True(IsReachable(state, origin, order.DestinationRegionId));
        }
    }

    private static bool IsReachable(CampaignState state, RegionId origin, RegionId destination)
    {
        var visited = new HashSet<RegionId> { origin };
        var queue = new Queue<RegionId>();
        queue.Enqueue(origin);
        while (queue.TryDequeue(out var current))
        {
            if (current == destination)
            {
                return true;
            }
            var neighbors = state.Regions.Values
                .Where(item => item.RegionId == current || item.Neighbors.Contains(current))
                .SelectMany(item => item.RegionId == current ? item.Neighbors : [item.RegionId])
                .Distinct();
            foreach (var neighbor in neighbors.Where(visited.Add))
            {
                queue.Enqueue(neighbor);
            }
        }
        return false;
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
                    var fabricated = theater.Events[index] with
                    {
                        Data = simulation with
                        {
                            NewTime = new CampaignTime(checked(simulation.NewTime.Quanta + 1)),
                        },
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

    private static async Task ProveAtomicBatchJournalAsync(TheaterResult theater)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"crimean-tom-campaign-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var injector = new ControlledCampaignPersistenceFaultInjector();
            var journal = new SqliteCampaignJournal(Path.Combine(directory, "campaign.db"), faultInjector: injector);
            await journal.InitializeAsync();
            await journal.CreateCampaignAsync(theater.State.Metadata);
            var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
            var lease = await journal.AcquireWriterAsync(
                Ids.Campaign,
                new CoordinatorId(Guid.Parse("a0000000-0000-0000-0000-000000000003")),
                now,
                TimeSpan.FromMinutes(10));
            var entries = theater.Commands
                .Zip(theater.Events, (command, campaignEvent) => new CampaignAppendEntry(command, campaignEvent))
                .ToArray();

            injector.Arm(CampaignPersistenceBoundary.AppendAfterEventInsert);
            _ = await Assert.ThrowsAsync<CampaignInjectedPersistenceFault>(() =>
                journal.AppendBatchAsync(lease, entries, now));
            Assert.Equal(CommitPosition.Origin, await journal.ReadHeadAsync(Ids.Campaign));
            Assert.Empty(await journal.ReadEventsAsync(Ids.Campaign, CommitPosition.Origin));

            var append = await journal.AppendBatchAsync(lease, entries, now);
            Assert.False(append.Duplicate);
            Assert.Equal(entries.Length, append.Count);
            Assert.Equal(new CommitPosition(1), append.FirstCommitPosition);
            Assert.Equal(theater.State.Head, append.LastCommitPosition);

            var recovered = await journal.RecoverAsync(Ids.Campaign);
            Assert.Equal(theater.State.Head, recovered.SnapshotPosition);
            Assert.Equal(theater.Hash, recovered.CanonicalHash);
            Assert.Equal(
                CampaignCanonicalSerializer.Serialize(theater.State),
                CampaignCanonicalSerializer.Serialize(recovered.State));

            var duplicate = await journal.AppendBatchAsync(lease, entries, now);
            Assert.True(duplicate.Duplicate);
            Assert.Equal(theater.State.Head, await journal.ReadHeadAsync(Ids.Campaign));

            var nextTime = new CampaignTime(theater.State.Time.Quanta + 1);
            var preparedCommand = Command(
                997,
                CampaignSubject.Campaign(Ids.Campaign),
                theater.State.CampaignRevision,
                new AdvanceClockCommand(nextTime),
                nextTime);
            var preparedEvent = CampaignKernel.Decide(
                theater.State,
                preparedCommand,
                theater.State.Head.Next());
            var prepared = await journal.AppendPreparedAsync(
                lease,
                theater.State,
                preparedCommand,
                preparedEvent,
                now);
            Assert.False(prepared.Duplicate);
            var preparedState = CampaignKernel.Apply(theater.State, prepared.CampaignEvent);
            Assert.Equal(preparedState.Head, await journal.ReadHeadAsync(Ids.Campaign));
            Assert.Equal(
                CampaignCanonicalSerializer.ComputeHash(preparedState),
                (await journal.RecoverAsync(Ids.Campaign)).CanonicalHash);
            var preparedSnapshot = await journal.SavePreparedSnapshotAsync(lease, preparedState, now);
            Assert.False(preparedSnapshot.Duplicate);
            var duplicatePreparedSnapshot = await journal.SavePreparedSnapshotAsync(lease, preparedState, now);
            Assert.True(duplicatePreparedSnapshot.Duplicate);
            var preparedRecovery = await journal.RecoverAsync(Ids.Campaign);
            Assert.Equal(preparedState.Head, preparedRecovery.SnapshotPosition);
            Assert.Equal(
                CampaignCanonicalSerializer.ComputeHash(preparedState),
                preparedRecovery.CanonicalHash);

            var staleTime = new CampaignTime(theater.State.Time.Quanta + 2);
            var staleCommand = Command(
                998,
                CampaignSubject.Campaign(Ids.Campaign),
                theater.State.CampaignRevision,
                new AdvanceClockCommand(staleTime),
                staleTime);
            var staleEvent = CampaignKernel.Decide(theater.State, staleCommand, theater.State.Head.Next());
            var stale = await Assert.ThrowsAsync<CampaignStoreConflict>(() => journal.AppendPreparedAsync(
                lease,
                theater.State,
                staleCommand,
                staleEvent,
                now));
            Assert.Equal("head-conflict", stale.Code);

            var integrity = await journal.VerifyIntegrityAsync();
            Assert.Equal(entries.Length + 1, integrity.EventCount);
            Assert.Equal(2, integrity.SnapshotCount);

            var rollingState = preparedState;
            for (var index = 0; index < 8; index++)
            {
                var rollingTime = new CampaignTime(rollingState.Time.Quanta + 1);
                var rollingCommand = Command(
                    1_000 + index,
                    CampaignSubject.Campaign(Ids.Campaign),
                    rollingState.CampaignRevision,
                    new AdvanceClockCommand(rollingTime),
                    rollingTime);
                var rollingEvent = CampaignKernel.Decide(
                    rollingState,
                    rollingCommand,
                    rollingState.Head.Next());
                var rollingAppend = await journal.AppendPreparedAsync(
                    lease,
                    rollingState,
                    rollingCommand,
                    rollingEvent,
                    now);
                rollingState = CampaignKernel.Apply(rollingState, rollingAppend.CampaignEvent);
                _ = await journal.SavePreparedSnapshotAsync(lease, rollingState, now);
            }

            var compactedIntegrity = await journal.VerifyIntegrityAsync();
            Assert.Equal(entries.Length + 9, compactedIntegrity.EventCount);
            Assert.Equal(8, compactedIntegrity.SnapshotCount);
            var compactedRecovery = await journal.RecoverAsync(Ids.Campaign);
            Assert.Equal(rollingState.Head, compactedRecovery.SnapshotPosition);
            Assert.Equal(
                CampaignCanonicalSerializer.ComputeHash(rollingState),
                compactedRecovery.CanonicalHash);
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

            long holderEpochValue = 0;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (true)
                {
                    Assert.False(holder.HasExited, "Independent writer exited before acquiring its lease.");
                    try
                    {
                        if (File.Exists(readyPath)
                            && long.TryParse(
                                (await File.ReadAllTextAsync(readyPath, timeout.Token)).Trim(),
                                out holderEpochValue))
                        {
                            break;
                        }
                    }
                    catch (IOException) when (!timeout.IsCancellationRequested)
                    {
                    }

                    await Task.Delay(25, timeout.Token);
                }
            }

            var holderEpoch = new WriterEpoch(holderEpochValue);
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

    private static void ProveSimulationRoleFences(CampaignState state)
    {
        var proxy = Command(
            91,
            CampaignSubject.Assignment(Ids.ProxyAssignment),
            EntityRevision.None,
            new AssignAuthorityCommand(
                Ids.ProxyAssignment,
                Ids.BlueKnox,
                AssignmentRole.RenderProxy,
                new AuthorityEpoch(91),
                state.Time),
            effectiveAt: state.Time);
        var proxyError = Assert.Throws<CampaignRuleViolation>(() => CampaignKernel.Commit(state, proxy));
        Assert.Equal("proxy-cannot-receive-simulation-authority", proxyError.Code);

        var observer = Command(
            92,
            CampaignSubject.Assignment(Ids.ObserverAssignment),
            EntityRevision.None,
            new AssignAuthorityCommand(
                Ids.ObserverAssignment,
                Ids.BlueKnox,
                AssignmentRole.Observer,
                new AuthorityEpoch(92),
                state.Time,
                WorkerClientInstanceId: "observer-worker"),
            effectiveAt: state.Time);
        var observed = CampaignKernel.Commit(state, observer);
        var observerAssignment = observed.Assignments[Ids.ObserverAssignment];
        Assert.Equal(AssignmentRole.Observer, observerAssignment.Role);
        Assert.Equal(AssignmentLifecycle.Preparing, observerAssignment.Lifecycle);

        var activate = Command(
            93,
            CampaignSubject.Assignment(Ids.ObserverAssignment),
            observerAssignment.Revision,
            new ActivateTacticalAssignmentCommand(
                Ids.ObserverAssignment,
                observerAssignment.AuthorityEpoch,
                Ids.BlueKnox,
                observed.Entities[Ids.BlueKnox].Revision,
                observed.Time,
                "promote-observer-without-complete-state"),
            effectiveAt: observed.Time);
        var activateError = Assert.Throws<CampaignRuleViolation>(() => CampaignKernel.Commit(observed, activate));
        Assert.Equal("assignment-activation-fenced", activateError.Code);

        var lifecycle = Command(
            94,
            CampaignSubject.Assignment(Ids.ObserverAssignment),
            observerAssignment.Revision,
            new SetAssignmentLifecycleCommand(
                Ids.ObserverAssignment,
                observerAssignment.AuthorityEpoch,
                AssignmentLifecycle.Active,
                observed.Time),
            effectiveAt: observed.Time);
        var lifecycleError = Assert.Throws<CampaignRuleViolation>(() => CampaignKernel.Commit(observed, lifecycle));
        Assert.Equal("assignment-transition", lifecycleError.Code);
        Assert.False(state.Assignments.ContainsKey(Ids.ObserverAssignment));
        Assert.False(state.Assignments.ContainsKey(Ids.ProxyAssignment));
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
        public static readonly CoalitionId NeutralCoalition = new(Guid.Parse("20000000-0000-0000-0000-000000000003"));
        public static readonly PlayerId Player = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));
        public static readonly RegionId Odessa = new(Guid.Parse("40000000-0000-0000-0000-000000000001"));
        public static readonly RegionId BlackSea = new(Guid.Parse("40000000-0000-0000-0000-000000000002"));
        public static readonly RegionId Bosporus = new(Guid.Parse("40000000-0000-0000-0000-000000000003"));
        public static readonly BaseId BlueBase = new(Guid.Parse("50000000-0000-0000-0000-000000000001"));
        public static readonly BaseId RedBase = new(Guid.Parse("50000000-0000-0000-0000-000000000002"));
        public static readonly EntityId BlueKnox = new(Guid.Parse("60000000-0000-0000-0000-000000000001"));
        public static readonly EntityId BlueOiler = new(Guid.Parse("60000000-0000-0000-0000-000000000002"));
        public static readonly EntityId RedKrivak = new(Guid.Parse("60000000-0000-0000-0000-000000000003"));
        public static readonly EntityId NeutralObserver = new(Guid.Parse("60000000-0000-0000-0000-000000000004"));
        public static readonly EntityId ExchangeActor = new(Guid.Parse("60000000-0000-0000-0000-000000000011"));
        public static readonly EntityId ExchangeTarget = new(Guid.Parse("60000000-0000-0000-0000-000000000012"));
        public static readonly EntityId PackageAir = new(Guid.Parse("60000000-0000-0000-0000-000000000013"));
        public static readonly EntityId PackageSubmarine = new(Guid.Parse("60000000-0000-0000-0000-000000000014"));
        public static readonly EntityId PackageSubsurfaceThreat = new(Guid.Parse("60000000-0000-0000-0000-000000000015"));
        public static readonly GroupId BlueGroup = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
        public static readonly GroupId RedGroup = new(Guid.Parse("70000000-0000-0000-0000-000000000002"));
        public static readonly GroupId ExchangeGroup = new(Guid.Parse("70000000-0000-0000-0000-000000000011"));
        public static readonly GroupId PackageAirGroup = new(Guid.Parse("70000000-0000-0000-0000-000000000013"));
        public static readonly GroupId PackageSubmarineGroup = new(Guid.Parse("70000000-0000-0000-0000-000000000014"));
        public static readonly OrderId TransitOrder = new(Guid.Parse("80000000-0000-0000-0000-000000000001"));
        public static readonly OrderId ExchangeOrder = new(Guid.Parse("80000000-0000-0000-0000-000000000011"));
        public static readonly RegionId ExchangeRegion = new(Guid.Parse("40000000-0000-0000-0000-000000000011"));
        public static readonly AssignmentId Assignment = new(Guid.Parse("90000000-0000-0000-0000-000000000001"));
        public static readonly AssignmentId ShadowAssignment = new(Guid.Parse("90000000-0000-0000-0000-000000000002"));
        public static readonly AssignmentId ProxyAssignment = new(Guid.Parse("90000000-0000-0000-0000-000000000099"));
        public static readonly AssignmentId ObserverAssignment = new(Guid.Parse("90000000-0000-0000-0000-000000000098"));
        public const string BlueTruth = "BLUE-TRUE-STATE-SENTINEL";
        public const string RedTruth = "RED-TRUE-STATE-SENTINEL";
    }
}
