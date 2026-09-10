using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using SeapowerUI.ViewModels;

namespace SeaPower.LoadingProfiler;

internal static class LoadingHooks
{
    private static int sequence;
    internal static void Install(Harmony harmony)
    {
        Patch(AccessTools.Method(typeof(LoadScreen), "ExecuteLoadScreen"), nameof(PrepareStages), nameof(TimeScreen));
        Patch(AccessTools.PropertySetter(typeof(LoadScreen), "LoadText"), nameof(Detail));
        Patch(AccessTools.Method(typeof(MissionManager), "DoLoad"), nameof(MissionRequested));
        Patch(AccessTools.Method(typeof(MenuSystemViewModel), "Start"), nameof(CallStart), finalizer: nameof(CallEnd));
        string[] routines = ["LoadMission", "loadAssetBundles", "preloadObjects", "LoadBackgroundData", "loadVessels", "loadSubmarines", "loadAircraft", "loadHelicopters", "loadBiologic", "loadLifeRafts", "loadWeapons", "loadFormations"];
        foreach (string routine in routines)
            Patch(AccessTools.Method(typeof(SceneCreator), routine), postfix: nameof(TimeRoutine));

        string[] startupTypes = ["SeaPower.BackgroundSceneController", "SeaPower.FileManager", "SeaPower.OptionsManager", "SeaPower.LanguageResourceHandler", "SeaPower.ListenerManager", "SeaPower.InputHandler", "SeaPower.VoiceMessage", "SeaPower.MusicManager", "SeaPower.SensorMetadataProvider", "SeaPower.UnitMetadataProvider", "SeaPower.EncyclopedicDataManager", "SeaPower.LoadScreen", "NoesisUnity"];
        foreach (string name in startupTypes)
        {
            Type? type = AccessTools.TypeByName(name);
            if (type == null) { TraceLog.Write("missing-type", new { name }); continue; }
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (method.Name is "Awake" or "Start" or "OnStart" or "OnEnable" or "RefreshSearchDirectories" or "GetSearchDirectories" or "InitialLoadActions" or "Init" or "OnBeforeSplashScreen")
                    Patch(method, nameof(CallStart), finalizer: nameof(CallEnd));
        }
        TraceLog.Write("hooks-ready");

        void Patch(MethodBase? method, string? prefix = null, string? postfix = null, string? finalizer = null)
        {
            if (method == null) { TraceLog.Write("missing-hook", new { prefix, postfix, finalizer }); return; }
            TraceLog.Write("install-hook", new { name = Name(method) });
            harmony.Patch(method, Hook(prefix), Hook(postfix), finalizer: Hook(finalizer));
        }
    }

    private static HarmonyMethod? Hook(string? name) => name == null ? null : new(typeof(LoadingHooks), name);
    private static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";
    private static void PrepareStages(List<LoadAction> coroutines, out int __state)
    {
        __state = ++sequence;
        for (int i = 0; i < coroutines.Count; i++)
        {
            var action = coroutines[i];
            action.enumerator = new TimedRoutine($"stage:{__state}:{action.description}", action.enumerator);
            coroutines[i] = action;
        }
    }
    private static void TimeScreen(int __state, ref IEnumerator __result) => __result = new TimedRoutine($"screen:{__state}", __result);
    private static void TimeRoutine(MethodBase __originalMethod, ref IEnumerator __result) =>
        __result = new TimedRoutine($"routine:{sequence}:{__originalMethod.Name}", __result);
    private static void Detail(string value) => TraceLog.Write("detail", new { text = value });
    private static void MissionRequested() => TraceLog.Write("mission-request", new { path = Globals.currentMissionFilePath });
    private static void CallStart(MethodBase __originalMethod, out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        TraceLog.Write("call-begin", new { name = Name(__originalMethod) });
    }
    private static void CallEnd(MethodBase __originalMethod, long __state, Exception? __exception) =>
        TraceLog.Write(__exception == null ? "call-end" : "call-failed", new
        {
            name = Name(__originalMethod),
            seconds = (Stopwatch.GetTimestamp() - __state) / (double)Stopwatch.Frequency,
            error = __exception?.GetType().FullName
        });
}
