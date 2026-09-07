namespace CrimeanTom.SeaPower;

// Native progress, time, or damage conditions release groups without forced targets.
internal static class RouteAmbushTrigger
{
    internal const int MaximumDelaySeconds = 16 * 60;
    internal const float ReleaseDistanceNm = 3;

    internal static void WriteTo(MissionWriter mission, FleetDeployment raiders, RouteEncounter voyage,
        IReadOnlyList<string> convoy, IReadOnlyList<NavalWeaponEnvelope> opposition, int seed, bool playerIsRaider, bool spectator)
    {
        int number = int.Parse(mission.Read("Mission", "NumberOfTriggers", "3"));
        Random random = new(seed ^ 0x414D4255);
        Dictionary<int, int> waiting = [];
        foreach (var (formation, index) in raiders.Formations.Select((formation, index) => (formation, index)))
        {
            var members = raiders.Units.Where(unit => formation.References.Contains(unit.Reference)).ToArray();
            // OOB groups split ownership. Never replace a player's orders or weapon controls.
            if (members.Length == 0 || !spectator && playerIsRaider && members.Any(unit => unit.SourceIndex < raiders.ControlledCount)) continue;
            // Either side being able to fire is enough. Sensor contact alone does not delay a group.
            if (!members.Any(unit => opposition.Any(NavalWeaponEnvelope.From(unit).Overlaps))) continue;
            DeployedFleetUnit leader = members.First(unit => unit.Reference == formation.References[0]);
            if (leader.Waypoints is not { Count: > 0 }) continue;
            int delay = random.Next(10 * 60, MaximumDelaySeconds + 1);
            foreach (DeployedFleetUnit member in members)
            {
                waiting[member.SourceIndex] = delay;
                Hold(member.Reference);
                mission.Set(member.Reference, "Telegraph", 0);
                mission.Set(member.Reference, "Waypoints", string.Empty);
                mission.Set(member.Reference, "MissionType", "NoMission");
            }
            // Waypoint ROE and sensor commands propagate through a FollowLeader formation.
            mission.Set("Mission", $"Taskforce2_Formation{index + 1}", (formation with { ControlMode = "FollowLeader" }).ToMissionValue());
            Release(leader.Reference, leader.Waypoints, leader.Ship.IsSubmarine, leader.Telegraph, formation.References, delay);
        }
        foreach (DeployedAircraft aircraft in raiders.Aircraft.Where(aircraft => waiting.ContainsKey(aircraft.ParentSourceIndex)))
        {
            Hold(aircraft.Reference);
            // Keep aircraft flying their patrol while waiting; remove any early attack/ROE orders.
            mission.Set(aircraft.Reference, "Waypoints", MissionWaypoint.FormatRoute(
                aircraft.Waypoints?.Select(waypoint => waypoint with { Order = null }) ?? []));
            Release(aircraft.Reference, aircraft.Waypoints ?? [], false, 3, [aircraft.Reference], waiting[aircraft.ParentSourceIndex]);
        }
        mission.Set("Mission", "NumberOfTriggers", number);

        void Hold(string reference)
        {
            mission.Set(reference, "OverrideWeaponStatus", "Hold");
            mission.Set(reference, "WeaponStatus", "Hold");
            mission.Set(reference, "RadarsActive", false);
            mission.Set(reference, "ExcludeFromAIRadarRoutine", true);
        }

        void Release(string reference, IReadOnlyList<MissionWaypoint> orders, bool submarine, int telegraph, IReadOnlyList<string> group, int delay)
        {
            mission.Section($"Trigger{++number}");
            mission.Write("Name", $"Release {reference} on convoy progress, time, or damage");
            mission.Write("Condition_Condition1_Type", "Time");
            mission.Write("Condition_Condition1_Time", delay);
            // A hidden route checkpoint measures forward progress without tracking units at runtime.
            mission.Write("Condition_Progress_Type", "UnitsInTheArea");
            mission.WriteList("Condition_Progress_Units", convoy);
            mission.Write("Condition_Progress_MinimumUnits", 1);
            mission.WritePosition("Condition_Progress_PositionNM", voyage.At(ReleaseDistanceNm + 2));
            mission.Write("Condition_Progress_AreaRadiusNM", 2);
            mission.Write("Condition_Progress_AreaDisplaySide", "None");
            mission.Write("Condition_Attacked_Type", "UnitDamageState");
            mission.WriteList("Condition_Attacked_Units", group);
            mission.Write("Condition_Attacked_MinimumUnits", 1);
            mission.Write("Condition_Attacked_DamageConditionMode", "IntegrityBelow");
            mission.Write("Condition_Attacked_DamageThresholdPercent", 99);
            mission.Write("ConditionsCompleted", "<Progress> OR <Condition1> OR <Attacked>");
            mission.Write("Action_Units", reference);
            mission.Write("Action_UnitTelegraph", telegraph);
            // Zero-distance relative waypoint applies orders at the unit's current position.
            // Submarines retain passive sensors and never raise radar masts for this release.
            string activation = "turn,0,0,current/SetWeaponStatus,Free" +
                (submarine ? string.Empty : "/SetSensors,AirSearch_on,SurfaceSearch_on");
            mission.Write("Action_UnitWaypoints", activation + (orders.Count == 0 ? string.Empty : "|" + MissionWaypoint.FormatRoute(orders)));
        }
    }
}
