using System;

using Noesis;

using SeaPower;

using SeapowerUI;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static class ScenarioSetupWindow
{
    private static FrameworkElement? _openPanel;

    private static readonly SolidColorBrush OverlayBrush =
        new(Noesis.Color.FromArgb(178, 10, 12, 14));

    private static readonly SolidColorBrush PanelBrush =
        new(Noesis.Color.FromArgb(255, 35, 38, 41));

    private static readonly SolidColorBrush BorderBrush =
        new(Noesis.Color.FromArgb(255, 87, 95, 101));

    private static readonly SolidColorBrush TextBrush =
        new(Noesis.Color.FromArgb(255, 232, 234, 235));

    private static readonly SolidColorBrush MutedTextBrush =
        new(Noesis.Color.FromArgb(255, 164, 171, 176));

    internal static void Show(string scenarioPath)
    {
        if (_openPanel is not null)
        {
            WindowHostCommands.OpenWindowCommand.Execute(_openPanel, ScenarioMenuView.Instance);
            return;
        }

        if (ScenarioDebugGenerator.IsRouteOperationLauncherPath(scenarioPath))
        {
            ShowRouteOperation(scenarioPath);
            return;
        }

        ShowFleetClash(scenarioPath);
    }

    private static void ShowFleetClash(string scenarioPath)
    {
        bool spectator = ScenarioDebugGenerator.IsSpectatorLauncherPath(scenarioPath);
        ComboBox size = CreateComboBox(["Random", "Small", "Medium", "Large"]);
        ComboBox region = CreateComboBox(
            ["Random", .. FleetClashTheaterCatalog.RegionNames]);
        ComboBox seaState = CreateComboBox(
            ["Random (calm strongly favored)", "1 - Calm", "2", "3", "4", "5", "6 - Heavy"],
            selectedIndex: 1);
        ComboBox commandSide = CreateComboBox(["Random", "Blue force", "Red force"]);
        ComboBox enemyMultiplier = CreateComboBox(
            ["50%", "75%", "100%", "125%", "150%", "200%"],
            selectedIndex: 2);
        ComboBox controlPercentage = CreateComboBox(
            ["25%", "50%", "75%", "100%"],
            selectedIndex: 3);
        ComboBox controlPriority = CreateComboBox(
            ["Strongest units first", "Weakest units first"]);
        CheckBox civilianNavalTraffic = new()
        {
            Content = "Civilian naval traffic",
            IsChecked = false,
            Margin = new Thickness(0f, 8f, 0f, 0f),
            Foreground = TextBrush,
        };
        CheckBox civilianAirTraffic = new()
        {
            Content = "Civilian air traffic",
            IsChecked = false,
            Margin = new Thickness(0f, 8f, 0f, 0f),
            Foreground = TextBrush,
        };
        TextBlock status = new()
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0f, 12f, 0f, 0f),
            Foreground = TextBrush,
        };

        StackPanel content = new()
        {
            Margin = new Thickness(24f),
        };
        content.Children.Add(new TextBlock
        {
            Text = spectator
                ? "GENERATED FLEET CLASH - SPECTATOR"
                : "GENERATED FLEET CLASH",
            Foreground = TextBrush,
            FontSize = 25f,
            Margin = new Thickness(0f, 0f, 0f, 8f),
        });
        content.Children.Add(new TextBlock
        {
            Text = spectator
                ? "Build a fresh AI fleet clash and watch it from a Blue observer side."
                : "Build a fresh fleet clash. Random choices are rerolled every launch.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedTextBrush,
            FontSize = 16f,
            Margin = new Thickness(0f, 0f, 0f, 14f),
        });
        AddField(content, "Force size", size);
        AddField(content, "Region", region);
        AddField(content, "Sea state", seaState);
        AddField(content, spectator ? "Red force size" : "Enemy force size", enemyMultiplier);
        if (!spectator)
        {
            AddField(content, "Player command", commandSide);
            AddField(content, "Units under your control", controlPercentage);
            AddField(content, "Controlled unit priority", controlPriority);
        }
        content.Children.Add(civilianNavalTraffic);
        content.Children.Add(civilianAirTraffic);
        content.Children.Add(new TextBlock
        {
            Text = "Civilian traffic is off by default. When enabled it adds neutral contacts and crossing routes.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0f, 8f, 0f, 0f),
            Foreground = MutedTextBrush,
        });
        content.Children.Add(status);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0f, 20f, 0f, 0f),
        };
        Button cancel = new()
        {
            Content = "Cancel",
            MinWidth = 110f,
            Margin = new Thickness(0f, 0f, 8f, 0f),
        };
        Button launch = new()
        {
            Content = "Generate and launch",
            MinWidth = 190f,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(launch);
        content.Children.Add(buttons);

        Grid overlay = CreateOverlay(content);

        cancel.Click += (_, _) => Close(overlay);
        launch.Click += (_, _) =>
        {
            FleetClashGenerationOptions options = new()
            {
                Size = size.SelectedIndex switch
                {
                    1 => FleetClashSize.Small,
                    2 => FleetClashSize.Medium,
                    3 => FleetClashSize.Large,
                    _ => null,
                },
                Region = region.SelectedIndex > 0
                    ? FleetClashTheaterCatalog.RegionNames[region.SelectedIndex - 1]
                    : null,
                SeaState = seaState.SelectedIndex > 0 ? seaState.SelectedIndex : null,
                PlayerTaskforce = commandSide.SelectedIndex switch
                {
                    1 => "Taskforce1",
                    2 => "Taskforce2",
                    _ => null,
                },
                EnemySizeMultiplier = enemyMultiplier.SelectedIndex switch
                {
                    0 => 0.5d,
                    1 => 0.75d,
                    3 => 1.25d,
                    4 => 1.5d,
                    5 => 2d,
                    _ => 1d,
                },
                PlayerControlPercentage = controlPercentage.SelectedIndex switch
                {
                    0 => 25,
                    1 => 50,
                    2 => 75,
                    _ => 100,
                },
                ControlPriority = controlPriority.SelectedIndex == 1
                    ? FleetControlPriority.WeakestFirst
                    : FleetControlPriority.StrongestFirst,
                Spectator = spectator,
                CivilianNavalTraffic = civilianNavalTraffic.IsChecked == true,
                CivilianAirTraffic = civilianAirTraffic.IsChecked == true,
            };

            status.Text = "Generating forces...";
            launch.IsEnabled = false;
            if (!ScenarioDebugGenerator.GenerateScenario(scenarioPath, options))
            {
                status.Text = "Generation failed. Check Player.log for the exact error.";
                launch.IsEnabled = true;
                return;
            }

            Close(overlay);
            GeneratedScenarioLoadPatch.LoadGeneratedScenario();
        };

        WindowHostCommands.OpenWindowCommand.Execute(overlay, ScenarioMenuView.Instance);
    }

    private static void ShowRouteOperation(string scenarioPath)
    {
        ComboBox variant = CreateComboBox(
            ["Civilian shipping", "Supply group", "Minesweeping group"]);
        ComboBox perspective = CreateComboBox(["Escort", "Interdict"]);
        ComboBox size = CreateComboBox(["Random", "Small", "Medium", "Large"]);
        ComboBox region = CreateComboBox(
            ["Random", .. FleetClashTheaterCatalog.RegionNames]);
        ComboBox seaState = CreateComboBox(
            ["Random (calm strongly favored)", "1 - Calm", "2", "3", "4", "5", "6 - Heavy"],
            selectedIndex: 1);
        ComboBox enemyMultiplier = CreateComboBox(
            ["50%", "75%", "100%", "125%", "150%", "200%"],
            selectedIndex: 2);
        ComboBox controlPercentage = CreateComboBox(
            ["25%", "50%", "75%", "100%"],
            selectedIndex: 3);
        ComboBox controlPriority = CreateComboBox(
            ["Strongest units first", "Weakest units first"]);
        CheckBox civilianNavalTraffic = new()
        {
            Content = "Background civilian naval traffic",
            IsChecked = false,
            Margin = new Thickness(0f, 8f, 0f, 0f),
            Foreground = TextBrush,
        };
        CheckBox civilianAirTraffic = new()
        {
            Content = "Background civilian air traffic",
            IsChecked = false,
            Margin = new Thickness(0f, 8f, 0f, 0f),
            Foreground = TextBrush,
        };
        TextBlock status = new()
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0f, 12f, 0f, 0f),
            Foreground = TextBrush,
        };

        StackPanel content = new()
        {
            Margin = new Thickness(24f),
        };
        content.Children.Add(new TextBlock
        {
            Text = "GENERATED ROUTE OPERATION",
            Foreground = TextBrush,
            FontSize = 25f,
            Margin = new Thickness(0f, 0f, 0f, 8f),
        });
        content.Children.Add(new TextBlock
        {
            Text = "Protect a moving group or attack the same operation from the other side.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedTextBrush,
            FontSize = 16f,
            Margin = new Thickness(0f, 0f, 0f, 14f),
        });
        AddField(content, "Protected group", variant);
        AddField(content, "Perspective", perspective);
        AddField(content, "Force size", size);
        AddField(content, "Region", region);
        AddField(content, "Sea state", seaState);
        AddField(content, "Enemy force size", enemyMultiplier);
        AddField(content, "Units under your control", controlPercentage);
        AddField(content, "Controlled unit priority", controlPriority);
        content.Children.Add(civilianNavalTraffic);
        content.Children.Add(civilianAirTraffic);
        content.Children.Add(new TextBlock
        {
            Text = "The protected group follows its route under AI control. Background traffic is off by default.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0f, 8f, 0f, 0f),
            Foreground = MutedTextBrush,
        });
        content.Children.Add(status);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0f, 20f, 0f, 0f),
        };
        Button cancel = new()
        {
            Content = "Cancel",
            MinWidth = 110f,
            Margin = new Thickness(0f, 0f, 8f, 0f),
        };
        Button launch = new()
        {
            Content = "Generate and launch",
            MinWidth = 190f,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(launch);
        content.Children.Add(buttons);

        Grid overlay = CreateOverlay(content);
        cancel.Click += (_, _) => Close(overlay);
        launch.Click += (_, _) =>
        {
            FleetClashGenerationOptions options = new()
            {
                RouteVariant = variant.SelectedIndex switch
                {
                    1 => RouteOperationVariant.SupplyGroup,
                    2 => RouteOperationVariant.MinesweepingGroup,
                    _ => RouteOperationVariant.CivilianShipping,
                },
                RoutePerspective = perspective.SelectedIndex == 1
                    ? RouteOperationPerspective.Interdict
                    : RouteOperationPerspective.Escort,
                Size = size.SelectedIndex switch
                {
                    1 => FleetClashSize.Small,
                    2 => FleetClashSize.Medium,
                    3 => FleetClashSize.Large,
                    _ => null,
                },
                Region = region.SelectedIndex > 0
                    ? FleetClashTheaterCatalog.RegionNames[region.SelectedIndex - 1]
                    : null,
                SeaState = seaState.SelectedIndex > 0 ? seaState.SelectedIndex : null,
                EnemySizeMultiplier = enemyMultiplier.SelectedIndex switch
                {
                    0 => 0.5d,
                    1 => 0.75d,
                    3 => 1.25d,
                    4 => 1.5d,
                    5 => 2d,
                    _ => 1d,
                },
                PlayerControlPercentage = controlPercentage.SelectedIndex switch
                {
                    0 => 25,
                    1 => 50,
                    2 => 75,
                    _ => 100,
                },
                ControlPriority = controlPriority.SelectedIndex == 1
                    ? FleetControlPriority.WeakestFirst
                    : FleetControlPriority.StrongestFirst,
                CivilianNavalTraffic = civilianNavalTraffic.IsChecked == true,
                CivilianAirTraffic = civilianAirTraffic.IsChecked == true,
            };

            status.Text = "Generating route operation...";
            launch.IsEnabled = false;
            if (!ScenarioDebugGenerator.GenerateScenario(scenarioPath, options))
            {
                status.Text = "Generation failed. Check Player.log for the exact error.";
                launch.IsEnabled = true;
                return;
            }

            Close(overlay);
            GeneratedScenarioLoadPatch.LoadGeneratedScenario();
        };

        WindowHostCommands.OpenWindowCommand.Execute(overlay, ScenarioMenuView.Instance);
    }

    private static Grid CreateOverlay(StackPanel content)
    {
        float hostWidth = ScenarioMenuView.Instance.ActualWidth > 0f
            ? ScenarioMenuView.Instance.ActualWidth
            : Screen.width;
        float hostHeight = ScenarioMenuView.Instance.ActualHeight > 0f
            ? ScenarioMenuView.Instance.ActualHeight
            : Screen.height;
        ScrollViewer scroll = new()
        {
            Content = content,
            MaxHeight = Math.Max(420f, hostHeight - 48f),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Border dialog = new()
        {
            Child = scroll,
            Width = Math.Min(760f, Math.Max(500f, hostWidth - 48f)),
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid overlay = new()
        {
            Width = hostWidth,
            Height = hostHeight,
            Background = OverlayBrush,
        };
        overlay.Children.Add(dialog);
        _openPanel = overlay;
        return overlay;
    }

    private static ComboBox CreateComboBox(string[] items, int selectedIndex = 0) => new()
    {
        ItemsSource = items,
        SelectedIndex = selectedIndex,
        MinWidth = 330f,
    };

    private static void AddField(StackPanel content, string label, Control control)
    {
        Grid row = new()
        {
            Margin = new Thickness(0f, 4f, 0f, 6f),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(170f),
        });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1f, GridUnitType.Star),
        });
        TextBlock text = new()
        {
            Text = label,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        row.Children.Add(text);
        row.Children.Add(control);
        content.Children.Add(row);
    }

    private static void Close(FrameworkElement panel)
    {
        WindowHostCommands.CloseWindowCommand.Execute(panel, ScenarioMenuView.Instance);
        _openPanel = null;
    }
}
