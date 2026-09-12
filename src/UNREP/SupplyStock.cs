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
    internal bool Visible => Current > 0 || showWhenEmpty;
    internal bool Transferable => change != null && !double.IsNaN(Current) && !double.IsInfinity(Current)
        && !double.IsNaN(Capacity) && !double.IsInfinity(Capacity) && (usable?.Invoke() ?? true);
    public string Amount => double.IsNaN(Capacity) ? $"{Current:N0} {Unit}" : $"{Current:N0} / {Capacity:N0} {Unit}";
    public string Handling => change == null ? "Inventory only" : !Transferable ? "Unavailable" : "Transferable";
    internal void Change(double amount) => change!(amount);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
