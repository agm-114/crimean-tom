using System;
using System.Security;
using Noesis;

namespace SeaPower.SharedUi;

// Compiled into each consuming mod, never distributed as a separate assembly.
internal static class NativeUi
{
    internal static TextBlock Text(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal static FrameworkElement? Find(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement element && element.Name == name) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find(VisualTreeHelper.GetChild(parent, i), name) is { } found) return found;
        return null;
    }

    internal static StackPanel Labelled(string label, FrameworkElement control)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(label));
        stack.Children.Add(control);
        return stack;
    }

    internal static Grid Meter(string label, ProgressBar bar, TextBlock value, FrameworkElement? action = null)
    {
        var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        line.ColumnDefinitions.Add(new() { Width = new GridLength(120) });
        line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new() { Width = new GridLength(90) });
        line.Children.Add(Text(label));
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(value, 1);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(bar);
        line.Children.Add(value);
        if (action != null) { Grid.SetColumn(action, 2); line.Children.Add(action); }
        return line;
    }

    internal static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = label, FontSize = 12, Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(2, 0, 2, 0), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (primary) button.Foreground = new SolidColorBrush(Color.FromRgb(213, 168, 118));
        button.Click += (_, _) => action();
        return button;
    }

    internal static GridViewColumn TextColumn(string header, string property, float width) =>
        Column(header, width, $"<TextBlock Text=\"{{Binding {SecurityElement.Escape(property)}}}\" Margin=\"4,2\" VerticalAlignment=\"Center\"/>");

    internal static GridViewColumn ContentColumn(string header, string property, float width) =>
        Column(header, width, $"<ContentControl Content=\"{{Binding {SecurityElement.Escape(property)}}}\" HorizontalAlignment=\"Left\"/>");

    internal static ListView Table(string name, params GridViewColumn[] columns)
    {
        var view = new GridView();
        foreach (var column in columns) view.Columns.Add(column);
        return new ListView { Name = name, View = view, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    internal static SeapowerUI.Window Window(string name, string title, FrameworkElement content,
        float width = 760, float height = 480, float minWidth = 600, float minHeight = 300)
    {
        var window = new SeapowerUI.Window
        {
            Name = name, Header = title, Content = content, Width = width, Height = height,
            MinWidth = minWidth, MinHeight = minHeight, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Draggable = true, CanResize = true, X = 40, Y = 60,
        };
        window.DataContext = window;
        window.Loaded += (_, _) =>
        {
            // Constrain the outer panel. Tables inside it own their scrolling.
            DependencyObject parent = content;
            while (parent != null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
            if (parent is not ScrollViewer scroll) return;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            scroll.VerticalContentAlignment = VerticalAlignment.Stretch;
        };
        return window;
    }

    private static GridViewColumn Column(string header, float width, string template) => new()
    {
        Header = header, Width = width,
        CellTemplate = (DataTemplate)GUI.ParseXaml(
            $"<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">{template}</DataTemplate>"),
    };
}
