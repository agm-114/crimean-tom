using Noesis;
using SeapowerUI;
using UnityEngine;
using static CrimeanTom.SeaPower.NativeUi;

namespace CrimeanTom.SeaPower;

internal static class CampaignAutoresolveWindow
{
    private static FrameworkElement? _openPanel;

    internal static void Show()
    {
        if (CampaignOperationsState.HasPendingBattle)
        {
            CampaignManagementWindow.Show();
            return;
        }

        FrameworkElement? host = GetCampaignHost();
        if (host is null || !CampaignOperationsState.IsActiveCampaign)
        {
            return;
        }

        if (_openPanel is not null)
        {
            return;
        }

        StackPanel content = new()
        {
            Margin = new Thickness(26f),
        };
        content.Children.Add(CreateText("CAMPAIGN AUTORESOLVE", 26f, TextBrush));
        TextBlock explanation = CreateText(
            "Debug tool: writes a deterministic after-action result and opens the real post-battle management flow. It does not simulate tactical damage, ammunition, or combat duration.",
            14f,
            MutedTextBrush);
        explanation.TextWrapping = TextWrapping.Wrap;
        explanation.Margin = new Thickness(0f, 6f, 0f, 18f);
        content.Children.Add(explanation);

        Grid selectorRow = new()
        {
            Margin = new Thickness(0f, 0f, 0f, 18f),
        };
        selectorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1f, GridUnitType.Star) });
        selectorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selectorRow.Children.Add(CreateText("Contract to resolve", 16f, TextBrush));
        ComboBox mission = new()
        {
            Width = 260f,
            ItemsSource = new[]
            {
                "1  Fleet Clash",
                "2  Route Escort",
                "3  Route Interdiction",
                "4  Strait Passage",
            },
            SelectedIndex = 3,
        };
        Grid.SetColumn(mission, 1);
        selectorRow.Children.Add(mission);
        content.Children.Add(selectorRow);

        TextBlock status = CreateText(string.Empty, 14f, TextBrush);
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(0f, 12f, 0f, 0f);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Button cancel = new()
        {
            Content = "Cancel",
            MinWidth = 120f,
            Margin = new Thickness(0f, 0f, 8f, 0f),
        };
        Button defeat = new()
        {
            Content = "Simulate defeat",
            MinWidth = 160f,
            Margin = new Thickness(0f, 0f, 8f, 0f),
        };
        Button victory = new()
        {
            Content = "Simulate victory",
            MinWidth = 160f,
        };
        actions.Children.Add(cancel);
        actions.Children.Add(defeat);
        actions.Children.Add(victory);
        content.Children.Add(actions);
        content.Children.Add(status);

        Grid overlay = CreateCampaignOverlay(content, host, 600f, 760f);
        cancel.Click += (_, _) => CloseWindow(overlay, host, ref _openPanel);
        defeat.Click += (_, _) => Resolve(mission.SelectedIndex + 1, false, status, overlay, host);
        victory.Click += (_, _) => Resolve(mission.SelectedIndex + 1, true, status, overlay, host);
        _openPanel = overlay;
        WindowHostCommands.OpenWindowCommand.Execute(overlay, host);
    }

    private static void Resolve(
        int missionIndex,
        bool victory,
        TextBlock status,
        FrameworkElement panel,
        FrameworkElement host)
    {
        if (!CampaignOperationsState.DebugAutoresolve(missionIndex, victory, out string message))
        {
            status.Text = message;
            return;
        }

        status.Text = message + " Opening post-battle management...";
        CampaignDebugController.RunNextFrame(() =>
        {
            CloseWindow(panel, host, ref _openPanel);
            CampaignDebugController.RunNextFrame(CampaignManagementWindow.Show);
        });
    }
}