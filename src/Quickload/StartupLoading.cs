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
    private static bool menuSceneReady;
    private static bool replacedNativeProgress;
    private static bool coreReady;
    private static bool failed;
    private static bool preparingEncyclopedia;
    private static bool hasEncyclopedia;
    private static bool loadingMusic;
    private static bool advancingMusic;
    private static string detail = "Preparing game data…";
    private static MenuProgress? display;
    private static StartupProgressPanel? panel;
    private static LoadProgress? progress;
    private static float nextRefresh;

    internal static bool Active => startupScreen || pending.Count > 0;
    internal static bool DeferredEncyclopedia => preparingEncyclopedia;
    private static bool EncyclopediaReady => coreReady && (EncyclopediaLoading.Ready || !hasEncyclopedia);
    private static bool StartupReady => EncyclopediaReady && !DeferredPointValues.Running;
    private static bool CanReadStartupData =>
        Singleton<MenuSystemViewModel>.Instance.CurrentWindow is (MainMenuView or CampaignMenuView) &&
        !Singleton<LoadScreen>.Instance.cameraObject.enabled;
    internal static bool CanBuildEncyclopedia => !preparingEncyclopedia || CanReadStartupData;
    internal static bool CanOpen(string action) => !Active || action switch
    {
        "Scenarios" or "Tutorials" or "Campaign" or "Continue" => coreReady,
        "Instant" => coreReady && !DeferredPointValues.Running,
        "Encyclopedia" => EncyclopediaReady,
        "Editor" or "Options" or "ModManager" => StartupReady,
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
        BeginStartupScreen();
        if (action.description == "LoadMainMenu")
        {
            collecting = false;
            if (Active) Singleton<BackgroundSceneController>.Instance.StartCoroutine(LoadBehindMenu());
            return menuLoad == null ? action.GetEnumerator() : Empty();
        }
        if (!IsMusic(action) && action.description is not (
            "VoiceMessage_LoadClips" or "SensorMetadataProvider LoadSensorMetadata" or "UnitMetadataProvider LoadUnitMetadata"
            or "IniPreloader PreloadAll" or "EncyclopedicDataManager.GetMuseumEntries" or "WriteTacViewDatabase" or "ClearLaunchMarker"))
        {
            string? check = action.description switch
            {
                "ValidateFiles" => "Checking game files",
                "CheckSafeMode" => "Checking recovery settings",
                "CheckForUpdates" => "Checking Workshop updates",
                "CheckCompatability" => "Checking mod compatibility",
                "LoadInputData" => "Loading input settings",
                _ => null,
            };
            if (startupScreen && check != null)
            {
                detail = check + "…";
                StartupTimeline.Advance(check);
            }
            return action.GetEnumerator();
        }

        pending.Add(action);
        hasEncyclopedia |= IsEncyclopedia(action);
        return Empty();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.ExecuteLoadScreen), MethodType.Enumerator)]
    private static void BeginStartupScreen()
    {
        if (!collecting)
        {
            if (!startupScreen) panel?.RestoreNative();
            return;
        }
        if (startupScreen || MainMenuView.Instance != null ||
            SceneManager.GetActiveScene().name != "background") return;
        menuLoad = SceneManager.LoadSceneAsync("main-menu", LoadSceneMode.Additive);
        startupScreen = menuLoad != null;
        if (!startupScreen) return;
        detail = "Loading the main menu…";
        StartupTimeline.Advance("Startup checks and main-menu scene");
        menuLoad!.completed += _ => menuSceneReady = true;
        panel ??= new();
        ShowStartupPanel();
        Debug.Log("[Quickload] Startup progress panel requested.");
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.LoadText), MethodType.Setter)]
    private static bool KeepKeelWaiting(LoadScreen __instance, string value)
    {
        if (advancingMusic) return false;
        if (!Active || coreReady) return true;
        // Keel uses the native "Done" marker to start mods that need the unit catalogue.
        if (value == "Done") return false;
        if (startupScreen)
            return true;
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
        if (Active && !coreReady)
            throw new InvalidOperationException("Wait for Quickload to finish preparing mission data.");
        if (DeferredPointValues.Running && CampaignPricing.MissionSaveNeedsPrices(Globals.currentMissionFilePath))
            throw new InvalidOperationException(CampaignPricing.WaitMessage);
    }

    private static IEnumerator LoadBehindMenu()
    {
        var screen = Singleton<LoadScreen>.Instance;
        while (startupScreen) yield return null;
        while (screen.cameraObject.enabled || MainMenuView.Instance is not { IsVisible: true }) yield return null;
        var stages = pending.Where(action => !IsEncyclopedia(action) && !IsMusic(action)).ToArray();
        var music = pending.Where(IsMusic).ToArray();
        progress = new(stages.Select(action => StageName(action.description))
            .Concat(hasEncyclopedia ? new[] { "Encyclopedia sensors", "Encyclopedia weapons", "Encyclopedia units" } : [])
            .Append("Point values").ToArray(),
            StartupTimeline.Finish());
        RefreshMenu(MainMenuView.Instance);
        Debug.Log("[Quickload] Main menu visible; loading deferred startup data.");
        MenuProgress.LogGates(MainMenuView.Instance);
        var archives = StartupArchives.Start();
        PointValueDisplay.Start(archives?.Profile);
        var iniPrefetch = archives == null ? null : IniWarmupCache.Prefetch(archives);
        if (archives != null)
        {
            ResolvedSensors.Begin(archives);
            PointValueProfiles.Start(archives);
            if (stages.Any(action => action.description == "UnitMetadataProvider LoadUnitMetadata"))
                UnitMetadataCache.StartValidation(archives);
            if (hasEncyclopedia) EncyclopediaCache.Start(archives);
        }
        yield return null;
        // All snapshot readers have been queued. Keep the buffers until the first
        // load has consumed them or reached core readiness.
        foreach (var action in stages)
        {
            BeginStage($"Loading {StageName(action.description).ToLowerInvariant()}…");
            var start = Time.realtimeSinceStartup;
            if (action.description == "SensorMetadataProvider LoadSensorMetadata" && archives != null)
                yield return Run(ResolvedSensors.Load(action.GetEnumerator(), archives));
            else if (action.description == "UnitMetadataProvider LoadUnitMetadata")
            {
                yield return UnitMetadataCache.OpenForStartup();
                while (iniPrefetch is { IsCompleted: false }) yield return null;
                IniWarmupCache.Begin(archives, iniPrefetch);
                iniPrefetch = null;
                try { yield return Run(DeferredPointValues.LoadUnitMetadata(action.GetEnumerator())); }
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
        archives?.ReleasePrefetch();
        if (!hasEncyclopedia) ResolvedSensors.Reset();
        RefreshMenu(MainMenuView.Instance);
        MenuProgress.LogGates(MainMenuView.Instance);
        screen.LoadText = "Done";
        Debug.Log("[Quickload] Core startup data ready.");
        if (music.Length > 0)
            Singleton<BackgroundSceneController>.Instance.StartCoroutine(LoadMusic(music));
        var museum = pending.Find(IsEncyclopedia);
        if (museum.description == null)
        {
            yield return FinishPricing();
            if (!failed) FinishProgress();
            yield break;
        }

        // The scenario browser can mount mission data when it opens a briefing. Keep the
        // global encyclopedia on the main menu, and pause it during a scenario or reload.
        preparingEncyclopedia = true;
        try
        {
            yield return Run(museum.GetEnumerator(), canAdvance: () => CanBuildEncyclopedia);
            while (!EncyclopediaLoading.Ready && !failed) yield return null;
            preparingEncyclopedia = false;
            if (DeferredPointValues.Running && MainMenuView.Instance is { } menu)
            {
                RefreshMenu(menu);
                MenuProgress.LogGates(menu);
            }
            if (!failed) yield return FinishPricing();
            if (!failed) FinishProgress();
        }
        finally
        {
            EncyclopediaCache.Finish();
            ResolvedSensors.Reset();
            preparingEncyclopedia = false;
            if (!failed && archives != null) _ = archives.ExportBundles();
        }
    }

    private static IEnumerator FinishPricing()
    {
        if (DeferredPointValues.Running)
        {
            BeginStage("Updating point values…");
            // Briefings and active missions can mount their own files. Only price against the startup catalogue.
            yield return Run(DeferredPointValues.Recalculate(), canAdvance: () => CanReadStartupData);
            if (failed) yield break;
            CampaignPricing.Refresh();
        }
        else progress?.Start();
        progress?.Finish();
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
        yield return Run(routine, canAdvance: () => CanBuildEncyclopedia);
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
        panel?.RestoreNative();
        panel = null;
        progress = null;
    }

    private static IEnumerator Run(IEnumerator routine, bool backgroundMusic = false,
        Func<bool>? canAdvance = null)
    {
        try
        {
            while (true)
            {
                while (canAdvance?.Invoke() == false) yield return null;
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
                if (item is IEnumerator nested and not CustomYieldInstruction)
                    yield return Run(nested, backgroundMusic, canAdvance);
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
        if (earlyMenuVisible || !startupScreen) display?.Shrink();
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
        menuSceneReady = false;
        replacedNativeProgress = false;
        loadingMusic = advancingMusic = false;
        detail = "Preparing game data…";
        menuLoad = null;
        display = null;
        panel?.RestoreNative();
        panel = null;
        progress = null;
        nextRefresh = 0;
        IniWarmupCache.Finish(false);
        PointValueProfiles.Stop();
        DeferredPointValues.Reset();
        ResolvedSensors.Reset();
        EncyclopediaCache.Finish();
        EncyclopediaLoading.Reset();
        StartupTimeline.Reset();
    }

    private static void RefreshStartupScreen()
    {
        ShowStartupPanel();
        if (MainMenuView.Instance is not { } menu) return;
        var screen = Singleton<LoadScreen>.Instance;
        bool hasDialog = screen.WindowHost.Windows.Count > 0 || screen.ShowWarningPSE;
        if (earlyMenuVisible || menuSceneReady) screen.cameraObject.enabled = hasDialog;
        if (!hasDialog && !earlyMenuVisible && menuSceneReady && menu.IsVisible)
        {
            earlyMenuVisible = true;
            detail = "Finishing startup checks…";
            RefreshMenu(menu);
            StartupTimeline.HandOffWindow();
            Debug.Log("[Quickload] Main menu shown during startup checks.");
        }
    }

    private static void ShowStartupPanel()
    {
        if (earlyMenuVisible || panel?.ShowOnLoadingScreen() != true) return;
        int hidden = panel.HideNativeProgress();
        if (!replacedNativeProgress && hidden > 0)
        {
            replacedNativeProgress = true;
            Debug.Log($"[Quickload] Replaced {hidden} native startup progress controls.");
        }
        panel.Refresh(detail, null, null, null);
        var screen = Singleton<LoadScreen>.Instance;
        if (screen.cameraObject.enabled)
        {
            panel.View.Visibility = screen.WindowHost.Windows.Count > 0 || screen.ShowWarningPSE
                ? Noesis.Visibility.Hidden : Noesis.Visibility.Visible;
            StartupTimeline.HandOffWindow();
        }
    }

    // The startup view can be rendered before the first LoadScreen coroutine dispatch.
    [HarmonyPostfix, HarmonyPatch(typeof(LoadScreenView), MethodType.Constructor)]
    private static void PrepareNativeProgress()
    {
        if (!collecting || MainMenuView.Instance != null || SceneManager.GetActiveScene().name != "background") return;
        panel ??= new();
        panel.ShowOnLoadingScreen();
    }

    private static void RefreshMenu(MainMenuView? menu)
    {
        if (!Active || menu == null) return;
        if (display?.Menu != menu)
        {
            if (StartupReady) return;
            if (!earlyMenuVisible && startupScreen) return;
            display?.DetachPanel();
            display = new(menu, panel ??= new());
        }
        var status = progress?.Read(failed);
        display.Refresh(failed ? "Startup data failed to load. Check Player.log." : detail, status?.Timing, status?.Stage, progress,
            StartupReady);
        if (StartupReady) display = null;
    }

    internal static void AttachMenu(MainMenuView menu)
    {
        if (!Active || StartupReady || startupScreen && !earlyMenuVisible) return;
        RefreshMenu(menu);
    }

    private static IEnumerator Empty() { yield break; }
}
