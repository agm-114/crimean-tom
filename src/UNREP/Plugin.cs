using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace UNREP;

[ACPlugin("crimean-tom.unrep", "UNREP", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        var harmony = new Harmony("crimean-tom.unrep");
        SupplySettings.Load(harmony);
        harmony.PatchAll(typeof(Plugin).Assembly);
        AviationFuel.Activate();
        var host = new GameObject("UNREP");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<SuppliesController>();
        Debug.Log("[UNREP] Loaded Supplies tab.");
    }
}
