using Noesis;
using SeaPower;
using static SeaPower.SharedUi.NativeUi;

namespace UNREP;

internal class SuppliesView
{
    internal FrameworkElement Content { get; }
    private readonly Action refresh;
    internal void Refresh() => refresh();

    internal SuppliesView(Vessel ship)
    {
        var stocks = SupplyInventory.Read(ship);
        string structure = SupplyInventory.Structure(ship);
        string category = "All", search = "";
        var table = Table("SupplyInventory", TextColumn("Category", nameof(SupplyStock.Category), 145),
            TextColumn("Item", nameof(SupplyStock.Name), 210), TextColumn("Location", nameof(SupplyStock.Location), 160),
            TextColumn("Quantity / capacity", nameof(SupplyStock.Amount), 180), TextColumn("Handling", nameof(SupplyStock.Handling), 120));
        var message = Text("Select a stock to transfer. Aircraft and mounted weapons are inventory-only.");
        message.Name = "SupplyTransferMessage";
        var detail = Text("");
        detail.Name = "SupplyTransferEstimate";
        Vessel? target = null;
        bool airlift = false;
        Helicopter? helicopter = null;
        var mode = Choice(new[] { "Alongside UNREP", "Helicopter VERTREP" }, value => value,
            changed: value => airlift = value == "Helicopter VERTREP");
        mode.Name = "SupplyTransferMode";
        var helicopters = new ComboBox { Name = "SupplyTransferHelicopter", MinHeight = 30 };
        helicopters.SelectionChanged += (_, _) => helicopter = (helicopters.SelectedItem as ComboBoxItem)?.Tag as Helicopter;
        Helicopter[] previousHelicopters = [];
        double amount = 1;
        var quantity = Number(1, 0, 1e9, value => amount = value);
        quantity.Name = "SupplyTransferQuantity";
        var targets = new ComboBox { Name = "SupplyTransferTarget", MinHeight = 30 };
        targets.SelectionChanged += (_, _) => target = (targets.SelectedItem as ComboBoxItem)?.Tag as Vessel;
        Vessel[] previousTargets = [];
        void UpdateTargets()
        {
            var aircraft = ship._taskforce?._taskforceUnits.OfType<Helicopter>()
                .Where(unit => unit._isInFlight && !unit.IsDestroyed && unit.AcceptsOrdersFromPlayer).ToArray() ?? [];
            if (!previousHelicopters.SequenceEqual(aircraft))
            {
                var oldHelicopter = helicopter;
                previousHelicopters = aircraft;
                helicopters.Items.Clear();
                foreach (var unit in aircraft) helicopters.Items.Add(new ComboBoxItem { Content = unit.getName(), Tag = unit });
                helicopters.SelectedIndex = aircraft.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(aircraft, oldHelicopter));
            }
            helicopters.IsEnabled = airlift;
            var ships = ship._taskforce?._taskforceUnits.OfType<Vessel>()
                .Where(unit => unit != ship && SupplyTransfers.Available(unit)).ToArray() ?? [];
            if (previousTargets.SequenceEqual(ships)) return;
            var old = target;
            previousTargets = ships;
            targets.Items.Clear();
            foreach (var unit in ships) targets.Items.Add(new ComboBoxItem { Content = unit.getName(), Tag = unit });
            targets.SelectedIndex = ships.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(ships, old));
        }
        SupplyStock[] displayed = [];
        void Filter()
        {
            var visible = stocks.Where(stock => stock.Visible && (category == "All" || stock.Category == category)
                && (stock.Name + " " + stock.Location).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (displayed.SequenceEqual(visible)) return;
            displayed = visible;
            var old = table.SelectedItem;
            table.Items.Clear();
            foreach (var stock in visible) table.Items.Add(stock);
            if (old != null && table.Items.Contains(old)) table.SelectedItem = old;
        }
        var categories = Choice(new[] { "All" }.Concat(stocks.Select(stock => stock.Category).Distinct()), value => value,
            changed: value => { category = value; Filter(); });
        var find = Entry("", value => { search = value; Filter(); });
        find.Name = "SupplySearch";
        var send = Button("TRANSFER", () =>
        {
            if (target == null || table.SelectedItem is not SupplyStock stock) { message.Text = "Choose a stock and a receiving ship."; return; }
            if (!double.TryParse(quantity.Text, out double entered) || double.IsNaN(entered) || double.IsInfinity(entered))
            { message.Text = "Enter a valid quantity."; return; }
            if (airlift && helicopter == null) { message.Text = "Launch a helicopter, then select it for VERTREP."; return; }
            message.Text = SupplyTransfers.Start(ship, target, stock, entered, airlift ? helicopter : null);
        }, primary: true);
        send.Name = "StartSupplyTransfer";
        var maximum = Button("MAX", () =>
        {
            if (target == null || table.SelectedItem is not SupplyStock stock || !stock.Transferable) return;
            double space = SupplyInventory.Read(target).Where(item => item.Key == stock.Key && item.Transferable).Sum(item => item.Free);
            double max = Math.Min(stock.Current, space);
            quantity.Text = (stock.Whole ? Math.Floor(max) : max).ToString(System.Globalization.CultureInfo.CurrentCulture);
        });
        var jobs = new StackPanel();
        var transferPanel = Stack(Text("UNREP reserves do not yet supply propulsion, aircraft fuel, crew consumption or repairs."),
            Columns(2, Labelled("Method", mode), Labelled("Airborne helicopter", helicopters)),
            FormRow("Receiving ship", targets), FormRow("Quantity", quantity),
            Wrap(maximum, send), detail, message, jobs);
        var root = new Grid { Margin = new Thickness(8), Name = "SuppliesPanel" };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(Columns(2, Labelled("Category", categories), Labelled("Search", find)));
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        Grid.SetRow(transferPanel, 2);
        root.Children.Add(transferPanel);
        List<SupplyTransfer> shownJobs = [];
        List<TextBlock> jobLabels = [];
        refresh = () =>
        {
            UpdateTargets();
            string updatedStructure = SupplyInventory.Structure(ship);
            if (structure != updatedStructure)
            {
                structure = updatedStructure;
                stocks = SupplyInventory.Read(ship);
                Filter();
            }
            foreach (var stock in stocks) stock.Refresh();
            Filter();
            var stockSelected = table.SelectedItem as SupplyStock;
            send.IsEnabled = target != null && stockSelected?.Transferable == true && amount > 0;
            if (target != null && stockSelected != null)
            {
                double free = SupplyInventory.Read(target).Where(item => item.Key == stockSelected.Key && item.Transferable).Sum(item => item.Free);
                double rate = stockSelected.Unit == "kg" ? SupplySettings.KgPerSecond
                    : stockSelected.Unit == "points" ? SupplySettings.PointsPerSecond : SupplySettings.ItemsPerSecond;
                detail.Text = $"Available {stockSelected.Current:N0} · Receiving space {free:N0} {stockSelected.Unit}\n"
                    + $"{rate:0.###} {stockSelected.Unit}/s · {TimeSpan.FromSeconds(Math.Min(amount / rate, 1e9)):g} handling time · "
                    + $"Range ≤ {SupplySettings.RangeNm:0.##} nm · Speed ≤ {SupplySettings.MaxSpeed:0.#} knots";
                if (airlift) detail.Text = $"Sling load ≤ {SupplySettings.SlingCapacityKg:N0} kg · {SupplySettings.SlingHandlingSeconds:0} s pickup/drop · Food, water and spare parts only";
            }
            else detail.Text = "";
            var active = SupplyTransfers.Jobs.Where(job => job.Source == ship || job.Target == ship).ToList();
            if (!shownJobs.SequenceEqual(active))
            {
                shownJobs = active;
                jobs.Children.Clear();
                jobLabels.Clear();
                foreach (var job in active)
                {
                    var label = Text("");
                    jobLabels.Add(label);
                    jobs.Children.Add(FormRow("Transfer", Wrap(label, Button("Cancel", job.Cancel))));
                }
            }
            for (int i = 0; i < shownJobs.Count; i++)
                jobLabels[i].Text = $"{shownJobs[i].Route} · {shownJobs[i].Item} · {shownJobs[i].Progress} · {shownJobs[i].State}";
        };
        Filter();
        Content = root;
    }
}
