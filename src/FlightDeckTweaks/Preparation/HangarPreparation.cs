using System.Runtime.CompilerServices;
using System.Xml.Linq;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace FlightDeckTweaks.Preparation;

// Recovered aircraft stay separate until their squadron next requests a launch batch.
[HarmonyPatch]
internal class HangarPreparation
{
    private const string SaveKey = "FDTHangar";
    private static readonly ConditionalWeakTable<FlightDeck, HangarPreparation> Decks = new();
    private readonly List<StoredAircraft> aircraft = [];
    private bool creating;
    internal static bool ChangingLoadout { get; set; }
    private static HangarPreparation For(FlightDeck deck) => Decks.GetValue(deck, _ => new());

    internal class StoredAircraft(string type, string squadron, string loadout, string serial, AircraftPreparation state)
    {
        internal string Type { get; } = type;
        internal string Squadron { get; } = squadron;
        internal string Loadout { get; } = loadout;
        internal string Serial { get; } = serial;
        internal AircraftPreparation State { get; } = state;
        internal bool Ready { get; set; }
    }

    private class LaunchTransfer(StoredAircraft[] selected, PreparationBatch batch, StoreReservation held, StoreReservation excess, bool tracked)
    {
        internal StoredAircraft[] Selected { get; } = selected;
        internal PreparationBatch Batch { get; } = batch;
        internal StoreReservation Held { get; } = held;
        internal StoreReservation Excess { get; } = excess;
        internal bool Tracked { get; } = tracked;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(DeSpawn), nameof(DeSpawn.onEnter))]
    private static void Capture(DeSpawn __instance, out StoredAircraft? __state)
    {
        __state = null;
        FlightDeckTask task = __instance._flightDeckTask;
        if (__instance._isFinished || task.Vehicle == null || task.Vehicle.IsDestroyed
            || !task._flightDeck._baseObject.AcceptsOrdersFromPlayer) return;
        try
        {
            ObjectBase unit = task.Vehicle;
            if (task.VehicleType is not { } vehicle) return;
            Loadout? loadout = vehicle.Loadouts.FirstOrDefault(item => item.Name == unit._obp._loadoutVariant);
            if (loadout == null) return;
            PreparationLoadout target = PreparationCatalog.Loadout(vehicle, loadout);
            double fuel = unit switch
            {
                Helicopter helicopter => helicopter._hp._currentFuelMass,
                Aircraft plane => plane.Ap._currentFuelMass,
                _ => 0,
            };
            AircraftPreparation state = new(target);
            state.Load(new XElement("Aircraft", new XAttribute("Fuel", Math.Clamp(fuel / Math.Max(1, target.FuelCapacity), 0, 1)),
                unit.AmmunitionAmountDictionary.Where(pair => pair.Value > 0).Select(pair => new XElement("Store",
                    new XAttribute("Ref", pair.Key), new XAttribute("Amount", pair.Value),
                    new XAttribute("RemoveSeconds", PreparationCatalog.Weapon(pair.Key).RemoveSeconds)))));
            __state = new(unit._obp._objectIniName, unit._obp._objectVariantReference, loadout.Name,
                unit._obp._objectSerialNumber, state);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Deck Tweaks] Could not capture returning aircraft; using native recovery. {exception}");
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(DeSpawn), nameof(DeSpawn.onEnter))]
    private static void StoreRecovered(DeSpawn __instance, StoredAircraft? __state)
    {
        if (__state == null || !__instance._isFinished) return;
        FlightDeck deck = __instance._flightDeckTask._flightDeck;
        For(deck).aircraft.Add(__state);
        // Native recovery refunded these weapons. Keep them aboard, unavailable to other aircraft.
        new StoreReservation().Replace(deck, StoreReservation.From([__state.State]));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(HandleCoolDownTask), nameof(HandleCoolDownTask.onExit))]
    private static void Available(CoolDownTask ____flightDeckTask)
    {
        CoolDownTask task = ____flightDeckTask;
        StoredAircraft? stored = For(task._flightDeck).aircraft.FirstOrDefault(item => !item.Ready
            && item.Type == task.iniRef && item.Squadron == task.squadronRef && item.Serial == task.serialNumber);
        if (stored == null) return;
        stored.Ready = true;
        task._flightDeck.UpdateLoadoutsAvailability();
    }

    internal static void Return(PendingLaunchTask task, PreparationBatch batch)
    {
        if (ChangingLoadout) return;
        HangarPreparation hangar = For(task._flightDeck);
        string squadron = task.VehicleType.Squadrons[task._squadronIndex]._name;
        foreach (AircraftPreparation state in batch.Aircraft)
            hangar.aircraft.Insert(0, new(task.VehicleType._fileName, squadron, TaskPreparation.Selected(task).Name, "", state) { Ready = true });
        // The following native abort refunds the full loadout. Retain only what's actually loaded.
        new StoreReservation().Replace(task._flightDeck, StoreReservation.From(batch.Aircraft), refresh: false);
    }

    private StoredAircraft[] Select(VehicleTypeOnBoard vehicle, int count, string? squadron = null) => aircraft
        .Where(item => item.Ready && item.Type == vehicle._fileName && (squadron == null || item.Squadron == squadron))
        .Take(count).ToArray();

    private static LaunchTransfer Transfer(StoredAircraft[] selected, int count, PreparationLoadout target, bool tracked = true)
    {
        PreparationBatch batch = new(count, target);
        for (int index = 0; index < selected.Length; index++) batch.Aircraft[index] = selected[index].State.Preview(target);
        return new(selected, batch, StoreReservation.From(selected.Select(item => item.State)),
            tracked ? StoreReservation.Excess(batch, target) : new(), tracked);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.createLaunchTask))]
    private static bool BeforeLaunch(FlightDeck __instance, VehicleTypeOnBoard vehicle, Loadout loadout, Squadron squadron,
        LaunchTaskParameters ltp, ref PendingLaunchTask? __result, out LaunchTransfer? __state)
    {
        __state = null;
        HangarPreparation hangar = For(__instance);
        hangar.creating = true;
        if (ChangingLoadout || ltp._fromSave || ltp._launchCount <= 0) return true;
        StoredAircraft[] selected = hangar.Select(vehicle, ltp._launchCount, squadron._name);
        if (selected.Length == 0) return true;
        PreparationLoadout target = PreparationCatalog.Loadout(vehicle, loadout);
        bool tracked = __instance._baseObject.AcceptsOrdersFromPlayer && !ltp._fromScript && ltp._airStrike == null && ltp._civRoute == null;
        LaunchTransfer transfer = Transfer(selected, ltp._launchCount, target, tracked);
        if (!transfer.Excess.CanFund(__instance, transfer.Held, loadout, ltp._launchCount))
        {
            __result = null;
            Globals._mainGameViewModel.RaiseAlert(__instance._baseObject,
                Singleton<LanguageResourceHandler>.Instance.getText("FlightDeck", "UnableToLaunchNoAmmo"));
            return false;
        }
        __state = transfer;
        foreach (StoredAircraft item in selected) hangar.aircraft.Remove(item);
        transfer.Held.Replace(__instance, transfer.Excess, refresh: false);
        return true;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.createLaunchTask))]
    private static void AfterLaunch(FlightDeck __instance, PendingLaunchTask? __result, LaunchTransfer? __state)
    {
        HangarPreparation hangar = For(__instance);
        hangar.creating = false;
        if (__state == null)
        {
            if (hangar.aircraft.Count > 0) __instance.UpdateLoadoutsAvailability();
            return;
        }
        if (__result == null)
        {
            hangar.aircraft.InsertRange(0, __state.Selected);
            __state.Excess.Replace(__instance, __state.Held);
            return;
        }
        if (!__state.Tracked)
        {
            __instance.UpdateLoadoutsAvailability();
            return;
        }
        StoreReservation reservation = __state.Excess;
        if (__result._ltp._skipReadyUp || !DM_TestTools._allowAircraftReadyUpTime)
        {
            __state.Batch.Complete();
            reservation.Replace(__instance, new(), refresh: false);
            reservation = new();
        }
        TaskPreparation.Attach(__result, __state.Batch, reservation);
        __instance.UpdateLoadoutsAvailability();
    }

    [HarmonyFinalizer, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.createLaunchTask))]
    private static void EndLaunch(FlightDeck __instance) => For(__instance).creating = false;

    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.CanLaunchVehicle))]
    private static void CanLaunch(FlightDeck __instance, Loadout loadout, int launchCount, ref bool __result)
    {
        if (__result || launchCount <= 0 || !Decks.TryGetValue(__instance, out var hangar) || hangar.creating) return;
        VehicleTypeOnBoard? vehicle = __instance._vehiclesOnBoard.FirstOrDefault(item => item.Loadouts.Contains(loadout));
        if (vehicle == null) return;
        StoredAircraft[] selected = hangar.Select(vehicle, launchCount);
        if (selected.Length == 0) return;
        LaunchTransfer transfer = Transfer(selected, launchCount, PreparationCatalog.Loadout(vehicle, loadout));
        __result = transfer.Excess.CanFund(__instance, transfer.Held, loadout, launchCount);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.UpdateLoadoutsAvailability))]
    private static void UpdateAvailability(FlightDeck __instance)
    {
        if (!Decks.TryGetValue(__instance, out var hangar) || hangar.creating || hangar.aircraft.All(item => !item.Ready)) return;
        foreach (VehicleTypeOnBoard vehicle in __instance._vehiclesOnBoard.Where(item => item.Numbers > 0))
        {
            if (hangar.Select(vehicle, 1).Length == 0) continue;
            foreach (Loadout loadout in vehicle.Loadouts)
                for (int count = Math.Max(1, loadout.Available.Value + 1); count <= vehicle.Numbers; count++)
                {
                    if (!__instance.CanLaunchVehicle(loadout, count)) break;
                    loadout.Available.Value = count;
                }
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.SaveStateToFile))]
    private static void Save(FlightDeck __instance, IniHandler ini, string sectionName, bool isCampaignStateSave)
    {
        if (isCampaignStateSave || !SaveLoadManager.SaveLoadFlightDeck || !ini.Data.TryGetValue(sectionName, out var section)) return;
        section.Remove(SaveKey);
        if (!Decks.TryGetValue(__instance, out var hangar) || hangar.aircraft.Count == 0) return;
        section[SaveKey] = PreparationBatch.Encode(new XElement("Hangar", hangar.aircraft.Select(item => new XElement("Unit",
            new XAttribute("Type", item.Type), new XAttribute("Squadron", item.Squadron), new XAttribute("Loadout", item.Loadout),
            new XAttribute("Serial", item.Serial), new XAttribute("Ready", item.Ready), item.State.Save()))));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.LoadStateFromFile))]
    private static void Load(FlightDeck __instance, IniHandler ini, string sectionName)
    {
        Decks.Remove(__instance);
        if (!ini.Data.TryGetValue(sectionName, out var section) || !section.TryGetValue(SaveKey, out string saved)) return;
        try
        {
            List<StoredAircraft> restored = [];
            foreach (XElement unit in PreparationBatch.Decode(saved).Elements("Unit"))
            {
                string type = (string)unit.Attribute("Type")!;
                string squadron = (string)unit.Attribute("Squadron")!;
                string name = (string)unit.Attribute("Loadout")!;
                VehicleTypeOnBoard vehicle = __instance._vehiclesOnBoard.First(item => item._fileName == type);
                Loadout loadout = vehicle.Loadouts.First(item => item.Name == name);
                AircraftPreparation state = new(PreparationCatalog.Loadout(vehicle, loadout));
                state.Load(unit.Element("Aircraft") ?? throw new FormatException("Missing recovered aircraft state"));
                restored.Add(new(type, squadron, name, (string?)unit.Attribute("Serial") ?? "", state)
                    { Ready = (bool?)unit.Attribute("Ready") ?? false });
            }
            // Native inventory is already saved with retained stores excluded.
            For(__instance).aircraft.AddRange(restored);
            __instance.UpdateLoadoutsAvailability();
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Deck Tweaks] Could not restore hangar preparation. {exception}");
        }
    }
}
