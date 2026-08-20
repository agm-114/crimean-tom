using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimeanTom.Campaign.Contracts;

public static class CampaignSchema
{
    public const int Version = 1;
    public const int AlgorithmVersion = 1;
    public const long TimeQuantumMicroseconds = 1_000_000;
}

public enum EntityLifecycle
{
    Planned,
    Available,
    Deployed,
    Disabled,
    Destroyed,
    Retired,
}

public enum FidelityState
{
    Strategic,
    Operational,
    Tactical,
    Suspended,
}

public enum ResourceKind
{
    Fuel,
    Stores,
    SpareParts,
    Personnel,
    Munitions,
}

public enum OrderKind
{
    Hold,
    Transit,
    Patrol,
    Escort,
    Resupply,
    Refit,
    Reinforce,
}

public enum AssignmentRole
{
    Primary,
    Shadow,
    Observer,
    RenderProxy,
}

public enum IntelligenceConfidence
{
    Unknown,
    Possible,
    Probable,
    Confirmed,
}

public enum CampaignSubjectKind
{
    Campaign,
    Region,
    Base,
    Entity,
    Group,
    Intelligence,
    Weather,
    Assignment,
}

public readonly record struct CampaignSubject(CampaignSubjectKind Kind, Guid Value)
{
    public static CampaignSubject Campaign(CampaignId id) => new(CampaignSubjectKind.Campaign, id.Value);
    public static CampaignSubject Region(RegionId id) => new(CampaignSubjectKind.Region, id.Value);
    public static CampaignSubject Base(BaseId id) => new(CampaignSubjectKind.Base, id.Value);
    public static CampaignSubject Entity(EntityId id) => new(CampaignSubjectKind.Entity, id.Value);
    public static CampaignSubject Group(GroupId id) => new(CampaignSubjectKind.Group, id.Value);
    public static CampaignSubject Assignment(AssignmentId id) => new(CampaignSubjectKind.Assignment, id.Value);
}

public sealed record InformationScope(CoalitionId CoalitionId, bool MayCommand, bool MaySeeClassifiedTruth);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "commandType")]
[JsonDerivedType(typeof(RegisterRegionCommand), "register-region/v1")]
[JsonDerivedType(typeof(RegisterBaseCommand), "register-base/v1")]
[JsonDerivedType(typeof(RegisterEntityCommand), "register-entity/v1")]
[JsonDerivedType(typeof(FormGroupCommand), "form-group/v1")]
[JsonDerivedType(typeof(IssueOrderCommand), "issue-order/v1")]
[JsonDerivedType(typeof(ChangeFidelityCommand), "change-fidelity/v1")]
[JsonDerivedType(typeof(AssignAuthorityCommand), "assign-authority/v1")]
[JsonDerivedType(typeof(ReportIntelligenceCommand), "report-intelligence/v1")]
[JsonDerivedType(typeof(SetWeatherCommand), "set-weather/v1")]
[JsonDerivedType(typeof(AdvanceClockCommand), "advance-clock/v1")]
public abstract record CampaignCommandData;

public sealed record RegisterRegionCommand(RegionId RegionId, string Name, IReadOnlyList<RegionId> Neighbors)
    : CampaignCommandData;

public sealed record RegisterBaseCommand(
    BaseId BaseId,
    string Name,
    CoalitionId CoalitionId,
    RegionId RegionId,
    GeoCoordinate Coordinate,
    IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources)
    : CampaignCommandData;

public sealed record RegisterEntityCommand(
    EntityId EntityId,
    string Name,
    string CatalogUnitId,
    CoalitionId CoalitionId,
    RegionId RegionId,
    GeoCoordinate Coordinate,
    BaseId? HomeBaseId,
    EntityLifecycle Lifecycle,
    FidelityState Fidelity,
    IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources,
    string ClassifiedTruthTag)
    : CampaignCommandData;

public sealed record FormGroupCommand(GroupId GroupId, string Name, CoalitionId CoalitionId, IReadOnlyList<EntityId> Members)
    : CampaignCommandData;

public sealed record IssueOrderCommand(
    OrderId OrderId,
    GroupId GroupId,
    OrderKind Kind,
    RegionId DestinationRegionId,
    CampaignTime StartsAt,
    CampaignTime? MustCompleteBy)
    : CampaignCommandData;

public sealed record ChangeFidelityCommand(EntityId EntityId, FidelityState Fidelity) : CampaignCommandData;

