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

    /// <summary>Applies a resource from the active game's theme or the caller's resource dictionaries.</summary>
    public static T WithResource<T>(T element, DependencyProperty property, object key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static Popup PopupFor(UIElement anchor, FrameworkElement content, PlacementMode placement = PlacementMode.Bottom) =>
        new() { PlacementTarget = anchor, Placement = placement, StaysOpen = false, Child = content };

    public static ToolBar Toolbar(params FrameworkElement[] items)
    {
        var toolbar = new ToolBar();
        foreach (var item in items) toolbar.Items.Add(item);
        return toolbar;
    }

    public static ItemsControl Items(System.Collections.IEnumerable source, DataTemplate? template = null) =>
        new() { ItemsSource = source, ItemTemplate = template };

    public static StackPanel ColorPicker(Color initial, Action<Color>? changed = null)
    {
        var brush = new SolidColorBrush(initial);
        var red = Range(initial.R, 0, 255, 1);
        var green = Range(initial.G, 0, 255, 1);
        var blue = Range(initial.B, 0, 255, 1);
        var panel = Stack(new Border { Height = 20, Background = brush },
            FormRow("Red", red, 55), FormRow("Green", green, 55), FormRow("Blue", blue, 55));
        red.ValueChanged += (_, _) => Refresh();
        green.ValueChanged += (_, _) => Refresh();
        blue.ValueChanged += (_, _) => Refresh();
        return panel;

        void Refresh()
        {
            var color = Color.FromArgb(initial.A, (byte)red.Value, (byte)green.Value, (byte)blue.Value);
            brush.Color = color;
            changed?.Invoke(color);
        }
    }

    public static SeapowerUI.FilePicker FilePicker(SeapowerUI.ViewModels.FilePickerViewModel model) =>
        new() { DataContext = model };

    /// <summary>Uses Sea Power's file picker and returns the selected path to the caller.</summary>
    public static void ShowFilePicker(UIElement host, SeapowerUI.ViewModels.FilePickerViewModel model, Action<string> selected)
    {
        model.Initialise();
        model.ButtonCommand = new SeaPower.DelegateCommand(_ =>
        {
            string path = model.FullName;
            if (string.IsNullOrEmpty(path)) return;
            SeapowerUI.WindowHostCommands.CloseWindowCommand.Execute(model, host);
            selected(path);
        });
        SeapowerUI.WindowHostCommands.OpenWindowCommand.Execute(model, host);
    }
}
