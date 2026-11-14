using System.Collections;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Quickload;

[HarmonyPatch]
internal static class StartupLoading
{
    private static readonly List<LoadAction> pending = [];
    private static bool collecting = true;
    private static AsyncOperation? menuLoad;
    private static bool startupScreen;
    private static bool earlyMenuVisible;
    private static bool coreReady;
    private static bool failed;
    private static bool preparingEncyclopedia;
    private static bool hasEncyclopedia;
    private static bool loadingMusic;
    private static bool advancingMusic;
    private static string detail = "Preparing game data…";
    private static MenuProgress? display;
    private static LoadProgress? progress;
    private static float nextRefresh;

    internal static bool Active => startupScreen || pending.Count > 0;
    internal static bool DeferredEncyclopedia => preparingEncyclopedia;
    private static bool EncyclopediaReady => coreReady && (EncyclopediaLoading.Ready || !hasEncyclopedia);
    internal static bool CanBuildEncyclopedia => !preparingEncyclopedia ||
        Singleton<MenuSystemViewModel>.Instance.CurrentWindow is MainMenuView && !Singleton<LoadScreen>.Instance.cameraObject.enabled;
    internal static bool CanOpen(string action) => !Active || action switch
    {
        "Continue" or "Scenarios" or "Tutorials" or "Campaign" or "Instant" => coreReady,
        "Editor" or "Encyclopedia" or "Options" or "ModManager" => EncyclopediaReady,
        _ => true,
    };

