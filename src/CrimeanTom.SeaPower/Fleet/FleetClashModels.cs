namespace CrimeanTom.SeaPower;

internal enum FleetClashSize
{
    Small,
    Medium,
    Large,
}

internal enum FleetRole
{
    Command,
    AirDefense,
    SurfaceStrike,
    AntiSubmarine,
    Escort,
}

internal enum FleetControlPriority
{
    StrongestFirst,
    WeakestFirst,
}

internal enum FleetComposition { Random, SurfaceFocused, Mixed, SubmarineHeavy, EvenMix }

internal enum RouteOperationPerspective
{
    Escort,
    Interdict,
}

internal enum RouteOperationVariant
{
    CivilianShipping,
    SupplyGroup,
    MinesweepingGroup,
    AmphibiousGroup,
    HighValueShip,
    EvacuationGroup,
    DamagedGroup,
    Replenishment,
}

internal enum ObjectiveOperation { AreaControl, FixedTarget, Passage }
internal enum AreaVariant { SeaLane, Anchorage, Bastion, SurfaceSweep, AswSweep, Chokepoint, PortApproach, Relief, FleetInBeing }
internal enum StrikeVariant { Radar, CoastalBattery, AirDefense, PortRaid, Airfield, Depot, ForwardBase, RearmingShips, RepairingShips, CableTerminal, Communications }
internal enum SearchVariant { Submarine, BallisticSubmarine, SurfaceRaider, Carrier, DamagedGroup, Cruiser, Tender, CounterHunt, Decoys }
internal enum PassageVariant { SurfacePicket, Breakout, AswBarrier, StraitTransit, BlockadeRun, CarrierBreakout, RaiderBreakout, Reinforcement, Rearguard }

internal class FleetClashGenerationOptions
{
    internal int? Seed { get; set; }
    internal FleetClashSize? Size { get; set; }
    internal string? Region { get; set; }
    internal bool ChaosMode { get; set; }
    internal ScenarioSpecialMode SpecialMode { get; set; }
    internal AreaVariant AreaVariant { get; set; }
    internal StrikeVariant StrikeVariant { get; set; }
    internal SearchVariant SearchVariant { get; set; }
    internal PassageVariant PassageVariant { get; set; }
    internal int? SeaState { get; set; }
    internal string? PlayerTaskforce { get; set; }
    internal double EnemySizeMultiplier { get; set; } = 1d;
    internal ScenarioStartingOptions Starting { get; set; } = new();
    internal ScenarioActivitySettings Activity { get; set; } = ScenarioActivitySettings.Default;
    internal ScenarioActivityPlan? ActivityPlan { get; set; }
    internal double ChaseAllowance { get; set; }
    internal RouteEncounterKind RouteEncounter { get; set; } = RouteEncounterKind.Random;
    internal FleetComposition EnemyComposition { get; set; } = FleetComposition.Random;
    internal int PlayerControlPercentage { get; set; } = 100;
    internal FleetControlPriority ControlPriority { get; set; } =
        FleetControlPriority.StrongestFirst;
    internal bool Spectator { get; set; }
    internal bool CivilianAirTraffic { get; set; } = true;
    internal bool CivilianNavalTraffic { get; set; } = true;
    internal RouteOperationPerspective RoutePerspective { get; set; } =
        RouteOperationPerspective.Escort;
    internal RouteOperationVariant RouteVariant { get; set; } =
        RouteOperationVariant.CivilianShipping;
}

internal class FleetSidePlan(
    string displayName,
    IReadOnlyList<string> regionalNations,
    IReadOnlyList<string> alliedNations)
{
    internal string DisplayName { get; } = displayName;
    internal IReadOnlyList<string> RegionalNations { get; } = regionalNations;
    internal IReadOnlyList<string> AlliedNations { get; } = alliedNations;
}

internal class FleetClashTheater(
    string name,
    float latitude,
    float longitude)
{
    internal string Name { get; } = name;
    internal float Latitude { get; } = latitude;
    internal float Longitude { get; } = longitude;
}

