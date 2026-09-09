using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    // Support hulls need not be combatants in the campaign purchase roster.
    private static FleetUnitArchetype[] SupportHullPool(FleetSidePlan side, IEnumerable<string> types, ScenarioSpecialMode mode)
    {
        var nations = side.RegionalNations.Concat(side.AlliedNations).ToArray();
        List<FleetUnitArchetype> output = [];
        foreach (string type in types.Where(type => UnitTypeExists(type) && mode.Allows(type)))
        {
            string path = Path.Combine(Application.streamingAssetsPath, "original", "vessels", type + "_variants.ini");
            if (!File.Exists(path)) continue;
            string fallback = "";
            foreach (var section in ScenarioUnitCatalog.ReadSections(path))
            {
                if (section.Section == "Default" && section.Values.TryGetValue("Nation", out string nation)) fallback = nation;
                if (!section.Section.StartsWith("Variant", StringComparison.OrdinalIgnoreCase)) continue;
                string owner = section.Values.TryGetValue("Nation", out string selected) ? selected : fallback;
                if (!nations.Contains(owner, StringComparer.OrdinalIgnoreCase)) continue;
                var profile = VesselCombatProfileReader.Read(type);
                output.Add(new(type, owner, [section.Section], side.RegionalNations.Contains(owner),
                    profile?.ApproximatePointCost ?? 10, profile?.SurfaceCapability ?? 0, profile?.AirDefenseCapability ?? 0,
                    profile?.AswCapability ?? 0, profile?.Resilience ?? 1, type.Contains("_ssbn_"), false));
            }
        }
        return output.ToArray();
    }

    private static void AddChartMarker(MissionWriter mission, ChartPosition center, string label, MissionPosition position, float radius)
    {
        int number = int.Parse(mission.Read("MapSymbols", "NumberOfSymbols", "0")) + 1;
        string key = "VariantChart" + number;
        mission.Set("MapSymbols", "NumberOfSymbols", number);
        mission.Set("MapSymbols", "Symbol" + number, key);
        mission.Set("Language_en", key + "Label_en", label);
        mission.Section(key); mission.Write("Kind", radius == 0 ? "Label" : "Circle"); mission.Write("LabelKey", key + "Label");
        mission.Write("GeoPoint", center.Offset(position).ToMissionValue()); mission.Write("RadiusNm", radius);
        mission.Write("Color", "Red"); mission.Write("BorderColor", "Red"); mission.Write("FillOpacity", 0.03);
        mission.Write("VisibleIn", "CampaignMap,BriefingMap,Mission");
    }

    private static void AreaCondition(MissionWriter mission, string key, IEnumerable<string> units, MissionPosition point, float radius, int minimum = 1)
    {
        mission.Write($"Condition_{key}_Type", "UnitsInTheArea");
        mission.WriteList($"Condition_{key}_Units", units);
        mission.Write($"Condition_{key}_MinimumUnits", minimum);
        mission.WritePosition($"Condition_{key}_PositionNM", point);
        mission.Write($"Condition_{key}_AreaRadiusNM", radius);
        mission.Write($"Condition_{key}_AreaDisplaySide", "None");
    }

    private static void CompletedCondition(MissionWriter mission, string key, string trigger)
    {
        mission.Write($"Condition_{key}_Type", "TriggerCompleted");
        mission.Write($"Condition_{key}_Triggers", trigger);
    }

    private static void DepleteStrikeWeapons(MissionWriter mission, DeployedFleetUnit unit)
    {
        string root = Path.Combine(Application.streamingAssetsPath, "original");
        foreach (var section in ScenarioUnitCatalog.ReadSections(Path.Combine(root, "vessels", unit.Ship.UnitType + ".ini"))
            .Where(section => section.Section.StartsWith("WeaponSystem", StringComparison.OrdinalIgnoreCase)))
        {
            if (!section.Values.TryGetValue("Ammunition", out string ammunition)) continue;
            string file = Path.Combine(root, "ammunition", ammunition + ".ini");
            if (!File.Exists(file)) continue;
            var general = ScenarioUnitCatalog.ReadSections(file).FirstOrDefault(item => item.Section == "General").Values;
            if (general is null || !general.TryGetValue("TargetType", out string targets) || !targets.Contains("ASuW") || targets.Contains("AAW")) continue;
            mission.Set(unit.Reference + "_" + section.Section, "InternalAmmoCount", 0);
            mission.Set(unit.Reference + "_" + section.Section, "LoadedAmmunitions", ammunition + ",0");
        }
    }

    private static void AppendHuntDecoys(MissionWriter mission, FleetClashMatch match, ChartPosition center,
        MissionPosition report, MissionPosition escape, System.Random random, string player)
    {
        var pool = FleetClashForceGenerator.BuildPool(new(match.SecondName, ScenarioActivityPlan.NationsFor(match, "Taskforce2"), []), match.SpecialMode)
            .Where(unit => !unit.IsSubmarine && !unit.IsCarrier && !match.Second.Ships.Any(target => target.UnitType == unit.UnitType))
            .OrderBy(unit => unit.PointCost).Take(8).ToArray();
        if (pool.Length == 0) throw new InvalidOperationException("No decoy patrol craft available.");
        int added = 0;
        for (int attempt = 0; attempt < 40 && added < 2; attempt++)
        {
            float sign = added == 0 ? -1 : 1;
            MissionPosition start = new(report.X + (float)random.NextDouble() * 12, 0, report.Z + sign * (22 + attempt));
            MissionPosition end = new(escape.X, 0, escape.Z + sign * 22);
            if (!ScenarioTerrain.IsNavigable(center.Offset(start), center.Offset(end), 20)) continue;
            var ship = pool[random.Next(pool.Length)];
            int number = int.Parse(mission.Read("Mission", "NumberOfTaskforce2Vessels", "0")) + 1;
            mission.Set("Mission", "NumberOfTaskforce2Vessels", number);
            string reference = $"Taskforce2Vessel{number}";
            if (player == "Taskforce2") mission.Set("CrimeanTom", "AlliedAIUnits", (mission.Read("CrimeanTom", "AlliedAIUnits") + "," + reference).Trim(','));
            mission.Section(reference); mission.Write("TaskForceModeIgnoreUnit", true);
            mission.Write("Type", ship.UnitType); mission.Write("Nation", ship.Nation); mission.Write("VariantReference", ship.VariantReferences[0]);
            mission.WritePosition("RelativePositionInNM", start); mission.Write("Telegraph", 3); mission.Write("WeaponStatus", "Free");
            mission.Write("RadarsActive", true); mission.Write("MissionType", "NoMission"); mission.WriteWaypoints([new(end)]);
            AddChartMarker(mission, center, $"UNCONFIRMED REPORT {++added}", start, 18);
        }
        if (added == 0) throw new UnsafeNavalSpawnException("No safe lanes for decoy patrols. Choose open water.");
        string classes = string.Join(", ", match.Second.Ships.Select(ship => ship.UnitType).Distinct());
        mission.Set("Language_en", "Taskforce1StartMessage", $"HUNT|Target classes: {classes}. Reports include decoy patrols of other classes. Classify contacts before attacking. Decoys do not count toward victory.|Begin search");
        mission.Set("Language_en", "Description", mission.Read("Language_en", "Description") + $"\nTarget classes: {classes}. Other patrol classes are decoys.");
        mission.Set("CrimeanTom", "DecoyPatrols", added);
    }
}
