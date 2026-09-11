using HarmonyLib;
using SeaPower;

namespace CrimeanTom.SeaPower;

[HarmonyPatch(typeof(SceneCreator), "LoadCivilianRoutes")]
internal static class CivilianAirTrafficLoadPatch
{
    private static void Postfix(IniHandler ini) => CivilianAirTraffic.RegisterInitialAircraft(ini);
}

internal static class CivilianAirTraffic
{
    private const string InitialAircraftKey = "CrimeanTomInitialAircraft";

    internal static void WriteRoutes(MissionWriter mission, IEnumerable<CivilianTrafficUnit> traffic, int seed)
    {
        Random random = new(seed ^ 0x54696D65);
        int count = int.Parse(mission.Read("Mission", "NumberOfCivilianRoutes", "0"));
        foreach (var unit in traffic.Where(unit => unit.IsAircraft && unit.AirTrafficRoute is { Count: > 1 }))
        {
            mission.Section($"CivilianRoute{++count}");
            mission.Write("Name", unit.Journey);
            mission.Write("Taskforce", "Neutral");
            mission.Write("UnitType", "Aircraft");
            mission.Write("AllowedUnits", unit.UnitType);
            // Native variant filtering is case-sensitive, unlike loadout filtering.
            mission.Write("AllowedNations", "All");
            mission.Write("AllowedLoadouts", "all");
            mission.Write("MaxUnits", 1);
            mission.Write("SpawnInGroupsOf", 1);
            mission.Write("Repeatable", true);
            // The installed loader reads the first value twice when given an interval range.
            mission.Write("SpawnInterval", random.Next(360, 901));
            mission.WriteWaypoints(unit.AirTrafficRoute!);
            mission.Write(InitialAircraftKey, unit.Reference);
        }
        mission.Set("Mission", "NumberOfCivilianRoutes", count);
    }

    internal static void RegisterInitialAircraft(IniHandler ini)
    {
        foreach (var route in Singleton<MissionManager>.Instance._civilianRoutes)
        {
            string reference = ini.readValue(route._section, InitialAircraftKey, string.Empty);
            if (reference.Length == 0 || !Singleton<SceneCreator>.Instance.ObjectsByPvKey.TryGetValue(reference, out var unit) ||
                unit is not Aircraft || unit.IsDestroyed || unit._taskforce != Globals._neutralTaskforce || unit._spawnedByCivRoute) continue;
            // Native routes own replacement, despawn without scoring, and their active-unit limit.
            route.AddActiveUnit(unit);
            route._lastSpawnTime = GameTime.missionElapsedTime;
            route._currentSpawnInterval = route._spawnInterval.x;
        }
    }
}
