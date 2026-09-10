using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPower.GameBridge;

internal static class ScenarioControls
{
    internal static bool Ready => MenuControls.Idle
        && Singleton<BackgroundSceneController>.InstanceExists()
        && Singleton<MenuSystemViewModel>.Instance.CurrentWindow is MainMenuView or ScenarioMenuView;

    internal static object Load(GameCommand command)
    {
        if (!command.Confirm) throw new InvalidOperationException("Scenario loading requires Confirm=true.");
        if (!Ready) throw new InvalidOperationException("Scenario loading requires the main or Scenarios menu, with no open dialog, mission or scene transition.");
        string path = command.Text;
        if (!Path.IsPathRooted(path) || !Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("Scenario must be an existing absolute .ini path.");
        IniHandler? mission = Utils.openIniFile(path, logErrors: false, useCaching: false);
        if (mission == null || !mission.doesSectionExist("Mission")) throw new ArgumentException("The selected file has no [Mission] section.");
        if (Singleton<MenuSystemViewModel>.Instance.CurrentWindow is not ScenarioMenuView)
            throw new InvalidOperationException("Open the Scenarios menu before submitting the load request.");
        Globals.currentMissionFilePath = path;
        // Same entry point as the native Scenarios Play button, including mod hooks.
        MissionManager.DoLoad();
        return new { ok = true, state = "load-requested", path };
    }

    internal static object Cancel(GameCommand command)
    {
        MenuControls.RequireConfirm(command);
        if (MenuControls.Loading || !TimeControls.Read().Available)
            throw new InvalidOperationException("Cancellation requires a fully loaded mission. For a setup dialog, inspect and click its Cancel button.");
        if (MenuControls.InCampaign || Singleton<Session>.Instance.EditorMission)
            throw new InvalidOperationException("Cancel through the native UI in campaign and editor sessions to preserve their return flow.");
        // Native confirmed pause-menu exit. No debrief result, campaign advancement or save.
        GameTime.Pause();
        Singleton<InputHandler>.Instance.menuOpen = true;
        MainGameViewModel.SetHudVisible(visible: false);
        PauseMenuView pause = new();
        Singleton<MenuSystemViewModel>.Instance.CurrentWindow = pause;
        var model = pause.DataContext as PauseMenuViewModel ?? new();
        pause.DataContext = model;
        model.InitialiseMissionPause();
        model.AcceptExit.Execute(null);
        return new { ok = true, state = "unload-requested" };
    }
}
