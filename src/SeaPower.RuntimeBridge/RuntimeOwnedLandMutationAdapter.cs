using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CrimeanTom.Protocol;

using SeaPower;
using SeapowerUI.ViewModels;

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

    private static readonly OwnedLandMaterializationProfilePayload[] PinnedProfiles =
    [
        Profile("usa_mbt_m60", "Ground"),
        Profile("usa_apc_m113", "Ground"),
        Profile("usa_aaa_vulcan", "Ground"),
        Profile("usa_mim-23_launcher", "Installation"),
        Profile("nato_ewr_station", "Installation"),
        Profile("nato_small_airbase", "Installation"),
    ];

    private static OwnedLandMaterializationProfilePayload[]? _catalogProfiles;
    private static int _catalogProfileSourceCount = -1;

    private static readonly Dictionary<string, OwnedLandRecord> OwnedEntities =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IdempotencyRecord> IdempotencyRecords =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LifecycleIdempotencyRecord> LifecycleIdempotencyRecords =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MaterializationRollbackIdempotencyRecord>
        MaterializationRollbackIdempotencyRecords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CommittedReleaseIdempotencyRecord>
        CommittedReleaseIdempotencyRecords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RecoveryReleaseIdempotencyRecord>
        RecoveryReleaseIdempotencyRecords = new(StringComparer.Ordinal);

    public static OwnedLandMaterializationProfilePayload[] SupportedProfiles() =>
        CatalogProfiles().Select(CloneProfile).ToArray();

    internal static bool HasOwnedEntity(string entityId) =>
        OwnedEntities.ContainsKey(entityId);

    internal static bool CanHandleRecoveryRelease(OwnedSurfaceEntityRequestPayload request) =>
        HasOwnedEntity(request.EntityId)
        || request.Context is { } context
            && RecoveryReleaseIdempotencyRecords.ContainsKey(
                LifecycleKey(GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery, context));

    internal static bool CanHandleMaterializationRollback(OwnedSurfaceEntityRequestPayload request) =>
        HasOwnedEntity(request.EntityId)
        || request.Context is { } context
            && MaterializationRollbackIdempotencyRecords.ContainsKey(
                LifecycleKey(GameCommandTypes.RollbackIncompleteOwnedMaterialization, context));

    internal static bool CanHandleCommittedRelease(OwnedSurfaceEntityRequestPayload request) =>
        HasOwnedEntity(request.EntityId)
        || request.Context is { } context
            && CommittedReleaseIdempotencyRecords.ContainsKey(
                LifecycleKey(GameCommandTypes.ReleaseCommittedOwnedResource, context));

    internal static bool Supports(string unitTypeId, string variantId, string loadoutId) =>
        CatalogProfiles().Any(profile =>
            string.Equals(profile.UnitTypeId, unitTypeId, StringComparison.Ordinal)
            && string.Equals(profile.VariantId, variantId, StringComparison.Ordinal)
            && string.Equals(profile.LoadoutId, loadoutId, StringComparison.Ordinal));

    private static OwnedLandMaterializationProfilePayload[] CatalogProfiles()
    {
        var unitData = Singleton<UnitMetadataProvider>.Instance?.UnitData;
        if (unitData is null || unitData.Count == 0)
        {
            return PinnedProfiles;
        }
        if (_catalogProfiles is { Length: > 0 }
            && _catalogProfileSourceCount == unitData.Count)
        {
            return _catalogProfiles;
        }

        var profiles = new List<OwnedLandMaterializationProfilePayload>(PinnedProfiles);
        var identities = PinnedProfiles
            .Select(ProfileIdentity)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in unitData
            .Where(item => item.Value.Item2 is not null
                && item.Value.Item2._objectType == ObjectBase.ObjectType.LandUnit)
            .OrderBy(item => item.Key.Item2, StringComparer.Ordinal))
        {
            var metadata = entry.Value.Item2;
            var variantId = metadata._variants.ContainsKey("Default")
                ? "Default"
                : metadata._variants.Keys.OrderBy(value => value, StringComparer.Ordinal).FirstOrDefault()
                    ?? "Default";
            var loadoutIds = metadata._loadouts?.Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray() ?? [];
            if (loadoutIds.Length == 0)
            {
                loadoutIds = ["Default"];
            }

            foreach (var loadoutId in loadoutIds)
            {
                var profile = Profile(entry.Key.Item2, "Ground", variantId, loadoutId);
                if (identities.Add(ProfileIdentity(profile)))
                {
                    profiles.Add(profile);
                }
            }
        }

        _catalogProfiles = profiles.ToArray();
        _catalogProfileSourceCount = unitData.Count;
        return _catalogProfiles;
    }

    private static string ProfileIdentity(OwnedLandMaterializationProfilePayload profile) =>
        $"{profile.UnitTypeId}\n{profile.VariantId}\n{profile.LoadoutId}";

    public static void EndSession(string sessionId)
    {
        // A pipe session is transport identity, not mutation identity. Keep
        // effect-keyed replay records across reconnects; owned-record release
        // is the lifecycle boundary that may evict them.
    }

    public static MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload> MaterializeLegacy(
        MaterializeOwnedSurfaceUnitRequestPayload request,
        string commandSessionId)
    {
        var materialization = request;
        if (request.SourceCheckpoint is { } sourceCheckpoint)
        {
            var checkpointError = ValidateImportedCheckpoint(
                sourceCheckpoint,
                request.EntityId,
                request.UnitTypeId,
                request.VariantId,
                request.LoadoutId,
                request.TaskforceId);
            if (checkpointError is not null)
            {
                return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    checkpointError);
            }
            materialization = new MaterializeOwnedSurfaceUnitRequestPayload
            {
                Context = request.Context,
                EntityId = request.EntityId,
                UnitTypeId = request.UnitTypeId,
                VariantId = request.VariantId,
                LoadoutId = request.LoadoutId,
                TaskforceId = request.TaskforceId,
                Latitude = sourceCheckpoint.Unit.Latitude!.Value,
                Longitude = sourceCheckpoint.Unit.Longitude!.Value,
                HeadingDegrees = sourceCheckpoint.Unit.HeadingDegrees!.Value,
                Telegraph = sourceCheckpoint.Unit.Telegraph,
                SourceCheckpoint = sourceCheckpoint,
            };
        }
        var result = Materialize(new MaterializeOwnedLandUnitRequestPayload
        {
            Context = materialization.Context,
            EntityId = materialization.EntityId,
            UnitTypeId = materialization.UnitTypeId,
            VariantId = materialization.VariantId,
            LoadoutId = materialization.LoadoutId,
            TaskforceId = materialization.TaskforceId,
            Latitude = materialization.Latitude,
            Longitude = materialization.Longitude,
            HeadingDegrees = materialization.HeadingDegrees,
            Telegraph = materialization.Telegraph,
        }, commandSessionId);
        if (!result.Succeeded)
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                result.ErrorCode,
                result.ErrorMessage,
                result.ErrorId);
        }
        var value = result.Payload!;
        if (request.SourceCheckpoint is not null
            && OwnedEntities.TryGetValue(request.EntityId, out var importedRecord))
        {
            importedRecord.Checkpoint = request.SourceCheckpoint;
        }
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
        var selected = Singleton<RenderPosition>.InstanceExists()
            ? Singleton<RenderPosition>.Instance.getSelectedObject()
            : null;
        var entries = OwnedEntities
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var record = pair.Value;
                var landUnit = record.LandUnit;
                if (!IsNativeObjectAvailable(landUnit))
                {
                    landUnit = null;
                }
                var lifecycle = ReferenceEquals(landUnit, null)
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
                    MaterializationRollbackPending =
                        record.RollbackMode != MaterializationRollbackMode.None,
                    NativeInitialized = RuntimeNativeUnitReadiness.IsInitialized(landUnit),
                    AcceptsPlayerOrders = RuntimeNativeUnitReadiness.AcceptsPlayerOrders(landUnit),
                    HasNativeControl = landUnit != null && landUnit._hasControl,
                    NativeStoresInitialized = RuntimeNativeUnitReadiness.HasInitializedStores(landUnit),
                    IsAircraftInFlight = true,
                    UnitTypeId = landUnit == null ? string.Empty : landUnit._obp?._objectIniName ?? string.Empty,
                    VariantId = landUnit == null ? string.Empty : landUnit._obp?._objectVariantReference ?? string.Empty,
                    LoadoutId = landUnit == null ? string.Empty : landUnit._obp?._loadoutVariant ?? string.Empty,
                    AmmunitionQuantity = landUnit == null
                        ? 0
                        : landUnit.AmmunitionAmountDictionary.Values
                            .Where(quantity => quantity > 0)
                            .Sum(quantity => checked((long)quantity)),
                    GameUniqueId = landUnit == null ? null : landUnit.UniqueID,
                    HasPosition = landUnit is not null,
                    Latitude = landUnit?._geoPosition._latitude ?? 0d,
                    Longitude = landUnit?._geoPosition._longitude ?? 0d,
                    HeadingDegrees = landUnit?.getHeading() ?? 0d,
                    HorizontalDistanceFromOriginMeters = landUnit is null
                        ? 0d
                        : HorizontalDistanceFromOriginMeters(landUnit),
                    IsSelectedByPlayer = landUnit is not null && ReferenceEquals(selected, landUnit),
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
            MemoryReclamationSupported = true,
            SchedulerPolicy = "active-objects-consume-capacity; committed-suspended-objects-are-reclaimed",
            Entities = entries,
        };
    }

    private static bool IsNativeObjectAvailable(LandUnit? unit)
    {
        if (ReferenceEquals(unit, null))
        {
            return false;
        }

        try
        {
            return unit != null && unit.gameObject != null && unit.transform != null;
        }
        catch (NullReferenceException)
        {
            return false;
        }
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

        var requestJson = IdempotencyRequestJson(request, request.Context);
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

        if (OwnedEntities.TryGetValue(request.EntityId, out var retained))
        {
            return RematerializeRetained(
                request,
                commandSessionId,
                requestJson,
                idempotencyKey,
                retained);
        }

        if (RuntimeOwnedMutationAdapter.HasOwnedEntity(request.EntityId)
            || Singleton<ObjectsManager>.Instance._listOfAllObjects.Any(candidate =>
                candidate != null
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
            var taskforce = RuntimeOwnedMutationAdapter.ResolveTaskforce(request.TaskforceId)!;
            created = Singleton<ObjectsManager>.Instance.createLandUnit(
                request.VariantId,
                request.LoadoutId,
                request.UnitTypeId,
                Singleton<ObjectsManager>.Instance._listOfAllObjects.Count + 1,
                Singleton<SceneCreator>.Instance.GeoCenterPosition,
                position,
                request.Telegraph,
                heading,
                taskforce);
            if (created == null)
            {
                return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    "Sea Power's supported land-unit constructor returned no unit.");
            }

            created._campaignTag = request.EntityId;
            RuntimeOwnedMutationAdapter.ApplyDisplayName(created, request.DisplayName);
            created._geoPosition = position;
            var local = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
            var terrainHeight = (float)terrain.TerrainHeightUnity.Value;
            local.y = terrainHeight + created._groundOffset + 0.001f;
            created.transform.position = local;
            created.transform.eulerAngles = heading;
            created.DesiredAltitude.Value = local.y;
            created.setTelegraph(request.Telegraph);
            ApplyTacticalPosture(created, request);
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
            var ownedRecord = new OwnedLandRecord(
                request.Context.AssignmentId,
                request.Context.AuthorityEpoch,
                revision,
                request.TaskforceId,
                request.UnitTypeId,
                request.VariantId,
                request.LoadoutId,
                created)
            {
                RollbackMode = MaterializationRollbackMode.DestroyNewObject,
            };
            OwnedEntities.Add(
                request.EntityId,
                ownedRecord);
            stopwatch.Stop();
            result.SynchronousDurationMicroseconds = ElapsedMicroseconds(stopwatch);
            EvictMaterializationRollbackForAssignment(request.Context.AssignmentId);
            IdempotencyRecords.Add(
                idempotencyKey,
                new(commandSessionId, requestJson, Clone(result)));
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            if (created != null
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
                OwnedEntities[request.EntityId].RollbackMode =
                    MaterializationRollbackMode.DestroyNewObject;
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

    private static MutationAdapterResult<MaterializeOwnedLandUnitResultPayload> RematerializeRetained(
        MaterializeOwnedLandUnitRequestPayload request,
        string commandSessionId,
        string requestJson,
        string idempotencyKey,
        OwnedLandRecord record)
    {
        var landUnit = record.LandUnit;
        var checkpoint = record.Checkpoint;
        if (record.RecoveryRequired
            || landUnit == null
            || !landUnit.Disabled
            || checkpoint?.Unit is null
            || checkpoint.Unit.IsDisabled
            || checkpoint.Unit.IsDestroyed
            || request.Context.ExpectedRevision != 0
            || request.Context.AuthorityEpoch <= record.AuthorityEpoch
            || string.Equals(request.Context.AssignmentId, record.AssignmentId, StringComparison.Ordinal)
            || !string.Equals(landUnit._campaignTag, request.EntityId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Unit.EntityId, request.EntityId, StringComparison.Ordinal)
            || !string.Equals(record.UnitTypeId, request.UnitTypeId, StringComparison.Ordinal)
            || !string.Equals(record.VariantId, request.VariantId, StringComparison.Ordinal)
            || !string.Equals(record.LoadoutId, request.LoadoutId, StringComparison.Ordinal)
            || !string.Equals(record.TaskforceId, request.TaskforceId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                "The retained owned land object is not a checkpointed suspended predecessor of this higher-epoch assignment.");
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
                $"Retained land rematerialization requires finalized land terrain; probe={terrain.Postcondition}.");
        }
        var predicted = CapturePredicted(request, terrain);
        if (request.Context.DryRun)
        {
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Success(new()
            {
                Outcome = "validated-retained-rematerialization",
                Postcondition = "retained-owned-land-unit-rematerialization-valid",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = request.Context.ExpectedRevision,
                IdempotencyKey = request.Context.IdempotencyKey,
                DryRun = true,
                RealizedUnit = predicted,
                Terrain = terrain,
                RecoverableOwnedObject = true,
                UnsupportedFields = UnsupportedFields(),
            });
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
            landUnit.Enable();
            var realized = Capture(landUnit, request, terrain);
            AssertReadback(request, realized);

            var revision = request.Context.ExpectedRevision + 1;
            record.AssignmentId = request.Context.AssignmentId;
            record.AuthorityEpoch = request.Context.AuthorityEpoch;
            record.Revision = revision;
            record.TaskforceId = request.TaskforceId;
            record.RecoveryRequired = false;
            record.RollbackMode = MaterializationRollbackMode.RestoreRetainedPredecessor;
            timer.Stop();
            var result = new MaterializeOwnedLandUnitResultPayload
            {
                Outcome = "rematerialized-retained",
                Postcondition = "retained-owned-land-unit-rebound-enabled-above-terrain-and-read-back",
                AssignmentId = request.Context.AssignmentId,
                AuthorityEpoch = request.Context.AuthorityEpoch,
                Revision = revision,
                IdempotencyKey = request.Context.IdempotencyKey,
                RecoverableOwnedObject = true,
                SynchronousDurationMicroseconds = ElapsedMicroseconds(timer),
                RealizedUnit = realized,
                Terrain = terrain,
                UnsupportedFields = UnsupportedFields(),
            };
            EvictMaterializationRollbackForAssignment(request.Context.AssignmentId);
            IdempotencyRecords.Add(
                idempotencyKey,
                new(commandSessionId, requestJson, Clone(result)));
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            var recovery = "source-suspension-restored";
            try
            {
                ApplyPortableState(landUnit, original);
            }
            catch
            {
                recovery = "recoverable-suspended-owned-object";
                record.RecoveryRequired = true;
                if (!landUnit.Disabled)
                {
                    landUnit.Disable();
                }
            }
            return MutationAdapterResult<MaterializeOwnedLandUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"operation=rematerialize-retained-land; recovery={recovery}; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    private static double HorizontalDistanceFromOriginMeters(ObjectBase unit)
    {
        const double metersPerUnityUnit = 67.2d;
        var position = unit.transform.position;
        return Math.Sqrt(position.x * position.x + position.z * position.z)
            * metersPerUnityUnit;
    }

    public static MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>
        RollbackIncompleteMaterialization(
            OwnedSurfaceEntityRequestPayload request,
            string commandSessionId)
    {
        var context = request.Context;
        if (context is not null
            && RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId)
            && string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            var replayKey = LifecycleKey(
                GameCommandTypes.RollbackIncompleteOwnedMaterialization,
                context);
            if (MaterializationRollbackIdempotencyRecords.TryGetValue(replayKey, out var prior))
            {
                var replayJson = IdempotencyRequestJson(request, context);
                if (!string.Equals(prior.RequestJson, replayJson, StringComparison.Ordinal))
                {
                    return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                        ProtocolErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used with a different land materialization rollback payload.");
                }
                var replay = CloneMaterializationRollback(prior.Result);
                replay.IdempotentReplay = true;
                return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Success(replay);
            }
        }

        if (!TryResolve(
                context,
                request.EntityId,
                commandSessionId,
                requirePaused: false,
                out var record,
                out var errorCode,
                out var errorMessage,
                requireNativeObject: false))
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                errorCode,
                errorMessage);
        }
        if (record!.RollbackMode == MaterializationRollbackMode.None
            || record.LandUnit == null)
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Only an uncommitted land materialization with a native object can be rolled back.");
        }

        var key = LifecycleKey(GameCommandTypes.RollbackIncompleteOwnedMaterialization, context!);
        var requestJson = IdempotencyRequestJson(request, context);
        var failedAssignmentId = record.AssignmentId;
        var failedAuthorityEpoch = record.AuthorityEpoch;
        var failedRevision = record.Revision;
        var landUnit = record.LandUnit!;
        var rollbackMode = record.RollbackMode;
        var removed = false;
        var postcondition = string.Empty;
        var timer = Stopwatch.StartNew();
        try
        {
            if (rollbackMode == MaterializationRollbackMode.RestoreRetainedPredecessor)
            {
                var checkpoint = record.Checkpoint
                    ?? throw new InvalidOperationException(
                        "The retained land predecessor has no portable checkpoint to restore.");
                ApplyPortableState(landUnit, checkpoint.Unit);
                if (!landUnit.Disabled)
                {
                    landUnit.Disable();
                }
                record.AssignmentId = checkpoint.AssignmentId;
                record.AuthorityEpoch = checkpoint.AuthorityEpoch;
                record.Revision = checkpoint.Revision;
                record.TaskforceId = checkpoint.Unit.TaskforceId;
                record.RecoveryRequired = false;
                record.RollbackMode = MaterializationRollbackMode.None;
                postcondition = "retained-land-predecessor-restored-and-suspended";
            }
            else
            {
                landUnit._campaignTag = string.Empty;
                if (!landUnit.IsDestroyed)
                {
                    var taskforce = landUnit._taskforce;
                    landUnit.destroyObject(
                        keepInFlightEffectAlive: false,
                        score: false,
                        TacView.TCEvent.LeftArea);
                    taskforce?._taskforceDisabledUnits?.RemoveAll(candidate =>
                        ReferenceEquals(candidate, landUnit));
                }
                record.LandUnit = null;
                OwnedEntities.Remove(request.EntityId);
                removed = true;
                postcondition = "uncommitted-native-land-object-removed-and-owned-record-released";
            }

            EvictIdempotencyForAssignment(failedAssignmentId);
            timer.Stop();
            var result = new RollbackIncompleteOwnedMaterializationResultPayload
            {
                Outcome = "rolled-back",
                Postcondition = postcondition,
                EntityId = request.EntityId,
                AssignmentId = failedAssignmentId,
                AuthorityEpoch = failedAuthorityEpoch,
                Revision = failedRevision,
                IdempotencyKey = context!.IdempotencyKey,
                NativeObjectRemoved = removed,
                SynchronousDurationMicroseconds = ElapsedMicroseconds(timer),
            };
            MaterializationRollbackIdempotencyRecords.Add(
                key,
                new(requestJson, CloneMaterializationRollback(result)));
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            if (landUnit != null && !landUnit.IsDestroyed)
            {
                landUnit._campaignTag = request.EntityId;
                record.RecoveryRequired = true;
                record.RollbackMode = rollbackMode;
                if (!landUnit.Disabled)
                {
                    landUnit.Disable();
                }
            }
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "operation=rollback-incomplete-land-materialization; "
                + "recovery=recoverable-suspended-owned-object; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    public static MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>
        ReleaseCommittedResource(
            OwnedSurfaceEntityRequestPayload request,
            string commandSessionId)
    {
        var context = request.Context;
        if (context is not null
            && RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId)
            && string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            var replayKey = LifecycleKey(GameCommandTypes.ReleaseCommittedOwnedResource, context);
            if (CommittedReleaseIdempotencyRecords.TryGetValue(replayKey, out var prior))
            {
                var replayJson = IdempotencyRequestJson(request, context);
                if (!string.Equals(prior.RequestJson, replayJson, StringComparison.Ordinal))
                {
                    return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                        ProtocolErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used with a different committed land-resource release payload.");
                }
                var replay = CloneCommittedRelease(prior.Result);
                replay.IdempotentReplay = true;
                return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Success(replay);
            }
        }

        if (!TryResolve(
                context,
                request.EntityId,
                commandSessionId,
                requirePaused: false,
                out var record,
                out var errorCode,
                out var errorMessage,
                requireNativeObject: false))
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                errorCode,
                errorMessage);
        }
        var landUnit = record!.LandUnit;
        if (record.RecoveryRequired
            || record.Checkpoint is null
            || record.RollbackMode != MaterializationRollbackMode.None
            || landUnit != null && !landUnit.Disabled)
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Committed release requires a healthy suspended owned land record with an authoritative checkpoint.");
        }

        var key = LifecycleKey(GameCommandTypes.ReleaseCommittedOwnedResource, context!);
        var requestJson = IdempotencyRequestJson(request, context);
        var timer = Stopwatch.StartNew();
        try
        {
            var nativeObjectRemoved = landUnit != null;
            if (landUnit != null)
            {
                landUnit._campaignTag = string.Empty;
                if (!landUnit.IsDestroyed)
                {
                    var taskforce = landUnit._taskforce;
                    landUnit.destroyObject(
                        keepInFlightEffectAlive: false,
                        score: false,
                        TacView.TCEvent.LeftArea);
                    taskforce?._taskforceDisabledUnits?.RemoveAll(candidate =>
                        ReferenceEquals(candidate, landUnit));
                }
            }
            record.LandUnit = null;
            OwnedEntities.Remove(request.EntityId);
            EvictIdempotencyForAssignment(record.AssignmentId);
            EvictTerminalReceiptsForEntity(request.EntityId);
            timer.Stop();
            var result = new ReleaseCommittedOwnedResourceResultPayload
            {
                Outcome = "released",
                Postcondition = nativeObjectRemoved
                    ? "committed-native-land-object-removed-and-owned-record-released"
                    : "committed-detached-owned-land-record-released",
                EntityId = request.EntityId,
                AssignmentId = record.AssignmentId,
                AuthorityEpoch = record.AuthorityEpoch,
                Revision = record.Revision,
                IdempotencyKey = context!.IdempotencyKey,
                NativeObjectRemoved = nativeObjectRemoved,
                SynchronousDurationMicroseconds = ElapsedMicroseconds(timer),
            };
            CommittedReleaseIdempotencyRecords.Add(
                key,
                new(requestJson, CloneCommittedRelease(result)));
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            if (landUnit != null && !landUnit.IsDestroyed)
            {
                landUnit._campaignTag = request.EntityId;
                record.LandUnit = landUnit;
                if (!landUnit.Disabled)
                {
                    landUnit.Disable();
                }
            }
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "operation=release-committed-owned-land-resource; recovery=suspended-owned-object-retained; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    public static MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>
        ReleaseDetachedRecovery(
            OwnedSurfaceEntityRequestPayload request,
            string commandSessionId)
    {
        var context = request.Context;
        if (context is not null
            && RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId)
            && string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            var replayKey = LifecycleKey(
                GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery,
                context);
            if (RecoveryReleaseIdempotencyRecords.TryGetValue(replayKey, out var prior))
            {
                var replayJson = IdempotencyRequestJson(request, context);
                if (!string.Equals(prior.RequestJson, replayJson, StringComparison.Ordinal))
                {
                    return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                        ProtocolErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used with a different detached land-recovery release payload.");
                }
                var replay = CloneRecoveryRelease(prior.Result);
                replay.IdempotentReplay = true;
                return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Success(replay);
            }
        }

        if (!TryResolve(
                context,
                request.EntityId,
                commandSessionId,
                requirePaused: false,
                out var record,
                out var errorCode,
                out var errorMessage,
                requireNativeObject: false))
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                errorCode,
                errorMessage);
        }
        if (record!.LandUnit != null || record.Checkpoint is not null)
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Detached land recovery release requires an absent native object and no checkpoint.");
        }

        var key = LifecycleKey(GameCommandTypes.ReleaseDetachedOwnedSurfaceRecovery, context!);
        var requestJson = IdempotencyRequestJson(request, context);
        OwnedEntities.Remove(request.EntityId);
        EvictIdempotencyForAssignment(record.AssignmentId);
        EvictTerminalReceiptsForEntity(request.EntityId);
        var result = new OwnedSurfaceRecoveryReleaseResultPayload
        {
            Outcome = "released",
            Postcondition = "detached-owned-land-recovery-record-released-without-unity-mutation",
            EntityId = request.EntityId,
            AssignmentId = record.AssignmentId,
            AuthorityEpoch = record.AuthorityEpoch,
            Revision = record.Revision,
            IdempotencyKey = context!.IdempotencyKey,
        };
        RecoveryReleaseIdempotencyRecords.Add(
            key,
            new(requestJson, CloneRecoveryRelease(result)));
        return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Success(result);
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
                out var errorMessage,
                requireNativeObject: false))
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                errorCode,
                errorMessage);
        }
        var ownedRecord = record!;
        if (ownedRecord.RecoveryRequired)
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A partial land materialization object cannot produce an authoritative checkpoint.");
        }
        if (ownedRecord.LandUnit == null)
        {
            if (ownedRecord.Checkpoint is { } detachedCheckpoint
                && detachedCheckpoint.AssignmentId == ownedRecord.AssignmentId
                && detachedCheckpoint.AuthorityEpoch == ownedRecord.AuthorityEpoch
                && detachedCheckpoint.Revision == ownedRecord.Revision)
            {
                return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Success(
                    detachedCheckpoint);
            }
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A detached owned land record has no checkpoint for its current active revision.");
        }
        var landUnit = ownedRecord.LandUnit;
        if (landUnit.IsDestroyed)
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Only an active, healthy owned land unit can produce a checkpoint.");
        }
        if (landUnit.Disabled)
        {
            if (ownedRecord.Checkpoint is { } retainedCheckpoint
                && retainedCheckpoint.AssignmentId == ownedRecord.AssignmentId
                && retainedCheckpoint.AuthorityEpoch == ownedRecord.AuthorityEpoch
                && retainedCheckpoint.Revision == ownedRecord.Revision - 1)
            {
                return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Success(
                    retainedCheckpoint);
            }
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A suspended owned land unit has no checkpoint for its immediately preceding active revision.");
        }
        var unit = CapturePortable(request.EntityId, ownedRecord);
        var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hash = ComputeCheckpointSha256(
            ownedRecord.AssignmentId,
            ownedRecord.AuthorityEpoch,
            ownedRecord.Revision,
            unit);
        var checkpoint = new OwnedSurfaceCheckpointPayload
        {
            CheckpointId = $"checkpoint:{request.EntityId}:{ownedRecord.Revision}:{hash[..12]}",
            CheckpointSha256 = hash,
            AssignmentId = ownedRecord.AssignmentId,
            AuthorityEpoch = ownedRecord.AuthorityEpoch,
            Revision = ownedRecord.Revision,
            CapturedAtUnixMilliseconds = capturedAt,
            Unit = unit,
            UnsupportedFields = UnsupportedFields(),
        };
        ownedRecord.Checkpoint = checkpoint;
        ownedRecord.RollbackMode = MaterializationRollbackMode.None;
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
                commandSessionId,
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
                commandSessionId,
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

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> RestoreCheckpoint(
        RestoreOwnedSurfaceCheckpointRequestPayload request,
        string commandSessionId)
    {
        if (TryLifecycleReplay(
                GameCommandTypes.RestoreOwnedSurfaceCheckpoint,
                request.Context,
                request,
                commandSessionId,
                out var replay))
        {
            return replay!;
        }
        if (!string.IsNullOrWhiteSpace(request.FaultInjectionStep))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Fault injection is not supported by the verified land lifecycle seam.");
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
        var checkpoint = record!.Checkpoint;
        if (checkpoint is null
            || !string.Equals(checkpoint.CheckpointId, request.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(
                checkpoint.CheckpointSha256,
                request.CheckpointSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The requested portable checkpoint is not retained by this land entity.");
        }
        var checkpointError = ValidateImportedCheckpoint(
            checkpoint,
            request.EntityId,
            record.UnitTypeId,
            record.VariantId,
            record.LoadoutId,
            record.TaskforceId);
        if (checkpointError is not null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                checkpointError);
        }
        var landUnit = record.LandUnit!;
        if (!landUnit.Disabled || record.RecoveryRequired || landUnit.IsDestroyed)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Land checkpoint restoration requires a healthy suspended owned object.");
        }

        var timer = Stopwatch.StartNew();
        try
        {
            ApplyPortableState(landUnit, checkpoint.Unit);
            var realized = CapturePortable(request.EntityId, record);
            AssertPortableState(checkpoint.Unit, realized, expectedDisabled: false);
            record.RecoveryRequired = false;
            record.Revision++;
            timer.Stop();
            var result = LifecycleResult(
                "restored-from-checkpoint",
                "owned-land-unit-reenabled-from-terrain-validated-portable-checkpoint",
                request.Context,
                record,
                request.EntityId,
                timer);
            result.SourceCheckpointId = checkpoint.CheckpointId;
            result.SourceCheckpointSha256 = checkpoint.CheckpointSha256;
            StoreLifecycleReplay(
                GameCommandTypes.RestoreOwnedSurfaceCheckpoint,
                request.Context,
                request,
                commandSessionId,
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
                "operation=restore-land; recovery=recoverable-suspended-owned-object; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
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
        if (TryLifecycleReplay(
                GameCommandTypes.StageOwnedSurfaceCheckpoint,
                request.Context,
                request,
                commandSessionId,
                out var replay))
        {
            return replay!;
        }
        if (!string.IsNullOrWhiteSpace(request.FaultInjectionStep))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Fault injection is not supported by the verified land lifecycle seam.");
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
        if (!landUnit.Disabled || record.RecoveryRequired || landUnit.IsDestroyed)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Land migration staging requires a healthy suspended destination object.");
        }
        var checkpointError = ValidateImportedCheckpoint(
            request.SourceCheckpoint,
            request.EntityId,
            record.UnitTypeId,
            record.VariantId,
            record.LoadoutId,
            record.TaskforceId);
        if (checkpointError is not null)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                checkpointError);
        }

        var original = CapturePortable(request.EntityId, record);
        var originalCheckpoint = record.Checkpoint;
        var timer = Stopwatch.StartNew();
        try
        {
            ApplyPortablePlacement(landUnit, request.SourceCheckpoint.Unit);
            if (!landUnit.Disabled)
            {
                landUnit.Disable();
            }
            var realized = CapturePortable(request.EntityId, record);
            AssertPortableState(request.SourceCheckpoint.Unit, realized, expectedDisabled: true);
            record.Checkpoint = request.SourceCheckpoint;
            record.Revision++;
            timer.Stop();
            var result = LifecycleResult(
                "migration-checkpoint-staged",
                "owned-land-unit-remains-suspended-with-terrain-validated-migration-checkpoint",
                request.Context,
                record,
                request.EntityId,
                timer);
            result.SourceCheckpointId = request.SourceCheckpoint.CheckpointId;
            result.SourceCheckpointSha256 = request.SourceCheckpoint.CheckpointSha256;
            StoreLifecycleReplay(
                GameCommandTypes.StageOwnedSurfaceCheckpoint,
                request.Context,
                request,
                commandSessionId,
                result);
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Success(result);
        }
        catch (Exception exception)
        {
            try
            {
                ApplyPortablePlacement(landUnit, original);
                if (!landUnit.Disabled)
                {
                    landUnit.Disable();
                }
                record.Checkpoint = originalCheckpoint;
            }
            catch
            {
                record.RecoveryRequired = true;
            }
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "operation=stage-land-checkpoint; recovery=destination-suspension-restored; "
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
        out string errorMessage,
        bool requireNativeObject = true)
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
        if (requireNativeObject
            && (record.LandUnit == null
                || !string.Equals(record.LandUnit._campaignTag, entityId, StringComparison.Ordinal)))
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
        ApplyPortablePlacement(landUnit, state);
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

    private static void ApplyPortablePlacement(
        LandUnit landUnit,
        OwnedSurfaceUnitStatePayload state)
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
    }

    private static string ComputeCheckpointSha256(
        string assignmentId,
        long authorityEpoch,
        long revision,
        OwnedSurfaceUnitStatePayload unit)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            AssignmentId = assignmentId,
            AuthorityEpoch = authorityEpoch,
            Revision = revision,
            Unit = unit,
        }, JsonOptions);
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return string.Concat(bytes.Select(value => value.ToString("x2")));
    }

    private static string? ValidateImportedCheckpoint(
        OwnedSurfaceCheckpointPayload checkpoint,
        string entityId,
        string unitTypeId,
        string variantId,
        string loadoutId,
        string taskforceId)
    {
        var unit = checkpoint.Unit;
        if (unit is null
            || !string.Equals(unit.EntityId, entityId, StringComparison.Ordinal)
            || !string.Equals(unit.StableUnitId, $"campaign-unit:{entityId}", StringComparison.Ordinal)
            || !string.Equals(unit.UnitTypeId, unitTypeId, StringComparison.Ordinal)
            || !string.Equals(unit.VariantId, variantId, StringComparison.Ordinal)
            || !string.Equals(unit.LoadoutId, loadoutId, StringComparison.Ordinal)
            || !string.Equals(unit.TaskforceId, taskforceId, StringComparison.Ordinal)
            || unit.Latitude is not double latitude
            || unit.Longitude is not double longitude
            || unit.HeadingDegrees is not double heading
            || !double.IsFinite(latitude)
            || !double.IsFinite(longitude)
            || !double.IsFinite(heading)
            || latitude is < -90d or > 90d
            || longitude is < -180d or >= 180d
            || unit.Telegraph is < -1 or > 5
            || unit.IsDisabled
            || unit.IsDestroyed)
        {
            return "The imported checkpoint does not contain a complete active state for this land profile.";
        }
        var expectedHash = ComputeCheckpointSha256(
            checkpoint.AssignmentId,
            checkpoint.AuthorityEpoch,
            checkpoint.Revision,
            unit);
        var expectedId = $"checkpoint:{entityId}:{checkpoint.Revision}:{expectedHash[..12]}";
        if (!string.Equals(checkpoint.CheckpointSha256, expectedHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(checkpoint.CheckpointId, expectedId, StringComparison.Ordinal))
        {
            return "The imported land checkpoint identity or canonical hash is invalid.";
        }
        return null;
    }

    private static void AssertPortableState(
        OwnedSurfaceUnitStatePayload expected,
        OwnedSurfaceUnitStatePayload realized,
        bool expectedDisabled)
    {
        if (expected.Latitude is not double expectedLatitude
            || expected.Longitude is not double expectedLongitude
            || expected.HeadingDegrees is not double expectedHeading
            || realized.Latitude is not double realizedLatitude
            || realized.Longitude is not double realizedLongitude
            || realized.HeadingDegrees is not double realizedHeading
            || Math.Abs(expectedLatitude - realizedLatitude) > CoordinateToleranceDegrees
            || Math.Abs(expectedLongitude - realizedLongitude) > CoordinateToleranceDegrees
            || Math.Abs(NormalizeHeading(expectedHeading) - NormalizeHeading(realizedHeading)) > 0.01d
            || realized.Telegraph != expected.Telegraph
            || realized.IsDisabled != expectedDisabled
            || realized.IsDestroyed)
        {
            throw new InvalidOperationException("The portable land checkpoint failed native readback.");
        }
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
        var requestJson = IdempotencyRequestJson(request, context);
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
        string commandSessionId,
        OwnedSurfaceLifecycleResultPayload result) =>
        LifecycleIdempotencyRecords.Add(
            LifecycleKey(command, context),
            new(
                commandSessionId,
                IdempotencyRequestJson(request, context),
                CloneLifecycle(result)));

    private static string IdempotencyRequestJson<T>(
        T request,
        OwnedMutationContextPayload? context)
    {
        if (context is null)
        {
            return JsonSerializer.Serialize(request, JsonOptions);
        }

        var sessionId = context.SessionId;
        try
        {
            context.SessionId = string.Empty;
            return JsonSerializer.Serialize(request, JsonOptions);
        }
        finally
        {
            context.SessionId = sessionId;
        }
    }

    private static string LifecycleKey(string command, OwnedMutationContextPayload context) =>
        string.Join("|", command, context.AssignmentId, context.IdempotencyKey);

    private static void EvictIdempotencyForAssignment(string assignmentId)
    {
        RemoveWhere(
            IdempotencyRecords,
            item => string.Equals(
                item.Result.AssignmentId,
                assignmentId,
                StringComparison.Ordinal));
        RemoveWhere(
            LifecycleIdempotencyRecords,
            item => string.Equals(
                item.Result.AssignmentId,
                assignmentId,
                StringComparison.Ordinal));
        RemoveWhere(
            CommittedReleaseIdempotencyRecords,
            item => string.Equals(
                item.Result.AssignmentId,
                assignmentId,
                StringComparison.Ordinal));
        RemoveWhere(
            RecoveryReleaseIdempotencyRecords,
            item => string.Equals(
                item.Result.AssignmentId,
                assignmentId,
                StringComparison.Ordinal));
    }

    private static void EvictTerminalReceiptsForEntity(string entityId)
    {
        RemoveWhere(
            CommittedReleaseIdempotencyRecords,
            item => string.Equals(item.Result.EntityId, entityId, StringComparison.Ordinal));
        RemoveWhere(
            RecoveryReleaseIdempotencyRecords,
            item => string.Equals(item.Result.EntityId, entityId, StringComparison.Ordinal));
    }

    private static void EvictMaterializationRollbackForAssignment(string assignmentId) =>
        RemoveWhere(
            MaterializationRollbackIdempotencyRecords,
            item => string.Equals(
                item.Result.AssignmentId,
                assignmentId,
                StringComparison.Ordinal));

    private static void RemoveWhere<T>(
        Dictionary<string, T> records,
        Func<T, bool> predicate)
    {
        foreach (var key in records
            .Where(item => predicate(item.Value))
            .Select(item => item.Key)
            .ToArray())
        {
            records.Remove(key);
        }
    }

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
        if (!Supports(request.UnitTypeId, request.VariantId, request.LoadoutId))
        {
            return Invalid(ProtocolErrorCodes.MutationPreconditionFailed,
                "The requested land materialization profile is not verified by this runtime build.");
        }
        var taskforce = RuntimeOwnedMutationAdapter.ResolveTaskforce(request.TaskforceId);
        if (taskforce is null
            || !RuntimeOwnedMutationAdapter.TaskforceMatchesRelationship(
                taskforce,
                request.Relationship))
        {
            return Invalid(ProtocolErrorCodes.MutationPreconditionFailed,
                "taskforceId must resolve to the declared friendly or opposing task-force side.");
        }
        if (request.DisplayName.Length > 128
            || request.WeaponStatus is not null
                && !Enum.TryParse<ObjectBase.WeaponStatus>(
                    request.WeaponStatus,
                    ignoreCase: true,
                    out _)
            || request.MissionType is not null
                && !Enum.TryParse<MissionEditorViewModel.MissionType>(
                    request.MissionType,
                    ignoreCase: true,
                    out _))
        {
            return Invalid(ProtocolErrorCodes.MutationPreconditionFailed,
                "The land display name or tactical posture is invalid.");
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

    private static OwnedLandMaterializationProfilePayload Profile(
        string unitTypeId,
        string domain,
        string variantId = "Default",
        string loadoutId = "Default") => new()
    {
        UnitTypeId = unitTypeId,
        VariantId = variantId,
        LoadoutId = loadoutId,
        SimulationDomain = domain,
    };

    private static void ApplyTacticalPosture(
        LandUnit unit,
        MaterializeOwnedLandUnitRequestPayload request)
    {
        if (request.WeaponStatus is not null
            && Enum.TryParse<ObjectBase.WeaponStatus>(
                request.WeaponStatus,
                ignoreCase: true,
                out var weaponStatus))
        {
            unit._weaponStatus = weaponStatus;
        }

        if (request.MissionType is not null
            && Enum.TryParse<MissionEditorViewModel.MissionType>(
                request.MissionType,
                ignoreCase: true,
                out var missionType))
        {
            unit._ai.CurrentMissionType.Value = missionType;
        }

        if (request.RadarsActive is bool radarsActive)
        {
            foreach (var radar in unit._obp!._searchRadarSystems)
            {
                if (radarsActive)
                {
                    radar.Enable();
                }
                else
                {
                    radar.Disable();
                }
            }
            unit.IsAirSearchRadarsOn.Value = radarsActive;
            unit.IsSurfaceSearchRadarsOn.Value = radarsActive;
        }

        unit._ai.AutoSetHidingState();
    }

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

    private static RollbackIncompleteOwnedMaterializationResultPayload
        CloneMaterializationRollback(
            RollbackIncompleteOwnedMaterializationResultPayload value) =>
        JsonSerializer.Deserialize<RollbackIncompleteOwnedMaterializationResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions)!;

    private static ReleaseCommittedOwnedResourceResultPayload CloneCommittedRelease(
        ReleaseCommittedOwnedResourceResultPayload value) =>
        JsonSerializer.Deserialize<ReleaseCommittedOwnedResourceResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions)!;

    private static OwnedSurfaceRecoveryReleaseResultPayload CloneRecoveryRelease(
        OwnedSurfaceRecoveryReleaseResultPayload value) =>
        JsonSerializer.Deserialize<OwnedSurfaceRecoveryReleaseResultPayload>(
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
        public MaterializationRollbackMode RollbackMode { get; set; }
    }

    private enum MaterializationRollbackMode
    {
        None,
        DestroyNewObject,
        RestoreRetainedPredecessor,
    }

    private sealed record IdempotencyRecord(
        string SessionId,
        string RequestJson,
        MaterializeOwnedLandUnitResultPayload Result);

    private sealed record LifecycleIdempotencyRecord(
        string SessionId,
        string RequestJson,
        OwnedSurfaceLifecycleResultPayload Result);

    private sealed record MaterializationRollbackIdempotencyRecord(
        string RequestJson,
        RollbackIncompleteOwnedMaterializationResultPayload Result);

    private sealed record CommittedReleaseIdempotencyRecord(
        string RequestJson,
        ReleaseCommittedOwnedResourceResultPayload Result);

    private sealed record RecoveryReleaseIdempotencyRecord(
        string RequestJson,
        OwnedSurfaceRecoveryReleaseResultPayload Result);
}
