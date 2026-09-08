namespace CrimeanTom.SeaPower;

// Conservative planning envelopes, not injected contacts or a replacement for native sensor simulation.
internal static class ScenarioDetectionPicture
{
    internal static bool Exposed(MissionWriter mission, DeployedFleetUnit unit, FleetDeployment opposition) =>
        opposition.Aircraft.Any(aircraft => !aircraft.AirPlan.IsHelicopter &&
            Within(unit.X, unit.Z, aircraft.X, aircraft.Z, aircraft.StationRole == "AEW" ? 220 : 80)) ||
        NearbySensors(mission, unit.Reference.StartsWith("Taskforce1", StringComparison.Ordinal) ? "Taskforce2" : "Taskforce1")
            .Any(sensor => Within(unit.X, unit.Z, sensor.X, sensor.Z, sensor.Range));

    private static IEnumerable<(float X, float Z, float Range)> NearbySensors(MissionWriter mission, string side)
    {
        foreach (string kind in new[] { "LandUnit", "Aircraft" })
        {
            int count = int.Parse(mission.Read("Mission", $"NumberOf{side}{kind}{(kind == "Aircraft" ? "" : "s")}", "0"));
            for (int index = 1; index <= count; index++)
            {
                string reference = side + kind + index;
                if (mission.Read(reference, "RadarsActive") != "True" &&
                    mission.Read(reference, "CrimeanTomIndependentAircraft") != "True") continue;
                string[] position = mission.Read(reference, "RelativePositionInNM").Split(',');
                if (position.Length == 3 && float.TryParse(position[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x) && float.TryParse(position[2],
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                    yield return (x, z, kind == "LandUnit" ? 55 : 220);
            }
        }
    }

    private static bool Within(float x, float z, float otherX, float otherZ, float range) =>
        Math.Pow(x - otherX, 2) + Math.Pow(z - otherZ, 2) <= range * range;
}
