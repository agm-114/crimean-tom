using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    private static IEnumerable<NearbyGroundOption> StrikeSites(StrikeVariant variant, string nation)
    {
        if (variant is StrikeVariant.Radar or StrikeVariant.CoastalBattery or StrikeVariant.AirDefense)
            return NearbyGroundCatalog.All.Where(site => MatchesStrike(site, variant));
        string[] types = variant switch
        {
            StrikeVariant.Airfield => ["airfield_small_1"],
            StrikeVariant.Depot => ["tgt_ammo_depot_small", "tgt_fueltanks_small", "tgt_fueltanks_medium"],
            StrikeVariant.ForwardBase => ["airfield_small_1", "tgt_ammo_depot_small", "tgt_fueltanks_small"],
            _ => ["wp_car_ural_command"],
        };
        return types.Where(type => File.Exists(Path.Combine(Application.streamingAssetsPath, "original", "land_units", type + ".ini")))
            .Select(type => new NearbyGroundOption(type, nation, GroundVariant(type, nation), type == "airfield_small_1" ? 12 : 6, 500));
    }

    private static string GroundVariant(string type, string nation)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "original", "land_units", type + "_variants.ini");
        if (!File.Exists(path)) throw new InvalidOperationException($"Missing variants for {type}.");
        var variants = ScenarioUnitCatalog.ReadSections(path).Where(section => section.Section.StartsWith("Variant", StringComparison.OrdinalIgnoreCase)).ToArray();
        return variants.FirstOrDefault(section => section.Values.TryGetValue("Nation", out string owner) && owner.Equals(nation, StringComparison.OrdinalIgnoreCase)).Section
            ?? variants.First().Section;
    }

    private static void WriteObjectiveVariant(MissionWriter mission, FleetClashGenerationOptions options, ObjectiveOperation operation,
        FleetDeployment first, FleetDeployment second, ChartPosition center,
        IReadOnlyList<string> targets, IReadOnlyList<string> essential,
        IReadOnlyList<MissionPosition> patrolStations, int seed)
    {
        int number = int.Parse(mission.Read("Mission", "NumberOfTriggers", "3"));
        bool AI(DeployedFleetUnit unit) => options.Spectator || (options.PlayerTaskforce ?? "Taskforce1") != "Taskforce1" || unit.SourceIndex >= first.ControlledCount;
        void Trigger(string name) { mission.Section($"Trigger{++number}"); mission.Write("Name", name); }
        void AttackText(string text)
        {
            mission.Set("Language_en", "Objective_Attack", text);
            mission.Set("Language_en", "Taskforce1StartMessage", $"OBJECTIVE|{text}|Begin");
        }
        if (operation == ObjectiveOperation.AreaControl)
        {
            if (options.AreaVariant is AreaVariant.SurfaceSweep or AreaVariant.AswSweep)
            {
                mission.Section("Trigger2");
                new MissionUnitCondition(second.Units.Select(unit => unit.Reference).ToArray(), second.Units.Count).WriteTo(mission);
                mission.Write("ConditionsCompleted", "<Condition1>");
                AttackText($"Find and destroy all {second.Units.Count} designated defending combatants");
            }
            if (options.AreaVariant == AreaVariant.FleetInBeing)
            {
                // The flagship must visit every station in order. Independent ships cannot complete separate legs at once.
                var flagship = first.Units[0];
                for (int index = 0; index < 3; index++)
                {
                    string name = $"Patrol station {index + 1}";
                    Trigger(name); AreaCondition(mission, "Arrived", essential, patrolStations[index], 4);
                    if (index > 0) CompletedCondition(mission, "Previous", $"Patrol station {index}");
                    mission.Write("ConditionsCompleted", index == 0 ? "<Arrived>" : "<Arrived> AND <Previous>");
                    AddChartMarker(mission, center, name.ToUpperInvariant(), patrolStations[index], 4);
                }
                mission.Section("Trigger2"); CompletedCondition(mission, "Patrolled", "Patrol station 3");
                mission.Write("ConditionsCompleted", "<Patrolled>");
                mission.Set(flagship.Reference, "MissionType", "NoMission");
                AttackText("Preserve the Essential flagship and visit patrol stations 1, 2 and 3 in order");
            }
            if (options.AreaVariant == AreaVariant.Relief)
            {
                var isolated = first.Units[0];
                MissionPosition start = new(isolated.X, 0, isolated.Z);
                MissionPosition exit = SafeWithdrawal(center, first.Units, new(-38, 0, 0), seed);
                mission.Set(isolated.Reference, "Damage", "Medium");
                mission.Set(isolated.Reference, "MissionType", "NoMission");
                string[] rescuers = first.Units.Skip(1).Select(unit => unit.Reference).ToArray();
                if (rescuers.Length == 0) throw new InvalidOperationException("Relief needs a separate rescue group.");
                Trigger("Relief joins isolated group");
                AreaCondition(mission, "Rescuer", rescuers, start, 4);
                AreaCondition(mission, "Survivor", essential, start, 4);
                mission.Write("ConditionsCompleted", "<Rescuer> AND <Survivor>");
                mission.WriteList("Action_Units", first.Units.Where(AI).Select(unit => unit.Reference));
                mission.Write("Action_UnitWaypoints", MissionWaypoint.FormatRoute([new(exit)]));
                mission.Write("Action_UnitTelegraph", 2);
                mission.Section("Trigger2");
                AreaCondition(mission, "Escaped", essential, exit, 6);
                CompletedCondition(mission, "Relieved", "Relief joins isolated group");
                mission.Write("ConditionsCompleted", "<Escaped> AND <Relieved>");
                AddChartMarker(mission, center, "ISOLATED GROUP", start, 4);
                AddChartMarker(mission, center, "WITHDRAWAL", exit, 6);
                AttackText("Join the isolated Essential group, then escort it to WITHDRAWAL. Its loss fails the mission");
            }
        }
        if (operation == ObjectiveOperation.Passage)
        {
            if (options.PassageVariant == PassageVariant.Rearguard)
            {
                mission.Set(essential[0], "Damage", "Medium");
                AttackText("Cover the damaged Essential group until it reaches EXIT. Its loss fails the mission");
            }
            if (options.PassageVariant == PassageVariant.BlockadeRun)
                first.Units.Where(AI).ForEach(unit => mission.Set(unit.Reference, "Telegraph", 4));
        }
        if (operation == ObjectiveOperation.FixedTarget)
        {
            if (options.StrikeVariant is StrikeVariant.PortRaid or StrikeVariant.RearmingShips or StrikeVariant.RepairingShips)
            {
                var moored = second.Units.Where(unit => targets.Contains(unit.Reference)).ToArray();
                MissionPosition exit = SafeWithdrawal(center, moored, new(moored[0].X, 0, moored[0].Z + 40), seed);
                bool repairing = options.StrikeVariant == StrikeVariant.RepairingShips;
                foreach (var unit in moored)
                {
                    mission.Set(unit.Reference, "Telegraph", 0); mission.Set(unit.Reference, "Waypoints", "");
                    mission.Set(unit.Reference, "MissionType", "NoMission");
                    mission.Set(unit.Reference, "RadarsActive", true); mission.Set(unit.Reference, "WeaponStatus", "Free");
                    if (repairing) mission.Set(unit.Reference, "Damage", "Medium");
                    if (options.StrikeVariant == StrikeVariant.RearmingShips) DepleteStrikeWeapons(mission, unit);
                }
                // Servicing is interrupted by an attack, not accelerated by it. Both paths send survivors out of the anchorage.
                foreach (bool attacked in new[] { false, true })
                {
                    string name = attacked ? "Anchorage attacked" : "Anchorage ready";
                    Trigger(name);
                    if (attacked)
                    {
                        AreaCondition(mission, "Approach", first.Units.Select(unit => unit.Reference), new(moored[0].X, 0, moored[0].Z), 12);
                        mission.Write("Condition_Hit_Type", "UnitDamageState"); mission.WriteList("Condition_Hit_Units", targets);
                        mission.Write("Condition_Hit_MinimumUnits", 1); mission.Write("Condition_Hit_DamageConditionMode", "IntegrityBelow");
                        mission.Write("Condition_Hit_DamageThresholdPercent", repairing ? 35 : 99);
                        mission.Write("ConditionsCompleted", "<Approach> OR <Hit>");
                    }
                    else
                    {
                        mission.Write("Condition_Ready_Type", "Time"); mission.Write("Condition_Ready_Time", repairing ? 1800 : 900);
                        mission.Write("ConditionsCompleted", "<Ready>");
                        mission.Write("Action_RepairUnits", repairing);
                        mission.Write("Action_RearmUnits", options.StrikeVariant == StrikeVariant.RearmingShips);
                    }
                    mission.Write("Action_CompleteTriggers", attacked ? "Anchorage ready" : "Anchorage attacked");
                    mission.WriteList("Action_Units", targets); mission.Write("Action_UnitTelegraph", 2);
                    mission.Write("Action_UnitWaypoints", MissionWaypoint.FormatRoute([new(exit)]));
                }
                Trigger("Anchored target escapes");
                new MissionUnitCondition(targets, 1, new(exit, 6, "None")).WriteTo(mission);
                mission.Write("ConditionsCompleted", "<Condition1>");
                new MissionOutcome("Taskforce2", "Taskforce2VictoryMessage", "Taskforce1DefeatMessage", ["Defend"], ["Attack"]).WriteTo(mission);
                moored.ForEach((unit, index) => AddChartMarker(mission, center, $"ANCHORED TARGET {index + 1}", new(unit.X, 0, unit.Z), 1));
                AddChartMarker(mission, center, "TARGET ESCAPE", exit, 6);
                AttackText($"Destroy all {targets.Count} anchored targets before any reaches TARGET ESCAPE");
                mission.Set("Language_en", "Objective_Defend", "Preserve an anchored target and escape, or destroy the essential attacking groups");
            }
            if (options.StrikeVariant == StrikeVariant.CableTerminal)
                AttackText("Destroy the seabed network's shore terminal. The target is the land communications vehicle, not an underwater cable");
        }
        mission.Set("Mission", "NumberOfTriggers", number);
    }

    private static MissionPosition SafeWithdrawal(ChartPosition center, IEnumerable<DeployedFleetUnit> units, MissionPosition preferred, int seed)
    {
        System.Random random = new(seed ^ 0x45584954);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var point = attempt == 0 ? preferred : new MissionPosition((float)random.NextDouble() * 80 - 40, 0, (float)random.NextDouble() * 80 - 40);
            if (units.All(unit => Math.Pow(point.X - unit.X, 2) + Math.Pow(point.Z - unit.Z, 2) > 400 &&
                ScenarioTerrain.IsNavigable(center.Offset(new(unit.X, 0, unit.Z)), center.Offset(point), unit.Ship.IsSubmarine ? 80 : 20))) return point;
        }
        throw new UnsafeNavalSpawnException("No safe withdrawal corridor near this objective. Reroll or choose another region.");
    }
}
