using System;
using System.Collections.Generic;
using System.Linq;

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

internal sealed class FleetClashGenerationOptions
{
    internal FleetClashSize? Size { get; set; }

    internal string? Region { get; set; }

    internal int? SeaState { get; set; }

    internal string? PlayerTaskforce { get; set; }

    internal double EnemySizeMultiplier { get; set; } = 1d;

    internal int PlayerControlPercentage { get; set; } = 100;

    internal FleetControlPriority ControlPriority { get; set; } =
        FleetControlPriority.StrongestFirst;

    internal bool Spectator { get; set; }

    internal bool CivilianAirTraffic { get; set; }

    internal bool CivilianNavalTraffic { get; set; }
}

internal sealed class FleetSidePlan(
    string displayName,
    IReadOnlyList<string> regionalSelections,
    IReadOnlyList<string> alliedSelections)
{
    internal string DisplayName { get; } = displayName;

    internal IReadOnlyList<string> RegionalSelections { get; } = regionalSelections;

    internal IReadOnlyList<string> AlliedSelections { get; } = alliedSelections;
}

internal sealed class FleetClashTheater(
    string name,
    float latitude,
    float longitude,
    FleetSidePlan first,
    FleetSidePlan second)
{
    internal string Name { get; } = name;

    internal float Latitude { get; } = latitude;

    internal float Longitude { get; } = longitude;

    internal FleetSidePlan First { get; } = first;

    internal FleetSidePlan Second { get; } = second;
}

internal sealed class FleetClashMatch(
    string theater,
    float latitude,
    float longitude,
    string firstName,
    string secondName,
    FleetClashForce first,
    FleetClashForce second,
    double balanceError)
{
    internal string Theater { get; } = theater;

    internal float Latitude { get; } = latitude;

    internal float Longitude { get; } = longitude;

    internal string FirstName { get; } = firstName;

    internal string SecondName { get; } = secondName;

    internal FleetClashForce First { get; } = first;

    internal FleetClashForce Second { get; } = second;

    internal double BalanceError { get; } = balanceError;
}

internal sealed class FleetClashForce(
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

internal sealed class FleetClashShip(
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

internal sealed class FleetUnitArchetype(
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

internal sealed class FleetCandidate(
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
