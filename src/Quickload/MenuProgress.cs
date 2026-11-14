using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Noesis;
using SeaPower.SharedUi;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace Quickload;

[HarmonyPatch]
internal class MenuProgress
{
    private static readonly string[] Gated = ["Scenarios", "Tutorials", "Campaign", "Instant", "Editor", "Encyclopedia", "Options", "ModManager"];
    private static readonly ConditionalWeakTable<MainMenuView, Dictionary<Button, string>> menuActions = new();
    private readonly StartupProgressPanel panel;
    private readonly UIElement content;
    private readonly Grid root = new();
    private readonly Dictionary<Button, string> actions;
    private readonly Dictionary<Button, (string Action, BindingBase? Binding, bool? LocalValue)> blocked = [];
    internal MainMenuView Menu { get; }

    private float shrinkStarted = -1;

    internal MenuProgress(MainMenuView menu, StartupProgressPanel panel)
    {
        Menu = menu;
        this.panel = panel;
        actions = menuActions.GetOrCreateValue(menu);
        content = (UIElement)menu.Content;
        menu.Content = null;
        root.Children.Add(content);
        panel.Detach();
        panel.View.Visibility = Visibility.Visible;
        root.Children.Add(panel.View);
        menu.Content = root;
    }

    internal void Shrink()
    {
        if (shrinkStarted < 0) shrinkStarted = UnityEngine.Time.unscaledTime;
        float t = Math.Clamp((UnityEngine.Time.unscaledTime - shrinkStarted) / 0.65f, 0, 1);
        float wide = (1 - t) * (1 - t);
        panel.Layout(wide, (float)root.ActualWidth, (float)root.ActualHeight);
    }

    internal void DetachPanel() => root.Children.Remove(panel.View);

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
            panel.Refresh(text, times, currentStage, progress);
    }

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
        private static bool Prefix(MainMenuView __instance, MethodBase __originalMethod, object sender)
        {
            string action = ActionName(__originalMethod.Name);
            if (StartupLoading.CanOpen(action))
            {
                if (action != "Encyclopedia" || !DeferredPointValues.Running) return true;
                MuseumLoading.OpenReferenceOnMenu(__instance);
                return false;
            }
            if (sender is Button) return false;
            throw new InvalidOperationException("This menu will be available when Quickload finishes loading its data.");
        }
    }

    private static string ActionName(string handler) => handler[6..^6];
}
