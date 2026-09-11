using AnchorChain;
using UnityEngine;
using HarmonyLib;

namespace Amphibs;

[ACPlugin("crimean-tom.amphibs", "Amphibs", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        if (!AmphibiousCatalog.Load()) return;
        var harmony = new Harmony("crimean-tom.amphibs");
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            if (AmphibiousCatalog.ShowTuningPanel)
            {
                var host = new GameObject("Amphibs tuning");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<WaterEffectsPanel>();
            }
            Debug.Log($"[Amphibs] Loaded {AmphibiousCatalog.Profiles.Count} amphibious profiles.");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            Debug.LogError($"[Amphibs] Patches rejected: {error}");
        }
    }
}
