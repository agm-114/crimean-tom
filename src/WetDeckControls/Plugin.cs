using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace WetDeckControls;

[ACPlugin("crimean-tom.wet-deck", "Well Deck", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        if (!WetDeckCatalog.Load()) return;
        new Harmony("crimean-tom.wet-deck").PatchAll(typeof(Plugin).Assembly);
        var host = new GameObject("Well Deck");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<WetDeckController>();
        Debug.Log("[Well Deck] Loaded well-deck launch and recovery controls.");
    }
}
