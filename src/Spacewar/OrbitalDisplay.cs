namespace Spacewar;

// Physical coordinates remain in Orbit. This projection only positions native representations.
internal static class OrbitalDisplay
{
    internal const double MetresPerUnityUnit = 67.200066;
    internal const double AccurateRadiusKm = 100;
    internal const double BoundRadiusKm = 450;
    // Match the native tile dimensions, but subtract the origin before converting to float.
    internal const double TileSize = 330.71396f;
    internal const double TileDegrees = 0.2f;
    internal const double TileOffset = 1f / 120f;
    internal const double UnitsPerDegree = TileSize / TileDegrees;
    internal static double HeightKm(double altitudeKm) => altitudeKm <= 10
        ? altitudeKm : 10 + 2 * Math.Log(1 + (altitudeKm - 10) / 2);
    internal static double HeightInUnity(double altitudeKm) => HeightKm(altitudeKm) * 1000 / MetresPerUnityUnit;
    internal static double RadiusKm(double distanceKm)
    {
        if (distanceKm <= AccurateRadiusKm) return distanceKm;
        double span = BoundRadiusKm - AccurateRadiusKm;
        double log = Math.Log(1 + (distanceKm - AccurateRadiusKm) / span);
        return AccurateRadiusKm + span * log / (1 + log);
    }

    internal static double WrapLongitude(double degrees) => degrees - 360 * Math.Floor((degrees + 180) / 360);

    internal static (double Latitude, double Longitude, double Scale) Project(double latitude, double longitude, double centerLatitude, double centerLongitude)
    {
        double x = WrapLongitude(longitude - centerLongitude);
        double z = latitude - centerLatitude;
        double distance = Math.Sqrt(x * x + z * z) * UnitsPerDegree * MetresPerUnityUnit / 1000;
        double scale = distance == 0 ? 1 : RadiusKm(distance) / distance;
        return (centerLatitude + z * scale, WrapLongitude(centerLongitude + x * scale), scale);
    }

    internal static (double X, double Z) Local(double latitude, double longitude, double tileX, double tileY)
    {
        double centerLongitude = TileDegrees * (Math.Floor(tileX) + 0.5) + TileOffset - 180;
        double centerLatitude = 90 - TileDegrees * (Math.Floor(tileY) + 0.5) - TileOffset;
        return (WrapLongitude(longitude - centerLongitude) * UnitsPerDegree, (latitude - centerLatitude) * UnitsPerDegree);
    }
}
