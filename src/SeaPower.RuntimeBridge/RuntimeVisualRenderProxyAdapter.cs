using CrimeanTom.Protocol;

using MapControl;

using SeaPower;

using SeapowerUI;
using SeapowerUI.ViewModels;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeVisualRenderProxyAdapter
{
    private const int MaximumProxyCount = 4_096;
    private const string CampaignEntityPrefix = "unit:crimean-tom:";
    private const string CampaignContactPrefix = "contact:crimean-tom:";
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
            .Select(item => item.Value.Root!)
            .Distinct()
            .ToArray();
        pivot = matches.Length == 1 ? matches[0] : null!;
        return matches.Length == 1;
    }

    public static bool TryRequestPhysicalProxy(string requestedId, out string error)
    {
        var matches = Records
            .Where(item => string.Equals(item.Key, requestedId, StringComparison.Ordinal)
                || string.Equals(
                    $"campaign-unit:{ProxyCampaignTag(item.Value.CampaignId, item.Key)}",
                    requestedId,
                    StringComparison.Ordinal))
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
                pair.Value.Dispose();
                Records.Remove(pair.Key);
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
                "coalition-map-projections-visible-with-lazy-friendly-physical-proxies",
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
            return "A canonical campaign ID, bounded taskforce ID, and at most 4096 map projections are required.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in request.Proxies)
        {
            if (spec is null
                || !ValidProjectionId(spec.EntityId)
                || !ids.Add(spec.EntityId)
                || string.IsNullOrWhiteSpace(spec.Name)
                || spec.Name.Length > 128
                || spec.Relationship is not ("Friendly" or "HostileContact")
                || spec.Relationship == "Friendly"
                    && (string.IsNullOrWhiteSpace(spec.UnitTypeId)
                        || string.IsNullOrWhiteSpace(spec.VariantId)
                        || string.IsNullOrWhiteSpace(spec.LoadoutId))
                || spec.Relationship == "HostileContact"
                    && !spec.EntityId.StartsWith(CampaignContactPrefix, StringComparison.Ordinal)
                || spec.UncertaintyRadiusMeters is < 0 or > 10_000_000
                || !ValidPosition(spec.Latitude, spec.Longitude, spec.HeadingDegrees)
                || spec.CampaignTelegraph is < -1 or > 5)
            {
                return "Every map projection must have a unique canonical friendly/contact identity, "
                    + "valid relationship and presentation fields, finite position, and bounded uncertainty.";
            }
        }
        return null;
    }

    private static bool ValidProjectionId(string value)
    {
        if (value.StartsWith(CampaignEntityPrefix, StringComparison.Ordinal))
        {
            return Guid.TryParseExact(value[CampaignEntityPrefix.Length..], "N", out _);
        }
        if (!value.StartsWith(CampaignContactPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var track = value[CampaignContactPrefix.Length..];
        return track.Length is >= 16 and <= 64 && track.All(Uri.IsHexDigit);
    }

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
        private ObjectBaseParameters? _parameters;
        private GameObject? _root;
        private CampaignMapMarkerDrawing? _mapMarker;
        private TextDrawing? _mapLabel;
        private bool _disposed;
        private bool _selected;
        private bool _physicalProxyRequested;
        private string _physicalProxyError = string.Empty;
        private string _name = string.Empty;
        private string _relationship = string.Empty;
        private string _classification = string.Empty;
        private string _confidence = string.Empty;
        private int _uncertaintyRadiusMeters;
        private string _unitTypeId = string.Empty;
        private string _variantId = string.Empty;
        private string _loadoutId = string.Empty;
        private string _simulationDomain = string.Empty;
        private double _latitude;
        private double _longitude;
        private double _headingDegrees;
        private int _campaignTelegraph;
        private readonly int _proxyNumber;

        private VisualProxyRecord(string campaignId, int proxyNumber)
        {
            CampaignId = campaignId;
            _proxyNumber = proxyNumber;
        }

        public string CampaignId { get; }
        public GameObject? Root => _root;

        public static VisualProxyRecord Create(
            string campaignId,
            SurfaceRenderProxySpecPayload spec,
            int proxyNumber)
        {
            var record = new VisualProxyRecord(campaignId, proxyNumber);
            record.Apply(spec);
            return record;
        }

        public void Apply(SurfaceRenderProxySpecPayload spec)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VisualProxyRecord));
            }

            _name = spec.Name;
            _relationship = spec.Relationship;
            _classification = spec.Classification;
            _confidence = spec.Confidence;
            _uncertaintyRadiusMeters = spec.UncertaintyRadiusMeters;
            _unitTypeId = spec.UnitTypeId;
            _variantId = spec.VariantId;
            _loadoutId = spec.LoadoutId;
            _simulationDomain = spec.SimulationDomain;
            _latitude = spec.Latitude;
            _longitude = spec.Longitude;
            _headingDegrees = NormalizeHeading(spec.HeadingDegrees);
            _campaignTelegraph = spec.CampaignTelegraph;
            var position = PresentationPosition();
            if (_root is not null)
            {
                _root.SetActive(true);
                ApplyWorldTransform(position);
            }
            EnsureMapAnnotations(position);
        }

        public bool Suspend()
        {
            if (_disposed)
            {
                return false;
            }
            var changed = _root?.activeSelf == true || MapMarkerVisible;
            _root?.SetActive(false);
            RemoveMapAnnotations();
            return changed;
        }

        public SurfaceRenderProxyStatePayload Capture(string entityId) => new()
        {
            EntityId = entityId,
            StableUnitId = $"campaign-unit:{ProxyCampaignTag(CampaignId, entityId)}",
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
            PhysicalProxyError = _physicalProxyError,
            Relationship = _relationship,
            Classification = _classification,
            Confidence = _confidence,
            UncertaintyRadiusMeters = _uncertaintyRadiusMeters,
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
            if (_parameters is not null)
            {
                Cleanup(_parameters);
            }
            _parameters = null;
            _root = null;
        }

        internal bool WorldModelVisible => !_disposed && _root?.activeSelf == true;
        private bool MapMarkerVisible => !_disposed
            && _mapMarker is not null
            && Globals._mainGameViewModel?.Map?.DrawingLayer?.Graphics.Contains(_mapMarker) == true;

        private void EnsureMapAnnotations(GeoPosition position)
        {
            var layer = Globals._mainGameViewModel?.Map?.DrawingLayer;
            if (layer is null)
            {
                return;
            }

            _mapMarker ??= new CampaignMapMarkerDrawing(RequestPhysicalProxy)
            {
                ColorPreset = _relationship == "HostileContact"
                    ? DrawingColorPreset.Red
                    : DrawingColorPreset.Blue,
            };
            _mapMarker.ColorPreset = _relationship == "HostileContact"
                ? DrawingColorPreset.Red
                : DrawingColorPreset.Blue;
            _mapMarker.Location = position;
            if (!layer.Graphics.Contains(_mapMarker))
            {
                layer.Graphics.Add(_mapMarker);
            }

            if (!_selected && !WorldModelVisible)
            {
                if (_mapLabel is not null)
                {
                    layer.Graphics.Remove(_mapLabel);
                }
                return;
            }

            _mapLabel ??= new TextDrawing(_name)
            {
                ColorPreset = _relationship == "HostileContact"
                    ? DrawingColorPreset.Red
                    : DrawingColorPreset.Blue,
                FontSize = 11f,
                Locked = true,
            };
            _mapLabel.ColorPreset = _relationship == "HostileContact"
                ? DrawingColorPreset.Red
                : DrawingColorPreset.Blue;
            _mapLabel.Text = _name;
            _mapLabel.Location = position;
            if (!layer.Graphics.Contains(_mapLabel))
            {
                layer.Graphics.Add(_mapLabel);
            }
        }

        private void RequestPhysicalProxy()
        {
            RequestPhysicalProxy(out _);
        }

        internal bool RequestPhysicalProxy(out string error)
        {
            if (_disposed)
            {
                error = "campaign map projection is disposed";
                return false;
            }
            _selected = true;
            _physicalProxyRequested = string.Equals(
                _relationship,
                "Friendly",
                StringComparison.Ordinal);
            foreach (var other in Records.Values.Where(item => !ReferenceEquals(item, this)))
            {
                other.ClearSelectionAndHideWorldModel();
            }
            if (_physicalProxyRequested)
            {
                var preparation = RuntimeTacticalLocationAdapter.PrepareForLocalMapSelection(
                    _latitude,
                    _longitude);
                if (!preparation.Succeeded)
                {
                    _physicalProxyError = preparation.ErrorMessage;
                }
                else if (_simulationDomain is "Ground" or "Installation" or "LandAbstract"
                    && preparation.Payload?.Terrain.PlacementReady != true)
                {
                    _physicalProxyError =
                        "target terrain is still streaming; select the campaign unit again when terrain is ready";
                }
                else
                {
                    EnsureWorldModel();
                }
            }
            EnsureMapAnnotations(PresentationPosition());
            if (!_physicalProxyRequested)
            {
                error = "hostile contact projections do not expose exact physical world positions";
                return false;
            }
            if (!WorldModelVisible)
            {
                error = string.IsNullOrWhiteSpace(_physicalProxyError)
                    ? "campaign unit visual model could not be loaded"
                    : _physicalProxyError;
                return false;
            }

            error = string.Empty;
            return true;
        }

        private void ClearSelectionAndHideWorldModel()
        {
            _selected = false;
            if (_root is not null)
            {
                _root.SetActive(false);
            }
            var graphics = Globals._mainGameViewModel?.Map?.DrawingLayer?.Graphics;
            if (graphics is not null && _mapLabel is not null)
            {
                graphics.Remove(_mapLabel);
            }
        }

        private void EnsureWorldModel()
        {
            if (_root is not null)
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

            foreach (var folder in PreferredFolders(_simulationDomain))
            {
                var parameters = new ObjectBaseParameters
                {
                    _objectVariantReference = string.IsNullOrWhiteSpace(_variantId)
                        ? "Default"
                        : _variantId,
                    _loadoutVariant = string.IsNullOrWhiteSpace(_loadoutId)
                        ? "Default"
                        : _loadoutId,
                };
                try
                {
                    if (!ObjectBaseLoader.loadObjectFromInternalResources(
                        parameters,
                        _unitTypeId,
                        folder,
                        1_000_000 + _proxyNumber,
                        Singleton<SceneCreator>.Instance.GeoCenterPosition,
                        PresentationPosition(),
                        nationOverride: string.Empty)
                        || parameters._rootGameObject is null)
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
            if (_root is null)
            {
                return;
            }
            _root.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
            _root.transform.eulerAngles = new Vector3(0f, (float)_headingDegrees, 0f);
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
            return new GeoPosition(_latitude, _longitude, altitudeMeters);
        }

        private static IEnumerable<string> PreferredFolders(string simulationDomain) =>
            simulationDomain switch
            {
                "Air" or "RotaryWing" => ["aircraft", "vessels", "land_units"],
                "Ground" or "Installation" or "LandAbstract" => ["land_units", "vessels", "aircraft"],
                _ => ["vessels", "land_units", "aircraft"],
            };

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

        private sealed class CampaignMapMarkerDrawing(Action selected) : MarkerDrawing, IMapSelectable
        {
            private bool _selected;

            public bool Selected
            {
                get => _selected;
                set
                {
                    _selected = value;
                    if (value)
                    {
                        selected();
                    }
                }
            }
        }
    }
}
