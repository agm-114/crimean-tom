using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace Airdrop;

[HarmonyPatch]
internal static class DropOperations
{
    private static readonly ConditionalWeakTable<Aircraft, CargoInventory> Inventories = new();
    internal static bool CanUse(Aircraft? aircraft) => aircraft != null && !aircraft.IsDestroyed
        && aircraft.IsPlayerUnit && aircraft.AcceptsOrdersFromPlayer && aircraft._taskforce != null
        && aircraft._obp != null && CargoCatalog.Manifests.ContainsKey(aircraft._obp._objectIniName);
    internal static CargoInventory Inventory(Aircraft aircraft) => Inventories.GetValue(aircraft,
        unit => new(CargoCatalog.Manifests[unit._obp._objectIniName]));

    internal static string Blocked(Aircraft aircraft, CargoInventory inventory, out GeoPosition position)
    {
        position = aircraft._geoPosition;
        if (!CanUse(aircraft)) return "Aircraft unavailable";
        if (!aircraft._isInFlight || aircraft.IsLanding) return "Aircraft must be airborne";
        var limits = inventory.Definition;
        float ground = TerrainUtils.GetRealHeightInMetersAt(position, false);
        if (!float.IsFinite(ground) || ground <= 1) return "Waiting for land";
        double altitude = Utils.unityHeightToMeter((float)position._height) - ground;
        if (altitude < limits.MinAltitude || altitude > limits.MaxAltitude)
            return $"Set {limits.MinAltitude}-{limits.MaxAltitude} m AGL";
        if (aircraft._velocityInKnots < limits.MinSpeed || aircraft._velocityInKnots > limits.MaxSpeed)
            return $"Set {limits.MinSpeed}-{limits.MaxSpeed} kn";
        if (Math.Abs(Mathf.DeltaAngle(0, aircraft.transform.eulerAngles.z)) > 10) return "Waiting for level wings";
        // Reject water edges and steep terrain across a 50-metre landing area.
        foreach (var offset in new[] { (25d, 0d), (-25d, 0d), (0d, 25d), (0d, -25d) })
        {
            float height = TerrainUtils.GetRealHeightInMetersAt(Offset(position, offset.Item1, offset.Item2), false);
            if (!float.IsFinite(height) || height <= 1 || Math.Abs(height - ground) > 5) return "Waiting for clear, level land";
        }
        var landing = position;
        if (Singleton<ObjectsManager>.Instance._listOfAllObjects.Any(unit => unit != null && !unit.IsDestroyed
            && unit is LandUnit && unit._geoPosition.getDistanceInMiles(landing) * 1852 < 30 + (unit._obp?._length ?? 0) * 0.5f))
            return "Landing area occupied";
        position._height = Utils.heightInMeterToUnity(ground);
        return "";
    }

    private static GeoPosition Offset(GeoPosition position, double north, double east)
    {
        position._latitude += north / 1852 / 60;
        position._longitude += east / 1852 / 60 / Math.Max(0.01, Math.Cos(position._latitude * Math.PI / 180));
        return position;
    }

    internal static void Update()
    {
        if (!Singleton<ObjectsManager>.InstanceExists()) return;
        foreach (var aircraft in Singleton<ObjectsManager>.Instance._listOfAllObjects.OfType<Aircraft>().ToArray())
        {
            if (!Inventories.TryGetValue(aircraft, out var inventory)) continue;
            float now = GameTime.missionSessionTime;
            float seconds = Math.Max(0, now - inventory.LastUpdate);
            inventory.LastUpdate = now;
            if (!CanUse(aircraft)) { inventory.Disarm(); continue; }
            if (seconds == 0 || GameTime.IsPaused() || inventory.ArmedCargo is not { } cargo || inventory.Remaining <= 0) continue;
            inventory.Elapsed = Math.Min(inventory.Definition.Interval, inventory.Elapsed + seconds);
            if (!cargo.Available) { inventory.Message = "Cargo unavailable"; inventory.Disarm(); continue; }
            string blocked = Blocked(aircraft, inventory, out var position);
            if (blocked.Length > 0 || inventory.Elapsed < inventory.Definition.Interval) continue;
            try
            {
                var manager = Singleton<ObjectsManager>.Instance;
                var unit = manager.createLandUnit("Random", "", cargo.Reference, manager._listOfAllObjects.Count + 1,
                    Singleton<SceneCreator>.Instance.GeoCenterPosition, position, 1,
                    new Vector3(0, aircraft.transform.eulerAngles.y, 0), aircraft._taskforce, nationOverride: aircraft.Nation.Value);
                if (unit == null) throw new InvalidOperationException("Ground-unit loader returned no unit.");
                // A successful native spawn consumes cargo exactly once. Failed attempts retain it.
                inventory.Aboard[cargo.Reference]--;
                inventory.Released[cargo.Reference]++;
                inventory.Remaining--;
                inventory.Elapsed = 0;
                unit.IsControllable = true;
                Debug.Log($"[Airdrop] {aircraft.DisplayNameShort} released {cargo.Reference}; {inventory.Aboard[cargo.Reference]} aboard.");
                if (inventory.Remaining == 0 || inventory.Aboard[cargo.Reference] == 0)
                {
                    inventory.Disarm();
                    inventory.Message = "Release complete";
                }
            }
            catch (Exception error)
            {
                inventory.Disarm();
                inventory.Message = "Release failed; see game log";
                Debug.LogError($"[Airdrop] Release failed: {error}");
            }
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SaveStateToFile))]
    private static void Save(Aircraft __instance, IniHandler ini, string sectionName)
    {
        if (__instance._obp == null || !CargoCatalog.Manifests.ContainsKey(__instance._obp._objectIniName)) return;
        var inventory = Inventory(__instance);
        foreach (var cargo in inventory.Definition.Cargo)
            ini.writeValue(sectionName, "Airdrop_" + cargo.Reference, $"{inventory.Aboard[cargo.Reference]},{inventory.Released[cargo.Reference]}");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Aircraft), nameof(Aircraft.LoadStateFromFile))]
    private static void Load(Aircraft __instance, IniHandler ini, string sectionName)
    {
        if (__instance._obp == null || !CargoCatalog.Manifests.ContainsKey(__instance._obp._objectIniName)
            || !ini.Data.TryGetValue(sectionName, out var section)) return;
        var inventory = Inventory(__instance);
        inventory.Disarm();
        inventory.LastUpdate = GameTime.missionSessionTime;
        foreach (var cargo in inventory.Definition.Cargo)
        {
            if (!section.TryGetValue("Airdrop_" + cargo.Reference, out string saved)) continue;
            string[] values = saved.Split(',');
            if (values.Length != 2 || !int.TryParse(values[0], out int aboard) || !int.TryParse(values[1], out int released)) continue;
            inventory.Aboard[cargo.Reference] = Math.Clamp(aboard, 0, cargo.Count);
            inventory.Released[cargo.Reference] = Math.Clamp(released, 0, cargo.Count);
        }
    }
}
