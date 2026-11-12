using CrimeanTom.SeaPower;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace OODA;

internal static class DrillTraffic
{
    private const int Seed = 4041;
    private static readonly Func<SceneCreator, string, List<WaypointData>> ReadWaypoints =
        AccessTools.MethodDelegate<Func<SceneCreator, string, List<WaypointData>>>(
            AccessTools.Method(typeof(SceneCreator), "ReadWpData"));

    internal static IReadOnlyList<CivilianTrafficUnit> Write(IniHandler ini, GeoPosition center)
    {
        var chart = new ChartPosition(center._latitude, center._longitude);
        CivilianTrafficRoute[] shipping =
        [
            Route("Lisbon", "Valencia", 660, (-132, -18), (-42, -8), (-14, -1), (3, 1.4f), (20, 2.5f), (48, 2), (134, 8)),
            Route("Tangier", "Almería", 175, (-18, -2), (-8, -0.6f), (7, 0.5f), (20, 1.2f), (45, 0), (130, -2)),
            Route("Marseille", "Casablanca", 845, (135, 14), (47, -1), (19, 1), (1, -0.9f), (-14, -2.4f), (-46, -12), (-133, -37)),
            Route("Oran", "Cádiz", 390, (136, 1), (44, 0.5f), (21, 2), (5, 0.5f), (-15, -1.7f), (-43, -10), (-130, -22)),
        ];
        CivilianTrafficRoute[] air =
        [
            Route("Lisbon", "Palma", 540, (-100, -15), (110, 6)),
            Route("Algiers", "Porto", 655, (100, 12), (-110, -10)),
            Route("Casablanca", "Madrid", 465, (-30, -110), (44, 100)),
            Route("Málaga", "Rabat", 250, (-80, 80), (100, -90)),
        ];
        CivilianTrafficRoute[] atlanticShipping =
        [
            Route("Lisbon", "Casablanca", 310, (-106, 130), (-93, 45), (-82, -25), (-84, -70), (-92, -130)),
            Route("Canary Islands", "Cádiz", 710, (-137, -79), (-111, -51), (-85, -20), (-52, 8), (-38, 23)),
            Route("Dakar", "Porto", 1500, (-95, -135), (-100, -50), (-109, 18), (-113, 130)),
        ];
        CivilianTrafficRoute[] mediterraneanShipping =
        [
            Route("Málaga", "Oran", 245, (57, 33), (81, 18), (107, 1), (137, -14)),
            Route("Algiers", "Gibraltar", 420, (138, 16), (108, 9), (75, 3), (38, 2), (15, 1)),
            Route("Almería", "Melilla", 110, (111, 41), (102, 22), (99, 3), (99, -22)),
        ];
        CivilianTrafficRoute[] outerFlights =
        [
            Route("Lisbon", "Palma", 540, (-175, 37), (175, 22)),
            Route("Algiers", "Porto", 655, (175, -18), (-175, -40)),
            Route("Faro", "Barcelona", 535, (-150, 72), (155, 106)),
            Route("Alicante", "Lisbon", 500, (155, 87), (-155, 102)),
            Route("Casablanca", "Algiers", 550, (-150, -101), (155, -72)),
            Route("Oran", "Rabat", 350, (155, -102), (-155, -77)),
        ];

        List<CivilianTrafficUnit> units = [];
        CivilianTrafficPlanner.AddTraffic(units, Seed, chart, 18, 20, 8, shipping, air);
        AddApproach(-82, -20, 1, atlanticShipping);
        AddApproach(93, 9, 2, mediterraneanShipping);
        CivilianTrafficPlanner.AddTraffic(units, Seed + 3, chart, 24, 0, 5, [], outerFlights, chart.Offset(new(0, 0, 86)));
        CivilianTrafficPlanner.AddTraffic(units, Seed + 4, chart, 24, 0, 5, [], outerFlights, chart.Offset(new(0, 0, -86)));

        var mission = new MissionWriter();
        foreach (CivilianTrafficUnit unit in units) unit.WriteTo(mission, Plugin.DrillSection, loop: IsFishingBoat(unit));
        int aircraft = units.Count(unit => unit.IsAircraft);
        int vessels = units.Count - aircraft;
        mission.Set("Mission", "NumberOfNeutralVessels", vessels);
        mission.Set("Mission", "NumberOfNeutralAircraft", aircraft);
        CivilianAirTraffic.WriteRoutes(mission, units, Seed, minimumSpawnInterval: 45, maximumSpawnInterval: 90);
        ScenarioWildlife.WriteTo(mission, Seed, chart, separationNm: 32, count: 6);
        mission.CopyTo(ini.writeValue);
        Debug.Log($"[OODA] Traffic generated. {vessels} civilian vessels, {aircraft} airliners, {mission.Read("Mission", "NumberOfNeutralBiologics")} biologics.");
        return units;

        void AddApproach(float x, float z, int seedOffset, CivilianTrafficRoute[] offshoreShipping) =>
            CivilianTrafficPlanner.AddTraffic(units, Seed + seedOffset, chart, 32, 12, 6,
                [.. shipping, .. offshoreShipping], outerFlights, chart.Offset(new(x, 0, z)));

        CivilianTrafficRoute Route(string origin, string destination, double distance, params (float X, float Z)[] points) =>
            new(origin, destination, points.Select(point => chart.Offset(new(point.X, 0, point.Z))).ToArray(), distance);
    }

