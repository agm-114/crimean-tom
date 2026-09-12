using Noesis;
using SeaPower;
using static SeaPower.SharedUi.NativeUi;

namespace UNREP;

internal class VertrepView
{
    internal FrameworkElement Content { get; }
    private readonly Action refresh;
    internal void Refresh() => refresh();

    internal VertrepView(Vessel ship)
    {
        var operation = VertrepOperation.For(ship);
        var availableHelos = new ComboBox { Name = "VertrepAvailableHelicopters", Foreground = TextBrush, MinWidth = 220 };
        var message = Text("");
        message.Name = "VertrepHelicopterMessage";
        var summary = Text("");
        summary.Name = "VertrepSummary";
        summary.Margin = new Thickness(0, 0, 0, 8);
        var helos = Table("VertrepHelicopters", TextColumn("Helicopter", nameof(HelicopterReplenishment.Name), 130),
            TextColumn("Supply", nameof(HelicopterReplenishment.Supply), 120), TextColumn("Receiver", nameof(HelicopterReplenishment.Receiver), 175),
            TextColumn("Cargo", nameof(HelicopterReplenishment.Load), 105), TextColumn("Delivered", nameof(HelicopterReplenishment.Delivered), 115),
            TextColumn("Status", nameof(HelicopterReplenishment.Status), 230));
        var assign = Button("ASSIGN", () =>
        {
            if ((availableHelos.SelectedItem as ComboBoxItem)?.Tag is Helicopter helo) message.Text = operation.Assign(helo);
        }, primary: true);
        assign.Name = "AssignVertrepHelicopter";
        var release = Button("UNASSIGN", () => { if (helos.SelectedItem is HelicopterReplenishment helo) helo.Cancel(); });
        release.Name = "UnassignVertrepHelicopter";
        var helicopterPanel = new Grid { Name = "VertrepHelicopterPanel", Margin = new Thickness(8) };
        helicopterPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        helicopterPanel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        helicopterPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        helicopterPanel.Children.Add(Wrap(availableHelos, assign, release));
        Grid.SetRow(helos, 1);
        helicopterPanel.Children.Add(helos);
        Grid.SetRow(message, 2);
        helicopterPanel.Children.Add(message);
        var transfers = new VertrepTransfersView(operation);
        var tabs = Tabs(("Helicopters", helicopterPanel), ("Transfers", transfers.Content));
        tabs.Name = "VertrepTabs";
        var root = new Grid { Name = "VertrepPanel", Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.Children.Add(summary);
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        Helicopter[] previousHelos = [];
        refresh = () =>
        {
            var aircraft = ship._taskforce?._taskforceUnits.OfType<Helicopter>().Where(operation.CanAssign).ToArray() ?? [];
            if (!previousHelos.SequenceEqual(aircraft))
            {
                previousHelos = aircraft;
                var selected = (availableHelos.SelectedItem as ComboBoxItem)?.Tag as Helicopter;
                availableHelos.Items.Clear();
                foreach (var helo in aircraft) availableHelos.Items.Add(new ComboBoxItem { Content = helo.getName(), Tag = helo });
                availableHelos.SelectedIndex = aircraft.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(aircraft, selected));
            }
            SyncRows(helos, operation.Helicopters);
            operation.Helicopters.ForEach(helo => helo.Refresh());
            var assigned = operation.Helicopters.Where(helo => !helo.Released).ToArray();
            summary.Text = $"Assigned {assigned.Length} · Flying {assigned.Count(helo => helo.InTransit)} · Idle {assigned.Count(helo => helo.Waiting)}";
            release.IsEnabled = helos.SelectedItem is HelicopterReplenishment { Released: false };
            assign.IsEnabled = availableHelos.SelectedItem is ComboBoxItem { Tag: Helicopter };
            transfers.Refresh();
        };
        Content = root;
        Refresh();
    }
    internal static void SyncRows<T>(ListView table, List<T> rows) where T : class
    {
        if (table.Items.Count == rows.Count && rows.Select((row, index) => ReferenceEquals(table.Items[index], row)).All(same => same)) return;
        var selected = table.SelectedItem;
        table.Items.Clear();
        rows.ForEach(row => table.Items.Add(row));
        if (selected is T item && rows.Contains(item)) table.SelectedItem = selected;
    }
}
