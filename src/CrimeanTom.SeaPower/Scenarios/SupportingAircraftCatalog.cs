namespace CrimeanTom.SeaPower;

internal record SupportingAircraftOption(string Nation, CarrierAirPlan Plan, string Role, int Points);

internal static class SupportingAircraftCatalog
{
    private static SupportingAircraftOption[]? all;
    internal static IReadOnlyList<SupportingAircraftOption> All => all ??= Load();

    private static SupportingAircraftOption[] Load()
    {
        List<SupportingAircraftOption> output = [];
        string directory = Path.Combine(UnityEngine.Application.streamingAssetsPath, "original", "aircraft");
        foreach (var aircraft in TaskForceRosterGenerator.GetAircraftOptions(FactionCatalog.Nations.Select(nation => nation.Id))
            .GroupBy(option => option.UnitType))
        {
            string path = Path.Combine(directory, aircraft.Key + ".ini");
            var data = ScenarioUnitCatalog.ReadSections(path).SelectMany(section => section.Values)
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            if (!data.TryGetValue("UnitType", out string type) || type != "Aircraft" ||
                !data.TryGetValue("AvailableLoadouts", out string available)) continue;
            string[] loadouts = available.Split(',').Select(value => value.Trim()).ToArray();
            foreach (var (role, loadout, points) in new[]
            {
                ("Fighter", loadouts.FirstOrDefault(value => value.StartsWith("AirToAir", StringComparison.OrdinalIgnoreCase)), 3),
                ("Strike", loadouts.FirstOrDefault(value => value.Equals("AntiShip", StringComparison.OrdinalIgnoreCase)), 6),
                ("Recon", loadouts.FirstOrDefault(value => value is "AEW" or "Recon"), 4),
            })
            {
                if (loadout is null) continue;
                foreach (var variant in aircraft)
                    output.Add(new(variant.Nation, new(aircraft.Key, variant.VariantReference, loadout,
                        role == "Strike" ? "AntiSurface" : "Patrol", false, 1, null), role, points));
            }
        }
        return output.ToArray();
    }
}
