using System.Runtime.CompilerServices;
using System.Globalization;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace WetDeckControls;

internal class BoatDeployment(string parent, string craft)
{
    internal string Parent { get; } = parent;
    internal string Craft { get; } = craft;
    internal bool Recovering { get; set; }
}

[HarmonyPatch]
internal static class WetDeckOperations
{
    private static readonly ConditionalWeakTable<ObjectBase, DeckInventory> Inventories = new();
    private static readonly ConditionalWeakTable<ObjectBase, BoatDeployment> Boats = new();
    private static List<ObjectBase> Units => Singleton<ObjectsManager>.Instance._listOfAllObjects;
    internal static DeckInventory Inventory(ObjectBase ship) => Inventories.GetValue(ship,
        unit => new(WetDeckCatalog.Decks[unit._obp._objectIniName]));
    internal static IEnumerable<(ObjectBase Boat, BoatDeployment Deployment)> Deployed(DeckInventory inventory)
    {
        foreach (var unit in Units.ToArray())
            if (unit != null && !unit.IsDestroyed && Boats.TryGetValue(unit, out var deployment) && deployment.Parent == inventory.Id)
                yield return (unit, deployment);
    }

    internal static string Launch(ObjectBase ship, CraftDefinition craft, int count)
    {
        var inventory = Inventory(ship);
        if (!Singleton<UnitMetadataProvider>.Instance.UnitData.ContainsKey((craft.Folder, craft.Reference)))
            return "Craft unavailable. Amphibious vehicles require the Amphibs mod.";
        if (Math.Abs(ship._velocityInKnots) > 5) return "Slow the ship to 5 knots or less to launch.";
        int launched = 0;
        for (int i = 0; i < count && inventory.Count(craft) > 0; i++)
        {
            GeoPosition? position = LaunchPosition(ship, craft);
            if (position == null) return $"Launched {launched}. No clear water astern.";
            try
            {
                bool seals = craft.Reference == "all_assault_raft";
                var manager = Singleton<ObjectsManager>.Instance;
                ObjectBase boat = craft.Folder == "land_units"
                    ? manager.createLandUnit("Random", "", craft.Reference, Units.Count + 1,
                        Singleton<SceneCreator>.Instance.GeoCenterPosition, position.Value,
                        1, new Vector3(0, ship.transform.eulerAngles.y + 180, 0), ship._taskforce, nationOverride: ship.Nation.Value)
                    : manager.createShip(seals ? "Variant1" : "Random", seals ? "Commandos" : "", craft.Reference,
                    Units.Count + 1, Singleton<SceneCreator>.Instance.GeoCenterPosition, position.Value,
                    1, new Vector3(0, ship.transform.eulerAngles.y + 180, 0), ship._taskforce, nationOverride: seals ? "US" : ship.Nation.Value);
                if (boat == null) return $"Launched {launched}. Craft could not be loaded.";
                boat.IsControllable = true;
                Boats.Add(boat, new(inventory.Id, craft.Key));
                inventory.Return(craft);
                inventory.Aboard[craft.Key]--;
                launched++;
            }
            catch (Exception error)
            {
                Debug.LogError($"[Well Deck] Launch failed: {error}");
                return $"Launched {launched}. Launch failed; see game log.";
            }
        }
        return launched == 0 ? "Ready craft before launching." : $"Launched {launched}.";
    }

    private static GeoPosition? LaunchPosition(ObjectBase ship, CraftDefinition craft)
    {
        float craftLength = Singleton<UnitMetadataProvider>.Instance.UnitData[(craft.Folder, craft.Reference)].Item2._length;
        // Leave ten metres between hulls, directly along the parent's stern line.
        float distance = (ship._obp._length + craftLength) * 0.5f + 10;
        float angle = ship.transform.eulerAngles.y + 180;
        double degrees = distance / 1852d / 60d;
        var position = new GeoPosition
        {
            _latitude = ship._geoPosition._latitude + Math.Cos(angle * Mathf.Deg2Rad) * degrees,
            _longitude = ship._geoPosition._longitude + Math.Sin(angle * Mathf.Deg2Rad) * degrees
                / Math.Max(0.01, Math.Cos(ship._geoPosition._latitude * Math.PI / 180)),
            _height = 0,
        };
        if (TerrainUtils.GetRealHeightInMetersAt(position, false) >= -5) return null;
        if (Units.Any(unit => unit != null && !unit.IsDestroyed && unit != ship
            && unit._geoPosition.getDistanceInMiles(position) * 1852 < (craftLength + (unit._obp?._length ?? 0)) * 0.5f + 10)) return null;
        return position;
    }

    internal static void Recover(ObjectBase ship, ObjectBase boat, BoatDeployment deployment)
    {
        if (!boat.AcceptsOrdersFromPlayer || boat.IsDestroyed) return;
        boat.RemoveWaypoints();
        boat.SetRelativeWaypointTask(new Vector3(0, 0, -180), ship, "Well Deck recovery");
        boat.setTelegraph(3);
        deployment.Recovering = true;
    }

