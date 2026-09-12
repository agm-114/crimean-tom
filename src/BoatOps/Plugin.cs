using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace BoatOps;

[ACPlugin("crimean-tom.boat-ops", "Boat Ops", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        if (!BoatCatalog.Load()) return;
        new Harmony("crimean-tom.boat-ops").PatchAll(typeof(Plugin).Assembly);
        var host = new GameObject("Boat Ops");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<BoatOpsController>();
        Debug.Log("[Boat Ops] Loaded embarked craft launch and recovery controls.");
    }
}
