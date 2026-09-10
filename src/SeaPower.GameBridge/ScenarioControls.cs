using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPower.GameBridge;

internal static class ScenarioControls
{
    private static bool requested;
    internal static bool Ready => !requested && !TimeControls.Read().Available
        && Singleton<BackgroundSceneController>.InstanceExists() && Singleton<SceneCreator>.InstanceExists()
        && Singleton<LoadScreen>.InstanceExists() && Singleton<MenuSystemViewModel>.InstanceExists()
        && Singleton<MenuSystemViewModel>.Instance.CurrentWindow is MainMenuView { IsVisible: true, IsEnabled: true };

    internal static object Load(GameCommand command)
    {
        if (!command.Confirm) throw new InvalidOperationException("Scenario loading requires Confirm=true.");
        if (!Ready) throw new InvalidOperationException("Scenario loading requires the ready main menu and no active mission or previous load request.");
        string path = command.Text;
        if (!Path.IsPathRooted(path) || !Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("Scenario must be an existing absolute .ini path.");
        IniHandler? mission = Utils.openIniFile(path, logErrors: false, useCaching: false);
        if (mission == null || !mission.doesSectionExist("Mission")) throw new ArgumentException("The selected file has no [Mission] section.");
        requested = true;
        Globals.currentMissionFilePath = path;
        // Same entry point as the native Scenarios Play button, including mod hooks.
        MissionManager.DoLoad();
        return new { ok = true, state = "load-requested", path };
    }
}
