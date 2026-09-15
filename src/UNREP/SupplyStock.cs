using System.ComponentModel;

namespace UNREP;

internal class SupplyStock(string key, string category, string name, string location, string unit,
    Func<double> current, Func<double> capacity, Action<double>? change = null, Func<bool>? usable = null,
    bool showWhenEmpty = false) : INotifyPropertyChanged
{
    internal string Key { get; } = key;
    public string Category { get; } = category;
    public string Name { get; } = name;
    public string Location { get; } = location;
    public string Unit { get; } = unit;
    internal double Current => Math.Max(0, current());
    internal double Capacity => Math.Max(0, capacity());
    internal double Free => Math.Max(0, Capacity - Current);
    internal bool Whole => Unit == "items";
    internal bool CanChange => change != null;
    private bool ShowWhenEmpty => showWhenEmpty;
    internal bool Visible => Current > 0 || ShowWhenEmpty;
    internal bool Transferable => change != null && !double.IsNaN(Current) && !double.IsInfinity(Current)
        && !double.IsNaN(Capacity) && !double.IsInfinity(Capacity) && (usable?.Invoke() ?? true);
    public string Amount => double.IsNaN(Capacity) ? $"{Current:N0} {Unit}" : $"{Current:N0} / {Capacity:N0} {Unit}";
    public string Handling => change == null ? "Inventory only" : !Transferable ? "Unavailable" : "Transferable";
    internal Func<double>? RatePerHour { get; set; }
    public string Rate => RatePerHour?.Invoke() is double value ? $"{value:+#,0.##;-#,0.##;0} {Unit}/h" : "";
    internal Func<double>? Minimum { get; set; }
    public string Retained => CanChange ? $"{Minimum?.Invoke() ?? 0:N0} {Unit}" : "";
    internal void Change(double amount) => change!(amount);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));

    internal static SupplyStock Stack(SupplyStock[] bins)
    {
        if (bins.Length == 1) return bins[0];
        var first = bins[0];
        void Change(double amount)
        {
            double remaining = Math.Abs(amount);
            foreach (var bin in bins.Where(bin => bin.Transferable))
            {
                double part = Math.Min(remaining, amount < 0 ? bin.Current : bin.Free);
                if (first.Whole) part = Math.Floor(part);
                if (part <= 0) continue;
                bin.Change(amount < 0 ? -part : part);
                remaining -= part;
                if (remaining <= 0) break;
            }
        }
        return new(first.Key, first.Category, first.Name,
            $"{bins.Length} locations: {string.Join(", ", bins.Select(bin => bin.Location).Distinct())}", first.Unit,
            () => bins.Sum(bin => bin.Current), () => bins.Sum(bin => bin.Capacity),
            first.CanChange ? Change : null, () => bins.Any(bin => bin.Transferable), bins.Any(bin => bin.ShowWhenEmpty))
        {
            Members = bins,
            RatePerHour = bins.Any(bin => bin.RatePerHour != null) ? () => bins.Sum(bin => bin.RatePerHour?.Invoke() ?? 0) : null
        };
    }

    private SupplyStock[]? Members { get; set; }
    internal double Withdrawable => Members?.Where(bin => bin.Transferable).Sum(bin => bin.Current)
        ?? (Transferable ? Current : 0);
}