    // Keel can start us after ExecuteLoadScreen has already sorted the startup actions.
    // Intercept each action as the native screen reaches it, including factory-backed actions.
    [HarmonyTranspiler, HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.ExecuteLoadScreen), MethodType.Enumerator)]
    private static IEnumerable<CodeInstruction> ReplaceStageDispatch(IEnumerable<CodeInstruction> code)
    {
        var instructions = code.ToList();
        var call = instructions.Single(instruction => instruction.Calls(AccessTools.Method(typeof(LoadAction), nameof(LoadAction.GetEnumerator))));
        call.opcode = OpCodes.Call;
        call.operand = AccessTools.Method(typeof(StartupLoading), nameof(Dispatch));
        return instructions;
    }

    private static IEnumerator Dispatch(ref LoadAction action)
    {
        if (Globals._inMuseum && action.description is ("PreloadInis" or "DEM Data" or "Terrain Chunks" or "OpenEncyclopedia"))
            return MuseumLoading.Dispatch(action);
        if (!collecting) return action.GetEnumerator();
        if (!startupScreen && MainMenuView.Instance == null &&
            SceneManager.GetActiveScene().name == "background")
        {
            menuLoad = SceneManager.LoadSceneAsync("main-menu", LoadSceneMode.Additive);
            startupScreen = menuLoad != null;
        }
        if (action.description == "LoadMainMenu")
        {
            collecting = false;
            if (Active) Singleton<BackgroundSceneController>.Instance.StartCoroutine(LoadBehindMenu());
            return menuLoad == null ? action.GetEnumerator() : Empty();
        }
        if (!IsMusic(action) && action.description is not (
            "VoiceMessage_LoadClips" or "SensorMetadataProvider LoadSensorMetadata" or "UnitMetadataProvider LoadUnitMetadata"
            or "IniPreloader PreloadAll" or "EncyclopedicDataManager.GetMuseumEntries" or "WriteTacViewDatabase" or "ClearLaunchMarker")) return action.GetEnumerator();

        pending.Add(action);
        hasEncyclopedia |= IsEncyclopedia(action);
        return Empty();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.LoadText), MethodType.Setter)]
    private static bool KeepKeelWaiting(LoadScreen __instance, string value)
    {
        if (advancingMusic) return false;
        if (!Active || coreReady) return true;
        // Keel uses the native "Done" marker to start mods that need the unit catalogue.
        if (value == "Done") return false;
        if (startupScreen)
        {
            if (value.StartsWith("Loading ", StringComparison.Ordinal)) detail = "Checking game files and mods…";
            return true;
        }
        if (__instance.cameraObject.enabled) return true;
        detail = value;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.LoadPercent), MethodType.Setter)]
    private static bool SuppressMusicProgress() => !advancingMusic;

    // A mission with ForceMusicLoading can request the same load while startup music is in flight.
    [HarmonyPrefix, HarmonyPatch(typeof(MusicManager), nameof(MusicManager.ReloadMusic))]
    private static bool SkipDuplicateMusicLoad() => !loadingMusic;

    [HarmonyPrefix, HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoLoad))]
    private static void RequireData()
    {
        if (Active && !coreReady) throw new InvalidOperationException("Wait for Quickload to finish loading game data before starting a mission.");
    }

    private static IEnumerator LoadBehindMenu()
    {
        var screen = Singleton<LoadScreen>.Instance;
        while (startupScreen) yield return null;
        while (screen.cameraObject.enabled || MainMenuView.Instance is not { IsVisible: true }) yield return null;
        var stages = pending.Where(action => !IsEncyclopedia(action) && !IsMusic(action)).ToArray();
        var music = pending.Where(IsMusic).ToArray();
        progress = new(stages.Select(action => StageName(action.description))
            .Concat(hasEncyclopedia ? new[] { "Encyclopedia sensors", "Encyclopedia weapons", "Encyclopedia units" } : []).ToArray());
        RefreshMenu(MainMenuView.Instance);
        Debug.Log("[Quickload] Main menu visible; loading deferred startup data.");
        MenuProgress.LogGates(MainMenuView.Instance);
        var archives = StartupArchives.Start();
        var iniPrefetch = archives == null ? null : IniWarmupCache.Prefetch(archives);
        if (archives != null)
        {
            if (stages.Any(action => action.description == "UnitMetadataProvider LoadUnitMetadata"))
                UnitMetadataCache.StartValidation(archives);
            if (hasEncyclopedia) EncyclopediaCache.Start(archives);
        }
        yield return null;
        foreach (var action in stages)
        {
            BeginStage($"Loading {StageName(action.description).ToLowerInvariant()}…");
            var start = Time.realtimeSinceStartup;
            if (action.description == "UnitMetadataProvider LoadUnitMetadata")
            {
                yield return UnitMetadataCache.OpenForStartup();
                IniWarmupCache.Begin(archives, iniPrefetch);
                iniPrefetch = null;
                try { yield return Run(action.GetEnumerator()); }
                finally
                {
                    UnitMetadataCache.Finish(archives, !failed);
                    if (failed) IniWarmupCache.Finish(false);
                }
                Debug.Log($"[Quickload] Unit stage restored {IniWarmupCache.Restored:N0} parsed INIs.");
            }
            else if (action.description == "IniPreloader PreloadAll")
            {
                if (iniPrefetch != null) IniWarmupCache.Begin(archives, iniPrefetch);
                iniPrefetch = null;
                try { yield return Run(action.GetEnumerator()); }
                finally
                {
                    Debug.Log($"[Quickload] Startup snapshot restored {IniWarmupCache.Restored:N0} parsed INIs.");
                    IniWarmupCache.Finish(!failed && IniPreloader.Completed);
                }
            }
            else yield return Run(action.GetEnumerator());
            if (failed) yield break;
            progress.Finish();
            Debug.Log($"[Quickload] {action.description}: {Time.realtimeSinceStartup - start:F2}s");
            yield return null;
        }

        coreReady = true;
        RefreshMenu(MainMenuView.Instance);
        MenuProgress.LogGates(MainMenuView.Instance);
        screen.LoadText = "Done";
        Debug.Log("[Quickload] Core startup data ready.");
        if (music.Length > 0)
            Singleton<BackgroundSceneController>.Instance.StartCoroutine(LoadMusic(music));
        var museum = pending.Find(IsEncyclopedia);
        if (museum.description == null)
        {
            FinishProgress();
            yield break;
        }

        // The scenario browser can mount mission data when it opens a briefing. Keep the
        // global encyclopedia on the main menu, and pause it during a scenario or reload.
        preparingEncyclopedia = true;
        try
        {
            yield return Run(museum.GetEnumerator(), waitForMenu: true);
            while (!EncyclopediaLoading.Ready && !failed) yield return null;
            if (!failed) FinishProgress();
        }
        finally
        {
            EncyclopediaCache.Finish();
            preparingEncyclopedia = false;
        }
    }

    private static IEnumerator LoadMusic(LoadAction[] actions)
    {
        loadingMusic = true;
        try
        {
            yield return null;
            var start = Time.realtimeSinceStartup;
            Debug.Log("[Quickload] Loading music after core startup.");
            foreach (var action in actions) yield return Run(action.GetEnumerator(), backgroundMusic: true);
            if (Singleton<MusicManager>.Instance._isInitialized)
                Debug.Log($"[Quickload] Music ready: {Time.realtimeSinceStartup - start:F2}s");
        }
        finally { loadingMusic = false; }
    }

    internal static IEnumerator BuildEncyclopediaPart(string part, IEnumerator routine)
    {
        if (!preparingEncyclopedia)
        {
            yield return routine;
            yield break;
        }
        // The native weapon pass can hold a single frame for seconds. Let music finish while the menu still responds.
        if (part == "weapons" && loadingMusic && !EncyclopediaCache.HasSnapshot)
        {
            detail = "Loading music…";
            while (loadingMusic) yield return null;
        }
        BeginStage($"Loading encyclopedia {part}…");
        yield return Run(routine, waitForMenu: true);
        if (!failed)
        {
            progress?.Finish();
            Debug.Log($"[Quickload] Encyclopedia {part} ready.");
        }
    }

    private static void BeginStage(string label)
    {
        detail = label;
        progress?.Start();
        Debug.Log($"[Quickload] {label}");
    }

    private static string StageName(string action) => action switch
    {
        "VoiceMessage_LoadClips" => "Voice messages",
        "SensorMetadataProvider LoadSensorMetadata" => "Sensor data",
        "UnitMetadataProvider LoadUnitMetadata" => "Unit data",
        "IniPreloader PreloadAll" => "INI cache",
        "WriteTacViewDatabase" => "Tacview database",
        _ => "Startup cleanup",
    };

    private static bool IsEncyclopedia(LoadAction action) => action.description == "EncyclopedicDataManager.GetMuseumEntries";
    private static bool IsMusic(LoadAction action) => action.description is "MusicManager LoadMusicAsync" or "MusicManager CreateAudioSource";

    private static void FinishProgress()
    {
        progress?.Save();
        var menu = Singleton<MenuSystemViewModel>.Instance.CurrentWindow as MainMenuView;
        RefreshMenu(menu);
        if (menu != null) MenuProgress.LogGates(menu);
        pending.Clear();
        display = null;
        progress = null;
    }

    private static IEnumerator Run(IEnumerator routine, bool waitForMenu = false, bool backgroundMusic = false)
    {
        try
        {
            while (true)
            {
                while (waitForMenu && !CanBuildEncyclopedia) yield return null;
                object? item;
                try
                {
                    advancingMusic = backgroundMusic;
                    if (!routine.MoveNext()) yield break;
                    item = routine.Current;
                }
                catch (Exception error)
                {
                    if (backgroundMusic) Debug.LogWarning($"[Quickload] Music loading failed: {error}");
                    else
                    {
                        failed = true;
                        progress?.SetRunning(false);
                        Debug.LogError($"[Quickload] Deferred startup failed: {error}");
                    }
                    yield break;
                }
                finally { advancingMusic = false; }
                if (item is IEnumerator nested and not CustomYieldInstruction) yield return Run(nested, waitForMenu, backgroundMusic);
                else yield return item;
                if (failed && !backgroundMusic) yield break;
            }
        }
        finally { (routine as IDisposable)?.Dispose(); }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MenuSystemViewModel), "Update")]
    private static void Update(MenuSystemViewModel __instance)
    {
        if (startupScreen) RefreshStartupScreen();
        if (preparingEncyclopedia) progress?.SetRunning(CanBuildEncyclopedia && !failed);
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.1f;
        RefreshMenu(__instance.CurrentWindow as MainMenuView);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.ExecuteLoadScreen), MethodType.Enumerator)]
    private static void FinishStartupScreen(bool __result)
    {
        if (__result || !startupScreen) return;
        startupScreen = false;
        menuLoad = null;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(BackgroundSceneController), "Start")]
    private static void ResetStartup()
    {
        pending.Clear();
        collecting = true;
        startupScreen = coreReady = failed = hasEncyclopedia = earlyMenuVisible = preparingEncyclopedia = false;
        loadingMusic = advancingMusic = false;
        detail = "Preparing game data…";
        menuLoad = null;
        display = null;
        progress = null;
        nextRefresh = 0;
        IniWarmupCache.Finish(false);
        EncyclopediaCache.Finish();
        EncyclopediaLoading.Reset();
    }

    private static void RefreshStartupScreen()
    {
        if (MainMenuView.Instance == null) return;
        var screen = Singleton<LoadScreen>.Instance;
        screen.cameraObject.enabled = screen.WindowHost.Windows.Count > 0 || screen.ShowWarningPSE;
        if (!screen.cameraObject.enabled && !earlyMenuVisible)
        {
            earlyMenuVisible = true;
            Debug.Log("[Quickload] Main menu shown during startup checks.");
        }
    }

    private static void RefreshMenu(MainMenuView? menu)
    {
        if (!Active || menu == null) return;
        if (display?.Menu != menu)
        {
            if (EncyclopediaReady) return;
            display = new(menu);
        }
        var status = progress?.Read(failed);
        display.Refresh(failed ? "Startup data failed to load. Check Player.log." : detail, status?.Timing, status?.Stage, progress,
            EncyclopediaReady);
        if (EncyclopediaReady) display = null;
    }

    internal static void AttachMenu(MainMenuView menu)
    {
        if (!Active || EncyclopediaReady) return;
        RefreshMenu(menu);
    }

    private static IEnumerator Empty() { yield break; }
}
