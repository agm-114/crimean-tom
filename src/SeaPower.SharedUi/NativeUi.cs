using System;
using System.Security;
using Noesis;

namespace SeaPower.SharedUi;

// Compiled into each consuming mod, never distributed as a separate assembly.
internal static partial class NativeUi
{
    // These controls also live inside expanders and custom tab presenters, where
    // inherited text color can fall back to black despite the game's dark theme.
    private const string TextColor = "#D0D2D6";
    internal static Brush TextBrush { get; } = new SolidColorBrush(Color.FromRgb(208, 210, 214));
    internal static TextBlock Text(string text) => new()
    {
        Text = text, Foreground = TextBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap,
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

    internal static Grid CapacityMeters(FrameworkElement action,
        params (string Label, ProgressBar Bar, TextBlock Value)[] meters)
    {
        if (meters.Length < 2) throw new ArgumentException("An action needs at least two meter rows.", nameof(meters));
        var grid = new Grid();
        foreach (var width in new[] { GridLength.Auto, new GridLength(10), new GridLength(1, GridUnitType.Star), new GridLength(10), GridLength.Auto })
            grid.ColumnDefinitions.Add(new() { Width = width });
        for (int i = 0; i < meters.Length; i++)
        {
            if (i > 0) grid.RowDefinitions.Add(new() { Height = new GridLength(i == 1 ? 5 : 2.5f) });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var (label, bar, value) = meters[i];
            var title = Text(label);
            Grid.SetRow(title, i * 2);
            grid.Children.Add(title);
            var meter = new Grid();
            value.HorizontalAlignment = HorizontalAlignment.Center;
            meter.Children.Add(bar);
            meter.Children.Add(value);
            Grid.SetRow(meter, i * 2);
            Grid.SetColumn(meter, 2);
            Grid.SetColumnSpan(meter, i == 0 ? 3 : 1);
            grid.Children.Add(meter);
        }
        action.Margin = new Thickness(0);
        Grid.SetRow(action, 2);
        Grid.SetRowSpan(action, Math.Max(1, meters.Length * 2 - 3));
        Grid.SetColumn(action, 4);
        grid.Children.Add(action);
        return grid;
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
        Column(header, width, $"<TextBlock Text=\"{{Binding {SecurityElement.Escape(property)}}}\" Foreground=\"{TextColor}\" Margin=\"4,2\" VerticalAlignment=\"Center\"/>");

    internal static GridViewColumn ContentColumn(string header, string property, float width) =>
        Column(header, width, $"<ContentControl Content=\"{{Binding {SecurityElement.Escape(property)}}}\" HorizontalAlignment=\"Left\"/>");

    internal static GridViewColumn CheckColumn(string header, string property, float width) =>
        Column(header, width, $"<CheckBox IsChecked=\"{{Binding {SecurityElement.Escape(property)}, Mode=TwoWay}}\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/>");

    internal static GridViewColumn ProgressColumn(string header, string property, float width) =>
        Column(header, width, $"<ProgressBar Value=\"{{Binding {SecurityElement.Escape(property)}}}\" Maximum=\"100\" Height=\"14\" Margin=\"4,2\" VerticalAlignment=\"Center\"/>");

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
        Header = Text(header), Width = width,
        CellTemplate = (DataTemplate)GUI.ParseXaml(
            $"<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">{template}</DataTemplate>"),
    };
}
