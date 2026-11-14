using Noesis;

namespace SeaPower.SharedUi;

public static partial class NativeUi
{
    public static ProgressBar Progress(float value, float maximum = 100, bool indeterminate = false)
    {
        ValidateRange(value, 0, maximum);
        return new() { Maximum = maximum, Value = value, IsIndeterminate = indeterminate };
    }

    public static Separator Divider() => new() { Margin = new Thickness(0, 6, 0, 6) };

    public static RepeatButton Repeat(string label, Action action)
    {
        var button = new RepeatButton { Content = label };
        button.Click += (_, _) => action();
        return button;
    }

    public static DockPanel Docked(FrameworkElement body, params (Dock Side, FrameworkElement Content)[] edges)
    {
        var panel = new DockPanel { LastChildFill = true };
        foreach (var (side, content) in edges)
        {
            DockPanel.SetDock(content, side);
            panel.Children.Add(content);
        }
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>Creates a native object or template from trusted Noesis XAML supplied by the mod.</summary>
    public static T FromXaml<T>(string xaml) where T : class =>
        GUI.ParseXaml(xaml) as T ?? throw new ArgumentException($"XAML must create a {typeof(T).Name}.", nameof(xaml));
}
