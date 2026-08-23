using CrimeanTom.Protocol;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeRenderProxyProvider
{
    private static bool _useNativeFallback;

    public static SurfaceRenderProxySnapshotPayload Capture() => _useNativeFallback
        ? RuntimeRenderProxyAdapter.CaptureState()
        : RuntimeVisualRenderProxyAdapter.Capture();

    public static MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload> Reconcile(
        ReconcileSurfaceRenderProxiesRequestPayload request,
        string commandSessionId)
    {
        if (_useNativeFallback)
        {
            return RuntimeRenderProxyAdapter.Reconcile(request, commandSessionId);
        }

        var visualResult = RuntimeVisualRenderProxyAdapter.Reconcile(request, commandSessionId);
        if (visualResult.Succeeded
            || !visualResult.ErrorMessage.StartsWith(
                "Visual-only render-proxy materialization failed:",
                StringComparison.Ordinal))
        {
            return visualResult;
        }

        RuntimeVisualRenderProxyAdapter.ClearAll();
        _useNativeFallback = true;
        Debug.LogWarning(
            "[Sea Power Runtime Bridge] visual-only render proxies failed; "
            + $"using fenced native-vessel fallback. {visualResult.ErrorMessage}");
        return RuntimeRenderProxyAdapter.Reconcile(request, commandSessionId);
    }
}
