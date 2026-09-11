using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPower.SharedUi;

// Owns the inserted slot and restores the native toolbar layout when detached.
internal class ToolbarToggle : IDisposable
{
    private Grid? toolbar;
    private Panel? buttonPanel;
    private ColumnDefinition? column;
    private readonly List<(FrameworkElement Element, Thickness Margin)> shifted = [];
    private readonly ToggleButton toggle;
    internal ToggleButton Button => toggle;

    private ToolbarToggle(ToggleButton button) => toggle = button;

    internal static ToolbarToggle? Attach(ToggleButton anchor, string name, string label, string tooltip)
    {
        var button = new ToggleButton
        {
            Name = name, Content = label, ToolTip = tooltip, Style = anchor.Style,
            Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed,
        };
        var attachment = new ToolbarToggle(button);
        return attachment.Insert(anchor) ? attachment : null;
    }

    private bool Insert(ToggleButton anchor)
    {
        // Native buttons sit inside ContentControl templates. Position beside
        // that wrapper without detaching the button or its window broker.
        FrameworkElement slot = anchor;
        var parent = VisualTreeHelper.GetParent(slot);
        while (parent is FrameworkElement element && parent is not Panel)
        {
            slot = element;
            parent = VisualTreeHelper.GetParent(slot);
        }
        if (parent is StackPanel stack)
        {
            buttonPanel = stack;
            stack.Children.Insert(stack.Children.IndexOf(slot) + 1, toggle);
            return true;
        }
        if (parent is not Grid grid) return false;
        int index = Grid.GetColumn(slot) + Grid.GetColumnSpan(slot);
        toolbar = grid;
        if (grid.ColumnDefinitions.Count == 0)
        {
            float right = slot.Margin.Right;
            foreach (FrameworkElement child in grid.Children.OfType<FrameworkElement>())
                if (child.HorizontalAlignment == HorizontalAlignment.Right && child.Margin.Right >= right)
                {
                    shifted.Add((child, child.Margin));
                    var margin = child.Margin;
                    child.Margin = new Thickness(margin.Left, margin.Top, margin.Right + 34, margin.Bottom);
                }
        }
        else
        {
            if (index > grid.ColumnDefinitions.Count) return false;
            foreach (UIElement child in grid.Children)
            {
                int start = Grid.GetColumn(child);
                if (start >= index) Grid.SetColumn(child, start + 1);
                else if (start + Grid.GetColumnSpan(child) > index) Grid.SetColumnSpan(child, Grid.GetColumnSpan(child) + 1);
            }
            column = new ColumnDefinition { Width = GridLength.Auto };
            grid.ColumnDefinitions.Insert(index, column);
        }
        if (column == null)
        {
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            var original = shifted.FirstOrDefault(item => item.Element == slot).Margin;
            toggle.Margin = original;
        }
        else Grid.SetColumn(toggle, index);
        Grid.SetRow(toggle, Grid.GetRow(slot));
        grid.Children.Add(toggle);
        return true;
    }

    public void Dispose()
    {
        if (buttonPanel != null && toggle != null) buttonPanel.Children.Remove(toggle);
        buttonPanel = null;
        if (toolbar != null && toggle != null) toolbar.Children.Remove(toggle);
        foreach (var (element, margin) in shifted) element.Margin = margin;
        shifted.Clear();
        if (toolbar != null && toggle != null && column != null)
        {
            int index = toolbar.ColumnDefinitions.IndexOf(column);
            if (index >= 0)
            {
                toolbar.ColumnDefinitions.Remove(column);
                foreach (UIElement child in toolbar.Children)
                {
                    int start = Grid.GetColumn(child);
                    if (start > index) Grid.SetColumn(child, start - 1);
                    else if (start < index && start + Grid.GetColumnSpan(child) > index) Grid.SetColumnSpan(child, Grid.GetColumnSpan(child) - 1);
                }
            }
        }
        toolbar = null;
        column = null;
    }
}