    internal static void RegisterShips(SceneCreator scene, IReadOnlyList<CivilianTrafficUnit> traffic)
    {
        // Sea Power's mission loader only accepts air routes, but CivilianRoute can spawn and track ships.
        var routes = Singleton<MissionManager>.Instance._civilianRoutes;
        int index = routes.Count == 0 ? 0 : routes.Max(route => route._index);
        System.Random random = new(Seed);
        int registered = 0;
        foreach (CivilianTrafficUnit entry in traffic)
        {
            if (entry.IsAircraft || !scene.ObjectsByPvKey.TryGetValue(entry.Reference, out var unit) || unit is not Vessel) continue;

            string waypoints = MissionWaypoint.FormatRoute(entry.Waypoints);
            bool loop = IsFishingBoat(entry);
            var definitions = ReadWaypoints(scene, waypoints + (loop ? "|Loop" : string.Empty));
            if (definitions.Count == 0) continue;
            float interval = random.Next(45, 91);
            GeoPosition start = unit._geoPosition;
            var route = new CivilianRoute
            {
                _index = ++index,
                _section = $"CivilianRoute{index}",
                _name = entry.Journey,
                _spawnedUnitTaskforce = Globals._neutralTaskforce,
                _unitsType = ObjectBase.ObjectType.Vessel,
                _allowedUnits = [entry.UnitType],
                _allowedNations = ["All"],
                _allowedLoadouts = ["all"],
                _initialMaxUnits = 1,
                _maxUnits = 1,
                _repeatable = true,
                _spawnGeoPosition = start,
                _spawnHeading = new Vector3(0, start.HeadingRangeMeters(definitions[0]._geoposition).heading, 0),
                _waypointDefinitions = definitions,
                _waypoints = definitions.Select(waypoint => waypoint._geoposition).ToList(),
                _startTime = GameTime.missionElapsedTime,
                _lastSpawnTime = GameTime.missionElapsedTime,
                _spawnInterval = new Vector2(interval, interval),
                _currentSpawnInterval = interval
            };
            route.AddActiveUnit(unit);
            routes.Add(route);
            registered++;
        }
        Debug.Log($"[OODA] Registered {registered} repeatable civilian ship routes.");
    }

    private static bool IsFishingBoat(CivilianTrafficUnit unit) => unit.UnitType.StartsWith("civ_fv_", StringComparison.Ordinal);
}
