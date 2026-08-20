using AnchorChain;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

[ACPlugin("seapower.runtime-bridge", "Sea Power Runtime Bridge", "0.1.0")]
public sealed class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        var assembly = typeof(Plugin).Assembly;
        Debug.Log($"[Sea Power Runtime Bridge] loaded via AnchorChain v{assembly.GetName().Version} from {assembly.Location}");
        RuntimeBridgeBootstrap.EnsureCreated();
    }
}