using AnchorChain;
using UnityEngine;

namespace SeaPower.StartupTweaks;

[ACPlugin(StartupTweaks.Id, "Quickload", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        GameObject host = new("Quickload");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<StartupTweaks>();
    }
}
