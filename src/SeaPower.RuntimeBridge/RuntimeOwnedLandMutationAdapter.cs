using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly Dictionary<string, LifecycleIdempotencyRecord> LifecycleIdempotencyRecords =
        new(StringComparer.Ordinal);

    public static OwnedLandMaterializationProfilePayload[] SupportedProfiles() =>
        Profiles.Select(CloneProfile).ToArray();

    internal static bool HasOwnedEntity(string entityId) =>
        OwnedEntities.ContainsKey(entityId);

    internal static bool Supports(string unitTypeId, string variantId, string loadoutId) =>
        Profiles.Any(profile =>
            string.Equals(profile.UnitTypeId, unitTypeId, StringComparison.Ordinal)
            && string.Equals(profile.VariantId, variantId, StringComparison.Ordinal)
            && string.Equals(profile.LoadoutId, loadoutId, StringComparison.Ordinal));

    public static MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload> MaterializeLegacy(
        MaterializeOwnedSurfaceUnitRequestPayload request,
        string commandSessionId)
    {
        if (request.SourceCheckpoint is not null)
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Initial land scheduling does not yet accept a source checkpoint.");
        }
        var result = Materialize(new MaterializeOwnedLandUnitRequestPayload
        {
            Context = request.Context,
            EntityId = request.EntityId,
            UnitTypeId = request.UnitTypeId,
            VariantId = request.VariantId,
            LoadoutId = request.LoadoutId,
            TaskforceId = request.TaskforceId,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            HeadingDegrees = request.HeadingDegrees,
            Telegraph = request.Telegraph,
        }, commandSessionId);
        if (!result.Succeeded)
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                result.ErrorCode,
                result.ErrorMessage,
                result.ErrorId);
        }
        var value = result.Payload!;
        return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Success(new()
        {
            Outcome = value.Outcome,
            Postcondition = value.Postcondition,
            AssignmentId = value.AssignmentId,
            AuthorityEpoch = value.AuthorityEpoch,
            Revision = value.Revision,
            IdempotencyKey = value.IdempotencyKey,
            DryRun = value.DryRun,
            IdempotentReplay = value.IdempotentReplay,
            RecoverableOwnedObject = value.RecoverableOwnedObject,
            SynchronousDurationMicroseconds = value.SynchronousDurationMicroseconds,
            RealizedUnit = value.RealizedUnit is null ? null : ToPortableState(value.RealizedUnit),
            UnsupportedFields = value.UnsupportedFields,
        });
    }

    public static OwnedSurfaceResourceStatePayload CaptureResourceState()
    {
        var entries = OwnedEntities
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var record = pair.Value;
                var landUnit = record.LandUnit;
                var lifecycle = landUnit == null
                    ? "detached-after-scene-loss"
                    : landUnit.Disabled
                        ? record.RecoveryRequired
                            ? "recovery-required-suspended"
                            : "suspended-retained"
                        : "active";
                return new OwnedSurfaceResourceEntryPayload
                {
                    EntityId = pair.Key,
                    AssignmentId = record.AssignmentId,
                    AuthorityEpoch = record.AuthorityEpoch,
                    Revision = record.Revision,
                    LifecycleState = lifecycle,
                    HasCheckpoint = record.Checkpoint is not null,
                    RecoveryRequired = record.RecoveryRequired,
                    GameUniqueId = landUnit == null ? null : landUnit.UniqueID,
                };
            })
            .ToArray();
        return new OwnedSurfaceResourceStatePayload
        {
            OwnedRecordCount = entries.Length,
            ActiveObjectCount = entries.Count(item => item.LifecycleState == "active"),
            SuspendedRetainedObjectCount = entries.Count(item =>
                item.LifecycleState is "suspended-retained" or "recovery-required-suspended"),
            DetachedObjectCount = entries.Count(item => item.LifecycleState == "detached-after-scene-loss"),
            DetachedCheckpointCount = entries.Count(item =>
                item.LifecycleState == "detached-after-scene-loss" && item.HasCheckpoint),
            RecoveryRequiredCount = entries.Count(item => item.RecoveryRequired),
            RetainedConstructedObjectCount = entries.Count(item =>
                item.LifecycleState is "active" or "suspended-retained" or "recovery-required-suspended"),
            MemoryReclamationSupported = false,
            SchedulerPolicy = "active-and-suspended-retained-objects-consume-materialized-capacity",
            Entities = entries,
        };
    }

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
                    request.UnitTypeId,
                    request.VariantId,
                    request.LoadoutId,
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
                    request.UnitTypeId,
                    request.VariantId,
                    request.LoadoutId,
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

    public static MutationAdapterResult<OwnedSurfaceCheckpointPayload> CaptureCheckpoint(
        OwnedSurfaceEntityRequestPayload request,
        string commandSessionId)
    {
        if (!TryResolve(
                request.Context,
                request.EntityId,
                commandSessionId,
                requirePaused: false,
                out var record,
                out var errorCode,
                out var errorMessage))
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                errorCode,
                errorMessage);
        }
        var landUnit = record!.LandUnit!;
        if (record.RecoveryRequired || landUnit.Disabled || landUnit.IsDestroyed)
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Only an active, healthy owned land unit can produce a checkpoint.");
        }
        var unit = CapturePortable(request.EntityId, record);
        var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hash = ComputeCheckpointSha256(record, unit);
        var checkpoint = new OwnedSurfaceCheckpointPayload
        {
            CheckpointId = $"checkpoint:{request.EntityId}:{record.Revision}:{hash[..12]}",
            CheckpointSha256 = hash,
            AssignmentId = record.AssignmentId,
            AuthorityEpoch = record.AuthorityEpoch,
            Revision = record.Revision,
            CapturedAtUnixMilliseconds = capturedAt,
            Unit = unit,
            UnsupportedFields = UnsupportedFields(),
        };
        record.Checkpoint = checkpoint;
        return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Success(checkpoint);
    }

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> Update(
        UpdateOwnedSurfaceUnitRequestPayload request,
        string commandSessionId)
    {
        if (TryLifecycleReplay(
                GameCommandTypes.UpdateOwnedSurfaceUnit,
                request.Context,
                request,
                commandSessionId,
                out var replay))
        {
            return replay!;
        }
        if (!TryResolve(
                request.Context,
                request.EntityId,
                commandSessionId,
                requirePaused: true,
                out var record,
                out var errorCode,
                out var errorMessage))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                errorCode,
                errorMessage);
        }
        if (!double.IsFinite(request.Latitude)
            || !double.IsFinite(request.Longitude)
            || !double.IsFinite(request.HeadingDegrees)
            || request.Latitude is < -90d or > 90d
            || request.Longitude is < -180d or >= 180d
            || request.Telegraph is < -1 or > 5)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The land update position, heading, or telegraph is invalid.");
        }
        var terrain = RuntimeTerrainPlacementAdapter.Probe(new()
        {
            Latitude = request.Latitude,
            Longitude = request.Longitude,
        });
        if (!terrain.PlacementReady || terrain.TerrainHeightUnity is null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"Land update requires finalized land terrain; probe={terrain.Postcondition}.");
        }
        var landUnit = record!.LandUnit!;
        if (landUnit.Disabled || landUnit.IsDestroyed || record.RecoveryRequired)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A healthy active land unit is required before applying an update.");
        }
        var original = CapturePortable(request.EntityId, record);
        var timer = Stopwatch.StartNew();
        try
        {
            ApplyPlacement(
                landUnit,
                request.Latitude,
                request.Longitude,
                request.HeadingDegrees,
                request.Telegraph,
                terrain);
            var realized = CapturePortable(request.EntityId, record);
            if (Math.Abs(realized.Latitude!.Value - request.Latitude) > CoordinateToleranceDegrees
                || Math.Abs(realized.Longitude!.Value - request.Longitude) > CoordinateToleranceDegrees
                || realized.IsDisabled
                || realized.IsDestroyed)
            {
                throw new InvalidOperationException("The land update failed native readback.");
            }
            record.Revision++;
            timer.Stop();
            var result = LifecycleResult(
                "updated",
                "owned-land-unit-updated-above-finalized-terrain-and-read-back",
                request.Context,
                record,
                request.EntityId,
                timer);
            StoreLifecycleReplay(
                GameCommandTypes.UpdateOwnedSurfaceUnit,
                request.Context,
                request,
                result);
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            try
            {
                ApplyPortableState(landUnit, original);
            }
            catch
            {
                if (!landUnit.Disabled)
                {
                    landUnit.Disable();
                }
                record.RecoveryRequired = true;
            }
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"operation=update-land; failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> SetSuspension(
        SetOwnedSurfaceSuspensionRequestPayload request,
        string commandSessionId)
    {
        if (TryLifecycleReplay(
                GameCommandTypes.SetOwnedSurfaceSuspension,
                request.Context,
                request,
                commandSessionId,
                out var replay))
        {
            return replay!;
        }
        if (!TryResolve(
                request.Context,
                request.EntityId,
                commandSessionId,
                requirePaused: true,
                out var record,
                out var errorCode,
                out var errorMessage))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                errorCode,
                errorMessage);
        }
        var landUnit = record!.LandUnit!;
        if (record.RecoveryRequired && !request.IsSuspended)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A recovery-required land unit cannot be enabled.");
        }
        if (landUnit.Disabled == request.IsSuspended)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                request.IsSuspended
                    ? "The owned land unit is already suspended."
                    : "The owned land unit is already enabled.");
        }
        var timer = Stopwatch.StartNew();
        try
        {
            if (request.IsSuspended)
            {
                landUnit.Disable();
            }
            else
            {
                landUnit.Enable();
            }
            if (landUnit.Disabled != request.IsSuspended || landUnit.IsDestroyed)
            {
                throw new InvalidOperationException(
                    "Sea Power did not realize the requested land suspension state.");
            }
            record.Revision++;
            timer.Stop();
            var result = LifecycleResult(
                request.IsSuspended ? "suspended" : "enabled",
                request.IsSuspended
                    ? "owned-land-unit-suspended-outside-active-world"
                    : "owned-land-unit-enabled-and-read-back",
                request.Context,
                record,
                request.EntityId,
                timer);
            StoreLifecycleReplay(
                GameCommandTypes.SetOwnedSurfaceSuspension,
                request.Context,
                request,
                result);
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            if (!landUnit.Disabled)
            {
                landUnit.Disable();
            }
            record.RecoveryRequired = true;
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"operation=suspend-land; recovery=recoverable-suspended-owned-object; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    private static bool TryResolve(
        OwnedMutationContextPayload? context,
        string entityId,
        string commandSessionId,
        bool requirePaused,
        out OwnedLandRecord? record,
        out string errorCode,
        out string errorMessage)
    {
        record = null;
        errorCode = ProtocolErrorCodes.MutationPreconditionFailed;
        errorMessage = string.Empty;
        if (!RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId))
        {
            errorCode = ProtocolErrorCodes.MutationDisabled;
            errorMessage = "Owned mutations are not armed for the current pipe session.";
            return false;
        }
        if (context is null
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            errorCode = ProtocolErrorCodes.InvalidSession;
            errorMessage = "The land lifecycle session does not match the executing pipe session.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || context.IdempotencyKey.Length > 128
            || !OwnedEntities.TryGetValue(entityId, out record))
        {
            errorMessage = "No owned land record matches the requested entity and lifecycle fence.";
            return false;
        }
        if (!string.Equals(record.AssignmentId, context.AssignmentId, StringComparison.Ordinal))
        {
            errorMessage = "The mutation assignment does not own this land record.";
            return false;
        }
        if (record.AuthorityEpoch != context.AuthorityEpoch)
        {
            errorCode = ProtocolErrorCodes.StaleAuthorityEpoch;
            errorMessage = $"Expected authority epoch {record.AuthorityEpoch}, received {context.AuthorityEpoch}.";
            return false;
        }
        if (record.Revision != context.ExpectedRevision)
        {
            errorCode = ProtocolErrorCodes.StaleRevision;
            errorMessage = $"Expected revision {record.Revision}, received {context.ExpectedRevision}.";
            return false;
        }
        if (record.LandUnit == null
            || !string.Equals(record.LandUnit._campaignTag, entityId, StringComparison.Ordinal))
        {
            errorMessage = "The owned land object is absent or no longer bears its exact ownership tag.";
            return false;
        }
        if (requirePaused
            && (!Singleton<SceneCreator>.InstanceExists()
                || !Singleton<SceneCreator>.Instance.IsLoadingDone
                || !GameTime.IsPaused()))
        {
            errorMessage = "A fully loaded, paused tactical mission is required.";
            return false;
        }
        return true;
    }

    private static OwnedSurfaceUnitStatePayload CapturePortable(
        string entityId,
        OwnedLandRecord record)
    {
        var landUnit = record.LandUnit!;
        return new OwnedSurfaceUnitStatePayload
        {
            StableUnitId = $"campaign-unit:{entityId}",
            GameUniqueId = landUnit.UniqueID,
            EntityId = entityId,
            UnitTypeId = record.UnitTypeId,
            VariantId = record.VariantId,
            LoadoutId = record.LoadoutId,
            TaskforceId = record.TaskforceId,
            Latitude = landUnit._geoPosition._latitude,
            Longitude = landUnit._geoPosition._longitude,
            HeadingDegrees = NormalizeHeading(landUnit.transform.eulerAngles.y),
            SpeedKnots = landUnit._velocityInKnots,
            Telegraph = landUnit.getTelegraph(),
            IsDisabled = landUnit.Disabled,
            IsDestroyed = landUnit.IsDestroyed,
        };
    }

    private static OwnedSurfaceUnitStatePayload ToPortableState(OwnedLandUnitStatePayload value) => new()
    {
        StableUnitId = value.StableUnitId,
        GameUniqueId = value.GameUniqueId,
        EntityId = value.EntityId,
        UnitTypeId = value.UnitTypeId,
        VariantId = value.VariantId,
        LoadoutId = value.LoadoutId,
        TaskforceId = value.TaskforceId,
        Latitude = value.Latitude,
        Longitude = value.Longitude,
        HeadingDegrees = value.HeadingDegrees,
        SpeedKnots = 0d,
        Telegraph = value.Telegraph,
        IsDisabled = value.IsDisabled,
        IsDestroyed = value.IsDestroyed,
    };

    private static void ApplyPlacement(
        LandUnit landUnit,
        double latitude,
        double longitude,
        double headingDegrees,
        int telegraph,
        TerrainPlacementProbeResultPayload terrain)
    {
        var position = new GeoPosition(latitude, longitude, 0d);
        var local = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
        local.y = (float)terrain.TerrainHeightUnity! + landUnit._groundOffset + 0.001f;
        landUnit._geoPosition = position;
        landUnit.transform.position = local;
        landUnit.transform.eulerAngles = new Vector3(
            0f,
            (float)NormalizeHeading(headingDegrees),
            0f);
        landUnit.DesiredAltitude.Value = local.y;
        landUnit.setTelegraph(telegraph);
        Physics.SyncTransforms();
    }

    private static void ApplyPortableState(LandUnit landUnit, OwnedSurfaceUnitStatePayload state)
    {
        if (state.Latitude is not double latitude
            || state.Longitude is not double longitude
            || state.HeadingDegrees is not double heading)
        {
            throw new InvalidOperationException("The portable land state has no complete placement.");
        }
        var terrain = RuntimeTerrainPlacementAdapter.Probe(new()
        {
            Latitude = latitude,
            Longitude = longitude,
        });
        if (!terrain.PlacementReady || terrain.TerrainHeightUnity is null)
        {
            throw new InvalidOperationException(
                $"Portable land placement is unavailable: {terrain.Postcondition}.");
        }
        ApplyPlacement(landUnit, latitude, longitude, heading, state.Telegraph, terrain);
        if (landUnit.Disabled != state.IsDisabled)
        {
            if (state.IsDisabled)
            {
                landUnit.Disable();
            }
            else
            {
                landUnit.Enable();
            }
        }
    }

    private static string ComputeCheckpointSha256(
        OwnedLandRecord record,
        OwnedSurfaceUnitStatePayload unit)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            record.AssignmentId,
            record.AuthorityEpoch,
            record.Revision,
            Unit = unit,
        }, JsonOptions);
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return string.Concat(bytes.Select(value => value.ToString("x2")));
    }

    private static OwnedSurfaceLifecycleResultPayload LifecycleResult(
        string outcome,
        string postcondition,
        OwnedMutationContextPayload context,
        OwnedLandRecord record,
        string entityId,
        Stopwatch timer) => new()
    {
        Outcome = outcome,
        Postcondition = postcondition,
        AssignmentId = record.AssignmentId,
        AuthorityEpoch = record.AuthorityEpoch,
        Revision = record.Revision,
        IdempotencyKey = context.IdempotencyKey,
        SynchronousDurationMicroseconds = ElapsedMicroseconds(timer),
        Unit = CapturePortable(entityId, record),
    };

    private static bool TryLifecycleReplay<TRequest>(
        string command,
        OwnedMutationContextPayload? context,
        TRequest request,
        string commandSessionId,
        out MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>? replay)
    {
        replay = null;
        if (context is null
            || !RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId)
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            return false;
        }
        var key = LifecycleKey(command, context);
        if (!LifecycleIdempotencyRecords.TryGetValue(key, out var prior))
        {
            return false;
        }
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
        {
            replay = MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.IdempotencyConflict,
                "The lifecycle idempotency key was already used with a different land payload.");
            return true;
        }
        var value = CloneLifecycle(prior.Result);
        value.IdempotentReplay = true;
        replay = MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(value);
        return true;
    }

    private static void StoreLifecycleReplay<TRequest>(
        string command,
        OwnedMutationContextPayload context,
        TRequest request,
        OwnedSurfaceLifecycleResultPayload result) =>
        LifecycleIdempotencyRecords.Add(
            LifecycleKey(command, context),
            new(
                JsonSerializer.Serialize(request, JsonOptions),
                CloneLifecycle(result)));

    private static string LifecycleKey(string command, OwnedMutationContextPayload context) =>
        string.Join("|", command, context.AssignmentId, context.IdempotencyKey);

    private static OwnedSurfaceLifecycleResultPayload CloneLifecycle(
        OwnedSurfaceLifecycleResultPayload value) =>
        JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions)!;

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
        string unitTypeId,
        string variantId,
        string loadoutId,
        LandUnit landUnit,
        bool recoveryRequired = false)
    {
        public string AssignmentId { get; set; } = assignmentId;
        public long AuthorityEpoch { get; set; } = authorityEpoch;
        public long Revision { get; set; } = revision;
        public string TaskforceId { get; set; } = taskforceId;
        public string UnitTypeId { get; } = unitTypeId;
        public string VariantId { get; } = variantId;
        public string LoadoutId { get; } = loadoutId;
        public LandUnit? LandUnit { get; set; } = landUnit;
        public OwnedSurfaceCheckpointPayload? Checkpoint { get; set; }
        public bool RecoveryRequired { get; set; } = recoveryRequired;
    }

    private sealed record IdempotencyRecord(
        string RequestJson,
        MaterializeOwnedLandUnitResultPayload Result);

    private sealed record LifecycleIdempotencyRecord(
        string RequestJson,
        OwnedSurfaceLifecycleResultPayload Result);
}
