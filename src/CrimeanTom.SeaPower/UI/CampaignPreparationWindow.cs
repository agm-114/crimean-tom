using Noesis;
using SeapowerUI;
using static CrimeanTom.SeaPower.NativeUi;

namespace CrimeanTom.SeaPower;

internal static class CampaignPreparationWindow
{
    private static FrameworkElement? openPanel;
    private static object? confirmedMission;

    internal static bool ConfirmLaunch(object mission)
    {
        if (ReferenceEquals(confirmedMission, mission)) return true;
        FrameworkElement? host = GetCampaignHost();
        if (host is null) throw new InvalidOperationException("Campaign preparation has no active window.");
        if (openPanel is not null) return false;

        ScenarioStartingControls starting = new(CampaignOperationsState.StartingOptions);
        StackPanel content = new() { Margin = new Thickness(24) };
        content.Children.Add(CreateText("SORTIE PREPARATION", 25, TextBrush));
        content.Children.Add(CreateText(CampaignOperationsState.Difficulty.Label, 15, MutedTextBrush));
        starting.AddTo(content);
        Button launch = new() { Content = "Continue to launch", Margin = new Thickness(0, 18, 0, 6) };
        Button cancel = new() { Content = "Return to preparation" };
        content.Children.Add(launch);
        content.Children.Add(cancel);
        Grid panel = CreateCampaignOverlay(content, host, 540, 720, scrollable: true);
        cancel.Click += (_, _) => CloseWindow(panel, host, ref openPanel);
        launch.Click += (_, _) =>
        {
            CampaignOperationsState.SaveStartingOptions(starting.Value);
            CloseWindow(panel, host, ref openPanel);
            CampaignDebugController.RunNextFrame(() =>
            {
                confirmedMission = mission;
                try { NativeCampaignAccess.LaunchMission(mission); }
                finally { confirmedMission = null; }
            });
        };
        openPanel = panel;
        WindowHostCommands.OpenWindowCommand.Execute(panel, host);
        return false;
    }
}
