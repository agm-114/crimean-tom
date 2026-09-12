using System;
using Noesis;

namespace SeaPower.SharedUi;

internal static partial class NativeUi
{
    internal static UniformGrid Columns(int count, params FrameworkElement[] children)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var grid = new UniformGrid { Columns = count };
        foreach (var child in children) grid.Children.Add(child);
        return grid;
    }

    // Keep file selection policy and dialogs in the caller.
    internal static Grid BrowseField(TextBox path, Action browse)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(path);
        var button = Button("...", browse);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    internal static StackPanel Stack(params FrameworkElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    internal static Grid FormRow(string label, FrameworkElement editor, float labelWidth = 180)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Text(label));
        Grid.SetColumn(editor, 2);
        grid.Children.Add(editor);
        return grid;
    }

    internal static ScrollViewer Scroll(FrameworkElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    internal static Border Card(FrameworkElement content) => new()
    {
        Child = content, Padding = new Thickness(12), BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromRgb(59, 62, 67)),
        Background = new SolidColorBrush(Color.FromRgb(36, 38, 41)),
    };

    internal static Expander Section(string title, FrameworkElement content, bool expanded = true) => new()
    {
        Header = title, Content = content, IsExpanded = expanded,
        Margin = new Thickness(0, 4, 0, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    internal static TabControl Tabs(params (string Title, FrameworkElement Content)[] pages)
    {
        var tabs = new TabControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        foreach (var (title, content) in pages) tabs.Items.Add(new TabItem { Header = title, Content = content });
        tabs.SelectedIndex = pages.Length > 0 ? 0 : -1;
        return tabs;
    }

    internal static Grid Split(FrameworkElement first, FrameworkElement second, bool vertical = false, float firstWeight = 1, float secondWeight = 2)
    {
        if (firstWeight <= 0 || secondWeight <= 0 || float.IsNaN(firstWeight) || float.IsNaN(secondWeight)
            || float.IsInfinity(firstWeight) || float.IsInfinity(secondWeight)) throw new ArgumentOutOfRangeException(nameof(firstWeight));
        var grid = new Grid();
        var splitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };
        foreach (var size in new[] { new GridLength(firstWeight, GridUnitType.Star), new GridLength(6), new GridLength(secondWeight, GridUnitType.Star) })
        {
            if (vertical) grid.RowDefinitions.Add(new() { Height = size });
            else grid.ColumnDefinitions.Add(new() { Width = size });
        }
        if (vertical) { Grid.SetRow(splitter, 1); Grid.SetRow(second, 2); }
        else { Grid.SetColumn(splitter, 1); Grid.SetColumn(second, 2); }
        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        return grid;
    }

    internal static TreeViewItem Branch(string label, object? value = null, params TreeViewItem[] children)
    {
        var item = new TreeViewItem { Header = label, Tag = value };
        foreach (var child in children) item.Items.Add(child);
        return item;
    }

    internal static TreeView Tree(params TreeViewItem[] roots)
    {
        var tree = new TreeView();
        foreach (var root in roots) tree.Items.Add(root);
        return tree;
    }

    internal static ListBox List(params object[] items)
    {
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items) list.Items.Add(item);
        return list;
    }

    internal static Grid CollectionEditor(ListBox list, Action add, Action<object> remove)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var removeButton = Button("Remove", () => { if (list.SelectedItem is { } item) remove(item); });
        removeButton.IsEnabled = list.SelectedItem != null;
        list.SelectionChanged += (_, _) => removeButton.IsEnabled = list.SelectedItem != null;
        buttons.Children.Add(Button("Add", add));
        buttons.Children.Add(removeButton);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    internal static WrapPanel Wrap(params FrameworkElement[] children)
    {
        var panel = new WrapPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    internal static Grid SearchBar(TextBox search, params FrameworkElement[] filters)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(search);
        var options = Wrap(filters);
        options.Margin = new Thickness(0, 5, 0, 5);
        Grid.SetRow(options, 1);
        grid.Children.Add(options);
        return grid;
    }

    internal static MenuItem MenuAction(string label, Action execute, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        item.Click += (_, _) => execute();
        return item;
    }

    internal static ContextMenu Menu(params MenuItem[] items)
    {
        var menu = new ContextMenu();
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    internal static Image Picture(ImageSource source, float height = 120) => new()
    {
        Source = source, Height = height, Stretch = Stretch.Uniform,
    };

    internal static T? Find<T>(DependencyObject root) where T : FrameworkElement
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
