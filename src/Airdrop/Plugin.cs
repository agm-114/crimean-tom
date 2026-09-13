using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace Airdrop;

[ACPlugin("crimean-tom.airdrop", "Airdrop", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        if (!CargoCatalog.Load()) return;
        new Harmony("crimean-tom.airdrop").PatchAll(typeof(Plugin).Assembly);
        var host = new GameObject("Airdrop");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<AirdropController>();
        Debug.Log("[Airdrop] Loaded finite transport manifests and ground-unit release controls.");
    }
}