public sealed record AssignAuthorityCommand(
    AssignmentId AssignmentId,
    EntityId EntityId,
    AssignmentRole Role,
    AuthorityEpoch AuthorityEpoch,
    CampaignTime EffectiveAt)
    : CampaignCommandData;

public sealed record ReportIntelligenceCommand(
    CoalitionId ObserverCoalitionId,
    EntityId ContactEntityId,
    RegionId EstimatedRegionId,
    GeoCoordinate EstimatedCoordinate,
    IntelligenceConfidence Confidence,
    CampaignTime ObservedAt)
    : CampaignCommandData;

public sealed record SetWeatherCommand(
    RegionId RegionId,
    int WindDirectionMilliDegrees,
    long WindMillimetersPerSecond,
    int VisibilityMeters,
    int SeaStateMilliUnits)
    : CampaignCommandData;

public sealed record AdvanceClockCommand(CampaignTime NewTime) : CampaignCommandData;

public sealed record CampaignCommand(
    CommandId CommandId,
    CampaignId CampaignId,
    PlayerId PlayerId,
    CampaignSubject Subject,
    EntityRevision ExpectedSubjectRevision,
    CampaignTime RequestedAt,
    CampaignTime EffectiveAt,
    InformationScope Scope,
    CampaignCommandData Data,
    int SchemaVersion = CampaignSchema.Version);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(RegionRegisteredEvent), "region-registered/v1")]
[JsonDerivedType(typeof(BaseRegisteredEvent), "base-registered/v1")]
[JsonDerivedType(typeof(EntityRegisteredEvent), "entity-registered/v1")]
[JsonDerivedType(typeof(GroupFormedEvent), "group-formed/v1")]
[JsonDerivedType(typeof(OrderIssuedEvent), "order-issued/v1")]
[JsonDerivedType(typeof(FidelityChangedEvent), "fidelity-changed/v1")]
[JsonDerivedType(typeof(AuthorityAssignedEvent), "authority-assigned/v1")]
[JsonDerivedType(typeof(IntelligenceReportedEvent), "intelligence-reported/v1")]
[JsonDerivedType(typeof(WeatherSetEvent), "weather-set/v1")]
[JsonDerivedType(typeof(ClockAdvancedEvent), "clock-advanced/v1")]
public abstract record CampaignEventData;

public sealed record RegionRegisteredEvent(RegisterRegionCommand Region) : CampaignEventData;
public sealed record BaseRegisteredEvent(RegisterBaseCommand Base) : CampaignEventData;
public sealed record EntityRegisteredEvent(RegisterEntityCommand Entity) : CampaignEventData;
public sealed record GroupFormedEvent(FormGroupCommand Group) : CampaignEventData;
public sealed record OrderIssuedEvent(IssueOrderCommand Order) : CampaignEventData;
public sealed record FidelityChangedEvent(ChangeFidelityCommand Change) : CampaignEventData;
public sealed record AuthorityAssignedEvent(AssignAuthorityCommand Assignment) : CampaignEventData;
public sealed record IntelligenceReportedEvent(ReportIntelligenceCommand Report) : CampaignEventData;
public sealed record WeatherSetEvent(SetWeatherCommand Weather) : CampaignEventData;
public sealed record ClockAdvancedEvent(AdvanceClockCommand Clock) : CampaignEventData;

public sealed record CommittedCampaignEvent(
    EventId EventId,
    CampaignId CampaignId,
    CommitPosition CommitPosition,
    CampaignSubject Subject,
    EntityRevision NewSubjectRevision,
    CampaignTime SourceTime,
    CampaignTime EffectiveTime,
    CommandId CausalCommandId,
    AssignmentId? AssignmentId,
    AuthorityEpoch AuthorityEpoch,
    CampaignEventData Data,
    int SchemaVersion = CampaignSchema.Version,
    int AlgorithmVersion = CampaignSchema.AlgorithmVersion);

public static class CampaignContractJson
{
    public static byte[] Serialize(CampaignCommand command) =>
        JsonSerializer.SerializeToUtf8Bytes(command, CreateOptions());

    public static byte[] Serialize(CommittedCampaignEvent campaignEvent) =>
        JsonSerializer.SerializeToUtf8Bytes(campaignEvent, CreateOptions());

    public static CampaignCommand DeserializeCommand(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<CampaignCommand>(json, CreateOptions())
        ?? throw new JsonException("Campaign command was null.");

    public static CommittedCampaignEvent DeserializeEvent(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<CommittedCampaignEvent>(json, CreateOptions())
        ?? throw new JsonException("Campaign event was null.");

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
