using System.Collections;
using HarmonyLib;
using SeaPower;
using SeapowerUI;
using UnityEngine;

namespace Quickload;

[HarmonyPatch]
public class Quickload : MonoBehaviour
{
    internal const string Id = "crimean-tom.quickload";
    private const string Tag = "[Quickload] ";
    internal static Quickload Instance = null!;
    private readonly Harmony harmony = new(Id);
    private readonly List<LoadAction> pending = [];
    private bool started;
    private bool hooksInstalled;
    private readonly SceneFileWarmup sceneFiles = new();
    internal bool CoreReady { get; private set; } = true;
    internal bool Failed { get; private set; }
    private MenuProgress? display;
    private MainMenuView? displayedMenu;
    internal LoadProgress? Progress { get; private set; }
    private float nextDisplay;
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Noesis.Button, string> MenuActions = new();
    internal bool DataReady => pending.Count == 0 || Progress?.Data.Done == true;
    private bool StageReady(string stage) => !pending.Any(action => action.description == stage)
        || Progress?.Data.Completed.ContainsKey(stage) == true;
    internal bool CanOpen(string action) => action switch
    {
        "Scenarios" => true,
        "Encyclopedia" => StageReady("UnitMetadataProvider LoadUnitMetadata"),
        "Campaign" or "Instant" => StageReady("UnitMetadataProvider LoadUnitMetadata"),
        "Editor" => DataReady,
        "Continue" => CoreReady,
        "Options" or "ModManager" => pending.Count == 0 || Progress is { } progress && progress.Lanes.All(lane => lane.Done) && !ScenarioCatalogue.IsRefreshing,
        _ => true,
    };

    private void Awake()
    {
        Instance = this;
        // Hook names and load-action contracts are verified only against this build.
        if (Application.version != "0.8.2-23444+s358")
        {
            Warn($"Unsupported game version {Application.version}; leaving startup unchanged.");
            return;
        }
        try
        {
            foreach (var type in typeof(Quickload).Assembly.GetTypes().Where(type => type.IsDefined(typeof(HarmonyPatch), false) && type != typeof(ScenarioCatalogue)))
            {
                Log($"Installing hooks: {type.Name}");
                harmony.CreateClassProcessor(type).Patch();
            }
        }
        catch { harmony.UnpatchSelf(); throw; }
        hooksInstalled = true;
        Log("Enabled deferred startup loading.");
    }

    [HarmonyPostfix, HarmonyPatch(typeof(BackgroundSceneController), "InitialLoadActions")]
    private static void Schedule(BackgroundSceneController __instance)
    {
        var loader = Instance;
        if (loader.started || loader.pending.Count > 0) return;
        // Move the dependent tail together, preserving native priorities and third-party actions.
        if (!__instance.loadActions.Any(action => action.description == "UnitMetadataProvider LoadUnitMetadata")) return;
        loader.pending.AddRange(__instance.loadActions.Where(action =>
            action.description == "VoiceMessage_LoadClips" || action.priority <= 6000
            && action.description is not ("LoadMainMenu" or "WaitWarning"))
            .OrderByDescending(action => action.priority));
        __instance.loadActions.RemoveAll(action => loader.pending.Contains(action));
        loader.CoreReady = loader.pending.Count == 0;
        Log($"Deferred {loader.pending.Count} startup stages until the menu is visible.");
    }

    private void Update()
    {
        if (!hooksInstalled) return;
        EncyclopediaCatalogue.Update();
        if (MainMenuView.Instance is not { IsVisible: true } menu) return;
        if (!Singleton<LoadScreen>.InstanceExists() || Singleton<LoadScreen>.Instance.cameraObject.enabled) return;
        if (!started && pending.Count > 0)
        {
            started = true;
            // ScenarioMenuViewModel's static properties require an initialized Noesis runtime.
            harmony.CreateClassProcessor(typeof(ScenarioCatalogue)).Patch();
            CatalogueStore.Initialize();
            ScenarioCatalogue.Initialize();
            UnitCatalogue.Initialize();
            Progress = new(pending.Select(action => action.description).ToArray(), LazyMusic.TimingKeys);
            StartCoroutine(LoadCore());
        }
        if (started && !Failed && CanOpen("Options")) sceneFiles.Begin(Application.dataPath);
        if (Progress != null && displayedMenu != menu)
        {
            display = new(menu);
            displayedMenu = menu;
        }
        if (Progress != null && Time.realtimeSinceStartup >= nextDisplay)
        {
            nextDisplay = Time.realtimeSinceStartup + 0.1f;
            display?.Update(CoreReady, Failed, Progress);
        }
    }

