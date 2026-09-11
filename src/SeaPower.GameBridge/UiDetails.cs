using System.Reflection;
using Noesis;

namespace SeaPower.GameBridge;

internal static class UiDetails
{
    private const int NodeLimit = 2000;
    internal static object Read(FrameworkElement root, int id, string snapshot, int depth)
    {
        if (depth is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be between 0 and 32.");
        int count = 0;
        bool truncated = false;
        List<object> ancestors = [];
        var ancestor = VisualTreeHelper.GetParent(root);
        while (ancestor != null && ancestors.Count < 64)
        {
            ancestors.Add(Describe(ancestor));
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }
        object tree = Walk(root, "0", 0);
        return new { snapshot, id, depth, nodeCount = count, nodeLimit = NodeLimit, truncated, ancestors, tree };

        object Walk(DependencyObject node, string path, int level)
        {
            count++;
            var result = Describe(node);
            result["path"] = path;
            int children = VisualTreeHelper.GetChildrenCount(node);
            result["childCount"] = children;
            List<object> expanded = [];
            if (level < depth)
                for (int i = 0; i < children && count < NodeLimit; i++) expanded.Add(Walk(VisualTreeHelper.GetChild(node, i), path + "/" + i, level + 1));
            result["children"] = expanded;
            if (expanded.Count < children) { result["childrenTruncated"] = true; truncated = true; }
            return result;
        }
    }

    private static Dictionary<string, object?> Describe(DependencyObject node)
    {
        Dictionary<string, object?> result = new() { ["type"] = node.GetType().FullName };
        if (node is not FrameworkElement element) return result;
        result["name"] = element.Name;
        result["dataContextType"] = element.DataContext?.GetType().FullName;
        result["logicalParent"] = Identity(element.Parent);
        result["visualParent"] = Identity(VisualTreeHelper.GetParent(element));
        result["layout"] = Properties(element, "Width", "Height", "ActualWidth", "ActualHeight", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
            "Margin", "Padding", "HorizontalAlignment", "VerticalAlignment", "Visibility", "IsVisible", "IsEnabled", "Opacity", "ClipToBounds",
            "RenderTransform", "LayoutTransform", "HorizontalContentAlignment", "VerticalContentAlignment");
        result["grid"] = new { row = Grid.GetRow(element), column = Grid.GetColumn(element), rowSpan = Grid.GetRowSpan(element), columnSpan = Grid.GetColumnSpan(element) };
        result["canvas"] = new { left = Value(Canvas.GetLeft(element)), top = Value(Canvas.GetTop(element)), right = Value(Canvas.GetRight(element)), bottom = Value(Canvas.GetBottom(element)), zIndex = Panel.GetZIndex(element) };
        result["appearance"] = Properties(element, "Background", "Foreground", "BorderBrush", "BorderThickness", "CornerRadius",
            "FontFamily", "FontSize", "FontWeight", "FontStyle", "TextAlignment", "TextWrapping", "TextTrimming");
        result["content"] = Properties(element, "Text", "Header", "Content", "ToolTip", "IsChecked", "SelectedIndex", "IsDropDownOpen");
        result["localValues"] = LocalValues(element);
        if (element is Control control)
        {
            result["templateType"] = control.Template?.GetType().FullName;
            result["style"] = StyleDetails(control.Style, 0);
        }
        if (element is Grid grid)
        {
            result["columns"] = grid.ColumnDefinitions.Select(column => Properties(column, "Width", "MinWidth", "MaxWidth", "ActualWidth")).ToArray();
            result["rows"] = grid.RowDefinitions.Select(row => Properties(row, "Height", "MinHeight", "MaxHeight", "ActualHeight")).ToArray();
        }
        if (element is ListView { View: GridView view })
            result["tableColumns"] = view.Columns.Select(column => Properties(column, "Header", "Width", "ActualWidth", "CellTemplate", "DisplayMemberBinding")).ToArray();
        return result;
    }

    private static object? Identity(DependencyObject? value) => value == null ? null : new { type = value.GetType().FullName, name = (value as FrameworkElement)?.Name };
    private static Dictionary<string, object?> Properties(object source, params string[] names)
    {
        Dictionary<string, object?> values = [];
        foreach (string name in names)
        {
            var property = source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(item => item.Name == name && item.CanRead && item.GetIndexParameters().Length == 0);
            if (property?.CanRead != true || property.GetIndexParameters().Length != 0) continue;
            try { values[name] = Value(property.GetValue(source)); }
            catch (Exception error) { values[name] = new { error = error.GetBaseException().Message }; }
        }
        return values;
    }

    private static object? StyleDetails(Style? style, int depth)
    {
        if (style == null) return null;
        if (depth >= 4) return new { truncated = true };
        var result = Properties(style, "TargetType");
        result["basedOn"] = StyleDetails(style.BasedOn, depth + 1);
        result["setters"] = style.Setters.Cast<object>().Take(128).Select(setter => Properties(setter, "Property", "Value", "TargetName")).ToArray();
        result["settersTruncated"] = style.Setters.Count > 128;
        return result;
    }

    private static Dictionary<string, object?> LocalValues(FrameworkElement element)
    {
        Dictionary<string, object?> values = [];
        for (Type? type = element.GetType(); type != null && typeof(DependencyObject).IsAssignableFrom(type); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(DependencyProperty)) continue;
                try
                {
                    if (field.GetValue(null) is not DependencyProperty property) continue;
                    object value = element.ReadLocalValue(property);
                    if (value != DependencyProperty.UnsetValue) values[type.Name + "." + field.Name] = Value(value);
                }
                catch (Exception error) { values[field.Name] = new { error = error.GetBaseException().Message }; }
            }
        return values;
    }

    private static object? Value(object? value) => value switch
    {
        null => null,
        string text => text.Length > 2000 ? text[..2000] + "..." : text,
        bool or int or uint or long or short or byte => value,
        float number => float.IsNaN(number) || float.IsInfinity(number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : number,
        double number => double.IsNaN(number) || double.IsInfinity(number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : number,
        Enum => value.ToString(),
        Thickness thickness => new { thickness.Left, thickness.Top, thickness.Right, thickness.Bottom },
        SolidColorBrush brush => new { type = "SolidColorBrush", color = brush.Color.ToString(), opacity = brush.Opacity },
        GridLength length => length.ToString(),
        Binding binding => Properties(binding, "Path", "Mode", "ElementName", "RelativeSource", "UpdateSourceTrigger"),
        PropertyPath path => path.Path,
        Type type => type.FullName,
        DependencyProperty property => property.Name,
        _ => new { type = value.GetType().FullName },
    };
}
