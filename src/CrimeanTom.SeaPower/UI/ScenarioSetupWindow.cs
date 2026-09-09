using Noesis;
using SeaPower;
using SeapowerUI;
using UnityEngine;
using HarmonyLib;
using System.Reflection;
using static CrimeanTom.SeaPower.NativeUi;

namespace CrimeanTom.SeaPower;

internal static class ScenarioSetupWindow
{
    private static FrameworkElement? _openPanel;
    private static readonly MethodInfo CreateMap = AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.MissionBriefViewModel"), "LoadGeneratedBriefingMapPane");

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
        bool search = ScenarioDebugGenerator.IsSearchLauncherPath(scenarioPath);
        ObjectiveOperation? operation = ScenarioDebugGenerator.ObjectiveFromPath(scenarioPath);
        bool spectator = ScenarioDebugGenerator.IsSpectatorLauncherPath(scenarioPath);
        ChoicePicker<ScenarioSpecialMode> specialMode = new(
            [("None", ScenarioSpecialMode.None), ("No missiles", ScenarioSpecialMode.NoMissiles)]);
        specialMode.Control.ToolTip = "Exclude dedicated anti-ship missile platforms and carriers. Defensive SAMs, ASW weapons, guns and torpedoes remain.";
        var regionChances = FleetClashTheaterCatalog.RegionChances(side => search
            ? ScenarioDebugGenerator.IsSearchSideViable(side) : FleetClashForceGenerator.IsViable(side));
        ChoicePicker<FleetClashSize?> size = new(
            [("Random", null), .. Enum.GetValues(typeof(FleetClashSize)).Cast<FleetClashSize>().Select(value =>
                (ChoiceLabel.Chance(value.ToString(), 1d / 3), (FleetClashSize?)value))]);
        ChoicePicker<string?> region = new(
            [("Random", null), .. regionChances.Select(pair => (ChoiceLabel.Chance(pair.Key, pair.Value), (string?)pair.Key))]);
        ChoicePicker<int?> seaState = new(
            [("Random", null), .. Enumerable.Range(1, 6).Select(value =>
                (ChoiceLabel.Chance(value == 1 ? "1 - Calm" : value == 6 ? "6 - Heavy" : value.ToString(),
                    ScenarioRandomization.Chance(ScenarioRandomization.SeaStateWeights, value - 1)), (int?)value))], selectedIndex: 1);
        ChoicePicker<string?> commandSide = new(
            search ? [("Hunter", "Taskforce1"), ("Hunted", "Taskforce2")]
                : operation == ObjectiveOperation.Passage ? [("Runner", "Taskforce1"), ("Blocker", "Taskforce2")]
                : operation is not null ? [("Attacker", "Taskforce1"), ("Defender", "Taskforce2")]
                : [("Random", null), (ChoiceLabel.Chance("Blue force", 0.5), "Taskforce1"), (ChoiceLabel.Chance("Red force", 0.5), "Taskforce2")]);
        ChoicePicker<AreaVariant> areaVariant = new([("Sea-lane control", AreaVariant.SeaLane), ("Anchorage defense", AreaVariant.Anchorage), ("Submarine bastion", AreaVariant.Bastion),
            ("Surface sweep", AreaVariant.SurfaceSweep), ("ASW sweep", AreaVariant.AswSweep), ("Chokepoint control", AreaVariant.Chokepoint),
            ("Port approach defense", AreaVariant.PortApproach), ("Relief of an isolated group", AreaVariant.Relief), ("Fleet-in-being patrol", AreaVariant.FleetInBeing)]);
        ChoicePicker<StrikeVariant> strikeVariant = new([("Radar sites", StrikeVariant.Radar), ("Coastal batteries", StrikeVariant.CoastalBattery), ("Air defenses", StrikeVariant.AirDefense),
            ("Port / anchorage raid", StrikeVariant.PortRaid), ("Airfield strike", StrikeVariant.Airfield), ("Fuel / ammunition depot", StrikeVariant.Depot),
            ("Forward base defense", StrikeVariant.ForwardBase), ("Ships rearming", StrikeVariant.RearmingShips), ("Ships repairing", StrikeVariant.RepairingShips),
            ("Seabed network shore terminal", StrikeVariant.CableTerminal), ("Communications site", StrikeVariant.Communications)]);
        ChoicePicker<PassageVariant> passageVariant = new([("Surface picket line", PassageVariant.SurfacePicket), ("Breakout", PassageVariant.Breakout), ("Submarines through an ASW barrier", PassageVariant.AswBarrier),
            ("Strait transit", PassageVariant.StraitTransit), ("Blockade run", PassageVariant.BlockadeRun), ("Carrier breakout", PassageVariant.CarrierBreakout),
            ("Raider breakout", PassageVariant.RaiderBreakout), ("Reinforcement interception", PassageVariant.Reinforcement), ("Rearguard withdrawal", PassageVariant.Rearguard)]);
        ChoicePicker<SearchVariant> searchVariant = new([("Submarine hunt", SearchVariant.Submarine), ("Ballistic submarine hunt", SearchVariant.BallisticSubmarine),
            ("Surface raider hunt", SearchVariant.SurfaceRaider), ("Isolated carrier hunt", SearchVariant.Carrier), ("Damaged group hunt", SearchVariant.DamagedGroup),
            ("Cruiser hunt", SearchVariant.Cruiser), ("Tender hunt", SearchVariant.Tender), ("Counter-hunt after an attack", SearchVariant.CounterHunt), ("False reports and decoys", SearchVariant.Decoys)]);
        searchVariant.Control.SelectionChanged += (_, _) =>
        {
            regionChances = FleetClashTheaterCatalog.RegionChances(side => ScenarioDebugGenerator.IsSearchSideViable(side, specialMode.Value, searchVariant.Value));
            region.UpdateLabels(value => value is null ? "Random" : ChoiceLabel.Chance(value, regionChances[value]));
        };
        ChoicePicker<double> enemyMultiplier = new(
            ScenarioDifficulty.Presets.Select(preset => (preset.Label, preset.Multiplier)).ToArray(), 2, 1d);
        ChoicePicker<FleetComposition> composition = new(
            [("Random", FleetComposition.Random), .. new[] { "Surface-focused", "Surface with submarine support", "Submarine-heavy", "Even mix: surface and submarines" }
                .Select((label, index) => (ChoiceLabel.Chance(label,
                    ScenarioRandomization.Chance(ScenarioRandomization.CompositionWeights, index)), (FleetComposition)(index + 1)))]);
        composition.Control.ToolTip = "Random roll chances. Unavailable or infeasible submarine profiles fall back to surface-focused forces.";
        ChoicePicker<int> controlPercentage = new(
            [("25%", 25), ("50%", 50), ("75%", 75), ("100%", 100)], 3, 100);
        ChoicePicker<FleetControlPriority> controlPriority = new(
            [("Strongest units first", FleetControlPriority.StrongestFirst), ("Weakest units first", FleetControlPriority.WeakestFirst)]);
        ChoicePicker<RouteOperationVariant> variant = new(
            [("Civilian shipping", RouteOperationVariant.CivilianShipping), ("Supply group", RouteOperationVariant.SupplyGroup),
             ("Minesweeping group", RouteOperationVariant.MinesweepingGroup), ("Amphibious group", RouteOperationVariant.AmphibiousGroup),
             ("High-value ship", RouteOperationVariant.HighValueShip), ("Evacuation shipping", RouteOperationVariant.EvacuationGroup),
             ("Damaged withdrawal group", RouteOperationVariant.DamagedGroup), ("Replenishment rendezvous", RouteOperationVariant.Replenishment)]);
        ChoicePicker<RouteOperationPerspective> perspective = new(
            [("Escort", RouteOperationPerspective.Escort), ("Interdict", RouteOperationPerspective.Interdict)]);
        void UpdateCompositionChances() => composition.UpdateLabels(value => value == FleetComposition.Random ? "Random" :
            ChoiceLabel.Chance(value switch
            {
                FleetComposition.SurfaceFocused => "Surface-focused",
                FleetComposition.Mixed => "Surface with submarine support",
                FleetComposition.SubmarineHeavy => "Submarine-heavy",
                _ => "Even mix: surface and submarines",
            }, ScenarioRandomization.Chance(route && (spectator || perspective.Value == RouteOperationPerspective.Escort)
                ? ScenarioRandomization.RouteAttackerCompositionWeights : ScenarioRandomization.CompositionWeights, (int)value - 1)));
        UpdateCompositionChances();
        perspective.Control.SelectionChanged += (_, _) => UpdateCompositionChances();
        ChoicePicker<RouteEncounterKind> encounter = new(
            [("Random", RouteEncounterKind.Random), ("Departure", RouteEncounterKind.Departure),
             ("Mid-crossing", RouteEncounterKind.MidCrossing), ("Port approach", RouteEncounterKind.PortApproach),
             ("Coastal transit", RouteEncounterKind.CoastalTransit), ("Chokepoint", RouteEncounterKind.Chokepoint)]);
        void UpdateEncounterChances()
        {
            IReadOnlyDictionary<string, double> selectedRegions = region.Value is { } selected
                ? new Dictionary<string, double> { [selected] = 1 } : regionChances;
            encounter.UpdateLabels(value => value == RouteEncounterKind.Random ? "Random" : ChoiceLabel.Chance(value switch
            {
                RouteEncounterKind.MidCrossing => "Mid-crossing",
                RouteEncounterKind.PortApproach => "Port approach",
                RouteEncounterKind.CoastalTransit => "Coastal transit",
                _ => value.ToString(),
            }, ShippingRoutes.EncounterChance(value, selectedRegions)));
        }
        if (route)
        {
            UpdateEncounterChances();
            region.Control.SelectionChanged += (_, _) => UpdateEncounterChances();
        }
        specialMode.Control.SelectionChanged += (_, _) =>
        {
            regionChances = FleetClashTheaterCatalog.RegionChances(side =>
                search ? ScenarioDebugGenerator.IsSearchSideViable(side, specialMode.Value, searchVariant.Value) : FleetClashForceGenerator.BuildPool(side, specialMode.Value)
                    .Any(unit => unit.IsRegional && !unit.IsSubmarine && (!route || !unit.IsCarrier)));
            region.UpdateLabels(value => value is null ? "Random" : ChoiceLabel.Chance(value, regionChances[value]));
            if (route) UpdateEncounterChances();
        };
        CheckBox civilianNavalTraffic = TrafficToggle(route ? "Background civilian naval traffic" : "Civilian naval traffic");
        CheckBox civilianAirTraffic = TrafficToggle(route ? "Background civilian air traffic" : "Civilian air traffic");
        CheckBox chaosMode = TrafficToggle("Chaos mode: random nation alignments");
        chaosMode.IsChecked = false;
        ScenarioStartingControls starting = new(new(), route);
        ScenarioActivityControls activity = new();
        TextBox seed = new() { Text = string.Empty };
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
            Text = operation is not null ? (operation == ObjectiveOperation.AreaControl ? "AREA CONTROL" : operation == ObjectiveOperation.Passage ? "PASSAGE" : "FIXED-TARGET STRIKE / DEFENSE") + (spectator ? " OBSERVER" : "")
                : search ? (spectator ? "HUNT OBSERVER" : "HUNT") : route ? (spectator ? "ROUTE OPERATION OBSERVER" : "GENERATED ROUTE OPERATION") : spectator ? "GENERATED FLEET CLASH - SPECTATOR" : "GENERATED FLEET CLASH",
            Foreground = TextBrush,
            FontSize = 25f,
            Margin = new Thickness(0f, 0f, 0f, 8f),
        });
        if (route)
        {
            AddField(content, "Protected group", variant.Control);
            if (!spectator) AddField(content, "Perspective", perspective.Control);
            AddField(content, "Encounter location", encounter.Control);
        }
        AddField(content, "Force size", size.Control);
        if (search) AddField(content, "Hunt target", searchVariant.Control);
        if (operation == ObjectiveOperation.AreaControl) AddField(content, "Objective", areaVariant.Control);
        if (operation == ObjectiveOperation.FixedTarget) AddField(content, "Targets", strikeVariant.Control);
        if (operation == ObjectiveOperation.Passage) AddField(content, "Passage", passageVariant.Control);
        AddField(content, "Region", region.Control);
        content.Children.Add(chaosMode);
        AddField(content, "Special mode", specialMode.Control);
        AddField(content, "Seed (blank = random)", seed);
        AddField(content, "Sea state", seaState.Control);
        AddField(content, spectator ? "Red force strength" : "Difficulty", enemyMultiplier.Control);
        if (!search) AddField(content, spectator ? "Red composition" : "Enemy composition", composition.Control);
        if (!spectator)
        {
            if (!route) AddField(content, "Player command", commandSide.Control);
            AddField(content, "Units under your control", controlPercentage.Control);
            AddField(content, "Controlled unit priority", controlPriority.Control);
            starting.AddTo(content);
        }
        content.Children.Add(civilianNavalTraffic);
        content.Children.Add(civilianAirTraffic);
        if (!search && operation is null) activity.AddTo(content);
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
        Button reroll = new() { Content = "Reroll", MinWidth = 90f, Margin = new Thickness(0, 0, 8, 0) };
        Button launch = new() { Content = "Launch", MinWidth = 110f, IsEnabled = false };
        buttons.Children.Add(cancel);
        buttons.Children.Add(reroll);
        buttons.Children.Add(launch);
        content.Children.Add(buttons);

        ContentControl preview = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid overlay = CreateOverlay(content, preview);
        cancel.Click += (_, _) => Close(overlay);
        int randomSeed = System.Environment.TickCount;
        int revision = 0;
        bool queued = false;
        float refreshAt = 0;
        void RefreshPreview()
        {
            int selectedSeed = randomSeed;
            if (!string.IsNullOrWhiteSpace(seed.Text))
            {
                if (!int.TryParse(seed.Text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsedSeed))
                {
                    status.Text = "Enter a whole-number seed between -2147483648 and 2147483647, or leave it blank.";
                    preview.Content = null;
                    return;
                }
                selectedSeed = parsedSeed;
            }
            FleetClashGenerationOptions options = new()
            {
                Seed = selectedSeed,
                Size = size.Value,
                Region = region.Value,
                ChaosMode = chaosMode.IsChecked == true,
                SpecialMode = specialMode.Value,
                SeaState = seaState.Value,
                PlayerTaskforce = route ? null : commandSide.Value,
                AreaVariant = areaVariant.Value,
                StrikeVariant = strikeVariant.Value,
                PassageVariant = passageVariant.Value,
                SearchVariant = searchVariant.Value,
                EnemySizeMultiplier = enemyMultiplier.Value,
                Starting = starting.Value,
                Activity = search || operation is not null ? ScenarioActivitySettings.Off : activity.Value,
                EnemyComposition = composition.Value,
                PlayerControlPercentage = controlPercentage.Value,
                ControlPriority = controlPriority.Value,
                RouteVariant = variant.Value,
                RoutePerspective = perspective.Value,
                RouteEncounter = encounter.Value,
                Spectator = spectator,
                CivilianNavalTraffic = civilianNavalTraffic.IsChecked == true,
                CivilianAirTraffic = civilianAirTraffic.IsChecked == true,
            };

            if (!ScenarioDebugGenerator.GenerateScenario(scenarioPath, options))
            {
                status.Text = ScenarioDebugGenerator.LastGenerationError ?? "Generation failed. Check Player.log for the exact error.";
                preview.Content = null;
                return;
            }
            try
            {
                IniHandler ini = Utils.openIniFile(scenarioPath, logErrors: true, useCaching: false);
                preview.Content = CreateMap.Invoke(null, [ini, "en"]);
                if (preview.Content is null) throw new InvalidOperationException("The briefing map could not be loaded.");
                status.Text = ini.readValue("Language_en", "Description", string.Empty).Replace("\\n", "\n");
                launch.IsEnabled = true;
            }
            catch (Exception exception)
            {
                preview.Content = null;
                status.Text = $"Preview failed: {exception.GetBaseException().Message}";
                Debug.LogError($"[Crimean Tom Operations] scenario preview failed: {exception}");
            }
        }
        void RefreshWhenReady()
        {
            if (_openPanel != overlay) return;
            if (Time.realtimeSinceStartup < refreshAt)
            {
                CampaignDebugController.RunNextFrame(RefreshWhenReady);
                return;
            }
            queued = false;
            RefreshPreview();
        }
        void SettingsChanged()
        {
            revision++;
            launch.IsEnabled = false;
            status.Text = "Updating preview...";
            refreshAt = Time.realtimeSinceStartup + 0.35f;
            if (queued) return;
            queued = true;
            CampaignDebugController.RunNextFrame(RefreshWhenReady);
        }
        void Watch(FrameworkElement element)
        {
            if (element is ComboBox choice) choice.SelectionChanged += (_, _) => SettingsChanged();
            else if (element is CheckBox toggle)
            {
                toggle.Checked += (_, _) => SettingsChanged();
                toggle.Unchecked += (_, _) => SettingsChanged();
            }
            else if (element is TextBox text) text.TextChanged += (_, _) => SettingsChanged();
            else if (element is Panel panel)
                foreach (FrameworkElement child in panel.Children) Watch(child);
        }
        Watch(content);
        reroll.Click += (_, _) =>
        {
            randomSeed = HashCode.Combine(randomSeed, ++revision, System.Environment.TickCount);
            seed.Text = string.Empty;
            SettingsChanged();
        };
        launch.Click += (_, _) =>
        {
            if (!launch.IsEnabled) return;
            Close(overlay);
            GeneratedScenarioLoadPatch.LoadGeneratedScenario();
        };
        WindowHostCommands.OpenWindowCommand.Execute(overlay, ScenarioMenuView.Instance);
        SettingsChanged();
    }

    private static CheckBox TrafficToggle(string label) => new()
    {
        Content = label,
        IsChecked = true,
        Margin = new Thickness(0f, 8f, 0f, 0f),
        Foreground = TextBrush,
    };

    private static Grid CreateOverlay(StackPanel content, ContentControl preview)
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
        Grid body = new();
        bool wide = hostWidth >= 1100;
        if (wide)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(570) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(preview, 1);
        }
        else
        {
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(280) });
            Grid.SetRow(preview, 1);
        }
        body.Children.Add(scroll);
        body.Children.Add(preview);
        Border dialog = new()
        {
            Child = body,
            Width = Math.Max(500f, hostWidth - 48f),
            Height = Math.Max(420f, hostHeight - 48f),
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
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
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
