using System.Diagnostics;
using System.Text.Json;

using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeOwnedMutationAdapter
{
    public const string Confirmation = "enable-owned-mutations";
    public const string FaultInjectionConfirmation = "inject-owned-mutation-failure";

    internal const string SupportedUnitType = "usn_ff_knox_84";
    internal const string SupportedVariant = "Variant43";
    internal const string SupportedLoadout = "Default";

    internal static string GetPlayerTaskforceId()
    {
        if (Globals._playerTaskforce is null || !Singleton<TaskforceManager>.InstanceExists())
        {
            return string.Empty;
        }

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
            if (ReferenceEquals(taskforce, Globals._playerTaskforce))
            {
                return occurrence == 1 ? baseId : $"{baseId}#{occurrence}";
            }
        }
        return string.Empty;
    }
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
    private static readonly Dictionary<string, RecoveryReleaseIdempotencyRecord>
        RecoveryReleaseIdempotencyRecords = new(StringComparer.Ordinal);

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
            RecoveryReleaseIdempotencyRecords.Clear();
        }
    }

    public static OwnedSurfaceResourceStatePayload CaptureResourceState()
    {
        var entries = OwnedEntities
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var record = pair.Value;
                var vessel = record.Vessel;
                var lifecycleState = vessel == null
                    ? "detached-after-scene-loss"
                    : vessel.Disabled
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
                    LifecycleState = lifecycleState,
                    HasCheckpoint = record.Checkpoint is not null,
                    RecoveryRequired = record.RecoveryRequired,
                    GameUniqueId = vessel == null ? null : vessel.UniqueID,
                };
            })
            .ToArray();
        var active = entries.Count(entry => entry.LifecycleState == "active");
        var suspended = entries.Count(entry =>
            entry.LifecycleState is "suspended-retained" or "recovery-required-suspended");
        var detached = entries.Count(entry => entry.LifecycleState == "detached-after-scene-loss");
        return new OwnedSurfaceResourceStatePayload
        {
            OwnedRecordCount = entries.Length,
            ActiveObjectCount = active,
            SuspendedRetainedObjectCount = suspended,
            DetachedObjectCount = detached,
            DetachedCheckpointCount = entries.Count(entry =>
                entry.LifecycleState == "detached-after-scene-loss" && entry.HasCheckpoint),
            RecoveryRequiredCount = entries.Count(entry => entry.RecoveryRequired),
            RetainedConstructedObjectCount = active + suspended,
            MemoryReclamationSupported = false,
            SchedulerPolicy = "active-and-suspended-retained-objects-consume-materialized-capacity",
            Entities = entries,
        };
    }

    public static MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>
        ReleaseDetachedRecovery(
            OwnedSurfaceEntityRequestPayload request,
            string commandSessionId)
    {
        if (!IsArmedFor(commandSessionId))
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        var context = request.Context;
        if (context is null
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The recovery release session does not match the executing pipe session.");
        }
        if (!ValidScopedId(request.EntityId, "unit:crimean-tom:")
            || !ValidScopedId(context.AssignmentId, "assignment:")
            || context.AuthorityEpoch < 1
            || context.ExpectedRevision < 0
            || string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || context.IdempotencyKey.Length > 128)
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The detached recovery identity or fence is invalid.");
        }

        var key = LifecycleKey(GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery, context);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        if (RecoveryReleaseIdempotencyRecords.TryGetValue(key, out var prior))
        {
            if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
            {
                return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                    ProtocolErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used with a different recovery release payload.");
            }
            var replay = CloneRecoveryRelease(prior.Result);
            replay.IdempotentReplay = true;
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Success(replay);
        }

        if (!OwnedEntities.TryGetValue(request.EntityId, out var record))
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "No bridge-owned recovery record matches the requested entity.");
        }
        if (!string.Equals(record.AssignmentId, context.AssignmentId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The mutation assignment does not own this recovery record.");
        }
        if (context.AuthorityEpoch != record.AuthorityEpoch)
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.StaleAuthorityEpoch,
                $"Expected authority epoch {record.AuthorityEpoch}, received {context.AuthorityEpoch}.");
        }
        if (context.ExpectedRevision != record.Revision)
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                $"Expected entity revision {record.Revision}, received {context.ExpectedRevision}.");
        }
        if (record.Vessel != null
            || !record.RecoveryRequired
            || record.Checkpoint is not null)
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Recovery release requires a detached partial-materialization record with no checkpoint.");
        }

        OwnedEntities.Remove(request.EntityId);
        var result = new OwnedSurfaceRecoveryReleaseResultPayload
        {
            Outcome = "released",
            Postcondition = "detached-owned-recovery-record-released-without-unity-mutation",
            EntityId = request.EntityId,
            AssignmentId = record.AssignmentId,
            AuthorityEpoch = record.AuthorityEpoch,
            Revision = record.Revision,
            IdempotencyKey = context.IdempotencyKey,
        };
        RecoveryReleaseIdempotencyRecords.Add(
            key,
            new(requestJson, CloneRecoveryRelease(result)));
        return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Success(result);
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

        var sourceCheckpointError = ValidateSourceCheckpoint(request);
        if (sourceCheckpointError is not null)
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                sourceCheckpointError);
        }

        if (OwnedEntities.TryGetValue(request.EntityId, out var retained))
        {
            return RematerializeRetained(request, requestJson, idempotencyRecordKey, retained);
        }

        if (Singleton<ObjectsManager>.Instance._listOfAllObjects.Any(candidate =>
                candidate is not null
                && string.Equals(candidate._campaignTag, request.EntityId, StringComparison.Ordinal)))
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                "The requested external entity already has a live or retained owned object.");
        }

        var sourceCheckpoint = request.SourceCheckpoint;
        var sourceUnit = sourceCheckpoint?.Unit;
        var predicted = new OwnedSurfaceUnitStatePayload
        {
            StableUnitId = $"campaign-unit:{request.EntityId}",
            EntityId = request.EntityId,
            UnitTypeId = request.UnitTypeId,
            VariantId = request.VariantId,
            LoadoutId = request.LoadoutId,
            TaskforceId = request.TaskforceId,
            Latitude = sourceUnit?.Latitude ?? request.Latitude,
            Longitude = sourceUnit?.Longitude ?? request.Longitude,
            HeadingDegrees = sourceUnit?.HeadingDegrees ?? NormalizeHeading(request.HeadingDegrees),
            SpeedKnots = sourceUnit?.SpeedKnots,
            Telegraph = sourceUnit?.Telegraph ?? request.Telegraph,
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
            var position = new GeoPosition(predicted.Latitude!.Value, predicted.Longitude!.Value, 0d);
            var heading = new Vector3(0f, (float)NormalizeHeading(predicted.HeadingDegrees!.Value), 0f);
            created = Singleton<ObjectsManager>.Instance.createShip(
                request.VariantId,
                request.LoadoutId,
                request.UnitTypeId,
                Singleton<ObjectsManager>.Instance._listOfAllObjects.Count + 1,
                Singleton<SceneCreator>.Instance.GeoCenterPosition,
                position,
                predicted.Telegraph,
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
            InjectFailure(request.FaultInjectionStep, "after-owned-tag");
            if (sourceUnit is null)
            {
                created.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
                created.transform.eulerAngles = heading;
                created._geoPosition = position;
                created.setTelegraph(request.Telegraph);
                created._velocityInKnots = created.getSpeedForTelegraph(request.Telegraph);
                created._velocityInUnity = created._velocityInKnots * 0.0076554087f;
                Physics.SyncTransforms();
            }
            else
            {
                ApplyPortableState(created, sourceUnit);
            }
            InjectFailure(request.FaultInjectionStep, "after-state-apply");

            var initialStoresQuantity = StoreQuantity(created);
            var realized = Capture(created, request.TaskforceId, initialStoresQuantity);
            if (sourceUnit is null)
            {
                AssertRealizedIdentity(request, realized);
            }
            else
            {
                AssertCheckpointRestored(sourceUnit, realized);
            }
            var revision = request.Context.ExpectedRevision + 1;
            var result = new MaterializeOwnedSurfaceUnitResultPayload
            {
                Outcome = sourceUnit is null ? "materialized" : "materialized-from-portable-checkpoint",
                Postcondition = sourceUnit is null
                    ? "owned-surface-unit-materialized-and-read-back"
                    : "owned-surface-unit-reconstructed-from-imported-portable-checkpoint",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = revision,
                IdempotencyKey = request.Context.IdempotencyKey,
                RealizedUnit = realized,
                RecoverableOwnedObject = true,
                UnsupportedFields = UnsupportedFields(),
            };
            var ownedRecord = new OwnedEntityRecord(
                request.Context.AssignmentId,
                request.Context.AuthorityEpoch,
                revision,
                request.TaskforceId,
                created,
                initialStoresQuantity);
            ownedRecord.Checkpoint = sourceCheckpoint;
            OwnedEntities.Add(request.EntityId, ownedRecord);
            InjectFailure(request.FaultInjectionStep, "after-registry-add");
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
                    created,
                    StoreQuantity(created),
                    recoveryRequired: true);
            }
            var injected = exception as InjectedMutationFailureException;
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                injected is null
                    ? ProtocolErrorCodes.MutationPreconditionFailed
                    : ProtocolErrorCodes.MutationInjectedFailure,
                "operation=materialize; recovery=recoverable-suspended-owned-object; "
                + $"failure={exception.GetType().Name}: {exception.Message}",
                injected is null
                    ? null
                    : $"fault:materialize:{injected.Step}:recoverable-suspended-owned-object");
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private static MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload> RematerializeRetained(
        MaterializeOwnedSurfaceUnitRequestPayload request,
        string requestJson,
        string idempotencyRecordKey,
        OwnedEntityRecord record)
    {
        var vessel = record.Vessel;
        var checkpoint = record.Checkpoint;
        if (record.RecoveryRequired
            || vessel is null
            || !vessel.Disabled
            || checkpoint?.Unit is null
            || checkpoint.Unit.IsDisabled
            || checkpoint.Unit.IsDestroyed
            || request.Context.ExpectedRevision != 0
            || request.Context.AuthorityEpoch <= record.AuthorityEpoch
            || string.Equals(request.Context.AssignmentId, record.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(vessel._campaignTag, request.EntityId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.EntityId, request.EntityId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.UnitTypeId, request.UnitTypeId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.VariantId, request.VariantId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.LoadoutId, request.LoadoutId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.TaskforceId, request.TaskforceId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                "The retained owned object is not a checkpointed suspended predecessor of this higher-epoch assignment.");
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
                Outcome = "validated-retained-rematerialization",
                Postcondition = "retained-owned-surface-unit-rematerialization-valid",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = request.Context.ExpectedRevision,
                IdempotencyKey = request.Context.IdempotencyKey,
                DryRun = true,
                RealizedUnit = predicted,
                RecoverableOwnedObject = true,
                UnsupportedFields = UnsupportedFields(),
            });
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var requestedState = new OwnedSurfaceUnitStatePayload
            {
                StableUnitId = checkpoint.Unit.StableUnitId,
                GameUniqueId = checkpoint.Unit.GameUniqueId,
                EntityId = checkpoint.Unit.EntityId,
                UnitTypeId = checkpoint.Unit.UnitTypeId,
                VariantId = checkpoint.Unit.VariantId,
                LoadoutId = checkpoint.Unit.LoadoutId,
                TaskforceId = checkpoint.Unit.TaskforceId,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                HeadingDegrees = NormalizeHeading(request.HeadingDegrees),
                SpeedKnots = checkpoint.Unit.SpeedKnots,
                Telegraph = request.Telegraph,
                FuelFractionMilliunits = checkpoint.Unit.FuelFractionMilliunits,
                StoresFractionMilliunits = checkpoint.Unit.StoresFractionMilliunits,
                DamageFractionMilliunits = checkpoint.Unit.DamageFractionMilliunits,
                IsDisabled = false,
                IsDestroyed = false,
            };
            ApplyPortableState(vessel, requestedState);
            if (vessel.Disabled)
            {
                vessel.Enable();
            }
            var realized = Capture(vessel, request.TaskforceId, record.InitialStoresQuantity);
            AssertRealizedIdentity(request, realized);
            var revision = request.Context.ExpectedRevision + 1;
            record.AssignmentId = request.Context.AssignmentId;
            record.AuthorityEpoch = request.Context.AuthorityEpoch;
            record.Revision = revision;
            record.TaskforceId = request.TaskforceId;
            record.RecoveryRequired = false;
            stopwatch.Stop();
            var result = new MaterializeOwnedSurfaceUnitResultPayload
            {
                Outcome = "rematerialized-retained",
                Postcondition = "retained-owned-surface-unit-reenabled-and-read-back",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = revision,
                IdempotencyKey = request.Context.IdempotencyKey,
                RealizedUnit = realized,
                RecoverableOwnedObject = true,
                SynchronousDurationMicroseconds = ElapsedMicroseconds(stopwatch),
                UnsupportedFields = UnsupportedFields(),
            };
            IdempotencyRecords.Add(idempotencyRecordKey, new(requestJson, Clone(result)));
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            var recovery = "source-suspension-restored";
            try
            {
                ApplyPortableState(vessel, checkpoint.Unit);
                if (!vessel.Disabled)
                {
                    vessel.Disable();
                }
            }
            catch
            {
                recovery = "recoverable-suspended-owned-object";
                record.RecoveryRequired = true;
                if (!vessel.Disabled)
                {
                    vessel.Disable();
                }
            }
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"operation=rematerialize-retained; recovery={recovery}; "
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
        if (record.RecoveryRequired)
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A partial materialization object cannot produce an authoritative checkpoint.");
        }
        if (vessel.Disabled)
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Portable checkpoints may only be captured from an active owned surface unit.");
        }
        var unit = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
        var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hash = ComputeCheckpointSha256(
            record.AssignmentId,
            record.AuthorityEpoch,
            record.Revision,
            unit);
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
        var faultError = ValidateFaultInjection(
            request.FaultInjectionStep,
            request.FaultInjectionConfirmation,
            "after-position-apply",
            "after-telegraph-apply",
            "after-readback");
        if (faultError is not null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                faultError);
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
        var original = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
        var timer = Stopwatch.StartNew();
        try
        {
            var position = new GeoPosition(request.Latitude, request.Longitude, 0d);
            vessel.transform.position = Utils.longLatToLocalV3(
                position,
                Globals._currentCenterTile);
            vessel.transform.eulerAngles = new Vector3(
                0f,
                (float)NormalizeHeading(request.HeadingDegrees),
                0f);
            vessel._geoPosition = position;
            InjectFailure(request.FaultInjectionStep, "after-position-apply");
            vessel.setTelegraph(request.Telegraph);
            vessel._velocityInKnots = vessel.getSpeedForTelegraph(request.Telegraph);
            vessel._velocityInUnity = vessel._velocityInKnots * 0.0076554087f;
            InjectFailure(request.FaultInjectionStep, "after-telegraph-apply");
            Physics.SyncTransforms();
            var realized = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
            AssertUpdatedState(request, realized);
            InjectFailure(request.FaultInjectionStep, "after-readback");
            record.Revision++;
            timer.Stop();
            var result = LifecycleResult(
                "updated",
                "owned-surface-unit-updated-and-read-back",
                request.Context,
                record,
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
            var recovery = "source-state-restored";
            try
            {
                ApplyPortableState(vessel, original);
                AssertCheckpointRestored(original, Capture(vessel, record.TaskforceId, record.InitialStoresQuantity));
            }
            catch
            {
                if (!vessel.Disabled)
                {
                    vessel.Disable();
                }
                recovery = "recoverable-suspended-owned-object";
            }
            record.RecoveryRequired = recovery == "recoverable-suspended-owned-object";
            var injected = exception as InjectedMutationFailureException;
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                injected is null
                    ? ProtocolErrorCodes.MutationPreconditionFailed
                    : ProtocolErrorCodes.MutationInjectedFailure,
                $"operation=update; recovery={recovery}; "
                + $"failure={exception.GetType().Name}: {exception.Message}",
                injected is null ? null : $"fault:update:{injected.Step}:{recovery}");
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
        var faultError = ValidateFaultInjection(
            request.FaultInjectionStep,
            request.FaultInjectionConfirmation,
            "after-native-toggle",
            "after-readback");
        if (faultError is not null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                faultError);
        }
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
        if (record.RecoveryRequired && !request.IsSuspended)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A partial materialization object cannot be enabled; unload its scene before cleanup.");
        }
        if (vessel.Disabled == request.IsSuspended)
        {
            if (request.IsSuspended)
            {
                return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                    ProtocolErrorCodes.StaleRevision,
                    "The owned surface unit is already suspended.");
            }

            var converged = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
            var convergedResult = LifecycleResult(
                "enabled",
                "owned-surface-unit-enabled-and-read-back",
                request.Context,
                record,
                Stopwatch.StartNew());
            convergedResult.IdempotentReplay = true;
            convergedResult.Unit = converged;
            StoreLifecycleReplay(
                GameCommandTypes.SetOwnedSurfaceSuspension,
                request.Context,
                request,
                convergedResult);
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(convergedResult);
        }
        var originalDisabled = vessel.Disabled;
        var timer = Stopwatch.StartNew();
        try
        {
            if (request.IsSuspended)
            {
                vessel.Disable();
            }
            else
            {
                vessel.Enable();
            }
            InjectFailure(request.FaultInjectionStep, "after-native-toggle");
            var realized = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
            if (realized.IsDisabled != request.IsSuspended || realized.IsDestroyed)
            {
                throw new InvalidOperationException(
                    "Sea Power did not realize the requested suspension state.");
            }
            InjectFailure(request.FaultInjectionStep, "after-readback");
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
        catch (Exception exception)
        {
            var recovery = "source-state-restored";
            try
            {
                if (vessel.Disabled != originalDisabled)
                {
                    if (originalDisabled)
                    {
                        vessel.Disable();
                    }
                    else
                    {
                        vessel.Enable();
                    }
                }
                if (vessel.Disabled != originalDisabled)
                {
                    throw new InvalidOperationException("Native suspension rollback did not stick.");
                }
            }
            catch
            {
                if (!vessel.Disabled)
                {
                    vessel.Disable();
                }
                recovery = "recoverable-suspended-owned-object";
            }
            record.RecoveryRequired = recovery == "recoverable-suspended-owned-object";
            var injected = exception as InjectedMutationFailureException;
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                injected is null
                    ? ProtocolErrorCodes.MutationPreconditionFailed
                    : ProtocolErrorCodes.MutationInjectedFailure,
                $"operation=suspension; recovery={recovery}; "
                + $"failure={exception.GetType().Name}: {exception.Message}",
                injected is null ? null : $"fault:suspension:{injected.Step}:{recovery}");
        }
        finally
        {
            timer.Stop();
        }
    }

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> RestoreCheckpoint(
        RestoreOwnedSurfaceCheckpointRequestPayload request,
        string commandSessionId)
    {
        var faultError = ValidateFaultInjection(
            request.FaultInjectionStep,
            request.FaultInjectionConfirmation,
            "after-state-apply",
            "after-enable",
            "after-readback");
        if (faultError is not null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                faultError);
        }
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
        var original = reconstructed ? null : Capture(vessel!, record.TaskforceId, record.InitialStoresQuantity);

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
            InjectFailure(request.FaultInjectionStep, "after-state-apply");
            if (vessel!.Disabled)
            {
                vessel.Enable();
            }
            InjectFailure(request.FaultInjectionStep, "after-enable");
            record.TaskforceId = checkpoint.Unit.TaskforceId;
            record.RecoveryRequired = false;
            var realized = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
            AssertCheckpointRestored(checkpoint.Unit, realized);
            InjectFailure(request.FaultInjectionStep, "after-readback");
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
            var recovery = reconstructed
                ? "recoverable-suspended-owned-object"
                : "source-suspension-restored";
            if (vessel != null
                && string.Equals(vessel._campaignTag, request.EntityId, StringComparison.Ordinal))
            {
                try
                {
                    if (original is not null)
                    {
                        ApplyPortableState(vessel, original);
                    }
                    if (!vessel.Disabled)
                    {
                        vessel.Disable();
                    }
                    if (original is not null)
                    {
                        AssertPortableState(
                            original,
                            Capture(vessel, record.TaskforceId, record.InitialStoresQuantity),
                            expectedDisabled: true);
                    }
                }
                catch
                {
                    recovery = "recoverable-suspended-owned-object";
                    if (!vessel.Disabled)
                    {
                        vessel.Disable();
                    }
                }
                record.Vessel = vessel;
            }
            record.RecoveryRequired = recovery == "recoverable-suspended-owned-object";
            var injected = exception as InjectedMutationFailureException;
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                injected is null
                    ? ProtocolErrorCodes.MutationPreconditionFailed
                    : ProtocolErrorCodes.MutationInjectedFailure,
                $"operation=restore; recovery={recovery}; "
                + $"failure={exception.GetType().Name}: {exception.Message}",
                injected is null ? null : $"fault:restore:{injected.Step}:{recovery}");
        }
        finally
        {
            timer.Stop();
        }
    }

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> StageCheckpoint(
        StageOwnedSurfaceCheckpointRequestPayload request,
        string commandSessionId)
    {
        var faultError = ValidateFaultInjection(
            request.FaultInjectionStep,
            request.FaultInjectionConfirmation,
            "after-state-apply",
            "after-readback");
        if (faultError is not null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                faultError);
        }
        var replay = TryLifecycleReplay(
            GameCommandTypes.StageOwnedSurfaceCheckpoint,
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
        if (!vessel.Disabled || record.RecoveryRequired)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Migration checkpoint staging requires a healthy suspended destination object.");
        }
        var current = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
        var checkpointError = ValidateImportedCheckpoint(
            request.SourceCheckpoint,
            request.Context,
            request.EntityId,
            current.UnitTypeId,
            current.VariantId,
            current.LoadoutId,
            current.TaskforceId);
        if (checkpointError is not null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                checkpointError);
        }

        var originalCheckpoint = record.Checkpoint;
        var timer = Stopwatch.StartNew();
        try
        {
            ApplyPortableState(vessel, request.SourceCheckpoint.Unit);
            InjectFailure(request.FaultInjectionStep, "after-state-apply");
            var realized = Capture(vessel, record.TaskforceId, record.InitialStoresQuantity);
            AssertPortableState(request.SourceCheckpoint.Unit, realized, expectedDisabled: true);
            InjectFailure(request.FaultInjectionStep, "after-readback");
            record.Checkpoint = request.SourceCheckpoint;
            record.Revision++;
            timer.Stop();
            var result = LifecycleResult(
                "migration-checkpoint-staged",
                "owned-surface-unit-remains-suspended-with-verified-migration-checkpoint",
                request.Context,
                record,
                timer);
            result.SourceCheckpointId = request.SourceCheckpoint.CheckpointId;
            result.SourceCheckpointSha256 = request.SourceCheckpoint.CheckpointSha256;
            StoreLifecycleReplay(
                GameCommandTypes.StageOwnedSurfaceCheckpoint,
                request.Context,
                request,
                result);
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            var recovery = "destination-suspension-restored";
            try
            {
                ApplyPortableState(vessel, current);
                if (!vessel.Disabled)
                {
                    vessel.Disable();
                }
                AssertPortableState(current, Capture(
                    vessel,
                    record.TaskforceId,
                    record.InitialStoresQuantity), expectedDisabled: true);
                record.Checkpoint = originalCheckpoint;
            }
            catch
            {
                if (!vessel.Disabled)
                {
                    vessel.Disable();
                }
                recovery = "recoverable-suspended-owned-object";
            }
            record.RecoveryRequired = recovery == "recoverable-suspended-owned-object";
            var injected = exception as InjectedMutationFailureException;
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                injected is null
                    ? ProtocolErrorCodes.MutationPreconditionFailed
                    : ProtocolErrorCodes.MutationInjectedFailure,
                $"operation=stage-checkpoint; recovery={recovery}; "
                + $"failure={exception.GetType().Name}: {exception.Message}",
                injected is null ? null : $"fault:stage-checkpoint:{injected.Step}:{recovery}");
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
        Unit = Capture(record.Vessel!, record.TaskforceId, record.InitialStoresQuantity),
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

    private static string? ValidateSourceCheckpoint(
        MaterializeOwnedSurfaceUnitRequestPayload request)
    {
        var checkpoint = request.SourceCheckpoint;
        if (checkpoint is null)
        {
            return null;
        }
        return ValidateImportedCheckpoint(
            checkpoint,
            request.Context,
            request.EntityId,
            request.UnitTypeId,
            request.VariantId,
            request.LoadoutId,
            request.TaskforceId);
    }

    private static string? ValidateImportedCheckpoint(
        OwnedSurfaceCheckpointPayload? checkpoint,
        OwnedMutationContextPayload? context,
        string entityId,
        string unitTypeId,
        string variantId,
        string loadoutId,
        string taskforceId)
    {
        if (checkpoint is null || context is null)
        {
            return "The imported portable checkpoint and destination context are required.";
        }
        var unit = checkpoint.Unit;
        if (checkpoint.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(checkpoint.CheckpointId)
            || checkpoint.CheckpointId.Length > 256
            || string.IsNullOrWhiteSpace(checkpoint.CheckpointSha256)
            || checkpoint.CheckpointSha256.Length != 64
            || !checkpoint.CheckpointSha256.All(Uri.IsHexDigit)
            || !ValidScopedId(checkpoint.AssignmentId, "assignment:")
            || checkpoint.AuthorityEpoch < 1
            || checkpoint.AuthorityEpoch >= context.AuthorityEpoch
            || checkpoint.Revision < 0
            || string.Equals(
                checkpoint.AssignmentId,
                context.AssignmentId,
                StringComparison.Ordinal))
        {
            return "The imported portable checkpoint identity or predecessor fence is invalid.";
        }
        if (unit is null
            || !string.Equals(unit.StableUnitId, $"campaign-unit:{entityId}", StringComparison.Ordinal)
            || !string.Equals(unit.EntityId, entityId, StringComparison.Ordinal)
            || !string.Equals(unit.UnitTypeId, unitTypeId, StringComparison.Ordinal)
            || !string.Equals(unit.VariantId, variantId, StringComparison.Ordinal)
            || !string.Equals(unit.LoadoutId, loadoutId, StringComparison.Ordinal)
            || !string.Equals(unit.TaskforceId, taskforceId, StringComparison.Ordinal)
            || unit.Latitude is not double latitude
            || unit.Longitude is not double longitude
            || unit.HeadingDegrees is not double heading
            || !ValidPosition(latitude, longitude, heading)
            || unit.SpeedKnots is not double speed
            || !double.IsFinite(speed)
            || unit.Telegraph is < -1 or > 5
            || unit.IsDisabled
            || unit.IsDestroyed
            || !ValidFraction(unit.FuelFractionMilliunits)
            || !ValidFraction(unit.StoresFractionMilliunits)
            || !ValidFraction(unit.DamageFractionMilliunits))
        {
            return "The imported portable checkpoint does not match the requested supported active surface identity.";
        }
        var computedHash = ComputeCheckpointSha256(
            checkpoint.AssignmentId,
            checkpoint.AuthorityEpoch,
            checkpoint.Revision,
            unit);
        return string.Equals(
            computedHash,
            checkpoint.CheckpointSha256,
            StringComparison.OrdinalIgnoreCase)
            ? null
            : "The imported portable checkpoint state hash is invalid.";
    }

    private static bool ValidFraction(long? value) => value is null or >= 0 and <= 1_000_000;

    private static string ComputeCheckpointSha256(
        string assignmentId,
        long authorityEpoch,
        long revision,
        OwnedSurfaceUnitStatePayload unit) => CapabilityFingerprint.ComputeSha256(new[]
        {
            Field("schema-version", 1),
            Field("assignment-id", assignmentId),
            Field("authority-epoch", authorityEpoch),
            Field("revision", revision),
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
            Field("fuel-fraction-milliunits", unit.FuelFractionMilliunits),
            Field("stores-fraction-milliunits", unit.StoresFractionMilliunits),
            Field("damage-fraction-milliunits", unit.DamageFractionMilliunits),
            Field("disabled", unit.IsDisabled),
            Field("destroyed", unit.IsDestroyed),
        });

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
        var faultError = ValidateFaultInjection(
            request.FaultInjectionStep,
            request.FaultInjectionConfirmation,
            "after-owned-tag",
            "after-state-apply",
            "after-registry-add");
        if (faultError is not null)
        {
            return Invalid(faultError);
        }
        if (request.Context.DryRun && !string.IsNullOrEmpty(request.FaultInjectionStep))
        {
            return Invalid("Controlled materialization faults require a live mutation, not dryRun.");
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

    private static OwnedSurfaceUnitStatePayload Capture(
        Vessel vessel,
        string taskforceId,
        long initialStoresQuantity)
    {
        var parameters = vessel._obp;
        var fuelFraction = parameters is not null && parameters._maxFuel > 0
            ? FractionMilliunits(parameters._currentFuelMass / parameters._maxFuel)
            : null;
        var storesFraction = initialStoresQuantity > 0
            ? FractionMilliunits((double)StoreQuantity(vessel) / initialStoresQuantity)
            : null;
        var damageFraction = vessel._totalDamagePoints > 0
            ? FractionMilliunits(vessel._overallDamage / vessel._totalDamagePoints)
            : null;
        return new()
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
        FuelFractionMilliunits = fuelFraction,
        StoresFractionMilliunits = storesFraction,
        DamageFractionMilliunits = damageFraction,
        IsDisabled = vessel.Disabled,
        IsDestroyed = vessel.IsDestroyed,
        };
    }

    private static long StoreQuantity(Vessel vessel) => vessel.AmmunitionAmountDictionary.Values
        .Where(value => value > 0)
        .Sum(value => checked((long)value));

    private static long? FractionMilliunits(double value) => double.IsFinite(value)
        ? checked((long)Math.Round(Math.Clamp(value, 0d, 1d) * 1_000_000d))
        : null;

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
        OwnedSurfaceUnitStatePayload realized) =>
        AssertPortableState(expected, realized, expectedDisabled: false);

    private static void AssertPortableState(
        OwnedSurfaceUnitStatePayload expected,
        OwnedSurfaceUnitStatePayload realized,
        bool expectedDisabled)
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
            || realized.IsDisabled != expectedDisabled)
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

    private static OwnedSurfaceRecoveryReleaseResultPayload CloneRecoveryRelease(
        OwnedSurfaceRecoveryReleaseResultPayload value) =>
        JsonSerializer.Deserialize<OwnedSurfaceRecoveryReleaseResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException(
                "Could not clone recovery release result.");

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

    private static string? ValidateFaultInjection(
        string step,
        string confirmation,
        params string[] allowedSteps)
    {
        if (string.IsNullOrEmpty(step) && string.IsNullOrEmpty(confirmation))
        {
            return null;
        }
        if (!string.Equals(
            confirmation,
            FaultInjectionConfirmation,
            StringComparison.Ordinal))
        {
            return $"faultInjectionConfirmation must be exactly "
                + $"'{FaultInjectionConfirmation}' when a fault step is requested.";
        }
        return allowedSteps.Contains(step, StringComparer.Ordinal)
            ? null
            : $"faultInjectionStep '{step}' is not valid for this operation.";
    }

    private static void InjectFailure(string requestedStep, string currentStep)
    {
        if (string.Equals(requestedStep, currentStep, StringComparison.Ordinal))
        {
            throw new InjectedMutationFailureException(currentStep);
        }
    }

    private sealed class OwnedEntityRecord(
        string assignmentId,
        long authorityEpoch,
        long revision,
        string taskforceId,
        Vessel vessel,
        long initialStoresQuantity,
        bool recoveryRequired = false)
    {
        public string AssignmentId { get; set; } = assignmentId;
        public long AuthorityEpoch { get; set; } = authorityEpoch;
        public long Revision { get; set; } = revision;
        public string TaskforceId { get; set; } = taskforceId;
        public long InitialStoresQuantity { get; } = initialStoresQuantity;
        public Vessel? Vessel { get; set; } = vessel;
        public OwnedSurfaceCheckpointPayload? Checkpoint { get; set; }
        public bool RecoveryRequired { get; set; } = recoveryRequired;
    }

    private sealed record IdempotencyRecord(
        string RequestJson,
        MaterializeOwnedSurfaceUnitResultPayload Result);

    private sealed record LifecycleIdempotencyRecord(
        string RequestJson,
        OwnedSurfaceLifecycleResultPayload Result);

    private sealed record RecoveryReleaseIdempotencyRecord(
        string RequestJson,
        OwnedSurfaceRecoveryReleaseResultPayload Result);

    private sealed class InjectedMutationFailureException(string step)
        : Exception($"Controlled owned-mutation failure injected at '{step}'.")
    {
        public string Step { get; } = step;
    }

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
    public string? ErrorId { get; private init; }
    public T? Payload { get; private init; }

    public static MutationAdapterResult<T> Success(T payload) => new()
    {
        Succeeded = true,
        Payload = payload,
    };

    public static MutationAdapterResult<T> Failure(
        string code,
        string message,
        string? errorId = null) => new()
    {
        ErrorCode = code,
        ErrorMessage = message,
        ErrorId = errorId,
    };
}
