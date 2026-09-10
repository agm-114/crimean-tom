using Noesis;
using SeapowerUI;

namespace SeaPower.StartupTweaks;

internal class MenuProgress
{
    private readonly Border panel;
    private readonly TextBlock label;
    private readonly TextBlock timing;
    private readonly TextBlock files;
    private readonly ProgressBar bar;
    private readonly List<(Button Button, bool Enabled, string Action)> buttons = [];

    internal MenuProgress(MainMenuView menu)
    {
        var original = (UIElement)menu.Content;
        Collect(original);
        menu.Content = null;
        Grid root = new();
        root.Children.Add(original);
        label = new() { FontSize = 15, Text = "Loading startup data...", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        timing = DetailLine();
        files = DetailLine();
        bar = new() { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 0) };
        StackPanel stack = new();
        stack.Children.Add(label);
        stack.Children.Add(bar);
        stack.Children.Add(timing);
        stack.Children.Add(files);
        panel = new()
        {
            Child = stack, Background = new SolidColorBrush(Color.FromArgb(240, 35, 38, 40)), Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Width = 520, Margin = new Thickness(0, 24, 24, 0), IsHitTestVisible = false,
        };
        root.Children.Add(panel);
        menu.Content = root;
    }

    private void Collect(DependencyObject element)
    {
        if (element is Button button)
        {
            string action = StartupTweaks.MenuActions.TryGetValue(button, out string name) ? name
                : button.DataContext is SeapowerUI.ViewModels.MainMenuViewModel model && ReferenceEquals(button.Command, model.ContinueCommand)
                    ? "Continue" : "";
            if (action.Length > 0) buttons.Add((button, button.IsEnabled, action));
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++) Collect(VisualTreeHelper.GetChild(element, index));
    }

    internal void Update(bool ready, bool failed, LoadProgress progress)
    {
        for (int index = buttons.Count - 1; index >= 0; index--)
        {
            var (button, enabled, action) = buttons[index];
            bool available = StartupTweaks.Instance.CanOpen(action);
            button.SetCurrentValue(UIElement.IsEnabledProperty, available && enabled);
            if (available) buttons.RemoveAt(index);
        }
        failed |= progress.Lanes.Any(lane => lane.Failed);
        if (failed) label.Text = "Startup loading failed. Check BepInEx/LogOutput.log.";
        else label.Text = ready ? "Background loading" : "Loading game data";
        double? percent = progress.Percent;
        bar.IsIndeterminate = percent == null;
        bar.Value = (float)(percent ?? 0);
        timing.Text = (percent is { } value ? $"{value:0}% | " : "") + progress.Eta();
        files.Text = string.Join("\n", progress.Lanes.Where(lane => lane.Current != null).Select(Describe));
        panel.Visibility = !failed && progress.Lanes.All(lane => lane.Done) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock DetailLine() => new()
    {
        FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };

    private static string Describe(LoadLane lane)
    {
        string file = lane.File.Replace('\\', '/');
        string root = UnityEngine.Application.streamingAssetsPath.Replace('\\', '/').TrimEnd('/') + "/";
        if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) file = file[root.Length..];
        return Friendly(lane.Current!) + (file.Length == 0 ? "" : $"\n{file}");
    }

    private static string Friendly(string stage) => stage switch
    {
        "VoiceMessage_LoadClips" => "Voice clips",
        "UnitMetadataProvider LoadUnitMetadata" => "Unit metadata",
        "EncyclopedicDataManager.GetMuseumEntries" => "Encyclopedia",
        "WriteTacViewDatabase" => "Tacview database",
        _ => stage.StartsWith("music:", StringComparison.Ordinal) ? "Music" : stage,
    };
}
