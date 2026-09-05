using HarmonyLib;
using Noesis;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static class NativeUi
{
    internal static readonly SolidColorBrush PanelBrush = new(Noesis.Color.FromArgb(255, 35, 38, 41));
    internal static readonly SolidColorBrush RowBrush = new(Noesis.Color.FromArgb(255, 49, 53, 56));
    internal static readonly SolidColorBrush BorderBrush = new(Noesis.Color.FromArgb(255, 87, 95, 101));
    internal static readonly SolidColorBrush TextBrush = new(Noesis.Color.FromArgb(255, 232, 234, 235));
    internal static readonly SolidColorBrush MutedTextBrush = new(Noesis.Color.FromArgb(255, 164, 171, 176));
    private static readonly SolidColorBrush CampaignOverlayBrush = new(Noesis.Color.FromArgb(190, 9, 11, 13));

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
        Border dialog = new()
        {
            Width = Math.Min(maxWidth, Math.Max(minWidth, hostWidth - 100f)),
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (scrollable)
        {
            float hostHeight = host.ActualHeight > 0f ? host.ActualHeight : Screen.height;
            dialog.MaxHeight = Math.Max(440f, hostHeight - 60f);
            dialog.Child = new ScrollViewer
            {
                Content = content,
                MaxHeight = dialog.MaxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        }
        else
        {
            dialog.Child = content;
        }
        Grid overlay = new()
        {
            Background = CampaignOverlayBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        overlay.Children.Add(dialog);
        return overlay;
    }
}