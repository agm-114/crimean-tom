using CrimeanTom.Protocol;

using System.Runtime.CompilerServices;

using MapControl;

using SeaPower;

using SeapowerUI;
using SeapowerUI.ViewModels;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeVisualRenderProxyAdapter
{
    private const int MaximumProxyCount = 4_096;
    private const int MaximumAttachedMapMarkers = 384;
    private const float MapClusterCellPixels = 42f;
    private const float ViewportRefreshIntervalSeconds = 0.5f;
    private const int MaximumViewportRecordsPerFrame = 512;
    private const double ViewportWorkBudgetMilliseconds = 1.5d;
    private const int MaximumProjectionRecordsPerFrame = 256;
    private const double ProjectionWorkBudgetMilliseconds = 1.5d;
    private const float PhysicalProxyRetryIntervalSeconds = 0.25f;
    private const float PhysicalProxyRetryTimeoutSeconds = 15f;
    private const float HeadingVectorPixels = 13f;
    private static readonly Dictionary<string, VisualProxyRecord> Records =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<object, VisualProxyRecord> MapMarkerRecords =
        new(ReferenceObjectComparer.Instance);
    private static KeyValuePair<string, VisualProxyRecord>[]? _orderedRecords;
    private static HashSet<VisualProxyRecord> _attachedMapRecords = [];
    private static ViewportHitCandidate[] _viewportHitCandidates = [];
    private static ViewportRefreshWork? _viewportRefreshWork;
    private static bool _viewportRefreshRequested;
    private static ProjectionApplyWork? _projectionApplyWork;
    private static MapBase? _activeMap;
    private static MissionEditorMapSymbolItem? _floatingOriginMarker;
    private static Vector2 _floatingOriginTile = new(float.NaN, float.NaN);
    private static VisualProxyRecord? _selectedRecord;
    private static VisualProxyRecord? _pendingPhysicalProxy;
    private static float _lastViewportRefreshAt = float.NegativeInfinity;
    private static int _nextProxyNumber;
    private static string _projectionCommandSessionId = string.Empty;
    private static long _acceptedProjectionGeneration;
    private static long _acceptedProjectionRevision;
    private static long _appliedProjectionGeneration;
    private static long _appliedProjectionRevision;
    private static bool _projectionResyncRequired;
    private static long _lastProjectionParseMicroseconds;
    private static long _lastProjectionApplyMicroseconds;
    private static long _maximumProjectionSliceMicroseconds;
    private static int _lastProjectionUpsertCount;
    private static int _lastProjectionRemovalCount;
    private static int _lastViewportTotalRecordCount;
    private static int _lastViewportPrefilteredRecordCount;
    private static int _lastViewportHitCandidateCount;
    private static int _lastViewportAttachedMarkerCount;
    private static long _lastViewportRefreshMicroseconds;
    private static long _lastViewportMaximumSliceMicroseconds;
    private static long _maximumViewportSliceMicroseconds;

    public static bool HasActiveMapViewport => _activeMap is not null;

    public static void RecordProjectionParse(long elapsedMicroseconds) =>
        _lastProjectionParseMicroseconds = Math.Max(0, elapsedMicroseconds);

    public static void Tick()
    {
        UpdateFloatingOriginMarker();
        if (_pendingPhysicalProxy is { } pending
            && pending.ContinuePhysicalProxyRequest())
        {
            _pendingPhysicalProxy = null;
        }
        ProcessProjectionApply();
        ProcessViewportRefresh();
        if (_viewportRefreshWork is null
            && _viewportRefreshRequested
            && _activeMap is not null
            && Time.realtimeSinceStartup - _lastViewportRefreshAt >= ViewportRefreshIntervalSeconds)
        {
            _viewportRefreshRequested = false;
            RefreshMapViewport(_activeMap, force: true);
        }
    }

    public static MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload> Reconcile(
        ReconcileSurfaceRenderProxiesRequestPayload request,
        string commandSessionId)
    {
        if (!RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId))
        {
            return Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }

        if (!string.Equals(
            _projectionCommandSessionId,
            commandSessionId,
            StringComparison.Ordinal))
        {
            ClearAll();
            _projectionCommandSessionId = commandSessionId;
            _acceptedProjectionGeneration = 0;
            _acceptedProjectionRevision = 0;
            _appliedProjectionGeneration = 0;
            _appliedProjectionRevision = 0;
            _projectionResyncRequired = false;
        }

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return Failure(ProtocolErrorCodes.MutationPreconditionFailed, validationError);
        }

        var taskforce = ResolveTaskforce(request.TaskforceId);
        if (taskforce is null || taskforce != Globals._playerTaskforce)
        {
            return Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "taskforceId must resolve exactly to the current player task force.");
        }

        try
        {
            if (_projectionResyncRequired)
            {
                if (request.ProjectionGeneration <= _acceptedProjectionGeneration
                    || !request.IsFullSnapshot)
                {
                    return ProjectionPositionResult(
                        request,
                        "resync-required",
                        "previous-projection-apply-failed",
                        resyncRequired: true);
                }
                _projectionResyncRequired = false;
            }
            if (_projectionApplyWork is not null)
            {
                return ProjectionPositionResult(
                    request,
                    "busy",
                    "previous-projection-is-still-applying");
            }
            if (request.ProjectionGeneration < _acceptedProjectionGeneration)
            {
                return ProjectionPositionResult(
                    request,
                    "resync-required",
                    "projection-generation-is-older-than-runtime-state",
                    resyncRequired: true);
            }
            if (request.ProjectionGeneration == _acceptedProjectionGeneration
                && request.ProjectionRevision <= _acceptedProjectionRevision)
            {
                return ProjectionPositionResult(
                    request,
                    "already-applied",
                    "projection-revision-is-not-newer-than-runtime-state");
            }
            _acceptedProjectionGeneration = request.ProjectionGeneration;
            _acceptedProjectionRevision = request.ProjectionRevision;
            _lastProjectionUpsertCount = request.Proxies.Length;
            _lastProjectionRemovalCount = request.RemovedEntityIds.Length;
            _projectionApplyWork = new ProjectionApplyWork(
                request,
                ExistingRecordsFor(request));
            return ProjectionPositionResult(
                request,
                "accepted",
                "projection-snapshot-queued-for-frame-budgeted-application");
        }
        catch (Exception exception)
        {
            ClearAll();
            return Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Visual-only render-proxy materialization failed: "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public static void ClearAll()
    {
        foreach (var record in Records.Values)
        {
            try
            {
                record.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Sea Power Runtime Bridge] visual render-proxy cleanup failed: "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }
        }
        Records.Clear();
        MapMarkerRecords.Clear();
        _orderedRecords = null;
        _attachedMapRecords = [];
        _selectedRecord = null;
        _pendingPhysicalProxy = null;
        _selectedRecord = null;
        MapMarkerRecords.Clear();
        _viewportHitCandidates = [];
        _viewportRefreshWork = null;
        _viewportRefreshRequested = false;
        _projectionApplyWork = null;
        ReleaseFloatingOriginMarker();
        _activeMap = null;
        _lastViewportRefreshAt = float.NegativeInfinity;
    }

    public static void ReleaseScenePresentation()
    {
        foreach (var record in Records.Values)
        {
            record.ReleaseScenePresentation();
        }
        _pendingPhysicalProxy = null;
        _attachedMapRecords = [];
        _viewportHitCandidates = [];
        _viewportRefreshWork = null;
        _viewportRefreshRequested = false;
        ReleaseFloatingOriginMarker();
        _activeMap = null;
        _lastViewportRefreshAt = float.NegativeInfinity;
    }

    public static SurfaceRenderProxySnapshotPayload Capture() => new()
    {
        RepresentationProvider = "visual-only-preferred",
        CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        AppliedProjectionGeneration = _appliedProjectionGeneration,
        AppliedProjectionRevision = _appliedProjectionRevision,
        Proxies = OrderedRecords()
            .Select(item => item.Value.Capture(item.Key))
            .ToArray(),
        LastProjectionParseMicroseconds = _lastProjectionParseMicroseconds,
        LastProjectionApplyMicroseconds = _lastProjectionApplyMicroseconds,
        MaximumProjectionSliceMicroseconds = _maximumProjectionSliceMicroseconds,
        LastProjectionUpsertCount = _lastProjectionUpsertCount,
        LastProjectionRemovalCount = _lastProjectionRemovalCount,
        LastViewportTotalRecordCount = _lastViewportTotalRecordCount,
        LastViewportPrefilteredRecordCount = _lastViewportPrefilteredRecordCount,
        LastViewportHitCandidateCount = _lastViewportHitCandidateCount,
        LastViewportAttachedMarkerCount = _lastViewportAttachedMarkerCount,
        LastViewportRefreshMicroseconds = _lastViewportRefreshMicroseconds,
        LastViewportMaximumSliceMicroseconds = _lastViewportMaximumSliceMicroseconds,
        MaximumViewportSliceMicroseconds = _maximumViewportSliceMicroseconds,
    };

    public static SelectedSurfaceRenderProxyPayload CaptureSelected()
    {
        var selected = _selectedRecord is { IsDisposed: false, IsSelected: true } record
            ? record.Capture(record.EntityId)
            : null;
        return new SelectedSurfaceRenderProxyPayload
        {
            HasSelection = selected is not null,
            Proxy = selected,
        };
    }

    public static bool TryResolveVisiblePivot(string requestedId, out GameObject pivot)
    {
        var matches = OrderedRecords()
            .Where(item => item.Value.WorldModelVisible
                && MatchesEntityReference(item.Value.CampaignId, item.Key, requestedId))
            .Select(item => item.Value.Root!)
            .Distinct()
            .ToArray();
        pivot = matches.Length == 1 ? matches[0] : null!;
        return matches.Length == 1;
    }

    public static bool TryRequestPhysicalProxy(string requestedId, out string error)
    {
        var matches = OrderedRecords()
            .Where(item => MatchesEntityReference(item.Value.CampaignId, item.Key, requestedId))
            .Select(item => item.Value)
            .Distinct()
            .ToArray();
        if (matches.Length != 1)
        {
            error = $"Unit ID '{requestedId}' resolved to {matches.Length} campaign map projections.";
            return false;
        }

        return matches[0].RequestPhysicalProxy(out error);
    }

    public static void SuppressForOwnedEntity(string entityId)
    {
        if (!Records.TryGetValue(entityId, out var record))
        {
            return;
        }
        if (ReferenceEquals(_pendingPhysicalProxy, record))
        {
            _pendingPhysicalProxy = null;
        }
        if (ReferenceEquals(_selectedRecord, record))
        {
            _selectedRecord = null;
        }
        record.ClearSelectionAndHideWorldModel();
        record.Suspend();
        _attachedMapRecords.Remove(record);
        _viewportRefreshRequested = _activeMap is not null;
    }

    public static bool TrySelectMapProjection(
        object hitObject,
        GeoPosition mousePosition,
        MapBase map,
        float selectionRadiusPixels)
    {
        if (map is null
            || mousePosition.IsInvalid()
            || selectionRadiusPixels <= 0f)
        {
            return false;
        }

        RefreshMapViewport(map);

        if (MapMarkerRecords.TryGetValue(hitObject, out var direct))
        {
            // Authored map-symbol items are presentation records, not native
            // units. Sea Power can mark one Selected without opening the
            // campaign promotion path, so consume the hit here and publish the
            // same physical-proxy request used by the proximity fallback.
            return direct.RequestPhysicalProxy(out _);
        }

        // Sea Power's native unit, formation, and waypoint records always win.
        // Campaign symbols can arrive here through a Noesis presentation
        // wrapper rather than the exact data item, so an arbitrary
        // IMapSelectable check would make those symbols visibly highlight but
        // never enter the campaign selection path.
        if (hitObject is MapUnitViewModel or MapFormationViewModel or WaypointViewModel)
        {
            ClearCurrentSelection();
            return false;
        }

        var mousePoint = map.LocationToView(mousePosition);
        VisualProxyRecord? nearest = null;
        var nearestDistanceSquared = (double)selectionRadiusPixels * selectionRadiusPixels;
        // The renderer only attaches a bounded representative set. Keep every
        // authorized record selectable through its exact screen-space position.
        foreach (var candidate in _viewportHitCandidates)
        {
            var deltaX = candidate.ScreenX - mousePoint.X;
            var deltaY = candidate.ScreenY - mousePoint.Y;
            var distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared > nearestDistanceSquared)
            {
                continue;
            }

            nearestDistanceSquared = distanceSquared;
            nearest = candidate.Record;
        }

        if (nearest is null)
        {
            ClearCurrentSelection();
            return false;
        }

        return nearest.RequestPhysicalProxy(out _);
    }

    public static void RefreshMapViewport(MapBase map, bool force = false)
    {
        _activeMap = map;
        UpdateFloatingOriginMarker(force: true);
        if (_viewportRefreshWork is not null)
        {
            _viewportRefreshRequested = true;
            return;
        }
        var now = Time.realtimeSinceStartup;
        if (!force && now - _lastViewportRefreshAt < ViewportRefreshIntervalSeconds)
        {
            _viewportRefreshRequested = true;
            return;
        }
        var width = Math.Max(1f, map.ActualWidth);
        var height = Math.Max(1f, map.ActualHeight);
        var preparationTimer = System.Diagnostics.Stopwatch.StartNew();
        var allRecords = OrderedRecords()
            .Select(item => item.Value)
            .ToArray();
        var hasViewportBounds = TryGetViewportBounds(
            map,
            width,
            height,
            out var viewportBounds);
        var work = new ViewportRefreshWork(
            map,
            width,
            height,
            allRecords.Length,
            allRecords,
            hasViewportBounds,
            viewportBounds);
        preparationTimer.Stop();
        work.ElapsedMicroseconds = ElapsedMicroseconds(preparationTimer);
        work.MaximumSliceMicroseconds = work.ElapsedMicroseconds;
        _maximumViewportSliceMicroseconds = Math.Max(
            _maximumViewportSliceMicroseconds,
            work.ElapsedMicroseconds);
        _viewportRefreshWork = work;
    }

    private static void UpdateFloatingOriginMarker(bool force = false)
    {
        if (_activeMap is null)
        {
            return;
        }

        var foreground = Globals._mainGameViewModel?.Map?.DisplayMap?.ForegroundElements;
        if (foreground is null)
        {
            return;
        }

        var tile = Globals._currentCenterTile;
        if (!force
            && tile == _floatingOriginTile
            && _floatingOriginMarker is not null
            && foreground.Contains(_floatingOriginMarker))
        {
            return;
        }

        _floatingOriginMarker ??= new MissionEditorMapSymbolItem
        {
            SectionName = "CrimeanTom_FloatingOrigin",
            Kind = MissionEditorMapSymbolKind.Label,
            ShowInMission = true,
            ColorPreset = DrawingColorPreset.Yellow,
            FontPreset = DrawingFontPreset.Type,
            FontSize = 14f,
            Label = "+ Floating origin",
        };

        var origin = Utils.worldPositionFromUnityToLongLat(
            Vector3.zero,
            tile);
        origin._height = 0d;
        _floatingOriginMarker.PlaceAt(origin);
        if (!foreground.Contains(_floatingOriginMarker))
        {
            foreground.Add(_floatingOriginMarker);
        }
        _floatingOriginTile = tile;
    }

    private static void ReleaseFloatingOriginMarker()
    {
        if (_floatingOriginMarker is not null)
        {
            Globals._mainGameViewModel?.Map?.DisplayMap?.ForegroundElements
                ?.Remove(_floatingOriginMarker);
        }
        _floatingOriginMarker = null;
        _floatingOriginTile = new Vector2(float.NaN, float.NaN);
    }

    private static void ProcessViewportRefresh()
    {
        if (_viewportRefreshWork is not { } work)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var processed = 0;
        try
        {
            if (work.Phase == ViewportRefreshPhase.PrefilterRecords)
            {
                while (work.NextSourceRecordIndex < work.SourceRecords.Length
                    && processed < MaximumViewportRecordsPerFrame
                    && (processed == 0
                        || stopwatch.Elapsed.TotalMilliseconds < ViewportWorkBudgetMilliseconds))
                {
                    var record = work.SourceRecords[work.NextSourceRecordIndex++];
                    if (!record.IsDisposed && IsInside(work.ViewportBounds, record.MapPosition))
                    {
                        work.PrefilteredRecords.Add(record);
                    }
                    processed++;
                }
                if (work.NextSourceRecordIndex < work.SourceRecords.Length)
                {
                    RecordViewportSlice(work, stopwatch);
                    return;
                }

                work.CompletePrefilter();
                RecordViewportSlice(work, stopwatch);
                return;
            }

            if (work.Phase == ViewportRefreshPhase.ScanRecords)
            {
                while (work.NextRecordIndex < work.Records.Length
                    && processed < MaximumViewportRecordsPerFrame
                    && (processed == 0
                        || stopwatch.Elapsed.TotalMilliseconds < ViewportWorkBudgetMilliseconds))
                {
                    var record = work.Records[work.NextRecordIndex++];
                    if (record.IsDisposed)
                    {
                        processed++;
                        continue;
                    }
                    var point = work.Map.LocationToView(record.MapPosition);
                    processed++;
                    if (!double.IsFinite(point.X)
                        || !double.IsFinite(point.Y)
                        || point.X < -MapClusterCellPixels
                        || point.X > work.Width + MapClusterCellPixels
                        || point.Y < -MapClusterCellPixels
                        || point.Y > work.Height + MapClusterCellPixels)
                    {
                        continue;
                    }
                    work.HitCandidates.Add(new ViewportHitCandidate(record, point.X, point.Y));

                    var key = (
                        (int)Math.Floor(point.X / MapClusterCellPixels),
                        (int)Math.Floor(point.Y / MapClusterCellPixels));
                    if (!work.Clusters.TryGetValue(key, out var cluster))
                    {
                        work.Clusters[key] = new MapCluster(
                            record,
                            point.X,
                            point.Y,
                            1,
                            record.GroupId,
                            record.GroupName,
                            false);
                        continue;
                    }

                    var centerX = (key.Item1 + 0.5d) * MapClusterCellPixels;
                    var centerY = (key.Item2 + 0.5d) * MapClusterCellPixels;
                    var candidateDistance = SquaredDistance(point.X, point.Y, centerX, centerY);
                    var currentDistance = SquaredDistance(
                        cluster.ScreenX,
                        cluster.ScreenY,
                        centerX,
                        centerY);
                    var useCandidate = record.IsSelected
                        || !cluster.Representative.IsSelected && candidateDistance < currentDistance;
                    work.Clusters[key] = new MapCluster(
                        useCandidate ? record : cluster.Representative,
                        useCandidate ? point.X : cluster.ScreenX,
                        useCandidate ? point.Y : cluster.ScreenY,
                        cluster.Count + 1,
                        cluster.GroupId,
                        cluster.GroupName,
                        cluster.MixedGroups
                            || !string.Equals(
                                cluster.GroupId,
                                record.GroupId,
                                StringComparison.Ordinal));
                }

                if (work.NextRecordIndex < work.Records.Length)
                {
                    RecordViewportSlice(work, stopwatch);
                    return;
                }

                work.Phase = ViewportRefreshPhase.PrepareAttachments;
                RecordViewportSlice(work, stopwatch);
                return;
            }

            if (work.Phase == ViewportRefreshPhase.PrepareAttachments)
            {
                work.PrepareAttachments(_attachedMapRecords, _selectedRecord);
                work.Phase = ViewportRefreshPhase.DetachMarkers;
                RecordViewportSlice(work, stopwatch);
                return;
            }

            if (work.Phase == ViewportRefreshPhase.DetachMarkers)
            {
                while (work.NextDetachedIndex < work.DetachedRecords.Length
                    && processed < MaximumViewportRecordsPerFrame
                    && (processed == 0
                        || stopwatch.Elapsed.TotalMilliseconds < ViewportWorkBudgetMilliseconds))
                {
                    work.DetachedRecords[work.NextDetachedIndex++].SetMapMarkerAttached(false);
                    processed++;
                }
                if (work.NextDetachedIndex < work.DetachedRecords.Length)
                {
                    RecordViewportSlice(work, stopwatch);
                    return;
                }
                work.Phase = ViewportRefreshPhase.AttachMarkers;
                RecordViewportSlice(work, stopwatch);
                return;
            }

            if (work.Phase == ViewportRefreshPhase.AttachMarkers)
            {
                while (work.NextAttachmentIndex < work.Attachments.Length
                    && processed < MaximumViewportRecordsPerFrame
                    && (processed == 0
                        || stopwatch.Elapsed.TotalMilliseconds < ViewportWorkBudgetMilliseconds))
                {
                    var pair = work.Attachments[work.NextAttachmentIndex++];
                    pair.Key.SetMapMarkerAttached(true, pair.Value.Label);
                    processed++;
                }
                if (work.NextAttachmentIndex < work.Attachments.Length)
                {
                    RecordViewportSlice(work, stopwatch);
                    return;
                }
                work.Phase = ViewportRefreshPhase.Complete;
                RecordViewportSlice(work, stopwatch);
                return;
            }

            _attachedMapRecords = work.Desired.Keys.ToHashSet();
            _viewportHitCandidates = work.HitCandidates.ToArray();
            _lastViewportRefreshAt = Time.realtimeSinceStartup;
            RecordViewportSlice(work, stopwatch);
            _lastViewportTotalRecordCount = work.TotalRecordCount;
            _lastViewportPrefilteredRecordCount = work.Records.Length;
            _lastViewportHitCandidateCount = work.HitCandidates.Count;
            _lastViewportAttachedMarkerCount = work.Desired.Count;
            _lastViewportRefreshMicroseconds = work.ElapsedMicroseconds;
            _lastViewportMaximumSliceMicroseconds = work.MaximumSliceMicroseconds;
            _viewportRefreshWork = null;
            // Viewport changes can arrive every frame while the player pans or
            // zooms. Keep one trailing refresh request, but let Tick debounce it
            // instead of starting another 4,096-record scan immediately.
        }
        catch (Exception exception)
        {
            _viewportRefreshWork = null;
            _viewportRefreshRequested = false;
            Debug.LogWarning(
                "[Sea Power Runtime Bridge] campaign map viewport refresh failed: "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool TryGetViewportBounds(
        MapBase map,
        double width,
        double height,
        out BoundingBox bounds)
    {
        bounds = default!;
        try
        {
            var padding = MapClusterCellPixels;
            const double metresPerDegree = 111_319.49079327358d;
            var paddedWidth = width + 2d * padding;
            var paddedHeight = height + 2d * padding;
            var paddedDiagonal = Math.Sqrt(
                paddedWidth * paddedWidth + paddedHeight * paddedHeight);
            var viewScale = map.ViewTransform.Scale;
            if (!double.IsFinite(viewScale) || viewScale <= 0d)
            {
                return false;
            }
            var longitudeSpanUpperBound = paddedDiagonal
                / (viewScale * metresPerDegree);
            if (!double.IsFinite(longitudeSpanUpperBound)
                || longitudeSpanUpperBound >= 359.5d)
            {
                return false;
            }
            bounds = map.ViewRectToBoundingBox(new Noesis.Rect(
                -padding,
                -padding,
                (float)width + 2f * padding,
                (float)height + 2f * padding));
            if (!double.IsFinite(bounds.West)
                || !double.IsFinite(bounds.East)
                || !double.IsFinite(bounds.South)
                || !double.IsFinite(bounds.North)
                || bounds.Width >= 359.5d)
            {
                return false;
            }

            return true;
        }
        catch
        {
            // A map can report a transient invalid transform while it opens or
            // changes layout. The existing screen-space test remains the safe
            // fallback for that refresh.
            bounds = default!;
            return false;
        }
    }

    private static bool IsInside(BoundingBox bounds, GeoPosition position)
    {
        if (position.Latitude < bounds.South || position.Latitude > bounds.North)
        {
            return false;
        }
        if (bounds.Width >= 359.5d)
        {
            return true;
        }

        for (var offset = -720d; offset <= 720d; offset += 360d)
        {
            var longitude = position.Longitude + offset;
            if (longitude >= bounds.West && longitude <= bounds.East)
            {
                return true;
            }
        }
        return false;
    }

    private static void RecordViewportSlice(
        ViewportRefreshWork work,
        System.Diagnostics.Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var elapsedMicroseconds = ElapsedMicroseconds(stopwatch);
        work.ElapsedMicroseconds = checked(work.ElapsedMicroseconds + elapsedMicroseconds);
        work.MaximumSliceMicroseconds = Math.Max(
            work.MaximumSliceMicroseconds,
            elapsedMicroseconds);
        _maximumViewportSliceMicroseconds = Math.Max(
            _maximumViewportSliceMicroseconds,
            elapsedMicroseconds);
    }

    private static double SquaredDistance(
        double x,
        double y,
        double targetX,
        double targetY)
    {
        var deltaX = x - targetX;
        var deltaY = y - targetY;
        return deltaX * deltaX + deltaY * deltaY;
    }

    private static string ClusterLabel(MapCluster cluster)
    {
        if (cluster.Count == 1)
        {
            return cluster.Representative.MapLabel;
        }
        if (string.IsNullOrWhiteSpace(cluster.GroupName))
        {
            return $"{cluster.Count} units";
        }
        if (cluster.MixedGroups)
        {
            return $"{cluster.Count} nearby units";
        }
        return cluster.Count > 1
            ? $"{cluster.GroupName} ({cluster.Count} units)"
            : cluster.GroupName;
    }

    private static void ProcessProjectionApply()
    {
        if (_projectionApplyWork is not { } work)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var processed = 0;
        try
        {
            while (work.NextExistingIndex < work.Existing.Length
                && processed < MaximumProjectionRecordsPerFrame
                && (processed == 0
                    || stopwatch.Elapsed.TotalMilliseconds < ProjectionWorkBudgetMilliseconds))
            {
                var pair = work.Existing[work.NextExistingIndex++];
                processed++;
                if (!Records.TryGetValue(pair.Key, out var current)
                    || !ReferenceEquals(current, pair.Value))
                {
                    continue;
                }

                var conflictsWithOwnedEntity =
                    RuntimeOwnedMutationAdapter.HasActiveOwnedEntity(pair.Key);
                if (string.Equals(
                        pair.Value.CampaignId,
                        work.Request.CampaignId,
                        StringComparison.Ordinal)
                    && work.Requested.ContainsKey(pair.Key)
                    && !conflictsWithOwnedEntity)
                {
                    continue;
                }

                if (pair.Value.Suspend())
                {
                    work.SuspendedCount++;
                }
                if (conflictsWithOwnedEntity)
                {
                    work.ExcludedOwnedEntities.Add(pair.Key);
                }
                pair.Value.Dispose();
                Records.Remove(pair.Key);
                _orderedRecords = null;
                _attachedMapRecords.Remove(pair.Value);
            }

            while (work.NextRequestedIndex < work.RequestedOrdered.Length
                && processed < MaximumProjectionRecordsPerFrame
                && (processed == 0
                    || stopwatch.Elapsed.TotalMilliseconds < ProjectionWorkBudgetMilliseconds))
            {
                var spec = work.RequestedOrdered[work.NextRequestedIndex++];
                processed++;
                if (RuntimeOwnedMutationAdapter.HasActiveOwnedEntity(spec.EntityId))
                {
                    work.ExcludedOwnedEntities.Add(spec.EntityId);
                    continue;
                }

                if (!Records.TryGetValue(spec.EntityId, out var record))
                {
                    record = VisualProxyRecord.Create(
                        work.Request.CampaignId,
                        spec.EntityId,
                        ++_nextProxyNumber);
                    Records[spec.EntityId] = record;
                    _orderedRecords = null;
                    work.CreatedCount++;
                }
                else
                {
                    work.UpdatedCount++;
                }
                record.Apply(spec);
            }

            if (work.NextExistingIndex < work.Existing.Length
                || work.NextRequestedIndex < work.RequestedOrdered.Length)
            {
                _maximumProjectionSliceMicroseconds = Math.Max(
                    _maximumProjectionSliceMicroseconds,
                    ElapsedMicroseconds(stopwatch));
                return;
            }

            _appliedProjectionGeneration = work.Request.ProjectionGeneration;
            _appliedProjectionRevision = work.Request.ProjectionRevision;
            _projectionApplyWork = null;
            _lastProjectionApplyMicroseconds = ElapsedMicroseconds(work.TotalStopwatch);
            _maximumProjectionSliceMicroseconds = Math.Max(
                _maximumProjectionSliceMicroseconds,
                ElapsedMicroseconds(stopwatch));
            if (_activeMap is not null)
            {
                RefreshMapViewport(_activeMap, force: true);
            }
            Debug.Log(
                "[Sea Power Runtime Bridge] applied campaign projection "
                + $"{_appliedProjectionGeneration}/{_appliedProjectionRevision}: "
                + $"{work.CreatedCount} created, {work.UpdatedCount} updated, "
                + $"{work.SuspendedCount} suspended, "
                + $"{work.ExcludedOwnedEntities.Count} owned entities excluded.");
        }
        catch (Exception exception)
        {
            _projectionApplyWork = null;
            _projectionResyncRequired = true;
            Debug.LogWarning(
                "[Sea Power Runtime Bridge] campaign projection apply failed: "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string? Validate(ReconcileSurfaceRenderProxiesRequestPayload? request)
    {
        if (request is null
            || request.ProjectionGeneration <= 0
            || request.ProjectionRevision <= 0
            || !Guid.TryParseExact(request.CampaignId, "N", out _)
            || string.IsNullOrWhiteSpace(request.TaskforceId)
            || request.TaskforceId.Length > 128
            || request.Proxies is null
            || request.RemovedEntityIds is null
            || request.Proxies.Length + request.RemovedEntityIds.Length > MaximumProxyCount)
        {
            return "Positive projection generation and revision values, a canonical campaign ID, bounded taskforce ID, and at most 4096 map projections are required.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in request.Proxies)
        {
            if (spec is null
                || !ValidProjectionId(request.CampaignId, spec.EntityId)
                || !ids.Add(spec.EntityId)
                || string.IsNullOrWhiteSpace(spec.Name)
                || spec.Name.Length > 128
                || spec.Relationship is not ("Friendly" or "HostileContact")
                || spec.Relationship == "Friendly"
                    && (string.IsNullOrWhiteSpace(spec.UnitTypeId)
                        || string.IsNullOrWhiteSpace(spec.VariantId)
                        || string.IsNullOrWhiteSpace(spec.LoadoutId))
                || spec.Relationship == "HostileContact"
                    && !spec.EntityId.StartsWith(CampaignContactRef.CanonicalPrefix, StringComparison.Ordinal)
                || spec.UncertaintyRadiusMeters is < 0 or > 10_000_000
                || !ValidPosition(spec.Latitude, spec.Longitude, spec.HeadingDegrees)
                || spec.CampaignTelegraph is < -1 or > 5)
            {
                return "Every map projection must have a unique canonical friendly/contact identity, "
                    + "valid relationship and presentation fields, finite position, and bounded uncertainty.";
            }
        }
        foreach (var removedEntityId in request.RemovedEntityIds)
        {
            if (!ValidProjectionId(request.CampaignId, removedEntityId)
                || !ids.Add(removedEntityId))
            {
                return "Projection removals must be unique canonical identities and cannot also be upserts.";
            }
        }
        return null;
    }

    private static bool ValidProjectionId(string campaignId, string value) =>
        CampaignEntityRef.TryParseCanonical(campaignId, value, out _)
        || CampaignContactRef.TryParse(campaignId, value, out _);

    private static bool ValidPosition(double latitude, double longitude, double heading) =>
        double.IsFinite(latitude)
        && latitude is >= -90d and <= 90d
        && double.IsFinite(longitude)
        && longitude is >= -180d and <= 180d
        && double.IsFinite(heading);

    private static double NormalizeHeading(double heading)
    {
        var normalized = heading % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    private static bool MatchesEntityReference(
        string campaignId,
        string canonicalEntityId,
        string requestedId) =>
        (CampaignEntityRef.TryParseCanonical(campaignId, requestedId, out var canonical)
            || CampaignEntityRef.TryParseStableUnitId(campaignId, requestedId, out canonical))
        && string.Equals(canonical.Canonical, canonicalEntityId, StringComparison.Ordinal);

    private static KeyValuePair<string, VisualProxyRecord>[] OrderedRecords() =>
        _orderedRecords ??= Records
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

    private static KeyValuePair<string, VisualProxyRecord>[] ExistingRecordsFor(
        ReconcileSurfaceRenderProxiesRequestPayload request)
    {
        if (request.IsFullSnapshot)
        {
            return OrderedRecords();
        }

        return request.RemovedEntityIds
            .OrderBy(item => item, StringComparer.Ordinal)
            .Where(Records.ContainsKey)
            .Select(entityId => new KeyValuePair<string, VisualProxyRecord>(
                entityId,
                Records[entityId]))
            .ToArray();
    }

    private static void ClearCurrentSelection()
    {
        _selectedRecord?.ClearSelectionAndHideWorldModel();
        _selectedRecord = null;
    }

    private static Taskforce? ResolveTaskforce(string requestedId)
    {
        var taskforces = Singleton<TaskforceManager>.Instance._taskForces
            .Where(taskforce => taskforce is not null)
            .ToArray();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < taskforces.Length; index++)
        {
            var taskforce = taskforces[index];
            var identityName = string.IsNullOrWhiteSpace(taskforce._nameInMissionFile)
                ? $"{taskforce.Side.ToString().ToLowerInvariant()}-{index + 1}"
                : taskforce._nameInMissionFile.Trim();
            var baseId = $"taskforce:{identityName}";
            occurrences.TryGetValue(baseId, out var occurrence);
            occurrence++;
            occurrences[baseId] = occurrence;
            var id = occurrence == 1 ? baseId : $"{baseId}#{occurrence}";
            if (string.Equals(id, requestedId, StringComparison.Ordinal))
            {
                return taskforce;
            }
        }
        return null;
    }

    private static MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload> Failure(
        string code,
        string message) =>
        MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload>.Failure(code, message);

    private static MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload>
        ProjectionPositionResult(
            ReconcileSurfaceRenderProxiesRequestPayload request,
            string outcome,
            string postcondition,
            bool resyncRequired = false)
    {
        var payload = new ReconcileSurfaceRenderProxiesResultPayload
        {
            Outcome = outcome,
            Postcondition = postcondition,
            CampaignId = request.CampaignId,
            RequestedCount = request.Proxies.Length + request.RemovedEntityIds.Length,
            ResyncRequired = resyncRequired,
            Proxies = OrderedRecords()
                .Where(item => item.Value.WorldModelVisible || item.Value.MapMarkerIsVisible)
                .Select(item => item.Value.Capture(item.Key))
                .ToArray(),
        };
        PopulateProjectionPosition(payload);
        return MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload>.Success(payload);
    }

    private static long ElapsedMicroseconds(System.Diagnostics.Stopwatch stopwatch) =>
        Math.Max(0, (long)Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds * 1_000d));

    private static void PopulateProjectionPosition(
        ReconcileSurfaceRenderProxiesResultPayload payload)
    {
        payload.AcceptedProjectionGeneration = _acceptedProjectionGeneration;
        payload.AcceptedProjectionRevision = _acceptedProjectionRevision;
        payload.AppliedProjectionGeneration = _appliedProjectionGeneration;
        payload.AppliedProjectionRevision = _appliedProjectionRevision;
    }

    private sealed record MapCluster(
        VisualProxyRecord Representative,
        double ScreenX,
        double ScreenY,
        int Count,
        string GroupId,
        string GroupName,
        bool MixedGroups);

    private sealed record MapMarkerAttachment(string Label);

    private sealed record ViewportHitCandidate(
        VisualProxyRecord Record,
        double ScreenX,
        double ScreenY);

    private enum ViewportRefreshPhase
    {
        PrefilterRecords,
        ScanRecords,
        PrepareAttachments,
        DetachMarkers,
        AttachMarkers,
        Complete,
    }

    private sealed class ReferenceObjectComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceObjectComparer Instance = new();

        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    private sealed class ViewportRefreshWork(
        MapBase map,
        double width,
        double height,
        int totalRecordCount,
        VisualProxyRecord[] sourceRecords,
        bool hasViewportBounds,
        BoundingBox viewportBounds)
    {
        public MapBase Map { get; } = map;
        public double Width { get; } = width;
        public double Height { get; } = height;
        public int TotalRecordCount { get; } = totalRecordCount;
        public VisualProxyRecord[] SourceRecords { get; } = sourceRecords;
        public BoundingBox ViewportBounds { get; } = viewportBounds;
        public List<VisualProxyRecord> PrefilteredRecords { get; } = [];
        public VisualProxyRecord[] Records { get; private set; } = hasViewportBounds
            ? []
            : sourceRecords;
        public ViewportRefreshPhase Phase { get; set; } = hasViewportBounds
            ? ViewportRefreshPhase.PrefilterRecords
            : ViewportRefreshPhase.ScanRecords;
        public int NextSourceRecordIndex { get; set; }
        public int NextRecordIndex { get; set; }
        public int NextDetachedIndex { get; set; }
        public int NextAttachmentIndex { get; set; }
        public long ElapsedMicroseconds { get; set; }
        public long MaximumSliceMicroseconds { get; set; }
        public Dictionary<(int X, int Y), MapCluster> Clusters { get; } = [];
        public List<ViewportHitCandidate> HitCandidates { get; } = [];
        public Dictionary<VisualProxyRecord, MapMarkerAttachment> Desired { get; private set; } = [];
        public VisualProxyRecord[] DetachedRecords { get; private set; } = [];
        public KeyValuePair<VisualProxyRecord, MapMarkerAttachment>[] Attachments { get; private set; } = [];

        public void CompletePrefilter()
        {
            Records = PrefilteredRecords.ToArray();
            Phase = ViewportRefreshPhase.ScanRecords;
        }

        public void PrepareAttachments(
            IEnumerable<VisualProxyRecord> attachedRecords,
            VisualProxyRecord? selectedRecord)
        {
            Desired = Clusters.Values
                .OrderByDescending(item => item.Representative.IsSelected)
                .ThenBy(item => SquaredDistance(
                    item.ScreenX,
                    item.ScreenY,
                    Width / 2d,
                    Height / 2d))
                .Take(MaximumAttachedMapMarkers)
                .ToDictionary(
                    item => item.Representative,
                    item => new MapMarkerAttachment(ClusterLabel(item)));
            if (selectedRecord is { IsDisposed: false, IsSelected: true })
            {
                Desired[selectedRecord] = new MapMarkerAttachment(selectedRecord.MapLabel);
            }
            DetachedRecords = attachedRecords
                .Where(record => !Desired.ContainsKey(record))
                .ToArray();
            Attachments = Desired.ToArray();
        }
    }

    private sealed class ProjectionApplyWork
    {
        public ProjectionApplyWork(
            ReconcileSurfaceRenderProxiesRequestPayload request,
            KeyValuePair<string, VisualProxyRecord>[] existing)
        {
            Request = request;
            Existing = existing;
            Requested = request.Proxies.ToDictionary(
                item => item.EntityId,
                StringComparer.Ordinal);
            RequestedOrdered = request.Proxies
                .OrderBy(item => item.EntityId, StringComparer.Ordinal)
                .ToArray();
        }

        public ReconcileSurfaceRenderProxiesRequestPayload Request { get; }
        public KeyValuePair<string, VisualProxyRecord>[] Existing { get; }
        public Dictionary<string, SurfaceRenderProxySpecPayload> Requested { get; }
        public SurfaceRenderProxySpecPayload[] RequestedOrdered { get; }
        public HashSet<string> ExcludedOwnedEntities { get; } = new(StringComparer.Ordinal);
        public int NextExistingIndex { get; set; }
        public int NextRequestedIndex { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int SuspendedCount { get; set; }
        public System.Diagnostics.Stopwatch TotalStopwatch { get; } =
            System.Diagnostics.Stopwatch.StartNew();
    }

    private sealed class VisualProxyRecord : IDisposable
    {
        private ObjectBaseParameters? _parameters;
        private GameObject? _root;
        private CampaignMapSymbolItem? _mapMarker;
        private MapPolyline? _headingLine;
        private double _headingMarkerScale = double.NaN;
        private bool _mapMarkerAttached;
        private bool _mapAnnotationDirty = true;
        private string _attachedMapLabel = string.Empty;
        private bool _disposed;
        private bool _selected;
        private bool _physicalProxyRequested;
        private bool _relocationPrepared;
        private string _physicalProxyState = "idle";
        private string _physicalProxyError = string.Empty;
        private bool _requiresLocalityHandoff;
        private float _nextPhysicalProxyAttemptAt;
        private float _physicalProxyDeadlineAt;
        private string _name = string.Empty;
        private string _relationship = string.Empty;
        private string _classification = string.Empty;
        private string _confidence = string.Empty;
        private int _uncertaintyRadiusMeters;
        private string _unitTypeId = string.Empty;
        private string _variantId = string.Empty;
        private string _loadoutId = string.Empty;
        private string _mapDisplayName = string.Empty;
        private string _mapTypeAbbreviation = string.Empty;
        private string _simulationDomain = string.Empty;
        private string _groupId = string.Empty;
        private string _groupName = string.Empty;
        private long _groupRevision;
        private double _latitude;
        private double _longitude;
        private double _headingDegrees;
        private int _campaignTelegraph;
        private readonly int _proxyNumber;
        private readonly string _entityId;

        private VisualProxyRecord(string campaignId, string entityId, int proxyNumber)
        {
            CampaignId = campaignId;
            _entityId = entityId;
            _proxyNumber = proxyNumber;
        }

        public string CampaignId { get; }
        public GameObject? Root => _root;

        public static VisualProxyRecord Create(
            string campaignId,
            string entityId,
            int proxyNumber)
        {
            return new VisualProxyRecord(campaignId, entityId, proxyNumber);
        }

        public void Apply(SurfaceRenderProxySpecPayload spec)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VisualProxyRecord));
            }

            _mapAnnotationDirty = _mapAnnotationDirty
                || !string.Equals(_name, spec.Name, StringComparison.Ordinal)
                || !string.Equals(_relationship, spec.Relationship, StringComparison.Ordinal)
                || !string.Equals(_classification, spec.Classification, StringComparison.Ordinal)
                || !string.Equals(_simulationDomain, spec.SimulationDomain, StringComparison.Ordinal)
                || !string.Equals(_unitTypeId, spec.UnitTypeId, StringComparison.Ordinal)
                || !string.Equals(_variantId, spec.VariantId, StringComparison.Ordinal)
                || _latitude != spec.Latitude
                || _longitude != spec.Longitude
                || _headingDegrees != NormalizeHeading(spec.HeadingDegrees);
            _name = spec.Name;
            _relationship = spec.Relationship;
            _classification = spec.Classification;
            _confidence = spec.Confidence;
            _uncertaintyRadiusMeters = spec.UncertaintyRadiusMeters;
            _unitTypeId = spec.UnitTypeId;
            _variantId = spec.VariantId;
            _loadoutId = spec.LoadoutId;
            _simulationDomain = spec.SimulationDomain;
            ResolveNativeMapIdentity();
            _groupId = spec.GroupId;
            _groupName = spec.GroupName;
            _groupRevision = spec.GroupRevision;
            _latitude = spec.Latitude;
            _longitude = spec.Longitude;
            _headingDegrees = NormalizeHeading(spec.HeadingDegrees);
            _campaignTelegraph = spec.CampaignTelegraph;
            var position = PresentationPosition();
            if (_root != null)
            {
                _root.SetActive(_selected);
                if (_selected)
                {
                    ApplyWorldTransform(position);
                }
            }
        }

        public bool Suspend()
        {
            if (_disposed)
            {
                return false;
            }
            var changed = _root != null && _root.activeSelf || MapMarkerVisible;
            if (_root != null)
            {
                _root.SetActive(false);
            }
            RemoveMapAnnotations();
            return changed;
        }

        public SurfaceRenderProxyStatePayload Capture(string entityId) => new()
        {
            EntityId = entityId,
            StableUnitId = $"campaign-unit:{entityId}",
            GameUniqueId = null,
            IsVisible = WorldModelVisible || MapMarkerVisible,
            IsSimulated = false,
            RepresentationKind = WorldModelVisible
                ? "visual-model-and-map-annotation"
                : "map-annotation",
            HasNativeSimulationObject = false,
            WorldModelVisible = WorldModelVisible,
            MapMarkerVisible = MapMarkerVisible,
            IsSelected = _selected,
            PhysicalProxyRequested = _physicalProxyRequested,
            PhysicalProxyState = _physicalProxyState,
            PhysicalProxyError = _physicalProxyError,
            RequiresLocalityHandoff = _requiresLocalityHandoff,
            Relationship = _relationship,
            Classification = _classification,
            Confidence = _confidence,
            UncertaintyRadiusMeters = _uncertaintyRadiusMeters,
            GroupId = _groupId,
            GroupName = _groupName,
            GroupRevision = _groupRevision,
            UnitTypeId = _unitTypeId,
            VariantId = _variantId,
            LoadoutId = _loadoutId,
            Latitude = _latitude,
            Longitude = _longitude,
            HeadingDegrees = _headingDegrees,
            CampaignTelegraph = _campaignTelegraph,
        };

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            RemoveMapAnnotations();
            if (_mapMarker is not null)
            {
                MapMarkerRecords.Remove(_mapMarker);
            }
            if (ReferenceEquals(_selectedRecord, this))
            {
                _selectedRecord = null;
            }
            if (_parameters is not null)
            {
                Cleanup(_parameters);
            }
            else if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _parameters = null;
            _root = null;
        }

        internal bool WorldModelVisible => !_disposed && _root != null && _root.activeSelf;
        internal bool IsDisposed => _disposed;
        internal bool IsSelected => _selected;
        internal bool MapMarkerIsVisible => MapMarkerVisible;
        internal GeoPosition MapPosition => PresentationPosition();
        internal string EntityId => _entityId;
        internal string Name => _name;
        internal string MapLabel => BuildNativeMapLabel();
        internal string GroupId => _groupId;
        internal string GroupName => _groupName;

        private bool MapMarkerVisible => !_disposed && _mapMarkerAttached && _mapMarker is not null;

        internal void SetMapMarkerAttached(bool attached, string label = "")
        {
            if (_disposed)
            {
                return;
            }
            if (!attached)
            {
                RemoveMapAnnotations();
                return;
            }
            if (_mapMarkerAttached
                && !_mapAnnotationDirty
                && string.Equals(_attachedMapLabel, label, StringComparison.Ordinal))
            {
                if (_activeMap is not null)
                {
                    UpdateHeadingMarkerGeometry(_activeMap, PresentationPosition());
                }
                return;
            }
            EnsureMapAnnotations(PresentationPosition(), label);
        }

        internal void ReleaseScenePresentation()
        {
            RemoveMapAnnotations();
            if (_mapMarker is not null)
            {
                MapMarkerRecords.Remove(_mapMarker);
            }
            _mapMarker = null;
            _headingLine = null;
            try
            {
                if (_parameters is not null)
                {
                    Cleanup(_parameters);
                }
                else if (_root != null)
                {
                    UnityEngine.Object.Destroy(_root);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[Sea Power Runtime Bridge] scene proxy release failed: "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }
            _parameters = null;
            _root = null;
            _selected = false;
            if (ReferenceEquals(_selectedRecord, this))
            {
                _selectedRecord = null;
            }
            _physicalProxyRequested = false;
            _relocationPrepared = false;
            _physicalProxyState = "idle";
            _physicalProxyError = string.Empty;
            _requiresLocalityHandoff = false;
        }

        private void EnsureMapAnnotations(GeoPosition position, string label)
        {
            var map = Globals._mainGameViewModel?.Map;
            var foreground = map?.DisplayMap?.ForegroundElements;
            if (foreground is null)
            {
                return;
            }

            if (_mapMarker is null)
            {
                _mapMarker = new CampaignMapSymbolItem(
                    () => RequestPhysicalProxy(out _))
                {
                    SectionName = $"CrimeanTom_{_proxyNumber}",
                    ShowInMission = true,
                    ColorPreset = _relationship == "HostileContact"
                        ? DrawingColorPreset.Red
                        : DrawingColorPreset.Blue,
                    FontPreset = DrawingFontPreset.Type,
                    FontSize = 18f,
                };
                MapMarkerRecords[_mapMarker] = this;
            }
            if (_headingLine is null && _activeMap is not null)
            {
                _headingLine = new MapPolyline
                {
                    ParentMap = _activeMap,
                    StrokeThickness = 1.5f,
                };
            }
            else if (_headingLine is not null && _activeMap is not null)
            {
                _headingLine.ParentMap = _activeMap;
            }
            _mapMarker.Kind = MissionEditorMapSymbolKind.UnitIcon;
            _mapMarker.Domain = MapSymbolDomain();
            _mapMarker.Side = _relationship == "HostileContact" ? "Hostile" : "Friendly";
            _mapMarker.ColorPreset = _relationship == "HostileContact"
                ? DrawingColorPreset.Red
                : DrawingColorPreset.Blue;
            if (_headingLine is not null && _activeMap is not null)
            {
                _headingLine.Stroke = _mapMarker.UnitColorBrush;
                UpdateHeadingMarkerGeometry(_activeMap, position, force: true);
            }
            _mapMarker.Bearing = _headingDegrees.ToString(
                "0.###",
                System.Globalization.CultureInfo.InvariantCulture);
            _mapMarker.SetLabel(_selected
                ? BuildNativeMapLabel()
                : label);
            _mapMarker.PlaceAt(position);
            if (_headingLine is not null && !foreground.Contains(_headingLine))
            {
                foreground.Add(_headingLine);
            }
            if (!foreground.Contains(_mapMarker))
            {
                foreground.Add(_mapMarker);
            }
            _mapMarkerAttached = true;
            _mapAnnotationDirty = false;
            _attachedMapLabel = label;
        }

        private void UpdateHeadingMarkerGeometry(
            MapBase map,
            GeoPosition position,
            bool force = false)
        {
            if (_headingLine is null || position.IsInvalid())
            {
                return;
            }

            var scale = map.ViewTransform.Scale;
            if (!force
                && double.IsFinite(_headingMarkerScale)
                && Math.Abs(_headingMarkerScale - scale) < 0.000001d)
            {
                return;
            }

            var origin = map.LocationToView(position);
            var headingRadians = _headingDegrees * Math.PI / 180d;
            var endpoint = map.ViewToGeoPosition(new Noesis.Point(
                (float)(origin.X + Math.Sin(headingRadians) * HeadingVectorPixels),
                (float)(origin.Y - Math.Cos(headingRadians) * HeadingVectorPixels)));
            if (endpoint.IsInvalid())
            {
                return;
            }

            _headingLine.Locations = new[] { position, endpoint };
            _headingMarkerScale = scale;
        }

        internal bool RequestPhysicalProxy(out string error)
        {
            if (_disposed)
            {
                error = "campaign map projection is disposed";
                return false;
            }
            _selected = true;
            if (!ReferenceEquals(_selectedRecord, this))
            {
                ClearCurrentSelection();
                _selectedRecord = this;
            }
            _mapAnnotationDirty = true;
            _mapMarker?.SetSelection(true);
            _physicalProxyRequested = _relationship is "Friendly" or "HostileContact";
            if (Globals._mainGameViewModel?.Map?.DisplayMap is { } displayMap)
            {
                displayMap.Center = PresentationPosition();
            }
            if (!_physicalProxyRequested)
            {
                _physicalProxyState = "failed";
                _physicalProxyError = "campaign relationship cannot create a local visual proxy";
                error = _physicalProxyError;
                return false;
            }

            if (WorldModelVisible)
            {
                _physicalProxyState = "focused";
                _physicalProxyError = string.Empty;
                FocusWorldModel();
                error = string.Empty;
                return true;
            }

            if (ReferenceEquals(_pendingPhysicalProxy, this)
                && (_physicalProxyState is "selected" or "relocating" or "terrain-streaming" or "spawning"))
            {
                error = string.Empty;
                return true;
            }

            _physicalProxyState = "selected";
            _physicalProxyError = string.Empty;
            _requiresLocalityHandoff = false;
            _relocationPrepared = false;
            _nextPhysicalProxyAttemptAt = Time.realtimeSinceStartup;
            _physicalProxyDeadlineAt = Time.realtimeSinceStartup + PhysicalProxyRetryTimeoutSeconds;
            _pendingPhysicalProxy = this;
            EnsureMapAnnotations(PresentationPosition(), BuildNativeMapLabel());
            PublishSelectionChanged();
            error = string.Empty;
            return true;
        }

        internal bool ContinuePhysicalProxyRequest()
        {
            if (_disposed || !_selected || !_physicalProxyRequested)
            {
                return true;
            }
            if (WorldModelVisible)
            {
                _physicalProxyState = "focused";
                _physicalProxyError = string.Empty;
                FocusWorldModel();
                return true;
            }

            var now = Time.realtimeSinceStartup;
            if (now < _nextPhysicalProxyAttemptAt)
            {
                return false;
            }
            _nextPhysicalProxyAttemptAt = now + PhysicalProxyRetryIntervalSeconds;

            if (now >= _physicalProxyDeadlineAt)
            {
                _physicalProxyState = "failed";
                if (string.IsNullOrWhiteSpace(_physicalProxyError))
                {
                    _physicalProxyError = "campaign proxy materialization timed out";
                }
                return true;
            }

            if (!_relocationPrepared)
            {
                _physicalProxyState = "relocating";
                var preparation = RuntimeTacticalLocationAdapter.PrepareForLocalMapSelection(
                    _latitude,
                    _longitude);
                if (!preparation.Succeeded)
                {
                    _physicalProxyState = "failed";
                    _physicalProxyError = preparation.ErrorMessage;
                    _requiresLocalityHandoff = string.Equals(
                        preparation.ErrorCode,
                        ProtocolErrorCodes.LocalityRebaseRequired,
                        StringComparison.Ordinal);
                    PublishSelectionChanged();
                    return true;
                }
                _requiresLocalityHandoff = false;
                _relocationPrepared = true;
            }

            if (UsesTerrainSurface())
            {
                var terrain = RuntimeTerrainPlacementAdapter.Probe(new()
                {
                    Latitude = _latitude,
                    Longitude = _longitude,
                });
                if (!terrain.PlacementReady)
                {
                    _physicalProxyState = "terrain-streaming";
                    _physicalProxyError = string.Equals(
                        terrain.Postcondition,
                        "terrain-sample-is-water",
                        StringComparison.Ordinal)
                        ? "target position does not resolve to land terrain"
                        : "target terrain is streaming";
                    if (string.Equals(
                        terrain.Postcondition,
                        "terrain-sample-is-water",
                        StringComparison.Ordinal))
                    {
                        _physicalProxyState = "failed";
                        return true;
                    }
                    return false;
                }
            }

            _physicalProxyState = "spawning";
            _physicalProxyError = string.Empty;
            if (string.Equals(_relationship, "HostileContact", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(_unitTypeId)
                    || _uncertaintyRadiusMeters > 0)
                {
                    _physicalProxyState = "contact-focused";
                    return true;
                }
                EnsureWorldModel();
            }
            else
            {
                EnsureWorldModel();
            }
            if (!WorldModelVisible)
            {
                _physicalProxyState = "failed";
                if (string.IsNullOrWhiteSpace(_physicalProxyError))
                {
                    _physicalProxyError = "campaign unit visual model could not be loaded";
                }
                return true;
            }

            FocusWorldModel();
            _physicalProxyState = "focused";
            _physicalProxyError = string.Empty;
            Debug.Log(
                $"[Sea Power Runtime Bridge] selected campaign proxy '{_name}' and focused its local visual model.");
            return true;
        }

        private void PublishSelectionChanged() =>
            RuntimeEventPublisher.CampaignMapSelectionChanged(new SelectedSurfaceRenderProxyPayload
            {
                HasSelection = true,
                Proxy = Capture(_entityId),
            });

        private void FocusWorldModel()
        {
            if (_root == null || !Singleton<RenderPosition>.InstanceExists())
            {
                return;
            }

            var renderPosition = Singleton<RenderPosition>.Instance;
            renderPosition.setGlobalPosition(PresentationPosition());
            renderPosition.switchToPivot(_root);
        }

        internal void ClearSelectionAndHideWorldModel()
        {
            if (_physicalProxyRequested)
            {
                _physicalProxyState = "cancelled";
            }
            _selected = false;
            if (ReferenceEquals(_selectedRecord, this))
            {
                _selectedRecord = null;
            }
            _mapAnnotationDirty = true;
            _physicalProxyRequested = false;
            _relocationPrepared = false;
            _requiresLocalityHandoff = false;
            if (_parameters is not null)
            {
                Cleanup(_parameters);
            }
            else if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _parameters = null;
            _root = null;
            _mapMarker?.SetSelection(false);
            _mapMarker?.SetLabel(string.Empty);
        }

        private void EnsureWorldModel()
        {
            if (_root != null)
            {
                _root.SetActive(true);
                ApplyWorldTransform(PresentationPosition());
                _physicalProxyError = string.Empty;
                return;
            }
            if (string.IsNullOrWhiteSpace(_unitTypeId))
            {
                _physicalProxyError = "campaign unit has no installed visual profile";
                return;
            }

            foreach (var candidate in VisualModelCandidates())
            {
                var parameters = new ObjectBaseParameters
                {
                    _objectVariantReference = candidate.VariantId,
                    _loadoutVariant = candidate.LoadoutId,
                };
                try
                {
                    if (!ObjectBaseLoader.loadObjectFromInternalResources(
                        parameters,
                        candidate.UnitTypeId,
                        candidate.Folder,
                        1_000_000 + _proxyNumber,
                        Singleton<SceneCreator>.Instance.GeoCenterPosition,
                        PresentationPosition(),
                        nationOverride: string.Empty)
                        || parameters._rootGameObject == null)
                    {
                        Cleanup(parameters);
                        continue;
                    }
                    parameters._rootGameObject.name = $"CrimeanTom render proxy - {_name}";
                    Sanitize(parameters);
                    _parameters = parameters;
                    _root = parameters._rootGameObject;
                    ApplyWorldTransform(PresentationPosition());
                    _physicalProxyError = string.Empty;
                    if (!string.Equals(
                        candidate.UnitTypeId,
                        _unitTypeId,
                        StringComparison.Ordinal))
                    {
                        Debug.LogWarning(
                            $"[Sea Power Runtime Bridge] campaign proxy '{_name}' uses installed "
                            + $"visual fallback '{candidate.UnitTypeId}' because catalog model "
                            + $"'{_unitTypeId}' was unavailable.");
                    }
                    return;
                }
                catch (Exception exception)
                {
                    Cleanup(parameters);
                    _physicalProxyError = $"{exception.GetType().Name}: {exception.Message}";
                }
            }
            if (string.IsNullOrWhiteSpace(_physicalProxyError))
            {
                _physicalProxyError = "installed catalog model could not be loaded";
            }
        }

        private void ApplyWorldTransform(GeoPosition position)
        {
            if (_root == null)
            {
                return;
            }
            var local = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
            float? terrainHeight = null;
            if (UsesTerrainSurface())
            {
                var terrain = RuntimeTerrainPlacementAdapter.Probe(new()
                {
                    Latitude = _latitude,
                    Longitude = _longitude,
                });
                if (!terrain.PlacementReady || terrain.TerrainHeightUnity is not { } height)
                {
                    return;
                }
                terrainHeight = (float)height;
                local.y = terrainHeight.Value;
            }
            _root.transform.position = local;
            _root.transform.eulerAngles = new Vector3(0f, (float)_headingDegrees, 0f);
            if (terrainHeight is { } surfaceHeight)
            {
                var renderers = _root.GetComponentsInChildren<Renderer>(includeInactive: false)
                    .Where(item => item is MeshRenderer or SkinnedMeshRenderer)
                    .ToArray();
                if (renderers.Length > 0)
                {
                    var minimumVisibleY = renderers.Min(item => item.bounds.min.y);
                    const float terrainClearanceUnity = 0.01f;
                    if (float.IsFinite(minimumVisibleY)
                        && minimumVisibleY < surfaceHeight + terrainClearanceUnity)
                    {
                        _root.transform.position += Vector3.up
                            * (surfaceHeight + terrainClearanceUnity - minimumVisibleY);
                    }
                }
            }
        }

        private bool UsesTerrainSurface() =>
            _simulationDomain is "Ground" or "Installation" or "LandAbstract"
            || _classification.Contains("Land", StringComparison.OrdinalIgnoreCase)
            || _classification.Contains("Ground", StringComparison.OrdinalIgnoreCase)
            || _classification.Contains("Installation", StringComparison.OrdinalIgnoreCase);

        private string MapSymbolDomain()
        {
            var domain = string.IsNullOrWhiteSpace(_simulationDomain)
                ? _classification
                : _simulationDomain;
            if (domain.Contains("Air", StringComparison.OrdinalIgnoreCase))
            {
                return "air";
            }
            if (domain.Contains("Rotary", StringComparison.OrdinalIgnoreCase)
                || domain.Contains("Helicopter", StringComparison.OrdinalIgnoreCase))
            {
                return "helicopter";
            }
            if (domain.Contains("Maritime", StringComparison.OrdinalIgnoreCase)
                || domain.Contains("Surface", StringComparison.OrdinalIgnoreCase))
            {
                return _unitTypeId.Contains("_ss", StringComparison.OrdinalIgnoreCase)
                    || _unitTypeId.Contains("sub", StringComparison.OrdinalIgnoreCase)
                    || _classification.Contains("Submarine", StringComparison.OrdinalIgnoreCase)
                        ? "submarine"
                        : "surface";
            }
            if (domain.Contains("Installation", StringComparison.OrdinalIgnoreCase))
            {
                if (_unitTypeId.Contains("airbase", StringComparison.OrdinalIgnoreCase)
                    || _unitTypeId.Contains("runway", StringComparison.OrdinalIgnoreCase))
                {
                    return "airbase";
                }
                if (_unitTypeId.Contains("sam", StringComparison.OrdinalIgnoreCase)
                    || _unitTypeId.Contains("mim-", StringComparison.OrdinalIgnoreCase))
                {
                    return "sam";
                }
                if (_unitTypeId.Contains("radar", StringComparison.OrdinalIgnoreCase)
                    || _unitTypeId.Contains("ewr", StringComparison.OrdinalIgnoreCase))
                {
                    return "radar";
                }
                if (_unitTypeId.Contains("port", StringComparison.OrdinalIgnoreCase))
                {
                    return "port";
                }
                return "installation";
            }
            if (domain.Contains("Ground", StringComparison.OrdinalIgnoreCase)
                || domain.Contains("Land", StringComparison.OrdinalIgnoreCase))
            {
                return "mobileunit";
            }
            if (domain.Contains("Logistics", StringComparison.OrdinalIgnoreCase))
            {
                return "installation";
            }
            // Sea Power's authored unit-symbol resolver intentionally treats an
            // unrecognized domain as its native generic surface-symbol fallback.
            return "unknown";
        }

        private string BuildNativeMapLabel()
        {
            if (string.Equals(_relationship, "Friendly", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(_name))
            {
                // Vanilla player objects label the map from their authored short
                // name, without a contact number or class prefix.
                return _name.Trim();
            }

            var trackId = 1_000 + _proxyNumber;
            if (trackId % 10 == 0)
            {
                trackId += 10;
            }
            var contactLine = string.IsNullOrWhiteSpace(_mapTypeAbbreviation)
                ? trackId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{trackId} ({_mapTypeAbbreviation})";
            return string.IsNullOrWhiteSpace(_mapDisplayName)
                ? contactLine
                : $"{contactLine}\n{_mapDisplayName}";
        }

        private void ResolveNativeMapIdentity()
        {
            _mapDisplayName = _name;
            _mapTypeAbbreviation = string.Empty;
            var unitData = Singleton<UnitMetadataProvider>.Instance?.UnitData;
            if (unitData is null || string.IsNullOrWhiteSpace(_unitTypeId))
            {
                return;
            }

            foreach (var folder in PreferredFolders(_simulationDomain))
            {
                if (!unitData.TryGetValue((folder, _unitTypeId), out var entry)
                    || entry.Item2 is not { } metadata)
                {
                    continue;
                }

                var variantId = string.IsNullOrWhiteSpace(_variantId) ? "Default" : _variantId;
                if (metadata._variants.TryGetValue(variantId, out var variant))
                {
                    _mapDisplayName = ResolvePreferredMapDisplayName(FirstNonEmpty(
                        variant.DefaultName,
                        metadata.DefaultName));
                    _mapTypeAbbreviation = FirstNonEmpty(
                        variant.TypeAbbr,
                        metadata._typeAbbr);
                }
                else
                {
                    _mapDisplayName = ResolvePreferredMapDisplayName(metadata.DefaultName);
                    _mapTypeAbbreviation = metadata._typeAbbr ?? string.Empty;
                }
                return;
            }
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? string.Empty;

        private string ResolvePreferredMapDisplayName(string catalogName)
        {
            var authoredName = _name.Trim();
            var operationalDescription = new[]
            {
                " class", " group", " patrol", " squadron", " convoy", " escort",
                " formation", " command", " site", " battery", " complex", " sortie",
                " target", " wing", " regiment", " traffic", " combatant",
            }.Any(token => authoredName.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (!operationalDescription && !string.IsNullOrWhiteSpace(authoredName))
            {
                return authoredName;
            }
            return FirstNonEmpty(catalogName, authoredName);
        }

        private GeoPosition PresentationPosition()
        {
            var altitudeMeters = _simulationDomain switch
            {
                "Air" => 5_000d,
                "RotaryWing" => 250d,
                "Maritime" when _classification.Contains(
                    "Submarine",
                    StringComparison.OrdinalIgnoreCase) => -50d,
                _ => 0d,
            };
            if (_relationship == "HostileContact"
                && _classification.Contains("Air", StringComparison.OrdinalIgnoreCase))
            {
                altitudeMeters = 5_000d;
            }
            return new GeoPosition(_latitude, _longitude, altitudeMeters);
        }

        private static IEnumerable<string> PreferredFolders(string simulationDomain) =>
            simulationDomain switch
            {
                "Air" or "RotaryWing" => ["aircraft", "vessels", "land_units"],
                "Ground" or "Installation" or "LandAbstract" => ["land_units", "vessels", "aircraft"],
                _ => ["vessels", "land_units", "aircraft"],
            };

        private readonly record struct VisualModelCandidate(
            string UnitTypeId,
            string VariantId,
            string LoadoutId,
            string Folder);

        private void RemoveMapAnnotations()
        {
            if (!_mapMarkerAttached)
            {
                return;
            }
            var foreground = Globals._mainGameViewModel?.Map?.DisplayMap?.ForegroundElements;
            if (_mapMarker is not null)
            {
                foreground?.Remove(_mapMarker);
            }
            if (_headingLine is not null)
            {
                foreground?.Remove(_headingLine);
                _headingLine.ParentMap = null;
            }
            _mapMarkerAttached = false;
            _attachedMapLabel = string.Empty;
        }

        private static void Sanitize(ObjectBaseParameters parameters)
        {
            var root = parameters._rootGameObject;
            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(includeInactive: true))
            {
                behaviour.enabled = false;
            }
            foreach (var collider in root.GetComponentsInChildren<Collider>(includeInactive: true))
            {
                collider.enabled = false;
            }
            foreach (var body in root.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            {
                body.detectCollisions = false;
                body.isKinematic = true;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            for (var index = 0; index < parameters._meshesObject.Length; index++)
            {
                parameters._meshesObject[index].SetActive(index == 0);
            }
            if (parameters._damageObjectParent != null)
            {
                parameters._damageObjectParent.SetActive(false);
            }
            if (parameters._collidersObject != null)
            {
                parameters._collidersObject.SetActive(false);
            }
        }

        private static void Cleanup(ObjectBaseParameters parameters)
        {
            if (Singleton<ObjectsManager>.InstanceExists()
                && !string.IsNullOrWhiteSpace(parameters._objectIniName)
                && !string.IsNullOrWhiteSpace(parameters._objectVariantReference)
                && !string.IsNullOrWhiteSpace(parameters._objectSerialNumber))
            {
                Singleton<ObjectsManager>.Instance.FreeSerialNumber(
                    parameters._objectIniName,
                    parameters._objectVariantReference,
                    parameters._objectSerialNumber);
            }
            if (parameters._rootGameObject != null)
            {
                UnityEngine.Object.Destroy(parameters._rootGameObject);
            }
            foreach (var material in parameters._materialsDictionary?.Values.Distinct()
                ?? Enumerable.Empty<Material>())
            {
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }
        }

        private sealed class CampaignMapSymbolItem(
            Action selectedByPlayer) : MissionEditorMapSymbolItem, IMapSelectable
        {
            private bool _selected;

            public void SetLabel(string label)
            {
                Label = label;
            }

            public void SetSelection(bool selected)
            {
                _selected = selected;
            }

            public bool Selected
            {
                get => _selected;
                set
                {
                    if (_selected == value)
                    {
                        return;
                    }
                    _selected = value;
                    if (value)
                    {
                        selectedByPlayer();
                    }
                }
            }
        }

        private IEnumerable<VisualModelCandidate> VisualModelCandidates()
        {
            var variantId = string.IsNullOrWhiteSpace(_variantId) ? "Default" : _variantId;
            var loadoutId = string.IsNullOrWhiteSpace(_loadoutId) ? "Default" : _loadoutId;
            foreach (var folder in PreferredFolders(_simulationDomain))
            {
                yield return new(_unitTypeId, variantId, loadoutId, folder);
            }

            var fallback = VisualFallbackCandidate();
            if (!string.Equals(fallback.UnitTypeId, _unitTypeId, StringComparison.Ordinal))
            {
                yield return fallback;
            }
        }

        private VisualModelCandidate VisualFallbackCandidate() => _simulationDomain switch
        {
            "Air" => new("usn_f-14a", "Default", "AirToAir", "aircraft"),
            "RotaryWing" => new("usn_sh-2f", "Squadron4", "ASW", "aircraft"),
            "Ground" or "LandAbstract" => new("usa_mbt_m60", "Default", "Default", "land_units"),
            "Installation" => new("nato_small_airbase", "Default", "Default", "land_units"),
            "Logistics" => new("usn_ao_t2", "Default", "Oil", "vessels"),
            _ => new("usn_ff_knox_84", "Variant43", "Default", "vessels"),
        };
    }
}
