using Noesis;

namespace SeaPower.SharedUi;

internal static partial class NativeUi
{
    internal static void SyncSelection<T, TKey>(Selector control, IReadOnlyList<T> items, Func<T, TKey> key,
        bool selectFirst = false) where T : class
    {
        if (control.Items.Count == items.Count &&
            items.Select((item, index) => ReferenceEquals(item, control.Items[index])).All(same => same)) return;
        T? selected = control.SelectedItem as T;
        TKey previousKey = selected == null ? default! : key(selected);
        control.Items.Clear();
        foreach (var item in items) control.Items.Add(item);
        var replacement = selected == null ? null :
            items.FirstOrDefault(item => EqualityComparer<TKey>.Default.Equals(key(item), previousKey));
        control.SelectedItem = replacement ?? (selectFirst ? items.FirstOrDefault() : null);
    }
}

// Keep the same visual and its local state when its key moves in the list.
internal class KeyedPanel<T>(Func<T, (FrameworkElement Content, Action Refresh)> create) where T : notnull
{
    private readonly Dictionary<T, (FrameworkElement Content, Action Refresh)> rows = [];
    private IReadOnlyList<T> displayed = [];
    internal StackPanel Panel { get; } = new();

    internal void Refresh(IReadOnlyList<T> items)
    {
        if (!displayed.SequenceEqual(items))
        {
            var current = new HashSet<T>(items);
            foreach (var item in rows.Keys.Where(item => !current.Contains(item)).ToArray()) rows.Remove(item);
            Panel.Children.Clear();
            foreach (var item in items)
            {
                if (!rows.TryGetValue(item, out var row)) rows[item] = row = create(item);
                Panel.Children.Add(row.Content);
            }
            displayed = items.ToArray();
        }
        foreach (var item in items) rows[item].Refresh();
    }
}
