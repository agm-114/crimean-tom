using System.Reflection;

using CrimeanTom.Protocol;

using HarmonyLib;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeControlledTickAdapter
{
    private const string HarmonyId = "crimean-tom.controlled-tick-experiment";
    private const BindingFlags StaticFields = BindingFlags.Static
        | BindingFlags.Public
        | BindingFlags.NonPublic;

    private static FieldInfo? _gameTimeValue;
    private static FieldInfo? _gameUnscaledTimeValue;
    private static FieldInfo? _gameDeltaTimeValue;
    private static FieldInfo? _gameFixedDeltaTimeValue;
    private static FieldInfo? _gameSensorTimeValue;
    private static FieldInfo? _gameSensorDeltaTimeValue;
    private static FieldInfo? _aiRandom;
    private static FieldInfo? _systemSurvivalRandom;

    private static bool _active;
    private static bool _pauseAfterUpdate;
    private static string _state = "idle";
    private static string _postcondition = string.Empty;
    private static int _requestedTicks;
    private static int _completedTicks;
    private static int _gameUpdateCount;
    private static int _physicsFixedUpdateCount;
    private static float _stepSeconds;
    private static float _startSimulationTime;
    private static float _startUnscaledTime;
    private static float _startSensorTime;
    private static float _targetSimulationTime;
    private static int? _seed;
    private static string[] _seededSources = [];
    private static float _originalEngineFixedDeltaTime;

    public static bool HooksInstalled { get; private set; }

    public static string InstallationError { get; private set; } = string.Empty;

    public static void Install()
    {
        if (HooksInstalled || !string.IsNullOrEmpty(InstallationError))
        {
            return;
        }
        try
        {
            _gameTimeValue = RequiredGameTimeField("time");
            _gameUnscaledTimeValue = RequiredGameTimeField("unscaledTime");
            _gameDeltaTimeValue = RequiredGameTimeField("deltaTime");
            _gameFixedDeltaTimeValue = RequiredGameTimeField("fixedDeltaTime");
            _gameSensorTimeValue = RequiredGameTimeField("sensorTime");
            _gameSensorDeltaTimeValue = RequiredGameTimeField("sensorDeltaTime");
            _aiRandom = typeof(AI).GetField("_aiRandom", StaticFields)
                ?? throw new MissingFieldException(typeof(AI).FullName, "_aiRandom");
            _systemSurvivalRandom = typeof(SystemCompartment)
                .GetField("_svRandom", StaticFields)
                ?? throw new MissingFieldException(typeof(SystemCompartment).FullName, "_svRandom");

            var harmony = new Harmony(HarmonyId);
            harmony.Patch(
                AccessTools.Method(typeof(GameTime), nameof(GameTime.OnUpdate)),
                prefix: new HarmonyMethod(
                    typeof(RuntimeControlledTickAdapter),
                    nameof(GameTimeOnUpdatePrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(GameUpdater), nameof(GameUpdater.update)),
                postfix: new HarmonyMethod(
                    typeof(RuntimeControlledTickAdapter),
                    nameof(GameUpdatePostfix)));
            harmony.Patch(
                AccessTools.Method(typeof(GameFixedUpdater), nameof(GameFixedUpdater.fixedUpdate)),
                postfix: new HarmonyMethod(
                    typeof(RuntimeControlledTickAdapter),
                    nameof(GameFixedUpdatePostfix)));
            HooksInstalled = true;
        }
        catch (Exception exception)
        {
            InstallationError = $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    public static (bool Success, string Error) Start(
        ControlledTickExperimentRequestPayload request)
    {
        if (!HooksInstalled)
        {
            return (false, string.IsNullOrEmpty(InstallationError)
                ? "Controlled tick hooks are not installed."
                : $"Controlled tick hooks failed to install: {InstallationError}");
        }
        if (_active)
        {
            return (false, "A controlled tick experiment is already running.");
        }
        if (request.TickCount is < 1 or > 10_000)
        {
            return (false, "tickCount must be between 1 and 10000.");
        }
        if (!double.IsFinite(request.StepSeconds)
            || request.StepSeconds is < 0.001d or > 0.1d)
        {
            return (false, "stepSeconds must be finite and between 0.001 and 0.1.");
        }

        _requestedTicks = request.TickCount;
        _completedTicks = 0;
        _gameUpdateCount = 0;
        _physicsFixedUpdateCount = 0;
        _stepSeconds = (float)request.StepSeconds;
        _startSimulationTime = GameTime.time;
        _startUnscaledTime = GameTime.unscaledTime;
        _startSensorTime = GameTime.sensorTime;
        _targetSimulationTime = _startSimulationTime + _stepSeconds * _requestedTicks;
        _seed = request.Seed;
        _seededSources = request.Seed is { } seed ? SeedKnownSources(seed) : [];
        _originalEngineFixedDeltaTime = Time.fixedDeltaTime;
        Time.fixedDeltaTime = _stepSeconds;
        GameTime.SetTimeCompression(1f);
        _pauseAfterUpdate = false;
        _state = "running";
        _postcondition = "controlled-ticks-running";
        _active = true;
        return (true, string.Empty);
    }

    public static ControlledTickExperimentPayload Capture() => new()
    {
        State = _state,
        Postcondition = _postcondition,
        HooksInstalled = HooksInstalled,
        InstallationError = InstallationError,
        RequestedTicks = _requestedTicks,
        CompletedTicks = _completedTicks,
        GameUpdateCount = _gameUpdateCount,
        PhysicsFixedUpdateCount = _physicsFixedUpdateCount,
        StepSeconds = _stepSeconds,
        StartSimulationTimeSeconds = _requestedTicks == 0 ? null : _startSimulationTime,
        TargetSimulationTimeSeconds = _requestedTicks == 0 ? null : _targetSimulationTime,
        CurrentSimulationTimeSeconds = GameTime.time,
        Seed = _seed,
        SeededSources = _seededSources,
        UncontrolledSources =
        [
            "per-instance-system-random",
            "job-merge-order",
            "coroutine-scheduling",
            "fixed-update-count",
            "direct-unity-time-readers",
        ],
    };

    private static bool GameTimeOnUpdatePrefix()
    {
        if (!_active)
        {
            return true;
        }

        var nextTime = _startSimulationTime + _stepSeconds * (_completedTicks + 1);
        SetFloat(_gameDeltaTimeValue, _stepSeconds);
        SetFloat(_gameFixedDeltaTimeValue, _stepSeconds);
        SetFloat(_gameSensorDeltaTimeValue, _stepSeconds);
        SetFloat(_gameTimeValue, nextTime);
        SetFloat(
            _gameUnscaledTimeValue,
            _startUnscaledTime + _stepSeconds * (_completedTicks + 1));
        SetFloat(
            _gameSensorTimeValue,
            _startSensorTime + _stepSeconds * (_completedTicks + 1));
        GameTime.TimeDilationField.Data = 1f;
        _completedTicks++;
        _gameUpdateCount++;
        _pauseAfterUpdate = _completedTicks >= _requestedTicks;
        return false;
    }

    private static void GameUpdatePostfix()
    {
        if (!_active || !_pauseAfterUpdate)
        {
            return;
        }

        _active = false;
        _pauseAfterUpdate = false;
        GameTime.Pause(muteSounds: false);
        Time.fixedDeltaTime = _originalEngineFixedDeltaTime;
        _state = "completed";
        _postcondition = "requested-logical-ticks-completed-paused";
    }

    private static void GameFixedUpdatePostfix()
    {
        if (_active)
        {
            _physicsFixedUpdateCount++;
        }
    }

    private static string[] SeedKnownSources(int seed)
    {
        Globals._rnd = new System.Random(seed);
        Globals._unityRandom = new Unity.Mathematics.Random(
            seed == 0 ? 1u : unchecked((uint)seed));
        _aiRandom!.SetValue(null, new System.Random(seed));
        _systemSurvivalRandom!.SetValue(null, new System.Random(seed));
        UnityEngine.Random.InitState(seed);
        return
        [
            "Globals._rnd",
            "Globals._unityRandom",
            "AI._aiRandom",
            "SystemCompartment._svRandom",
            "UnityEngine.Random",
        ];
    }

    private static FieldInfo RequiredGameTimeField(string propertyName) =>
        typeof(GameTime).GetField($"<{propertyName}>k__BackingField", StaticFields)
        ?? throw new MissingFieldException(typeof(GameTime).FullName, propertyName);

    private static void SetFloat(FieldInfo? field, float value) =>
        (field ?? throw new InvalidOperationException("Controlled tick fields are unavailable."))
        .SetValue(null, value);
}
