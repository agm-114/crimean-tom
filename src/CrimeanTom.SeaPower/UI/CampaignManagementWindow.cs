using Noesis;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;
using static CrimeanTom.SeaPower.NativeUi;

namespace CrimeanTom.SeaPower;

internal static class CampaignManagementWindow
{
    private static FrameworkElement? _openPanel;
    private static string _statusMessage = string.Empty;

    internal static void Show()
    {
        FrameworkElement? host = GetCampaignHost();
        if (host is null || !CampaignOperationsState.IsActiveCampaign)
        {
            return;
        }

        if (_openPanel is not null)
        {
            WindowHostCommands.OpenWindowCommand.Execute(_openPanel, host);
            return;
        }

        LinearCampaignTaskForceSnapshot snapshot = CampaignOperationsState.ReadCurrentSnapshot();
        TextBlock status = CreateText(_statusMessage, 14f, TextBrush);
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(0f, 12f, 0f, 0f);

        StackPanel content = new()
        {
            Margin = new Thickness(26f),
        };
        content.Children.Add(CreateText("POST-BATTLE MANAGEMENT", 26f, TextBrush));
        TextBlock summary = CreateText(
            $"Turn {CampaignOperationsState.Turn}  |  " +
            $"{CampaignOperationsState.SalvageShares} salvage shares  |  " +
            $"{snapshot.AvailablePoints}/{snapshot.CapPoints} task force points",
            15f,
            MutedTextBrush);
        summary.Margin = new Thickness(0f, 4f, 0f, 18f);
        content.Children.Add(summary);

        AddSectionHeading(content, "SALVAGE");
        content.Children.Add(CreateHint("Victory grants contract salvage shares. Recover at most one enemy hull; unused shares carry forward. Unclaimed hulls expire when management ends."));
        CampaignSalvageCandidate[] salvage = CampaignOperationsState.ReadPendingSalvage().ToArray();
        if (salvage.Length == 0)
        {
            content.Children.Add(CreateHint("No recoverable enemy hulls remain."));
        }
        foreach (CampaignSalvageCandidate candidate in salvage)
        {
            Button recover = new()
            {
                Content = $"Recover  {candidate.SalvageCost}",
                MinWidth = 140f,
                IsEnabled = CampaignOperationsState.SalvageShares >= candidate.SalvageCost,
            };
            recover.Click += (_, _) =>
            {
                CampaignOperationsState.TryRecover(candidate, out string message);
                Refresh(message);
            };
            content.Children.Add(CreateActionRow(
                candidate.DisplayName,
                $"Destroyed enemy hull  |  full value {candidate.FullPrice}",
                recover));
        }

        AddSectionHeading(content, "MARKET");
        CampaignUnitOffer[] offers = CampaignOperationsState.ReadMarketOffers().ToArray();
        if (offers.Length == 0)
        {
            content.Children.Add(CreateHint("No additional ships are offered this turn."));
        }
        foreach (CampaignUnitOffer offer in offers)
        {
            int price = CampaignOperationsState.GetMarketPrice(offer);
            Button buy = new()
            {
                Content = $"Buy  {price}",
                MinWidth = 140f,
                IsEnabled = snapshot.AvailablePoints >= price,
            };
            buy.Click += (_, _) =>
            {
                CampaignOperationsState.TryBuy(offer, out string message);
                Refresh(message);
            };
            content.Children.Add(CreateActionRow(
                offer.DisplayName,
                $"{offer.Nation.Replace('_', ' ')}  |  list {offer.FullPrice}, commander price {price}",
                buy));
        }

        AddSectionHeading(content, "TASK GROUPS");
        content.Children.Add(CreateHint(
            "Group assignments are free. Recovered ships need two turns to refit; purchased ships need one. Assign them to an active group now; they deploy only when ready. Reserve ships never deploy. If no ships are ready, finish management and try launching to open port management."));
        foreach (LinearCampaignTaskForceBuilderRosterItem item in snapshot.Items
                     .Where(item => !item.IsAirUnit)
                     .OrderBy(item => CampaignOperationsState.GetGroup(item.InstanceId))
                     .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            string[] groups = ["Group 1", "Group 2", "Reserve"];
            string currentGroup = CampaignOperationsState.GetGroup(item.InstanceId);
            ComboBox selector = new()
            {
                Width = 150f,
                ItemsSource = groups,
                SelectedIndex = Math.Max(
                    0,
                    Array.FindIndex(groups, group => string.Equals(
                        group,
                        currentGroup,
                        StringComparison.OrdinalIgnoreCase))),
            };
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedItem is string group &&
                    !string.Equals(group, currentGroup, StringComparison.OrdinalIgnoreCase))
                {
                    CampaignOperationsState.AssignGroup(item.InstanceId, group);
                    Refresh($"Moved {item.DisplayName} to {group}. Readiness is unchanged.");
                }
            };
            int fatigue = CampaignOperationsState.GetFatigue(item.InstanceId);
            content.Children.Add(CreateActionRow(
                item.DisplayName,
                fatigue > 0 ? $"Refit/workup: {fatigue} turns" : "Ready",
                selector));
        }

        content.Children.Add(status);
        if (!CampaignOperationsState.HasPendingBattle)
        {
            Button rest = new() { Content = "Wait one turn in port", IsEnabled = CampaignOperationsState.CanWaitInPort };
            rest.Click += (_, _) =>
            {
                CampaignOperationsState.WaitInPort();
                Refresh("One refit turn passed. No payment, salvage or new market stock was awarded.");
            };
            content.Children.Add(rest);
        }
        Button finish = new()
        {
            Content = CampaignOperationsState.HasPendingBattle ? "Finish management" : "Return to contracts",
            MinWidth = 190f,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0f, 20f, 0f, 0f),
        };
        content.Children.Add(finish);

        Grid overlay = CreateCampaignOverlay(content, host, 720f, 1000f, scrollable: true);
        finish.Click += (_, _) =>
        {
            CampaignOperationsState.CompleteManagement();
            _statusMessage = string.Empty;
            Close(overlay, host);
        };
        _openPanel = overlay;
        WindowHostCommands.OpenWindowCommand.Execute(overlay, host);
    }

    private static void Refresh(string message)
    {
        _statusMessage = message;
        FrameworkElement? host = GetCampaignHost();
        if (_openPanel is not null && host is not null)
        {
            Close(_openPanel, host);
        }
        _openPanel = null;
        CampaignDebugController.RunNextFrame(Show);
        Debug.Log($"[Crimean Tom Operations] {message}");
    }

    private static Border CreateActionRow(
        string title,
        string subtitle,
        FrameworkElement action)
    {
        Grid grid = new()
        {
            Margin = new Thickness(12f, 9f, 12f, 9f),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1f, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel labels = new();
        labels.Children.Add(CreateText(title, 16f, TextBrush));
        labels.Children.Add(CreateText(subtitle, 13f, MutedTextBrush));
        Grid.SetColumn(labels, 0);
        Grid.SetColumn(action, 1);
        action.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(labels);
        grid.Children.Add(action);
        return new Border
        {
            Child = grid,
            Background = RowBrush,
            Margin = new Thickness(0f, 0f, 0f, 6f),
        };
    }

    private static void AddSectionHeading(StackPanel content, string text)
    {
        TextBlock heading = CreateText(text, 17f, TextBrush);
        heading.Margin = new Thickness(0f, 14f, 0f, 8f);
        content.Children.Add(heading);
    }

    private static TextBlock CreateHint(string text)
    {
        TextBlock hint = CreateText(text, 13f, MutedTextBrush);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0f, 0f, 0f, 8f);
        return hint;
    }

    private static void Close(FrameworkElement panel, FrameworkElement host)
    {
        WindowHostCommands.CloseWindowCommand.Execute(panel, host);
        if (ReferenceEquals(_openPanel, panel))
        {
            _openPanel = null;
        }
    }
}