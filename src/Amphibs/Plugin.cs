using AnchorChain;
using UnityEngine;
using HarmonyLib;

namespace Amphibs;

[ACPlugin("crimean-tom.amphibs", "Amphibs", "0.1.4", after: new[] { Controls.Plugin.Id })]
[ACDependency(Controls.Plugin.Id, "0.3.0", null)]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        var harmony = new Harmony("crimean-tom.amphibs");
        try
        {
            AmphibsSettings.Register();
            harmony.PatchAll(typeof(Plugin).Assembly);
            AmphibiousVariants.RegisterLoadedVariants();
            var host = new GameObject("Amphibs tuning");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<WaterEffectsPanel>();
            Debug.Log("[Amphibs] Loaded amphibious movement and water effects.");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            Debug.LogError($"[Amphibs] Patches rejected: {error}");
        }
    }
}
