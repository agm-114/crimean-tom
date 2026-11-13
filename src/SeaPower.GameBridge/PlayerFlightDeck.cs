using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPower.GameBridge;

internal static partial class PlayerFlightDeck
{
    private record ScreenFit(VehicleTypeOnBoard Aircraft, Loadout Loadout, PendingLaunchTask? Held)
    {
        internal string Id => $"{Aircraft._fileName}:{Loadout.Name}";
    }
    private static readonly Lazy<Func<PendingLaunchTask, Loadout, PendingLaunchTask?>?> ChangePreparedLoadout = new(() =>
        AccessTools.TypeByName("DeckTweaks.TaskLoadout")?.GetMethod("ChangePreparedLoadout")
            ?.CreateDelegate(typeof(Func<PendingLaunchTask, Loadout, PendingLaunchTask?>)) as Func<PendingLaunchTask, Loadout, PendingLaunchTask?>);

    internal static JArray Helicopters(Taskforce side, Vessel[] ships)
    {
        Helicopter[] flights = ScreenFlights(side).ToArray();
        JArray result = [];
        foreach (Vessel ship in ships)
        {
            if (ship._obp?._flightDeck is not { } deck) continue;
            VehicleTypeOnBoard[] aircraft = deck._vehiclesOnBoard.Where(IsHelicopter).ToArray();
            FlightDeckTask[] tasks = HelicopterTasks(deck);
            Helicopter[] airborne = flights.Where(unit => unit.getHomeBase() == ship).ToArray();
            if (aircraft.Length == 0 && tasks.Length == 0 && airborne.Length == 0) continue;
            result.Add(new JObject
            {
                ["shipId"] = PlayerPicture.Id(ship), ["shipName"] = ship.DisplayNameShort, ["launchesBlocked"] = LaunchesBlocked(deck),
                ["available"] = new JArray(aircraft.Where(unit => unit.Numbers > 0).Select(unit => new JObject
                {
                    ["type"] = unit.DisplayName, ["count"] = unit.Numbers
                })),
                ["screenFits"] = new JArray(ScreenFits(deck, tasks).GroupBy(fit => fit.Id).Select(group => DescribeFit(group.First()))),
                ["deckTasks"] = new JArray(tasks.Select(task => new JObject
                {
                    ["type"] = task.AircraftType, ["state"] = task.FlightDeckTaskLabel, ["preparation"] = task.Info,
                    ["launchAuthorized"] = task is PendingLaunchTask pending ? pending.launchAllowed : null,
                    ["loadout"] = task is PendingLaunchTask preparing ? preparing.VehicleType.Loadouts[preparing._loadoutIndex].DisplayName : null
                })),
                ["airborne"] = new JArray(airborne.Select(unit => new JObject
                {
                    ["id"] = PlayerPicture.Id(unit), ["name"] = unit.DisplayNameShort, ["order"] = unit.CurrentOrderText.Value
                }))
            });
        }
        return result;
    }