    private IEnumerator LoadCore()
    {
        // Give the menu one frame to render before the first native data batch.
        yield return null;
        var progress = Progress!;
        StartCoroutine(ScenarioCatalogue.Refresh());
        yield return LoadActions(progress.Data);
        if (Failed) yield break;
        yield return LoadActions(progress.Voice);
        if (Failed) yield break;
        CoreReady = true;
        ScenarioCatalogue.Ready();
        Log($"Core ready: {Singleton<UnitMetadataProvider>.Instance.UnitData.Count} unit records; voice ready={Singleton<VoiceMessage>.Instance._voiceLoaded}.");
        StartCoroutine(LazyMusic.Warm());
        yield return LoadActions(progress.Extras);
        progress.SaveIfComplete();
    }

    private IEnumerator LoadActions(LoadLane lane)
    {
        foreach (var action in pending.Where(action => lane.Keys.Contains(action.description)))
        {
            if (Failed) yield break;
            lane.Begin(action.description);
            float start = Time.realtimeSinceStartup;
            yield return Run(action.enumerator, lane);
            if (action.description == "EncyclopedicDataManager.GetMuseumEntries")
                while (EncyclopediaLoading.Pending && !Failed) yield return null;
            if (Failed) { lane.Failed = true; yield break; }
            lane.End();
            if (action.description == "UnitMetadataProvider LoadUnitMetadata") UnitCatalogue.Complete();
            Log($"Background stage {action.description}: {Time.realtimeSinceStartup - start:F3}s");
            yield return null;
        }
    }

    private IEnumerator Run(IEnumerator routine, LoadLane lane)
    {
        while (true)
        {
            object? current;
            try
            {
                if (!routine.MoveNext()) break;
                current = routine.Current;
            }
            catch (Exception error)
            {
                Failed = true;
                lane.Failed = true;
                Error($"Deferred loading failed at {lane.Current}: {error}");
                break;
            }
            if (current is IEnumerator nested) yield return Run(nested, lane);
            else yield return current;
            if (Failed) break;
        }
        (routine as IDisposable)?.Dispose();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoLoad))]
    private static void RequireReady()
    {
        Instance.sceneFiles.Stop();
        ScenarioCatalogue.ValidateLaunch(Globals.currentMissionFilePath);
        bool encyclopedia = string.Equals(Path.GetFileName(Globals.currentMissionFilePath),
            "EncyclopediaMission.ini", StringComparison.OrdinalIgnoreCase);
        if (!(encyclopedia ? Instance.StageReady("UnitMetadataProvider LoadUnitMetadata") : Instance.CoreReady))
            throw new InvalidOperationException("Required startup data is still loading. Wait before launching this mission.");
    }

    [HarmonyPatch]
    private static class MenuGate
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            new[] { "Scenarios", "Campaign", "Editor", "Instant", "Encyclopedia", "Options", "ModManager" }
                .Select(name => AccessTools.Method(typeof(MainMenuView), $"Button{name}_Click"));
        private static bool Prefix(System.Reflection.MethodBase __originalMethod) =>
            Instance.CanOpen(__originalMethod.Name.Replace("Button", "").Replace("_Click", ""));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MainMenuView), "ConnectEvent")]
    private static void RememberMenuAction(object source, string handlerName)
    {
        if (source is not Noesis.Button button || !handlerName.StartsWith("Button", StringComparison.Ordinal)) return;
        MenuActions.Remove(button);
        MenuActions.Add(button, handlerName.Replace("Button", "").Replace("_Click", ""));
    }

    internal static void Log(string text) => Debug.Log(Tag + text);
    private static void Warn(string text) => Debug.LogWarning(Tag + text);
    private static void Error(string text) => Debug.LogError(Tag + text);
    internal void Fail(Exception error)
    {
        Failed = true;
        if (Progress != null) Progress.Data.Failed = true;
        Error($"Background loading failed: {error}");
    }
    private void OnDestroy()
    {
        sceneFiles.Stop();
        harmony.UnpatchSelf();
    }
    private void OnApplicationQuit()
    {
        sceneFiles.Stop();
        TextureLoadingLifetime.Quitting = true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static void UnitFile(string objectIniName, string objectIniFolder)
    {
        if (Instance.Progress?.Data is { Current: not null } data) data.File = $"{objectIniFolder}/{objectIniName}.ini";
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ResourceLoader), nameof(ResourceLoader.getAudioClipResourceFromStreamingAssetsAwaitable))]
    private static void VoiceFile(string resourceName)
    {
        if (Instance.Progress?.Voice is { Current: not null } data) data.File = resourceName;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(Utils), nameof(Utils.openVirtualIniFile))]
    private static void IniFile(string basePath, string fileName)
    {
        if (Instance.Progress?.Data is { Current: "EncyclopedicDataManager.GetMuseumEntries" } data)
            data.File = $"{basePath}/{fileName}.ini";
    }
}
