using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace Deckpark;

[HarmonyPatch]
internal static class DeckOperations
{
    private static readonly ConditionalWeakTable<Vessel, List<DeckSlot>> Decks = new();
    private static readonly ConditionalWeakTable<LandUnit, DeckMount> Mounts = new();
    internal static List<DeckSlot> Slots(Vessel ship) => Decks.GetValue(ship, DeckCatalog.Slots);
    internal static bool CanUse(Vessel? ship) => ship != null && !ship.IsDestroyed && ship.IsPlayerUnit
        && ship.AcceptsOrdersFromPlayer && ship._taskforce != null && ship._obp != null;
    internal static DeckMount? Mounted(LandUnit unit) => Mounts.TryGetValue(unit, out var mount) ? mount : null;
    internal static IEnumerable<DeckMount> On(Vessel ship) => Singleton<ObjectsManager>.Instance._listOfAllObjects
        .OfType<LandUnit>().Select(Mounted).Where(mount => mount != null && mount.ParentId == ship.UniqueID).Cast<DeckMount>();

    internal static string Mount(Vessel ship, DeckSlot slot, string reference)
    {
        if (!CanUse(ship) || slot.Used) return "Slot unavailable";
        if (!DeckCatalog.Available(reference)) return "Ground unit not installed";
        float length = Singleton<UnitMetadataProvider>.Instance.UnitData[("land_units", reference)].Item2._length;
        if (On(ship).Any(mount => (mount.Offset - slot.Offset).magnitude < (length + mount.Unit._obp._length) / 2 + 2))
            return "Move the slot clear of the other mounted units";
        LandUnit? unit = null;
        try
        {
            var manager = Singleton<ObjectsManager>.Instance;
            var position = ship._geoPosition;
            position._height += Utils.heightInMeterToUnity(slot.Offset.y);
            unit = manager.createLandUnit("Random", "", reference, manager._listOfAllObjects.Count + 1,
                Singleton<SceneCreator>.Instance.GeoCenterPosition, position, 1,
                new Vector3(0, ship.transform.eulerAngles.y + slot.Heading, 0), ship._taskforce, nationOverride: ship.Nation.Value);
            if (unit == null) return "Ground-unit loader returned no unit";
            Attach(unit, ship.UniqueID, slot.Number, slot.Offset, slot.Heading, true);
            slot.Used = true;
            unit.IsControllable = true;
            unit.SetWeaponStatus(ObjectBase.WeaponStatus.Free);
            Debug.Log($"[Deckpark] Mounted {reference} on {ship.DisplayNameShort}, slot {slot.Number}.");
            return "Mounted. Use the unit's native controls for sensors and engagements.";
        }
        catch (Exception error)
        {
            if (unit != null) unit.destroyObject(score: false);
            Debug.LogError($"[Deckpark] Mount failed: {error}");
            return "Mount failed; see game log";
        }
    }

    private static DeckMount Attach(LandUnit unit, int parentId, int slot, Vector3 offset, float heading, bool weapons)
    {
        var mount = unit.gameObject.AddComponent<DeckMount>();
        mount.Unit = unit;
        mount.ParentId = parentId;
        mount.Slot = slot;
        mount.Offset = offset;
        mount.Heading = heading;
        mount.WeaponsEnabled = weapons;
        Mounts.Add(unit, mount);
        mount.ApplyPose();
        return mount;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LandUnit), "ApplyMovement")]
    private static bool KeepDeckPosition(LandUnit __instance) => Mounted(__instance) == null;

    [HarmonyPrefix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.OnUpdateEveryFrame))]
    private static void BeforeFrame(LandUnit __instance) => Mounted(__instance)?.ApplyPose();

    [HarmonyPrefix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.OnFixedUpdate))]
    private static void BeforePhysics(LandUnit __instance) => Mounted(__instance)?.ApplyPose();

    [HarmonyPrefix, HarmonyPatch(typeof(WeaponSystemGun), nameof(WeaponSystemGun.fire))]
    private static bool Gun(WeaponSystemGun __instance) => CanFire(__instance);
    [HarmonyPrefix, HarmonyPatch(typeof(WeaponSystemLauncher), nameof(WeaponSystemLauncher.launch))]
    private static bool Missile(WeaponSystemLauncher __instance) => CanFire(__instance);
    [HarmonyPrefix, HarmonyPatch(typeof(WeaponSystemCIWS), nameof(WeaponSystemCIWS.StartFire))]
    private static bool Ciws(WeaponSystemCIWS __instance) => CanFire(__instance);
    private static bool CanFire(WeaponSystem weapon) => weapon._baseObject is not LandUnit unit
        || Mounted(unit) is not { } mount || (mount.Ready && mount.WeaponsEnabled && !unit.IsDestroyed);

    [HarmonyPostfix, HarmonyPatch(typeof(Vessel), nameof(Vessel.SaveStateToFile))]
    private static void SaveShip(Vessel __instance, IniHandler ini, string sectionName)
    {
        if (Decks.TryGetValue(__instance, out var slots))
            ini.writeValue(sectionName, "DeckparkUsedSlots", string.Join(",", slots.Where(slot => slot.Used).Select(slot => slot.Number)));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Vessel), nameof(Vessel.LoadStateFromFile))]
    private static void LoadShip(Vessel __instance, IniHandler ini, string sectionName)
    {
        if (!ini.Data.TryGetValue(sectionName, out var section) || !section.TryGetValue("DeckparkUsedSlots", out string saved)) return;
        var used = saved.Split(',').ToHashSet();
        foreach (var slot in Slots(__instance)) slot.Used = used.Contains(slot.Number.ToString(CultureInfo.InvariantCulture));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.SaveStateToFile))]
    private static void SaveUnit(LandUnit __instance, IniHandler ini, string sectionName)
    {
        if (Mounted(__instance) is not { } mount) return;
        ini.writeValue(sectionName, "DeckparkMount", FormattableString.Invariant(
            $"{mount.ParentId},{mount.Slot},{mount.Offset.x},{mount.Offset.y},{mount.Offset.z},{mount.Heading},{(mount.WeaponsEnabled ? 1 : 0)}"));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.LoadStateFromFile))]
    private static void LoadUnit(LandUnit __instance, IniHandler ini, string sectionName)
    {
        if (!ini.Data.TryGetValue(sectionName, out var section) || !section.TryGetValue("DeckparkMount", out string saved)) return;
        try
        {
            string[] values = saved.Split(',');
            if (values.Length != 7) throw new FormatException("Expected seven mount values");
            float Read(int index) => float.Parse(values[index], CultureInfo.InvariantCulture);
            var offset = new Vector3(Read(2), Read(3), Read(4));
            float heading = Read(5);
            if (!float.IsFinite(offset.sqrMagnitude) || !float.IsFinite(heading) || offset.magnitude > 1000)
                throw new FormatException("Invalid mount position");
            Attach(__instance, int.Parse(values[0], CultureInfo.InvariantCulture), int.Parse(values[1], CultureInfo.InvariantCulture),
                offset, heading, values[6] == "1");
        }
        catch (Exception error)
        {
            __instance.SetWeaponStatus(ObjectBase.WeaponStatus.Hold);
            Debug.LogError($"[Deckpark] Could not restore mount: {error.Message}");
        }
    }
}
