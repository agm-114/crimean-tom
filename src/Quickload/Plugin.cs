using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace Quickload;

[ACPlugin("crimean-tom.quickload", "Quickload", "0.2.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        if (Application.version != "0.9.2-261113")
        {
            Debug.LogWarning($"[Quickload] Unsupported game version {Application.version}.");
            return;
        }

        Harmony harmony = new("crimean-tom.quickload");
        try { harmony.PatchAll(typeof(Plugin).Assembly); }
        catch { harmony.UnpatchSelf(); throw; }
        Debug.Log("[Quickload] Enabled deferred startup loading and encyclopedia batching.");
    }
}
