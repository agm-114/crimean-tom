using Noesis;
using static CrimeanTom.SeaPower.NativeUi;

namespace CrimeanTom.SeaPower;

internal class ScenarioStartingControls(ScenarioStartingOptions initial, bool includeProtectedShips = true)
{
    private readonly CheckBox aircraft = Toggle("Friendly aircraft already deployed", initial.AircraftDeployed);
    private readonly CheckBox orders = Toggle("Preload player-unit orders", initial.PreloadOrders);
    private readonly CheckBox protectedShips = Toggle("Player controls protected ships (escort only)", initial.ControlProtectedShips);
    internal ScenarioStartingOptions Value => new(aircraft.IsChecked == true, orders.IsChecked == true,
        includeProtectedShips && protectedShips.IsChecked == true);

    internal void AddTo(StackPanel content)
    {
        content.Children.Add(aircraft);
        content.Children.Add(orders);
        if (includeProtectedShips) content.Children.Add(protectedShips);
    }

    private static CheckBox Toggle(string label, bool enabled) => new()
    {
        Content = label,
        IsChecked = enabled,
        Foreground = TextBrush,
        Margin = new Thickness(0, 8, 0, 4),
    };
}
