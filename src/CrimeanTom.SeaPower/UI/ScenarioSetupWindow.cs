using Noesis;
using SeaPower;
using SeapowerUI;
using UnityEngine;
using static CrimeanTom.SeaPower.NativeUi;

namespace CrimeanTom.SeaPower;

internal static class ScenarioSetupWindow
{
    private static FrameworkElement? _openPanel;

    private static readonly SolidColorBrush OverlayBrush =
        new(Noesis.Color.FromArgb(178, 10, 12, 14));

    internal static void Show(string scenarioPath)
    {
        if (_openPanel is not null)
        {
            WindowHostCommands.OpenWindowCommand.Execute(_openPanel, ScenarioMenuView.Instance);
            return;
        }

        bool route = ScenarioDebugGenerator.IsRouteOperationLauncherPath(scenarioPath);
        bool spectator = !route && ScenarioDebugGenerator.IsSpectatorLauncherPath(scenarioPath);
        ChoicePicker<FleetClashSize?> size = new(
            [("Random", null), ("Small", FleetClashSize.Small), ("Medium", FleetClashSize.Medium), ("Large", FleetClashSize.Large)]);
        ChoicePicker<string?> region = new(
            [("Random", null), .. FleetClashTheaterCatalog.RegionNames.Select(name => (name, (string?)name))]);
        ChoicePicker<int?> seaState = new(
            [("Random (calm strongly favored)", null), ("1 - Calm", 1), ("2", 2), ("3", 3), ("4", 4), ("5", 5), ("6 - Heavy", 6)], selectedIndex: 1);
        ChoicePicker<string?> commandSide = new(
            [("Random", null), ("Blue force", "Taskforce1"), ("Red force", "Taskforce2")]);
        ChoicePicker<double> enemyMultiplier = new(
            [("50%", 0.5d), ("75%", 0.75d), ("100%", 1d), ("125%", 1.25d), ("150%", 1.5d), ("200%", 2d)], 2, 1d);
        ChoicePicker<int> controlPercentage = new(
            [("25%", 25), ("50%", 50), ("75%", 75), ("100%", 100)], 3, 100);
        ChoicePicker<FleetControlPriority> controlPriority = new(
            [("Strongest units first", FleetControlPriority.StrongestFirst), ("Weakest units first", FleetControlPriority.WeakestFirst)]);
        ChoicePicker<RouteOperationVariant> variant = new(
            [("Civilian shipping", RouteOperationVariant.CivilianShipping), ("Supply group", RouteOperationVariant.SupplyGroup),
             ("Minesweeping group", RouteOperationVariant.MinesweepingGroup)]);
        ChoicePicker<RouteOperationPerspective> perspective = new(
            [("Escort", RouteOperationPerspective.Escort), ("Interdict", RouteOperationPerspective.Interdict)]);
        CheckBox civilianNavalTraffic = TrafficToggle(route ? "Background civilian naval traffic" : "Civilian naval traffic");
        CheckBox civilianAirTraffic = TrafficToggle(route ? "Background civilian air traffic" : "Civilian air traffic");
        TextBlock status = new()
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0f, 12f, 0f, 0f),
            Foreground = TextBrush,
        };

        StackPanel content = new() { Margin = new Thickness(24f) };
        content.Children.Add(new TextBlock
        {
            Text = route ? "GENERATED ROUTE OPERATION" : spectator ? "GENERATED FLEET CLASH - SPECTATOR" : "GENERATED FLEET CLASH",
            Foreground = TextBrush,
            FontSize = 25f,
            Margin = new Thickness(0f, 0f, 0f, 8f),
        });
        content.Children.Add(new TextBlock
        {
            Text = route ? "Protect a moving group or attack the same operation from the other side."
                : spectator ? "Build a fresh AI fleet clash and watch it from a Blue observer side."
                : "Build a fresh fleet clash. Random choices are rerolled every launch.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedTextBrush,
            FontSize = 16f,
            Margin = new Thickness(0f, 0f, 0f, 14f),
        });
        if (route)
        {
            AddField(content, "Protected group", variant.Control);
            AddField(content, "Perspective", perspective.Control);
        }
        AddField(content, "Force size", size.Control);
        AddField(content, "Region", region.Control);
        AddField(content, "Sea state", seaState.Control);
        AddField(content, spectator ? "Red force size" : "Enemy force size", enemyMultiplier.Control);
        if (!spectator)
        {
            if (!route) AddField(content, "Player command", commandSide.Control);
            AddField(content, "Units under your control", controlPercentage.Control);
            AddField(content, "Controlled unit priority", controlPriority.Control);
        }
        content.Children.Add(civilianNavalTraffic);
        content.Children.Add(civilianAirTraffic);
        content.Children.Add(new TextBlock
        {
            Text = route ? "The protected group follows its route under AI control. Background traffic is off by default."
                : "Civilian traffic is off by default. When enabled it adds neutral contacts and crossing routes.",
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
        Button launch = new() { Content = "Generate and launch", MinWidth = 190f };
        buttons.Children.Add(cancel);
        buttons.Children.Add(launch);
        content.Children.Add(buttons);

        Grid overlay = CreateOverlay(content);
        cancel.Click += (_, _) => Close(overlay);
        launch.Click += (_, _) =>
        {
            FleetClashGenerationOptions options = new()
            {
                Size = size.Value,
                Region = region.Value,
                SeaState = seaState.Value,
                PlayerTaskforce = route ? null : commandSide.Value,
                EnemySizeMultiplier = enemyMultiplier.Value,
                PlayerControlPercentage = controlPercentage.Value,
                ControlPriority = controlPriority.Value,
                RouteVariant = variant.Value,
                RoutePerspective = perspective.Value,
                Spectator = spectator,
                CivilianNavalTraffic = civilianNavalTraffic.IsChecked == true,
                CivilianAirTraffic = civilianAirTraffic.IsChecked == true,
            };

            status.Text = route ? "Generating route operation..." : "Generating forces...";
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

    private static CheckBox TrafficToggle(string label) => new()
    {
        Content = label,
        IsChecked = false,
        Margin = new Thickness(0f, 8f, 0f, 0f),
        Foreground = TextBrush,
    };

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