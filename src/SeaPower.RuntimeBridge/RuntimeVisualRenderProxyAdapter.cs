using CrimeanTom.Protocol;

using MapControl;

using SeaPower;

using SeapowerUI.ViewModels;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeVisualRenderProxyAdapter
{
    private const int MaximumProxyCount = 512;
    private const string CampaignEntityPrefix = "unit:crimean-tom:";
    private static readonly Dictionary<string, VisualProxyRecord> Records =
        new(StringComparer.Ordinal);

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
            return ReconcileValidated(request);
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
    }

    public static SurfaceRenderProxySnapshotPayload Capture() => new()
    {
        RepresentationProvider = "visual-only-preferred",
        CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Proxies = Records
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value.Capture(item.Key))
            .ToArray(),
    };

    public static bool TryResolveVisiblePivot(string requestedId, out GameObject pivot)
    {
        var matches = Records
            .Where(item => item.Value.WorldModelVisible
                && (string.Equals(item.Key, requestedId, StringComparison.Ordinal)
                    || string.Equals(
                        $"campaign-unit:{ProxyCampaignTag(item.Value.CampaignId, item.Key)}",
                        requestedId,
                        StringComparison.Ordinal)))
            .Select(item => item.Value.Root)
            .Distinct()
            .ToArray();
        pivot = matches.Length == 1 ? matches[0] : null!;
        return matches.Length == 1;
    }

    private static MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload>
        ReconcileValidated(ReconcileSurfaceRenderProxiesRequestPayload request)
    {
        var requested = request.Proxies.ToDictionary(item => item.EntityId, StringComparer.Ordinal);
        var createdCount = 0;
        var updatedCount = 0;
        var suspendedCount = 0;
        var excludedOwnedEntities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in Records.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray())
        {
            if (!string.Equals(pair.Value.CampaignId, request.CampaignId, StringComparison.Ordinal))
            {
                if (pair.Value.Suspend())
                {
                    suspendedCount++;
                }
                pair.Value.Dispose();
                Records.Remove(pair.Key);
                continue;
            }

            var conflictsWithOwnedEntity = RuntimeOwnedMutationAdapter.HasActiveOwnedEntity(pair.Key);
            if (!requested.ContainsKey(pair.Key) || conflictsWithOwnedEntity)
            {
                if (pair.Value.Suspend())
                {
                    suspendedCount++;
                }
                if (conflictsWithOwnedEntity)
                {
                    excludedOwnedEntities.Add(pair.Key);
                }
            }
        }

        foreach (var spec in request.Proxies.OrderBy(item => item.EntityId, StringComparer.Ordinal))
        {
            if (RuntimeOwnedMutationAdapter.HasActiveOwnedEntity(spec.EntityId))
            {
                excludedOwnedEntities.Add(spec.EntityId);
                continue;
            }

            if (!Records.TryGetValue(spec.EntityId, out var record))
            {
                record = VisualProxyRecord.Create(request.CampaignId, spec, Records.Count + 1);
                Records[spec.EntityId] = record;
                createdCount++;
            }
            else
            {
                updatedCount++;
            }
            record.Apply(spec);
        }

        return MutationAdapterResult<ReconcileSurfaceRenderProxiesResultPayload>.Success(new()
        {
            Outcome = "reconciled",
            Postcondition =
                "friendly-campaign-render-proxies-visible-without-simulation-authority",
            CampaignId = request.CampaignId,
            RequestedCount = request.Proxies.Length,
            CreatedCount = createdCount,
            UpdatedCount = updatedCount,
            SuspendedCount = suspendedCount,
            ExcludedOwnedEntityCount = excludedOwnedEntities.Count,
            Proxies = Records
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Value.Capture(item.Key))
                .ToArray(),
        });
    }

    private static string? Validate(ReconcileSurfaceRenderProxiesRequestPayload? request)
    {
        if (request is null
            || !Guid.TryParseExact(request.CampaignId, "N", out _)
            || string.IsNullOrWhiteSpace(request.TaskforceId)
            || request.TaskforceId.Length > 128
            || request.Proxies is null
            || request.Proxies.Length > MaximumProxyCount)
        {
            return "A canonical campaign ID, bounded taskforce ID, and at most 512 proxies are required.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in request.Proxies)
        {
            if (spec is null
                || !ValidCampaignEntityId(spec.EntityId)
                || !ids.Add(spec.EntityId)
                || string.IsNullOrWhiteSpace(spec.Name)
                || spec.Name.Length > 128
                || !string.Equals(
                    spec.UnitTypeId,
                    RuntimeOwnedMutationAdapter.SupportedUnitType,
                    StringComparison.Ordinal)
                || !string.Equals(
                    spec.VariantId,
                    RuntimeOwnedMutationAdapter.SupportedVariant,
                    StringComparison.Ordinal)
                || !string.Equals(
                    spec.LoadoutId,
                    RuntimeOwnedMutationAdapter.SupportedLoadout,
                    StringComparison.Ordinal)
                || !ValidPosition(spec.Latitude, spec.Longitude, spec.HeadingDegrees)
                || spec.CampaignTelegraph is < -1 or > 5)
            {
                return "Every proxy must have a unique canonical campaign entity, supported "
                    + "surface profile, finite position, name, and valid campaign telegraph.";
            }
        }
        return null;
    }

    private static bool ValidCampaignEntityId(string value) =>
        value.StartsWith(CampaignEntityPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(value[CampaignEntityPrefix.Length..], "N", out _);

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

    private static string ProxyCampaignTag(string campaignId, string entityId) =>
        $"render-proxy:{campaignId}:{entityId}";

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

    private sealed class VisualProxyRecord : IDisposable
    {
        private readonly ObjectBaseParameters _parameters;
        private readonly GameObject _root;
        private MarkerDrawing? _mapMarker;
        private TextDrawing? _mapLabel;
        private bool _disposed;
        private double _latitude;
        private double _longitude;
        private double _headingDegrees;
        private int _campaignTelegraph;

        private VisualProxyRecord(
            string campaignId,
            ObjectBaseParameters parameters,
            GameObject root)
        {
            CampaignId = campaignId;
            _parameters = parameters;
            _root = root;
        }

        public string CampaignId { get; }
        public GameObject Root => _root;

        public static VisualProxyRecord Create(
            string campaignId,
            SurfaceRenderProxySpecPayload spec,
            int proxyNumber)
        {
            var parameters = new ObjectBaseParameters
            {
                _objectVariantReference = spec.VariantId,
                _loadoutVariant = spec.LoadoutId,
            };
            var position = new GeoPosition(spec.Latitude, spec.Longitude, 0d);
            if (!ObjectBaseLoader.loadObjectFromInternalResources(
                parameters,
                spec.UnitTypeId,
                "vessels",
                1_000_000 + proxyNumber,
                Singleton<SceneCreator>.Instance.GeoCenterPosition,
                position,
                nationOverride: string.Empty)
                || parameters._rootGameObject is null)
            {
                Cleanup(parameters);
                throw new InvalidOperationException(
                    "Sea Power's model-only object loader returned no presentation root.");
            }

            parameters._rootGameObject.name = $"CrimeanTom render proxy - {spec.Name}";
            Sanitize(parameters);
            return new VisualProxyRecord(campaignId, parameters, parameters._rootGameObject);
        }

        public void Apply(SurfaceRenderProxySpecPayload spec)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VisualProxyRecord));
            }

            var position = new GeoPosition(spec.Latitude, spec.Longitude, 0d);
            _root.SetActive(true);
            _root.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
            _root.transform.eulerAngles = new Vector3(
                0f,
                (float)NormalizeHeading(spec.HeadingDegrees),
                0f);
            EnsureMapAnnotations(position, spec.Name);
            _latitude = spec.Latitude;
            _longitude = spec.Longitude;
            _headingDegrees = NormalizeHeading(spec.HeadingDegrees);
            _campaignTelegraph = spec.CampaignTelegraph;
        }

        public bool Suspend()
        {
            if (_disposed || !_root.activeSelf)
            {
                return false;
            }
            _root.SetActive(false);
            RemoveMapAnnotations();
            return true;
        }

        public SurfaceRenderProxyStatePayload Capture(string entityId) => new()
        {
            EntityId = entityId,
            StableUnitId = $"campaign-unit:{ProxyCampaignTag(CampaignId, entityId)}",
            GameUniqueId = null,
            IsVisible = WorldModelVisible || MapMarkerVisible,
            IsSimulated = false,
            RepresentationKind = "visual-model-and-map-annotation",
            HasNativeSimulationObject = false,
            WorldModelVisible = WorldModelVisible,
            MapMarkerVisible = MapMarkerVisible,
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
            Cleanup(_parameters);
        }

        internal bool WorldModelVisible => !_disposed && _root.activeSelf;
        private bool MapMarkerVisible => !_disposed
            && _mapMarker is not null
            && Globals._mainGameViewModel?.Map?.DrawingLayer?.Graphics.Contains(_mapMarker) == true;

        private void EnsureMapAnnotations(GeoPosition position, string name)
        {
            var layer = Globals._mainGameViewModel?.Map?.DrawingLayer;
            if (layer is null)
            {
                return;
            }

            _mapMarker ??= new MarkerDrawing
            {
                ColorPreset = DrawingColorPreset.Blue,
            };
            _mapMarker.Location = position;
            if (!layer.Graphics.Contains(_mapMarker))
            {
                layer.Graphics.Add(_mapMarker);
            }

            _mapLabel ??= new TextDrawing(name)
            {
                ColorPreset = DrawingColorPreset.Blue,
                FontSize = 11f,
                Locked = true,
            };
            _mapLabel.Text = name;
            _mapLabel.Location = position;
            if (!layer.Graphics.Contains(_mapLabel))
            {
                layer.Graphics.Add(_mapLabel);
            }
        }

        private void RemoveMapAnnotations()
        {
            var graphics = Globals._mainGameViewModel?.Map?.DrawingLayer?.Graphics;
            if (graphics is null)
            {
                return;
            }
            if (_mapMarker is not null)
            {
                graphics.Remove(_mapMarker);
            }
            if (_mapLabel is not null)
            {
                graphics.Remove(_mapLabel);
            }
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
            parameters._damageObjectParent?.SetActive(false);
            parameters._collidersObject?.SetActive(false);
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
            if (parameters._rootGameObject is not null)
            {
                UnityEngine.Object.Destroy(parameters._rootGameObject);
            }
            foreach (var material in parameters._materialsDictionary?.Values.Distinct()
                ?? Enumerable.Empty<Material>())
            {
                if (material is not null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }
        }
    }
}