    internal static string QueueHelicopterScreen(Vessel ship, Taskforce side, string fitId, GeoPosition[] route, bool useRadar)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side)
            || ship._obp?._flightDeck is not { } deck || LaunchesBlocked(deck)) return "Rejected · deck launches unavailable";
        if (ScreenFlights(side).Any(unit => unit.getHomeBase() == ship))
            return "Kept · a screening helicopter from this ship is airborne";

        FlightDeckTask[] tasks = HelicopterTasks(deck);
        if (tasks.Any(task => task is LaunchTask || task is PendingLaunchTask { launchAllowed: true }))
            return "Kept · a helicopter launch is already queued";
        ScreenFit? fit = ScreenFits(deck, tasks).FirstOrDefault(option => option.Id == fitId);
        if (fit == null) return "Rejected · selected helicopter fit is no longer available";
        if (fit.Held is { } held)
        {
            if (fit.Loadout != held.VehicleType.Loadouts[held._loadoutIndex])
            {
                PendingLaunchTask? replacement = ChangePreparedLoadout.Value?.Invoke(held, fit.Loadout);
                if (replacement == null) return $"Rejected · could not prepare {fit.Loadout.DisplayName} on the held helicopter";
                held = replacement;
            }
            held._ltp._waypoints.AddRange(route);
            WatchLaunch(held, ship, fit.Loadout, useRadar);
            if (!held.launchAllowed) held.ToggleLaunchAllowedFunc(null);
            return $"Launch authorized · {held.AircraftType} · {fit.Loadout.DisplayName} · {held.FlightDeckTaskLabel}";
        }

        Squadron? squadron = fit.Aircraft.Squadrons.FirstOrDefault(unit => unit.Numbers > 0 && unit.Callsigns.Count > 0);
        if (squadron == null) return "Rejected · no available squadron";
        LaunchTaskParameters parameters = new() { _waypoints = route.ToList() };
        PendingLaunchTask? task = deck.createLaunchTask(fit.Aircraft, fit.Loadout, squadron, squadron.Callsigns[0], parameters);
        if (task == null) return $"Rejected · deck could not queue {fit.Loadout.DisplayName}";
        WatchLaunch(task, ship, fit.Loadout, useRadar);
        return $"Screen queued · {fit.Aircraft.DisplayName} · {fit.Loadout.DisplayName} · {task.FlightDeckTaskLabel}";
    }

    private static IEnumerable<ScreenFit> ScreenFits(FlightDeck deck, FlightDeckTask[] tasks)
    {
        int year = Singleton<SeaPower.Environment>.InstanceExists() ? Singleton<SeaPower.Environment>.Instance.Year : int.MaxValue;
        foreach (PendingLaunchTask held in tasks.OfType<PendingLaunchTask>())
        {
            if (held.launchAllowed || held.LaunchCount != 1 || held._ltp._missionType != FlightDeckTask.MissionType.None
                || held._ltp._waypoints.Count > 0 || held.FlightDeckTaskLabel.Contains("Aborting", StringComparison.OrdinalIgnoreCase)) continue;
            Loadout current = held.VehicleType.Loadouts[held._loadoutIndex];
            foreach (Loadout fit in held.VehicleType.Loadouts)
                if (!IsTransportFit(fit.Name) && (fit == current || fit.ServiceEntryYear <= year && ChangePreparedLoadout.Value != null))
                    yield return new(held.VehicleType, fit, held);
        }
        foreach (VehicleTypeOnBoard aircraft in deck._vehiclesOnBoard.Where(unit => IsHelicopter(unit) && unit.Numbers > 0))
            foreach (Loadout fit in aircraft.Loadouts)
                if (!IsTransportFit(fit.Name) && fit.ServiceEntryYear <= year && deck.CanLaunchVehicle(fit, 1)) yield return new(aircraft, fit, null);
    }

    private static JObject DescribeFit(ScreenFit fit) => new()
    {
        ["id"] = fit.Id, ["aircraft"] = fit.Aircraft.DisplayName, ["loadout"] = fit.Loadout.DisplayName,
        ["antiShip"] = fit.Loadout._loadoutCapabilities._hasAShM || fit.Loadout.Name.IndexOf("AntiShip", StringComparison.OrdinalIgnoreCase) >= 0,
        ["antiSubmarine"] = fit.Loadout._loadoutCapabilities._hasASWTorpedoes || fit.Loadout.Name.StartsWith("ASW", StringComparison.OrdinalIgnoreCase),
        ["stores"] = new JArray(fit.Loadout.AmmunitionAmounts.Where(ammo => ammo.Value > 0).Select(ammo => $"{ammo.Value}× {ammo.Key}")),
        ["preparing"] = fit.Held != null,
        ["requiresLoadoutChange"] = fit.Held is { } held && held.VehicleType.Loadouts[held._loadoutIndex] != fit.Loadout
    };

    private static bool IsHelicopter(VehicleTypeOnBoard? unit) => unit?._objectType == ObjectBase.ObjectType.Helicopter;
    private static bool LaunchesBlocked(FlightDeck deck) => deck.LaunchesDisabled || deck.DisabledByMission;
    private static IEnumerable<Helicopter> ScreenFlights(Taskforce side) => FlightUnits(side).OfType<Helicopter>()
        .Where(unit => !IsTransportFit(unit._obp._loadoutVariant));
    private static FlightDeckTask[] HelicopterTasks(FlightDeck deck) => deck.FlightDeckTasks.Concat(deck._flightDeckTasksToAdd)
        .Where(task => !task._isCancelled && IsHelicopter(task.VehicleType)
            && (task._ltp?._loadoutIndex is not int index || index < 0 || index >= task.VehicleType.Loadouts.Count
                || !IsTransportFit(task.VehicleType.Loadouts[index].Name))).Distinct().ToArray();

    private static void WatchLaunch(PendingLaunchTask task, Vessel ship, Loadout fit, bool useRadar) => task.Launched += (_, unit) =>
    {
        if (useRadar) unit.EnableSurfaceSearchRadars();
        UnityEngine.Debug.Log($"[Player flight deck] {ship.DisplayNameShort} launched {unit.DisplayNameShort} for a helicopter screen · {fit.DisplayName}");
    };
}
