using HarmonyLib;

using MapControl;

using Noesis;

using SeaPower;

using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPowerRuntimeBridge;

/// <summary>
/// Authored mission symbols deliberately render as passive map decoration in
/// Sea Power. Campaign projections reuse that renderer for native symbology,
/// so route clicks that land on their screen-space footprint back into the
/// campaign proxy selection path.
/// </summary>
internal static class RuntimeCampaignMapSelectionAdapter
{
    private const string HarmonyId = "crimean-tom.campaign-map-selection";
    private const float SelectionRadiusPixels = 24f;
    private static float _nextMapSearchAt;

    public static bool HooksInstalled { get; private set; }

    public static bool ViewportHookInstalled { get; private set; }

    public static string InstallationError { get; private set; } = string.Empty;

    public static void Install()
    {
        if (HooksInstalled || !string.IsNullOrEmpty(InstallationError))
        {
            return;
        }

        try
        {
            var target = AccessTools.Method(typeof(InteractionState), nameof(InteractionState.MouseAction))
                ?? throw new MissingMethodException(
                    typeof(InteractionState).FullName,
                    nameof(InteractionState.MouseAction));
            new Harmony(HarmonyId).Patch(
                target,
                prefix: new HarmonyMethod(
                    typeof(RuntimeCampaignMapSelectionAdapter),
                    nameof(MouseActionPrefix)));
            HooksInstalled = true;
            UnityEngine.Debug.Log(
                "[Sea Power Runtime Bridge] campaign map-selection hook installed.");
        }
        catch (Exception exception)
        {
            InstallationError = $"{exception.GetType().Name}: {exception.Message}";
            UnityEngine.Debug.LogWarning(
                "[Sea Power Runtime Bridge] campaign map-selection hook failed: "
                + InstallationError);
        }
    }

    public static void TryInstallViewportHook()
    {
        if (!HooksInstalled
            || Globals._mainGameViewModel?.Map?.DisplayMap is null
            || MainGameView.Instance is null)
        {
            return;
        }

        if (!ViewportHookInstalled)
        {
            try
            {
                // MapBase registers Noesis dependency properties in its static
                // initializer. Patching it during plugin Awake initializes Noesis
                // too early and can crash the process, so wait for the tactical UI.
                var viewportTarget = AccessTools.Method(
                        typeof(MapBase),
                        "OnViewportChanged",
                        [typeof(ViewportChangedEventArgs)])
                    ?? throw new MissingMethodException(
                        typeof(MapBase).FullName,
                        "OnViewportChanged");
                new Harmony(HarmonyId).Patch(
                    viewportTarget,
                    postfix: new HarmonyMethod(
                        typeof(RuntimeCampaignMapSelectionAdapter),
                        nameof(ViewportChangedPostfix)));
                ViewportHookInstalled = true;
                UnityEngine.Debug.Log(
                    "[Sea Power Runtime Bridge] campaign map-viewport hook installed.");
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    "[Sea Power Runtime Bridge] campaign map-viewport hook failed: "
                    + $"{exception.GetType().Name}: {exception.Message}");
                return;
            }
        }

        if (RuntimeVisualRenderProxyAdapter.HasActiveMapViewport
            || UnityEngine.Time.realtimeSinceStartup < _nextMapSearchAt)
        {
            return;
        }
        _nextMapSearchAt = UnityEngine.Time.realtimeSinceStartup + 0.5f;
        if (FindMapBase(MainGameView.Instance) is { } map)
        {
            RuntimeVisualRenderProxyAdapter.RefreshMapViewport(map, force: true);
        }
    }

    private static MapBase? FindMapBase(Noesis.DependencyObject root)
    {
        var pending = new Stack<Noesis.DependencyObject>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited++ < 16_384)
        {
            var node = pending.Pop();
            if (node is MapBase map)
            {
                return map;
            }
            var childCount = Noesis.VisualTreeHelper.GetChildrenCount(node);
            for (var index = 0; index < childCount; index++)
            {
                if (Noesis.VisualTreeHelper.GetChild(node, index) is { } child)
                {
                    pending.Push(child);
                }
            }
        }
        return null;
    }

    private static void ViewportChangedPostfix(MapBase __instance)
    {
        RuntimeVisualRenderProxyAdapter.RefreshMapViewport(__instance);
    }

    private static bool MouseActionPrefix(
        object v,
        RoutedEventArgs args,
        GeoPosition mousePosition,
        MapBase map,
        ref bool handled,
        ref HitTestResultBehavior __result)
    {
        RuntimeVisualRenderProxyAdapter.RefreshMapViewport(map);
        var interactionState = Globals._mainGameViewModel?
            .Map?
            .DisplayMap?
            .InteractionState?
            .GetType()
            .FullName;
        if (args is not MouseButtonEventArgs mouse
            || mouse.LeftButton != MouseButtonState.Pressed
            || interactionState is not (
                "SeapowerUI.ViewModels.DefaultInteractionState"
                or "SeapowerUI.ViewModels.UnitSelectedState")
            || !RuntimeVisualRenderProxyAdapter.TrySelectMapProjection(
                v,
                mousePosition,
                map,
                SelectionRadiusPixels))
        {
            return true;
        }

        handled = true;
        __result = HitTestResultBehavior.Stop;
        return false;
    }
}
