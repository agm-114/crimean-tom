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
            new RoutedEventHandler(OnStateLoaded));
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnOptionLoaded));
        registered = true;
    }

    private static void OnListLoaded(object sender, RoutedEventArgs args)
    {
        if (!Equals(sender, args.Source)) return;
        if (sender is not ListView { View: GridView view } list
            || list.ItemsSource is not ObservableCollection<FlightDeckTask>
            || view.Columns.Any(column => column.Header as string == "Loadout")) return;
        view.Columns[0].DisplayMemberBinding = null;
        view.Columns[0].CellTemplate = (DataTemplate)GUI.ParseXaml("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border Tag="DeckTweaks.State" Background="Transparent" Padding="4,2">
                    <TextBlock Text="{Binding FlightDeckTaskLabel}" VerticalAlignment="Center"
                               TextWrapping="Wrap" MaxWidth="220"/>
                </Border>
            </DataTemplate>
            """);
        view.Columns.Insert(3, new GridViewColumn
        {
            Header = "Loadout",
            Width = 180,
            CellTemplate = (DataTemplate)GUI.ParseXaml("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <ComboBox Tag="DeckTweaks.Loadout" MinWidth="165" Margin="4,2"
                              DisplayMemberPath="DisplayName" VerticalAlignment="Center"/>
                </DataTemplate>
                """),
        });
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

    private static void OnStateLoaded(object sender, RoutedEventArgs args)
    {
        if (!Equals(sender, args.Source)) return;
        if (sender is not Border border || border.Tag as string != StateTag) return;
        border.DataContextChanged -= OnStateContextChanged;
        border.DataContextChanged += OnStateContextChanged;
        BindStatus(border, Row(border));
    }

    private static void OnStateContextChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        BindStatus((Border)sender, Row((Border)sender));

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
        Grid grid = new();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        return new() { Content = new ScrollViewer { Content = grid, MaxHeight = 480, MaxWidth = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    private static void SetToolTipLines(ToolTip tooltip, IEnumerable<PreparationLine> lines)
    {
        if (tooltip.Content is not ScrollViewer { Content: Grid grid }) return;
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
}