internal record FleetClashMatch(
    string Theater,
    float Latitude,
    float Longitude,
    string FirstName,
    string SecondName,
    FleetClashForce First,
    FleetClashForce Second,
    double BalanceError,
    FleetSidePlan? ChaosFirstSide = null,
    FleetSidePlan? ChaosSecondSide = null,
    ScenarioSpecialMode SpecialMode = ScenarioSpecialMode.None);

internal class FleetClashForce(
    IReadOnlyList<FleetClashShip> ships,
    int pointCost,
    int surfaceCapability,
    int airDefenseCapability,
    int aswCapability,
    double resilience)
{
    internal IReadOnlyList<FleetClashShip> Ships { get; } = ships;
    internal int PointCost { get; } = pointCost;
    internal int SurfaceCapability { get; } = surfaceCapability;
    internal int AirDefenseCapability { get; } = airDefenseCapability;
    internal int AswCapability { get; } = aswCapability;
    internal double Resilience { get; } = resilience;

    internal string PrimaryNation => Ships
        .GroupBy(ship => ship.Nation, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Key)
        .FirstOrDefault() ?? string.Empty;

    internal string Summary => string.Join(
        ", ",
        Ships.Select(ship => $"{ship.Nation.Replace('_', ' ')} {ship.UnitType}"));
}

internal class FleetClashShip(
    string unitType,
    string nation,
    string variantReference,
    string stationRole,
    FleetRole role,
    int pointCost,
    int surfaceCapability,
    int airDefenseCapability,
    int aswCapability,
    double resilience,
    bool isSubmarine,
    bool isCarrier)
{
    internal string UnitType { get; } = unitType;
    internal string Nation { get; } = nation;
    internal string VariantReference { get; } = variantReference;
    internal string StationRole { get; } = stationRole;
    internal FleetRole Role { get; } = role;
    internal int PointCost { get; } = pointCost;
    internal int SurfaceCapability { get; } = surfaceCapability;
    internal int AirDefenseCapability { get; } = airDefenseCapability;
    internal int AswCapability { get; } = aswCapability;
    internal double Resilience { get; } = resilience;
    internal bool IsSubmarine { get; } = isSubmarine;
    internal bool IsCarrier { get; } = isCarrier;

    internal double CombatValue =>
        PointCost + SurfaceCapability + AirDefenseCapability + AswCapability +
        (Resilience * 10d);
}

internal class FleetUnitArchetype(
    string unitType,
    string nation,
    IReadOnlyList<string> variantReferences,
    bool isRegional,
    int pointCost,
    int surfaceCapability,
    int airDefenseCapability,
    int aswCapability,
    double resilience,
    bool isSubmarine,
    bool isCarrier)
{
    internal string UnitType { get; } = unitType;
    internal string Nation { get; } = nation;
    internal IReadOnlyList<string> VariantReferences { get; } = variantReferences;
    internal bool IsRegional { get; } = isRegional;
    internal int PointCost { get; } = pointCost;
    internal int SurfaceCapability { get; } = surfaceCapability;
    internal int AirDefenseCapability { get; } = airDefenseCapability;
    internal int AswCapability { get; } = aswCapability;
    internal double Resilience { get; } = resilience;
    internal bool IsSubmarine { get; } = isSubmarine;
    internal bool IsCarrier { get; } = isCarrier;
}

internal class FleetCandidate(
    IReadOnlyList<FleetUnitArchetype> ships,
    IReadOnlyList<FleetRole> roles)
{
    internal IReadOnlyList<FleetUnitArchetype> Ships { get; } = ships;
    internal IReadOnlyList<FleetRole> Roles { get; } = roles;
    internal int PointCost { get; } = ships.Sum(ship => ship.PointCost);
    internal int SurfaceCapability { get; } = ships.Sum(ship => ship.SurfaceCapability);
    internal int AirDefenseCapability { get; } = ships.Sum(ship => ship.AirDefenseCapability);
    internal int AswCapability { get; } = ships.Sum(ship => ship.AswCapability);
    internal double Resilience { get; } = ships.Sum(ship => ship.Resilience);
}
