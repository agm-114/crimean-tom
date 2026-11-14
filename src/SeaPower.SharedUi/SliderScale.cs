using Noesis;

namespace SeaPower.SharedUi;

public static partial class NativeUi
{
    /// <summary>Labels selected values using the position of the native slider track.</summary>
    public static FrameworkElement SliderScale(Slider slider, params (float Value, string Label)[] notches)
    {
        if (notches.Length == 0) return slider;
        foreach (var (value, _) in notches) ValidateRange(value, slider.Minimum, slider.Maximum);

        bool vertical = slider.Orientation == Orientation.Vertical;
        var labels = new Canvas { IsHitTestVisible = false };
        var panel = new Grid();
        if (vertical)
        {
            panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Grid.SetColumn(labels, 1);
            panel.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRow(labels, 1);
        }
        panel.Children.Add(slider);
        panel.Children.Add(labels);
        var captions = notches.Select(notch =>
        {
            var caption = Text(notch.Label);
            caption.TextWrapping = TextWrapping.NoWrap;
            labels.Children.Add(caption);
            return (notch.Value, Caption: caption);
        }).ToArray();
        panel.LayoutUpdated += (_, _) => PositionLabels();
        return panel;

        void PositionLabels()
        {
            if (!panel.IsVisible || Find<Noesis.Track>(slider, track => track.Name == "PART_Track") is not { Thumb: { } thumb } track)
                return;
            var origin = track.TranslatePoint(new Point(0, 0), labels);
            float thumbSize = vertical ? thumb.ActualHeight : thumb.ActualWidth;
            float trackSize = vertical ? track.ActualHeight : track.ActualWidth;
            float span = slider.Maximum - slider.Minimum;
            float extent = 0;
            foreach (var (value, caption) in captions)
            {
                caption.Measure(new Size(float.PositiveInfinity, float.PositiveInfinity));
                var size = caption.DesiredSize;
                float fraction = span > 0 ? Math.Clamp((value - slider.Minimum) / span, 0, 1) : 0;
                if (vertical != slider.IsDirectionReversed) fraction = 1 - fraction;
                float offset = thumbSize / 2 + fraction * Math.Max(0, trackSize - thumbSize);
                float position = vertical
                    ? Math.Clamp(origin.Y + offset - size.Height / 2, 0, Math.Max(0, labels.ActualHeight - size.Height))
                    : Math.Clamp(origin.X + offset - size.Width / 2, 0, Math.Max(0, labels.ActualWidth - size.Width));
                if (vertical)
                {
                    if (Canvas.GetTop(caption) != position) Canvas.SetTop(caption, position);
                    if (Canvas.GetLeft(caption) != 8) Canvas.SetLeft(caption, 8);
                    extent = Math.Max(extent, size.Width + 8);
                }
                else
                {
                    if (Canvas.GetLeft(caption) != position) Canvas.SetLeft(caption, position);
                    if (Canvas.GetTop(caption) != 0) Canvas.SetTop(caption, 0);
                    extent = Math.Max(extent, size.Height);
                }
            }
            if (vertical)
            {
                if (labels.Width != extent) labels.Width = extent;
            }
            else if (labels.Height != extent) labels.Height = extent;
        }
    }
}
