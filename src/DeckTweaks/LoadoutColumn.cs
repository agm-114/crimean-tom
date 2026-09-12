using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
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
        item.ToolTip ??= CreateToolTip(new TextBlock { TextWrapping = TextWrapping.Wrap });
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
        if (item.ToolTip is ToolTip { Content: ScrollViewer { Content: TextBlock status } })
            status.Text = row.Preview(loadout);
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
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskLoadout.PreparationStatus)) { Source = row });
        element.ToolTip = CreateToolTip(status);
        ConfigureToolTip(element);
    }

    private static ToolTip CreateToolTip(TextBlock status) => new()
    {
        Content = new ScrollViewer { Content = status, MaxHeight = 480, MaxWidth = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
    };

    private static void ConfigureToolTip(FrameworkElement element)
    {
        ToolTipService.SetShowOnDisabled(element, true);
        ToolTipService.SetPlacement(element, PlacementMode.Right);
        ToolTipService.SetInitialShowDelay(element, 300);
        ToolTipService.SetBetweenShowDelay(element, 0);
        ToolTipService.SetShowDuration(element, 60000);
    }
}
