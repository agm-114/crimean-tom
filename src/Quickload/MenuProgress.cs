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
    private static readonly ConditionalWeakTable<Button, string> actions = new();
    private readonly Border panel;
    private readonly UIElement content;
    private readonly TextBlock label = new() { FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock timing = DetailLine();
    private readonly TextBlock stage = DetailLine();
    private readonly ProgressBar bar = new() { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Dictionary<Button, (string Action, BindingBase? Binding, bool? LocalValue)> blocked = [];
    internal MainMenuView Menu { get; }

    internal MenuProgress(MainMenuView menu)
    {
        Menu = menu;
        content = (UIElement)menu.Content;
        menu.Content = null;
        Grid root = new();
        root.Children.Add(content);
        StackPanel stack = new();
        stack.Children.Add(label);
        stack.Children.Add(bar);
        stack.Children.Add(timing);
        stack.Children.Add(stage);
        panel = new()
        {
            Child = stack, Background = new SolidColorBrush(Color.FromArgb(240, 35, 38, 40)), Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Width = 440, Margin = new Thickness(0, 0, 24, 24), IsHitTestVisible = false,
        };
        root.Children.Add(panel);
        menu.Content = root;
    }

    internal void Refresh(string text, string? times, string? currentStage, float? percent, bool ready)
    {
        if (!ready) BlockButtons(content, Menu.DataContext as MainMenuViewModel);
        foreach (var (button, (action, binding, localValue)) in blocked.ToArray())
        {
            if (!StartupLoading.CanOpen(action)) continue;
            button.IsEnabledChanged -= KeepBlocked;
            if (binding != null) BindingOperations.SetBinding(button, UIElement.IsEnabledProperty, binding);
            else if (localValue is bool enabled) button.SetValue(UIElement.IsEnabledProperty, enabled);
            else button.ClearValue(UIElement.IsEnabledProperty);
            blocked.Remove(button);
        }
        if (ready && panel.Parent is Grid root)
        {
            root.Children.Remove(content);
            Menu.Content = content;
        }
        else
        {
            if (label.Text != text) label.Text = text;
            if (timing.Text != times) timing.Text = times ?? "";
            if (stage.Text != currentStage) stage.Text = currentStage ?? "";
            bar.Maximum = 100;
            bar.Value = percent ?? 0;
            bar.IsIndeterminate = percent == null;
        }
    }

    private static TextBlock DetailLine() => new()
    {
        FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };

    private void BlockButtons(DependencyObject element, MainMenuViewModel? model)
    {
        if (element is Button button)
        {
            string action = actions.TryGetValue(button, out string name) ? name
                : model != null && ReferenceEquals(button.Command, model.ContinueCommand) ? "Continue" : "";
            if (!StartupLoading.CanOpen(action))
            {
                var property = UIElement.IsEnabledProperty;
                if (!blocked.ContainsKey(button))
                {
                    blocked.Add(button, (action, BindingOperations.GetBindingBase(button, property), button.ReadLocalValue(property) as bool?));
                    button.IsEnabledChanged += KeepBlocked;
                }
                if (button.IsEnabled) button.SetValue(property, false);
            }
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) BlockButtons(VisualTreeHelper.GetChild(element, i), model);
    }

    private void KeepBlocked(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Button { IsEnabled: true } button && blocked.TryGetValue(button, out var gate)
            && !StartupLoading.CanOpen(gate.Action)) button.SetValue(UIElement.IsEnabledProperty, false);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MainMenuView), "ConnectEvent")]
    private static void RememberAction(object source, string handlerName)
    {
        if (!StartupLoading.Active || source is not Button button || !handlerName.StartsWith("Button", StringComparison.Ordinal)
            || !handlerName.EndsWith("_Click", StringComparison.Ordinal)) return;
        actions.GetValue(button, _ => ActionName(handlerName));
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
