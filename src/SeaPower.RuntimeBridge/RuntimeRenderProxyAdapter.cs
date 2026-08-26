using System.Reflection;

using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeRenderProxyAdapter
{
    private const int MaximumProxyCount = 512;
    private const string CampaignEntityPrefix = "unit:crimean-tom:";
    private static readonly Dictionary<string, RenderProxyRecord> Records =
        new(StringComparer.Ordinal);
    private static readonly MethodInfo? DisableEntityMethod = typeof(ObjectBase).GetMethod(
        "DisableEntity",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public static SurfaceRenderProxySnapshotPayload CaptureState() => new()
    {
        RepresentationProvider = "fenced-native-vessel-fallback",
        CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Proxies = Records
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => Capture(item.Key, item.Value))
            .ToArray(),
    };

    public static void SuppressForOwnedEntity(string entityId)
    {
        if (Records.TryGetValue(entityId, out var record))
        {
            Suspend(record);
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

        var requested = request.Proxies.ToDictionary(item => item.EntityId, StringComparer.Ordinal);
        var createdCount = 0;
        var updatedCount = 0;
        var suspendedCount = 0;
        var excludedOwnedEntities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in Records.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray())
        {
            if (!string.Equals(pair.Value.CampaignId, request.CampaignId, StringComparison.Ordinal))
            {
                if (Suspend(pair.Value))
                {
                    suspendedCount++;
                }
                Records.Remove(pair.Key);
                continue;
            }

            var conflictsWithOwnedEntity = RuntimeOwnedMutationAdapter.HasActiveOwnedEntity(pair.Key);
            if (!requested.ContainsKey(pair.Key) || conflictsWithOwnedEntity)
            {
                if (Suspend(pair.Value))
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

            if (Records.TryGetValue(spec.EntityId, out var retained)
                && retained.Vessel != null)
            {
                if (retained.Vessel.Disabled)
                {
                    retained.Vessel.Enable();
                }
                ApplyPresentationState(retained.Vessel, spec);
                retained.CampaignTelegraph = spec.CampaignTelegraph;
                updatedCount++;
                continue;
            }

            Vessel? created = null;
            try
            {
                var position = new GeoPosition(spec.Latitude, spec.Longitude, 0d);
                created = Singleton<ObjectsManager>.Instance.createShip(
                    spec.VariantId,
                    spec.LoadoutId,
                    spec.UnitTypeId,
                    Singleton<ObjectsManager>.Instance._listOfAllObjects.Count + 1,
                    Singleton<SceneCreator>.Instance.GeoCenterPosition,
                    position,
                    0,
                    new Vector3(0f, (float)NormalizeHeading(spec.HeadingDegrees), 0f),
                    taskforce,
                    loadAsync: false);
                if (created == null)
                {
                    return Failure(
                        ProtocolErrorCodes.MutationPreconditionFailed,
                        $"Sea Power could not construct render proxy '{spec.EntityId}'.");
                }

                created._campaignTag = ProxyCampaignTag(request.CampaignId, spec.EntityId);
                ApplyPresentationState(created, spec);
                Records[spec.EntityId] = new RenderProxyRecord(
                    request.CampaignId,
                    created,
                    spec.CampaignTelegraph);
                createdCount++;
            }
            catch (Exception exception)
            {
                if (created != null && !created.Disabled)
                {
                    created.Disable();
                }
                return Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    $"Render proxy '{spec.EntityId}' could not be materialized: "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        var states = Records
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => Capture(item.Key, item.Value))
            .ToArray();
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
            Proxies = states,
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
                || !RuntimeOwnedMutationAdapter.SupportsSurfaceRenderProxy(
                    spec.UnitTypeId,
                    spec.VariantId,
                    spec.LoadoutId)
                || !ValidPosition(spec.Latitude, spec.Longitude, spec.HeadingDegrees)
                || spec.CampaignTelegraph is < -1 or > 5)
            {
                return "Every proxy must have a unique canonical campaign entity, supported "
                    + "surface profile, finite position, name, and valid campaign telegraph.";
            }
        }
        return null;
    }

    private static void ApplyPresentationState(Vessel vessel, SurfaceRenderProxySpecPayload spec)
    {
        var position = new GeoPosition(spec.Latitude, spec.Longitude, 0d);
        var heading = (float)NormalizeHeading(spec.HeadingDegrees);
        vessel.setName(spec.Name);
        vessel.IsControllable = false;
        vessel._hasControl = false;
        vessel._canUpdate = false;
        vessel.setTelegraph(0);
        vessel._velocityInKnots = 0f;
        vessel._velocityInUnity = 0f;
        vessel._geoPosition = position;
        vessel.Position.Value = position;
        vessel.Heading.Value = heading;
        vessel.Velocity.Value = 0f;
        vessel.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
        vessel.transform.eulerAngles = new Vector3(0f, heading, 0f);
        vessel.DisableAirSearchRadars();
        vessel.DisableSurfaceSearchRadars();
        vessel.DisableActiveSonars(voiceover: false);

        foreach (var collider in vessel.getRootGameObject()
            .GetComponentsInChildren<Collider>(includeInactive: true))
        {
            collider.enabled = false;
        }
        foreach (var body in vessel.getRootGameObject()
            .GetComponentsInChildren<Rigidbody>(includeInactive: true))
        {
            body.detectCollisions = false;
            body.isKinematic = true;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        // Sea Power's private DisableEntity method removes only the DOTS simulation
        // reference. Unlike ObjectBase.Disable(), it leaves the native model and map
        // registration visible. _canUpdate=false independently fences the managed path.
        if (DisableEntityMethod is null)
        {
            throw new MissingMethodException(
                typeof(ObjectBase).FullName,
                "DisableEntity");
        }
        DisableEntityMethod.Invoke(vessel, null);
        Physics.SyncTransforms();
    }

    private static bool Suspend(RenderProxyRecord record)
    {
        var vessel = record.Vessel;
        if (vessel == null || vessel.Disabled)
        {
            return false;
        }
        vessel.Disable();
        return true;
    }

    private static SurfaceRenderProxyStatePayload Capture(
        string entityId,
        RenderProxyRecord record)
    {
        var vessel = record.Vessel;
        var hasVessel = vessel != null;
        return new SurfaceRenderProxyStatePayload
        {
            EntityId = entityId,
            StableUnitId = $"campaign-unit:{ProxyCampaignTag(record.CampaignId, entityId)}",
            GameUniqueId = hasVessel ? vessel!.UniqueID : null,
            IsVisible = hasVessel && !vessel!.Disabled,
            IsSimulated = false,
            RepresentationKind = "fenced-native-vessel-fallback",
            HasNativeSimulationObject = hasVessel,
            WorldModelVisible = hasVessel && !vessel!.Disabled,
            MapMarkerVisible = hasVessel && !vessel!.Disabled,
            Latitude = hasVessel ? vessel!._geoPosition._latitude : 0d,
            Longitude = hasVessel ? vessel!._geoPosition._longitude : 0d,
            HeadingDegrees = hasVessel ? vessel!.getHeading() : 0d,
            CampaignTelegraph = record.CampaignTelegraph,
        };
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

    private sealed class RenderProxyRecord(
        string campaignId,
        Vessel vessel,
        int campaignTelegraph)
    {
        public string CampaignId { get; } = campaignId;
        public Vessel? Vessel { get; } = vessel;
        public int CampaignTelegraph { get; set; } = campaignTelegraph;
    }
}
