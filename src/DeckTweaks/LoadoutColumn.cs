using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeckTweaks.Preparation;
using HarmonyLib;
using Noesis;
using SeaPower;
using SeapowerUI;

namespace DeckTweaks;

[HarmonyPatch(typeof(MainGameView), MethodType.Constructor)]
internal static class LoadoutColumn
{
    private const string PickerTag = "DeckTweaks.Loadout";
    private const string StateTag = "DeckTweaks.State";
    private const string TimingTag = "DeckTweaks.Timing";
    private const float LoadoutWidth = 180;
    private static readonly ConditionalWeakTable<PendingLaunchTask, TaskLoadout> Rows = new();
    private static readonly ConditionalWeakTable<FrameworkElement, TaskLoadout> BoundRows = new();
    private static bool registered;

    private static void Postfix()
    {
        if (registered) return;
        EventManager.RegisterClassHandler(typeof(ListView), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnListLoaded));
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPickerLoaded));
        EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnCellLoaded));
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnOptionLoaded));
        registered = true;
    }

    private static void OnListLoaded(object sender, RoutedEventArgs args)
    {
        if (!Equals(sender, args.Source)) return;
        if (sender is not ListView { View: GridView view } list
            || list.ItemsSource is not ObservableCollection<FlightDeckTask>
            || view.Columns.Any(column => column.Header is Grid { Tag: TimingTag })) return;
        view.Columns[0].DisplayMemberBinding = null;
        view.Columns[0].CellTemplate = (DataTemplate)GUI.ParseXaml("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border Tag="DeckTweaks.State" Background="Transparent" Padding="4,2"/>
            </DataTemplate>
            """);
        GridViewColumn info = view.Columns[3];
        info.Header = GUI.ParseXaml("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Tag="DeckTweaks.Timing">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="180"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <TextBlock Text="Loadout" HorizontalAlignment="Center"/>
                <TextBlock Grid.Column="1" Text="Info" HorizontalAlignment="Center"/>
            </Grid>
            """);
        info.DisplayMemberBinding = null;
        info.CellTemplate = (DataTemplate)GUI.ParseXaml("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border Tag="DeckTweaks.Timing" VerticalAlignment="Center"/>
            </DataTemplate>
            """);
    }

    private static void OnPickerLoaded(object sender, RoutedEventArgs args)
    {
        if (!Equals(sender, args.Source)) return;
        if (sender is not ComboBox picker || picker.Tag as string != PickerTag) return;
        picker.DataContextChanged -= OnContextChanged;
        picker.DataContextChanged += OnContextChanged;
        picker.ToolTipOpening -= OnPickerToolTipOpening;
        picker.ToolTipOpening += OnPickerToolTipOpening;
        Bind(picker);
    }

    private static void OnPickerToolTipOpening(object sender, ToolTipEventArgs args)
    {
        if (sender is ComboBox { IsDropDownOpen: true } picker && args.Source is ComboBox source && source == picker)
            args.Handled = true;
    }

    private static TaskLoadout? Row(FrameworkElement element) => element.DataContext is PendingLaunchTask task
        ? Rows.GetValue(task, value => new(value)) : null;

    private static void OnCellLoaded(object sender, RoutedEventArgs args)
    {
        if (!Equals(sender, args.Source) || sender is not Border border) return;
        if (border.Tag as string == TimingTag)
        {
            if (border.Child == null) new TimingCell(border);
            return;
        }
        if (border.Tag as string != StateTag) return;
        if (border.Child == null) new StateCell(border);
    }

    private static void OnOptionLoaded(object sender, RoutedEventArgs args)
    {
        if (!Equals(sender, args.Source)) return;
        if (sender is not ComboBoxItem item || OptionPicker(item) == null) return;
        item.ToolTip ??= CreateToolTip();
        ConfigureToolTip(item);
        ToolTipService.SetPlacement(item, PlacementMode.Right);
        item.ToolTipOpening -= OnOptionToolTipOpening;
        item.ToolTipOpening += OnOptionToolTipOpening;
    }

    private static ComboBox? OptionPicker(ComboBoxItem item) =>
        ItemsControl.ItemsControlFromItemContainer(item) is ComboBox picker && picker.Tag as string == PickerTag ? picker : null;

    private static void OnOptionToolTipOpening(object sender, ToolTipEventArgs args)
    {
        if (sender is not ComboBoxItem item || OptionPicker(item) is not { } picker
            || !picker.IsDropDownOpen || Row(picker) is not { } row || item.Content is not Loadout loadout)
        {
            args.Handled = true;
            return;
        }
        if (item.ToolTip is ToolTip tooltip) SetToolTipLines(tooltip, row.Preview(loadout));
    }

    private static void OnContextChanged(object sender, DependencyPropertyChangedEventArgs args) => Bind((ComboBox)sender);
    private static void Bind(ComboBox picker)
    {
        TaskLoadout? row = Row(picker);
        if (row != null && BoundRows.TryGetValue(picker, out var bound) && bound == row) return;
        picker.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        BindStatus(picker, row);
        if (row == null) return;
        BindingOperations.ClearBinding(picker, Selector.SelectedItemProperty);
        picker.ItemsSource = row.Loadouts;
        picker.SelectedItem = row.SelectedLoadout;
        picker.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(TaskLoadout.SelectedLoadout)) { Source = row, Mode = BindingMode.TwoWay });
        picker.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(TaskLoadout.CanChange)) { Source = row });
    }

    private static void BindStatus(FrameworkElement element, TaskLoadout? row)
    {
        if (row != null && BoundRows.TryGetValue(element, out var bound) && bound == row) return;
        BoundRows.Remove(element);
        if (element.ToolTip is ToolTip previous) previous.IsOpen = false;
        element.ToolTip = null;
        if (row == null) return;
        BoundRows.Add(element, row);
        ToolTip tooltip = CreateToolTip();
        void Refresh(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(TaskLoadout.PreparationStatus)) SetToolTipLines(tooltip, row.PreparationStatus);
        }
        tooltip.Opened += (_, _) =>
        {
            SetToolTipLines(tooltip, row.PreparationStatus);
            row.PropertyChanged += Refresh;
        };
        tooltip.Closed += (_, _) => row.PropertyChanged -= Refresh;
        element.ToolTip = tooltip;
        ConfigureToolTip(element);
    }

    private static ToolTip CreateToolTip()
    {
        return new() { Content = new ScrollViewer { Content = CreateDetails(), MaxHeight = 480, MaxWidth = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    private static void SetToolTipLines(ToolTip tooltip, IEnumerable<PreparationLine> lines)
    {
        if (tooltip.Content is ScrollViewer { Content: Grid grid }) SetDetails(grid, lines);
    }

    private static Grid CreateDetails()
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        return grid;
    }

    private static void SetDetails(Grid grid, IEnumerable<PreparationLine> lines)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        foreach (PreparationLine line in lines)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new() { Height = line.Text.Length == 0 ? new GridLength(8) : GridLength.Auto });
            TextBlock text = new() { Text = line.Text, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(text, row);
            if (line.PerUnit == null && line.Completion == null) Grid.SetColumnSpan(text, 3);
            grid.Children.Add(text);
            AddTimeCell(grid, row, 1, line.PerUnit);
            AddTimeCell(grid, row, 2, line.Completion);
        }
    }

    private static void AddTimeCell(Grid grid, int row, int column, string? value)
    {
        if (value == null) return;
        TextBlock time = new() { Text = value, TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetRow(time, row);
        Grid.SetColumn(time, column);
        grid.Children.Add(time);
    }

    private static void ConfigureToolTip(FrameworkElement element)
    {
        ToolTipService.SetShowOnDisabled(element, true);
        ToolTipService.SetPlacement(element, PlacementMode.Right);
        ToolTipService.SetInitialShowDelay(element, 300);
        ToolTipService.SetBetweenShowDelay(element, 0);
        ToolTipService.SetShowDuration(element, 60000);
    }

    private class StateCell
    {
        private readonly TextBlock count = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button add = ResizeButton("DeckBatchAdd", "+", "Add an aircraft");
        private readonly Button remove = ResizeButton("DeckBatchRemove", "−", "Remove the last aircraft");
        private readonly StackPanel size = new() { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock label = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 220,
            VerticalAlignment = VerticalAlignment.Center };
        private FlightDeckTask? task;
        private TaskLoadout? row;

        internal StateCell(Border host)
        {
            Grid layout = new();
            layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(label, 1);
            StackPanel buttons = new() { Margin = new Thickness(3, 0, 0, 0) };
            buttons.Children.Add(add);
            buttons.Children.Add(remove);
            size.Children.Add(count);
            size.Children.Add(buttons);
            layout.Children.Add(size);
            layout.Children.Add(label);
            add.Click += (_, _) => { row?.AddAircraft(); Refresh(); };
            remove.Click += (_, _) => { row?.RemoveAircraft(); Refresh(); };
            size.MouseEnter += (_, _) => Refresh();
            host.Child = layout;
            host.Loaded += (_, _) => Bind(host, host.DataContext as FlightDeckTask);
            host.DataContextChanged += (_, _) => { if (host.IsLoaded) Bind(host, host.DataContext as FlightDeckTask); };
            host.Unloaded += (_, _) => Bind(host, null);
            Bind(host, host.DataContext as FlightDeckTask);
        }

        private static Button ResizeButton(string name, string symbol, string tooltip) => new()
        {
            Name = name, Content = symbol, ToolTip = tooltip, Width = 20, Height = 17, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };

        private void Bind(Border host, FlightDeckTask? next)
        {
            if (task != null) task.PropertyChanged -= RefreshLabel;
            if (row != null) row.PropertyChanged -= RefreshOptions;
            task = next;
            row = next == null ? null : Row(host);
            if (task != null) task.PropertyChanged += RefreshLabel;
            if (row != null) row.PropertyChanged += RefreshOptions;
            BindStatus(label, row);
            Refresh();
        }

        private void RefreshOptions(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(TaskLoadout.PreparationStatus)) Refresh();
        }

        private void RefreshLabel(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(FlightDeckTask.FlightDeckTaskLabel)) UpdateLabel();
        }

        private void Refresh()
        {
            size.Visibility = row?.CanResize == true ? Visibility.Visible : Visibility.Collapsed;
            add.IsEnabled = row?.CanAddAircraft == true;
            remove.IsEnabled = row?.CanRemoveAircraft == true;
            UpdateLabel();
        }

        private void UpdateLabel()
        {
            string text = task?.FlightDeckTaskLabel ?? "";
            count.Text = $"{row?.Count}x";
            string prefix = count.Text + " ";
            label.Text = size.Visibility == Visibility.Visible && !text.Contains('\n')
                && text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
        }
    }

    private class TimingCell
    {
        private static readonly ControlTemplate CaretTemplate = (ControlTemplate)GUI.ParseXaml("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
                <Border x:Name="Face" Background="Transparent">
                    <Path x:Name="Caret" Data="M 0,0 L 4,4 L 8,0" Stroke="#C8C9CB" StrokeThickness="1.5"
                          HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Face" Property="Background" Value="#494C4F"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Caret" Property="RenderTransform">
                            <Setter.Value><RotateTransform Angle="180"/></Setter.Value>
                        </Setter>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
            """);

        private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly ToggleButton toggle = new() { Name = "DeckTimingDetails", Template = CaretTemplate,
            Width = 18, Height = 20 };
        private readonly Grid details = CreateDetails();
        private readonly ScrollViewer scroll;
        private TaskLoadout? row;

        internal TimingCell(Border host)
        {
            summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(FlightDeckTask.Info)));
            scroll = new() { Content = details, MaxHeight = 280, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(4, 3, 4, 2), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            ComboBox picker = new() { Tag = PickerTag, DisplayMemberPath = "DisplayName",
                Margin = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            toggle.Checked += (_, _) => ToggleDetails(true);
            toggle.Unchecked += (_, _) => ToggleDetails(false);
            host.Child = new TimingLayout(picker, summary, scroll, toggle);
            host.Loaded += (_, _) => Bind(Row(host));
            host.DataContextChanged += (_, _) => { if (host.IsLoaded) Bind(Row(host)); };
            host.Unloaded += (_, _) => Bind(null);
            Bind(Row(host));
        }

        private void Bind(TaskLoadout? next)
        {
            if (ReferenceEquals(row, next) && toggle.ToolTip != null) return;
            if (row != null) row.PropertyChanged -= Refresh;
            row = next;
            if (row != null) row.PropertyChanged += Refresh;
            UpdateAvailability();
        }

        private void UpdateAvailability()
        {
            bool available = row?.CanShowTiming == true;
            toggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            bool expanded = available && row?.TimingExpanded == true;
            if (toggle.IsChecked != expanded) toggle.IsChecked = expanded;
            else SetExpanded(expanded);
        }

        private void ToggleDetails(bool expanded)
        {
            if (row != null) row.TimingExpanded = expanded;
            SetExpanded(expanded);
        }

        private void SetExpanded(bool expanded)
        {
            toggle.ToolTip = expanded ? "Hide timing details" : "Show timing details";
            summary.Visibility = expanded ? Visibility.Hidden : Visibility.Visible;
            scroll.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            BindStatus(summary, expanded || row?.CanShowTiming != true ? null : row);
            if (expanded && row != null)
            {
                scroll.ScrollToTop();
                SetDetails(details, row.PreparationStatus);
            }
            else SetDetails(details, []);
        }

        private void Refresh(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(TaskLoadout.PreparationStatus) || row == null) return;
            if ((toggle.Visibility == Visibility.Visible) != row.CanShowTiming) UpdateAvailability();
            else if (toggle.IsChecked == true) SetDetails(details, row.PreparationStatus);
        }
    }

    private class TimingLayout : Panel
    {
        private readonly ComboBox picker;
        private readonly TextBlock summary;
        private readonly ScrollViewer details;
        private readonly ToggleButton toggle;

        internal TimingLayout(ComboBox picker, TextBlock summary, ScrollViewer details, ToggleButton toggle)
        {
            this.picker = picker;
            this.summary = summary;
            this.details = details;
            this.toggle = toggle;
            Children.Add(picker);
            Children.Add(summary);
            Children.Add(details);
            Children.Add(toggle);
        }

        protected override Size MeasureOverride(Size available)
        {
            picker.Measure(new(LoadoutWidth, available.Height));
            summary.Measure(new(float.PositiveInfinity, available.Height));
            toggle.Measure(available);
            bool expanded = toggle.IsChecked == true;
            // The collapsed Loadout and ETA determine the width in both states.
            float width = Math.Min(LoadoutWidth + Math.Max(summary.DesiredSize.Width, toggle.DesiredSize.Width), available.Width);
            float height = Math.Max(picker.DesiredSize.Height, toggle.DesiredSize.Height);
            if (expanded)
            {
                details.Measure(new(width, Math.Max(0, available.Height - height)));
                height += details.DesiredSize.Height;
            }
            else
            {
                summary.Measure(new(Math.Max(0, width - LoadoutWidth - toggle.DesiredSize.Width), available.Height));
                height = Math.Max(height, summary.DesiredSize.Height);
            }
            return new(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            bool expanded = toggle.IsChecked == true;
            float pickerWidth = Math.Min(LoadoutWidth, finalSize.Width);
            float toggleX = Math.Max(pickerWidth, finalSize.Width - toggle.DesiredSize.Width);
            float headerHeight = expanded ? Math.Max(picker.DesiredSize.Height, toggle.DesiredSize.Height) : finalSize.Height;
            picker.Arrange(new(0, 0, pickerWidth, headerHeight));
            toggle.Arrange(new(toggleX, (headerHeight - toggle.DesiredSize.Height) / 2, toggle.DesiredSize.Width, toggle.DesiredSize.Height));
            if (expanded)
                details.Arrange(new(0, headerHeight, finalSize.Width, Math.Max(0, finalSize.Height - headerHeight)));
            else
                summary.Arrange(new(pickerWidth, 0, toggleX - pickerWidth, headerHeight));
            return finalSize;
        }
    }
}
