using AnchorChain;
using UnityEngine;

namespace CrimeanTom;

[ACPlugin("crimean.tom", "Crimean Tom", "0.1.0")]
public sealed class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        Debug.Log("[Crimean Tom] loaded via AnchorChain");
    }
}
