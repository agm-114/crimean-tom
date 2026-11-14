using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using static SeaPower.SharedUi.NativeUi;

namespace UNREP;

internal class TransferView
{
    internal FrameworkElement Content { get; }
    private readonly Action refresh;
    internal void Refresh() => refresh();

    internal TransferView(Vessel ship)
    {
        var empty = Text("No compatible receiving ships.");
        var message = Text("");
        message.Name = "UnrepMessage";
        var rows = new KeyedPanel<SupplyTransfer>(plan =>
        {
            var row = new ReceiverRow(plan, text => message.Text = text);
            return (row.Content, row.Refresh);
        });
        Content = FillPanel("UnrepPanel", 8, Text("Receiving ships"), Scroll(Stack(empty, rows.Panel)), message);
        refresh = () =>
        {
            var receivers = ship._taskforce?._taskforceUnits.OfType<Vessel>()
                .Where(unit => unit != ship && SupplyTransfers.Available(unit)) ?? [];
            var plans = receivers.Select(unit => SupplyTransfers.Plan(ship, unit))
                .Where(plan => plan.Supplies.Count > 0).OrderBy(plan => plan.Finished ? 1 : 0)
                .ThenBy(plan => SupplyTransfers.Jobs.IndexOf(plan)).ToArray();
            empty.Visibility = plans.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            rows.Refresh(plans);
        };
        Refresh();
    }

    private class ReceiverRow
    {
        internal Expander Content { get; }
        private readonly Action refresh;
        internal void Refresh() => refresh();
        internal ReceiverRow(SupplyTransfer job, Action<string> report)
        {
            var title = Text(job.Target.getName());
            var status = Text("");
            var queue = Button("QUEUE", () => report(SupplyTransfers.Queue(job)), primary: true);
            queue.Name = "UnrepQueueShip";
            var up = Button("UP", () => SupplyTransfers.MoveShip(job, -1));
            up.Name = "UnrepShipUp";
            var down = Button("DOWN", () => SupplyTransfers.MoveShip(job, 1));
            down.Name = "UnrepShipDown";
            var cancel = Button("CANCEL", job.Cancel);
            cancel.Name = "UnrepCancelShip";
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
            var supplies = Table("UnrepShipSupplies", CheckColumn("Use", nameof(UnrepSupply.Enabled), 50),
                TextColumn("#", nameof(UnrepSupply.Priority), 35), TextColumn("Supply", nameof(UnrepSupply.Item), 240),
                TextColumn("To transfer", nameof(UnrepSupply.Amount), 155),
                TextColumn("Delivered", nameof(UnrepSupply.DeliveredAmount), 155),
                TextColumn("Status", nameof(UnrepSupply.Status), 220));
            var supplyUp = Button("UP", () => { if (supplies.SelectedItem is UnrepSupply plan) job.MoveSupply(plan, -1); });
            supplyUp.Name = "UnrepSupplyUp";
            var supplyDown = Button("DOWN", () => { if (supplies.SelectedItem is UnrepSupply plan) job.MoveSupply(plan, 1); });
            supplyDown.Name = "UnrepSupplyDown";
            var enableAll = Button("ENABLE ALL", () => job.Supplies.ForEach(plan => plan.Enabled = true));
            enableAll.Name = "UnrepEnableAll";
            var disableAll = Button("DISABLE ALL", () => job.Supplies.ForEach(plan => plan.Enabled = false));
            disableAll.Name = "UnrepDisableAll";
            Content = Section("", Stack(supplies, Wrap(supplyUp, supplyDown, enableAll, disableAll)), expanded: false);
            Content.Name = "UnrepReceiver";
            Content.Header = header;
            refresh = () =>
            {
                var queueJobs = SupplyTransfers.Jobs.Where(plan => plan.Source == job.Source && !plan.Finished).ToList();
                int index = queueJobs.IndexOf(job);
                title.Text = index >= 0 ? $"{index + 1}. {job.Target.getName()}" : job.Target.getName();
                string blocked = SupplyTransfers.QueueBlocked(job);
                status.Text = !job.Finished || job.HasRun ? job.State : blocked.Length > 0 ? blocked : "Ready";
                queue.Content = job.HasRun ? "REQUEUE" : "QUEUE";
                queue.IsEnabled = job.Finished && blocked.Length == 0;
                up.IsEnabled = index > 0;
                down.IsEnabled = index >= 0 && index < queueJobs.Count - 1;
                cancel.IsEnabled = !job.Finished;
                SyncSelection(supplies, job.Supplies, supply => supply);
                supplies.Height = Math.Min(360, 42 + job.Supplies.Count * 39);
                job.Supplies.ForEach(plan => plan.Refresh());
                int selectedIndex = supplies.SelectedItem is UnrepSupply selectedPlan ? job.Supplies.IndexOf(selectedPlan) : -1;
                supplyUp.IsEnabled = selectedIndex > 0;
                supplyDown.IsEnabled = selectedIndex >= 0 && selectedIndex < job.Supplies.Count - 1;
            };
            Refresh();
        }
    }
}
