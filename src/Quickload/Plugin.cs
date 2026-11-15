using AnchorChain;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        var enabledFolders = StartupTimeline.Initialize();
        StartupPrefetch.Start(Application.streamingAssetsPath, enabledFolders ?? StartupArchives.ActiveRoots());
        if (SceneManager.GetActiveScene().isLoaded)
            PatchGame();
        else
            SceneManager.sceneLoaded += PatchAfterSceneLoad;
    }

    private static void PatchAfterSceneLoad(Scene scene, LoadSceneMode mode)
    {
        SceneManager.sceneLoaded -= PatchAfterSceneLoad;
        PatchGame();
    }

    private static void PatchGame()
    {
        StartupTimeline.Advance("Quickload patch setup");
        Harmony harmony = new("crimean-tom.quickload");
        try { harmony.PatchAll(typeof(Plugin).Assembly); }
        catch { harmony.UnpatchSelf(); throw; }
        StartupTimeline.Advance("Waiting for startup checks");
        Debug.Log("[Quickload] Enabled deferred startup loading and encyclopedia batching.");
    }
}
