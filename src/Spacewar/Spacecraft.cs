namespace Spacewar;

// A physical spacecraft exists independently of its optional native display proxy.
internal class Spacecraft(string name, string role, Orbit orbit)
{
    internal string Name { get; } = name;
    internal string Role { get; } = role;
    internal Orbit Orbit { get; } = orbit;
    internal double SimulationSeconds { get; private set; }
    internal OrbitalPosition Position { get; private set; } = orbit.At(0);

    internal void AdvanceTo(double seconds)
    {
        if (SimulationSeconds == seconds) return;
        Position = Orbit.At(seconds);
        SimulationSeconds = seconds;
    }

    internal OrbitalPosition Predict(double secondsAhead) => Orbit.At(SimulationSeconds + secondsAhead);
}
