using System.Diagnostics;
using System.Text.Json;

using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeOwnedMutationAdapter
{
    public const string Confirmation = "enable-owned-mutations";

    private const string SupportedUnitType = "usn_ff_knox_84";
    private const string SupportedVariant = "Variant13";
    private const string SupportedLoadout = "Default";
    private const int MaximumArmDurationSeconds = 600;
    private const double PositionToleranceDegrees = 0.000000001d;
    private const double HeadingToleranceDegrees = 0.001d;
    private const double SpeedToleranceKnots = 0.001d;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly Dictionary<string, OwnedEntityRecord> OwnedEntities =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IdempotencyRecord> IdempotencyRecords =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LifecycleIdempotencyRecord>
        LifecycleIdempotencyRecords = new(StringComparer.Ordinal);

    private static string _armedSessionId = string.Empty;
    private static long _expiresAtUnixMilliseconds;

    public static bool IsArmed => !string.IsNullOrEmpty(_armedSessionId)
        && _expiresAtUnixMilliseconds > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long? ExpiresAtUnixMilliseconds => IsArmed
        ? _expiresAtUnixMilliseconds
        : null;

    public static bool IsArmedFor(string? sessionId) => IsArmed
        && string.Equals(_armedSessionId, sessionId, StringComparison.Ordinal);

    public static MutationAdapterResult<ArmOwnedMutationModeResultPayload> Arm(
        ArmOwnedMutationModeRequestPayload request,
        string commandSessionId,
        string currentRuntimeFingerprint)
    {
        if (!string.Equals(request.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<ArmOwnedMutationModeResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The arming request session does not match the executing pipe session.");
        }
        if (!string.Equals(request.Confirmation, Confirmation, StringComparison.Ordinal))
        {
            return MutationAdapterResult<ArmOwnedMutationModeResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"confirmation must be exactly '{Confirmation}'.");
        }
        if (request.DurationSeconds is < 1 or > MaximumArmDurationSeconds)
        {
            return MutationAdapterResult<ArmOwnedMutationModeResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"durationSeconds must be between 1 and {MaximumArmDurationSeconds}.");
        }
        if (!string.Equals(
            request.RuntimeFingerprintSha256,
            currentRuntimeFingerprint,
            StringComparison.OrdinalIgnoreCase))
        {
            return MutationAdapterResult<ArmOwnedMutationModeResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The requested runtime fingerprint does not match the executing build.");
        }

        _armedSessionId = commandSessionId;
        _expiresAtUnixMilliseconds = DateTimeOffset.UtcNow
            .AddSeconds(request.DurationSeconds)
            .ToUnixTimeMilliseconds();
        return MutationAdapterResult<ArmOwnedMutationModeResultPayload>.Success(new()
        {
            Mode = "owned-mutations-armed",
            SessionId = commandSessionId,
            ExpiresAtUnixMilliseconds = _expiresAtUnixMilliseconds,
            Postcondition = "owned-mutations-armed-for-current-session",
        });
    }

    public static void EndSession(string sessionId)
    {
        if (string.Equals(_armedSessionId, sessionId, StringComparison.Ordinal))
        {
            _armedSessionId = string.Empty;
            _expiresAtUnixMilliseconds = 0;
            IdempotencyRecords.Clear();
            LifecycleIdempotencyRecords.Clear();
        }
    }

    public static MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload> Materialize(
        MaterializeOwnedSurfaceUnitRequestPayload request,
        string commandSessionId)
    {
        var validationError = Validate(request, commandSessionId);
        if (validationError is not null)
        {
            return validationError;
        }

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var idempotencyRecordKey = string.Join(
            "|",
            request.Context.AssignmentId,
            request.Context.IdempotencyKey);
        if (IdempotencyRecords.TryGetValue(idempotencyRecordKey, out var prior))
        {
            if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
            {
                return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                    ProtocolErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used with a different mutation payload.");
            }

            var replay = Clone(prior.Result);
            replay.IdempotentReplay = true;
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Success(replay);
        }

        var taskforce = ResolveTaskforce(request.TaskforceId);
        if (taskforce is null || taskforce != Globals._playerTaskforce)
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "taskforceId must resolve exactly to the current player task force.");
        }

        if (OwnedEntities.ContainsKey(request.EntityId)
            || Singleton<ObjectsManager>.Instance._listOfAllObjects.Any(candidate =>
                candidate is not null
                && string.Equals(candidate._campaignTag, request.EntityId, StringComparison.Ordinal)))
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                "The requested external entity already has a live or retained owned object.");
        }

        var predicted = new OwnedSurfaceUnitStatePayload
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
        };
        if (request.Context.DryRun)
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Success(new()
            {
                Outcome = "validated",
                Postcondition = "owned-surface-unit-materialization-valid",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = request.Context.ExpectedRevision,
                IdempotencyKey = request.Context.IdempotencyKey,
                DryRun = true,
                RealizedUnit = predicted,
                UnsupportedFields = UnsupportedFields(),
            });
        }

        Vessel? created = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var position = new GeoPosition(request.Latitude, request.Longitude, 0d);
            var heading = new Vector3(0f, (float)NormalizeHeading(request.HeadingDegrees), 0f);
            created = Singleton<ObjectsManager>.Instance.createShip(
                request.VariantId,
                request.LoadoutId,
                request.UnitTypeId,
                Singleton<ObjectsManager>.Instance._listOfAllObjects.Count + 1,
                Singleton<SceneCreator>.Instance.GeoCenterPosition,
                position,
                request.Telegraph,
                heading,
                taskforce,
                loadAsync: false);
            if (created is null)
            {
                return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    "Sea Power's supported ship constructor returned no vessel.");
            }

            // Attach external ownership before any later operation can fail. Cleanup paths may
            // only touch objects bearing this exact tag and present in OwnedEntities.
            created._campaignTag = request.EntityId;
            created.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
            created.transform.eulerAngles = heading;
            created._geoPosition = position;
            created.setTelegraph(request.Telegraph);
            created._velocityInKnots = created.getSpeedForTelegraph(request.Telegraph);
            created._velocityInUnity = created._velocityInKnots * 0.0076554087f;
            Physics.SyncTransforms();

            var realized = Capture(created, request.TaskforceId);
            AssertRealizedIdentity(request, realized);
            var revision = request.Context.ExpectedRevision + 1;
            var result = new MaterializeOwnedSurfaceUnitResultPayload
            {
                Outcome = "materialized",
                Postcondition = "owned-surface-unit-materialized-and-read-back",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = revision,
                IdempotencyKey = request.Context.IdempotencyKey,
                RealizedUnit = realized,
                RecoverableOwnedObject = true,
                UnsupportedFields = UnsupportedFields(),
            };
            OwnedEntities.Add(request.EntityId, new(
                request.Context.AssignmentId,
                request.Context.AuthorityEpoch,
                revision,
                request.TaskforceId,
                created));
            stopwatch.Stop();
            result.SynchronousDurationMicroseconds = ElapsedMicroseconds(stopwatch);
            IdempotencyRecords.Add(idempotencyRecordKey, new(requestJson, Clone(result)));
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            if (created is not null
                && string.Equals(created._campaignTag, request.EntityId, StringComparison.Ordinal)
                && !created.Disabled)
            {
                created.Disable();
                OwnedEntities[request.EntityId] = new(
                    request.Context.AssignmentId,
                    request.Context.AuthorityEpoch,
                    request.Context.ExpectedRevision,
                    request.TaskforceId,
                    created);
            }
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"Owned materialization failed and retained only its tagged recoverable object: "
                + $"{exception.GetType().Name}: {exception.Message}");
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
        var resolved = ResolveOwned<OwnedSurfaceCheckpointPayload>(
            request.Context,
            request.EntityId,
            commandSessionId);
        if (!resolved.Succeeded)
        {
            return resolved.Error!;
        }

        var record = resolved.Record!;
        var vessel = record.Vessel!;
        if (vessel.Disabled)
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Portable checkpoints may only be captured from an active owned surface unit.");
        }
        var unit = Capture(vessel, record.TaskforceId);
        var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hash = CapabilityFingerprint.ComputeSha256(new[]
        {
            Field("schema-version", 1),
            Field("assignment-id", record.AssignmentId),
            Field("authority-epoch", record.AuthorityEpoch),
            Field("revision", record.Revision),
            Field("entity-id", unit.EntityId),
            Field("unit-type", unit.UnitTypeId),
            Field("variant", unit.VariantId),
            Field("loadout", unit.LoadoutId),
            Field("taskforce", unit.TaskforceId),
            Field("latitude", unit.Latitude),
            Field("longitude", unit.Longitude),
            Field("heading", unit.HeadingDegrees),
            Field("speed", unit.SpeedKnots),
            Field("telegraph", unit.Telegraph),
            Field("disabled", unit.IsDisabled),
            Field("destroyed", unit.IsDestroyed),
        });
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
        if (!ValidPosition(request.Latitude, request.Longitude, request.HeadingDegrees)
            || request.Telegraph is < -1 or > 5)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The surface update position, heading, or telegraph is invalid.");
        }
        var replay = TryLifecycleReplay(
            GameCommandTypes.UpdateOwnedSurfaceUnit,
            request.Context,
            request,
            commandSessionId);
        if (replay is not null)
        {
            return replay;
        }
        var resolved = ResolveOwned<OwnedSurfaceLifecycleResultPayload>(
            request.Context,
            request.EntityId,
            commandSessionId);
        if (!resolved.Succeeded)
        {
            return resolved.Error!;
        }

        var record = resolved.Record!;
        var vessel = record.Vessel!;
        if (vessel.Disabled)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A suspended surface unit must be enabled before applying an update.");
        }
        var timer = Stopwatch.StartNew();
        var position = new GeoPosition(request.Latitude, request.Longitude, 0d);
        vessel.transform.position = Utils.longLatToLocalV3(
            position,
            Globals._currentCenterTile);
        vessel.transform.eulerAngles = new Vector3(
            0f,
            (float)NormalizeHeading(request.HeadingDegrees),
            0f);
        vessel._geoPosition = position;
        vessel.setTelegraph(request.Telegraph);
        vessel._velocityInKnots = vessel.getSpeedForTelegraph(request.Telegraph);
        vessel._velocityInUnity = vessel._velocityInKnots * 0.0076554087f;
        Physics.SyncTransforms();
        var realized = Capture(vessel, record.TaskforceId);
        AssertUpdatedState(request, realized);
        record.Revision++;
        timer.Stop();
        var result = LifecycleResult(
            "updated",
            "owned-surface-unit-updated-and-read-back",
            request.Context,
            record,
            timer);
        StoreLifecycleReplay(GameCommandTypes.UpdateOwnedSurfaceUnit, request.Context, request, result);
        return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(result);
    }

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> SetSuspension(
        SetOwnedSurfaceSuspensionRequestPayload request,
        string commandSessionId)
    {
        var replay = TryLifecycleReplay(
            GameCommandTypes.SetOwnedSurfaceSuspension,
            request.Context,
            request,
            commandSessionId);
        if (replay is not null)
        {
            return replay;
        }
        var resolved = ResolveOwned<OwnedSurfaceLifecycleResultPayload>(
            request.Context,
            request.EntityId,
            commandSessionId);
        if (!resolved.Succeeded)
        {
            return resolved.Error!;
        }

        var record = resolved.Record!;
        var vessel = record.Vessel!;
        if (vessel.Disabled == request.IsSuspended)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                request.IsSuspended
                    ? "The owned surface unit is already suspended."
                    : "The owned surface unit is already enabled.");
        }
        var timer = Stopwatch.StartNew();
        if (request.IsSuspended)
        {
            vessel.Disable();
        }
        else
        {
            vessel.Enable();
        }
        record.Revision++;
        timer.Stop();
        var result = LifecycleResult(
            request.IsSuspended ? "suspended" : "enabled",
            request.IsSuspended
                ? "owned-surface-unit-suspended-outside-active-world"
                : "owned-surface-unit-enabled-and-read-back",
            request.Context,
            record,
            timer);
        StoreLifecycleReplay(
            GameCommandTypes.SetOwnedSurfaceSuspension,
            request.Context,
            request,
            result);
        return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(result);
    }

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> RestoreCheckpoint(
        RestoreOwnedSurfaceCheckpointRequestPayload request,
        string commandSessionId)
    {
        var replay = TryLifecycleReplay(
            GameCommandTypes.RestoreOwnedSurfaceCheckpoint,
            request.Context,
            request,
            commandSessionId);
        if (replay is not null)
        {
            return replay;
        }
        var resolved = ResolveRecord<OwnedSurfaceLifecycleResultPayload>(
            request.Context,
            request.EntityId,
            commandSessionId);
        if (!resolved.Succeeded)
        {
            return resolved.Error!;
        }

        var record = resolved.Record!;
        var checkpoint = record.Checkpoint;
        if (checkpoint is null
            || !string.Equals(checkpoint.CheckpointId, request.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(
                checkpoint.CheckpointSha256,
                request.CheckpointSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The requested portable checkpoint is not the retained checkpoint for this entity.");
        }
        if (checkpoint.Unit.IsDisabled || checkpoint.Unit.IsDestroyed)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Only an active, non-destroyed portable checkpoint can be rematerialized.");
        }
        if (!string.Equals(checkpoint.Unit.UnitTypeId, SupportedUnitType, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.VariantId, SupportedVariant, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.LoadoutId, SupportedLoadout, StringComparison.Ordinal)
            || checkpoint.Unit.Latitude is not double checkpointLatitude
            || checkpoint.Unit.Longitude is not double checkpointLongitude
            || checkpoint.Unit.HeadingDegrees is not double checkpointHeading
            || !ValidPosition(checkpointLatitude, checkpointLongitude, checkpointHeading)
            || checkpoint.Unit.SpeedKnots is not double checkpointSpeed
            || !double.IsFinite(checkpointSpeed)
            || checkpoint.Unit.Telegraph is < -1 or > 5)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The retained checkpoint does not contain a complete supported Knox state.");
        }

        var taskforce = ResolveTaskforce(checkpoint.Unit.TaskforceId);
        if (taskforce is null || taskforce != Globals._playerTaskforce)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The checkpoint task force must resolve exactly to the current player task force.");
        }

        Vessel? vessel = record.Vessel;
        var reconstructed = vessel == null;
        if (!reconstructed)
        {
            if (!string.Equals(vessel!._campaignTag, request.EntityId, StringComparison.Ordinal))
            {
                return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    "The retained object no longer bears the exact external ownership tag.");
            }
            if (!vessel.Disabled)
            {
                return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    "The owned surface unit must be suspended or absent before rematerialization.");
            }
        }

        var timer = Stopwatch.StartNew();
        try
        {
            if (reconstructed)
            {
                var position = new GeoPosition(
                    checkpointLatitude,
                    checkpointLongitude,
                    0d);
                var heading = new Vector3(
                    0f,
                    (float)NormalizeHeading(checkpointHeading),
                    0f);
                vessel = Singleton<ObjectsManager>.Instance.createShip(
                    checkpoint.Unit.VariantId,
                    checkpoint.Unit.LoadoutId,
                    checkpoint.Unit.UnitTypeId,
                    Singleton<ObjectsManager>.Instance._listOfAllObjects.Count + 1,
                    Singleton<SceneCreator>.Instance.GeoCenterPosition,
                    position,
                    checkpoint.Unit.Telegraph,
                    heading,
                    taskforce,
                    loadAsync: false);
                if (vessel is null)
                {
                    return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                        ProtocolErrorCodes.MutationPreconditionFailed,
                        "Sea Power's supported ship constructor returned no vessel during rematerialization.");
                }
                vessel._campaignTag = request.EntityId;
                record.Vessel = vessel;
            }

            ApplyPortableState(vessel!, checkpoint.Unit);
            if (vessel!.Disabled)
            {
                vessel.Enable();
            }
            record.TaskforceId = checkpoint.Unit.TaskforceId;
            var realized = Capture(vessel, record.TaskforceId);
            AssertCheckpointRestored(checkpoint.Unit, realized);
            record.Revision++;
            timer.Stop();
            var result = LifecycleResult(
                "restored-from-checkpoint",
                reconstructed
                    ? "owned-surface-unit-reconstructed-from-portable-checkpoint"
                    : "owned-surface-unit-reenabled-from-portable-checkpoint",
                request.Context,
                record,
                timer);
            result.SourceCheckpointId = checkpoint.CheckpointId;
            result.SourceCheckpointSha256 = checkpoint.CheckpointSha256;
            result.ReconstructedAfterSceneLoss = reconstructed;
            StoreLifecycleReplay(
                GameCommandTypes.RestoreOwnedSurfaceCheckpoint,
                request.Context,
                request,
                result);
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            if (vessel != null
                && string.Equals(vessel._campaignTag, request.EntityId, StringComparison.Ordinal))
            {
                if (!vessel.Disabled)
                {
                    vessel.Disable();
                }
                record.Vessel = vessel;
            }
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Checkpoint rematerialization failed and retained only its tagged recoverable object: "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    private static OwnedResolution<T> ResolveOwned<T>(
        OwnedMutationContextPayload? context,
        string entityId,
        string commandSessionId)
    {
        var resolved = ResolveRecord<T>(context, entityId, commandSessionId);
        if (!resolved.Succeeded)
        {
            return resolved;
        }
        var record = resolved.Record!;
        if (record.Vessel == null
            || !string.Equals(record.Vessel._campaignTag, entityId, StringComparison.Ordinal))
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "No recoverable bridge-owned surface unit matches the requested entity.");
        }
        return resolved;
    }

    private static OwnedResolution<T> ResolveRecord<T>(
        OwnedMutationContextPayload? context,
        string entityId,
        string commandSessionId)
    {
        if (!IsArmedFor(commandSessionId))
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        if (context is null
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.InvalidSession,
                "The mutation context session does not match the executing pipe session.");
        }
        if (!ValidScopedId(entityId, "unit:crimean-tom:")
            || !ValidScopedId(context.AssignmentId, "assignment:")
            || string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || context.IdempotencyKey.Length > 128)
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Owned entity, assignment, and idempotency identifiers are required.");
        }
        if (!OwnedEntities.TryGetValue(entityId, out var record))
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "No bridge-owned surface record matches the requested entity.");
        }
        if (!string.Equals(record.AssignmentId, context.AssignmentId, StringComparison.Ordinal))
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The mutation assignment does not own this surface unit.");
        }
        if (context.AuthorityEpoch != record.AuthorityEpoch)
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.StaleAuthorityEpoch,
                $"Expected authority epoch {record.AuthorityEpoch}, received {context.AuthorityEpoch}.");
        }
        if (context.ExpectedRevision != record.Revision)
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.StaleRevision,
                $"Expected entity revision {record.Revision}, received {context.ExpectedRevision}.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<ObjectsManager>.InstanceExists()
            || !Singleton<TaskforceManager>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone
            || !GameTime.IsPaused())
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A loaded, paused tactical mission is required for owned lifecycle operations.");
        }
        return OwnedResolution<T>.Success(record);
    }

    private static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>? TryLifecycleReplay<T>(
        string command,
        OwnedMutationContextPayload context,
        T request,
        string commandSessionId)
    {
        if (!IsArmedFor(commandSessionId)
            || context is null
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return null;
        }
        var key = LifecycleKey(command, context);
        if (!LifecycleIdempotencyRecords.TryGetValue(key, out var prior))
        {
            return null;
        }
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.IdempotencyConflict,
                "The idempotency key was already used with a different lifecycle payload.");
        }
        var replay = CloneLifecycle(prior.Result);
        replay.IdempotentReplay = true;
        return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(replay);
    }

    private static void StoreLifecycleReplay<T>(
        string command,
        OwnedMutationContextPayload context,
        T request,
        OwnedSurfaceLifecycleResultPayload result) =>
        LifecycleIdempotencyRecords.Add(
            LifecycleKey(command, context),
            new(
                JsonSerializer.Serialize(request, JsonOptions),
                CloneLifecycle(result)));

    private static string LifecycleKey(string command, OwnedMutationContextPayload context) =>
        string.Join("|", command, context.AssignmentId, context.IdempotencyKey);

    private static OwnedSurfaceLifecycleResultPayload LifecycleResult(
        string outcome,
        string postcondition,
        OwnedMutationContextPayload context,
        OwnedEntityRecord record,
        Stopwatch timer) => new()
    {
        Outcome = outcome,
        Postcondition = postcondition,
        AssignmentId = record.AssignmentId,
        AuthorityEpoch = record.AuthorityEpoch,
        Revision = record.Revision,
        IdempotencyKey = context.IdempotencyKey,
        SynchronousDurationMicroseconds = ElapsedMicroseconds(timer),
        Unit = Capture(record.Vessel!, record.TaskforceId),
    };

    private static void ApplyPortableState(
        Vessel vessel,
        OwnedSurfaceUnitStatePayload state)
    {
        var position = new GeoPosition(state.Latitude!.Value, state.Longitude!.Value, 0d);
        vessel.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
        vessel.transform.eulerAngles = new Vector3(
            0f,
            (float)NormalizeHeading(state.HeadingDegrees!.Value),
            0f);
        vessel._geoPosition = position;
        vessel.setTelegraph(state.Telegraph);
        vessel._velocityInKnots = (float)state.SpeedKnots!.Value;
        vessel._velocityInUnity = vessel._velocityInKnots * 0.0076554087f;
        Physics.SyncTransforms();
    }

    private static MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>? Validate(
        MaterializeOwnedSurfaceUnitRequestPayload request,
        string commandSessionId)
    {
        if (!IsArmedFor(commandSessionId))
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        if (request.Context is null)
        {
            return Invalid("mutation context is required.");
        }
        if (!string.Equals(request.Context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The mutation context session does not match the executing pipe session.");
        }
        if (!ValidScopedId(request.Context.AssignmentId, "assignment:"))
        {
            return Invalid("assignmentId must be a bounded 'assignment:' identifier.");
        }
        if (!ValidScopedId(request.EntityId, "unit:crimean-tom:"))
        {
            return Invalid("entityId must be a bounded 'unit:crimean-tom:' identifier.");
        }
        if (request.Context.AuthorityEpoch < 1)
        {
            return Invalid("authorityEpoch must be positive.");
        }
        if (request.Context.ExpectedRevision != 0)
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                "Initial surface materialization requires expectedRevision 0.");
        }
        if (string.IsNullOrWhiteSpace(request.Context.IdempotencyKey)
            || request.Context.IdempotencyKey.Length > 128)
        {
            return Invalid("idempotencyKey must contain 1 to 128 characters.");
        }
        if (!string.Equals(request.UnitTypeId, SupportedUnitType, StringComparison.Ordinal)
            || !string.Equals(request.VariantId, SupportedVariant, StringComparison.Ordinal)
            || !string.Equals(request.LoadoutId, SupportedLoadout, StringComparison.Ordinal))
        {
            return Invalid(
                $"Only {SupportedUnitType}/{SupportedVariant}/{SupportedLoadout} is allow-listed.");
        }
        if (!double.IsFinite(request.Latitude)
            || !double.IsFinite(request.Longitude)
            || !double.IsFinite(request.HeadingDegrees)
            || request.Latitude is < -90d or > 90d
            || request.Longitude is < -180d or > 180d)
        {
            return Invalid("Latitude, longitude, and heading must be finite and geographically valid.");
        }
        if (request.Telegraph is < -1 or > 5)
        {
            return Invalid("telegraph must be between -1 and 5.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<ObjectsManager>.InstanceExists()
            || !Singleton<TaskforceManager>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone
            || !GameTime.IsPaused())
        {
            return Invalid("A fully loaded, paused tactical mission is required.");
        }
        return null;
    }

    private static MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload> Invalid(
        string message) =>
        MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
            ProtocolErrorCodes.MutationPreconditionFailed,
            message);

    private static bool ValidScopedId(string value, string prefix) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.All(character => char.IsLetterOrDigit(character)
            || character is ':' or '-' or '_' or '.');

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

    private static OwnedSurfaceUnitStatePayload Capture(Vessel vessel, string taskforceId) => new()
    {
        StableUnitId = $"campaign-unit:{vessel._campaignTag}",
        GameUniqueId = vessel.UniqueID,
        EntityId = vessel._campaignTag ?? string.Empty,
        UnitTypeId = vessel._obp?._objectIniName ?? string.Empty,
        VariantId = vessel._obp?._objectVariantReference ?? string.Empty,
        LoadoutId = vessel._obp?._loadoutVariant ?? string.Empty,
        TaskforceId = taskforceId,
        Latitude = Finite(vessel._geoPosition._latitude),
        Longitude = Finite(vessel._geoPosition._longitude),
        HeadingDegrees = Finite(vessel.getHeading()),
        SpeedKnots = Finite(vessel.getVelocityInKnots()),
        Telegraph = vessel.getTelegraph(),
        IsDisabled = vessel.Disabled,
        IsDestroyed = vessel.IsDestroyed,
    };

    private static void AssertRealizedIdentity(
        MaterializeOwnedSurfaceUnitRequestPayload request,
        OwnedSurfaceUnitStatePayload realized)
    {
        if (!string.Equals(realized.EntityId, request.EntityId, StringComparison.Ordinal)
            || !string.Equals(realized.UnitTypeId, request.UnitTypeId, StringComparison.Ordinal)
            || !string.Equals(realized.VariantId, request.VariantId, StringComparison.Ordinal)
            || !string.Equals(realized.LoadoutId, request.LoadoutId, StringComparison.Ordinal)
            || !string.Equals(realized.TaskforceId, request.TaskforceId, StringComparison.Ordinal)
            || !Within(realized.Latitude, request.Latitude, PositionToleranceDegrees)
            || !Within(realized.Longitude, request.Longitude, PositionToleranceDegrees)
            || !Within(
                realized.HeadingDegrees,
                NormalizeHeading(request.HeadingDegrees),
                HeadingToleranceDegrees)
            || realized.Telegraph != request.Telegraph
            || realized.IsDestroyed
            || realized.IsDisabled)
        {
            throw new InvalidOperationException("Sea Power realized a different surface identity.");
        }
    }

    private static void AssertUpdatedState(
        UpdateOwnedSurfaceUnitRequestPayload request,
        OwnedSurfaceUnitStatePayload realized)
    {
        if (!string.Equals(realized.EntityId, request.EntityId, StringComparison.Ordinal)
            || !Within(realized.Latitude, request.Latitude, PositionToleranceDegrees)
            || !Within(realized.Longitude, request.Longitude, PositionToleranceDegrees)
            || !Within(
                realized.HeadingDegrees,
                NormalizeHeading(request.HeadingDegrees),
                HeadingToleranceDegrees)
            || realized.Telegraph != request.Telegraph
            || realized.IsDestroyed
            || realized.IsDisabled)
        {
            throw new InvalidOperationException(
                "Sea Power did not realize the requested supported surface update.");
        }
    }

    private static void AssertCheckpointRestored(
        OwnedSurfaceUnitStatePayload expected,
        OwnedSurfaceUnitStatePayload realized)
    {
        if (!string.Equals(realized.StableUnitId, expected.StableUnitId, StringComparison.Ordinal)
            || !string.Equals(realized.EntityId, expected.EntityId, StringComparison.Ordinal)
            || !string.Equals(realized.UnitTypeId, expected.UnitTypeId, StringComparison.Ordinal)
            || !string.Equals(realized.VariantId, expected.VariantId, StringComparison.Ordinal)
            || !string.Equals(realized.LoadoutId, expected.LoadoutId, StringComparison.Ordinal)
            || !string.Equals(realized.TaskforceId, expected.TaskforceId, StringComparison.Ordinal)
            || !Within(realized.Latitude, expected.Latitude, PositionToleranceDegrees)
            || !Within(realized.Longitude, expected.Longitude, PositionToleranceDegrees)
            || !Within(realized.HeadingDegrees, expected.HeadingDegrees, HeadingToleranceDegrees)
            || !Within(realized.SpeedKnots, expected.SpeedKnots, SpeedToleranceKnots)
            || realized.Telegraph != expected.Telegraph
            || realized.IsDestroyed
            || realized.IsDisabled)
        {
            throw new InvalidOperationException(
                "Sea Power did not restore every supported checkpoint field within tolerance.");
        }
    }

    private static bool Within(double? actual, double? expected, double tolerance) =>
        actual is double actualValue
        && expected is double expectedValue
        && Math.Abs(actualValue - expectedValue) <= tolerance;

    private static MaterializeOwnedSurfaceUnitResultPayload Clone(
        MaterializeOwnedSurfaceUnitResultPayload value) =>
        JsonSerializer.Deserialize<MaterializeOwnedSurfaceUnitResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException("Could not clone mutation result.");

    private static OwnedSurfaceLifecycleResultPayload CloneLifecycle(
        OwnedSurfaceLifecycleResultPayload value) =>
        JsonSerializer.Deserialize<OwnedSurfaceLifecycleResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException("Could not clone lifecycle result.");

    private static string[] UnsupportedFields() =>
    [
        "damage-state-application",
        "fuel-state-application",
        "magazine-state-application",
        "sensor-state-application",
        "formation-state-application",
        "hosted-aircraft-application",
    ];

    private static double NormalizeHeading(double value)
    {
        var normalized = value % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    private static double? Finite(double value) =>
        double.IsFinite(value) ? value : null;

    private static bool ValidPosition(double latitude, double longitude, double heading) =>
        double.IsFinite(latitude)
        && double.IsFinite(longitude)
        && double.IsFinite(heading)
        && latitude is >= -90d and <= 90d
        && longitude is >= -180d and <= 180d;

    private static KeyValuePair<string, string?> Field(string name, object? value) =>
        new(name, value switch
        {
            null => null,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(
                null,
                System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        });

    private static long ElapsedMicroseconds(Stopwatch stopwatch) =>
        stopwatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

    private sealed class OwnedEntityRecord(
        string assignmentId,
        long authorityEpoch,
        long revision,
        string taskforceId,
        Vessel vessel)
    {
        public string AssignmentId { get; } = assignmentId;
        public long AuthorityEpoch { get; } = authorityEpoch;
        public long Revision { get; set; } = revision;
        public string TaskforceId { get; set; } = taskforceId;
        public Vessel? Vessel { get; set; } = vessel;
        public OwnedSurfaceCheckpointPayload? Checkpoint { get; set; }
    }

    private sealed record IdempotencyRecord(
        string RequestJson,
        MaterializeOwnedSurfaceUnitResultPayload Result);

    private sealed record LifecycleIdempotencyRecord(
        string RequestJson,
        OwnedSurfaceLifecycleResultPayload Result);

    private sealed class OwnedResolution<T>
    {
        public bool Succeeded { get; private init; }
        public OwnedEntityRecord? Record { get; private init; }
        public MutationAdapterResult<T>? Error { get; private init; }

        public static OwnedResolution<T> Success(OwnedEntityRecord record) => new()
        {
            Succeeded = true,
            Record = record,
        };

        public static OwnedResolution<T> Fail(string code, string message) => new()
        {
            Error = MutationAdapterResult<T>.Failure(code, message),
        };
    }
}

internal sealed class MutationAdapterResult<T>
{
    public bool Succeeded { get; private init; }
    public string ErrorCode { get; private init; } = string.Empty;
    public string ErrorMessage { get; private init; } = string.Empty;
    public T? Payload { get; private init; }

    public static MutationAdapterResult<T> Success(T payload) => new()
    {
        Succeeded = true,
        Payload = payload,
    };

    public static MutationAdapterResult<T> Failure(string code, string message) => new()
    {
        ErrorCode = code,
        ErrorMessage = message,
    };
}
