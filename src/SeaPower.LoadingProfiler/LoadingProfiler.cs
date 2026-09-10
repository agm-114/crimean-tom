using BepInEx;
using HarmonyLib;
using SeapowerUI.ViewModels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SeaPower.LoadingProfiler;

[BepInPlugin(PluginId, "Sea Power Loading Profiler", "0.1.0")]
[BepInDependency("io.github.seapower_modders.anchorchain_preloader", BepInDependency.DependencyFlags.SoftDependency)]
public class LoadingProfiler : BaseUnityPlugin
{
    internal const string PluginId = "crimean-tom.loading-profiler";
    private readonly Harmony harmony = new(PluginId);
    private StartupCounters? counters;
    private string lastMenu = "";
    private float nextPoll;
    private bool firstFrame = true;

    private void Awake()
    {
        try
        {
            TraceLog.Open();
            TraceLog.Write("profiler-start", new
            {
                id = TraceDirectory.Game.Id,
                processStartUtc = TraceDirectory.ProcessStartUtc,
                version = Application.version,
                profilerVersion = Info.Metadata.Version.ToString()
            });
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.activeSceneChanged += ActiveSceneChanged;
            LoadingHooks.Install(harmony);
            if (Config.Bind("Capture", "StartupCounters", false, "Capture the Unity counters available before the first Update. Release builds expose few loading counters.").Value)
                counters = new StartupCounters();
            Logger.LogInfo($"Timing hooks ready. Logs: {TraceDirectory.Path}");
        }
        catch (Exception error)
        {
            Logger.LogError($"Profiler initialization failed: {error}");
            Cleanup();
            enabled = false;
        }
    }

    private void Update()
    {
        if (!TraceLog.Active) return;
        if (firstFrame)
        {
            firstFrame = false;
            TraceLog.Write("first-update", new { splashFinished = UnityEngine.Rendering.SplashScreen.isFinished });
            counters?.Capture();
            counters?.Dispose();
            counters = null;
        }
        if (Time.realtimeSinceStartup < nextPoll) return;
        nextPoll = Time.realtimeSinceStartup + 0.1f;
        if (!Singleton<MenuSystemViewModel>.InstanceExists()) return;
        var window = Singleton<MenuSystemViewModel>.Instance.CurrentWindow as Noesis.FrameworkElement;
        string menu = $"{window?.GetType().Name}:{window?.IsVisible}";
        if (menu == lastMenu) return;
        lastMenu = menu;
        TraceLog.Write("menu", new { menu });
    }

    private static void SceneLoaded(Scene scene, LoadSceneMode mode) =>
        TraceLog.Write("scene-loaded", new { scene.name, scene.buildIndex, mode = mode.ToString() });
    private static void ActiveSceneChanged(Scene previous, Scene scene) =>
        TraceLog.Write("active-scene", new { scene.name, scene.buildIndex });
    private void OnDestroy() => Cleanup();
    private void Cleanup()
    {
        SceneManager.sceneLoaded -= SceneLoaded;
        SceneManager.activeSceneChanged -= ActiveSceneChanged;
        harmony.UnpatchSelf();
        counters?.Dispose();
        counters = null;
        TraceLog.Write("profiler-stop");
        TraceLog.Close();
    }
}
