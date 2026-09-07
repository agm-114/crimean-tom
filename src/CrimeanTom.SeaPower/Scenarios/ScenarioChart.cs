namespace CrimeanTom.SeaPower;

internal static class ScenarioChart
{
    internal static void WriteFleetClash(MissionWriter mission, FleetClashMatch match, int seed, float separation,
        FleetDeployment friendly, FleetDeployment enemy)
    {
        ChartPosition center = new(match.Latitude, match.Longitude);
        Random random = new(seed ^ 0x4D6170);
        double longitudeScale = Math.Cos(center.Latitude * Math.PI / 180);
        var contacts = enemy.Units.Select(unit => new MissionPosition(unit.X, 0, unit.Z)).ToArray();
        if (contacts.Length == 0) return;
        MissionPosition report = contacts[random.Next(contacts.Length)];
        List<MissionPosition> sectors = [new((float)Math.Round(report.X / 16) * 16, 0,
            (float)Math.Round(report.Z / 16) * 16)];
        for (int attempt = 0; sectors.Count < 3 && attempt < 100; attempt++)
        {
            double angle = random.NextDouble() * Math.PI * 2;
            double distance = 36 + random.NextDouble() * Math.Max(24, separation * 0.3);
            MissionPosition candidate = new(report.X + (float)(Math.Cos(angle) * distance / longitudeScale), 0,
                report.Z + (float)(Math.Sin(angle) * distance));
            if (sectors.Any(sector => Math.Pow((sector.X - candidate.X) * longitudeScale, 2) +
                    Math.Pow(sector.Z - candidate.Z, 2) < 32 * 32) || ScenarioTerrain.HeightMeters(center.Offset(candidate)) > -20) continue;
            sectors.Add(candidate);
        }
        sectors = sectors.OrderBy(_ => random.Next()).ToList();
        mission.Set("Language_en", "MissionBriefingGenerateMapFromSymbols", true);
        mission.Section("MapSymbols");
        mission.Write("NumberOfSymbols", sectors.Count + 1);
        for (int index = 0; index <= sectors.Count; index++) mission.Write($"Symbol{index + 1}", $"ClashChart{index}");
        for (int index = 0; index <= sectors.Count; index++)
        {
            bool own = index == sectors.Count;
            MissionPosition position = own ? new(friendly.Units[0].X, 0, friendly.Units[0].Z) : sectors[index];
            mission.Set("Language_en", $"ClashChart{index}Label_en", own ? "FRIENDLY FORCE" : $"Search {(char)('A' + index)}");
            mission.Section($"ClashChart{index}");
            mission.Write("Kind", own ? "Label" : "Circle");
            mission.Write("LabelKey", $"ClashChart{index}Label");
            mission.Write("GeoPoint", center.Offset(position).ToMissionValue());
            mission.Write("Color", own ? "Blue" : "Red");
            mission.Write("BorderColor", own ? "Blue" : "Red");
            mission.Write("RadiusNm", 12);
            mission.Write("BorderWidth", 1.5);
            mission.Write("FillOpacity", 0.04);
            mission.Write("BorderOpacity", 0.6);
            mission.Write("FillStyle", "Fill");
            mission.Write("FontSize", 12);
            mission.Write("VisibleIn", "CampaignMap,BriefingMap,Mission");
        }
        WriteViewport(mission, center, sectors, friendly.Units.Select(unit => center.Offset(new(unit.X, 0, unit.Z))), 12);
    }

    internal static void WriteViewport(MissionWriter mission, ChartPosition center, IEnumerable<MissionPosition> sectors,
        IEnumerable<ChartPosition> route, float radius)
    {
        var bounds = route.Concat(sectors.SelectMany(sector =>
        {
            ChartPosition point = center.Offset(sector);
            double longitudeRadius = radius / (60 * Math.Cos(point.Latitude * Math.PI / 180));
            return new[] { new ChartPosition(point.Latitude - radius / 60, point.Longitude - longitudeRadius),
                new ChartPosition(point.Latitude + radius / 60, point.Longitude + longitudeRadius) };
        })).ToArray();
        double Mercator(double latitude) => Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360)) / (2 * Math.PI);
        double minX = bounds.Min(point => point.Longitude / 360), maxX = bounds.Max(point => point.Longitude / 360);
        double minY = bounds.Min(point => Mercator(point.Latitude)), maxY = bounds.Max(point => Mercator(point.Latitude));
        double latitude = (Math.Atan(Math.Exp((minY + maxY) * Math.PI)) * 2 - Math.PI / 2) * 180 / Math.PI;
        double zoom = Math.Log(500 / (256 * Math.Max(0.001, Math.Max(maxX - minX, maxY - minY)) * 1.25), 2);
        mission.Set("Language_en", "MissionBriefingMapInitialLatitude", latitude);
        mission.Set("Language_en", "MissionBriefingMapInitialLongitude", (minX + maxX) * 180);
        mission.Set("Language_en", "MissionBriefingMapInitialZoom", zoom);
    }
}
