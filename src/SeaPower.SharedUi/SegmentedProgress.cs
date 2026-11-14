using Noesis;

namespace SeaPower.SharedUi;

public class SegmentedProgress(string segmentName, float gap = 1, float minimumWidth = 3)
{
    private readonly List<ProgressBar> bars = [];
    public Grid Content { get; } = new();

    public void Refresh(int count, Func<int, (string Tooltip, float Weight, float Fraction, bool Indeterminate)> read)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        while (bars.Count > count)
        {
            int index = bars.Count - 1;
            Content.Children.RemoveAt(index);
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
        }
    }
}
