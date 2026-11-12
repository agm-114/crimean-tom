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
        if (PlayerBriefing.Read() is string briefing) view["briefing"] = briefing;
        return view;
    }

    internal static object Decisions() => Call("OODA.DecisionWindow", "ReadStatus") ?? new { mode = "Unavailable" };

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

    private static object? Call(string type, string method) => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(assembly => assembly.GetName().Name == "OODA")?.GetType(type)
        ?.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
}
