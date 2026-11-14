using System;
using System.Collections.Generic;
using System.Globalization;
using Noesis;

namespace SeaPower.SharedUi;

public static partial class NativeUi
{
    public static ToggleButton Toggle(string label, bool value, Action<bool>? changed = null)
    {
        var button = new ToggleButton { Content = label, IsChecked = value };
        button.Checked += (_, _) => changed?.Invoke(true);
        button.Unchecked += (_, _) => changed?.Invoke(false);
        return button;
    }

    // The caller owns limits and availability, as with native damage-control teams.
    public static Panel Stepper(TextBlock value, ButtonBase decrease, ButtonBase increase, bool compact = false)
    {
        if (compact)
        {
            var buttons = Stack(increase, decrease);
            buttons.Margin = new Thickness(3, 0, 0, 0);
            var row = Stack(value, buttons);
            row.Orientation = Orientation.Horizontal;
            return row;
        }
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

    public static CheckBox Check(string label, bool value, Action<bool>? changed = null)
    {
        var box = new CheckBox { Content = label, Foreground = TextBrush, IsChecked = value, VerticalContentAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) => changed?.Invoke(true);
        box.Unchecked += (_, _) => changed?.Invoke(false);
        return box;
    }

    public static RadioButton Radio(string label, string group, bool selected, Action? selectedAction = null)
    {
        var button = new RadioButton { Content = label, GroupName = group, IsChecked = selected };
        button.Checked += (_, _) => selectedAction?.Invoke();
        return button;
    }

    public static CheckBox TriState(string label, bool? value, Action<bool?>? changed = null)
    {
        var box = new CheckBox { Content = label, IsThreeState = true, IsChecked = value };
        box.Checked += (_, _) => changed?.Invoke(true);
        box.Unchecked += (_, _) => changed?.Invoke(false);
        box.Indeterminate += (_, _) => changed?.Invoke(null);
        return box;
    }

    public static ComboBox Choice<T>(IEnumerable<T> values, Func<T, string> label,
        int selected = 0, Action<T>? changed = null, Func<T, string>? tooltip = null)
    {
        var picker = new ChoicePicker<T>(values.Select(value => (label(value), value)), selected, tooltip: tooltip);
        if (changed != null) picker.Control.SelectionChanged += (_, _) =>
        {
            if (picker.TryGetValue(out T value)) changed(value);
        };
        return picker.Control;
    }

    public static TextBox Entry(string text, Action<string>? changed = null, bool multiline = false)
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

    // Callers that validate the whole form can keep invalid text visible until it is corrected.
    public static TextBox Number(double value, double minimum, double maximum, Action<double>? changed = null,
        Action<string?>? validation = null)
    {
        ValidateRange(value, minimum, maximum);
        var box = Entry(value.ToString(CultureInfo.CurrentCulture));
        double accepted = value;
        box.TextChanged += (_, _) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
                || double.IsNaN(number) || number < minimum || number > maximum)
            {
                string error = $"Enter a number from {minimum} to {maximum}.";
                box.ToolTip = error;
                validation?.Invoke(error);
                return;
            }
            box.ToolTip = null;
            validation?.Invoke(null);
            if (accepted == number) return;
            accepted = number;
            changed?.Invoke(number);
        };
        if (validation == null) box.LostFocus += (_, _) => box.Text = accepted.ToString(CultureInfo.CurrentCulture);
        return box;
    }

    public static Slider Range(float value, float minimum, float maximum, float step, Action<float>? changed = null)
        => Range(value, minimum, maximum, step, Orientation.Horizontal, changed);

    public static Slider Range(float value, float minimum, float maximum, float step, Orientation orientation,
        Action<float>? changed = null)
    {
        ValidateRange(value, minimum, maximum);
        if (!float.IsFinite(step) || step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        var slider = new Slider
        {
            Minimum = minimum, Maximum = maximum, Value = value, SmallChange = step,
            LargeChange = (float)Math.Min(float.MaxValue, (double)step * 10), TickFrequency = step,
            IsSnapToTickEnabled = true, Orientation = orientation,
            MinWidth = orientation == Orientation.Horizontal ? 80 : 0,
            MinHeight = orientation == Orientation.Vertical ? 90 : 0,
        };
        slider.ValueChanged += (_, _) => changed?.Invoke(slider.Value);
        return slider;
    }

    /// <summary>Native speed and depth style, with visual ticks and a continuous value.</summary>
    public static Slider VerticalRange(float value, float minimum, float maximum, float step,
        Action<float>? changed = null, bool reversed = false, string? ticks = null)
    {
        var slider = Range(value, minimum, maximum, step, Orientation.Vertical, changed);
        slider.HorizontalAlignment = HorizontalAlignment.Center;
        slider.TickPlacement = TickPlacement.BottomRight;
        slider.IsDirectionReversed = reversed;
        slider.IsSnapToTickEnabled = false;
        if (ticks != null) slider.Ticks = ticks;
        slider.SetResourceReference(Control.BackgroundProperty, "Brush.Foreground.Normal");
        return slider;
    }

    /// <summary>Combines a slider with an editable numeric value and optional units.</summary>
    public static StackPanel SliderEntry(Slider slider, string unit = "") => SliderEntry(slider, unit, []);

    public static StackPanel SliderEntry(Slider slider, string unit, params (float Value, string Label)[] notches)
    {
        bool syncing = false;
        var number = Number(slider.Value, slider.Minimum, slider.Maximum, value =>
        {
            if (!syncing) slider.Value = (float)value;
        });
        number.MinWidth = 60;
        slider.ValueChanged += (_, _) => { if (!number.IsKeyboardFocused) Sync(); };
        number.LostFocus += (_, _) => Sync();
        var header = Wrap(number, Text(unit));
        header.HorizontalAlignment = HorizontalAlignment.Center;
        return Stack(header, SliderScale(slider, notches));

        void Sync()
        {
            syncing = true;
            try { number.Text = slider.Value.ToString("G9", CultureInfo.CurrentCulture); }
            finally { syncing = false; }
        }
    }

    public static StackPanel SliderField(string label, Slider slider, Func<float, string>? format = null) =>
        SliderField(label, slider, format, []);

    public static StackPanel SliderField(string label, Slider slider, Func<float, string>? format,
        params (float Value, string Label)[] notches)
        => Stack(Text(label), SliderScale(slider, notches), RangeValue(slider, format));

    public static T WithTooltip<T>(T control, string text) where T : FrameworkElement
    {
        control.ToolTip = new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } };
        return control;
    }

    public static void Bind(FrameworkElement target, DependencyProperty property, string path,
        object source, BindingMode mode = BindingMode.OneWay) =>
        target.SetBinding(property, new Binding(path) { Source = source, Mode = mode });

    private static void ValidateRange(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum)
            || minimum > maximum) throw new ArgumentException("Bounds must be finite and ordered.");
        if (double.IsNaN(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
    }
}
