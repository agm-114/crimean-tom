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
        var policy = SupplyPolicy.For(ship);
        var stocks = SupplyInventory.Stacked(ship);
        string structure = SupplyInventory.Structure(ship), category = "All", search = "";
        var table = Table("SupplyInventory", TextColumn("Category", nameof(SupplyStock.Category), 145),
            TextColumn("Item", nameof(SupplyStock.Name), 170), TextColumn("Location", nameof(SupplyStock.Location), 190),
            TextColumn("Quantity / capacity", nameof(SupplyStock.Amount), 170),
            TextColumn("Use / production", nameof(SupplyStock.Rate), 145),
            TextColumn("Minimum retained", nameof(SupplyStock.Retained), 125));
        var unrep = Check("Enable UNREP", policy.Unrep, enabled => policy.Enable(ship, false, enabled));
        unrep.Name = "EnableUnrep";
        var vertrep = Check("Enable VERTREP", policy.Vertrep, enabled => policy.Enable(ship, true, enabled));
        vertrep.Name = "EnableVertrep";
        var minimum = Entry("0");
        minimum.Name = "SupplyMinimum";
        minimum.MinWidth = 130;
        minimum.IsEnabled = false;
        var message = Text("");
        var reserve = Text("");
        reserve.Name = "SupplyMinimumStatus";
        var apply = Button("SET MINIMUM", () =>
        {
            if (table.SelectedItem is not SupplyStock stock || !stock.CanChange) return;
            if (!double.TryParse(minimum.Text, out double value) || !double.IsFinite(value)
                || value < 0 || value > 1e9 || stock.Whole && value != Math.Floor(value))
            { message.Text = "Enter a nonnegative quantity, whole numbers for items, up to 1,000,000,000."; return; }
            policy.SetMinimum(stock.Key, value);
            message.Text = "";
        }, primary: true);
        apply.Name = "SetSupplyMinimum";
        table.SelectionChanged += (_, _) =>
        {
            var stock = table.SelectedItem as SupplyStock;
            message.Text = "";
            minimum.IsEnabled = apply.IsEnabled = stock?.CanChange == true;
            minimum.Text = stock == null ? "0" : policy.Minimum(stock.Key).ToString(System.Globalization.CultureInfo.CurrentCulture);
        };
        SupplyStock[] displayed = [];
        void Filter()
        {
            var visible = stocks.Where(stock => stock.Visible && (category == "All" || stock.Category == category)
                && (stock.Name + " " + stock.Location).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (displayed.SequenceEqual(visible)) return;
            string? selectedKey = (table.SelectedItem as SupplyStock)?.Key;
            displayed = visible;
            table.Items.Clear();
            foreach (var stock in visible) table.Items.Add(stock);
            table.SelectedItem = visible.FirstOrDefault(stock => stock.Key == selectedKey);
        }
        var categories = Choice(new[] { "All" }.Concat(stocks.Select(stock => stock.Category).Distinct()), value => value,
            changed: value => { category = value; Filter(); });
        var find = Entry("", value => { search = value; Filter(); });
        find.Name = "SupplySearch";
        var root = new Grid { Name = "SuppliesPanel", Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(Stack(Wrap(unrep, vertrep), Columns(2, Labelled("Category", categories), Labelled("Search", find))));
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        var footer = Stack(FormRow("Minimum retained", Wrap(minimum, apply)), reserve, message);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        refresh = () =>
        {
            string updated = SupplyInventory.Structure(ship);
            if (structure != updated) { structure = updated; stocks = SupplyInventory.Stacked(ship); }
            foreach (var stock in stocks) stock.Refresh();
            Filter();
            apply.IsEnabled = table.SelectedItem is SupplyStock { CanChange: true };
            reserve.Text = table.SelectedItem is SupplyStock selected && selected.CanChange
                ? $"Available to send: {SupplyPolicy.Available(ship, selected):N0} {selected.Unit}"
                : "";
        };
        Filter();
        Content = root;
    }
}
