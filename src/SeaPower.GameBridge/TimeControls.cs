using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SeaPower;

namespace SeaPower.GameBridge;

internal static class TimeControls
{
    private static readonly float[] Speeds = [0.1f, 0.2f, 0.5f, 1, 2, 3, 5, 10, 20, 30, 50, 100];
    private static bool Available => Singleton<GameMain>.InstanceExists() && Singleton<GameMain>.Instance._initialized
        && Singleton<InputHandler>.InstanceExists() && Singleton<MissionManager>.InstanceExists();
    private static bool Blocked => !Available || Singleton<InputHandler>.Instance.menuOpen
        || Singleton<InputHandler>.Instance.IsInMessagePauseMode;

    internal static TimeStatus Read() => new(Available, Blocked, GameTime.IsPaused(), GameTime.TimeCompression,
        Available ? GameTime.CurrentMissionSessionTime : null,
        Available && Singleton<SceneCreator>.InstanceExists() ? Singleton<SceneCreator>.Instance._missionStartTime : null);

    internal static object Execute(GameCommand command)
    {
        if (Blocked) throw new InvalidOperationException("Time controls require a running mission with menus and blocking messages closed.");
        switch (command.Action)
        {
            case "pause":
                if (!GameTime.IsPaused()) GameTime.Pause();
                break;
            case "resume":
                if (GameTime.IsPaused()) GameTime.UnPause();
                break;
            case "set-speed":
                float maximum = Utils.openIniFile("", "/config.ini").readValue("General", "MaxTimeCompression", 100f);
                if (!Speeds.Contains(command.Speed) || command.Speed > maximum)
                    throw new ArgumentException("Choose a native speed between 0.1x and 100x, within the game's configured limit.");
                GameTime.SetTimeCompression(command.Speed);
                break;
        }
        return new { ok = true, action = command.Action, time = Read() };
    }
}

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
internal class TimeStatus(bool available, bool blocked, bool paused, float speed, float? elapsedSeconds, float? missionStartSeconds)
{
    public bool Available { get; } = available;
    public bool Blocked { get; } = blocked;
    public bool Paused { get; } = paused;
    public float Speed { get; } = speed;
    public float? ElapsedSeconds { get; } = elapsedSeconds;
    public float? MissionStartSeconds { get; } = missionStartSeconds;
}
