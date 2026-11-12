using HarmonyLib;
using Noesis;
using SeaPower;
using SeapowerUI.ViewModels;

namespace SeaPower.GameBridge;

[HarmonyPatch]
internal static class PlayerBriefing
{
    private static UIElement? pane;
    private static string? missionPath;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MissionBriefViewModel), MethodType.Constructor)]
    private static void RememberPane(MissionBriefViewModel __instance)
    {
        if (__instance.LeftContent is not { } content) return;
        pane = content;
        missionPath = Globals.currentMissionFilePath;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoUnload))]
    private static void Clear()
    {
        pane = null;
        missionPath = null;
    }

    internal static string? Read()
    {
        if (pane is { } content && missionPath == Globals.currentMissionFilePath)
        {
            string briefing = string.Join("\n", TextFrom(content));
            if (briefing.Length > 0) return briefing;
        }
        string? description = Singleton<SceneCreator>.InstanceExists() ? Singleton<SceneCreator>.Instance.CurrentMissionDescription : null;
        return string.IsNullOrWhiteSpace(description) ? null : description;
    }

    private static IEnumerable<string> TextFrom(UIElement element)
    {
        if (element.Visibility != Visibility.Visible) yield break;
        if (element is TextBlock block)
        {
            string text = (block.Inlines.Count == 0 ? block.Text : ReadInlines(block.Inlines)).Trim();
            if (text.Length > 0) yield return text;
            yield break;
        }

        IEnumerable<UIElement> children = element switch
        {
            Panel panel => panel.Children.OfType<UIElement>(),
            Decorator { Child: { } child } => [child],
            ContentControl { Content: UIElement child } => [child],
            _ => Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element))
                .Select(i => VisualTreeHelper.GetChild(element, i)).OfType<UIElement>()
        };
        foreach (UIElement child in children)
            foreach (string text in TextFrom(child)) yield return text;
    }

    private static string ReadInlines(InlineCollection inlines) => string.Concat(inlines.OfType<Inline>().Select(inline => inline switch
    {
        Run run => run.Text,
        LineBreak => "\n",
        Span span => ReadInlines(span.Inlines),
        _ => ""
    }));
}
