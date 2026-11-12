using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPower.GameBridge;

// Also compiled into OODA so its player view works without the development bridge enabled.
internal static class PlayerPicture
{
    internal static Taskforce? Side
    {
        get
        {
            if (DM_Sensors._showTruthForPlayer || !Singleton<GameMain>.InstanceExists() || !Singleton<GameMain>.Instance._initialized
                || !Singleton<SceneCreator>.InstanceExists() || !Singleton<ObjectsManager>.InstanceExists()
                || Globals._inMuseum) return null;
            Taskforce side = Globals._playerTaskforce;
            return side != null && !side.IsAI && side._nameInMissionFile == Singleton<SceneCreator>.Instance.PlayerSideName ? side : null;
        }
    }

    internal static ObjectBase[] Units(Taskforce side) => Singleton<ObjectsManager>.Instance._listOfAllUnits
        .Where(unit => unit is not WeaponBase && CanCommand(unit, side)).OrderBy(unit => unit.GetInstanceID()).ToArray();

    internal static Vessel[] Ships(Taskforce side) => Units(side).OfType<Vessel>().OrderBy(ship => ship.getName()).ToArray();

    internal static bool CanCommand(ObjectBase unit, Taskforce side) => unit && unit._taskforce == side && !unit.IsDestroyed
        && unit.IsControllable && unit.AcceptsOrdersFromPlayer;

    internal static string Id(ObjectBase unit) => $"{(unit is Vessel ? "ship" : "unit")}-{unit.GetInstanceID()}";

    internal static JObject Read(Taskforce side, Vessel[] ships)
    {
        IEnumerable<Vehicle> tracks = side.PlottingTable?.Vehicles.AsEnumerable() ?? Enumerable.Empty<Vehicle>();
        JArray contacts = new(tracks.Where(track => track.BaseObject && track.IsVisible() && track.UnitTaskforce.Value != side)
            .Take(100).Select(track => new JObject
            {
                ["id"] = $"track-{track.Id}", ["posture"] = track.CurrentRelationship().ToString(),
                ["identified"] = track.Identified.Value, ["live"] = track.HasLiveTrack,
                ["dormant"] = track.IsDormant.Value, ["sensor"] = track.Type.Value.ToString(),
                ["category"] = track.GetMapType().ToString(), ["observedClass"] = track.Class?.Value,
                ["estimatedPosition"] = track.PositionEstimate is { } estimate ? Point(estimate.Item1) : null
            }));
        return new JObject
        {
            ["scope"] = "player", ["taskforce"] = side._nameInMissionFile,
            ["missionClock"] = Clock(),
            ["rulesOfEngagement"] = side._roe.ToString(), ["objectiveUpdates"] = Objectives(), ["contacts"] = contacts,
            ["ownedUnits"] = new JArray(ships.Select(Describe)),
            ["ownedOtherUnits"] = new JArray(Units(side).Where(unit => unit is not Vessel).Select(unit => new JObject
            {
                ["id"] = Id(unit), ["name"] = unit.DisplayNameShort, ["type"] = unit._type.ToString(),
                ["position"] = Point(unit.getGeoPosition()), ["order"] = unit.CurrentOrderText.Value
            })),
            ["ownedGroups"] = new JArray(ships.Where(CanNavigate).Select(ship => new JObject { ["id"] = Id(ship), ["name"] = ship.getName() }))
        };
    }

    private static JObject Clock()
    {
        JObject clock = new() { ["elapsedSeconds"] = GameTime.missionElapsedTime, ["compression"] = GameTime.TimeCompression };
        if (Singleton<SeaPower.Environment>.InstanceExists())
        {
            SeaPower.Environment environment = Singleton<SeaPower.Environment>.Instance;
            clock["scenarioUtc"] = environment.StartTime.AddSeconds(GameTime.missionElapsedTime);
            clock["scenarioLocal"] = environment.StartTimeLocal.AddSeconds(GameTime.missionElapsedTime);
        }
        return clock;
    }

    internal static JArray Objectives() => Singleton<MissionManager>.InstanceExists() ? new(Singleton<MissionManager>.Instance.Objectives
        .Where(objective => !objective.IsHidden && !string.IsNullOrWhiteSpace(objective.Text))
        .Select(objective => new JObject { ["text"] = objective.Text, ["status"] = objective.Status })) : new();

    internal static bool CanNavigate(Vessel ship) => ship.Formation == null || ship.IsFormationLeader || !ship.FollowingFormation;

    internal static JObject Describe(Vessel ship) => new()
    {
        ["id"] = Id(ship), ["name"] = ship.getName(), ["position"] = Point(ship.getGeoPosition()),
        ["speedKnots"] = Math.Round(ship._velocityInKnots, 1), ["order"] = ship.CurrentOrderText.Value,
        ["weaponsPosture"] = ship._weaponStatus.ToString(),
        ["formationLeaderId"] = ship.Formation?.LeaderStation?.UnitObject is Vessel leader && leader != ship ? Id(leader) : null,
        ["route"] = new JArray(ship.GetAllWaypointsGeoPositions(getRelative: false).Take(12).Select(waypoint => Point(waypoint.wpGP)))
    };

    internal static JObject Inspect(Vessel ship)
    {
        JObject unit = Describe(ship);
        unit["headingDegrees"] = Math.Round(ship.Heading.Value, 1);
        unit["totalWaypoints"] = ship.GetNumberOfWaypoints();
        unit["route"] = new JArray(ship.GetAllWaypointsGeoPositions(getRelative: false).Take(40)
            .Select(waypoint => Point(waypoint.wpGP)));
        return unit;
    }

    internal static JObject Point(GeoPosition position) => new()
    {
        ["latitude"] = Math.Round(position._latitude, 5), ["longitude"] = Math.Round(position._longitude, 5)
    };
}
