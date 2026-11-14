using Noesis;
using SeaPower;
using SeaPower.SharedUi;
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
        var availableHelos = new ChoicePicker<Helicopter>([]);
        availableHelos.Control.Name = "VertrepAvailableHelicopters";
        availableHelos.Control.MinWidth = 220;
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
            if (availableHelos.TryGetValue(out var helo)) message.Text = operation.Assign(helo);
        }, primary: true);
        assign.Name = "AssignVertrepHelicopter";
        var release = Button("UNASSIGN", () => { if (helos.SelectedItem is HelicopterReplenishment helo) helo.Cancel(); });
        release.Name = "UnassignVertrepHelicopter";
        var helicopterPanel = FillPanel("VertrepHelicopterPanel", 8, Wrap(availableHelos.Control, assign, release), helos, message);
        var transfers = new VertrepTransfersView(operation);
        var tabs = Tabs(("Helicopters", helicopterPanel), ("Transfers", transfers.Content));
        tabs.Name = "VertrepTabs";
        Content = FillPanel("VertrepPanel", 8, summary, tabs);
        refresh = () =>
        {
            var aircraft = ship._taskforce?._taskforceUnits.OfType<Helicopter>().Where(operation.CanAssign) ?? [];
            availableHelos.Replace(aircraft, helo => helo.getName());
            SyncSelection(helos, operation.Helicopters, helo => helo);
            operation.Helicopters.ForEach(helo => helo.Refresh());
            var assigned = operation.Helicopters.Where(helo => !helo.Released).ToArray();
            summary.Text = $"Assigned {assigned.Length} · Flying {assigned.Count(helo => helo.InTransit)} · Idle {assigned.Count(helo => helo.Waiting)}";
            release.IsEnabled = helos.SelectedItem is HelicopterReplenishment { Released: false };
            assign.IsEnabled = availableHelos.TryGetValue(out _);
            transfers.Refresh();
        };
        Refresh();
    }
}
