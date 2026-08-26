using System.Globalization;

using CrimeanTom.Protocol;

using HarmonyLib;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimePositionWatch
{
    private const string HarmonyId = "crimean-tom.position-watch";
    private const int MinimumSamples = 128;
    private const int MaximumSamplesLimit = 5_000;

    private static readonly string[] AllPhases =
    [
        "game-fixed-before",
        "game-fixed-after",
        "game-update-before",
        "render-position-before",
        "render-position-after",
        "game-update-after",
        "game-late-before",
        "camera-late-before",
        "camera-late-after",
        "game-late-after",
    ];

    private static readonly Queue<PositionWatchSamplePayload> Samples = new();
    private static readonly Dictionary<string, ObjectBase> Resolved = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Requested = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ActivePhases = new(StringComparer.Ordinal);
    private static long _sequence;
    private static int _maximumSamples = 2_000;
    private static int _droppedSampleCount;
    private static long _startedAtUnixMilliseconds;
    private static float _nextResolveAt;

    public static bool Enabled { get; private set; }

    public static bool EmitPlayerLog { get; private set; }

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
            var gameFixedUpdate = AccessTools.Method(typeof(GameMain), "FixedUpdate")
                ?? throw new MissingMethodException(typeof(GameMain).FullName, "FixedUpdate");
            var gameUpdate = AccessTools.Method(typeof(GameMain), "Update")
                ?? throw new MissingMethodException(typeof(GameMain).FullName, "Update");
            var gameLateUpdate = AccessTools.Method(typeof(GameMain), "LateUpdate")
                ?? throw new MissingMethodException(typeof(GameMain).FullName, "LateUpdate");
            var renderUpdate = AccessTools.Method(typeof(RenderPosition), nameof(RenderPosition.OnUpdate))
                ?? throw new MissingMethodException(
                    typeof(RenderPosition).FullName,
                    nameof(RenderPosition.OnUpdate));
            var cameraLateUpdate = AccessTools.Method(typeof(CameraManager), nameof(CameraManager.OnLateUpdate))
                ?? throw new MissingMethodException(
                    typeof(CameraManager).FullName,
                    nameof(CameraManager.OnLateUpdate));
            var harmony = new Harmony(HarmonyId);
            PatchBoth(
                harmony,
                gameFixedUpdate,
                nameof(GameFixedUpdatePrefix),
                nameof(GameFixedUpdatePostfix));
            PatchBoth(
                harmony,
                gameUpdate,
                nameof(GameUpdatePrefix),
                nameof(GameUpdatePostfix));
            PatchBoth(
                harmony,
                gameLateUpdate,
                nameof(GameLateUpdatePrefix),
                nameof(GameLateUpdatePostfix));
            harmony.Patch(
                renderUpdate,
                prefix: new HarmonyMethod(
                    typeof(RuntimePositionWatch),
                    nameof(RenderPositionUpdatePrefix)),
                postfix: new HarmonyMethod(
                    typeof(RuntimePositionWatch),
                    nameof(RenderPositionUpdatePostfix)));
            harmony.Patch(
                cameraLateUpdate,
                prefix: new HarmonyMethod(
                    typeof(RuntimePositionWatch),
                    nameof(CameraLateUpdatePrefix)),
                postfix: new HarmonyMethod(
                    typeof(RuntimePositionWatch),
                    nameof(CameraLateUpdatePostfix)));
            HooksInstalled = true;
            Debug.Log("[Sea Power Runtime Bridge] position-watch hooks installed.");
        }
        catch (Exception exception)
        {
            InstallationError = $"{exception.GetType().Name}: {exception.Message}";
            Debug.LogWarning(
                "[Sea Power Runtime Bridge] position-watch hooks failed: "
                + InstallationError);
        }
    }

    public static PositionWatchStatePayload Configure(PositionWatchRequestPayload request)
    {
        if (request.ClearSamples)
        {
            Samples.Clear();
            _sequence = 0;
            _droppedSampleCount = 0;
        }

        Requested.Clear();
        Resolved.Clear();
        foreach (var entityId in request.EntityIds
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            Requested.Add(entityId);
        }

        if (request.UseSelectedEntity
            && Singleton<RenderPosition>.InstanceExists()
            && Singleton<RenderPosition>.Instance.getSelectedObject() is { } selected)
        {
            var selectedId = StableUnitId(selected);
            Requested.Add(selectedId);
            Resolved[selectedId] = selected;
        }

        _maximumSamples = Math.Clamp(
            request.MaximumSamples,
            MinimumSamples,
            MaximumSamplesLimit);
        while (Samples.Count > _maximumSamples)
        {
            Samples.Dequeue();
            _droppedSampleCount++;
        }

        EmitPlayerLog = request.EmitPlayerLog;
        ActivePhases.Clear();
        var requestedPhases = request.Phases
            .Select(value => value?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var phase in requestedPhases.Count == 0
            ? AllPhases
            : AllPhases.Where(requestedPhases.Contains))
        {
            ActivePhases.Add(phase);
        }
        Enabled = request.Enabled && Requested.Count > 0;
        _startedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _nextResolveAt = 0f;
        ResolveRequestedEntities(force: true);

        Debug.Log(
            $"[Sea Power Runtime Bridge] position watch {(Enabled ? "enabled" : "disabled")}; "
            + $"requested={Requested.Count}; resolved={Resolved.Count}; maxSamples={_maximumSamples}; "
            + $"playerLog={EmitPlayerLog}.");
        return Capture();
    }

    public static PositionWatchStatePayload Capture()
    {
        ResolveRequestedEntities(force: true);
        var resolvedIds = Resolved.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new PositionWatchStatePayload
        {
            Enabled = Enabled,
            StartedAtUnixMilliseconds = _startedAtUnixMilliseconds,
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaximumSamples = _maximumSamples,
            DroppedSampleCount = _droppedSampleCount,
            EmitPlayerLog = EmitPlayerLog,
            AvailablePhases = AllPhases.ToArray(),
            ActivePhases = ActivePhases
                .OrderBy(value => Array.IndexOf(AllPhases, value))
                .ToArray(),
            RequestedEntityIds = Requested
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            ResolvedEntityIds = resolvedIds,
            UnresolvedEntityIds = Requested
                .Except(resolvedIds, StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            Samples = Samples.ToArray(),
        };
    }

    public static void Reset()
    {
        Enabled = false;
        EmitPlayerLog = false;
        Requested.Clear();
        Resolved.Clear();
        ActivePhases.Clear();
        Samples.Clear();
        _sequence = 0;
        _droppedSampleCount = 0;
        _startedAtUnixMilliseconds = 0;
        _nextResolveAt = 0f;
    }

    private static void GameFixedUpdatePrefix() => Sample("game-fixed-before");

    private static void GameFixedUpdatePostfix() => Sample("game-fixed-after");

    private static void GameUpdatePrefix() => Sample("game-update-before");

    private static void GameUpdatePostfix() => Sample("game-update-after");

    private static void RenderPositionUpdatePrefix() => Sample("render-position-before");

    private static void RenderPositionUpdatePostfix() => Sample("render-position-after");

    private static void GameLateUpdatePrefix() => Sample("game-late-before");

    private static void GameLateUpdatePostfix() => Sample("game-late-after");

    private static void CameraLateUpdatePrefix() => Sample("camera-late-before");

    private static void CameraLateUpdatePostfix() => Sample("camera-late-after");

    private static void Sample(string phase)
    {
        if (!Enabled
            || !ActivePhases.Contains(phase)
            || !Singleton<ObjectsManager>.InstanceExists())
        {
            return;
        }

        ResolveRequestedEntities(force: false);
        var renderPosition = Singleton<RenderPosition>.InstanceExists()
            ? Singleton<RenderPosition>.Instance
            : null;
        var selected = renderPosition?.getSelectedObject();
        var renderPivot = renderPosition?.getUnityPosition() ?? Vector3.zero;
        var camera = Singleton<CameraManager>.InstanceExists()
            ? Singleton<CameraManager>.Instance.getCameraUnityPosition()
            : Vector3.zero;
        var cameraComponent = Singleton<CameraManager>.InstanceExists()
            ? Singleton<CameraManager>.Instance.getCurrentCamera()?.getCamera()
            : null;

        foreach (var pair in Resolved.ToArray())
        {
            var unit = pair.Value;
            if (unit == null)
            {
                Resolved.Remove(pair.Key);
                continue;
            }

            var unity = unit.getUnityPosition();
            var geo = unit.getGeoPosition();
            var geoUnity = Utils.longLatToLocalV3(geo, Globals._currentCenterTile);
            var rotation = unit.getTransform().eulerAngles;
            var parameters = unit._obp;
            var visibleTransform = parameters?._visibleObjectsParent?.transform;
            var visible = visibleTransform?.position ?? unity;
            var visibleLocal = visibleTransform?.localPosition ?? Vector3.zero;
            var lod = parameters?._lod0Parent?.transform.position ?? visible;
            var resourceRoot = parameters?._objectResourcesRoot?.transform.position ?? unity;
            var rigidbody = parameters?._rigidbody;
            var rigidbodyPosition = rigidbody != null ? rigidbody.position : unity;
            var unitScreen = cameraComponent != null
                ? cameraComponent.WorldToScreenPoint(unity)
                : Vector3.zero;
            var visibleScreen = cameraComponent != null
                ? cameraComponent.WorldToScreenPoint(visible)
                : Vector3.zero;
            var sample = new PositionWatchSamplePayload
            {
                Sequence = ++_sequence,
                Phase = phase,
                FrameCount = Time.frameCount,
                RealtimeSeconds = Finite(Time.realtimeSinceStartup),
                SimulationTimeSeconds = double.IsFinite(GameTime.time) ? GameTime.time : null,
                EntityId = StableUnitId(unit),
                Name = unit.getName() ?? string.Empty,
                ObjectType = unit.GetType().Name,
                IsSelected = ReferenceEquals(selected, unit),
                CenterTileX = (int)Globals._currentCenterTile.x,
                CenterTileY = (int)Globals._currentCenterTile.y,
                Latitude = Finite(geo._latitude),
                Longitude = Finite(geo._longitude),
                Height = Finite(geo._height),
                UnityX = Finite(unity.x),
                UnityY = Finite(unity.y),
                UnityZ = Finite(unity.z),
                GeoUnityX = Finite(geoUnity.x),
                GeoUnityY = Finite(geoUnity.y),
                GeoUnityZ = Finite(geoUnity.z),
                GeoUnityError = Finite(Vector3.Distance(unity, geoUnity)),
                RenderPivotX = Finite(renderPivot.x),
                RenderPivotY = Finite(renderPivot.y),
                RenderPivotZ = Finite(renderPivot.z),
                RenderPivotError = Finite(Vector3.Distance(unity, renderPivot)),
                CameraX = Finite(camera.x),
                CameraY = Finite(camera.y),
                CameraZ = Finite(camera.z),
                CameraToPivotX = Finite(camera.x - renderPivot.x),
                CameraToPivotY = Finite(camera.y - renderPivot.y),
                CameraToPivotZ = Finite(camera.z - renderPivot.z),
                VisibleX = Finite(visible.x),
                VisibleY = Finite(visible.y),
                VisibleZ = Finite(visible.z),
                VisibleLocalX = Finite(visibleLocal.x),
                VisibleLocalY = Finite(visibleLocal.y),
                VisibleLocalZ = Finite(visibleLocal.z),
                VisibleToUnityError = Finite(Vector3.Distance(visible, unity)),
                LodX = Finite(lod.x),
                LodY = Finite(lod.y),
                LodZ = Finite(lod.z),
                LodToUnityError = Finite(Vector3.Distance(lod, unity)),
                ResourceRootX = Finite(resourceRoot.x),
                ResourceRootY = Finite(resourceRoot.y),
                ResourceRootZ = Finite(resourceRoot.z),
                ResourceRootToUnityError = Finite(Vector3.Distance(resourceRoot, unity)),
                HasRigidbody = rigidbody != null,
                RigidbodyX = Finite(rigidbodyPosition.x),
                RigidbodyY = Finite(rigidbodyPosition.y),
                RigidbodyZ = Finite(rigidbodyPosition.z),
                RigidbodyToUnityError = Finite(Vector3.Distance(rigidbodyPosition, unity)),
                UnitScreenX = Finite(unitScreen.x),
                UnitScreenY = Finite(unitScreen.y),
                UnitScreenDepth = Finite(unitScreen.z),
                VisibleScreenX = Finite(visibleScreen.x),
                VisibleScreenY = Finite(visibleScreen.y),
                VisibleScreenDepth = Finite(visibleScreen.z),
                PitchDegrees = Finite(rotation.x),
                HeadingDegrees = Finite(rotation.y),
                RollDegrees = Finite(rotation.z),
            };
            AddSample(sample);
        }
    }

    private static void AddSample(PositionWatchSamplePayload sample)
    {
        while (Samples.Count >= _maximumSamples)
        {
            Samples.Dequeue();
            _droppedSampleCount++;
        }
        Samples.Enqueue(sample);

        if (!EmitPlayerLog)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[Sea Power Runtime Bridge] position-watch seq={0} phase={1} frame={2} entity={3} "
            + "unity=({4:R},{5:R},{6:R}) geoUnity=({7:R},{8:R},{9:R}) geoError={10:R} "
            + "pivot=({11:R},{12:R},{13:R}) pivotError={14:R} camera=({15:R},{16:R},{17:R}) "
            + "cameraToPivot=({18:R},{19:R},{20:R})",
            sample.Sequence,
            sample.Phase,
            sample.FrameCount,
            sample.EntityId,
            sample.UnityX,
            sample.UnityY,
            sample.UnityZ,
            sample.GeoUnityX,
            sample.GeoUnityY,
            sample.GeoUnityZ,
            sample.GeoUnityError,
            sample.RenderPivotX,
            sample.RenderPivotY,
            sample.RenderPivotZ,
            sample.RenderPivotError,
            sample.CameraX,
            sample.CameraY,
            sample.CameraZ,
            sample.CameraToPivotX,
            sample.CameraToPivotY,
            sample.CameraToPivotZ));
    }

    private static void PatchBoth(
        Harmony harmony,
        System.Reflection.MethodBase target,
        string prefixName,
        string postfixName)
    {
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(typeof(RuntimePositionWatch), prefixName),
            postfix: new HarmonyMethod(typeof(RuntimePositionWatch), postfixName));
    }

    private static void ResolveRequestedEntities(bool force)
    {
        if (!Singleton<ObjectsManager>.InstanceExists())
        {
            return;
        }
        if (!force && Time.realtimeSinceStartup < _nextResolveAt)
        {
            return;
        }
        _nextResolveAt = Time.realtimeSinceStartup + 0.5f;

        var unresolved = Requested
            .Where(id => !Resolved.TryGetValue(id, out var value) || value == null)
            .ToHashSet(StringComparer.Ordinal);
        if (unresolved.Count == 0)
        {
            return;
        }

        foreach (var unit in Singleton<ObjectsManager>.Instance._listOfAllObjects
            .Where(value => value != null && value.isUnit()))
        {
            foreach (var requestedId in unresolved.ToArray())
            {
                if (!Matches(unit, requestedId))
                {
                    continue;
                }
                Resolved[requestedId] = unit;
                unresolved.Remove(requestedId);
            }
            if (unresolved.Count == 0)
            {
                return;
            }
        }
    }

    private static bool Matches(ObjectBase unit, string requestedId) =>
        string.Equals(StableUnitId(unit), requestedId, StringComparison.Ordinal)
        || string.Equals($"unit:{unit.UniqueID}", requestedId, StringComparison.Ordinal)
        || string.Equals($"object:{unit.UniqueID}", requestedId, StringComparison.Ordinal)
        || (!string.IsNullOrWhiteSpace(unit._campaignTag)
            && string.Equals(unit._campaignTag.Trim(), requestedId, StringComparison.Ordinal));

    private static string StableUnitId(ObjectBase unit) =>
        string.IsNullOrWhiteSpace(unit._campaignTag)
            ? $"unit:{unit.UniqueID}"
            : $"campaign-unit:{unit._campaignTag.Trim()}";

    private static double Finite(double value) => double.IsFinite(value) ? value : 0d;

    private static double Finite(float value) => float.IsFinite(value) ? value : 0d;
}
