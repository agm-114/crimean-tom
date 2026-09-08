using System.Globalization;

namespace CrimeanTom.SeaPower;

internal static class AirlinerRoutes
{
    private static CivilianTrafficRoute[]? routes;
    internal static IReadOnlyList<CivilianTrafficRoute> Routes => routes ??= Build();

    private static CivilianTrafficRoute[] Build()
    {
        // Civil hubs absent from the game's small airport catalog. Coordinates: https://ourairports.com/data/
        Dictionary<string, ChartPosition> airports = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ESSA"] = new(59.64849, 17.928829), ["EKCH"] = new(55.61790, 12.65600),
            ["EFHK"] = new(60.31836, 24.96334), ["HECA"] = new(30.11153, 31.39669),
            ["LPPT"] = new(38.7813, -9.13592), ["LEMD"] = new(40.49341, -3.57225),
            ["WSSS"] = new(1.35019, 103.99400), ["RPLL"] = new(14.5086, 121.0200),
            ["RCTP"] = new(25.0777, 121.2330), ["OMDB"] = new(25.24979, 55.37099),
        };
        string directory = Path.Combine(UnityEngine.Application.streamingAssetsPath, "original", "campaigns");
        foreach (string file in Directory.EnumerateFiles(directory, "*airports*.ini").OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var (code, values) in ScenarioUnitCatalog.ReadSections(file))
            {
                if (!values.TryGetValue("Role", out string role) ||
                    !role.Split(',').Any(value => value.Trim().Equals("Civil", StringComparison.OrdinalIgnoreCase)) ||
                    !values.TryGetValue("Location", out string location)) continue;
                string[] coordinates = location.Split(',');
                if (coordinates.Length >= 2 &&
                    double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) &&
                    double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
                    airports[code] = new(latitude, longitude);
            }
        }
        List<CivilianTrafficRoute> output = [];
        foreach (var origin in airports)
        foreach (var destination in airports)
        {
            if (origin.Key == destination.Key || Math.Abs(origin.Value.Longitude - destination.Value.Longitude) > 180) continue;
            var route = Connect(origin.Key, destination.Key, origin.Value, destination.Value);
            if (route.DistanceNm is >= 80 and <= 6000) output.Add(route);
        }
        return output.ToArray();
    }

    // Great-circle cruise legs, sampled every ~120 NM. No runway units or simulated takeoffs are needed.
    private static CivilianTrafficRoute Connect(string origin, string destination, ChartPosition first, ChartPosition last)
    {
        const double radians = Math.PI / 180;
        double lat1 = first.Latitude * radians, lat2 = last.Latitude * radians;
        double lon1 = first.Longitude * radians, lon2 = last.Longitude * radians;
        double angle = Math.Acos(Math.Clamp(Math.Sin(lat1) * Math.Sin(lat2) +
            Math.Cos(lat1) * Math.Cos(lat2) * Math.Cos(lon2 - lon1), -1, 1));
        double distance = angle * 3440.065;
        int steps = Math.Max(1, (int)Math.Ceiling(distance / 120));
        List<ChartPosition> points = [first];
        for (int index = 1; index < steps; index++)
        {
            double fraction = (double)index / steps;
            double a = Math.Sin((1 - fraction) * angle) / Math.Sin(angle), b = Math.Sin(fraction * angle) / Math.Sin(angle);
            double x = a * Math.Cos(lat1) * Math.Cos(lon1) + b * Math.Cos(lat2) * Math.Cos(lon2);
            double y = a * Math.Cos(lat1) * Math.Sin(lon1) + b * Math.Cos(lat2) * Math.Sin(lon2);
            double z = a * Math.Sin(lat1) + b * Math.Sin(lat2);
            points.Add(new(Math.Atan2(z, Math.Sqrt(x * x + y * y)) / radians, Math.Atan2(y, x) / radians));
        }
        points.Add(last);
        return new(origin, destination, points, distance);
    }
}
