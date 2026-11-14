using Noesis;

namespace SeaPower.SharedUi;

internal class ChoicePicker<T>
{
    private T[] values = [];
    private readonly T fallback;
    private readonly Func<T, string>? tooltip;
    internal ComboBox Control { get; } = new()
    {
        Foreground = NativeUi.TextBrush, MinHeight = 30, HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    internal T Value => TryGetValue(out T value) ? value : fallback;

    internal ChoicePicker(IEnumerable<(string Label, T Value)> choices, int selectedIndex = 0,
        T fallback = default!, Func<T, string>? tooltip = null)
    {
        var items = choices.ToArray();
        if (selectedIndex < -1 || selectedIndex >= Math.Max(1, items.Length))
            throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        this.fallback = fallback;
        this.tooltip = tooltip;
        SetItems(items, items.Length == 0 ? -1 : selectedIndex);
    }

    internal bool TryGetValue(out T value)
    {
        int index = Control.SelectedIndex;
        bool selected = index >= 0 && index < values.Length;
        value = selected ? values[index] : fallback;
        return selected;
    }

    internal void Replace(IEnumerable<T> options, Func<T, string> label, int fallbackIndex = 0, bool preserveSelection = true)
    {
        T[] next = options.ToArray();
        int selected = preserveSelection && TryGetValue(out T value) ? Array.IndexOf(next, value) : -1;
        selected = selected >= 0 ? selected : Math.Clamp(fallbackIndex, -1, next.Length - 1);
        if (values.SequenceEqual(next))
        {
            if (Control.SelectedIndex != selected) Control.SelectedIndex = selected;
            return;
        }
        SetItems(next.Select(item => (label(item), item)).ToArray(), selected);
    }

    internal void UpdateLabels(Func<T, string> label)
    {
        var items = values.Select(value => (Label: label(value), Value: value)).ToArray();
        if (items.Select((item, index) => Equals(((ComboBoxItem)Control.Items[index]).Content, item.Label)).All(same => same))
            return;
        // Refresh the closed ComboBox's displayed selection along with its dropdown items.
        SetItems(items, Control.SelectedIndex);
    }

    private void SetItems((string Label, T Value)[] items, int selected)
    {
        var controls = items.Select(item => new ComboBoxItem
            { Content = item.Label, Tag = item.Value, ToolTip = tooltip?.Invoke(item.Value) }).ToArray();
        values = [];
        Control.Items.Clear();
        foreach (var control in controls) Control.Items.Add(control);
        values = items.Select(item => item.Value).ToArray();
        Control.SelectedIndex = selected;
    }
}
