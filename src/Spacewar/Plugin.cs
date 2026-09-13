using AnchorChain;
using UnityEngine;

namespace Spacewar;

[ACPlugin("crimean-tom.spacewar", "Spacewar", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        try
        {
            var satellites = SatelliteCatalog.Load();
            var host = new GameObject("Spacewar");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<SpacewarController>().Satellites = satellites;
            Debug.Log($"[Spacewar] Loaded {satellites.Count} satellites and Keplerian orbits.");
        }
        catch (Exception error) { Debug.LogError($"[Spacewar] Configuration rejected: {error.Message}"); }
    }
}
