using Noesis;
using SeaPower;
using static SeaPower.SharedUi.NativeUi;

namespace UNREP;

internal class VertrepTransfersView
{
    internal FrameworkElement Content { get; }
    private readonly Action refresh;
    internal void Refresh() => refresh();

    internal VertrepTransfersView(VertrepOperation operation)
    {
        var list = new StackPanel();
        var empty = Text("No compatible receiving ships.");
        var message = Text("");
        message.Name = "VertrepTransferMessage";
        var root = new Grid { Name = "VertrepTransfersPanel", Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(Text("Receiving ships"));
        var scroll = Scroll(Stack(empty, list));
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(message, 2);
        root.Children.Add(message);
        var rows = new Dictionary<VertrepReceiver, ReceiverRow>();
        VertrepReceiver[] displayed = [];
        refresh = () =>
        {
            var receivers = operation.Source._taskforce?._taskforceUnits.OfType<Vessel>()
                .Where(unit => unit != operation.Source && SupplyTransfers.Available(unit)) ?? [];
            var plans = receivers.Select(operation.Plan).Where(plan => plan.Supplies.Count > 0)
                .OrderBy(plan => plan.Queued ? 0 : 1).ThenBy(plan => operation.Receivers.IndexOf(plan)).ToArray();
            empty.Visibility = plans.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!displayed.SequenceEqual(plans))
            {
                displayed = plans;
                list.Children.Clear();
                foreach (var plan in plans)
                {
                    if (!rows.TryGetValue(plan, out var row)) rows[plan] = row = new(plan, text => message.Text = text);
                    list.Children.Add(row.Content);
                }
                foreach (var removed in rows.Keys.Where(plan => !plans.Contains(plan)).ToArray()) rows.Remove(removed);
            }
            foreach (var plan in plans) rows[plan].Refresh();
        };
        Content = root;
        Refresh();
    }

    private class ReceiverRow
    {
        internal Expander Content { get; }
        private readonly Action refresh;
        internal void Refresh() => refresh();
        internal ReceiverRow(VertrepReceiver receiver, Action<string> report)
        {
            var operation = receiver.Operation;
            var title = Text(receiver.Target.getName());
            var status = Text("");
            var queue = Button("QUEUE", () => report(operation.Queue(receiver)), primary: true);
            queue.Name = "VertrepQueueShip";
            var up = Button("UP", () => operation.MoveReceiver(receiver, -1));
            up.Name = "VertrepShipUp";
            var down = Button("DOWN", () => operation.MoveReceiver(receiver, 1));
            down.Name = "VertrepShipDown";
            var cancel = Button("CANCEL", receiver.Cancel);
            cancel.Name = "VertrepCancelShip";
            var header = new Grid();
            header.ColumnDefinitions.Add(new() { Width = new(230) });
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(title);
            Grid.SetColumn(status, 1);
            header.Children.Add(status);
            var actions = Wrap(queue, up, down, cancel);
            Grid.SetColumn(actions, 2);
            header.Children.Add(actions);
            var supplies = Table("VertrepShipSupplies", CheckColumn("Use", nameof(VertrepTransfer.Enabled), 50),
                TextColumn("#", nameof(VertrepTransfer.Priority), 35), TextColumn("Supply", nameof(VertrepTransfer.Item), 190),
                TextColumn("To transfer", nameof(VertrepTransfer.AvailableAmount), 140),
                TextColumn("Delivered", nameof(VertrepTransfer.DeliveredAmount), 125),
                TextColumn("In flight", nameof(VertrepTransfer.AirborneAmount), 125),
                TextColumn("Status", nameof(VertrepTransfer.Status), 185));
            var supplyUp = Button("UP", () => { if (supplies.SelectedItem is VertrepTransfer plan) receiver.MoveSupply(plan, -1); });
            supplyUp.Name = "VertrepSupplyUp";
            var supplyDown = Button("DOWN", () => { if (supplies.SelectedItem is VertrepTransfer plan) receiver.MoveSupply(plan, 1); });
            supplyDown.Name = "VertrepSupplyDown";
            var enableAll = Button("ENABLE ALL", () => receiver.Supplies.ForEach(plan => plan.Enabled = true));
            enableAll.Name = "VertrepEnableAll";
            var disableAll = Button("DISABLE ALL", () => receiver.Supplies.ForEach(plan => plan.Enabled = false));
            disableAll.Name = "VertrepDisableAll";
            Content = Section("", Stack(supplies, Wrap(supplyUp, supplyDown, enableAll, disableAll)), expanded: false);
            Content.Name = "VertrepReceiver";
            Content.Header = header;
            refresh = () =>
            {
                var queued = operation.Receivers.Where(plan => plan.Queued).ToList();
                int index = queued.IndexOf(receiver);
                title.Text = index >= 0 ? $"{index + 1}. {receiver.Target.getName()}" : receiver.Target.getName();
                string blocked = operation.QueueBlocked(receiver);
                status.Text = receiver.InUse || receiver.HasRun ? receiver.Status : blocked.Length > 0 ? blocked : "Ready";
                queue.Content = receiver.HasRun ? "REQUEUE" : "QUEUE";
                queue.IsEnabled = !receiver.Queued && blocked.Length == 0;
                up.IsEnabled = index > 0;
                down.IsEnabled = index >= 0 && index < queued.Count - 1;
                cancel.IsEnabled = receiver.Queued;
                VertrepView.SyncRows(supplies, receiver.Supplies);
                supplies.Height = Math.Min(300, 42 + receiver.Supplies.Count * 39);
                receiver.Supplies.ForEach(plan => plan.Refresh());
                int selected = supplies.SelectedItem is VertrepTransfer supply ? receiver.Supplies.IndexOf(supply) : -1;
                supplyUp.IsEnabled = selected > 0;
                supplyDown.IsEnabled = selected >= 0 && selected < receiver.Supplies.Count - 1;
            };
            Refresh();
        }
    }
}
