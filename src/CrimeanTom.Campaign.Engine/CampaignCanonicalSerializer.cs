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
                ParseId(item.Id, value => new EntityId(value)), item.Name, item.CatalogUnitId,
                ParseId(item.CoalitionId, value => new CoalitionId(value)),
                ParseId(item.RegionId, value => new RegionId(value)),
                new GeoCoordinate(item.LatitudeMicrodegrees, item.LongitudeMicrodegrees),
                item.HomeBaseId is null ? null : ParseId(item.HomeBaseId, value => new BaseId(value)),
                ParseEnum<EntityLifecycle>(item.Lifecycle), ParseEnum<FidelityState>(item.Fidelity),
                ReadResources(item.Resources), HeadingMilliDegrees.FromMilliDegrees(item.HeadingMilliDegrees), item.Telegraph,
                item.ReadinessMilliUnits, item.DamageMilliUnits,
                item.ClassifiedTruthTag, new EntityRevision(item.Revision)))
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
            Intelligence = document.Intelligence.Select(item =>
            {
                var key = new IntelligenceKey(
                    ParseId(item.ObserverCoalitionId, value => new CoalitionId(value)),
                    ParseId(item.ContactEntityId, value => new EntityId(value)));
                return new IntelligenceEstimate(key,
                    ParseId(item.EstimatedRegionId, value => new RegionId(value)),
                    new GeoCoordinate(item.LatitudeMicrodegrees, item.LongitudeMicrodegrees),
                    ParseEnum<IntelligenceConfidence>(item.Confidence), new CampaignTime(item.ObservedAt),
                    new EntityRevision(item.Revision));
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
                new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.AssignmentId, item => item),
            ScheduledActivities = document.ScheduledActivities.Select(item => new ScheduledActivityState(
                ParseId(item.Id, value => new ScheduledActivityId(value)),
                ParseEnum<CampaignActivityKind>(item.Kind),
                new CampaignSubject(ParseEnum<CampaignSubjectKind>(item.SubjectKind), ParseGuid(item.SubjectId)),
                new CampaignTime(item.DueAt),
                new EntityRevision(item.Revision)))
                .ToImmutableSortedDictionary(item => item.ActivityId, item => item),
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
        WriteIntelligence(writer, state);
        WriteWeather(writer, state);
        WriteAssignments(writer, state);
        WriteScheduledActivities(writer, state);
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
            writer.WriteNumber("lifecycle", (int)item.Lifecycle);
            writer.WriteNumber("fidelity", (int)item.Fidelity);
            WriteResources(writer, item.Resources);
            writer.WriteNumber("headingMilliDegrees", item.Heading.Value);
            writer.WriteNumber("telegraph", item.Telegraph);
            writer.WriteNumber("readinessMilliUnits", item.ReadinessMilliUnits);
            writer.WriteNumber("damageMilliUnits", item.DamageMilliUnits);
            writer.WriteString("classifiedTruthTag", item.ClassifiedTruthTag);
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
            writer.WriteNumber("revision", item.Revision.Value);
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
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
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
        public required IntelligenceDocument[] Intelligence { get; init; }
        public required WeatherDocument[] Weather { get; init; }
        public required AssignmentDocument[] Assignments { get; init; }
        public required ScheduledActivityDocument[] ScheduledActivities { get; init; }
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
        public required string CoalitionId { get; init; }
        public required string RegionId { get; init; }
        public required int LatitudeMicrodegrees { get; init; }
        public required int LongitudeMicrodegrees { get; init; }
        public string? HomeBaseId { get; init; }
        public required int Lifecycle { get; init; }
        public required int Fidelity { get; init; }
        public required ResourceDocument[] Resources { get; init; }
        public required int HeadingMilliDegrees { get; init; }
        public required int Telegraph { get; init; }
        public required int ReadinessMilliUnits { get; init; }
        public required int DamageMilliUnits { get; init; }
        public required string ClassifiedTruthTag { get; init; }
        public required long Revision { get; init; }
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

    private sealed class IntelligenceDocument
    {
        public required string ObserverCoalitionId { get; init; }
        public required string ContactEntityId { get; init; }
        public required string EstimatedRegionId { get; init; }
        public required int LatitudeMicrodegrees { get; init; }
        public required int LongitudeMicrodegrees { get; init; }
        public required int Confidence { get; init; }
        public required long ObservedAt { get; init; }
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
}