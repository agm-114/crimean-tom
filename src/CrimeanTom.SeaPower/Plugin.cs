using AnchorChain;
using UnityEngine;

namespace CrimeanTom.SeaPower;

[ACPlugin("crimean-tom.operations", "Crimean Tom Operations", "0.2.0")]
public sealed class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        System.Reflection.Assembly assembly = typeof(Plugin).Assembly;
        Debug.Log($"[Crimean Tom Operations] loaded native Task Force Mode shim v{assembly.GetName().Version} from {assembly.Location}");
    }
}
