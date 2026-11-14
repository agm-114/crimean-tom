using System.Collections;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace Quickload;

[HarmonyPatch]
internal static class StartupLoading
{
    private static readonly List<LoadAction> pending = [];
    private static bool collecting = true;
    private static bool coreReady;
    private static bool failed;
    private static bool preparingEncyclopedia;
    private static bool hasEncyclopedia;
    private static string detail = "Preparing game data…";
    private static MenuProgress? display;
    private static LoadProgress? progress;
    private static float nextRefresh;

    internal static bool Active => pending.Count > 0;
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
        if (!collecting || MainMenuView.Instance != null) return action.GetEnumerator();
        if (action.description == "LoadMainMenu")
        {
            collecting = false;
            if (Active) Singleton<BackgroundSceneController>.Instance.StartCoroutine(LoadBehindMenu());
            return action.GetEnumerator();
        }
        if (action.description is not ("MusicManager LoadMusicAsync" or "MusicManager CreateAudioSource"
            or "VoiceMessage_LoadClips" or "SensorMetadataProvider LoadSensorMetadata" or "UnitMetadataProvider LoadUnitMetadata"
            or "IniPreloader PreloadAll" or "EncyclopedicDataManager.GetMuseumEntries" or "WriteTacViewDatabase" or "ClearLaunchMarker")) return action.GetEnumerator();

        pending.Add(action);
        hasEncyclopedia |= IsEncyclopedia(action);
        return Empty();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.LoadText), MethodType.Setter)]
    private static bool KeepKeelWaiting(LoadScreen __instance, string value)
    {
        if (!Active || collecting || coreReady) return true;
        // Keel uses the native "Done" marker to start mods that need the unit catalogue.
        if (value == "Done" && __instance.cameraObject.enabled) return false;
        if (__instance.cameraObject.enabled) return true;
        detail = value;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoLoad))]
    private static void RequireData()
    {
        if (Active && !coreReady) throw new InvalidOperationException("Wait for Quickload to finish loading game data before starting a mission.");
    }

    private static IEnumerator LoadBehindMenu()
    {
        var screen = Singleton<LoadScreen>.Instance;
        while (screen.cameraObject.enabled || MainMenuView.Instance is not { IsVisible: true }) yield return null;
        var stages = pending.Where(action => !IsEncyclopedia(action)).ToArray();
        progress = new(stages.Select(action => StageName(action.description))
            .Concat(hasEncyclopedia ? new[] { "Brief menu pause", "Encyclopedia sensors", "Encyclopedia weapons", "Encyclopedia units" } : []).ToArray());
        Debug.Log("[Quickload] Main menu visible; loading deferred startup data.");
        yield return null;
        foreach (var action in stages)
        {
            BeginStage($"Loading {StageName(action.description).ToLowerInvariant()}…");
            var start = Time.realtimeSinceStartup;
            yield return Run(action.GetEnumerator());
            if (failed) yield break;
            progress.Finish();
            Debug.Log($"[Quickload] {action.description}: {Time.realtimeSinceStartup - start:F2}s");
            yield return null;
        }

        coreReady = true;
        screen.LoadText = "Done";
        Debug.Log("[Quickload] Core startup data ready.");
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
            BeginStage("Encyclopedia starts shortly…");
            // Give the player a chance to choose a scenario before native encyclopedia
            // work starts, since some of its routines don't yield until a batch is done.
            yield return new WaitForSecondsRealtime(5f);
            progress.Finish();
            yield return Run(museum.GetEnumerator(), waitForMenu: true);
            while (!EncyclopediaLoading.Ready && !failed) yield return null;
            if (!failed) FinishProgress();
        }
        finally { preparingEncyclopedia = false; }
    }

    internal static IEnumerator BuildEncyclopediaPart(string part, IEnumerator routine)
    {
        if (!preparingEncyclopedia)
        {
            yield return routine;
            yield break;
        }
        BeginStage($"Loading encyclopedia {part}…");
        yield return Run(routine, waitForMenu: true);
        if (!failed) progress?.Finish();
    }

    private static void BeginStage(string label)
    {
        detail = label;
        progress?.Start();
    }

    private static string StageName(string action) => action switch
    {
        "MusicManager LoadMusicAsync" => "Music",
        "MusicManager CreateAudioSource" => "Menu audio",
        "VoiceMessage_LoadClips" => "Voice messages",
        "SensorMetadataProvider LoadSensorMetadata" => "Sensor data",
        "UnitMetadataProvider LoadUnitMetadata" => "Unit data",
        "IniPreloader PreloadAll" => "INI cache",
        "WriteTacViewDatabase" => "Tacview database",
        _ => "Startup cleanup",
    };

    private static bool IsEncyclopedia(LoadAction action) => action.description == "EncyclopedicDataManager.GetMuseumEntries";

    private static void FinishProgress()
    {
        progress?.Save();
        RefreshMenu(Singleton<MenuSystemViewModel>.Instance.CurrentWindow as MainMenuView);
        pending.Clear();
        display = null;
        progress = null;
    }

    private static IEnumerator Run(IEnumerator routine, bool waitForMenu = false)
    {
        try
        {
            while (true)
            {
                while (waitForMenu && !CanBuildEncyclopedia) yield return null;
                object? item;
                try
                {
                    if (!routine.MoveNext()) yield break;
                    item = routine.Current;
                }
                catch (Exception error)
                {
                    failed = true;
                    progress?.SetRunning(false);
                    Debug.LogError($"[Quickload] Deferred startup failed: {error}");
                    yield break;
                }
                if (item is IEnumerator nested and not CustomYieldInstruction) yield return Run(nested, waitForMenu);
                else yield return item;
                if (failed) yield break;
            }
        }
        finally { (routine as IDisposable)?.Dispose(); }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MenuSystemViewModel), "Update")]
    private static void Update(MenuSystemViewModel __instance)
    {
        if (preparingEncyclopedia) progress?.SetRunning(CanBuildEncyclopedia && !failed);
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.1f;
        RefreshMenu(__instance.CurrentWindow as MainMenuView);
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
        display.Refresh(failed ? "Startup data failed to load. Check Player.log." : detail, status?.Timing, status?.Stage, status?.Percent,
            EncyclopediaReady);
        if (EncyclopediaReady) display = null;
    }

    private static IEnumerator Empty() { yield break; }
}
