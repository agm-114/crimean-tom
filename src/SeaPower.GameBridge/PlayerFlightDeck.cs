using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPower.GameBridge;

internal static class PlayerFlightDeck
{
    internal static JArray Helicopters(Taskforce side, Vessel[] ships)
    {
        Helicopter[] flights = PlayerPicture.Units(side).OfType<Helicopter>().Where(unit => unit._isInFlight).ToArray();
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
                ["deckTasks"] = new JArray(tasks.Select(task => new JObject
                {
                    ["type"] = task.AircraftType, ["state"] = task.FlightDeckTaskLabel, ["preparation"] = task.Info,
                    ["launchAuthorized"] = task is PendingLaunchTask pending ? pending.launchAllowed : null
                })),
                ["airborne"] = new JArray(airborne.Select(unit => new JObject
                {
                    ["id"] = PlayerPicture.Id(unit), ["name"] = unit.DisplayNameShort, ["order"] = unit.CurrentOrderText.Value
                }))
            });
        }
        return result;
    }

    internal static string QueueHelicopterScreen(Vessel ship, Taskforce side, GeoPosition[] route, bool useRadar)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side || !PlayerPicture.CanCommand(ship, side)
            || ship._obp?._flightDeck is not { } deck || LaunchesBlocked(deck)) return "Rejected · deck launches unavailable";
        if (PlayerPicture.Units(side).OfType<Helicopter>().Any(unit => unit._isInFlight && unit.getHomeBase() == ship))
            return "Kept · a helicopter from this ship is airborne";

        FlightDeckTask[] tasks = HelicopterTasks(deck);
        if (tasks.Any(task => task is LaunchTask || task is PendingLaunchTask { launchAllowed: true }))
            return "Kept · a helicopter launch is already queued";
        PendingLaunchTask? held = tasks.OfType<PendingLaunchTask>().FirstOrDefault(task => task.LaunchCount == 1
            && task._ltp._missionType == FlightDeckTask.MissionType.None && task._ltp._waypoints.Count == 0
            && !task.FlightDeckTaskLabel.Contains("Aborting", StringComparison.OrdinalIgnoreCase));
        if (held != null)
        {
            held._ltp._waypoints.AddRange(route);
            WatchLaunch(held, ship, useRadar);
            held.ToggleLaunchAllowedFunc(null);
            return $"Launch authorized · {held.AircraftType} · {held.FlightDeckTaskLabel} · {held.Info}";
        }

        foreach (VehicleTypeOnBoard aircraft in deck._vehiclesOnBoard.Where(unit => IsHelicopter(unit) && unit.Numbers > 0))
        {
            Squadron? squadron = aircraft.Squadrons.FirstOrDefault(unit => unit.Numbers > 0 && unit.Callsigns.Count > 0);
            Loadout? loadout = aircraft.Loadouts.OrderBy(unit => unit.Name.StartsWith("ASW", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(unit => deck.CanLaunchVehicle(unit, 1));
            if (squadron == null || loadout == null) continue;
            LaunchTaskParameters parameters = new() { _waypoints = route.ToList() };
            PendingLaunchTask? task = deck.createLaunchTask(aircraft, loadout, squadron, squadron.Callsigns[0], parameters);
            if (task == null) continue;
            WatchLaunch(task, ship, useRadar);
            return $"Screen queued · {aircraft.DisplayName} · {loadout.DisplayName} · {task.FlightDeckTaskLabel}";
        }
        return "Rejected · no available helicopter and loadout";
    }

    private static bool IsHelicopter(VehicleTypeOnBoard? unit) => unit?._objectType == ObjectBase.ObjectType.Helicopter;
    private static bool LaunchesBlocked(FlightDeck deck) => deck.LaunchesDisabled || deck.DisabledByMission;
    private static FlightDeckTask[] HelicopterTasks(FlightDeck deck) => deck.FlightDeckTasks.Concat(deck._flightDeckTasksToAdd)
        .Where(task => !task._isCancelled && IsHelicopter(task.VehicleType)).Distinct().ToArray();

    private static void WatchLaunch(PendingLaunchTask task, Vessel ship, bool useRadar) => task.Launched += (_, unit) =>
    {
        if (useRadar) unit.EnableSurfaceSearchRadars();
        UnityEngine.Debug.Log($"[Player flight deck] {ship.DisplayNameShort} launched {unit.DisplayNameShort} for a helicopter screen");
    };
}
