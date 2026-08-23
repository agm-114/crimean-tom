using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimeanTom.Campaign.Contracts;

public static class CampaignSchema
{
    public const int Version = 9;
    public const int AlgorithmVersion = 11;
    public const long TimeQuantumMicroseconds = 1_000_000;
    public const long MaximumTacticalAssignmentQuanta = 120;
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

public enum SimulationDomain
{
    Maritime,
    Air,
    Logistics,
    LandAbstract,
    Ground,
    Installation,
    RotaryWing,
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
    Withdraw,
    Intercept,
    Cap,
    Asw,
}

public enum AssignmentRole
{
    Primary,
    Shadow,
    Observer,
    RenderProxy,
}

public enum AssignmentLifecycle
{
    Preparing,
    Active,
    Quiescing,
    Released,
    Prepared,
    Quiesced,
}

public enum IntelligenceConfidence
{
    Unknown,
    Possible,
    Probable,
    Confirmed,
}

public enum ContactClassification
{
    Unknown,
    Surface,
    Subsurface,
    Air,
    Logistics,
    LandFormation,
}

public sealed record OperationalWeaponCapability(
    string AmmunitionId,
    ContactClassification TargetClassification,
    DistanceMeters MaximumRange);

public enum OperationalOutcomeKind
{
    Detection,
    WeaponExpenditure,
    Damage,
    Withdrawal,
    Destruction,
    Launch,
    PointDefense,
    SeekerLost,
    Countermeasure,
    SecondaryExplosion,
}

public enum SensorChannel
{
    Visual,
    Radar,
    Sonar,
    Esm,
    Mad,
    Laser,
    Ecm,
}

public enum TrackQuality
{
    Detected,
    Classified,
    Identified,
}

public enum EmconState
{
    Radiate,
    Passive,
    Silent,
}

public enum RulesOfEngagement
{
    WeaponsHold,
    WeaponsTight,
    WeaponsFree,
}

public enum AlertState
{
    Routine,
    Warning,
    General,
}

public enum OrdnanceFlightStage
{
    Launch,
    Cruise,
    Terminal,
}

public enum OrdnanceGuidance
{
    Inertial,
    Active,
    SemiActive,
    Command,
}

public enum OrdnanceKind
{
    Missile,
    Torpedo,
    Bomb,
    Decoy,
}

public sealed record AnalogueTacticalCondition(
    int AltitudeOrDepthMeters = 0,
    int FireMilliUnits = 0,
    int FloodMilliUnits = 0,
    int PropulsionIntegrityMilliUnits = 1_000,
    int SensorIntegrityMilliUnits = 1_000,
    int WeaponIntegrityMilliUnits = 1_000,
    EmconState Emcon = EmconState.Radiate,
    RulesOfEngagement Roe = RulesOfEngagement.WeaponsTight,
    AlertState Alert = AlertState.Routine,
    int MagazineIntegrityMilliUnits = 1_000,
    int FlightDeckIntegrityMilliUnits = 1_000,
    int CrewMilliUnits = 1_000,
    int PortIntegrityMilliUnits = 1_000,
    int StarboardIntegrityMilliUnits = 1_000,
    int EcmMilliUnits = 0,
    int CountermeasureMilliUnits = 1_000,
    int WeaponReadyMilliUnits = 1_000,
    int HostedAircraft = 0,
    int SnorkelCycle = 0)
{
    public static AnalogueTacticalCondition Healthy { get; } = new();
}

public sealed record InFlightOrdnance(
    string OrdnanceId,
    EntityId ShooterEntityId,
    EntityId TargetEntityId,
    CoalitionId ShooterCoalitionId,
    string AmmunitionId,
    ContactClassification TargetClassification,
    GeoCoordinate Coordinate,
    int HeadingMilliDegrees,
    OrdnanceFlightStage Stage,
    OrdnanceGuidance Guidance,
    long RemainingMeters,
    long SpeedMetersPerTick,
    int InsDriftMilliUnits,
    CampaignTime LaunchedAt,
    OrdnanceKind Kind = OrdnanceKind.Missile,
    int SeekerDelayTicks = 0,
    int LostGuidanceTicks = 0);

public enum CampaignActivityKind
{
    AiIntent,
    Movement,
    Sustainment,
    Repair,
    Attrition,
    Intelligence,
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
    Order,
    ScheduledActivity,
}

public readonly record struct CampaignSubject(CampaignSubjectKind Kind, Guid Value)
{
    public static CampaignSubject Campaign(CampaignId id) => new(CampaignSubjectKind.Campaign, id.Value);
    public static CampaignSubject Region(RegionId id) => new(CampaignSubjectKind.Region, id.Value);
    public static CampaignSubject Base(BaseId id) => new(CampaignSubjectKind.Base, id.Value);
    public static CampaignSubject Entity(EntityId id) => new(CampaignSubjectKind.Entity, id.Value);
    public static CampaignSubject Group(GroupId id) => new(CampaignSubjectKind.Group, id.Value);
    public static CampaignSubject Assignment(AssignmentId id) => new(CampaignSubjectKind.Assignment, id.Value);
    public static CampaignSubject Order(OrderId id) => new(CampaignSubjectKind.Order, id.Value);
    public static CampaignSubject ScheduledActivity(ScheduledActivityId id) => new(CampaignSubjectKind.ScheduledActivity, id.Value);
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
[JsonDerivedType(typeof(SimulateCampaignCommand), "simulate-campaign/v1")]
[JsonDerivedType(typeof(SetAssignmentLifecycleCommand), "set-assignment-lifecycle/v1")]
[JsonDerivedType(typeof(ActivateTacticalAssignmentCommand), "activate-tactical-assignment/v1")]
[JsonDerivedType(typeof(ActivateTacticalIslandCommand), "activate-tactical-island/v1")]
[JsonDerivedType(typeof(ReleaseTacticalIslandCommand), "release-tactical-island/v1")]
[JsonDerivedType(typeof(MigrateTacticalIslandCommand), "migrate-tactical-island/v1")]
[JsonDerivedType(typeof(UpdateSchedulerPolicyCommand), "update-scheduler-policy/v1")]
[JsonDerivedType(typeof(ApplyTacticalStateCommand), "apply-tactical-state/v1")]
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
    string ClassifiedTruthTag,
    SimulationDomain SimulationDomain = SimulationDomain.Maritime,
    FidelityState MaximumFidelity = FidelityState.Tactical,
    string CatalogLoadoutId = "Default",
    IReadOnlyList<OperationalWeaponCapability>? OperationalWeapons = null)
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

public sealed record ChangeFidelityCommand(
    EntityId EntityId,
    FidelityState Fidelity,
    string Reason = "manual") : CampaignCommandData;

public sealed record AssignAuthorityCommand(
    AssignmentId AssignmentId,
    EntityId EntityId,
    AssignmentRole Role,
    AuthorityEpoch AuthorityEpoch,
    CampaignTime EffectiveAt,
    string BatchId = "",
    int BatchSize = 1,
    string WorkerClientInstanceId = "")
    : CampaignCommandData;

public sealed record ReportIntelligenceCommand(
    CoalitionId ObserverCoalitionId,
    EntityId ContactEntityId,
    RegionId EstimatedRegionId,
    GeoCoordinate EstimatedCoordinate,
    IntelligenceConfidence Confidence,
    CampaignTime ObservedAt,
    int UncertaintyRadiusMeters = 100_000,
    ContactClassification Classification = ContactClassification.Unknown)
    : CampaignCommandData;

public sealed record SetWeatherCommand(
    RegionId RegionId,
    int WindDirectionMilliDegrees,
    long WindMillimetersPerSecond,
    int VisibilityMeters,
    int SeaStateMilliUnits)
    : CampaignCommandData;

public sealed record AdvanceClockCommand(CampaignTime NewTime) : CampaignCommandData;

public sealed record SimulateCampaignCommand(CampaignTime NewTime) : CampaignCommandData;

public sealed record SetAssignmentLifecycleCommand(
    AssignmentId AssignmentId,
    AuthorityEpoch AuthorityEpoch,
    AssignmentLifecycle Lifecycle,
    CampaignTime EffectiveAt)
    : CampaignCommandData;

public sealed record ActivateTacticalAssignmentCommand(
    AssignmentId AssignmentId,
    AuthorityEpoch AuthorityEpoch,
    EntityId EntityId,
    EntityRevision ExpectedEntityRevision,
    CampaignTime EffectiveAt,
    string Reason)
    : CampaignCommandData;

public sealed record TacticalIslandActivationMember(
    AssignmentId AssignmentId,
    AuthorityEpoch AuthorityEpoch,
    EntityRevision ExpectedAssignmentRevision,
    EntityId EntityId,
    EntityRevision ExpectedEntityRevision);

public sealed record ActivateTacticalIslandCommand(
    string BatchId,
    IReadOnlyList<TacticalIslandActivationMember> Members,
    CampaignTime EffectiveAt,
    string Reason)
    : CampaignCommandData;

public sealed record TacticalIslandReleaseMember(
    AssignmentId AssignmentId,
    AuthorityEpoch AuthorityEpoch,
    EntityRevision ExpectedAssignmentRevision,
    EntityId EntityId,
    EntityRevision ExpectedEntityRevision);

public sealed record ReleaseTacticalIslandCommand(
    string BatchId,
    IReadOnlyList<TacticalIslandReleaseMember> Members,
    CampaignTime EffectiveAt,
    string Reason)
    : CampaignCommandData;

public sealed record TacticalIslandMigrationMember(
    AssignmentId SourceAssignmentId,
    AuthorityEpoch SourceAuthorityEpoch,
    EntityRevision ExpectedSourceAssignmentRevision,
    AssignmentId DestinationAssignmentId,
    AuthorityEpoch DestinationAuthorityEpoch,
    EntityRevision ExpectedDestinationAssignmentRevision,
    EntityId EntityId,
    EntityRevision ExpectedEntityRevision);

public sealed record MigrateTacticalIslandCommand(
    string BatchId,
    IReadOnlyList<TacticalIslandMigrationMember> Members,
    CampaignTime EffectiveAt,
    string Reason)
    : CampaignCommandData;

public sealed record UpdateSchedulerPolicyCommand(
    IReadOnlyList<EntityId> ObservedEntityIds,
    IReadOnlyList<EntityId> ControlledEntityIds,
    string Health,
    string Eligibility,
    int TacticalEntityCapacity,
    int BacklogDepth,
    string RequestHash,
    int StableSampleCount,
    CampaignTime EffectiveAt,
    int TacticalReserveCapacity = 0)
    : CampaignCommandData;

public sealed record ApplyTacticalStateCommand(
    AssignmentId AssignmentId,
    AuthorityEpoch AuthorityEpoch,
    EntityId EntityId,
    GeoCoordinate Coordinate,
    int HeadingMilliDegrees,
    int Telegraph,
    IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources,
    int DamageMilliUnits,
    EntityLifecycle Lifecycle)
    : CampaignCommandData;

public sealed record CampaignCommand(
    CommandId CommandId,
    IdempotencyKey IdempotencyKey,
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
[JsonDerivedType(typeof(CampaignSimulatedEvent), "campaign-simulated/v1")]
[JsonDerivedType(typeof(AssignmentLifecycleChangedEvent), "assignment-lifecycle-changed/v1")]
[JsonDerivedType(typeof(TacticalAssignmentActivatedEvent), "tactical-assignment-activated/v1")]
[JsonDerivedType(typeof(TacticalIslandActivatedEvent), "tactical-island-activated/v1")]
[JsonDerivedType(typeof(TacticalIslandReleasedEvent), "tactical-island-released/v1")]
[JsonDerivedType(typeof(TacticalIslandMigratedEvent), "tactical-island-migrated/v1")]
[JsonDerivedType(typeof(SchedulerPolicyUpdatedEvent), "scheduler-policy-updated/v1")]
[JsonDerivedType(typeof(TacticalStateAppliedEvent), "tactical-state-applied/v1")]
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

public sealed record SimulatedEntityUpdate(
    EntityId EntityId,
    RegionId RegionId,
    GeoCoordinate Coordinate,
    EntityLifecycle Lifecycle,
    IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources,
    int ReadinessMilliUnits,
    int DamageMilliUnits,
    EntityRevision Revision,
    int HeadingMilliDegrees = 0,
    int Telegraph = 0,
    AnalogueTacticalCondition? Analogue = null);

public sealed record SimulatedBaseUpdate(
    BaseId BaseId,
    IReadOnlyDictionary<ResourceKind, ResourceAmount> Resources,
    EntityRevision Revision);

public sealed record SimulatedGroupUpdate(GroupId GroupId, OrderId? ActiveOrderId, EntityRevision Revision);

public sealed record SimulatedOrderUpdate(
    OrderId OrderId,
    GroupId GroupId,
    OrderKind Kind,
    RegionId DestinationRegionId,
    CampaignTime StartsAt,
    CampaignTime? MustCompleteBy,
    CampaignTime? CompletedAt,
    EntityRevision Revision);

public sealed record SimulatedIntelligenceUpdate(
    CoalitionId ObserverCoalitionId,
    EntityId ContactEntityId,
    RegionId EstimatedRegionId,
    GeoCoordinate EstimatedCoordinate,
    IntelligenceConfidence Confidence,
    CampaignTime ObservedAt,
    int UncertaintyRadiusMeters,
    ContactClassification Classification,
    EntityRevision Revision,
    SensorChannel Channel = SensorChannel.Visual,
    TrackQuality Quality = TrackQuality.Detected,
    int ObservedHeadingMilliDegrees = 0,
    int ObservedSpeedMetersPerTick = 0);

public sealed record OperationalOutcome(
    string OutcomeId,
    CampaignTime OccurredAt,
    OperationalOutcomeKind Kind,
    RegionId RegionId,
    EntityId ActorEntityId,
    EntityId? TargetEntityId,
    long MunitionsExpendedMilliUnits,
    int DamageInflictedMilliUnits,
    string Detail);

public sealed record ScheduledCampaignActivity(
    ScheduledActivityId ActivityId,
    CampaignActivityKind Kind,
    CampaignSubject Subject,
    CampaignTime DueAt,
    EntityRevision Revision);

public sealed record CampaignSimulatedEvent(
    CampaignTime NewTime,
    IReadOnlyList<SimulatedEntityUpdate> Entities,
    IReadOnlyList<SimulatedBaseUpdate> Bases,
    IReadOnlyList<SimulatedGroupUpdate> Groups,
    IReadOnlyList<SimulatedOrderUpdate> Orders,
    IReadOnlyList<SimulatedIntelligenceUpdate> Intelligence,
    IReadOnlyList<OperationalOutcome> Outcomes,
    IReadOnlyList<ScheduledCampaignActivity> PendingActivities,
    IReadOnlyList<InFlightOrdnance>? InFlight = null)
    : CampaignEventData;

public sealed record AssignmentLifecycleChangedEvent(SetAssignmentLifecycleCommand Change) : CampaignEventData;

public sealed record TacticalAssignmentActivatedEvent(ActivateTacticalAssignmentCommand Activation) : CampaignEventData;

public sealed record TacticalIslandActivatedEvent(ActivateTacticalIslandCommand Activation) : CampaignEventData;

public sealed record TacticalIslandReleasedEvent(ReleaseTacticalIslandCommand Release) : CampaignEventData;

public sealed record TacticalIslandMigratedEvent(MigrateTacticalIslandCommand Migration) : CampaignEventData;

public sealed record SchedulerPolicyUpdatedEvent(UpdateSchedulerPolicyCommand Policy) : CampaignEventData;

public sealed record TacticalStateAppliedEvent(ApplyTacticalStateCommand State) : CampaignEventData;

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
