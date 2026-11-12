namespace UNREP;

internal static class ReserveEstimates
{
    internal static (double Base, double PerTonne)? For(string prefix, double displacement, string roles, string power)
    {
        if (!double.IsFinite(displacement) || displacement < 1000) return null;
        if (prefix is "Water" or "WaterCapacity") return Load(prefix, 15_000, 10);
        if (prefix is not ("Fuel" or "FuelCapacity" or "AviationFuel" or "AviationFuelCapacity")) return null;

        var tags = roles.Split(',');
        bool HasRole(string role) => tags.Any(tag => tag.Trim().Equals(role, StringComparison.OrdinalIgnoreCase));
        bool carrier = HasRole("Carrier") || HasRole("CVS");
        bool fleetCarrier = carrier && displacement >= 50_000;
        if (prefix is "AviationFuel" or "AviationFuelCapacity")
        {
            if (fleetCarrier) return Load(prefix, 200_000, 85);
            if (carrier) return Load(prefix, 25_000, 28);
            if (HasRole("Landing")) return Load(prefix, 20_000, 5);
        }
        if (fleetCarrier && power.Trim().ToUpperInvariant() is "STEAM" or "DIESEL" or "GASTURBINE")
            return Load(prefix, 250_000, 90);
        return null;
    }

    private static (double Base, double PerTonne) Load(string prefix, double capacityBase, double capacityPerTonne) =>
        prefix.EndsWith("Capacity", StringComparison.Ordinal) ? (capacityBase, capacityPerTonne)
        : (capacityBase / 2, capacityPerTonne / 2);
}
