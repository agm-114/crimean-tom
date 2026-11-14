using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using SeapowerUI.ViewModels;

namespace SeaPower.LoadingProfiler;

internal static class LoadingHooks
{
    private static int sequence;
    private static int missionLoads;
    private static string? lastDetail;
    private static bool InMissionLoad => Volatile.Read(ref missionLoads) > 0;
    internal static void Install(Harmony harmony)
    {
        Patch(AccessTools.Method(typeof(LoadScreen), "ExecuteLoadScreen"), nameof(PrepareStages), nameof(TimeScreen));
        Patch(AccessTools.PropertySetter(typeof(LoadScreen), "LoadText"), nameof(Detail));
        Patch(AccessTools.Method(typeof(MissionManager), "DoLoad"), nameof(MissionRequested));
        Patch(AccessTools.Method(typeof(MenuSystemViewModel), "Start"), nameof(CallStart), finalizer: nameof(CallEnd));
        string[] routines = ["LoadMission", "loadAssetBundles", "preloadObjects", "LoadBackgroundData", "loadVessels", "loadSubmarines", "loadAircraft", "loadHelicopters", "loadBiologic", "loadLifeRafts", "loadWeapons", "loadFormations"];
        foreach (string routine in routines)
            Patch(AccessTools.Method(typeof(SceneCreator), routine), postfix: nameof(TimeRoutine));
        Patch(AccessTools.Method(typeof(MissionManager), nameof(MissionManager.PreLoadSystems)), postfix: nameof(TimeRoutine));
        var encyclopedia = AccessTools.TypeByName("SeaPower.EncyclopedicDataManager");
        foreach (string routine in new[] { "LoadSensors", "LoadWeapons", "LoadUnitsFromMetadata" })
            Patch(AccessTools.Method(encyclopedia, routine), postfix: nameof(TimeRoutine), after: ["crimean-tom.quickload"]);

        // Straight-line work inside LoadMission that the routine hooks above cannot see.
        (string Type, string Method)[] missionCalls =
        [
            ("SeaPower.PlottingTableSerializer", "RecreateWorldUsingTemp"),
            ("SeaPower.SaveLoadManager", "LoadConfig"),
            ("SeaPower.SaveLoadManager", "LoadMetadata"),
            ("SeaPower.MissionManager", "ResetData"),
            ("SeaPower.UnitMetadataProvider", "EnsureStart"),
            ("SeaPower.Morale", "Reset"),
            ("SeaPower.ObjectsManager", "SetInitialActiveObject"),
            ("SeaPower.ObjectsManager", "InitializeHeightCalculationForAllObjects"),
            ("SeaPower.AIPlayer", "Init"),
            ("SeaPower.TerrainManager", "readIniValues"),
            ("SeaPower.TerrainManager", "initDEM"),
            ("SeaPower.DynamicSky", "initializeSkyParameters"),
            ("SeaPower.DynamicSky", "ForceUpdate"),
            ("SeaPower.Environment", "InitLocalTime"),
            ("SeaPower.Environment", "OnUpdate"),
            ("SeaPower.Environment", "setSeaState"),
            ("SeaPower.Environment", "setCloudsCoverage"),
            ("SeaPower.CameraManager", "InitWeatherEffects"),
            ("SeaPower.TacView", "Init"),
            ("ScriptRuntime", "LoadFromIni"),
            ("SeaPower.UI_Parameters", "LoadTacticalUI"),
            ("SeaPower.SceneCreator", "SetupAirSupport"),
            ("SeaPower.SceneCreator", "SetupPresetAttack"),
            ("SeaPower.SceneCreator", "CalculateMaxEnemyLossScore"),
            ("SeaPower.SceneCreator", "RefreshExistingLinearCampaignForMissionSave"),
            ("SeaPower.SceneCreator", "ReadCampaignVariables"),
            ("SeaPower.SaveLoadManager", "LoadTaskforceData"),
            ("SeaPower.SaveLoadManager", "LoadSensorAndFlightDeckData"),
            ("SeaPower.SaveLoadManager", "LoadQuickAccessData"),
            ("SeaPower.SaveLoadManager", "RequestAnnotationLoad"),
        ];
        foreach (var (typeName, methodName) in missionCalls)
        {
            Type? type = AccessTools.TypeByName(typeName);
            if (type == null) { TraceLog.Write("missing-type", new { name = typeName }); continue; }
            var methods = AccessTools.GetDeclaredMethods(type).Where(method => method.Name == methodName).ToArray();
            if (methods.Length == 0) TraceLog.Write("missing-hook", new { name = $"{typeName}.{methodName}" });
            foreach (var method in methods)
                Patch(method, nameof(MissionCallStart), finalizer: nameof(MissionCallEnd));
        }

        // Called far too often to trace individually, so these are totalled per load.
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Utils)).Where(method => method.Name is "openIniFile" or "openVirtualIniFile"))
        {
            var options = method.GetParameters().Where(parameter => parameter.ParameterType == typeof(bool)
                && parameter.Name is "useCaching" or "checkCached" or "forceDisk").ToArray();
            if (options.Length == 0) continue;
            cacheOptions.Add(method, options);
            Patch(method, nameof(AggregateStart), finalizer: nameof(AggregateEnd));
        }

        string[] startupTypes = ["SeaPower.BackgroundSceneController", "SeaPower.FileManager", "SeaPower.OptionsManager", "SeaPower.LanguageResourceHandler", "SeaPower.ListenerManager", "SeaPower.InputHandler", "SeaPower.VoiceMessage", "SeaPower.MusicManager", "SeaPower.SensorMetadataProvider", "SeaPower.UnitMetadataProvider", "SeaPower.EncyclopedicDataManager", "SeaPower.LoadScreen", "NoesisUnity"];
        foreach (string name in startupTypes)
        {
            Type? type = AccessTools.TypeByName(name);
            if (type == null) { TraceLog.Write("missing-type", new { name }); continue; }
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (method.Name is "Awake" or "Start" or "OnStart" or "OnEnable" or "RefreshSearchDirectories" or "GetSearchDirectories" or "InitialLoadActions" or "Init" or "OnBeforeSplashScreen")
                    Patch(method, nameof(CallStart), finalizer: nameof(CallEnd));
        }
        UnitMetadataTiming.Install(harmony);
        TraceLog.Write("hooks-ready");

        void Patch(MethodBase? method, string? prefix = null, string? postfix = null, string? finalizer = null, string[]? after = null)
        {
            if (method == null) { TraceLog.Write("missing-hook", new { prefix, postfix, finalizer }); return; }
            TraceLog.Write("install-hook", new { name = Name(method) });
            var post = Hook(postfix);
            if (post != null) post.after = after;
            harmony.Patch(method, Hook(prefix), post, finalizer: Hook(finalizer));
        }
    }

    private static HarmonyMethod? Hook(string? name) => name == null ? null : new(typeof(LoadingHooks), name);
    private static string Name(MethodBase method) => $"{method.DeclaringType?.Name}.{method.Name}";
    private static void PrepareStages(List<LoadAction> coroutines, out int __state)
    {
        __state = ++sequence;
        lastDetail = null;
        for (int i = 0; i < coroutines.Count; i++)
        {
            var native = coroutines[i];
            string name = $"stage:{__state}:{native.description}";
            coroutines[i] = new LoadAction(native.priority, native.description, () => new TimedRoutine(name, native.GetEnumerator()), native.numberOfSubSteps);
        }
    }
    private static void TimeScreen(int __state, ref IEnumerator __result) => __result = new TimedRoutine($"screen:{__state}", __result);
    private static void TimeRoutine(MethodBase __originalMethod, ref IEnumerator __result) =>
        __result = new TimedRoutine($"routine:{sequence}:{__originalMethod.Name}", __result,
            __originalMethod.Name is "LoadMission" or nameof(MissionManager.PreLoadSystems) ? EnterMissionLoad : null);
    private static void EnterMissionLoad(bool active)
    {
        KeyValuePair<MethodBase, Tally>[] totals;
        lock (aggregates)
        {
            if (active)
            {
                if (missionLoads++ == 0) aggregates.Clear();
                return;
            }
            if (missionLoads == 0 || --missionLoads > 0) return;
            totals = aggregates.ToArray();
            aggregates.Clear();
        }
        foreach (var total in totals)
            TraceLog.Write("mission-total", new
            {
                name = $"{Name(total.Key)}({string.Join(",", total.Key.GetParameters().Select(parameter => parameter.ParameterType.Name))})",
                calls = total.Value.Calls,
                distinct = total.Value.Paths.Count,
                cacheBypassed = total.Value.Uncached,
                notFound = total.Value.NotFound,
                failed = total.Value.Failed,
                seconds = total.Value.Ticks / (double)Stopwatch.Frequency
            });
    }
    private static void Detail(string value)
    {
        if (lastDetail == value) return;
        lastDetail = value;
        TraceLog.Write("detail", new { text = value });
    }
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

    private class Tally
    {
        internal int Calls;
        internal long Ticks;
        internal int Uncached;
        internal int NotFound;
        internal int Failed;
        internal readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);
    }
    private class Sample
    {
        internal long Started;
        internal string Path = "";
        internal bool Cached;
    }
    private static readonly Dictionary<MethodBase, Tally> aggregates = [];
    private static readonly Dictionary<MethodBase, ParameterInfo[]> cacheOptions = [];

    private static void AggregateStart(MethodBase __originalMethod, object[] __args, out Sample? __state)
    {
        if (!InMissionLoad) { __state = null; return; }
        __state = new Sample
        {
            Path = string.Join("/", __args.OfType<string>()),
            Cached = cacheOptions[__originalMethod].All(option => (bool)__args[option.Position] != (option.Name == "forceDisk"))
        };
        __state.Started = Stopwatch.GetTimestamp();
    }
    private static void AggregateEnd(MethodBase __originalMethod, Sample? __state, object? __result, Exception? __exception)
    {
        if (__state == null) return;
        long ticks = Stopwatch.GetTimestamp() - __state.Started;
        lock (aggregates)
        {
            if (!InMissionLoad) return;
            if (!aggregates.TryGetValue(__originalMethod, out var tally)) aggregates[__originalMethod] = tally = new Tally();
            tally.Calls++;
            tally.Ticks += ticks;
            tally.Paths.Add(__state.Path);
            if (!__state.Cached) tally.Uncached++;
            if (__exception != null) tally.Failed++;
            else if (__result == null) tally.NotFound++;
        }
    }

    // Several of these run every frame in a live mission, so they only trace during the load.
    private static void MissionCallStart(out long __state) => __state = InMissionLoad ? Stopwatch.GetTimestamp() : 0L;
    private static void MissionCallEnd(MethodBase __originalMethod, long __state, Exception? __exception)
    {
        if (__state == 0L) return;
        TraceLog.Write(__exception == null ? "mission-call" : "mission-call-failed", new
        {
            name = Name(__originalMethod),
            seconds = (Stopwatch.GetTimestamp() - __state) / (double)Stopwatch.Frequency,
            frame = UnityEngine.Time.frameCount,
            error = __exception?.GetType().FullName
        });
    }
}