    internal static void UpdateRecovery()
    {
        if (!Singleton<ObjectsManager>.InstanceExists()) return;
        foreach (var ship in Units.ToArray())
        {
            if (ship == null || ship.IsDestroyed || !Inventories.TryGetValue(ship, out var inventory)) continue;
            float now = GameTime.missionSessionTime;
            float elapsed = Math.Max(0, now - inventory.LastUpdate);
            inventory.LastUpdate = now;
            if (elapsed > 0 && ship.IsPlayerUnit && ship.AcceptsOrdersFromPlayer)
            {
                inventory.Advance(elapsed);
                foreach (var task in inventory.Tasks.Where(task => task.Remaining <= 0 && task.AutoLaunch).ToArray())
                {
                    inventory.Message = Launch(ship, task.Craft, 1);
                    // A blocked launch remains ready and can be retried explicitly.
                    if (inventory.Tasks.Contains(task)) task.AutoLaunch = false;
                }
            }
            foreach (var (boat, deployment) in Deployed(inventory).ToArray())
            {
                if (!deployment.Recovering || !ship.IsPlayerUnit || !boat.AcceptsOrdersFromPlayer
                    || Math.Abs(ship._velocityInKnots) > 5 || Math.Abs(boat._velocityInKnots) > 8
                    || ship._geoPosition.getDistanceInMiles(boat._geoPosition) > 0.13) continue;
                var craft = inventory.Definition.Craft.FirstOrDefault(item => item.Key == deployment.Craft);
                if (craft == null || inventory.Aboard[craft.Key] >= craft.Count
                    || inventory.OccupiedSpace + craft.Space > inventory.Definition.StorageCapacity) continue;
                boat.destroyObject(score: false);
                Boats.Remove(boat);
                inventory.Aboard[craft.Key]++;
            }
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.SaveStateToFile))]
    private static void SaveLandCraft(LandUnit __instance, IniHandler ini, string sectionName) => Save(__instance, ini, sectionName);

    [HarmonyPostfix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.LoadStateFromFile))]
    private static void LoadLandCraft(LandUnit __instance, IniHandler ini, string sectionName) => Load(__instance, ini, sectionName);

    [HarmonyPostfix, HarmonyPatch(typeof(Vessel), nameof(Vessel.SaveStateToFile))]
    private static void Save(ObjectBase __instance, IniHandler ini, string sectionName)
    {
        if (Inventories.TryGetValue(__instance, out var inventory))
        {
            ini.writeValue(sectionName, "WetDeckId", inventory.Id);
            foreach (var craft in inventory.Definition.Craft)
            {
                ini.writeValue(sectionName, "WetDeck_" + craft.Reference, $"{inventory.Aboard[craft.Key]},{inventory.Count(craft)}");
                ini.writeValue(sectionName, "WetDeckPrep_" + craft.Reference, string.Join(";", inventory.Tasks.Where(task => task.Craft == craft)
                    .Select(task => task.Remaining.ToString("R", CultureInfo.InvariantCulture) + ":" + task.AutoLaunch)));
            }
        }
        if (Boats.TryGetValue(__instance, out var deployment))
            ini.writeValue(sectionName, "WetDeckBoat", $"{deployment.Parent},{deployment.Craft},{deployment.Recovering}");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Vessel), nameof(Vessel.LoadStateFromFile))]
    private static void Load(ObjectBase __instance, IniHandler ini, string sectionName)
    {
        if (!ini.Data.TryGetValue(sectionName, out var section)) return;
        if (section.TryGetValue("WetDeckId", out string id) && Guid.TryParseExact(id, "N", out _)
            && WetDeckCatalog.Decks.ContainsKey(__instance._obp._objectIniName))
        {
            var inventory = Inventory(__instance);
            inventory.Id = id;
            inventory.Tasks.Clear();
            inventory.LastUpdate = GameTime.missionSessionTime;
            foreach (var craft in inventory.Definition.Craft)
            {
                if (!section.TryGetValue("WetDeck_" + craft.Reference, out string saved)) continue;
                string[] values = saved.Split(',');
                if (values.Length != 2 || !int.TryParse(values[0], out int aboard) || !int.TryParse(values[1], out int staged)) continue;
                inventory.Aboard[craft.Key] = Math.Clamp(aboard, 0, craft.Count);
                if (section.TryGetValue("WetDeckPrep_" + craft.Reference, out string preparation))
                {
                    foreach (string row in preparation.Split(';').Take(inventory.Aboard[craft.Key]))
                    {
                        string[] fields = row.Split(':');
                        if (fields.Length == 2 && float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)
                            && !float.IsNaN(seconds) && !float.IsInfinity(seconds) && bool.TryParse(fields[1], out bool launch))
                            inventory.Tasks.Add(new(craft, Math.Clamp(seconds, 0, inventory.Definition.PreparationSeconds), launch));
                    }
                }
                else for (int i = 0; i < Math.Clamp(staged, 0, inventory.Aboard[craft.Key]); i++) inventory.Tasks.Add(new(craft, 0, false));
            }
        }
        if (section.TryGetValue("WetDeckBoat", out string record))
        {
            string[] values = record.Split(',');
            if (values.Length != 3 || !Guid.TryParseExact(values[0], "N", out _)) return;
            Boats.Remove(__instance);
            Boats.Add(__instance, new(values[0], values[1]) { Recovering = bool.TryParse(values[2], out bool recovering) && recovering });
        }
    }
}
