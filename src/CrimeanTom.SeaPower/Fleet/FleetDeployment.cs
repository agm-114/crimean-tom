namespace CrimeanTom.SeaPower;

internal record FleetDeployment(
    IReadOnlyList<DeployedFleetUnit> Units,
    IReadOnlyList<FleetFormationPlan> Formations,
    IReadOnlyList<DeployedAircraft> Aircraft,
    int ControlledCount)
{
    internal int VesselCount => Units.Count(unit => !unit.Ship.IsSubmarine);
    internal int SubmarineCount => Units.Count(unit => unit.Ship.IsSubmarine);
    internal int FixedWingCount => Aircraft.Count(unit => !unit.AirPlan.IsHelicopter);
    internal int HelicopterCount => Aircraft.Count(unit => unit.AirPlan.IsHelicopter);
    internal IReadOnlyList<string> AlliedReferences => Units
        .Where(unit => unit.SourceIndex >= ControlledCount)
        .Select(unit => unit.Reference)
        .Concat(Aircraft
            .Where(aircraft => aircraft.ParentSourceIndex >= ControlledCount)
            .Select(aircraft => aircraft.Reference))
        .ToArray();

    internal void WriteTo(MissionWriter mission, bool maskFirstNation = false)
    {
        Units.ForEach(unit => unit.WriteTo(mission, maskFirstNation && unit.SourceIndex == 0));
        Aircraft.ForEach(aircraft => aircraft.WriteTo(mission));
    }
}

internal record DeployedFleetUnit(
    int SourceIndex,
    FleetClashShip Ship,
    string Reference,
    float X,
    float Z,
    IReadOnlyList<MissionWaypoint>? Waypoints,
    string MissionType,
    string StationRole,
    int Telegraph,
    float Heading)
{
    internal void WriteTo(MissionWriter mission, bool maskNation = false)
    {
        mission.Section(Reference);
        mission.Write("Type", Ship.UnitType);
        mission.Write("VariantReference", Ship.VariantReference);
        mission.Write("Nation", maskNation ? "Unknown" : Ship.Nation);
        if (StationRole != "Default") mission.Write("StationRole", StationRole);
        mission.Write("MissionType", MissionType);
        mission.Write("WeaponStatus", "Free");
        mission.Write("RadarsActive", false);
        mission.Write("ActiveSonarsEnabled", false);
        if (Ship.IsSubmarine) mission.Write("TowedArrayDeployed", true);
        mission.Write("CrewSkill", "Trained");
        mission.Write("Morale", 3);
        mission.Write("RetreatAfterWeaponsExpended", true);
        mission.WritePosition("RelativePositionInNM", new(X, 0, Z, Ship.IsSubmarine ? "shallow" : null));
        mission.Write("Telegraph", Telegraph);
        mission.Write("Heading", Heading, "0");
        if (Waypoints is not null) mission.WriteWaypoints(Waypoints);
    }
}

internal record DeployedAircraft(
    int ParentSourceIndex,
    string Reference,
    string HomeBaseReference,
    CarrierAirPlan AirPlan,
    float X,
    float Z,
    float Altitude,
    IReadOnlyList<MissionWaypoint>? Waypoints,
    float Heading,
    string StationRole)
{
    internal void WriteTo(MissionWriter mission)
    {
        mission.Section(Reference);
        mission.Write("Type", AirPlan.AircraftType);
        mission.Write("SquadronReference", AirPlan.SquadronReference);
        mission.Write("LoadoutVariant", AirPlan.LoadoutVariant);
        mission.Write("MissionType", AirPlan.MissionType);
        mission.Write("StationRole", StationRole);
        mission.Write("WeaponStatus", "Free");
        mission.Write("RadarsActive", StationRole == "AEW" || !AirPlan.IsHelicopter);
        mission.Write("CrewSkill", "Trained");
        mission.WritePosition("RelativePositionInNM", new(X, Altitude, Z));
        mission.Write("Telegraph", StationRole == "AEW" ? 2 : 3);
        mission.Write("Heading", Heading, "0");
        if (Waypoints is not null) mission.WriteWaypoints(Waypoints);
        mission.Write("HomeBase", HomeBaseReference);
    }
}

internal record FleetFormationPlan(
    IReadOnlyList<string> References,
    string Name,
    string Pattern,
    float Spacing)
{
    internal string ToMissionValue() => FormattableString.Invariant(
        $"{string.Join(',', References)}|{Name}|{Pattern}|{Spacing:0.##}|OverrideSpawnPositions");
}