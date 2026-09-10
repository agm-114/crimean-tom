using AnchorChain;
using UnityEngine;

namespace SeaPower.StartupTweaks;

[ACPlugin(StartupTweaks.Id, "Sea Power Startup Tweaks", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        GameObject host = new("Sea Power Startup Tweaks");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<StartupTweaks>();
    }
}
