using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SeaPower;

namespace SeaPower.GameBridge;

internal static class TimeControls
{
    internal static TimeStatus Read() => new(PlayerTime.Available, PlayerTime.Blocked, GameTime.IsPaused(), GameTime.TimeCompression,
        PlayerTime.Available ? GameTime.missionSessionTime : null,
        PlayerTime.Available && Singleton<SceneCreator>.InstanceExists() ? Singleton<SceneCreator>.Instance._missionStartTime : null);

    internal static object Execute(GameCommand command)
    {
        if (PlayerTime.Blocked) throw new InvalidOperationException("Time controls require a running mission with menus and blocking messages closed.");
        switch (command.Action)
        {
            case "pause":
                PlayerTime.TryPause();
                break;
            case "resume":
                PlayerTime.TryResume();
                break;
            case "set-speed":
                if (!PlayerTime.TrySetSpeed(command.Speed))
                    throw new ArgumentException("Choose a native speed between 0.1x and 100x, within the game's configured limit.");
                break;
        }
        return new { ok = true, action = command.Action, time = Read() };
    }
}

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class TimeStatus(bool available, bool blocked, bool paused, float speed, double? elapsedSeconds, double? missionStartSeconds)
{
    public bool Available { get; } = available;
    public bool Blocked { get; } = blocked;
    public bool Paused { get; } = paused;
    public float Speed { get; } = speed;
    public double? ElapsedSeconds { get; } = elapsedSeconds;
    public double? MissionStartSeconds { get; } = missionStartSeconds;
}
