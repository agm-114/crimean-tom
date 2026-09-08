namespace CrimeanTom.SeaPower;

internal static class ScenarioWildlife
{
    private static readonly string[] Whales = ["bio_fin_whale", "bio_humpback_whale", "bio_blue_whale"];

    internal static void WriteTo(MissionWriter mission, int seed, ChartPosition center, float separationNm)
    {
        Random random = new(seed ^ 0x5768616C);
        List<ChartPosition> placed = [];
        for (int attempt = 0; attempt < 80 && placed.Count < 2; attempt++)
        {
            MissionPosition position = new((float)((random.NextDouble() - 0.5) * Math.Max(30, separationNm)),
                -100, (float)((random.NextDouble() - 0.5) * 48));
            ChartPosition location = center.Offset(position);
            if (!ScenarioTerrain.HasSpawnClearance(location, 80) || placed.Any(other => other.DistanceTo(location) < 4)) continue;
            placed.Add(location);
            mission.Section($"NeutralBiologic{placed.Count}");
            mission.Write("Type", Whales[random.Next(Whales.Length)]);
            mission.Write("VariantReference", "Default");
            mission.Write("CrewSkill", "Trained");
            mission.WritePosition("RelativePositionInNM", position);
            mission.Write("Telegraph", 2);
            mission.Write("AutomateRoute", true);
            mission.Write("Heading", random.Next(360));
        }
        mission.Set("Mission", "NumberOfNeutralBiologics", placed.Count);
    }
}
