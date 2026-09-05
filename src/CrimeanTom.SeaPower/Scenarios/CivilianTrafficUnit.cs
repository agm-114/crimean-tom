namespace CrimeanTom.SeaPower;

internal record CivilianTrafficUnit(
    string Reference, string UnitType, MissionPosition Position, float Heading, bool IsAircraft = false)
{
    internal void WriteTo(MissionWriter mission)
    {
        mission.Section(Reference);
        mission.Write("Type", UnitType);
        mission.Write(IsAircraft ? "SquadronReference" : "VariantReference", "Random");
        mission.Write("CrewSkill", "Trained");
        mission.WritePosition("RelativePositionInNM", Position);
        mission.Write("Telegraph", IsAircraft ? 3 : 2);
        mission.Write("Heading", Heading, "0");
        mission.WriteWaypoints([new(Position with { X = -Position.X })]);
    }
}