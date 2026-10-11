namespace Spacewar;

// Kilometres, seconds and degrees. Two-body motion about a spherical, rotating Earth.
internal class Orbit
{
    internal const double EarthRadius = 6378.137;
    internal const double Mu = 398600.4418;
    internal const double SiderealDay = 86164.0905;
    internal const double Radians = Math.PI / 180;
    internal double Perigee { get; }
    internal double Apogee { get; }
    internal double Inclination { get; }
    internal double AscendingNode { get; }
    internal double PeriapsisArgument { get; }
    internal double MeanAnomaly { get; }
    internal double Axis { get; }
    internal double Eccentricity { get; }
    internal double Period { get; }

    internal Orbit(double perigee, double apogee, double inclination, double ascendingNode, double periapsisArgument, double meanAnomaly)
    {
        if (new[] { perigee, apogee, inclination, ascendingNode, periapsisArgument, meanAnomaly }.Any(value => !double.IsFinite(value))
            || perigee < 160 || apogee < perigee || apogee > 100000 || inclination < 0 || inclination > 180)
            throw new ArgumentException("Orbit requires 160 <= perigee <= apogee <= 100000 km and inclination 0-180 degrees.");
        Perigee = perigee;
        Apogee = apogee;
        Inclination = inclination;
        AscendingNode = Wrap(ascendingNode);
        PeriapsisArgument = Wrap(periapsisArgument);
        MeanAnomaly = Wrap(meanAnomaly);
        Axis = EarthRadius + (Perigee + Apogee) / 2;
        Eccentricity = (Apogee - Perigee) / (2 * Axis);
        Period = 2 * Math.PI * Math.Sqrt(Axis * Axis * Axis / Mu);
    }

    internal OrbitalPosition At(double seconds)
    {
        double mean = Wrap(MeanAnomaly + 360 * (seconds % Period) / Period) * Radians;
        double eccentricity = Eccentricity;
        // Bisection stays well behaved for the high-eccentricity profiles allowed above.
        double low = 0, high = 2 * Math.PI;
        for (int step = 0; step < 48; step++)
        {
            double middle = (low + high) / 2;
            if (middle - eccentricity * Math.Sin(middle) < mean) low = middle;
            else high = middle;
        }
        double anomaly = (low + high) / 2;
        double x = Axis * (Math.Cos(anomaly) - eccentricity);
        double y = Axis * Math.Sqrt(1 - eccentricity * eccentricity) * Math.Sin(anomaly);
        double argument = PeriapsisArgument * Radians;
        double along = x * Math.Cos(argument) - y * Math.Sin(argument);
        double across = x * Math.Sin(argument) + y * Math.Cos(argument);
        double node = (AscendingNode - 360 * (seconds % SiderealDay) / SiderealDay) * Radians;
        double tilt = Inclination * Radians;
        double earthX = along * Math.Cos(node) - across * Math.Cos(tilt) * Math.Sin(node);
        double earthY = along * Math.Sin(node) + across * Math.Cos(tilt) * Math.Cos(node);
        double earthZ = across * Math.Sin(tilt);
        double radius = Math.Sqrt(x * x + y * y);
        return new(earthX, earthY, earthZ, Math.Sqrt(Mu * (2 / radius - 1 / Axis)));
    }

    internal static double Wrap(double angle) => (angle % 360 + 360) % 360;
}

internal class OrbitalPosition(double x, double y, double z, double speed)
{
    internal double Radius => Math.Sqrt(x * x + y * y + z * z);
    internal double Altitude => Radius - Orbit.EarthRadius;
    internal double Latitude => Math.Asin(Math.Clamp(z / Radius, -1, 1)) / Orbit.Radians;
    internal double Longitude => Math.Atan2(y, x) / Orbit.Radians;
    internal double Speed { get; } = speed;

    // Geometric elevation above a surface observer's horizon, without terrain or refraction.
    internal double Elevation(double latitude, double longitude)
    {
        double lat = latitude * Orbit.Radians, lon = longitude * Orbit.Radians;
        double ux = Math.Cos(lat) * Math.Cos(lon), uy = Math.Cos(lat) * Math.Sin(lon), uz = Math.Sin(lat);
        double dx = x - Orbit.EarthRadius * ux, dy = y - Orbit.EarthRadius * uy, dz = z - Orbit.EarthRadius * uz;
        return Math.Asin(Math.Clamp((dx * ux + dy * uy + dz * uz) / Math.Sqrt(dx * dx + dy * dy + dz * dz), -1, 1)) / Orbit.Radians;
    }
}
