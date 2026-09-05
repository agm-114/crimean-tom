namespace CrimeanTom.SeaPower;

internal record MissionTrigger(int Number, string Name, MissionUnitCondition Condition, MissionOutcome Outcome)
{
    internal void WriteTo(MissionWriter mission)
    {
        mission.Section($"Trigger{Number}");
        mission.Write("Name", Name);
        Condition.WriteTo(mission);
        mission.Write("ConditionsCompleted", "<Condition1>");
        Outcome.WriteTo(mission);
    }
}

// An area checks arrivals; without one, the condition counts destroyed units.
internal record MissionUnitCondition(IReadOnlyList<string> Units, int MinimumUnits, MissionArea? Area = null)
{
    internal void WriteTo(MissionWriter mission)
    {
        mission.Write("Condition_Condition1_Type", Area is null ? "UnitDestroyed" : "UnitsInTheArea");
        Area?.WriteTo(mission);
        mission.WriteList("Condition_Condition1_Units", Units);
        mission.Write("Condition_Condition1_MinimumUnits", MinimumUnits);
    }
}

internal record MissionArea(MissionPosition Position, float RadiusNm, string DisplaySide)
{
    internal void WriteTo(MissionWriter mission)
    {
        mission.WritePosition("Condition_Condition1_PositionNM", Position);
        mission.Write("Condition_Condition1_AreaRadiusNM", RadiusNm);
        mission.Write("Condition_Condition1_AreaDisplaySide", DisplaySide);
    }
}

internal record MissionOutcome(
    string WinningTaskforce,
    string VictoryMessage,
    string DefeatMessage,
    IReadOnlyList<string> CompletedObjectives,
    IReadOnlyList<string> FailedObjectives)
{
    internal void WriteTo(MissionWriter mission)
    {
        string losingTaskforce = WinningTaskforce == "Taskforce1" ? "Taskforce2" : "Taskforce1";
        mission.Write($"Action_{WinningTaskforce}_Message", VictoryMessage);
        mission.Write($"Action_{losingTaskforce}_Message", DefeatMessage);
        mission.Write("Action_Victory", WinningTaskforce);
        mission.WriteList("Action_ObjectivesCompleted", CompletedObjectives);
        mission.WriteList("Action_ObjectivesFailed", FailedObjectives);
        mission.Write("Action_EndMission", true);
    }
}