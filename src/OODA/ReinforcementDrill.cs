using CrimeanTom.SeaPower;
using SeaPower;
using UnityEngine;

namespace OODA;

internal class ReinforcementDrill(SceneCreator scene, Taskforce blueTaskforce, Taskforce redTaskforce,
    ReinforcementSpawns spawns, IReadOnlyList<CivilianTrafficUnit> traffic)
{
    private const int WaveIntervalSeconds = 120;
    private const int WaveCount = 6;
    private readonly Side blue = new(blueTaskforce, -1);
    private readonly Side red = new(redTaskforce, 1);
    private readonly Dictionary<ObjectBase, Side> alive = [];
    private int wave;
    private double? startedAt;

    internal void RegisterCivilianTraffic(IniHandler ini)
    {
        CivilianAirTraffic.RegisterInitialAircraft(ini);
        DrillTraffic.RegisterShips(scene, traffic);
    }

    private void Start(IEnumerable<ObjectBase> units)
    {
        foreach (ObjectBase unit in units)
        {
            if (unit.IsDestroyed) continue;
            if (unit._taskforce == blue.Taskforce) Add(unit, blue);
            else if (unit._taskforce == red.Taskforce) Add(unit, red);
        }
        startedAt = GameTime.missionElapsedTime;
        FindStartingCarrier(blue);
        FindStartingCarrier(red);
        LogScore("Drill started");
    }

    private void FindStartingCarrier(Side side)
    {
        if (scene.ObjectsByPvKey.TryGetValue($"{side.Taskforce._nameInMissionFile}Vessel2", out ObjectBase unit))
            RegisterCarrier(side, unit);
    }

    internal void Update()
    {
        if (startedAt is not double start)
        {
            if (scene.IsLoadingDone) Start(Singleton<ObjectsManager>.Instance._listOfAllUnits);
            return;
        }
        if (GameTime.IsPaused()
            || GameTime.missionElapsedTime - start < (wave + 1) * WaveIntervalSeconds)
            return;

        int blueCount = blue.Losses > red.Losses ? 2 : 1;
        int redCount = red.Losses > blue.Losses ? 2 : 1;
        wave++;
        if (wave > WaveCount && blue.HasCarrier && red.HasCarrier) return;
        spawns.BeginWave();
        ReplaceCarrier(blue);
        ReplaceCarrier(red);
        if (wave > WaveCount) return;
        int blueAdded = Spawn(blue, blueCount);
        int redAdded = Spawn(red, redCount);
        LogScore($"Wave {wave}/{WaveCount}, +{blueAdded} {blue.Taskforce._nameInMissionFile}, +{redAdded} {red.Taskforce._nameInMissionFile}");
    }

    internal void RecordLoss(ObjectBase unit)
    {
        if (!alive.Remove(unit, out Side side)) return;
        side.Losses++;
        LogScore($"{side.Taskforce._nameInMissionFile} lost {unit.getName()}");
    }

    internal void RecordCarrierAircraft(ObjectBase unit)
    {
        if (!startedAt.HasValue || !unit.IsAirUnit || unit._homeBase is not Vessel) return;
        Side? side = unit._taskforce == blue.Taskforce ? blue : unit._taskforce == red.Taskforce ? red : null;
        if (side is null || !Add(unit, side)) return;
        unit._ai._identifySelfMode = AI.IdentifySelfMode.No;
    }

    internal void Finish()
    {
        if (startedAt.HasValue) LogScore("Drill ended");
    }

    private int Spawn(Side side, int count)
    {
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            ObjectBase? unit = spawns.Spawn(side.Taskforce, side.Direction, ++side.ReinforcementNumber);
            if (unit == null) continue;
            Add(unit, side);
            spawned++;
        }
        return spawned;
    }

    private void ReplaceCarrier(Side side)
    {
        if (side.HasCarrier) return;
        ObjectBase? carrier = spawns.Spawn(side.Taskforce, side.Direction, ++side.ReinforcementNumber, carrier: true);
        if (carrier == null) return;
        Add(carrier, side);
        RegisterCarrier(side, carrier);
        LogScore($"{side.Taskforce._nameInMissionFile} received a replacement carrier");
    }

    private static void RegisterCarrier(Side side, ObjectBase carrier)
    {
        side.Carrier = carrier;
        var deck = carrier._obp._flightDeck;
        bool patrol = carrier.FlightDeckLaunchUnit(ObjectBase.ObjectType.Aircraft, "AirToAir",
            missionType: FlightDeckTask.MissionType.CAP);
        Debug.Log($"[OODA] {side.Taskforce._nameInMissionFile} carrier {carrier.getObjectIniName()}. "
            + $"Aircraft aboard {deck?.TotalVehiclesOnBoard() ?? 0}. Patrol queued {patrol}.");
    }

    private bool Add(ObjectBase unit, Side side)
    {
        if (!alive.TryAdd(unit, side)) return false;
        side.Deployed++;
        return true;
    }

    private void LogScore(string message) =>
        Debug.Log($"[OODA] {message}. Losses / deployed: {blue}, {red}.");

    private class Side(Taskforce taskforce, int direction)
    {
        internal Taskforce Taskforce { get; } = taskforce;
        internal int Direction { get; } = direction;
        internal int Deployed { get; set; }
        internal int Losses { get; set; }
        internal int ReinforcementNumber { get; set; } = 2;
        internal ObjectBase? Carrier { get; set; }
        internal bool HasCarrier => Carrier != null && !Carrier.IsDestroyed && !Carrier.IsDespawned;
        public override string ToString() => $"{Taskforce._nameInMissionFile} {Losses}/{Deployed}";
    }
}
