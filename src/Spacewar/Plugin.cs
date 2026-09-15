using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace Spacewar;

[ACPlugin("crimean-tom.spacewar", "Spacewar", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        var harmony = new Harmony("crimean-tom.spacewar");
        GameObject? host = null;
        try
        {
            SpacecraftCatalog.Load();
            harmony.PatchAll();
            host = new GameObject("Spacewar");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<SpacewarController>();
            Debug.Log($"[Spacewar] Loaded {SpacecraftCatalog.Units.Count} spacecraft and Keplerian orbits.");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            if (host != null) UnityEngine.Object.Destroy(host);
            Debug.LogError($"[Spacewar] Initialization failed: {error}");
        }
    }
}
