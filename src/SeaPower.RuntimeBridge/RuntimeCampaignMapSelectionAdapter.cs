using HarmonyLib;

using MapControl;

using Noesis;

using SeaPower;

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
    private const float SelectionRadiusPixels = 18f;

    public static bool HooksInstalled { get; private set; }

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

    private static bool MouseActionPrefix(
        object v,
        RoutedEventArgs args,
        GeoPosition mousePosition,
        MapBase map,
        ref bool handled,
        ref HitTestResultBehavior __result)
    {
        if (args is not MouseButtonEventArgs mouse
            || mouse.LeftButton != MouseButtonState.Pressed
            || Globals._mainGameViewModel?.Map?.DisplayMap?.InteractionState?.GetType().FullName
                != "SeapowerUI.ViewModels.DefaultInteractionState"
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
