using Noesis;

namespace SeaPower.SharedUi;

public class SegmentedProgress(string segmentName, float gap = 1, float minimumWidth = 3)
{
    private readonly List<ProgressBar> bars = [];
    private readonly List<TextBlock> labels = [];
    private readonly RowDefinition barRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    private readonly RowDefinition labelRow = new() { Height = GridLength.Auto };
    public Grid Content { get; } = new();

    public void Refresh(int count, Func<int, (string Tooltip, float Weight, float Fraction, bool Indeterminate)> read) =>
        Refresh(count, read, null);

    public void Refresh(int count, Func<int, (string Tooltip, float Weight, float Fraction, bool Indeterminate)> read,
        Func<int, string>? label)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        while (bars.Count > count)
        {
            int index = bars.Count - 1;
            Content.Children.Remove(bars[index]);
            Content.ColumnDefinitions.RemoveAt(index * 2);
            if (index > 0) Content.ColumnDefinitions.RemoveAt(index * 2 - 1);
            bars.RemoveAt(index);
        }
        while (bars.Count < count)
        {
            int index = bars.Count;
            if (index > 0) Content.ColumnDefinitions.Add(new() { Width = new GridLength(gap) });
            var bar = new ProgressBar { Name = $"{segmentName}{index + 1}", Maximum = 1 };
            Grid.SetColumn(bar, index * 2);
            Content.ColumnDefinitions.Add(new() { MinWidth = minimumWidth });
            Content.Children.Add(bar);
            bars.Add(bar);
        }
        UpdateLabels(label == null ? 0 : count);
        for (int i = 0; i < count; i++)
        {
            var (tooltip, weight, fraction, indeterminate) = read(i);
            var bar = bars[i];
            if (!Equals(bar.ToolTip, tooltip)) bar.ToolTip = tooltip;
            var width = new GridLength(weight, GridUnitType.Star);
            var column = Content.ColumnDefinitions[i * 2];
            if (column.Width != width) column.Width = width;
            bar.Value = fraction;
            bar.IsIndeterminate = indeterminate;
            if (label != null)
            {
                string text = label(i);
                if (labels[i].Text != text) labels[i].Text = text;
            }
        }
    }

    private void UpdateLabels(int count)
    {
        bool hadLabels = labels.Count > 0;
        while (labels.Count > count)
        {
            Content.Children.Remove(labels[^1]);
            labels.RemoveAt(labels.Count - 1);
        }
        if (count > 0 && !hadLabels)
        {
            Content.RowDefinitions.Add(barRow);
            Content.RowDefinitions.Add(labelRow);
        }
        else if (count == 0 && hadLabels)
        {
            Content.RowDefinitions.Remove(labelRow);
            Content.RowDefinitions.Remove(barRow);
        }
        while (labels.Count < count)
        {
            var label = NativeUi.Text("");
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.TextAlignment = TextAlignment.Center;
            label.Margin = new Thickness(0, 3, 0, 0);
            label.IsHitTestVisible = false;
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, labels.Count * 2);
            Content.Children.Add(label);
            labels.Add(label);
        }
    }
}
