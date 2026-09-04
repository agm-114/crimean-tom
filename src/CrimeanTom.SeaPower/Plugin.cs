using AnchorChain;

using HarmonyLib;

using UnityEngine;

namespace CrimeanTom.SeaPower;

[ACPlugin("crimean-tom.operations", "Crimean Tom Operations", "0.1.0")]
public sealed class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        Harmony harmony = new("crimean-tom.operations");
        harmony.PatchAll(typeof(Plugin).Assembly);

        System.Reflection.Assembly assembly = typeof(Plugin).Assembly;
        Debug.Log($"[Crimean Tom Operations] loaded v{assembly.GetName().Version} from {assembly.Location}");
    }
}
