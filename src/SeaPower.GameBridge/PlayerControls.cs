using System.Reflection;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPower.GameBridge;

internal static class PlayerControls
{
    internal static object PlayerView(string shipId)
    {
        if (PlayerPicture.Side is not { } side) return new { available = false, scope = "player" };
        if (shipId.Length > 0) return new { available = true, scope = "player", unit = PlayerPicture.Inspect(FindShip(side, shipId)) };
        JObject view = PlayerPicture.Read(side, PlayerPicture.Ships(side));
        view["available"] = true;
        view["airDecks"] = PlayerFlightDeck.AirOperations(side);
        view["airborne"] = PlayerFlightDeck.Airborne(side);
        view["airTargets"] = PlayerFlightDeck.AirTargets(side);
        if (PlayerBriefing.Read() is string briefing) view["briefing"] = briefing;
        if (Call("ReadPlanMarks") is JArray marks) view["planMarks"] = marks;
        return view;
    }

    internal static object Decisions() => Call("ReadStatus") ?? new { mode = "Unavailable" };

    internal static object StandingOrders(GameCommand command)
    {
        if (PlayerPicture.Side is not { } side) throw new InvalidOperationException("There is no active player task force.");
        if (command.Setting.Length == 0)
        {
            JArray orders = PlayerDoctrine.Read(side, command.Text.Length == 0 ? null : command.Text);
            if (orders.Count == 0) throw new ArgumentException("No player Standing Orders target has that ID.");
            return new { scope = "player", standingOrders = orders };
        }
        if (!command.Confirm || command.Text.Length == 0 || command.SettingValue.Length == 0)
            throw new ArgumentException("Changing a standing order requires a target, setting, value, and --confirm.");
        string result = PlayerDoctrine.Set(side, command.Text, command.Setting, command.SettingValue);
        if (result.StartsWith("Rejected", StringComparison.Ordinal)) throw new InvalidOperationException(result);
        return new { targetId = command.Text, settingId = command.Setting, result };
    }

    internal static object Waypoint(GameCommand command)
    {
        if (!command.Confirm || command.Latitude is not { } latitude || command.Longitude is not { } longitude)
            throw new ArgumentException("A native waypoint requires coordinates and --confirm.");
        if (PlayerPicture.Side is not { } side) throw new InvalidOperationException("There is no active player task force.");
        Vessel ship = FindShip(side, command.Text);
        var result = PlayerOrders.ReplaceWaypoint(ship, side, new GeoPosition { _latitude = latitude, _longitude = longitude });
        if (result.Disposition == WaypointDisposition.Rejected) throw new InvalidOperationException(result.Detail);
        return new { shipId = command.Text, result = result.Disposition.ToString(), detail = result.Detail };
    }

    private static Vessel FindShip(Taskforce side, string shipId) => PlayerPicture.Ships(side).FirstOrDefault(ship => PlayerPicture.Id(ship) == shipId)
        ?? throw new InvalidOperationException("No commandable player ship has that ID. Read player-view again.");

    private static object? Call(string method) => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(assembly => assembly.GetName().Name == "OODA")?.GetType("OODA.DecisionWindow")
        ?.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
}
