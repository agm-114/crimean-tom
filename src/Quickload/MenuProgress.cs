using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Noesis;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace Quickload;

[HarmonyPatch]
internal class MenuProgress
{
    private static readonly string[] Gated = ["Scenarios", "Tutorials", "Campaign", "Instant", "Editor", "Encyclopedia", "Options", "ModManager"];
    private static readonly ConditionalWeakTable<MainMenuView, Dictionary<Button, string>> menuActions = new();
    private readonly Border panel;
    private readonly UIElement content;
    private readonly Grid root = new();
    private readonly TextBlock label = new() { FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock timing = DetailLine();
    private readonly TextBlock stage = DetailLine();
    private const float SegmentGap = 1;
    private const float MinSegmentWidth = 3;
    private readonly Grid segments = new() { Name = "QuickloadPhases", Height = 6, Margin = new Thickness(0, 8, 0, 0) };
    private readonly List<ProgressBar> stageBars = [];
    private readonly Dictionary<Button, string> actions;
    private readonly Dictionary<Button, (string Action, BindingBase? Binding, bool? LocalValue)> blocked = [];
    internal MainMenuView Menu { get; }

    internal MenuProgress(MainMenuView menu)
    {
        Menu = menu;
        actions = menuActions.GetOrCreateValue(menu);
        content = (UIElement)menu.Content;
        menu.Content = null;
        root.Children.Add(content);
        StackPanel stack = new();
        stack.Children.Add(label);
        stack.Children.Add(segments);
        stack.Children.Add(timing);
        stack.Children.Add(stage);
        panel = new()
        {
            Child = stack, Background = new SolidColorBrush(Color.FromArgb(240, 35, 38, 40)), Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Width = 440, Margin = new Thickness(0, 0, 24, 24),
        };
        root.Children.Add(panel);
        menu.Content = root;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MainMenuView), MethodType.Constructor)]
    private static void AttachOnCreation(MainMenuView __instance) => StartupLoading.AttachMenu(__instance);

    internal static void LogGates(MainMenuView menu)
    {
        if (!menuActions.TryGetValue(menu, out var buttons)) return;
        UnityEngine.Debug.Log("[Quickload] Menu gates: " + string.Join(", ",
            buttons.Select(pair => $"{pair.Value}={pair.Key.IsEnabled}")));
    }

    internal void Refresh(string text, string? times, string? currentStage, LoadProgress? progress, bool ready)
    {
        if (!ready) BlockButtons(root, Menu.DataContext as MainMenuViewModel);
        foreach (var (button, (action, binding, localValue)) in blocked.ToArray())
        {
            if (!StartupLoading.CanOpen(action)) continue;
            button.IsEnabledChanged -= KeepBlocked;
            if (binding != null) BindingOperations.SetBinding(button, UIElement.IsEnabledProperty, binding);
            else if (localValue is bool enabled) button.SetValue(UIElement.IsEnabledProperty, enabled);
            else button.ClearValue(UIElement.IsEnabledProperty);
            blocked.Remove(button);
        }
        if (ready && ReferenceEquals(Menu.Content, root))
        {
            root.Children.Remove(content);
            Menu.Content = content;
        }
        else
        {
            if (label.Text != text) label.Text = text;
            if (timing.Text != times) timing.Text = times ?? "";
            if (stage.Text != currentStage) stage.Text = currentStage ?? "";
            if (progress != null) RefreshSegments(progress);
        }
    }

    private void RefreshSegments(LoadProgress progress)
    {
        while (stageBars.Count < progress.StageCount)
        {
            if (stageBars.Count > 0) segments.ColumnDefinitions.Add(new() { Width = new GridLength(SegmentGap) });
            ProgressBar bar = new() { Name = $"QuickloadPhase{stageBars.Count + 1}", Maximum = 1 };
            Grid.SetColumn(bar, stageBars.Count * 2);
            segments.ColumnDefinitions.Add(new() { MinWidth = MinSegmentWidth });
            segments.Children.Add(bar);
            stageBars.Add(bar);
        }
        for (int i = 0; i < stageBars.Count; i++)
        {
            var bar = stageBars[i];
            var (tooltip, seconds, fraction, indeterminate) = progress.ReadStage(i);
            if (!Equals(bar.ToolTip, tooltip)) bar.ToolTip = tooltip;
            var width = new GridLength(seconds, GridUnitType.Star);
            var column = segments.ColumnDefinitions[i * 2];
            if (column.Width != width) column.Width = width;
            bar.Value = fraction;
            bar.IsIndeterminate = indeterminate;
        }
    }

    private static TextBlock DetailLine() => new()
    {
        FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };

    private void BlockButtons(DependencyObject element, MainMenuViewModel? model)
    {
        // Noesis may build the visual tree after it connects the XAML buttons.
        foreach (var (button, action) in actions)
            if (!StartupLoading.CanOpen(action)) Block(button, action);
        if (model != null) BlockContinue(element, model);
    }

    private void BlockContinue(DependencyObject element, MainMenuViewModel model)
    {
        if (StartupLoading.CanOpen("Continue")) return;
        if (element is Button button)
        {
            if (ReferenceEquals(button.Command, model.ContinueCommand)) Block(button, "Continue");
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) BlockContinue(VisualTreeHelper.GetChild(element, i), model);
    }

    private void Block(Button button, string action)
    {
        var property = UIElement.IsEnabledProperty;
        if (!blocked.ContainsKey(button))
        {
            blocked.Add(button, (action, BindingOperations.GetBindingBase(button, property), button.ReadLocalValue(property) as bool?));
            button.IsEnabledChanged += KeepBlocked;
        }
        if (button.IsEnabled) button.SetValue(property, false);
    }

    private void KeepBlocked(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Button { IsEnabled: true } button && blocked.TryGetValue(button, out var gate)
            && !StartupLoading.CanOpen(gate.Action)) button.SetValue(UIElement.IsEnabledProperty, false);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MainMenuView), "ConnectEvent")]
    private static void RememberAction(MainMenuView __instance, object source, string eventName, string handlerName, bool __result)
    {
        if (!__result || eventName != "Click" || source is not Button button || !handlerName.StartsWith("Button", StringComparison.Ordinal)
            || !handlerName.EndsWith("_Click", StringComparison.Ordinal)) return;
        string action = ActionName(handlerName);
        if (Gated.Contains(action)) menuActions.GetOrCreateValue(__instance)[button] = action;
    }

    [HarmonyPatch]
    private static class MenuGate
    {
        private static IEnumerable<MethodBase> TargetMethods() => Gated.Select(action => AccessTools.Method(typeof(MainMenuView), $"Button{action}_Click"));
        private static bool Prefix(MethodBase __originalMethod, object sender)
        {
            if (StartupLoading.CanOpen(ActionName(__originalMethod.Name))) return true;
            if (sender is Button) return false;
            throw new InvalidOperationException("This menu will be available when Quickload finishes loading its data.");
        }
    }

    private static string ActionName(string handler) => handler[6..^6];
}
