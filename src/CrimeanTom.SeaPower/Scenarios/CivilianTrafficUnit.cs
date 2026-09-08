namespace CrimeanTom.SeaPower;

internal record CivilianTrafficUnit(
    string Reference, string UnitType, MissionPosition Position, IReadOnlyList<MissionWaypoint> Waypoints,
    string Journey, bool IsAircraft = false, IReadOnlyList<MissionWaypoint>? AirTrafficRoute = null)
{
    internal void WriteTo(MissionWriter mission)
    {
        mission.Section(Reference);
        mission.Write("Type", UnitType);
        mission.Write(IsAircraft ? "SquadronReference" : "VariantReference", "Random");
        mission.Write("CrewSkill", "Trained");
        mission.WritePosition("RelativePositionInNM", Position);
        mission.Write("Telegraph", IsAircraft ? 3 : 2);
        MissionPosition next = Waypoints[0].Position;
        mission.Write("Heading", Math.Atan2(next.X - Position.X, next.Z - Position.Z) * 180 / Math.PI, "0");
        mission.WriteWaypoints(Waypoints);
        mission.Set("CrimeanTom", Reference + "Route", Journey);
    }
}
