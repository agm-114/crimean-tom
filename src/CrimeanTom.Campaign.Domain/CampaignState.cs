using System.Collections.Immutable;

using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.Domain;

public sealed record CampaignMetadata(
    CampaignId CampaignId,
    string Name,
    long Seed,
    string CatalogFingerprint,
    int SchemaVersion,
    int AlgorithmVersion);

public sealed record RegionState(
    RegionId RegionId,
    string Name,
    ImmutableSortedSet<RegionId> Neighbors,
    EntityRevision Revision);

public sealed record BaseState(
    BaseId BaseId,
    string Name,
    CoalitionId CoalitionId,
    RegionId RegionId,
    GeoCoordinate Coordinate,
    ImmutableSortedDictionary<ResourceKind, ResourceAmount> Resources,
    EntityRevision Revision);

public sealed record EntityState(
    EntityId EntityId,
    string Name,
    string CatalogUnitId,
    string CatalogLoadoutId,
    CoalitionId CoalitionId,
    RegionId RegionId,
    GeoCoordinate Coordinate,
    BaseId? HomeBaseId,
    EntityLifecycle Lifecycle,
    FidelityState Fidelity,
    SimulationDomain SimulationDomain,
    FidelityState MaximumFidelity,
    ImmutableSortedDictionary<ResourceKind, ResourceAmount> Resources,
    HeadingMilliDegrees Heading,
    int Telegraph,
    int ReadinessMilliUnits,
    int DamageMilliUnits,
    string ClassifiedTruthTag,
    ImmutableArray<OperationalWeaponCapability> OperationalWeapons,
    EntityRevision Revision);

public sealed record GroupState(
    GroupId GroupId,
    string Name,
    CoalitionId CoalitionId,
    ImmutableSortedSet<EntityId> Members,
    OrderId? ActiveOrderId,
    EntityRevision Revision);

public sealed record OrderState(
    OrderId OrderId,
    GroupId GroupId,
    OrderKind Kind,
    RegionId DestinationRegionId,
    CampaignTime StartsAt,
    CampaignTime? MustCompleteBy,
    CampaignTime? CompletedAt,
    EntityRevision Revision);

public sealed record ScheduledActivityState(
    ScheduledActivityId ActivityId,
    CampaignActivityKind Kind,
    CampaignSubject Subject,
    CampaignTime DueAt,
    EntityRevision Revision);

public readonly record struct IntelligenceKey(CoalitionId ObserverCoalitionId, EntityId ContactEntityId)
    : IComparable<IntelligenceKey>
{
    public int CompareTo(IntelligenceKey other)
    {
        var coalition = ObserverCoalitionId.CompareTo(other.ObserverCoalitionId);
        return coalition != 0 ? coalition : ContactEntityId.CompareTo(other.ContactEntityId);
    }
}

public sealed record IntelligenceEstimate(
    IntelligenceKey Key,
    RegionId EstimatedRegionId,
    GeoCoordinate EstimatedCoordinate,
    IntelligenceConfidence Confidence,
    CampaignTime ObservedAt,
    int UncertaintyRadiusMeters,
    ContactClassification Classification,
    EntityRevision Revision);

public sealed record WeatherState(
    RegionId RegionId,
    HeadingMilliDegrees WindDirection,
    SpeedMillimetersPerSecond WindSpeed,
    DistanceMeters Visibility,
    int SeaStateMilliUnits,
    EntityRevision Revision);

public sealed record AssignmentState(
    AssignmentId AssignmentId,
    EntityId EntityId,
    AssignmentRole Role,
    AuthorityEpoch AuthorityEpoch,
    CampaignTime EffectiveAt,
    AssignmentLifecycle Lifecycle,
    EntityRevision Revision,
    string BatchId = "",
    int BatchSize = 1,
    string WorkerClientInstanceId = "");

public sealed record SchedulerPolicyState(
    ImmutableSortedSet<EntityId> ObservedEntityIds,
    ImmutableSortedSet<EntityId> ControlledEntityIds,
    string Health,
    string Eligibility,
    int TacticalEntityCapacity,
    int BacklogDepth,
    string RequestHash,
    int StableSampleCount,
    CampaignTime EffectiveAt,
    EntityRevision Revision,
    int TacticalReserveCapacity = 0);

public sealed record CampaignState
{
    public required CampaignMetadata Metadata { get; init; }
    public CampaignTime Time { get; init; } = CampaignTime.Origin;
    public CommitPosition Head { get; init; } = CommitPosition.Origin;
    public EntityRevision CampaignRevision { get; init; } = EntityRevision.None;
    public ImmutableSortedDictionary<RegionId, RegionState> Regions { get; init; } = ImmutableSortedDictionary<RegionId, RegionState>.Empty;
    public ImmutableSortedDictionary<BaseId, BaseState> Bases { get; init; } = ImmutableSortedDictionary<BaseId, BaseState>.Empty;
    public ImmutableSortedDictionary<EntityId, EntityState> Entities { get; init; } = ImmutableSortedDictionary<EntityId, EntityState>.Empty;
    public ImmutableSortedDictionary<GroupId, GroupState> Groups { get; init; } = ImmutableSortedDictionary<GroupId, GroupState>.Empty;
    public ImmutableSortedDictionary<OrderId, OrderState> Orders { get; init; } = ImmutableSortedDictionary<OrderId, OrderState>.Empty;
    public ImmutableSortedDictionary<IntelligenceKey, IntelligenceEstimate> Intelligence { get; init; } = ImmutableSortedDictionary<IntelligenceKey, IntelligenceEstimate>.Empty;
    public ImmutableSortedDictionary<RegionId, WeatherState> Weather { get; init; } = ImmutableSortedDictionary<RegionId, WeatherState>.Empty;
    public ImmutableSortedDictionary<AssignmentId, AssignmentState> Assignments { get; init; } = ImmutableSortedDictionary<AssignmentId, AssignmentState>.Empty;
    public SchedulerPolicyState? SchedulerPolicy { get; init; }
    public ImmutableSortedDictionary<ScheduledActivityId, ScheduledActivityState> ScheduledActivities { get; init; } = ImmutableSortedDictionary<ScheduledActivityId, ScheduledActivityState>.Empty;
    public ImmutableArray<OperationalOutcome> RecentOperationalOutcomes { get; init; } = [];

    public static CampaignState Create(CampaignMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        CampaignInvariants.RequireId(metadata.CampaignId);
        CampaignInvariants.RequireText(metadata.Name, nameof(metadata.Name));
        CampaignInvariants.RequireText(metadata.CatalogFingerprint, nameof(metadata.CatalogFingerprint));

        if (metadata.SchemaVersion != CampaignSchema.Version || metadata.AlgorithmVersion != CampaignSchema.AlgorithmVersion)
        {
            throw new InvalidOperationException("Campaign metadata versions are not supported by this engine.");
        }

        return new CampaignState { Metadata = metadata };
    }
}

public static class CampaignInvariants
{
    public static void RequireId<T>(T id) where T : struct, ICampaignId
    {
        if (id.Value == Guid.Empty)
        {
            throw new InvalidOperationException($"{typeof(T).Name} cannot be empty.");
        }
    }

    public static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} cannot be empty.");
        }
    }
}
