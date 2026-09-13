using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace Deckpark;

[ACPlugin("crimean-tom.deckpark", "Deckpark", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        var harmony = new Harmony("crimean-tom.deckpark");
        try
        {
            DeckCatalog.Load();
            harmony.PatchAll(typeof(Plugin).Assembly);
            var host = new GameObject("Deckpark");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<DeckparkController>();
            Debug.Log("[Deckpark] Loaded ship-mounted ground units and native weapon controls.");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            Debug.LogError($"[Deckpark] Initialization failed: {error}");
        }
    }
}
