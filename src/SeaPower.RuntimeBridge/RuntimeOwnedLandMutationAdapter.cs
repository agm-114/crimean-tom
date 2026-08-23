using System.Diagnostics;
using System.Text.Json;

using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeOwnedLandMutationAdapter
{
    private const double CoordinateToleranceDegrees = 0.000001d;
    private const double MinimumClearanceMeters = -0.05d;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly OwnedLandMaterializationProfilePayload[] Profiles =
    [
        Profile("usa_mbt_m60", "Ground"),
        Profile("usa_apc_m113", "Ground"),
        Profile("usa_aaa_vulcan", "Ground"),
        Profile("usa_mim-23_launcher", "Installation"),
        Profile("nato_ewr_station", "Installation"),
        Profile("nato_small_airbase", "Installation"),
    ];

    private static readonly Dictionary<string, OwnedLandRecord> OwnedEntities =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IdempotencyRecord> IdempotencyRecords =
        new(StringComparer.Ordinal);

    public static OwnedLandMaterializationProfilePayload[] SupportedProfiles() =>
        Profiles.Select(CloneProfile).ToArray();

    internal static bool HasOwnedEntity(string entityId) =>
        OwnedEntities.ContainsKey(entityId);

    public static MutationAdapterResult<MaterializeOwnedLandUnitResultPayload> Materialize(
        MaterializeOwnedLandUnitRequestPayload request,
        string commandSessionId)
    {
        var validation = Validate(request, commandSessionId);
        if (validation is not null)
        {
            return validation;
        }

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var idempotencyKey = string.Join(
            "|",
            request.Context.AssignmentId,
            request.Context.IdempotencyKey);
        if (IdempotencyRecords.TryGetValue(idempotencyKey, out var prior))
        {
            if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
            {
                return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                    ProtocolErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used with a different land mutation payload.");
            }
            var replay = Clone(prior.Result);
            replay.IdempotentReplay = true;
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Success(replay);
        }

        if (OwnedEntities.ContainsKey(request.EntityId)
            || RuntimeOwnedMutationAdapter.HasOwnedEntity(request.EntityId)
            || Singleton<ObjectsManager>.Instance._listOfAllObjects.Any(candidate =>
                candidate is not null
                && string.Equals(candidate._campaignTag, request.EntityId, StringComparison.Ordinal)))
        {
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                "The requested external entity already has a live or retained owned object.");
        }

        var terrain = RuntimeTerrainPlacementAdapter.Probe(new()
        {
            Latitude = request.Latitude,
            Longitude = request.Longitude,
        });
        if (!terrain.PlacementReady || terrain.TerrainHeightUnity is null)
        {
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"Land materialization requires finalized land terrain; probe={terrain.Postcondition}.");
        }

        var predicted = CapturePredicted(request, terrain);
        if (request.Context.DryRun)
        {
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Success(new()
            {
                Outcome = "validated",
                Postcondition = "owned-land-unit-materialization-valid",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = request.Context.ExpectedRevision,
                IdempotencyKey = request.Context.IdempotencyKey,
                DryRun = true,
                RealizedUnit = predicted,
                Terrain = terrain,
                UnsupportedFields = UnsupportedFields(),
            });
        }

        LandUnit? created = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var position = new GeoPosition(request.Latitude, request.Longitude, 0d);
            var heading = new Vector3(0f, (float)NormalizeHeading(request.HeadingDegrees), 0f);
            created = Singleton<ObjectsManager>.Instance.createLandUnit(
                request.VariantId,
                request.LoadoutId,
                request.UnitTypeId,
                Singleton<ObjectsManager>.Instance._listOfAllObjects.Count + 1,
                Singleton<SceneCreator>.Instance.GeoCenterPosition,
                position,
                request.Telegraph,
                heading,
                Globals._playerTaskforce);
            if (created is null)
            {
                return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    "Sea Power's supported land-unit constructor returned no unit.");
            }

            created._campaignTag = request.EntityId;
            created._geoPosition = position;
            var local = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
            var terrainHeight = (float)terrain.TerrainHeightUnity.Value;
            local.y = terrainHeight + created._groundOffset + 0.001f;
            created.transform.position = local;
            created.transform.eulerAngles = heading;
            created.DesiredAltitude.Value = local.y;
            created.setTelegraph(request.Telegraph);
            Physics.SyncTransforms();

            var realized = Capture(created, request, terrain);
            AssertReadback(request, realized);
            var revision = request.Context.ExpectedRevision + 1;
            var result = new MaterializeOwnedLandUnitResultPayload
            {
                Outcome = "materialized",
                Postcondition = "owned-land-unit-materialized-above-finalized-terrain-and-read-back",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = revision,
                IdempotencyKey = request.Context.IdempotencyKey,
                RecoverableOwnedObject = true,
                RealizedUnit = realized,
                Terrain = terrain,
                UnsupportedFields = UnsupportedFields(),
            };
            OwnedEntities.Add(
                request.EntityId,
                new OwnedLandRecord(
                    request.Context.AssignmentId,
                    request.Context.AuthorityEpoch,
                    revision,
                    request.TaskforceId,
                    created));
            stopwatch.Stop();
            result.SynchronousDurationMicroseconds = ElapsedMicroseconds(stopwatch);
            IdempotencyRecords.Add(idempotencyKey, new(requestJson, Clone(result)));
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            if (created is not null
                && string.Equals(created._campaignTag, request.EntityId, StringComparison.Ordinal))
            {
                if (!created.Disabled)
                {
                    created.Disable();
                }
                OwnedEntities[request.EntityId] = new(
                    request.Context.AssignmentId,
                    request.Context.AuthorityEpoch,
                    request.Context.ExpectedRevision,
                    request.TaskforceId,
                    created,
                    recoveryRequired: true);
            }
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "operation=materialize-land; recovery=recoverable-suspended-owned-object; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private static MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>? Validate(
        MaterializeOwnedLandUnitRequestPayload request,
        string commandSessionId)
    {
        if (!RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId))
        {
            return Invalid(ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        var context = request.Context;
        if (context is null
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return Invalid(ProtocolErrorCodes.InvalidSession,
                "The land mutation session does not match the executing pipe session.");
        }
        if (!request.EntityId.StartsWith("unit:crimean-tom:", StringComparison.Ordinal)
            || request.EntityId.Length > 128
            || !ValidScopedId(request.EntityId)
            || !context.AssignmentId.StartsWith("assignment:", StringComparison.Ordinal)
            || context.AssignmentId.Length > 128
            || !ValidScopedId(context.AssignmentId)
            || context.AuthorityEpoch < 1
            || context.ExpectedRevision != 0
            || string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || context.IdempotencyKey.Length > 128
            || !double.IsFinite(request.Latitude)
            || !double.IsFinite(request.Longitude)
            || !double.IsFinite(request.HeadingDegrees)
            || request.Latitude is < -90d or > 90d
            || request.Longitude is < -180d or >= 180d
            || request.Telegraph is < -1 or > 5)
        {
            return Invalid(ProtocolErrorCodes.MutationPreconditionFailed,
                "The owned land mutation identity, authority fence, or placement is invalid.");
        }
        if (!Profiles.Any(profile =>
                string.Equals(profile.UnitTypeId, request.UnitTypeId, StringComparison.Ordinal)
                && string.Equals(profile.VariantId, request.VariantId, StringComparison.Ordinal)
                && string.Equals(profile.LoadoutId, request.LoadoutId, StringComparison.Ordinal)))
        {
            return Invalid(ProtocolErrorCodes.MutationPreconditionFailed,
                "The requested land materialization profile is not verified by this runtime build.");
        }
        if (!string.Equals(
                request.TaskforceId,
                RuntimeOwnedMutationAdapter.GetPlayerTaskforceId(),
                StringComparison.Ordinal)
            || Globals._playerTaskforce is null)
        {
            return Invalid(ProtocolErrorCodes.MutationPreconditionFailed,
                "taskforceId must resolve exactly to the current player task force.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<ObjectsManager>.InstanceExists()
            || !Singleton<TaskforceManager>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone
            || !GameTime.IsPaused())
        {
            return Invalid(ProtocolErrorCodes.MutationPreconditionFailed,
                "A fully loaded, paused tactical mission is required.");
        }
        return null;
    }

    private static bool ValidScopedId(string value) => value.All(character =>
        char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');

    private static OwnedLandUnitStatePayload CapturePredicted(
        MaterializeOwnedLandUnitRequestPayload request,
        TerrainPlacementProbeResultPayload terrain) => new()
    {
        StableUnitId = $"campaign-unit:{request.EntityId}",
        EntityId = request.EntityId,
        UnitTypeId = request.UnitTypeId,
        VariantId = request.VariantId,
        LoadoutId = request.LoadoutId,
        TaskforceId = request.TaskforceId,
        Latitude = request.Latitude,
        Longitude = request.Longitude,
        HeadingDegrees = NormalizeHeading(request.HeadingDegrees),
        Telegraph = request.Telegraph,
        TerrainHeightMeters = terrain.TerrainHeightMeters ?? 0d,
        FlatteningFinishedForLandUnits = terrain.FlatteningFinishedForLandUnits,
    };

    private static OwnedLandUnitStatePayload Capture(
        LandUnit unit,
        MaterializeOwnedLandUnitRequestPayload request,
        TerrainPlacementProbeResultPayload terrain)
    {
        var terrainHeight = (float)(terrain.TerrainHeightUnity ?? 0d);
        return new OwnedLandUnitStatePayload
        {
            StableUnitId = $"campaign-unit:{request.EntityId}",
            GameUniqueId = unit.UniqueID,
            EntityId = request.EntityId,
            UnitTypeId = request.UnitTypeId,
            VariantId = request.VariantId,
            LoadoutId = request.LoadoutId,
            TaskforceId = request.TaskforceId,
            Latitude = unit._geoPosition._latitude,
            Longitude = unit._geoPosition._longitude,
            HeadingDegrees = NormalizeHeading(unit.transform.eulerAngles.y),
            Telegraph = unit.getTelegraph(),
            TerrainHeightMeters = terrain.TerrainHeightMeters ?? 0d,
            GroundClearanceMeters = Utils.unityHeightToMeter(
                unit.transform.position.y - terrainHeight - unit._groundOffset),
            FlatteningFinishedForLandUnits = terrain.FlatteningFinishedForLandUnits,
            IsDisabled = unit.Disabled,
            IsDestroyed = unit.IsDestroyed,
        };
    }

    private static void AssertReadback(
        MaterializeOwnedLandUnitRequestPayload request,
        OwnedLandUnitStatePayload realized)
    {
        if (!string.Equals(realized.StableUnitId, $"campaign-unit:{request.EntityId}", StringComparison.Ordinal)
            || Math.Abs(realized.Latitude - request.Latitude) > CoordinateToleranceDegrees
            || Math.Abs(realized.Longitude - request.Longitude) > CoordinateToleranceDegrees
            || realized.GroundClearanceMeters < MinimumClearanceMeters
            || realized.IsDisabled
            || realized.IsDestroyed)
        {
            throw new InvalidOperationException(
                "The owned land unit failed identity, coordinate, lifecycle, or terrain-clearance readback.");
        }
    }

    private static MutationAdapterResult<MaterializeOwnedLandUnitResultPayload> Invalid(
        string code,
        string message) =>
        MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(code, message);

    private static OwnedLandMaterializationProfilePayload Profile(string unitTypeId, string domain) => new()
    {
        UnitTypeId = unitTypeId,
        VariantId = "Default",
        LoadoutId = "Default",
        SimulationDomain = domain,
    };

    private static OwnedLandMaterializationProfilePayload CloneProfile(
        OwnedLandMaterializationProfilePayload profile) => new()
    {
        UnitTypeId = profile.UnitTypeId,
        VariantId = profile.VariantId,
        LoadoutId = profile.LoadoutId,
        SimulationDomain = profile.SimulationDomain,
    };

    private static string[] UnsupportedFields() =>
    [
        "fuel-fraction",
        "stores-fraction",
        "damage-fraction",
        "weapon-state",
        "sensor-state",
        "behaviour-tree-state",
    ];

    private static double NormalizeHeading(double value)
    {
        var normalized = value % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    private static long ElapsedMicroseconds(Stopwatch stopwatch) =>
        (long)Math.Round(
            stopwatch.ElapsedTicks * 1_000_000d / Stopwatch.Frequency,
            MidpointRounding.AwayFromZero);

    private static MaterializeOwnedLandUnitResultPayload Clone(
        MaterializeOwnedLandUnitResultPayload value) =>
        JsonSerializer.Deserialize<MaterializeOwnedLandUnitResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions)!;

    private sealed class OwnedLandRecord(
        string assignmentId,
        long authorityEpoch,
        long revision,
        string taskforceId,
        LandUnit landUnit,
        bool recoveryRequired = false)
    {
        public string AssignmentId { get; } = assignmentId;
        public long AuthorityEpoch { get; } = authorityEpoch;
        public long Revision { get; } = revision;
        public string TaskforceId { get; } = taskforceId;
        public LandUnit LandUnit { get; } = landUnit;
        public bool RecoveryRequired { get; } = recoveryRequired;
    }

    private sealed record IdempotencyRecord(
        string RequestJson,
        MaterializeOwnedLandUnitResultPayload Result);
}
