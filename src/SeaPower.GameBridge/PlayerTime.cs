using SeaPower;
using SeaPower.RollingReplay;

namespace SeaPower.GameBridge;

internal static class PlayerTime
{
    private static readonly float[] Speeds = [0.1f, 0.2f, 0.5f, 1, 2, 3, 5, 10, 20, 30, 50, 100];

    internal static bool Available => Singleton<GameMain>.InstanceExists() && Singleton<GameMain>.Instance._initialized
        && Singleton<InputHandler>.InstanceExists() && Singleton<MissionManager>.InstanceExists();
    internal static bool Blocked => !Available || Singleton<InputHandler>.Instance.menuOpen
        || Singleton<InputHandler>.Instance.IsInMessagePauseMode || RollingReplayManager.IsActive
        || Singleton<MissionManager>.Instance.MissionEnded;
    internal static float Maximum => Utils.openIniFile("", "/config.ini").readValue("General", "MaxTimeCompression", 100f);
    internal static float AtMost(float speed) => Speeds.LastOrDefault(value => value <= Math.Min(speed, Maximum));

    internal static bool TryPause()
    {
        if (Blocked || GameTime.IsPaused()) return false;
        GameTime.Pause();
        return GameTime.IsPaused();
    }

    internal static bool TryResume()
    {
        if (Blocked || !GameTime.IsPaused()) return false;
        GameTime.UnPause();
        return !GameTime.IsPaused();
    }

    internal static bool TrySetSpeed(float speed)
    {
        if (Blocked || !Speeds.Contains(speed) || speed > Maximum) return false;
        GameTime.SetTimeCompression(speed);
        return true;
    }
}
