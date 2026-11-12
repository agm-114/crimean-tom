using CrimeanTom.SeaPower;
using SeaPower;
using UnityEngine;

namespace OODA;

internal class ReinforcementSpawns(GeoPosition center, ReinforcementCatalog catalog)
{
    private readonly System.Random random = new();
    private readonly Dictionary<Taskforce, Queue<ReinforcementKind>> queues = [];
    private List<ChartPosition> occupied = [];

    internal void WriteStartingShips(IniHandler ini, string side)
    {
        WriteShip(1, ReinforcementKind.Surface);
        WriteShip(2, ReinforcementKind.Carrier);

        void WriteShip(int number, ReinforcementKind kind)
        {
            ReinforcementCatalog.Model model = catalog.Pick(kind, random);
            string key = $"{side}Vessel{number}";
            ini.writeValue(key, "Type", model.Type);
            ini.writeValue(key, "LoadoutVariant", Loadout(model));
        }
    }

    internal void BeginWave()
    {
        var objects = Singleton<ObjectsManager>.Instance;
        occupied = objects._listOfAllUnits
            .Where(unit => unit != null && !unit.IsDespawned)
            .Select(unit => ReinforcementPlacement.Chart(unit._geoPosition)).ToList();
    }

    internal ObjectBase? Spawn(Taskforce side, int direction, int number, bool carrier = false)
    {
        ReinforcementKind kind = carrier ? ReinforcementKind.Carrier : NextKind(side);
        string name = $"{side._nameInMissionFile} {number}";
        ReinforcementCatalog.Model model = catalog.Pick(kind, random);
        var objects = Singleton<ObjectsManager>.Instance;
        if (ReinforcementPlacement.Find(center, occupied, objects._listOfAllUnits, model, side, direction, random) is not { } placement)
        {
            Debug.LogWarning($"[OODA] Skipped {name}. No clear {kind} staging position for {model.Type}.");
            return null;
        }

        GeoPosition position = placement.Position;
        string loadout = Loadout(model);
        var heading = new Vector3(0, placement.Heading, 0);
        ObjectBase? unit = kind switch
        {
            ReinforcementKind.Surface or ReinforcementKind.Carrier => objects.createShip("Random", loadout, model.Type, number, center, position, 4, heading, side, loadAsync: false),
            ReinforcementKind.Submarine => objects.createSubmarine("Random", loadout, model.Type, number, center, position, 3, heading, side, loadAsync: false),
            ReinforcementKind.Aircraft => objects.createAircraft("Random", loadout, null, null, model.Type, number, center, position, heading, side,
                createMinimapSymbol: true, loadWeaponSystems: true),
            ReinforcementKind.Helicopter => objects.createHelicopter("Random", loadout, null, null, model.Type, number, center, position, heading, side),
            ReinforcementKind.Land => objects.createLandUnit("Random", loadout, model.Type, number, center, position, 0, heading, side),
            _ => null,
        };
        if (unit == null)
        {
            Debug.LogError($"[OODA] Could not spawn {name} ({model.Type}).");
            return null;
        }

        occupied.Add(ReinforcementPlacement.Chart(position));
        unit.setName(name);
        unit._ai._identifySelfMode = AI.IdentifySelfMode.No;
        if (kind != ReinforcementKind.Submarine) unit.EnableAllActiveSensors();
        if (unit is LandUnit land)
        {
            SnapToGround(land, position, model.Port);
            unit.SetWeaponStatus(ObjectBase.WeaponStatus.Free);
        }
        else
        {
            unit.SetWeaponStatus(ObjectBase.WeaponStatus.Tight);
            unit.SetWaypointTask(new WaypointData
            {
                _geoposition = placement.Entry,
                wpName = "Enter combat area",
                _setWeaponStatus = (true, ObjectBase.WeaponStatus.Free)
            });
            if (placement.Destination is { } destination) unit.setWaypointTask(destination);
        }
        if (carrier) unit._obp._flightDeck?.LoadFlightDeckAirgroupAndAmmo();
        if (unit is Aircraft aircraft)
        {
            aircraft.setTelegraph(3);
            aircraft._velocityInKnots = aircraft.SpeedCommand.Value.GetDemandedSpeedInKnots(aircraft);
            aircraft.ImmediateStartEngines();
        }
        Debug.Log(FormattableString.Invariant($"[OODA] Spawned {name} ({model.Type}) at {position._latitude:F5}, {position._longitude:F5}. Nearest hostile {placement.NearestHostileNm:F1} nm."));
        return unit;
    }

    private ReinforcementKind NextKind(Taskforce side)
    {
        if (!queues.TryGetValue(side, out var queue)) queues[side] = queue = new();
        if (queue.Count == 0)
            foreach (ReinforcementKind kind in catalog.Kinds.OrderBy(_ => random.Next())) queue.Enqueue(kind);
        return queue.Dequeue();
    }

    private string Loadout(ReinforcementCatalog.Model model) =>
        model.Loadouts.Length == 0 ? "" : model.Loadouts[random.Next(model.Loadouts.Length)];

    private static void SnapToGround(LandUnit land, GeoPosition position, bool port)
    {
        if (port) land._performAutoFlatten = false;
        position._height = TerrainUtils.GetHeightAt(position) + land._groundOffset;
        land._altitudeCorrection = ObjectBase.AltitudeCorrection.Low;
        land.setGeoPosition(position);
        land.DesiredAltitude.Value = (float)position._height;
        land.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
        land.SetTeleportProtection();
    }
}
