using Noesis;
using UI;
using SeapowerUI;

namespace Quickload;

internal class StartupProgressPanel
{
    private readonly TextBlock label = Text(15);
    private readonly TextBlock timing = Text(13);
    private readonly TextBlock stage = Text(13);
    private readonly SegmentedProgress segments = new("QuickloadPhase");
    private UserControl? host;
    private UIElement? nativeContent;
    private Grid? startupRoot;
    private readonly Dictionary<FrameworkElement, (BindingBase? Binding, Visibility? Value)> hidden = [];

    internal Border View { get; }

    internal StartupProgressPanel()
    {
        var stack = new StackPanel();
        stack.Children.Add(label);
        segments.Content.Name = "QuickloadPhases";
        segments.Content.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(segments.Content);
        stack.Children.Add(timing);
        stack.Children.Add(stage);
        View = new Border
        {
            Child = stack, Background = new SolidColorBrush(Color.FromArgb(240, 35, 38, 40)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        };
        Layout(1);
    }

    internal bool ShowOnLoadingScreen()
    {
        if (host != null) return true;
        if (LoadScreenView.Instance is not { Content: UIElement content } view) return false;
        host = view;
        nativeContent = content;
        startupRoot = new Grid();
        view.Content = null;
        startupRoot.Children.Add(content);
        startupRoot.Children.Add(View);
        view.Content = startupRoot;
        HideNativeProgress(content);
        return true;
    }

    internal int HideNativeProgress()
    {
        if (nativeContent != null) HideNativeProgress(nativeContent);
        return hidden.Count;
    }

    private void HideNativeProgress(DependencyObject element)
    {
        if (element is FrameworkElement progress && IsNativeProgress(progress) && !hidden.ContainsKey(progress))
        {
            var property = UIElement.VisibilityProperty;
            hidden.Add(progress, (BindingOperations.GetBindingBase(progress, property), progress.ReadLocalValue(property) as Visibility?));
            progress.SetValue(property, Visibility.Collapsed);
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            HideNativeProgress(VisualTreeHelper.GetChild(element, i));
    }

    private static bool IsNativeProgress(FrameworkElement element) => element switch
    {
        ProgressBar bar => BoundTo(bar, RangeBase.ValueProperty, "LoadPercent"),
        TextBlock text => BoundTo(text, TextBlock.TextProperty, "LoadText"),
        _ => false,
    };

    private static bool BoundTo(DependencyObject element, DependencyProperty property, string path) =>
        BindingOperations.GetBindingBase(element, property) is Binding binding && binding.Path?.Path == path;

    internal void Detach()
    {
        startupRoot?.Children.Remove(View);
    }

    internal void RestoreNative()
    {
        foreach (var (element, (binding, value)) in hidden)
        {
            if (binding != null) BindingOperations.SetBinding(element, UIElement.VisibilityProperty, binding);
            else if (value is Visibility visibility) element.SetValue(UIElement.VisibilityProperty, visibility);
            else element.ClearValue(UIElement.VisibilityProperty);
        }
        hidden.Clear();
        if (host != null && nativeContent != null)
        {
            startupRoot?.Children.Remove(View);
            startupRoot?.Children.Remove(nativeContent);
            host.Content = nativeContent;
        }
        host = null;
        startupRoot = null;
        nativeContent = null;
    }

    internal void Layout(float wide, float width = 0, float height = 0)
    {
        float large = Math.Max(280, Math.Min(820, (width > 0 ? width : 1200) - 48));
        View.Width = Math.Min(440, large) + (large - Math.Min(440, large)) * wide;
        View.Padding = new Thickness(12 + 12 * wide);
        View.Margin = new Thickness(0, 0, 24 + Math.Max(0, (width - large) / 2 - 24) * wide,
            24 + Math.Max(0, height * 0.13f - 24) * wide);
        segments.Content.Height = 6 + 8 * wide;
        label.FontSize = 15 + 9 * wide;
        timing.FontSize = stage.FontSize = 13 + 3 * wide;
    }

    internal void Refresh(string text, string? times, string? currentStage, LoadProgress? progress)
    {
        if (label.Text != text) label.Text = text;
        var previous = PreviousLaunch.Current;
        string elapsed = times ?? $"Elapsed {PreviousLaunch.FormatTime(StartupTimeline.Elapsed)} · {previous?.Estimate ?? "Learning ETA"}";
        if (timing.Text != elapsed) timing.Text = elapsed;
        string active = currentStage ?? StartupTimeline.Current;
        if (stage.Text != active) stage.Text = active;
        if (progress != null) segments.Refresh(progress.StageCount, progress.ReadStage);
        else if (previous != null) segments.Refresh(previous.Count, previous.ReadStage);
        else segments.Refresh(StartupTimeline.Count, StartupTimeline.ReadStage);
    }

    private static TextBlock Text(float size) => new()
    {
        FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 5, 0, 0),
    };
}
