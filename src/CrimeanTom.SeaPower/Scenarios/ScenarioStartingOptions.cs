namespace CrimeanTom.SeaPower;

internal record ScenarioStartingOptions(bool AircraftDeployed = true, bool PreloadOrders = true, bool ControlProtectedShips = false)
{
    internal FleetDeployment Apply(FleetDeployment fleet)
    {
        HashSet<string> removed = AircraftDeployed ? [] : fleet.Aircraft
            .Select(aircraft => aircraft.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return fleet with
        {
            Units = PreloadOrders ? fleet.Units : fleet.Units.Select(unit =>
                unit.SourceIndex < fleet.ControlledCount
                    ? unit with { Waypoints = null, FollowReference = null, MissionType = string.Empty, Telegraph = 0 }
                    : unit).ToArray(),
            Aircraft = !AircraftDeployed ? [] : PreloadOrders ? fleet.Aircraft : fleet.Aircraft.Select(aircraft =>
                aircraft.ParentSourceIndex < fleet.ControlledCount
                    ? aircraft with { Waypoints = null, PreloadOrders = false }
                    : aircraft).ToArray(),
            Formations = fleet.Formations.Where(formation => !formation.References.Any(removed.Contains)).ToArray(),
        };
    }

    internal void WriteTo(MissionWriter mission)
    {
        mission.Write("FriendlyAircraftDeployed", AircraftDeployed);
        mission.Write("PreloadPlayerOrders", PreloadOrders);
        mission.Write("PlayerControlsProtectedShips", ControlProtectedShips);
    }
}

internal record ScenarioDifficulty(string Id, string Name, double Multiplier)
{
    internal static readonly ScenarioDifficulty[] Presets =
    [
        new("Easy", "Easy", 0.5),
        new("Relaxed", "Relaxed", 0.75),
        new("Standard", "Standard", 1),
        new("Hard", "Hard", 1.25),
        new("VeryHard", "Very hard", 1.5),
        new("Extreme", "Extreme", 2),
    ];
    internal static ScenarioDifficulty Find(string? id) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Presets[2];
    internal string Label => FormattableString.Invariant($"{Name} ({Multiplier:0.00}x enemy budget)");
}
