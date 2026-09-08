namespace CrimeanTom.SeaPower;

// Native triggers only. Tight retains defensive interception; Hold bypasses the normal AI engagement pass.
internal static class ScenarioOpeningOrders
{
    internal const int MaximumDelaySeconds = 16 * 60;

    internal static void WriteTo(MissionWriter mission, FleetDeployment fleet, FleetDeployment opposition,
        int seed, bool playerSide, bool spectator, RouteEncounter? voyage = null,
        IReadOnlyList<string>? convoy = null, IReadOnlyList<NavalWeaponEnvelope>? protectedUnits = null)
    {
        Random random = new(seed ^ (playerSide ? 0x506C6179 : 0x456E656D));
        var enemies = opposition.Units.Select(NavalWeaponEnvelope.From).Concat(protectedUnits ?? []).ToArray();
        List<(DeployedFleetUnit[] Members, int Search, int Attack, float Progress)> groups = [];
        int ordinal = 0;
        foreach (var (formation, index) in fleet.Formations.Select((formation, index) => (formation, index)))
        {
            var members = formation.References.Select(reference => fleet.Units.FirstOrDefault(unit => unit.Reference == reference))
                .Where(unit => unit is not null).Cast<DeployedFleetUnit>().ToArray();
            if (members.Length == 0 || playerSide && !spectator && members.Any(unit => unit.SourceIndex < fleet.ControlledCount)) continue;
            int role = ordinal++ % 3;
            bool exposed = ScenarioDetectionPicture.Exposed(mission, members[0], opposition);
            // Radar contact alone is not a reason to delay a group with no overlapping weapon envelope.
            if (!members.Any(unit => enemies.Any(NavalWeaponEnvelope.From(unit).Overlaps))) continue;
            int search = 120 + role * 75 + random.Next(90);
            int attack = Math.Min(MaximumDelaySeconds, 480 + role * 120 + random.Next(120) + (exposed ? 90 : 0));
            float progress = 3 + ordinal * 2 + (float)random.NextDouble() * 2;
            groups.Add((members, search, attack, progress));
            DeployedFleetUnit leader = members[0];
            if (leader.Waypoints is not { Count: > 0 }) continue;
            string side = leader.Reference.StartsWith("Taskforce1", StringComparison.Ordinal) ? "Taskforce1" : "Taskforce2";
            mission.Set("Mission", $"{side}_Formation{index + 1}", (formation with { ControlMode = "FollowLeader" }).ToMissionValue());
            foreach (var unit in members)
            {
                Defend(mission, unit.Reference);
                mission.Set(unit.Reference, "MissionType", "NoMission");
                mission.Set(unit.Reference, "Telegraph", unit.Telegraph);
                mission.Set(unit.Reference, "Waypoints", string.Empty);
            }
            // Movement starts immediately; only sensors and offensive engagement are staged.
            mission.Set(leader.Reference, "Waypoints", MissionWaypoint.FormatRoute(
                leader.Waypoints.Select(waypoint => waypoint with { Order = null })));
            Release(mission, leader.Reference, members.Select(unit => unit.Reference).ToArray(), leader.Waypoints,
                leader.Ship.IsSubmarine, leader.Telegraph, search, attack, voyage, convoy, progress);
        }
        foreach (DeployedAircraft aircraft in fleet.Aircraft)
        {
            if (playerSide && !spectator && aircraft.ParentSourceIndex < fleet.ControlledCount) continue;
            var parent = groups.FirstOrDefault(group => group.Members.Any(unit => unit.SourceIndex == aircraft.ParentSourceIndex));
            Defend(mission, aircraft.Reference);
            if (aircraft.Waypoints is null)
            {
                // Wingmen inherit their flight leader's sensor and ROE commands without receiving a solo waypoint.
                var formation = fleet.Formations.Select((value, index) => (value, index))
                    .FirstOrDefault(item => item.value.References.Contains(aircraft.Reference));
                if (formation.value is not null)
                {
                    string side = aircraft.Reference.StartsWith("Taskforce1", StringComparison.Ordinal) ? "Taskforce1" : "Taskforce2";
                    mission.Set("Mission", $"{side}_Formation{formation.index + 1}",
                        (formation.value with { ControlMode = "FollowLeader" }).ToMissionValue());
                    continue;
                }
            }
            int search = parent.Members is null ? 180 + random.Next(120) : parent.Search;
            int attack = parent.Members is null ? 540 + random.Next(180) : parent.Attack;
            string[] damageGroup = parent.Members?.Select(unit => unit.Reference).Append(aircraft.Reference).ToArray() ?? [aircraft.Reference];
            Release(mission, aircraft.Reference, damageGroup, aircraft.Waypoints ?? [], false, 3, search, attack,
                voyage, convoy, parent.Members is null ? 6 : parent.Progress, aircraft.StationRole is "CAP" or "AEW");
        }
    }

