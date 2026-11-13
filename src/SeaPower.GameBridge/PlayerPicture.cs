using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeapowerUI;

namespace SeaPower.GameBridge;

// Also compiled into OODA so its player view works without the development bridge enabled.
internal static class PlayerPicture
{
    internal record SurfaceMotion(double Course, double Speed, string Source)
    {
        internal GeoPosition Project(GeoPosition fix, double hours) => fix.GeoOffsetByDistanceOnHeading((float)(Speed * hours), (float)Course);
        internal JObject Read() => new() { ["courseDegrees"] = Math.Round(Course, 1), ["speedKnots"] = Math.Round(Speed, 1), ["source"] = Source };
    }

    private class TrackSample(GeoPosition point, double time)
    {
        internal GeoPosition Point = point;
        internal double Time = time;
        internal SurfaceMotion? Motion;
        internal double LastSeen = time;
    }

    private static readonly ConditionalWeakTable<Vehicle, TrackSample> MotionSamples = new();

    internal static void SampleSurfaceTracks(Taskforce side)
    {
        foreach (Vehicle track in side.PlottingTable?.Vehicles.AsEnumerable() ?? [])
        {
            if (!IsLive(track)) MotionSamples.Remove(track);
            else if (track.GetMapType() == MapVisualType.Surface && track.UnitTaskforce.Value != side) ObservedSurfaceMotion(track);
        }
    }

    internal static SurfaceMotion? ObservedSurfaceMotion(Vehicle track)
    {
        if (!IsLive(track) || track.GetMapType() != MapVisualType.Surface || track.PositionEstimate?.Item1 is not { } fix) return null;
        double now = GameTime.missionElapsedTime;
        TrackSample sample = MotionSamples.GetValue(track, _ => new(fix, now));
        double elapsed = now - sample.Time;
        if (now - sample.LastSeen > 120)
        {
            sample.Point = fix;
            sample.Time = now;
            sample.Motion = null;
            elapsed = 0;
        }
        sample.LastSeen = now;
        if (elapsed is >= 60 and <= 240)
        {
            double speed = GeoPosition.HaversineDistanceNmi(sample.Point, fix) * 3600 / elapsed;
            sample.Motion = speed is >= 3 and <= 65 ? new(Course(sample.Point, fix), speed, "successive plotted fixes") : null;
        }
        if (elapsed >= 60 || elapsed < 0)
        {
            if (elapsed is < 0 or > 240) sample.Motion = null;
            sample.Point = fix;
            sample.Time = now;
        }
        if (IsVisual(track) && track.BaseObject is Vessel vessel)
            return new(vessel.Heading.Value, Math.Abs(vessel._velocityInKnots), "visual observation");
        return sample.Motion;
    }

    private static double Course(GeoPosition from, GeoPosition to)
    {
        double degrees = Math.PI / 180;
        double lon = Math.IEEERemainder(to._longitude - from._longitude, 360) * degrees;
        double first = from._latitude * degrees, second = to._latitude * degrees;
        double east = Math.Sin(lon) * Math.Cos(second);
        double north = Math.Cos(first) * Math.Sin(second) - Math.Sin(first) * Math.Cos(second) * Math.Cos(lon);
        return (Math.Atan2(east, north) / degrees + 360) % 360;
    }

    internal static (double Hours, GeoPosition Target)? Intercept(GeoPosition origin, GeoPosition fix, SurfaceMotion? motion,
        double ownSpeed, double standoffNm, double horizonHours)
    {
        double range = GeoPosition.HaversineDistanceNmi(origin, fix);
        if (range <= standoffNm) return (0, fix);
        if (!double.IsFinite(ownSpeed) || ownSpeed <= 0.5) return null;
        if (motion == null || motion.Speed < 0.5)
        {
            double hours = (range - standoffNm) / ownSpeed;
            return hours <= horizonHours ? (hours, fix) : null;
        }

        // Solve |relativePosition + targetVelocity * t| = standoff + ownSpeed * t in the local chart plane.
        double bearing = Course(origin, fix) * Math.PI / 180, heading = motion.Course * Math.PI / 180;
        double dot = range * motion.Speed * Math.Cos(bearing - heading);
        double a = motion.Speed * motion.Speed - ownSpeed * ownSpeed;
        double b = 2 * (dot - standoffNm * ownSpeed), c = range * range - standoffNm * standoffNm;
        double discriminant = b * b - 4 * a * c;
        if (discriminant < 0 || b >= 0 && a >= 0) return null;
        double denominator = -b + Math.Sqrt(discriminant);
        if (denominator <= 0) return null;
        double intercept = 2 * c / denominator;
        return intercept <= horizonHours ? (intercept, motion.Project(fix, intercept)) : null;
    }

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
    internal static string Id(Vehicle track) => $"track-{track.Id}";
    internal static bool IsLive(Vehicle track) => track.BaseObject && !track.BaseObject.IsDestroyed && track.HasLiveTrack && track.IsVisible();
    internal static bool IsVisual(Vehicle track) => IsLive(track) && track.IsObservedByVisual;
    internal static bool ObservesEmission(Vehicle track) => (track.DetectingSensors & SensorSystem.SensorTypeSet.ESM) != 0;
    internal static string ContactLabel(Vehicle track)
    {
        if (track.HasReportedIdentity && track.BaseObject.DisplayNameShort is { Length: > 0 } name) return name;
        if (track.Identified.Value && track.BaseObject.ClassNameShort is { Length: > 0 } className) return className;
        return track.Class?.Value ?? Id(track);
    }
    internal static Vehicle? LiveContact(Taskforce side, string id) => side.PlottingTable?.Vehicles
        .FirstOrDefault(track => Id(track) == id && IsLive(track));

    internal static JObject Read(Taskforce side, Vessel[] ships)
    {
        IEnumerable<Vehicle> tracks = side.PlottingTable?.Vehicles.AsEnumerable() ?? Enumerable.Empty<Vehicle>();
        JArray contacts = new(tracks.Where(track => track.BaseObject && track.IsVisible() && track.UnitTaskforce.Value != side)
            .Take(100).Select(track => new JObject
            {
                ["id"] = Id(track), ["posture"] = track.CurrentRelationship().ToString(),
                ["identified"] = track.Identified.Value, ["live"] = track.HasLiveTrack,
                ["dormant"] = track.IsDormant.Value, ["sensor"] = track.Type.Value.ToString(),
                ["category"] = track.GetMapType().ToString(), ["observedClass"] = track.Class?.Value,
                ["displayLabel"] = ContactLabel(track), ["visuallyObserved"] = IsVisual(track),
                ["visualHeadingDegrees"] = IsVisual(track) ? Math.Round(track.BaseObject.Heading.Value, 1) : null,
                ["surfaceMotion"] = ObservedSurfaceMotion(track)?.Read(),
                ["estimatedPosition"] = track.PositionEstimate is { } estimate ? Point(estimate.Item1) : null
            }));
        return new JObject
        {
            ["scope"] = "player", ["taskforce"] = side._nameInMissionFile,
            ["missionClock"] = Clock(),
            ["rulesOfEngagement"] = side._roe.ToString(), ["objectiveUpdates"] = Objectives(), ["contacts"] = contacts,
            ["observedGunfire"] = PlayerCombat.Snapshot(),
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
        ["engagedContacts"] = new JArray(ship._currentEngageTasks
            .Select(task => task._targetObject?.GetMapVehicle(ship._taskforce)).OfType<Vehicle>().Where(IsLive).Select(Id).Distinct()),
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
