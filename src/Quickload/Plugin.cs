using AnchorChain;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Quickload;

[ACPlugin("crimean-tom.quickload", "Quickload", "0.2.6")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        if (Application.version != "0.9.3-261116")
        {
            Debug.LogWarning($"[Quickload] Unsupported game version {Application.version}.");
            return;
        }

        var enabledFolders = StartupTimeline.Initialize();
        StartupPrefetch.Start(Application.streamingAssetsPath, enabledFolders ?? StartupArchives.ActiveRoots());
        IniWarmupCache.SnapshotReader.Start();
        ResolvedSensors.DecodeAhead();
        if (SceneManager.GetActiveScene().isLoaded)
            PatchGame();
        else
        {
            StartupTimeline.Advance("Waiting for first scene");
            Debug.Log($"[Quickload] Waiting for the first scene (frame {Time.frameCount}, splash finished: {UnityEngine.Rendering.SplashScreen.isFinished}).");
            SceneManager.sceneLoaded += PatchAfterSceneLoad;
        }
    }

    private static void PatchAfterSceneLoad(Scene scene, LoadSceneMode mode)
    {
        SceneManager.sceneLoaded -= PatchAfterSceneLoad;
        Debug.Log($"[Quickload] Scene '{scene.name}' loaded (frame {Time.frameCount}, splash finished: {UnityEngine.Rendering.SplashScreen.isFinished}).");
        PatchGame();
    }

    private static void PatchGame()
    {
        SplashProgress.SceneReady();
        // Noesis initializes native UI types with the scene. Installing its detours during BepInEx startup can crash Unity.
        if (StartupPrefetch.Progress is { } ready)
        {
            string worker = StartupPrefetch.SecondsSinceWorkerFinished is { } seconds
                ? $"finished {seconds:F2} s ago" : "still running";
            Debug.Log($"[Quickload] Before patch setup: {ready.Hashes:N0} input hashes and {ready.Archives} archives ready, worker {worker}.");
        }
        StartupTimeline.Advance("Quickload patch setup");
        Harmony harmony = new("crimean-tom.quickload");
        try { harmony.PatchAll(typeof(Plugin).Assembly); }
        catch { harmony.UnpatchSelf(); throw; }
        UnitMetadataCache.DecodeAhead();
        EncyclopediaCache.DecodeAhead();
        StartupTimeline.Advance("Waiting for startup checks");
        Debug.Log("[Quickload] Enabled deferred startup loading and encyclopedia batching.");
    }
}
