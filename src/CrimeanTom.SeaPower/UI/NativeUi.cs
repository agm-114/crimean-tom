using HarmonyLib;
using Noesis;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;
using SharedUi = global::SeaPower.SharedUi.NativeUi;

namespace CrimeanTom.SeaPower;

internal static class NativeUi
{
    internal static readonly SolidColorBrush PanelBrush = new(Noesis.Color.FromArgb(255, 35, 38, 41));
    internal static readonly SolidColorBrush RowBrush = new(Noesis.Color.FromArgb(255, 49, 53, 56));
    internal static readonly SolidColorBrush BorderBrush = new(Noesis.Color.FromArgb(255, 87, 95, 101));
    internal static readonly SolidColorBrush TextBrush = new(Noesis.Color.FromArgb(255, 232, 234, 235));
    internal static readonly SolidColorBrush MutedTextBrush = new(Noesis.Color.FromArgb(255, 164, 171, 176));
    private static readonly SolidColorBrush CampaignOverlayBrush = new(Noesis.Color.FromArgb(190, 9, 11, 13));

    internal static ComboBox ConfigurePicker<T>(ChoicePicker<T> picker)
    {
        var control = picker.Control;
        control.MinWidth = 330;
        if (control.ToolTip == null && control.Items.OfType<ComboBoxItem>().Any(item => item.Content is "Random"))
            control.ToolTip = "Chances apply when Random is selected. Choosing an entry directly overrides the roll.";
        return control;
    }

    internal static void CloseWindow(
        FrameworkElement panel, FrameworkElement host, ref FrameworkElement? openPanel)
    {
        WindowHostCommands.CloseWindowCommand.Execute(panel, host);
        if (ReferenceEquals(openPanel, panel)) openPanel = null;
    }

    internal static TextBlock CreateText(string text, float size, Brush brush) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
    };

    internal static FrameworkElement? GetCampaignHost()
    {
        Type? type = AccessTools.TypeByName("SeapowerUI.LinearCampaignView");
        return type is null ? null : AccessTools.Field(type, "Instance")?.GetValue(null) as FrameworkElement;
    }

    internal static Grid CreateCampaignOverlay(
        StackPanel content, FrameworkElement host, float minWidth, float maxWidth, bool scrollable = false)
    {
        float hostWidth = host.ActualWidth > 0f ? host.ActualWidth : Screen.width;
        float width = Math.Min(maxWidth, Math.Max(minWidth, hostWidth - 100f));
        float height = float.PositiveInfinity;
        FrameworkElement body = content;
        if (scrollable)
        {
            float hostHeight = host.ActualHeight > 0f ? host.ActualHeight : Screen.height;
            height = Math.Max(440f, hostHeight - 60f);
            var scroll = SharedUi.Scroll(content);
            scroll.MaxHeight = height;
            body = scroll;
        }
        return SharedUi.Overlay(body, CampaignOverlayBrush, PanelBrush, BorderBrush, width, maxHeight: height);
    }
}
