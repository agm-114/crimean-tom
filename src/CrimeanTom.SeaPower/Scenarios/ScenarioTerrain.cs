using System.Globalization;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioTerrain
{
    internal static void ValidateNavalSpawns(MissionWriter mission)
    {
        ChartPosition center = new(Number(mission.Read("Environment", "MapCenterLatitude")),
            Number(mission.Read("Environment", "MapCenterLongitude")));
        foreach (string side in new[] { "Taskforce1", "Taskforce2", "Neutral" })
        foreach (string kind in new[] { "Vessel", "Submarine", "Biologic" })
        {
            int count = int.Parse(mission.Read("Mission", $"NumberOf{side}{kind}s", "0"), CultureInfo.InvariantCulture);
            for (int index = 1; index <= count; index++)
            {
                string reference = side + kind + index;
                string[] coordinates = mission.Read(reference, "RelativePositionInNM").Split(',');
                if (coordinates.Length != 3)
                    throw new InvalidOperationException($"Missing starting position for {reference}.");
                // Native 'shallow' depends on class and environment, not a fixed depth.
                // Use an explicit 100 ft start so the seabed clearance check matches the saved position.
                float altitude = coordinates[1] == "shallow" ? -100 : Number(coordinates[1]);
                MissionPosition position = new(Number(coordinates[0]), altitude, Number(coordinates[2]));
                float clearance = Math.Max(kind == "Submarine" ? 80 : 20, -altitude * 0.3048f + 20);
                ChartPosition location = center.Offset(position);
                if (!HasSpawnClearance(location, clearance))
                    throw new UnsafeNavalSpawnException($"Unsafe spawn for {reference} ({mission.Read(reference, "Type")}): land or insufficient seabed clearance. Reroll the layout or choose a more open-water region.");
                if (coordinates[1] == "shallow") mission.Set(reference, "RelativePositionInNM", position.ToMissionValue());
            }
        }

        static float Number(string value) => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
            && float.IsFinite(number) ? number
            : throw new InvalidOperationException($"Invalid naval spawn coordinate: '{value}'.");
    }

    internal static void ValidateDeployment(RouteEncounter voyage, FleetDeployment fleet)
    {
        foreach (DeployedFleetUnit unit in fleet.Units)
        {
            float depth = unit.Ship.IsSubmarine ? 80 : 20;
            ChartPosition position = voyage.Center.Offset(new(unit.X, 0, unit.Z));
            if (HeightMeters(position) > -depth)
                throw new InvalidOperationException($"{unit.Ship.UnitType} has no safe starting depth in this {voyage.Kind} layout. Choose a mid-crossing encounter or smaller force.");
            if (unit.FollowReference is not null || unit.Waypoints is null) continue;
            foreach (MissionWaypoint waypoint in unit.Waypoints)
            {
                ChartPosition next = voyage.Center.Offset(waypoint.Position);
                if (!IsNavigable(position, next, depth))
                    throw new InvalidOperationException($"{unit.Ship.UnitType} would cross land or shallow water in this {voyage.Kind} layout. Choose a mid-crossing encounter or smaller force.");
                position = next;
            }
        }
    }

    internal static bool CanPlace(RouteEncounter voyage, IReadOnlyList<DeployedFleetUnit> units)
    {
        try { ValidateDeployment(voyage, new(units, [], [], 0)); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal static FleetDeployment PlaceFleet(RouteEncounter voyage, FleetDeployment fleet)
    {
        Dictionary<string, DeployedFleetUnit> placed = fleet.Units.ToDictionary(unit => unit.Reference);
        Dictionary<int, MissionPosition> offsets = [];
        foreach (FleetFormationPlan formation in fleet.Formations)
        {
            DeployedFleetUnit[] members = formation.References.Where(placed.ContainsKey).Select(reference => placed[reference]).ToArray();
            if (members.Length == 0) continue;
            bool found = false;
            for (int attempt = 0; attempt < 49; attempt++)
            {
                double angle = (attempt % 8) * Math.PI / 4;
                float radius = attempt == 0 ? 0 : ((attempt - 1) / 8 + 1) * 4;
                MissionPosition offset = new((float)Math.Cos(angle) * radius, 0, (float)Math.Sin(angle) * radius);
                var shifted = members.Select(unit => unit with { X = unit.X + offset.X, Z = unit.Z + offset.Z }).ToArray();
                // A short interception/search leg stays in the encounter instead of sending subs into the harbor.
                if (attempt > 0)
                    shifted = shifted.Select(unit => unit.FollowReference is not null || unit.Waypoints is null ? unit : unit with
                    {
                        Waypoints = unit.Ship.IsSubmarine
                            ? [new(new(unit.X, 0, unit.Z, "shallow")),
                                new(new(unit.X + 3, 0, unit.Z, "shallow"), WaypointOrder.WeaponsFree)]
                            : [new(voyage.At(voyage.LengthNm * 0.25f)),
                                new(voyage.At(voyage.LengthNm * 0.4f), WaypointOrder.WeaponsFree)],
                    }).ToArray();
                try { ValidateDeployment(voyage, fleet with { Units = shifted }); }
                catch (InvalidOperationException) { continue; }
                foreach (DeployedFleetUnit unit in shifted)
                {
                    placed[unit.Reference] = unit;
                    offsets[unit.SourceIndex] = offset;
                }
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException($"No navigable placement for {formation.Name} in this {voyage.Kind} encounter. Try a smaller force or mid-crossing location.");
        }
        FleetDeployment result = fleet with
        {
            Units = fleet.Units.Select(unit => placed[unit.Reference]).ToArray(),
            Aircraft = fleet.Aircraft.Select(aircraft => offsets.TryGetValue(aircraft.ParentSourceIndex, out MissionPosition offset)
                ? aircraft with { X = aircraft.X + offset.X, Z = aircraft.Z + offset.Z } : aircraft).ToArray(),
        };
        ValidateDeployment(voyage, result);
        return result;
    }
}

internal class UnsafeNavalSpawnException(string message) : InvalidOperationException(message);