    internal static void Defend(MissionWriter mission, string reference)
    {
        // The override also prevents the native proximity-alert cascade from switching the whole fleet to Free.
        mission.Set(reference, "OverrideWeaponStatus", "Tight");
        mission.Set(reference, "WeaponStatus", "Tight");
        // Tight permits missile interception, but only if the ship can detect the threat.
        // Do not combine suppressed alert activation with blind surface ships or aircraft.
        mission.Set(reference, "RadarsActive", !reference.Contains("Submarine"));
        mission.Set(reference, "ExcludeFromAIRadarRoutine", true);
    }

    internal static void Release(MissionWriter mission, string reference, IReadOnlyList<string> group,
        IReadOnlyList<MissionWaypoint> orders, bool submarine, int telegraph, int searchSeconds, int attackSeconds,
        RouteEncounter? voyage = null, IReadOnlyList<string>? convoy = null, float progress = 5, bool loop = false)
    {
        int number = int.Parse(mission.Read("Mission", "NumberOfTriggers", "3"));
        string searchName = $"Search {reference}", attackName = $"Commit {reference}";
        WritePhase(++number, searchName, searchSeconds, false, Math.Max(1, progress - 2));
        WritePhase(++number, attackName, attackSeconds, true, progress);
        // Damage can commit a group early. Its later search trigger must not downgrade it to Tight again.
        mission.Write("Action_CompleteTriggers", searchName);
        mission.Set("Mission", "NumberOfTriggers", number);
        mission.Set("CrimeanTom", reference + "Opening", $"search {searchSeconds}s, commit {attackSeconds}s, progress {progress:0.0}nm");

        void WritePhase(int index, string name, int seconds, bool commit, float distance)
        {
            mission.Section($"Trigger{index}");
            mission.Write("Name", name);
            mission.Write("Condition_Time_Type", "Time");
            mission.Write("Condition_Time_Time", seconds);
            string conditions = "<Time>";
            if (voyage is not null && convoy is { Count: > 0 })
            {
                mission.Write("Condition_Progress_Type", "UnitsInTheArea");
                mission.WriteList("Condition_Progress_Units", convoy);
                mission.Write("Condition_Progress_MinimumUnits", 1);
                mission.WritePosition("Condition_Progress_PositionNM", voyage.At(distance + 1));
                mission.Write("Condition_Progress_AreaRadiusNM", 1);
                mission.Write("Condition_Progress_AreaDisplaySide", "None");
                conditions += " OR <Progress>";
            }
            if (commit)
            {
                mission.Write("Condition_Attacked_Type", "UnitDamageState");
                mission.WriteList("Condition_Attacked_Units", group);
                mission.Write("Condition_Attacked_MinimumUnits", 1);
                mission.Write("Condition_Attacked_DamageConditionMode", "IntegrityBelow");
                mission.Write("Condition_Attacked_DamageThresholdPercent", 99);
                conditions += " OR <Attacked>";
            }
            mission.Write("ConditionsCompleted", conditions);
            mission.Write("Action_Units", reference);
            mission.Write("Action_UnitTelegraph", telegraph);
            string activation = $"turn,0,0,current/SetWeaponStatus,{(commit ? "Free" : "Tight")}" +
                (submarine ? string.Empty : "/SetSensors,AirSearch_on,SurfaceSearch_on");
            var onward = commit ? orders : orders.Select(waypoint => waypoint with { Order = null }).ToArray();
            mission.Write("Action_UnitWaypoints", activation + (onward.Count == 0 ? string.Empty : "|" + MissionWaypoint.FormatRoute(onward)) +
                (loop && onward.Count > 0 ? "|Loop" : string.Empty));
        }
    }

}
