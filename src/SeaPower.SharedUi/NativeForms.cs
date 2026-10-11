using System;
using System.Collections.Generic;
using System.Globalization;
using Noesis;

namespace SeaPower.SharedUi;

internal static partial class NativeUi
{
    internal static ToggleButton Toggle(string label, bool value, Action<bool>? changed = null)
    {
        var button = new ToggleButton { Content = label, IsChecked = value };
        button.Checked += (_, _) => changed?.Invoke(true);
        button.Unchecked += (_, _) => changed?.Invoke(false);
        return button;
    }

    // The caller owns limits and availability, as with native damage-control teams.
    internal static Grid Stepper(TextBlock value, Button decrease, Button increase)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        value.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(value, 1);
        Grid.SetColumn(increase, 2);
        grid.Children.Add(decrease);
        grid.Children.Add(value);
        grid.Children.Add(increase);
        return grid;
    }

    internal static CheckBox Check(string label, bool value, Action<bool>? changed = null)
    {
        var box = new CheckBox { Content = label, Foreground = TextBrush, IsChecked = value, VerticalContentAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) => changed?.Invoke(true);
        box.Unchecked += (_, _) => changed?.Invoke(false);
        return box;
    }

    internal static RadioButton Radio(string label, string group, bool selected, Action? selectedAction = null)
    {
        var button = new RadioButton { Content = label, GroupName = group, IsChecked = selected };
        button.Checked += (_, _) => selectedAction?.Invoke();
        return button;
    }

    internal static CheckBox TriState(string label, bool? value, Action<bool?>? changed = null)
    {
        var box = new CheckBox { Content = label, IsThreeState = true, IsChecked = value };
        box.Checked += (_, _) => changed?.Invoke(true);
        box.Unchecked += (_, _) => changed?.Invoke(false);
        box.Indeterminate += (_, _) => changed?.Invoke(null);
        return box;
    }

    internal static ComboBox Choice<T>(IEnumerable<T> values, Func<T, string> label,
        int selected = 0, Action<T>? changed = null, Func<T, string>? tooltip = null)
    {
        var box = new ComboBox { Foreground = TextBrush, MinHeight = 30, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var value in values)
            box.Items.Add(new ComboBoxItem { Content = label(value), Tag = value, ToolTip = tooltip?.Invoke(value) });
        if (selected < -1 || selected >= Math.Max(1, box.Items.Count))
            throw new ArgumentOutOfRangeException(nameof(selected));
        box.SelectedIndex = box.Items.Count == 0 ? -1 : selected;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: T value }) changed?.Invoke(value);
        };
        return box;
    }

    internal static TextBox Entry(string text, Action<string>? changed = null, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = text, Foreground = TextBrush, MinHeight = 30, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => changed?.Invoke(box.Text);
        return box;
    }

    // Partial edits remain visible but never reach the model. Losing focus
    // restores the last valid value instead of silently clamping an invalid edit.
    internal static TextBox Number(double value, double minimum, double maximum, Action<double>? changed = null)
    {
        ValidateRange(value, minimum, maximum);
        var box = Entry(value.ToString(CultureInfo.CurrentCulture));
        double accepted = value;
        box.TextChanged += (_, _) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
                || double.IsNaN(number) || number < minimum || number > maximum)
            {
                box.ToolTip = $"Enter a number from {minimum} to {maximum}.";
                return;
            }
            box.ToolTip = null;
            if (accepted == number) return;
            accepted = number;
            changed?.Invoke(number);
        };
        box.LostFocus += (_, _) => box.Text = accepted.ToString(CultureInfo.CurrentCulture);
        return box;
    }

    internal static Slider Range(float value, float minimum, float maximum, float step, Action<float>? changed = null)
    {
        ValidateRange(value, minimum, maximum);
        if (!float.IsFinite(step) || step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        var slider = new Slider
        {
            Minimum = minimum, Maximum = maximum, Value = value, SmallChange = step,
            LargeChange = (float)Math.Min(float.MaxValue, (double)step * 10), TickFrequency = step, IsSnapToTickEnabled = true, MinWidth = 80,
        };
        slider.ValueChanged += (_, _) => changed?.Invoke(slider.Value);
        return slider;
    }

    internal static StackPanel SliderField(string label, Slider slider, Func<float, string>? format = null)
    {
        format ??= value => value.ToString("0.##", CultureInfo.CurrentCulture);
        var valueText = Text(format(slider.Value));
        var panel = Stack(Text(label), slider, valueText);
        slider.ValueChanged += (_, _) => valueText.Text = format(slider.Value);
        return panel;
    }

    internal static T WithTooltip<T>(T control, string text) where T : FrameworkElement
    {
        control.ToolTip = new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } };
        return control;
    }

    internal static void Bind(FrameworkElement target, DependencyProperty property, string path,
        object source, BindingMode mode = BindingMode.OneWay) =>
        target.SetBinding(property, new Binding(path) { Source = source, Mode = mode });

    private static void ValidateRange(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum)
            || minimum > maximum) throw new ArgumentException("Bounds must be finite and ordered.");
        if (double.IsNaN(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
    }
}
