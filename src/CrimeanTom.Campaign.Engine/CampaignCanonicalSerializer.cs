using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.Engine;

public static class CampaignCanonicalSerializer
{
    public static byte[] Serialize(CampaignState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteState(writer, state);
        }

        return stream.ToArray();
    }

    public static string ComputeHash(CampaignState state) =>
        Convert.ToHexString(SHA256.HashData(Serialize(state))).ToLowerInvariant();

    public static CampaignState Deserialize(ReadOnlySpan<byte> json)
    {
        var document = JsonSerializer.Deserialize<SnapshotDocument>(json, SnapshotOptions)
            ?? throw new JsonException("Campaign snapshot was null.");
        var metadata = new CampaignMetadata(
            ParseId(document.CampaignId, value => new CampaignId(value)),
            document.Name,
            document.Seed,
            document.CatalogFingerprint,
            document.SchemaVersion,
            document.AlgorithmVersion);
        var state = CampaignState.Create(metadata) with
        {
            Time = new CampaignTime(document.TimeQuanta),
            Head = new CommitPosition(document.CommitPosition),
            CampaignRevision = new EntityRevision(document.CampaignRevision),
            Regions = document.Regions.Select(item => new RegionState(
                ParseId(item.Id, value => new RegionId(value)), item.Name,
                item.Neighbors.Select(value => ParseId(value, id => new RegionId(id))).ToImmutableSortedSet(),
                new EntityRevision(item.Revision))).ToImmutableSortedDictionary(item => item.RegionId, item => item),
            Bases = document.Bases.Select(item => new BaseState(
                ParseId(item.Id, value => new BaseId(value)), item.Name,
                ParseId(item.CoalitionId, value => new CoalitionId(value)),
                ParseId(item.RegionId, value => new RegionId(value)),
                new GeoCoordinate(item.LatitudeMicrodegrees, item.LongitudeMicrodegrees),
                ReadResources(item.Resources), new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.BaseId, item => item),
            Entities = document.Entities.Select(item => new EntityState(
                ParseId(item.Id, value => new EntityId(value)), item.Name, item.CatalogUnitId, item.CatalogLoadoutId,
                ParseId(item.CoalitionId, value => new CoalitionId(value)),
                ParseId(item.RegionId, value => new RegionId(value)),
                new GeoCoordinate(item.LatitudeMicrodegrees, item.LongitudeMicrodegrees),
                item.HomeBaseId is null ? null : ParseId(item.HomeBaseId, value => new BaseId(value)),
                ParseEnum<EntityLifecycle>(item.Lifecycle), ParseEnum<FidelityState>(item.Fidelity),
                ParseEnum<SimulationDomain>(item.SimulationDomain), ParseEnum<FidelityState>(item.MaximumFidelity),
                ReadResources(item.Resources), HeadingMilliDegrees.FromMilliDegrees(item.HeadingMilliDegrees), item.Telegraph,
                item.ReadinessMilliUnits, item.DamageMilliUnits,
                item.ClassifiedTruthTag,
                item.OperationalWeapons.Select(weapon => new OperationalWeaponCapability(
                    weapon.AmmunitionId,
                    ParseEnum<ContactClassification>(weapon.TargetClassification),
                    DistanceMeters.FromMeters(weapon.MaximumRangeMeters),
                    DistanceMeters.FromMeters(weapon.MinimumRangeMeters),
                    ParseEnum<OrdnanceKind>(weapon.Kind),
                    ParseEnum<OrdnanceGuidance>(weapon.Guidance),
                    weapon.SpeedMetersPerSecond,
                    DistanceMeters.FromMeters(weapon.SeekerRangeMeters),
                    weapon.RequiresExternalGuidance,
                    weapon.LaunchReliabilityMilliUnits,
                    weapon.KillProbabilityMilliUnits,
                    weapon.WarheadPower,
                    ParseEnum<WeaponMechanicsSource>(weapon.MechanicsSource))).ToImmutableArray(),
                new EntityRevision(item.Revision),
                ReadAnalogue(item),
                item.HostEntityId is null
                    ? null
                    : ParseId(item.HostEntityId, value => new EntityId(value))))
                .ToImmutableSortedDictionary(item => item.EntityId, item => item),
            Groups = document.Groups.Select(item => new GroupState(
                ParseId(item.Id, value => new GroupId(value)), item.Name,
                ParseId(item.CoalitionId, value => new CoalitionId(value)),
                item.Members.Select(value => ParseId(value, id => new EntityId(id))).ToImmutableSortedSet(),
                item.ActiveOrderId is null ? null : ParseId(item.ActiveOrderId, value => new OrderId(value)),
                new EntityRevision(item.Revision))).ToImmutableSortedDictionary(item => item.GroupId, item => item),
            Orders = document.Orders.Select(item => new OrderState(
                ParseId(item.Id, value => new OrderId(value)),
                ParseId(item.GroupId, value => new GroupId(value)), ParseEnum<OrderKind>(item.Kind),
                ParseId(item.DestinationRegionId, value => new RegionId(value)), new CampaignTime(item.StartsAt),
                item.MustCompleteBy is null ? null : new CampaignTime(item.MustCompleteBy.Value),
                item.CompletedAt is null ? null : new CampaignTime(item.CompletedAt.Value),
                new EntityRevision(item.Revision))).ToImmutableSortedDictionary(item => item.OrderId, item => item),
            StrategicObjectives = document.StrategicObjectives.Select(item => new StrategicObjectiveState(
                ParseId(item.Id, value => new StrategicObjectiveId(value)),
                ParseId(item.CoalitionId, value => new CoalitionId(value)),
                ParseId(item.RegionId, value => new RegionId(value)),
                ParseEnum<StrategicObjectiveKind>(item.Kind),
                item.PriorityMilliUnits,
                new CampaignTime(item.CreatedAt),
                item.Epoch,
                new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.ObjectiveId, item => item),
            GroupCommitments = document.GroupCommitments.Select(item => new GroupCommitmentState(
                ParseId(item.GroupId, value => new GroupId(value)),
                item.ObjectiveId is null ? null : ParseId(item.ObjectiveId, value => new StrategicObjectiveId(value)),
                new CampaignTime(item.ExpiresAt),
                item.WithdrawLatched,
                new CampaignTime(item.LatchReleaseAt),
                new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.GroupId, item => item),
            OperationalPackages = document.OperationalPackages.Select(item => new OperationalPackageState(
                ParseId(item.Id, value => new OperationalPackageId(value)),
                ParseId(item.CoalitionId, value => new CoalitionId(value)),
                ParseId(item.ObjectiveId, value => new StrategicObjectiveId(value)),
                ParseId(item.RegionId, value => new RegionId(value)),
                new CampaignTime(item.AssemblyAt),
                new CampaignTime(item.ExecuteAt),
                new CampaignTime(item.ExpiresAt),
                item.Members.Select(member => new OperationalPackageMemberState(
                        ParseId(member.GroupId, value => new GroupId(value)),
                        ParseEnum<OperationalPackageRole>(member.Role),
                        new CampaignTime(member.StartsAt)))
                    .ToImmutableSortedDictionary(member => member.GroupId, member => member),
                new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.PackageId, item => item),
            CoalitionStrategies = document.CoalitionStrategies.Select(item => new CoalitionStrategyState(
                ParseId(item.CoalitionId, value => new CoalitionId(value)),
                new CampaignTime(item.NextDirectorAt),
                item.Epoch,
                new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.CoalitionId, item => item),
            Intelligence = document.Intelligence.Select(item =>
            {
                var key = new IntelligenceKey(
                    ParseId(item.ObserverCoalitionId, value => new CoalitionId(value)),
                    ParseId(item.ContactEntityId, value => new EntityId(value)));
                return new IntelligenceEstimate(key,
                    ParseId(item.EstimatedRegionId, value => new RegionId(value)),
                    new GeoCoordinate(item.LatitudeMicrodegrees, item.LongitudeMicrodegrees),
                    ParseEnum<IntelligenceConfidence>(item.Confidence), new CampaignTime(item.ObservedAt),
                    item.UncertaintyRadiusMeters, ParseEnum<ContactClassification>(item.Classification),
                    new EntityRevision(item.Revision),
                    ParseEnum<SensorChannel>(item.Channel),
                    ParseEnum<TrackQuality>(item.Quality),
                    item.ObservedHeadingMilliDegrees,
                    item.ObservedSpeedMetersPerTick);
            }).ToImmutableSortedDictionary(item => item.Key, item => item),
            Weather = document.Weather.Select(item => new WeatherState(
                ParseId(item.RegionId, value => new RegionId(value)), new HeadingMilliDegrees(item.WindDirectionMilliDegrees),
                new SpeedMillimetersPerSecond(item.WindMillimetersPerSecond), new DistanceMeters(item.VisibilityMeters),
                item.SeaStateMilliUnits, new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.RegionId, item => item),
            Assignments = document.Assignments.Select(item => new AssignmentState(
                ParseId(item.Id, value => new AssignmentId(value)), ParseId(item.EntityId, value => new EntityId(value)),
                ParseEnum<AssignmentRole>(item.Role), new AuthorityEpoch(item.AuthorityEpoch),
                new CampaignTime(item.EffectiveAt), ParseEnum<AssignmentLifecycle>(item.Lifecycle),
                new EntityRevision(item.Revision), item.BatchId, item.BatchSize, item.WorkerClientInstanceId))
                .ToImmutableSortedDictionary(item => item.AssignmentId, item => item),
            SchedulerPolicy = document.SchedulerPolicy is null ? null : new SchedulerPolicyState(
                document.SchedulerPolicy.ObservedEntityIds
                    .Select(value => ParseId(value, id => new EntityId(id))).ToImmutableSortedSet(),
                document.SchedulerPolicy.ControlledEntityIds
                    .Select(value => ParseId(value, id => new EntityId(id))).ToImmutableSortedSet(),
                document.SchedulerPolicy.Health,
                document.SchedulerPolicy.Eligibility,
                document.SchedulerPolicy.TacticalEntityCapacity,
                document.SchedulerPolicy.BacklogDepth,
                document.SchedulerPolicy.RequestHash,
                document.SchedulerPolicy.StableSampleCount,
                new CampaignTime(document.SchedulerPolicy.EffectiveAt),
                new EntityRevision(document.SchedulerPolicy.Revision),
                document.SchedulerPolicy.TacticalReserveCapacity),
            ScheduledActivities = document.ScheduledActivities.Select(item => new ScheduledActivityState(
                ParseId(item.Id, value => new ScheduledActivityId(value)),
                ParseEnum<CampaignActivityKind>(item.Kind),
                new CampaignSubject(ParseEnum<CampaignSubjectKind>(item.SubjectKind), ParseGuid(item.SubjectId)),
                new CampaignTime(item.DueAt),
                new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.ActivityId, item => item),
            RecentOperationalOutcomes = document.RecentOperationalOutcomes.Select(item => new OperationalOutcome(
                item.OutcomeId,
                new CampaignTime(item.OccurredAt),
                ParseEnum<OperationalOutcomeKind>(item.Kind),
                ParseId(item.RegionId, value => new RegionId(value)),
                ParseId(item.ActorEntityId, value => new EntityId(value)),
                item.TargetEntityId is null ? null : ParseId(item.TargetEntityId, value => new EntityId(value)),
                item.MunitionsExpendedMilliUnits,
                item.DamageInflictedMilliUnits,
                item.Detail)).ToImmutableArray(),
            InFlight = (document.InFlight ?? []).Select(item => new InFlightOrdnance(
                item.OrdnanceId,
                ParseId(item.ShooterEntityId, value => new EntityId(value)),
                ParseId(item.TargetEntityId, value => new EntityId(value)),
                ParseId(item.ShooterCoalitionId, value => new CoalitionId(value)),
                item.AmmunitionId,
                ParseEnum<ContactClassification>(item.TargetClassification),
                new GeoCoordinate(item.LatitudeMicrodegrees, item.LongitudeMicrodegrees),
                item.HeadingMilliDegrees,
                ParseEnum<OrdnanceFlightStage>(item.Stage),
                ParseEnum<OrdnanceGuidance>(item.Guidance),
                item.RemainingMeters,
                item.SpeedMetersPerTick,
                item.InsDriftMilliUnits,
                new CampaignTime(item.LaunchedAt),
                ParseEnum<OrdnanceKind>(item.Kind),
                item.SeekerDelayTicks,
                item.LostGuidanceTicks)).ToImmutableArray(),
        };
        CampaignStateValidator.Validate(state);
        return state;
    }

    private static void WriteState(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", state.Metadata.SchemaVersion);
        writer.WriteNumber("algorithmVersion", state.Metadata.AlgorithmVersion);
        writer.WriteString("campaignId", state.Metadata.CampaignId.ToString());
        writer.WriteString("name", state.Metadata.Name);
        writer.WriteNumber("seed", state.Metadata.Seed);
        writer.WriteString("catalogFingerprint", state.Metadata.CatalogFingerprint);
        writer.WriteNumber("timeQuanta", state.Time.Quanta);
        writer.WriteNumber("commitPosition", state.Head.Value);
        writer.WriteNumber("campaignRevision", state.CampaignRevision.Value);
        WriteRegions(writer, state);
        WriteBases(writer, state);
        WriteEntities(writer, state);
        WriteGroups(writer, state);
        WriteOrders(writer, state);
        WriteStrategicObjectives(writer, state);
        WriteGroupCommitments(writer, state);
        WriteOperationalPackages(writer, state);
        WriteCoalitionStrategies(writer, state);
        WriteIntelligence(writer, state);
        WriteWeather(writer, state);
        WriteAssignments(writer, state);
        WriteSchedulerPolicy(writer, state);
        WriteScheduledActivities(writer, state);
        WriteOperationalOutcomes(writer, state);
        WriteInFlight(writer, state);
        writer.WriteEndObject();
    }

    private static void WriteRegions(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("regions");
        foreach (var region in state.Regions.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", region.RegionId.ToString());
            writer.WriteString("name", region.Name);
            writer.WriteNumber("revision", region.Revision.Value);
            writer.WriteStartArray("neighbors");
            foreach (var neighbor in region.Neighbors)
            {
                writer.WriteStringValue(neighbor.ToString());
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteBases(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("bases");
        foreach (var item in state.Bases.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.BaseId.ToString());
            writer.WriteString("name", item.Name);
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteString("regionId", item.RegionId.ToString());
            WriteCoordinate(writer, item.Coordinate);
            WriteResources(writer, item.Resources);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteEntities(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("entities");
        foreach (var item in state.Entities.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.EntityId.ToString());
            writer.WriteString("name", item.Name);
            writer.WriteString("catalogUnitId", item.CatalogUnitId);
            writer.WriteString("catalogLoadoutId", item.CatalogLoadoutId);
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteString("regionId", item.RegionId.ToString());
            WriteCoordinate(writer, item.Coordinate);
            if (item.HomeBaseId is { } homeBaseId)
            {
                writer.WriteString("homeBaseId", homeBaseId.ToString());
            }
            else
            {
                writer.WriteNull("homeBaseId");
            }
            if (item.HostEntityId is { } hostEntityId)
            {
                writer.WriteString("hostEntityId", hostEntityId.ToString());
            }
            writer.WriteNumber("lifecycle", (int)item.Lifecycle);
            writer.WriteNumber("fidelity", (int)item.Fidelity);
            writer.WriteNumber("simulationDomain", (int)item.SimulationDomain);
            writer.WriteNumber("maximumFidelity", (int)item.MaximumFidelity);
            WriteResources(writer, item.Resources);
            writer.WriteNumber("headingMilliDegrees", item.Heading.Value);
            writer.WriteNumber("telegraph", item.Telegraph);
            writer.WriteNumber("readinessMilliUnits", item.ReadinessMilliUnits);
            writer.WriteNumber("damageMilliUnits", item.DamageMilliUnits);
            writer.WriteString("classifiedTruthTag", item.ClassifiedTruthTag);
            writer.WriteStartArray("operationalWeapons");
            foreach (var weapon in item.OperationalWeapons)
            {
                writer.WriteStartObject();
                writer.WriteString("ammunitionId", weapon.AmmunitionId);
                writer.WriteNumber("targetClassification", (int)weapon.TargetClassification);
                writer.WriteNumber("maximumRangeMeters", weapon.MaximumRange.Value);
                writer.WriteNumber("minimumRangeMeters", weapon.MinimumRange.Value);
                writer.WriteNumber("kind", (int)weapon.Kind);
                writer.WriteNumber("guidance", (int)weapon.Guidance);
                writer.WriteNumber("speedMetersPerSecond", weapon.SpeedMetersPerSecond);
                writer.WriteNumber("seekerRangeMeters", weapon.SeekerRange.Value);
                writer.WriteBoolean("requiresExternalGuidance", weapon.RequiresExternalGuidance);
                writer.WriteNumber("launchReliabilityMilliUnits", weapon.LaunchReliabilityMilliUnits);
                writer.WriteNumber("killProbabilityMilliUnits", weapon.KillProbabilityMilliUnits);
                writer.WriteNumber("warheadPower", weapon.WarheadPower);
                writer.WriteNumber("mechanicsSource", (int)weapon.MechanicsSource);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("altitudeOrDepthMeters", item.Analogue.AltitudeOrDepthMeters);
            writer.WriteNumber("fireMilliUnits", item.Analogue.FireMilliUnits);
            writer.WriteNumber("floodMilliUnits", item.Analogue.FloodMilliUnits);
            writer.WriteNumber("propulsionIntegrityMilliUnits", item.Analogue.PropulsionIntegrityMilliUnits);
            writer.WriteNumber("sensorIntegrityMilliUnits", item.Analogue.SensorIntegrityMilliUnits);
            writer.WriteNumber("weaponIntegrityMilliUnits", item.Analogue.WeaponIntegrityMilliUnits);
            writer.WriteNumber("emcon", (int)item.Analogue.Emcon);
            writer.WriteNumber("roe", (int)item.Analogue.Roe);
            writer.WriteNumber("alert", (int)item.Analogue.Alert);
            writer.WriteNumber("magazineIntegrityMilliUnits", item.Analogue.MagazineIntegrityMilliUnits);
            writer.WriteNumber("flightDeckIntegrityMilliUnits", item.Analogue.FlightDeckIntegrityMilliUnits);
            writer.WriteNumber("crewMilliUnits", item.Analogue.CrewMilliUnits);
            writer.WriteNumber("portIntegrityMilliUnits", item.Analogue.PortIntegrityMilliUnits);
            writer.WriteNumber("starboardIntegrityMilliUnits", item.Analogue.StarboardIntegrityMilliUnits);
            writer.WriteNumber("ecmMilliUnits", item.Analogue.EcmMilliUnits);
            writer.WriteNumber("countermeasureMilliUnits", item.Analogue.CountermeasureMilliUnits);
            writer.WriteNumber("weaponReadyMilliUnits", item.Analogue.WeaponReadyMilliUnits);
            writer.WriteNumber("hostedAircraft", item.Analogue.HostedAircraft);
            writer.WriteNumber("snorkelCycle", item.Analogue.SnorkelCycle);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteGroups(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("groups");
        foreach (var item in state.Groups.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.GroupId.ToString());
            writer.WriteString("name", item.Name);
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteStartArray("members");
            foreach (var member in item.Members)
            {
                writer.WriteStringValue(member.ToString());
            }
            writer.WriteEndArray();
            if (item.ActiveOrderId is { } orderId)
            {
                writer.WriteString("activeOrderId", orderId.ToString());
            }
            else
            {
                writer.WriteNull("activeOrderId");
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteOrders(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("orders");
        foreach (var item in state.Orders.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.OrderId.ToString());
            writer.WriteString("groupId", item.GroupId.ToString());
            writer.WriteNumber("kind", (int)item.Kind);
            writer.WriteString("destinationRegionId", item.DestinationRegionId.ToString());
            writer.WriteNumber("startsAt", item.StartsAt.Quanta);
            if (item.MustCompleteBy is { } deadline)
            {
                writer.WriteNumber("mustCompleteBy", deadline.Quanta);
            }
            else
            {
                writer.WriteNull("mustCompleteBy");
            }
            if (item.CompletedAt is { } completedAt)
            {
                writer.WriteNumber("completedAt", completedAt.Quanta);
            }
            else
            {
                writer.WriteNull("completedAt");
            }
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteIntelligence(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("intelligence");
        foreach (var item in state.Intelligence.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("observerCoalitionId", item.Key.ObserverCoalitionId.ToString());
            writer.WriteString("contactEntityId", item.Key.ContactEntityId.ToString());
            writer.WriteString("estimatedRegionId", item.EstimatedRegionId.ToString());
            WriteCoordinate(writer, item.EstimatedCoordinate);
            writer.WriteNumber("confidence", (int)item.Confidence);
            writer.WriteNumber("observedAt", item.ObservedAt.Quanta);
            writer.WriteNumber("uncertaintyRadiusMeters", item.UncertaintyRadiusMeters);
            writer.WriteNumber("classification", (int)item.Classification);
            writer.WriteNumber("channel", (int)item.Channel);
            writer.WriteNumber("quality", (int)item.Quality);
            writer.WriteNumber("observedHeadingMilliDegrees", item.ObservedHeadingMilliDegrees);
            writer.WriteNumber("observedSpeedMetersPerTick", item.ObservedSpeedMetersPerTick);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteStrategicObjectives(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("strategicObjectives");
        foreach (var item in state.StrategicObjectives.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.ObjectiveId.ToString());
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteString("regionId", item.RegionId.ToString());
            writer.WriteNumber("kind", (int)item.Kind);
            writer.WriteNumber("priorityMilliUnits", item.PriorityMilliUnits);
            writer.WriteNumber("createdAt", item.CreatedAt.Quanta);
            writer.WriteNumber("epoch", item.Epoch);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteGroupCommitments(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("groupCommitments");
        foreach (var item in state.GroupCommitments.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("groupId", item.GroupId.ToString());
            if (item.ObjectiveId is { } objectiveId)
            {
                writer.WriteString("objectiveId", objectiveId.ToString());
            }
            else
            {
                writer.WriteNull("objectiveId");
            }
            writer.WriteNumber("expiresAt", item.ExpiresAt.Quanta);
            writer.WriteBoolean("withdrawLatched", item.WithdrawLatched);
            writer.WriteNumber("latchReleaseAt", item.LatchReleaseAt.Quanta);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteCoalitionStrategies(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("coalitionStrategies");
        foreach (var item in state.CoalitionStrategies.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteNumber("nextDirectorAt", item.NextDirectorAt.Quanta);
            writer.WriteNumber("epoch", item.Epoch);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteOperationalPackages(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("operationalPackages");
        foreach (var item in state.OperationalPackages.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.PackageId.ToString());
            writer.WriteString("coalitionId", item.CoalitionId.ToString());
            writer.WriteString("objectiveId", item.ObjectiveId.ToString());
            writer.WriteString("regionId", item.RegionId.ToString());
            writer.WriteNumber("assemblyAt", item.AssemblyAt.Quanta);
            writer.WriteNumber("executeAt", item.ExecuteAt.Quanta);
            writer.WriteNumber("expiresAt", item.ExpiresAt.Quanta);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteStartArray("members");
            foreach (var member in item.Members.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("groupId", member.GroupId.ToString());
                writer.WriteNumber("role", (int)member.Role);
                writer.WriteNumber("startsAt", member.StartsAt.Quanta);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteOperationalOutcomes(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("recentOperationalOutcomes");
        foreach (var item in state.RecentOperationalOutcomes)
        {
            writer.WriteStartObject();
            writer.WriteString("outcomeId", item.OutcomeId);
            writer.WriteNumber("occurredAt", item.OccurredAt.Quanta);
            writer.WriteNumber("kind", (int)item.Kind);
            writer.WriteString("regionId", item.RegionId.ToString());
            writer.WriteString("actorEntityId", item.ActorEntityId.ToString());
            if (item.TargetEntityId is { } target)
            {
                writer.WriteString("targetEntityId", target.ToString());
            }
            else
            {
                writer.WriteNull("targetEntityId");
            }
            writer.WriteNumber("munitionsExpendedMilliUnits", item.MunitionsExpendedMilliUnits);
            writer.WriteNumber("damageInflictedMilliUnits", item.DamageInflictedMilliUnits);
            writer.WriteString("detail", item.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteInFlight(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("inFlight");
        foreach (var item in state.InFlight.OrderBy(entry => entry.OrdnanceId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("ordnanceId", item.OrdnanceId);
            writer.WriteString("shooterEntityId", item.ShooterEntityId.ToString());
            writer.WriteString("targetEntityId", item.TargetEntityId.ToString());
            writer.WriteString("shooterCoalitionId", item.ShooterCoalitionId.ToString());
            writer.WriteString("ammunitionId", item.AmmunitionId);
            writer.WriteNumber("targetClassification", (int)item.TargetClassification);
            WriteCoordinate(writer, item.Coordinate);
            writer.WriteNumber("headingMilliDegrees", item.HeadingMilliDegrees);
            writer.WriteNumber("stage", (int)item.Stage);
            writer.WriteNumber("guidance", (int)item.Guidance);
            writer.WriteNumber("remainingMeters", item.RemainingMeters);
            writer.WriteNumber("speedMetersPerTick", item.SpeedMetersPerTick);
            writer.WriteNumber("insDriftMilliUnits", item.InsDriftMilliUnits);
            writer.WriteNumber("launchedAt", item.LaunchedAt.Quanta);
            writer.WriteNumber("kind", (int)item.Kind);
            writer.WriteNumber("seekerDelayTicks", item.SeekerDelayTicks);
            writer.WriteNumber("lostGuidanceTicks", item.LostGuidanceTicks);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteWeather(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("weather");
        foreach (var item in state.Weather.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("regionId", item.RegionId.ToString());
            writer.WriteNumber("windDirectionMilliDegrees", item.WindDirection.Value);
            writer.WriteNumber("windMillimetersPerSecond", item.WindSpeed.Value);
            writer.WriteNumber("visibilityMeters", item.Visibility.Value);
            writer.WriteNumber("seaStateMilliUnits", item.SeaStateMilliUnits);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteAssignments(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("assignments");
        foreach (var item in state.Assignments.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.AssignmentId.ToString());
            writer.WriteString("entityId", item.EntityId.ToString());
            writer.WriteNumber("role", (int)item.Role);
            writer.WriteNumber("authorityEpoch", item.AuthorityEpoch.Value);
            writer.WriteNumber("effectiveAt", item.EffectiveAt.Quanta);
            writer.WriteNumber("lifecycle", (int)item.Lifecycle);
            writer.WriteNumber("revision", item.Revision.Value);
            if (!string.IsNullOrEmpty(item.BatchId))
            {
                writer.WriteString("batchId", item.BatchId);
                writer.WriteNumber("batchSize", item.BatchSize);
            }
            if (!string.IsNullOrEmpty(item.WorkerClientInstanceId))
            {
                writer.WriteString("workerClientInstanceId", item.WorkerClientInstanceId);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSchedulerPolicy(Utf8JsonWriter writer, CampaignState state)
    {
        if (state.SchedulerPolicy is not { } policy)
        {
            return;
        }
        writer.WriteStartObject("schedulerPolicy");
        writer.WriteStartArray("observedEntityIds");
        foreach (var entityId in policy.ObservedEntityIds)
        {
            writer.WriteStringValue(entityId.ToString());
        }
        writer.WriteEndArray();
        writer.WriteStartArray("controlledEntityIds");
        foreach (var entityId in policy.ControlledEntityIds)
        {
            writer.WriteStringValue(entityId.ToString());
        }
        writer.WriteEndArray();
        writer.WriteString("health", policy.Health);
        writer.WriteString("eligibility", policy.Eligibility);
        writer.WriteNumber("tacticalEntityCapacity", policy.TacticalEntityCapacity);
        if (policy.TacticalReserveCapacity != 0)
        {
            writer.WriteNumber("tacticalReserveCapacity", policy.TacticalReserveCapacity);
        }
        writer.WriteNumber("backlogDepth", policy.BacklogDepth);
        writer.WriteString("requestHash", policy.RequestHash);
        writer.WriteNumber("stableSampleCount", policy.StableSampleCount);
        writer.WriteNumber("effectiveAt", policy.EffectiveAt.Quanta);
        writer.WriteNumber("revision", policy.Revision.Value);
        writer.WriteEndObject();
    }

    private static void WriteScheduledActivities(Utf8JsonWriter writer, CampaignState state)
    {
        writer.WriteStartArray("scheduledActivities");
        foreach (var item in state.ScheduledActivities.Values)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.ActivityId.ToString());
            writer.WriteNumber("kind", (int)item.Kind);
            writer.WriteNumber("subjectKind", (int)item.Subject.Kind);
            writer.WriteString("subjectId", item.Subject.Value.ToString("N"));
            writer.WriteNumber("dueAt", item.DueAt.Quanta);
            writer.WriteNumber("revision", item.Revision.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteCoordinate(Utf8JsonWriter writer, GeoCoordinate coordinate)
    {
        writer.WriteNumber("latitudeMicrodegrees", coordinate.LatitudeMicrodegrees);
        writer.WriteNumber("longitudeMicrodegrees", coordinate.LongitudeMicrodegrees);
    }

    private static void WriteResources(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<ResourceKind, ResourceAmount>> resources)
    {
        writer.WriteStartArray("resources");
        foreach (var item in resources.OrderBy(item => item.Key))
        {
            writer.WriteStartObject();
            writer.WriteNumber("kind", (int)item.Key);
            writer.WriteNumber("milliUnits", item.Value.MilliUnits);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static T ParseId<T>(string value, Func<Guid, T> factory)
    {
        if (value.Length != 32 || !Guid.TryParseExact(value, "N", out var parsed)
            || parsed == Guid.Empty || parsed.ToString("N") != value)
        {
            throw new JsonException("A canonical identifier is invalid.");
        }
        return factory(parsed);
    }

    private static Guid ParseGuid(string value) => ParseId(value, parsed => parsed);

    private static T ParseEnum<T>(int value) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value)
            ? (T)Enum.ToObject(typeof(T), value)
            : throw new JsonException($"{typeof(T).Name} value {value} is invalid.");

    private static ImmutableSortedDictionary<ResourceKind, ResourceAmount> ReadResources(ResourceDocument[] resources)
    {
        try
        {
            return resources.Select(item => new KeyValuePair<ResourceKind, ResourceAmount>(
                    ParseEnum<ResourceKind>(item.Kind), new ResourceAmount(item.MilliUnits)))
                .ToImmutableSortedDictionary();
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Canonical resources contain duplicate kinds.", exception);
        }
    }

    private sealed class SnapshotDocument
    {
        public required int SchemaVersion { get; init; }
        public required int AlgorithmVersion { get; init; }
        public required string CampaignId { get; init; }
        public required string Name { get; init; }
        public required long Seed { get; init; }
        public required string CatalogFingerprint { get; init; }
        public required long TimeQuanta { get; init; }
        public required long CommitPosition { get; init; }
        public required long CampaignRevision { get; init; }
        public required RegionDocument[] Regions { get; init; }
        public required BaseDocument[] Bases { get; init; }
        public required EntityDocument[] Entities { get; init; }
        public required GroupDocument[] Groups { get; init; }
        public required OrderDocument[] Orders { get; init; }
        public required StrategicObjectiveDocument[] StrategicObjectives { get; init; }
        public required GroupCommitmentDocument[] GroupCommitments { get; init; }
        public required OperationalPackageDocument[] OperationalPackages { get; init; }
        public required CoalitionStrategyDocument[] CoalitionStrategies { get; init; }
        public required IntelligenceDocument[] Intelligence { get; init; }
        public required WeatherDocument[] Weather { get; init; }
        public required AssignmentDocument[] Assignments { get; init; }
        public SchedulerPolicyDocument? SchedulerPolicy { get; init; }
        public required ScheduledActivityDocument[] ScheduledActivities { get; init; }
        public required OperationalOutcomeDocument[] RecentOperationalOutcomes { get; init; }
        public InFlightDocument[] InFlight { get; init; } = [];
    }

    private sealed class RegionDocument
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required long Revision { get; init; }
        public required string[] Neighbors { get; init; }
    }

    private sealed class BaseDocument
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string CoalitionId { get; init; }
        public required string RegionId { get; init; }
        public required int LatitudeMicrodegrees { get; init; }
        public required int LongitudeMicrodegrees { get; init; }
        public required ResourceDocument[] Resources { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class EntityDocument
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string CatalogUnitId { get; init; }
        public required string CatalogLoadoutId { get; init; }
        public required string CoalitionId { get; init; }
        public required string RegionId { get; init; }
        public required int LatitudeMicrodegrees { get; init; }
        public required int LongitudeMicrodegrees { get; init; }
        public string? HomeBaseId { get; init; }
        public string? HostEntityId { get; init; }
        public required int Lifecycle { get; init; }
        public required int Fidelity { get; init; }
        public required int SimulationDomain { get; init; }
        public required int MaximumFidelity { get; init; }
        public required ResourceDocument[] Resources { get; init; }
        public required int HeadingMilliDegrees { get; init; }
        public required int Telegraph { get; init; }
        public required int ReadinessMilliUnits { get; init; }
        public required int DamageMilliUnits { get; init; }
        public required string ClassifiedTruthTag { get; init; }
        public required OperationalWeaponDocument[] OperationalWeapons { get; init; }
        public int AltitudeOrDepthMeters { get; init; }
        public int FireMilliUnits { get; init; }
        public int FloodMilliUnits { get; init; }
        public int PropulsionIntegrityMilliUnits { get; init; } = 1_000;
        public int SensorIntegrityMilliUnits { get; init; } = 1_000;
        public int WeaponIntegrityMilliUnits { get; init; } = 1_000;
        public int Emcon { get; init; }
        public int Roe { get; init; } = (int)RulesOfEngagement.WeaponsTight;
        public int Alert { get; init; }
        public int MagazineIntegrityMilliUnits { get; init; } = 1_000;
        public int FlightDeckIntegrityMilliUnits { get; init; } = 1_000;
        public int CrewMilliUnits { get; init; } = 1_000;
        public int PortIntegrityMilliUnits { get; init; } = 1_000;
        public int StarboardIntegrityMilliUnits { get; init; } = 1_000;
        public int EcmMilliUnits { get; init; }
        public int CountermeasureMilliUnits { get; init; } = 1_000;
        public int WeaponReadyMilliUnits { get; init; } = 1_000;
        public int HostedAircraft { get; init; }
        public int SnorkelCycle { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class OperationalWeaponDocument
    {
        public required string AmmunitionId { get; init; }
        public required int TargetClassification { get; init; }
        public required long MaximumRangeMeters { get; init; }
        public long MinimumRangeMeters { get; init; }
        public int Kind { get; init; }
        public int Guidance { get; init; } = (int)OrdnanceGuidance.Active;
        public long SpeedMetersPerSecond { get; init; }
        public long SeekerRangeMeters { get; init; }
        public bool RequiresExternalGuidance { get; init; }
        public int LaunchReliabilityMilliUnits { get; init; } = 1_000;
        public int KillProbabilityMilliUnits { get; init; } = 1_000;
        public int WarheadPower { get; init; }
        public int MechanicsSource { get; init; }
    }

    private sealed class GroupDocument
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string CoalitionId { get; init; }
        public required long Revision { get; init; }
        public required string[] Members { get; init; }
        public string? ActiveOrderId { get; init; }
    }

    private sealed class OrderDocument
    {
        public required string Id { get; init; }
        public required string GroupId { get; init; }
        public required int Kind { get; init; }
        public required string DestinationRegionId { get; init; }
        public required long StartsAt { get; init; }
        public long? MustCompleteBy { get; init; }
        public long? CompletedAt { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class StrategicObjectiveDocument
    {
        public required string Id { get; init; }
        public required string CoalitionId { get; init; }
        public required string RegionId { get; init; }
        public required int Kind { get; init; }
        public required int PriorityMilliUnits { get; init; }
        public required long CreatedAt { get; init; }
        public required long Epoch { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class GroupCommitmentDocument
    {
        public required string GroupId { get; init; }
        public string? ObjectiveId { get; init; }
        public required long ExpiresAt { get; init; }
        public required bool WithdrawLatched { get; init; }
        public required long LatchReleaseAt { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class OperationalPackageDocument
    {
        public required string Id { get; init; }
        public required string CoalitionId { get; init; }
        public required string ObjectiveId { get; init; }
        public required string RegionId { get; init; }
        public required long AssemblyAt { get; init; }
        public required long ExecuteAt { get; init; }
        public required long ExpiresAt { get; init; }
        public required OperationalPackageMemberDocument[] Members { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class OperationalPackageMemberDocument
    {
        public required string GroupId { get; init; }
        public required int Role { get; init; }
        public required long StartsAt { get; init; }
    }

    private sealed class CoalitionStrategyDocument
    {
        public required string CoalitionId { get; init; }
        public required long NextDirectorAt { get; init; }
        public required long Epoch { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class IntelligenceDocument
    {
        public required string ObserverCoalitionId { get; init; }
        public required string ContactEntityId { get; init; }
        public required string EstimatedRegionId { get; init; }
        public required int LatitudeMicrodegrees { get; init; }
        public required int LongitudeMicrodegrees { get; init; }
        public required int Confidence { get; init; }
        public required long ObservedAt { get; init; }
        public required int UncertaintyRadiusMeters { get; init; }
        public required int Classification { get; init; }
        public int Channel { get; init; }
        public int Quality { get; init; }
        public int ObservedHeadingMilliDegrees { get; init; }
        public int ObservedSpeedMetersPerTick { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class WeatherDocument
    {
        public required string RegionId { get; init; }
        public required int WindDirectionMilliDegrees { get; init; }
        public required long WindMillimetersPerSecond { get; init; }
        public required long VisibilityMeters { get; init; }
        public required int SeaStateMilliUnits { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class AssignmentDocument
    {
        public required string Id { get; init; }
        public required string EntityId { get; init; }
        public required int Role { get; init; }
        public required long AuthorityEpoch { get; init; }
        public required long EffectiveAt { get; init; }
        public required int Lifecycle { get; init; }
        public required long Revision { get; init; }
        public string BatchId { get; init; } = string.Empty;
        public int BatchSize { get; init; } = 1;
        public string WorkerClientInstanceId { get; init; } = string.Empty;
    }

    private sealed class SchedulerPolicyDocument
    {
        public required string[] ObservedEntityIds { get; init; }
        public required string[] ControlledEntityIds { get; init; }
        public required string Health { get; init; }
        public required string Eligibility { get; init; }
        public required int TacticalEntityCapacity { get; init; }
        public int TacticalReserveCapacity { get; init; }
        public required int BacklogDepth { get; init; }
        public required string RequestHash { get; init; }
        public required int StableSampleCount { get; init; }
        public required long EffectiveAt { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class ResourceDocument
    {
        public required int Kind { get; init; }
        public required long MilliUnits { get; init; }
    }

    private sealed class ScheduledActivityDocument
    {
        public required string Id { get; init; }
        public required int Kind { get; init; }
        public required int SubjectKind { get; init; }
        public required string SubjectId { get; init; }
        public required long DueAt { get; init; }
        public required long Revision { get; init; }
    }

    private sealed class OperationalOutcomeDocument
    {
        public required string OutcomeId { get; init; }
        public required long OccurredAt { get; init; }
        public required int Kind { get; init; }
        public required string RegionId { get; init; }
        public required string ActorEntityId { get; init; }
        public string? TargetEntityId { get; init; }
        public required long MunitionsExpendedMilliUnits { get; init; }
        public required int DamageInflictedMilliUnits { get; init; }
        public required string Detail { get; init; }
    }

    private sealed class InFlightDocument
    {
        public required string OrdnanceId { get; init; }
        public required string ShooterEntityId { get; init; }
        public required string TargetEntityId { get; init; }
        public required string ShooterCoalitionId { get; init; }
        public required string AmmunitionId { get; init; }
        public required int TargetClassification { get; init; }
        public required int LatitudeMicrodegrees { get; init; }
        public required int LongitudeMicrodegrees { get; init; }
        public required int HeadingMilliDegrees { get; init; }
        public required int Stage { get; init; }
        public required int Guidance { get; init; }
        public required long RemainingMeters { get; init; }
        public required long SpeedMetersPerTick { get; init; }
        public required int InsDriftMilliUnits { get; init; }
        public required long LaunchedAt { get; init; }
        public int Kind { get; init; }
        public int SeekerDelayTicks { get; init; }
        public int LostGuidanceTicks { get; init; }
    }

    private static AnalogueTacticalCondition ReadAnalogue(EntityDocument item) => new(
        item.AltitudeOrDepthMeters,
        item.FireMilliUnits,
        item.FloodMilliUnits,
        item.PropulsionIntegrityMilliUnits,
        item.SensorIntegrityMilliUnits,
        item.WeaponIntegrityMilliUnits,
        ParseEnum<EmconState>(item.Emcon),
        ParseEnum<RulesOfEngagement>(item.Roe),
        ParseEnum<AlertState>(item.Alert),
        item.MagazineIntegrityMilliUnits,
        item.FlightDeckIntegrityMilliUnits,
        item.CrewMilliUnits,
        item.PortIntegrityMilliUnits,
        item.StarboardIntegrityMilliUnits,
        item.EcmMilliUnits,
        item.CountermeasureMilliUnits,
        item.WeaponReadyMilliUnits,
        item.HostedAircraft,
        item.SnorkelCycle);
}
