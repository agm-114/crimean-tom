namespace CrimeanTom.SeaPower;

// A voyage is independent of the battle. Only its nearby legs are eligible for initial placement.
internal record CivilianTrafficRoute(string Origin, string Destination, IReadOnlyList<ChartPosition> Points,
    double DistanceNm = 0)
{
    internal IEnumerable<TrafficRouteWindow> Near(ChartPosition center, double radius, double minimumLegRemaining = 2)
    {
        for (int leg = 1; leg < Points.Count; leg++)
        {
            MissionPosition start = Points[leg - 1].RelativeTo(center), end = Points[leg].RelativeTo(center);
            double dx = end.X - start.X, dz = end.Z - start.Z, squared = dx * dx + dz * dz;
            if (squared < 0.01) continue;
            double closest = -(start.X * dx + start.Z * dz) / squared;
            double x = start.X + closest * dx, z = start.Z + closest * dz;
            double clearance = radius * radius - x * x - z * z;
            if (clearance <= 0) continue;
            double half = Math.Sqrt(clearance / squared);
            double from = Math.Max(0, closest - half), to = Math.Min(1, closest + half);
            // Keep a meaningful first leg instead of spawning on top of the next waypoint.
            to = Math.Min(to, 1 - minimumLegRemaining / Math.Sqrt(squared));
            if (to > from) yield return new(this, leg, from, to);
        }
    }

    internal IEnumerable<ChartPosition[]> Crossings(ChartPosition center, double radius)
    {
        List<ChartPosition> points = [];
        int previousLeg = -1;
        foreach (var window in Near(center, radius, minimumLegRemaining: 0))
        {
            if (window.Leg != previousLeg + 1 || window.From > 0)
            {
                points.Clear();
                // Only complete overflights, not routes beginning at an airport inside the scenario area.
                if (window.From > 0)
                    points.Add(Points[window.Leg - 1].Towards(Points[window.Leg], window.From));
            }
            previousLeg = window.Leg;
            if (points.Count == 0) continue;
            points.Add(Points[window.Leg - 1].Towards(Points[window.Leg], window.To));
            if (window.To >= 1) continue;
            yield return points.ToArray();
            points.Clear();
        }
    }
}

internal record TrafficRouteWindow(CivilianTrafficRoute Route, int Leg, double From, double To)
{
    internal (ChartPosition Start, ChartPosition[] Onward) Sample(Random random)
    {
        ChartPosition start = Route.Points[Leg - 1].Towards(Route.Points[Leg], From + random.NextDouble() * (To - From));
        return (start, Route.Points.Skip(Leg).ToArray());
    }
}
