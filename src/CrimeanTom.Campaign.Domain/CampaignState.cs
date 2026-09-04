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
    EntityRevision Revision,
    AnalogueTacticalCondition Analogue,
    /// <summary>
    /// Rounds remaining per Sea Power ammunition id, matching the native
    /// <c>AmmunitionAmountDictionary</c>. A missing key means the catalog
    /// declares no countable magazine for that ammunition, so the server does
    /// not round-limit it; a present key holding zero means a spent magazine
    /// and blocks further employment of that ammunition.
    /// </summary>
    ImmutableSortedDictionary<string, int> Magazines,
    /// <summary>
    /// Campaign quantum at which each mount finishes reloading and can fire
    /// again, keyed by the platform's own weapon-system section name. A missing
    /// key means ready now. Tracking this per mount rather than per ammunition
    /// is what lets two tubes fire in quick succession and then both wait,
    /// instead of one averaged cadence that never bursts.
    /// </summary>
    ImmutableSortedDictionary<string, long> MountReadyAt,
    /// <summary>
    /// The radars this platform carries, from the game catalog. Empty means the
    /// catalog resolved none, in which case detection falls back to the
    /// server's approximate horizons rather than treating the platform as
    /// blind.
    /// </summary>
    ImmutableArray<OperationalSensorCapability> OperationalSensors,
    /// <summary>
    /// The fourth root of this platform's radar cross-section, in thousandths,
    /// from the size the game declares for it. Detection range varies with that
    /// fourth root, so a radar's range against this platform is its own
    /// coefficient scaled by this. Zero means the catalog declared nothing and
    /// the platform is not sized for the radar equation.
    /// </summary>
    int RadarReflectivityMilliUnits,
    EntityId? HostEntityId = null)
{
    public static readonly ImmutableSortedDictionary<string, int> NoMagazines =
        ImmutableSortedDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);

    public static readonly ImmutableSortedDictionary<string, long> NoMountReadyAt =
        ImmutableSortedDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal);
}

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

public sealed record StrategicObjectiveState(
    StrategicObjectiveId ObjectiveId,
    CoalitionId CoalitionId,
    RegionId RegionId,
    StrategicObjectiveKind Kind,
    int PriorityMilliUnits,
    CampaignTime CreatedAt,
    long Epoch,
    EntityRevision Revision);

public sealed record GroupCommitmentState(
    GroupId GroupId,
    StrategicObjectiveId? ObjectiveId,
    CampaignTime ExpiresAt,
    bool WithdrawLatched,
    CampaignTime LatchReleaseAt,
    EntityRevision Revision);

public sealed record OperationalPackageMemberState(
    GroupId GroupId,
    OperationalPackageRole Role,
    CampaignTime StartsAt);

public sealed record OperationalPackageState(
    OperationalPackageId PackageId,
    CoalitionId CoalitionId,
    StrategicObjectiveId ObjectiveId,
    RegionId RegionId,
    CampaignTime AssemblyAt,
    CampaignTime ExecuteAt,
    CampaignTime ExpiresAt,
    ImmutableSortedDictionary<GroupId, OperationalPackageMemberState> Members,
    EntityRevision Revision);

public sealed record CoalitionStrategyState(
    CoalitionId CoalitionId,
    CampaignTime NextDirectorAt,
    long Epoch,
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
    EntityRevision Revision,
    SensorChannel Channel = SensorChannel.Visual,
    TrackQuality Quality = TrackQuality.Detected,
    int ObservedHeadingMilliDegrees = 0,
    int ObservedSpeedMetersPerTick = 0);

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
    public ImmutableSortedDictionary<StrategicObjectiveId, StrategicObjectiveState> StrategicObjectives { get; init; } = ImmutableSortedDictionary<StrategicObjectiveId, StrategicObjectiveState>.Empty;
    public ImmutableSortedDictionary<GroupId, GroupCommitmentState> GroupCommitments { get; init; } = ImmutableSortedDictionary<GroupId, GroupCommitmentState>.Empty;
    public ImmutableSortedDictionary<OperationalPackageId, OperationalPackageState> OperationalPackages { get; init; } = ImmutableSortedDictionary<OperationalPackageId, OperationalPackageState>.Empty;
    public ImmutableSortedDictionary<CoalitionId, CoalitionStrategyState> CoalitionStrategies { get; init; } = ImmutableSortedDictionary<CoalitionId, CoalitionStrategyState>.Empty;
    public ImmutableSortedDictionary<IntelligenceKey, IntelligenceEstimate> Intelligence { get; init; } = ImmutableSortedDictionary<IntelligenceKey, IntelligenceEstimate>.Empty;
    public ImmutableSortedDictionary<RegionId, WeatherState> Weather { get; init; } = ImmutableSortedDictionary<RegionId, WeatherState>.Empty;
    public ImmutableSortedDictionary<AssignmentId, AssignmentState> Assignments { get; init; } = ImmutableSortedDictionary<AssignmentId, AssignmentState>.Empty;
    public SchedulerPolicyState? SchedulerPolicy { get; init; }
    public ImmutableSortedDictionary<ScheduledActivityId, ScheduledActivityState> ScheduledActivities { get; init; } = ImmutableSortedDictionary<ScheduledActivityId, ScheduledActivityState>.Empty;
    public ImmutableArray<OperationalOutcome> RecentOperationalOutcomes { get; init; } = [];
    public ImmutableArray<InFlightOrdnance> InFlight { get; init; } = [];

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
