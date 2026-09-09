using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Noesis;
using SeaPower;
using SeapowerUI;

namespace FlightDeckTweaks;

[HarmonyPatch(typeof(MainGameView), MethodType.Constructor)]
internal static class LoadoutColumn
{
    private const string PickerTag = "FlightDeckTweaks.Loadout";
    private static readonly ConditionalWeakTable<PendingLaunchTask, TaskLoadout> Rows = new();
    private static bool registered;

    private static void Postfix()
    {
        if (registered) return;
        EventManager.RegisterClassHandler(typeof(ListView), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnListLoaded));
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnPickerLoaded));
        registered = true;
    }

    private static void OnListLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not ListView { View: GridView view } list
            || list.ItemsSource is not ObservableCollection<FlightDeckTask>
            || view.Columns.Any(column => column.Header as string == "Loadout")) return;
        view.Columns.Insert(3, new GridViewColumn
        {
            Header = "Loadout",
            Width = 180,
            CellTemplate = (DataTemplate)GUI.ParseXaml("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <ComboBox Tag="FlightDeckTweaks.Loadout" MinWidth="165" Margin="4,2"
                              DisplayMemberPath="DisplayName" VerticalAlignment="Center"/>
                </DataTemplate>
                """),
        });
    }

    private static void OnPickerLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not ComboBox picker || picker.Tag as string != PickerTag) return;
        picker.DataContextChanged -= OnContextChanged;
        picker.DataContextChanged += OnContextChanged;
        Bind(picker);
    }

    private static void OnContextChanged(object sender, DependencyPropertyChangedEventArgs args) => Bind((ComboBox)sender);
    private static void Bind(ComboBox picker)
    {
        TaskLoadout? row = picker.DataContext is PendingLaunchTask task ? Rows.GetValue(task, value => new(value)) : null;
        picker.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        if (row == null) return;
        picker.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(TaskLoadout.Loadouts)) { Source = row });
        picker.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(TaskLoadout.SelectedLoadout)) { Source = row, Mode = BindingMode.TwoWay });
        picker.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(TaskLoadout.CanChange)) { Source = row });
    }
}
