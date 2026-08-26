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

    private static readonly OwnedSurfaceMaterializationProfilePayload[] PinnedProfiles =
    [
        Profile("usn_ff_knox_84", "Variant43"),
        Profile("usn_cg_ticonderoga", "Default"),
        Profile("usn_dd_spruance", "Default"),
        Profile("usn_ssn_los_angeles", "Variant31"),
        Profile("usn_sh-2f", "Squadron4", "ASW"),
        Profile("usn_sh-3h", "Squadron1", "ASW"),
        Profile("wp_skr_krivak2", "Default"),
    ];

    private static OwnedSurfaceMaterializationProfilePayload[]? _catalogProfiles;
    private static int _catalogProfileSourceCount = -1;

    private static readonly string[] SurfaceRenderProxyUnitTypeIds =
    [
        "usn_ff_knox_84",
        "usn_cg_ticonderoga",
        "usn_dd_spruance",
        "wp_skr_krivak2",
    ];

    internal static OwnedSurfaceMaterializationProfilePayload[] SupportedProfiles() =>
        CatalogProfiles().Select(CloneProfile).ToArray();

    internal static bool Supports(string unitTypeId, string variantId, string loadoutId) =>
        CatalogProfiles().Any(profile =>
            string.Equals(profile.UnitTypeId, unitTypeId, StringComparison.Ordinal)
            && string.Equals(profile.VariantId, variantId, StringComparison.Ordinal)
            && string.Equals(profile.LoadoutId, loadoutId, StringComparison.Ordinal));

    private static OwnedSurfaceMaterializationProfilePayload[] CatalogProfiles()
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

        var profiles = new List<OwnedSurfaceMaterializationProfilePayload>(PinnedProfiles);
        var identities = PinnedProfiles
            .Select(ProfileIdentity)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in unitData
            .Where(item => item.Value.Item2 is not null
                && item.Value.Item2._objectType is ObjectBase.ObjectType.Vessel
                    or ObjectBase.ObjectType.Submarine
                    or ObjectBase.ObjectType.Aircraft
                    or ObjectBase.ObjectType.Helicopter)
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
                var profile = Profile(entry.Key.Item2, variantId, loadoutId);
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

    private static string ProfileIdentity(OwnedSurfaceMaterializationProfilePayload profile) =>
        $"{profile.UnitTypeId}\n{profile.VariantId}\n{profile.LoadoutId}";

    internal static bool SupportsSurfaceRenderProxy(
        string unitTypeId,
        string variantId,
        string loadoutId) =>
        SurfaceRenderProxyUnitTypeIds.Contains(unitTypeId, StringComparer.Ordinal)
        && Supports(unitTypeId, variantId, loadoutId);

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
    private const double AltitudeToleranceFeet = 5d;
    private const double SpeedToleranceKnots = 0.001d;
    private const long FuelFractionToleranceMilliunits = 2_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly Dictionary<string, OwnedEntityRecord> OwnedEntities =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HostedEntityRecord> HostedEntities =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> HostedLaunchPending = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IdempotencyRecord> IdempotencyRecords =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HostedIdempotencyRecord> HostedIdempotencyRecords =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HostedLaunchIdempotencyRecord>
        HostedLaunchIdempotencyRecords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LifecycleIdempotencyRecord>
        LifecycleIdempotencyRecords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RecoveryReleaseIdempotencyRecord>
        RecoveryReleaseIdempotencyRecords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MaterializationRollbackIdempotencyRecord>
        MaterializationRollbackIdempotencyRecords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CommittedReleaseIdempotencyRecord>
        CommittedReleaseIdempotencyRecords = new(StringComparer.Ordinal);

    private static string _armedSessionId = string.Empty;
    private static long _expiresAtUnixMilliseconds;

    public static bool IsArmed => !string.IsNullOrEmpty(_armedSessionId)
        && _expiresAtUnixMilliseconds > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long? ExpiresAtUnixMilliseconds => IsArmed
        ? _expiresAtUnixMilliseconds
        : null;

    public static bool IsArmedFor(string? sessionId) => IsArmed
        && string.Equals(_armedSessionId, sessionId, StringComparison.Ordinal);

    internal static bool HasOwnedEntity(string entityId) =>
        OwnedEntities.ContainsKey(entityId) || HostedEntities.ContainsKey(entityId);

    internal static bool HasActiveOwnedEntity(string entityId) =>
        OwnedEntities.TryGetValue(entityId, out var record)
        && record.Unit != null
        && !record.Unit.Disabled;

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
        }
    }

    public static OwnedSurfaceResourceStatePayload CaptureResourceState()
    {
        ReconcileLaunchedHostedAircraft();
        var selected = Singleton<RenderPosition>.InstanceExists()
            ? Singleton<RenderPosition>.Instance.getSelectedObject()
            : null;
        var materializedEntries = OwnedEntities
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var record = pair.Value;
                var vessel = record.Unit;
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
                    MaterializationRollbackPending =
                        record.RollbackMode != MaterializationRollbackMode.None,
                    NativeInitialized = RuntimeNativeUnitReadiness.IsInitialized(vessel),
                    AcceptsPlayerOrders = RuntimeNativeUnitReadiness.AcceptsPlayerOrders(vessel),
                    HasNativeControl = vessel != null && vessel._hasControl,
                    NativeStoresInitialized = RuntimeNativeUnitReadiness.HasInitializedStores(vessel),
                    IsAircraftInFlight = RuntimeNativeUnitReadiness.AirborneRequirementSatisfied(vessel),
                    UnitTypeId = vessel == null ? string.Empty : vessel._obp?._objectIniName ?? string.Empty,
                    VariantId = vessel == null ? string.Empty : vessel._obp?._objectVariantReference ?? string.Empty,
                    LoadoutId = vessel == null ? string.Empty : vessel._obp?._loadoutVariant ?? string.Empty,
                    AmmunitionQuantity = vessel == null ? 0 : StoreQuantity(vessel),
                    GameUniqueId = vessel == null ? null : vessel.UniqueID,
                    HostEntityId = record.HostEntityId,
                    HasPosition = vessel is not null,
                    Latitude = vessel?._geoPosition._latitude ?? 0d,
                    Longitude = vessel?._geoPosition._longitude ?? 0d,
                    HeadingDegrees = vessel?.getHeading() ?? 0d,
                    HorizontalDistanceFromOriginMeters = vessel is null
                        ? 0d
                        : HorizontalDistanceFromOriginMeters(vessel),
                    IsSelectedByPlayer = vessel is not null && ReferenceEquals(selected, vessel),
                };
            })
            .ToArray();
        var hostedEntries = HostedEntities
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new OwnedSurfaceResourceEntryPayload
            {
                EntityId = pair.Key,
                AssignmentId = pair.Value.AssignmentId,
                AuthorityEpoch = pair.Value.AuthorityEpoch,
                Revision = pair.Value.Revision,
                LifecycleState = "hosted-on-board",
                NativeInitialized = true,
                NativeStoresInitialized = true,
                UnitTypeId = pair.Value.UnitTypeId,
                VariantId = pair.Value.VariantId,
                LoadoutId = pair.Value.LoadoutId,
                HostEntityId = pair.Value.HostEntityId,
            });
        var entries = materializedEntries.Concat(hostedEntries).ToArray();
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
            MemoryReclamationSupported = true,
            SchedulerPolicy = "active-objects-consume-capacity; committed-suspended-objects-are-reclaimed",
            Entities = entries,
        };
    }

    public static MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>
        RegisterHostedAircraft(
            RegisterOwnedHostedAircraftRequestPayload request,
            string commandSessionId)
    {
        var requestJson = IdempotencyRequestJson(request, request.Context);
        var idempotencyKey = string.Join(
            "|",
            request.Context?.AssignmentId,
            request.Context?.IdempotencyKey);
        if (IsArmedFor(commandSessionId)
            && string.Equals(request.Context?.SessionId, commandSessionId, StringComparison.Ordinal)
            && HostedIdempotencyRecords.TryGetValue(idempotencyKey, out var prior))
        {
            if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
            {
                return MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Failure(
                    ProtocolErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used with different hosted-aircraft data.");
            }

            var replay = CloneHosted(prior.Result);
            replay.IdempotentReplay = true;
            return MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Success(replay);
        }

        var invalid = ValidateHostedAircraft(request, commandSessionId);
        if (invalid is not null)
        {
            return invalid;
        }
        var context = request.Context!;

        var duplicateError = ExistingHostedIdentityError(request);
        if (duplicateError is not null)
        {
            return duplicateError;
        }

        var host = OwnedEntities[request.HostEntityId];
        var flightDeck = host.Unit!._obp!._flightDeck!;
        var realized = HostedState(request, flightDeck);
        var result = new RegisterOwnedHostedAircraftResultPayload
        {
            Outcome = context.DryRun ? "validated" : "registered-on-board",
            Postcondition = context.DryRun
                ? "owned-hosted-aircraft-registration-valid"
                : "owned-aircraft-present-in-native-flight-deck-inventory",
            AssignmentId = context.AssignmentId,
            AuthorityEpoch = context.AuthorityEpoch,
            Revision = context.DryRun ? context.ExpectedRevision : 1,
            IdempotencyKey = context.IdempotencyKey,
            DryRun = context.DryRun,
            RealizedAircraft = realized,
        };
        if (context.DryRun)
        {
            return MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Success(result);
        }

        var nativeRecord = new FlightDeck.CampaignUnitOnBoard(
            request.UnitTypeId,
            request.VariantId,
            request.EntityId);
        InitializeNewHostedFlightDeckStores(request.HostEntityId, flightDeck);
        flightDeck._campaignUnitsOnBoard.Add(nativeRecord);
        HostedEntities.Add(request.EntityId, new(
            context.AssignmentId,
            context.AuthorityEpoch,
            result.Revision,
            request.HostEntityId,
            request.DisplayName,
            request.UnitTypeId,
            request.VariantId,
            request.LoadoutId,
            host.TaskforceId,
            request.FuelFractionMilliunits ?? 1_000_000));
        if (!flightDeck.AddVehicle(
            request.UnitTypeId,
            request.VariantId,
            1,
            spawnRandom: false,
            logErrors: false))
        {
            flightDeck._campaignUnitsOnBoard.Remove(nativeRecord);
            HostedEntities.Remove(request.EntityId);
            return MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The host flight deck rejected the requested aircraft profile or capacity.");
        }

        result.RealizedAircraft = HostedState(request, flightDeck);
        HostedIdempotencyRecords.Add(
            idempotencyKey,
            new(requestJson, CloneHosted(result)));
        return MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Success(result);
    }

    public static MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>
        LaunchHostedAircraft(
            LaunchOwnedHostedAircraftRequestPayload request,
            string commandSessionId)
    {
        var requestJson = IdempotencyRequestJson(request, request.Context);
        var idempotencyKey = string.Join(
            "|",
            request.Context?.AssignmentId,
            request.Context?.IdempotencyKey);
        if (IsArmedFor(commandSessionId)
            && string.Equals(request.Context?.SessionId, commandSessionId, StringComparison.Ordinal)
            && HostedLaunchIdempotencyRecords.TryGetValue(idempotencyKey, out var prior))
        {
            if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
            {
                return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Failure(
                    ProtocolErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used with a different hosted-aircraft launch.");
            }

            var replay = CloneHostedLaunch(prior.Result);
            replay.IdempotentReplay = true;
            return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Success(replay);
        }


        var invalid = ValidateHostedLaunch(request, commandSessionId);
        if (invalid is not null)
        {
            return invalid;
        }
        var context = request.Context!;

        var hosted = HostedEntities[request.EntityId];
        var host = OwnedEntities[hosted.HostEntityId];
        var flightDeck = host.Unit!._obp!._flightDeck!;
        var vehicle = flightDeck.GetVehicleByName(hosted.UnitTypeId)!;
        var squadron = vehicle.Squadrons.First(item => string.Equals(
            item._name,
            hosted.VariantId,
            StringComparison.Ordinal));
        var loadout = vehicle.Loadouts.First(item => string.Equals(
            item.Name,
            hosted.LoadoutId,
            StringComparison.Ordinal));
        var callsign = squadron.Callsigns.First();
        var task = flightDeck.createLaunchTask(
            vehicle,
            loadout,
            squadron,
            callsign,
            new LaunchTaskParameters { _launchCount = 1 });
        if (task is null)
        {
            return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Sea Power rejected the native launch task, usually because the selected loadout cannot be supplied.");
        }

        var result = new LaunchOwnedHostedAircraftResultPayload
        {
            Outcome = "launch-scheduled",
            Postcondition = "native-flight-deck-launch-task-created",
            AssignmentId = context.AssignmentId,
            AuthorityEpoch = context.AuthorityEpoch,
            Revision = hosted.Revision,
            IdempotencyKey = context.IdempotencyKey,
            EntityId = request.EntityId,
            HostEntityId = hosted.HostEntityId,
            FlightDeckTaskCount = flightDeck.FlightDeckTasks.Count,
        };
        HostedLaunchIdempotencyRecords.Add(
            idempotencyKey,
            new(requestJson, CloneHostedLaunch(result)));
        HostedLaunchPending.Add(request.EntityId);
        return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Success(result);
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
        var requestJson = IdempotencyRequestJson(request, request.Context);
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
        if (record.Unit != null || record.Checkpoint is not null)
        {
            return MutationAdapterResult<OwnedSurfaceRecoveryReleaseResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Recovery release requires a detached owned record with no checkpoint.");
        }

        OwnedEntities.Remove(request.EntityId);
        EvictIdempotencyForAssignment(record.AssignmentId);
        EvictTerminalReceiptsForEntity(request.EntityId);
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

    private static double HorizontalDistanceFromOriginMeters(ObjectBase unit)
    {
        const double metersPerUnityUnit = 67.2d;
        var position = unit.transform.position;
        return Math.Sqrt(position.x * position.x + position.z * position.z)
            * metersPerUnityUnit;
    }

    public static MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>
        ReleaseCommittedResource(
            OwnedSurfaceEntityRequestPayload request,
            string commandSessionId)
    {
        if (!IsArmedFor(commandSessionId))
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        var context = request.Context;
        if (context is null
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The committed-resource release session does not match the executing pipe session.");
        }
        if (!ValidScopedId(request.EntityId, "unit:crimean-tom:")
            || !ValidScopedId(context.AssignmentId, "assignment:")
            || context.AuthorityEpoch < 1
            || context.ExpectedRevision < 0
            || string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || context.IdempotencyKey.Length > 128)
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The committed-resource release identity or fence is invalid.");
        }

        var key = LifecycleKey(GameCommandTypes.ReleaseCommittedOwnedResource, context);
        var requestJson = IdempotencyRequestJson(request, context);
        if (CommittedReleaseIdempotencyRecords.TryGetValue(key, out var prior))
        {
            if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
            {
                return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                    ProtocolErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used with a different committed-resource release payload.");
            }
            var replay = CloneCommittedRelease(prior.Result);
            replay.IdempotentReplay = true;
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Success(replay);
        }

        if (HostedEntities.TryGetValue(request.EntityId, out var hosted))
        {
            if (!string.Equals(hosted.AssignmentId, context.AssignmentId, StringComparison.Ordinal)
                || hosted.AuthorityEpoch != context.AuthorityEpoch
                || hosted.Revision != context.ExpectedRevision)
            {
                return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    "The committed hosted-aircraft record does not match the requested authority fence.");
            }
            HostedEntities.Remove(request.EntityId);
            HostedLaunchPending.Remove(request.EntityId);
            EvictIdempotencyForAssignment(hosted.AssignmentId);
            EvictTerminalReceiptsForEntity(request.EntityId);
            var hostedResult = new ReleaseCommittedOwnedResourceResultPayload
            {
                Outcome = "released",
                Postcondition = "committed-hosted-aircraft-metadata-released",
                EntityId = request.EntityId,
                AssignmentId = hosted.AssignmentId,
                AuthorityEpoch = hosted.AuthorityEpoch,
                Revision = hosted.Revision,
                IdempotencyKey = context.IdempotencyKey,
            };
            CommittedReleaseIdempotencyRecords.Add(
                key,
                new(requestJson, CloneCommittedRelease(hostedResult)));
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Success(
                hostedResult);
        }

        if (!OwnedEntities.TryGetValue(request.EntityId, out var record))
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "No bridge-owned surface record matches the committed-resource release.");
        }
        if (!string.Equals(record.AssignmentId, context.AssignmentId, StringComparison.Ordinal)
            || record.AuthorityEpoch != context.AuthorityEpoch
            || record.Revision != context.ExpectedRevision)
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The committed owned-surface record does not match the requested authority fence.");
        }
        if (record.RecoveryRequired
            || record.Checkpoint is null
            || record.RollbackMode != MaterializationRollbackMode.None
            || record.Unit != null && !record.Unit.Disabled)
        {
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Committed release requires a healthy suspended or detached owned record with an authoritative checkpoint.");
        }

        var unit = record.Unit;
        var timer = Stopwatch.StartNew();
        try
        {
            var nativeObjectRemoved = unit != null;
            if (unit != null)
            {
                unit._campaignTag = string.Empty;
                if (!unit.IsDestroyed)
                {
                    var taskforce = unit._taskforce;
                    unit.destroyObject(
                        keepInFlightEffectAlive: false,
                        score: false,
                        TacView.TCEvent.LeftArea);
                    taskforce?._taskforceDisabledUnits?.RemoveAll(candidate =>
                        ReferenceEquals(candidate, unit));
                }
                record.Unit = null;
            }
            OwnedEntities.Remove(request.EntityId);
            EvictIdempotencyForAssignment(record.AssignmentId);
            EvictTerminalReceiptsForEntity(request.EntityId);
            timer.Stop();
            var result = new ReleaseCommittedOwnedResourceResultPayload
            {
                Outcome = "released",
                Postcondition = nativeObjectRemoved
                    ? "committed-native-object-removed-and-owned-record-released"
                    : "committed-detached-owned-record-released",
                EntityId = request.EntityId,
                AssignmentId = record.AssignmentId,
                AuthorityEpoch = record.AuthorityEpoch,
                Revision = record.Revision,
                IdempotencyKey = context.IdempotencyKey,
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
            if (unit != null && !unit.IsDestroyed)
            {
                unit._campaignTag = request.EntityId;
                record.Unit = unit;
                if (!unit.Disabled)
                {
                    unit.Disable();
                }
            }
            return MutationAdapterResult<ReleaseCommittedOwnedResourceResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "operation=release-committed-owned-resource; recovery=suspended-owned-object-retained; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    public static MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>
        RollbackIncompleteMaterialization(
            OwnedSurfaceEntityRequestPayload request,
            string commandSessionId)
    {
        if (!IsArmedFor(commandSessionId))
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        var context = request.Context;
        if (context is null
            || !string.Equals(context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The materialization rollback session does not match the executing pipe session.");
        }
        if (!ValidScopedId(request.EntityId, "unit:crimean-tom:")
            || !ValidScopedId(context.AssignmentId, "assignment:")
            || context.AuthorityEpoch < 1
            || context.ExpectedRevision < 0
            || string.IsNullOrWhiteSpace(context.IdempotencyKey)
            || context.IdempotencyKey.Length > 128)
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The incomplete materialization identity or fence is invalid.");
        }

        var key = LifecycleKey(GameCommandTypes.RollbackIncompleteOwnedMaterialization, context);
        var requestJson = IdempotencyRequestJson(request, context);
        if (MaterializationRollbackIdempotencyRecords.TryGetValue(key, out var prior))
        {
            if (!string.Equals(prior.RequestJson, requestJson, StringComparison.Ordinal))
            {
                return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                    ProtocolErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used with a different materialization rollback payload.");
            }
            var replay = CloneMaterializationRollback(prior.Result);
            replay.IdempotentReplay = true;
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Success(replay);
        }

        if (!OwnedEntities.TryGetValue(request.EntityId, out var record))
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "No bridge-owned materialization record matches the requested entity.");
        }
        if (!string.Equals(record.AssignmentId, context.AssignmentId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The mutation assignment does not own this materialization record.");
        }
        if (record.AuthorityEpoch != context.AuthorityEpoch)
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.StaleAuthorityEpoch,
                $"Expected authority epoch {record.AuthorityEpoch}, received {context.AuthorityEpoch}.");
        }
        if (record.Revision != context.ExpectedRevision)
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                $"Expected entity revision {record.Revision}, received {context.ExpectedRevision}.");
        }
        if (record.RollbackMode == MaterializationRollbackMode.None
            || record.Unit == null
            || !string.Equals(record.Unit._campaignTag, request.EntityId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "Only an uncommitted materialization bearing the exact ownership tag can be rolled back.");
        }

        var timer = Stopwatch.StartNew();
        var failedAssignmentId = record.AssignmentId;
        var failedAuthorityEpoch = record.AuthorityEpoch;
        var failedRevision = record.Revision;
        var unit = record.Unit;
        var rollbackMode = record.RollbackMode;
        var removed = false;
        var postcondition = string.Empty;
        try
        {
            if (record.RollbackMode == MaterializationRollbackMode.RestoreRetainedPredecessor)
            {
                var checkpoint = record.Checkpoint
                    ?? throw new InvalidOperationException(
                        "The retained predecessor has no portable checkpoint to restore.");
                ApplyPortableState(unit, checkpoint.Unit);
                if (!unit.Disabled)
                {
                    unit.Disable();
                }
                record.AssignmentId = checkpoint.AssignmentId;
                record.AuthorityEpoch = checkpoint.AuthorityEpoch;
                record.Revision = checkpoint.Revision;
                record.TaskforceId = checkpoint.Unit.TaskforceId;
                record.RecoveryRequired = false;
                record.RollbackMode = MaterializationRollbackMode.None;
                postcondition = "retained-predecessor-restored-and-suspended";
            }
            else
            {
                unit._campaignTag = string.Empty;
                if (!unit.IsDestroyed)
                {
                    var taskforce = unit._taskforce;
                    unit.destroyObject(
                        keepInFlightEffectAlive: false,
                        score: false,
                        TacView.TCEvent.LeftArea);
                    taskforce?._taskforceDisabledUnits?.RemoveAll(candidate =>
                        ReferenceEquals(candidate, unit));
                }
                record.Unit = null;
                OwnedEntities.Remove(request.EntityId);
                removed = true;
                postcondition = "uncommitted-native-object-removed-and-owned-record-released";
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
                IdempotencyKey = context.IdempotencyKey,
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
            if (unit != null && !unit.IsDestroyed)
            {
                unit._campaignTag = request.EntityId;
                record.RecoveryRequired = true;
                record.RollbackMode = rollbackMode;
                if (!unit.Disabled)
                {
                    unit.Disable();
                }
            }
            return MutationAdapterResult<RollbackIncompleteOwnedMaterializationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "operation=rollback-incomplete-materialization; "
                + "recovery=recoverable-suspended-owned-object; "
                + $"failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
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

        var requestJson = IdempotencyRequestJson(request, request.Context);
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
        if (taskforce is null || !TaskforceMatchesRelationship(taskforce, request.Relationship))
        {
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "taskforceId must resolve to the declared friendly or opposing task-force side.");
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
                candidate != null
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
            AltitudeFeet = sourceUnit?.AltitudeFeet ?? request.AltitudeFeet ?? 0d,
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

        ObjectBase? created = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var position = new GeoPosition(
                predicted.Latitude!.Value,
                predicted.Longitude!.Value,
                FeetToUnityHeight(predicted.AltitudeFeet ?? 0d));
            var heading = new Vector3(0f, (float)NormalizeHeading(predicted.HeadingDegrees!.Value), 0f);
            created = CreateOwnedUnit(
                request.UnitTypeId,
                request.VariantId,
                request.LoadoutId,
                position,
                predicted.Telegraph,
                heading,
                taskforce);
            if (created == null)
            {
                return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Failure(
                    ProtocolErrorCodes.MutationPreconditionFailed,
                    "Sea Power's supported native constructor returned no unit.");
            }

            // Attach external ownership before any later operation can fail. Cleanup paths may
            // only touch objects bearing this exact tag and present in OwnedEntities.
            created._campaignTag = request.EntityId;
            ApplyDisplayName(created, request.DisplayName);
            InjectFailure(request.FaultInjectionStep, "after-owned-tag");
            if (sourceUnit is null)
            {
                created.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
                created.transform.eulerAngles = heading;
                created._geoPosition = position;
                ApplyTelegraph(created, request.Telegraph);
                created._velocityInKnots = SpeedForTelegraph(created, request.Telegraph);
                created._velocityInUnity = created._velocityInKnots * 0.0076554087f;
                InitializeAirborneState(created, position);
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
                AssertRealizedIdentity(request, realized, created);
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
                request.DisplayName,
                created,
                initialStoresQuantity);
            ownedRecord.Checkpoint = sourceCheckpoint;
            ownedRecord.RollbackMode = MaterializationRollbackMode.DestroyNewObject;
            OwnedEntities.Add(request.EntityId, ownedRecord);
            InjectFailure(request.FaultInjectionStep, "after-registry-add");
            stopwatch.Stop();
            result.SynchronousDurationMicroseconds = ElapsedMicroseconds(stopwatch);
            EvictMaterializationRollbackForAssignment(request.Context.AssignmentId);
            IdempotencyRecords.Add(idempotencyRecordKey, new(requestJson, Clone(result)));
            return MutationAdapterResult<MaterializeOwnedSurfaceUnitResultPayload>.Success(result);
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
                    request.DisplayName,
                    created,
                    StoreQuantity(created),
                    recoveryRequired: true);
                OwnedEntities[request.EntityId].RollbackMode =
                    MaterializationRollbackMode.DestroyNewObject;
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
        var vessel = record.Unit;
        var checkpoint = record.Checkpoint;
        if (record.RecoveryRequired
            || vessel == null
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
            ApplyDisplayName(vessel, request.DisplayName);
            ApplyPortableState(vessel, requestedState);
            if (vessel.Disabled)
            {
                vessel.Enable();
            }
            var realized = Capture(vessel, request.TaskforceId, record.InitialStoresQuantity);
            AssertRealizedIdentity(request, realized, vessel);
            var revision = request.Context.ExpectedRevision + 1;
            record.AssignmentId = request.Context.AssignmentId;
            record.AuthorityEpoch = request.Context.AuthorityEpoch;
            record.Revision = revision;
            record.TaskforceId = request.TaskforceId;
            record.RecoveryRequired = false;
            record.RollbackMode = MaterializationRollbackMode.RestoreRetainedPredecessor;
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
            EvictMaterializationRollbackForAssignment(request.Context.AssignmentId);
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
        var resolved = ResolveRecord<OwnedSurfaceCheckpointPayload>(
            request.Context,
            request.EntityId,
            commandSessionId,
            requirePaused: false);
        if (!resolved.Succeeded)
        {
            return resolved.Error!;
        }

        var record = resolved.Record!;
        if (record.RecoveryRequired)
        {
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A partial materialization object cannot produce an authoritative checkpoint.");
        }
        if (record.Unit == null)
        {
            if (record.Checkpoint is { } detachedCheckpoint
                && detachedCheckpoint.AssignmentId == record.AssignmentId
                && detachedCheckpoint.AuthorityEpoch == record.AuthorityEpoch
                && detachedCheckpoint.Revision == record.Revision)
            {
                return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Success(
                    detachedCheckpoint);
            }
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A detached owned surface record has no checkpoint for its current active revision.");
        }
        var vessel = record.Unit;
        if (vessel.Disabled)
        {
            if (record.Checkpoint is { } retainedCheckpoint
                && retainedCheckpoint.AssignmentId == record.AssignmentId
                && retainedCheckpoint.AuthorityEpoch == record.AuthorityEpoch
                && retainedCheckpoint.Revision == record.Revision - 1)
            {
                return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Success(
                    retainedCheckpoint);
            }
            return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A suspended owned surface unit has no checkpoint for its immediately preceding active revision.");
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
        record.RollbackMode = MaterializationRollbackMode.None;
        return MutationAdapterResult<OwnedSurfaceCheckpointPayload>.Success(checkpoint);
    }

    public static MutationAdapterResult<OwnedSurfaceLifecycleResultPayload> Update(
        UpdateOwnedSurfaceUnitRequestPayload request,
        string commandSessionId)
    {
        if (!ValidPosition(request.Latitude, request.Longitude, request.HeadingDegrees)
            || !ValidAltitude(request.AltitudeFeet)
            || request.Telegraph is < -1 or > 5)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The owned-unit update position, altitude, heading, or telegraph is invalid.");
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
        var vessel = record.Unit!;
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
            var altitudeFeet = request.AltitudeFeet ?? vessel.getHeightInFeet();
            var position = new GeoPosition(
                request.Latitude,
                request.Longitude,
                FeetToUnityHeight(altitudeFeet));
            vessel.transform.position = Utils.longLatToLocalV3(
                position,
                Globals._currentCenterTile);
            vessel.transform.eulerAngles = new Vector3(
                0f,
                (float)NormalizeHeading(request.HeadingDegrees),
                0f);
            vessel._geoPosition = position;
            InjectFailure(request.FaultInjectionStep, "after-position-apply");
            ApplyTelegraph(vessel, request.Telegraph);
            vessel._velocityInKnots = SpeedForTelegraph(vessel, request.Telegraph);
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
        var vessel = record.Unit!;
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
        if (!Supports(
                checkpoint.Unit.UnitTypeId,
                checkpoint.Unit.VariantId,
                checkpoint.Unit.LoadoutId)
            || checkpoint.Unit.Latitude is not double checkpointLatitude
            || checkpoint.Unit.Longitude is not double checkpointLongitude
            || checkpoint.Unit.HeadingDegrees is not double checkpointHeading
            || !ValidPosition(checkpointLatitude, checkpointLongitude, checkpointHeading)
            || !ValidAltitude(checkpoint.Unit.AltitudeFeet)
            || checkpoint.Unit.SpeedKnots is not double checkpointSpeed
            || !double.IsFinite(checkpointSpeed)
            || checkpoint.Unit.Telegraph is < -1 or > 5)
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The retained checkpoint does not contain a complete supported surface state.");
        }

        var taskforce = ResolveTaskforce(checkpoint.Unit.TaskforceId);
        if (taskforce is null
            || !string.Equals(
                checkpoint.Unit.TaskforceId,
                record.TaskforceId,
                StringComparison.Ordinal))
        {
            return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The checkpoint task force must resolve exactly to the task force retained by this owned entity.");
        }

        ObjectBase? vessel = record.Unit;
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
                    FeetToUnityHeight(checkpoint.Unit.AltitudeFeet ?? 0d));
                var heading = new Vector3(
                    0f,
                    (float)NormalizeHeading(checkpointHeading),
                    0f);
                vessel = CreateOwnedUnit(
                    checkpoint.Unit.UnitTypeId,
                    checkpoint.Unit.VariantId,
                    checkpoint.Unit.LoadoutId,
                    position,
                    checkpoint.Unit.Telegraph,
                    heading,
                    taskforce);
                if (vessel == null)
                {
                    return MutationAdapterResult<OwnedSurfaceLifecycleResultPayload>.Failure(
                        ProtocolErrorCodes.MutationPreconditionFailed,
                        "Sea Power's supported native constructor returned no unit during rematerialization.");
                }
                vessel._campaignTag = request.EntityId;
                ApplyDisplayName(vessel, record.DisplayName);
                record.Unit = vessel;
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
                record.Unit = vessel;
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
        var vessel = record.Unit!;
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
        string commandSessionId,
        bool requirePaused = true)
    {
        var resolved = ResolveRecord<T>(context, entityId, commandSessionId, requirePaused);
        if (!resolved.Succeeded)
        {
            return resolved;
        }
        var record = resolved.Record!;
        if (record.Unit == null
            || !string.Equals(record.Unit._campaignTag, entityId, StringComparison.Ordinal))
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
        string commandSessionId,
        bool requirePaused = true)
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
        var detachedCheckpointRead = !requirePaused
            && record.Unit == null
            && record.Checkpoint is { } detachedCheckpoint
            && detachedCheckpoint.AssignmentId == record.AssignmentId
            && detachedCheckpoint.AuthorityEpoch == record.AuthorityEpoch
            && detachedCheckpoint.Revision == record.Revision;
        if (!detachedCheckpointRead
            && (!Singleton<GameMain>.InstanceExists()
                || !Singleton<SceneCreator>.InstanceExists()
                || !Singleton<ObjectsManager>.InstanceExists()
                || !Singleton<TaskforceManager>.InstanceExists()
                || !Singleton<SceneCreator>.Instance.IsLoadingDone
                || requirePaused && !GameTime.IsPaused()))
        {
            return OwnedResolution<T>.Fail(
                ProtocolErrorCodes.MutationPreconditionFailed,
                requirePaused
                    ? "A loaded, paused tactical mission is required for owned lifecycle operations."
                    : "A loaded tactical mission is required for owned state capture.");
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
        var requestJson = IdempotencyRequestJson(request, context);
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
            item => string.Equals(item.Result.AssignmentId, assignmentId, StringComparison.Ordinal));
        RemoveWhere(
            HostedIdempotencyRecords,
            item => string.Equals(item.Result.AssignmentId, assignmentId, StringComparison.Ordinal));
        RemoveWhere(
            HostedLaunchIdempotencyRecords,
            item => string.Equals(item.Result.AssignmentId, assignmentId, StringComparison.Ordinal));
        RemoveWhere(
            LifecycleIdempotencyRecords,
            item => string.Equals(item.Result.AssignmentId, assignmentId, StringComparison.Ordinal));
        RemoveWhere(
            CommittedReleaseIdempotencyRecords,
            item => string.Equals(item.Result.AssignmentId, assignmentId, StringComparison.Ordinal));
        RemoveWhere(
            RecoveryReleaseIdempotencyRecords,
            item => string.Equals(item.Result.AssignmentId, assignmentId, StringComparison.Ordinal));
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
        Unit = Capture(record.Unit!, record.TaskforceId, record.InitialStoresQuantity),
    };

    private static void ApplyPortableState(
        ObjectBase vessel,
        OwnedSurfaceUnitStatePayload state)
    {
        var position = new GeoPosition(
            state.Latitude!.Value,
            state.Longitude!.Value,
            FeetToUnityHeight(state.AltitudeFeet ?? 0d));
        vessel.transform.position = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
        vessel.transform.eulerAngles = new Vector3(
            0f,
            (float)NormalizeHeading(state.HeadingDegrees!.Value),
            0f);
        vessel._geoPosition = position;
        ApplyTelegraph(vessel, state.Telegraph);
        vessel._velocityInKnots = (float)state.SpeedKnots!.Value;
        vessel._velocityInUnity = vessel._velocityInKnots * 0.0076554087f;
        ApplyFuel(vessel, state.FuelFractionMilliunits);
        Physics.SyncTransforms();

        // getHeightInFeet() applies Sea Power's public domain offset (notably
        // a submarine's center-to-keel distance), while GeoPosition height is
        // the raw transform height. Correct from native readback so public
        // altitude or depth round-trips.
        if (state.AltitudeFeet is double expectedAltitudeFeet)
        {
            var realizedAltitudeFeet = vessel.getHeightInFeet();
            var correctionFeet = expectedAltitudeFeet - realizedAltitudeFeet;
            if (double.IsFinite(correctionFeet)
                && Math.Abs(correctionFeet) > 0.001d)
            {
                position = new GeoPosition(
                    state.Latitude.Value,
                    state.Longitude.Value,
                    position._height + FeetToUnityHeight(correctionFeet));
                vessel.transform.position = Utils.longLatToLocalV3(
                    position,
                    Globals._currentCenterTile);
                vessel._geoPosition = position;
                Physics.SyncTransforms();
            }
        }
        if (state.AltitudeFeet > 0d)
        {
            ApplyAirborneConfiguration(vessel, position);
        }
    }

    private static void ApplyFuel(ObjectBase vessel, long? fuelFractionMilliunits)
    {
        if (fuelFractionMilliunits is not long fuel)
        {
            return;
        }

        var parameters = vessel._obp;
        if (parameters is null || parameters._maxFuel <= 0f)
        {
            throw new InvalidOperationException(
                "The owned unit has no native fuel capacity to restore.");
        }

        parameters._currentFuelMass = (float)(fuel / 1_000_000d * parameters._maxFuel);
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
            || !ValidAltitude(unit.AltitudeFeet)
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
        OwnedSurfaceUnitStatePayload unit)
    {
        var fields = new List<KeyValuePair<string, string?>>
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
        };
        if (unit.AltitudeFeet is not null)
        {
            fields.Insert(11, Field("altitude-feet", unit.AltitudeFeet));
        }
        return CapabilityFingerprint.ComputeSha256(fields);
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
        if (request.DisplayName.Length > 128)
        {
            return Invalid("displayName must contain at most 128 characters.");
        }
        if (!Supports(request.UnitTypeId, request.VariantId, request.LoadoutId))
        {
            return Invalid(
                "The requested materialization profile is not present in Sea Power's active unit catalog.");
        }
        if (IsHelicopterProfile(request.UnitTypeId)
            && request.SourceCheckpoint is null
            && request.AltitudeFeet is not (>= 10d and <= 20_000d))
        {
            return Invalid("Airborne helicopter materialization requires altitudeFeet from 10 to 20000.");
        }
        if (!double.IsFinite(request.Latitude)
            || !double.IsFinite(request.Longitude)
            || !double.IsFinite(request.HeadingDegrees)
            || !ValidAltitude(request.AltitudeFeet)
            || request.Latitude is < -90d or > 90d
            || request.Longitude is < -180d or > 180d)
        {
            return Invalid("Latitude, longitude, altitude, and heading must be finite and geographically valid.");
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

    private static MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>?
        ValidateHostedAircraft(
            RegisterOwnedHostedAircraftRequestPayload request,
            string commandSessionId)
    {
        MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload> InvalidHosted(
            string message) =>
            MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                message);

        if (!IsArmedFor(commandSessionId))
        {
            return MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        if (request.Context is null
            || !string.Equals(request.Context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The hosted-aircraft context session does not match the executing pipe session.");
        }
        if (!ValidScopedId(request.Context.AssignmentId, "assignment:")
            || !ValidScopedId(request.EntityId, "unit:crimean-tom:")
            || !ValidScopedId(request.HostEntityId, "unit:crimean-tom:")
            || string.Equals(request.EntityId, request.HostEntityId, StringComparison.Ordinal))
        {
            return InvalidHosted("The assignment, hosted-aircraft, or host identity is invalid.");
        }
        if (request.Context.AuthorityEpoch < 1
            || request.Context.ExpectedRevision != 0)
        {
            return InvalidHosted("Initial hosted-aircraft registration requires a positive authority epoch and expectedRevision 0.");
        }
        if (request.FuelFractionMilliunits is { } fuelFraction
            && !ValidFraction(fuelFraction))
        {
            return InvalidHosted("Hosted-aircraft fuel fraction must be between 0 and 1000000 milliunits.");
        }
        if (string.IsNullOrWhiteSpace(request.Context.IdempotencyKey)
            || request.Context.IdempotencyKey.Length > 128
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Length > 128)
        {
            return InvalidHosted("The idempotency key or display name is outside its supported bound.");
        }
        if (string.IsNullOrWhiteSpace(request.UnitTypeId)
            || string.IsNullOrWhiteSpace(request.VariantId)
            || string.IsNullOrWhiteSpace(request.LoadoutId)
            || !IsHelicopterProfile(request.UnitTypeId)
            || !Supports(request.UnitTypeId, request.VariantId, request.LoadoutId))
        {
            return InvalidHosted("The requested hosted-aircraft profile is not allow-listed.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<ObjectsManager>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone
            || !GameTime.IsPaused())
        {
            return InvalidHosted("A fully loaded, paused tactical mission is required.");
        }
        if (!OwnedEntities.TryGetValue(request.HostEntityId, out var host)
            || host.Unit == null
            || host.Unit.Disabled
            || host.Unit._obp?._flightDeck is not { } flightDeck)
        {
            return InvalidHosted("The requested host is not an active bridge-owned unit with a native flight deck.");
        }
        var supportedVehicle = flightDeck.GetVehicleByName(request.UnitTypeId);
        if (supportedVehicle is null
            || supportedVehicle.Squadrons.All(squadron =>
                !string.Equals(squadron._name, request.VariantId, StringComparison.Ordinal))
            || flightDeck.AircraftCurrentlyOnBoard >= flightDeck.MaxAircraftOnBoard)
        {
            return InvalidHosted("The host flight deck does not support the requested squadron or has no free capacity.");
        }
        return null;
    }

    private static MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>?
        ValidateHostedLaunch(
            LaunchOwnedHostedAircraftRequestPayload request,
            string commandSessionId)
    {
        MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload> InvalidLaunch(
            string message) =>
            MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                message);

        if (!IsArmedFor(commandSessionId))
        {
            return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Owned mutations are not armed for the current pipe session.");
        }
        if (request.Context is null
            || !string.Equals(request.Context.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The hosted-aircraft launch session does not match the executing pipe session.");
        }
        if (!ValidScopedId(request.Context.AssignmentId, "assignment:")
            || !ValidScopedId(request.EntityId, "unit:crimean-tom:")
            || request.Context.AuthorityEpoch < 1
            || request.Context.ExpectedRevision < 1
            || string.IsNullOrWhiteSpace(request.Context.IdempotencyKey)
            || request.Context.IdempotencyKey.Length > 128)
        {
            return InvalidLaunch("The hosted-aircraft launch identity or fence is invalid.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<ObjectsManager>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone
            || !GameTime.IsPaused())
        {
            return InvalidLaunch("A fully loaded, paused tactical mission is required.");
        }
        if (!HostedEntities.TryGetValue(request.EntityId, out var hosted)
            || !string.Equals(hosted.AssignmentId, request.Context.AssignmentId, StringComparison.Ordinal))
        {
            return InvalidLaunch("No onboard campaign aircraft belongs to the requested assignment.");
        }
        if (hosted.AuthorityEpoch != request.Context.AuthorityEpoch)
        {
            return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.StaleAuthorityEpoch,
                $"Expected authority epoch {hosted.AuthorityEpoch}, received {request.Context.AuthorityEpoch}.");
        }
        if (hosted.Revision != request.Context.ExpectedRevision)
        {
            return MutationAdapterResult<LaunchOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                $"Expected hosted-aircraft revision {hosted.Revision}, received {request.Context.ExpectedRevision}.");
        }
        if (HostedLaunchPending.Contains(request.EntityId))
        {
            return InvalidLaunch("This hosted aircraft already has a pending native launch task.");
        }
        if (!OwnedEntities.TryGetValue(hosted.HostEntityId, out var host)
            || host.Unit == null
            || host.Unit.Disabled
            || host.Unit._obp?._flightDeck is not { } flightDeck)
        {
            return InvalidLaunch("The hosted aircraft no longer has an active bridge-owned flight deck.");
        }
        var vehicle = flightDeck.GetVehicleByName(hosted.UnitTypeId);
        var squadron = vehicle?.Squadrons.FirstOrDefault(item => string.Equals(
            item._name,
            hosted.VariantId,
            StringComparison.Ordinal));
        var loadout = vehicle?.Loadouts.FirstOrDefault(item => string.Equals(
            item.Name,
            hosted.LoadoutId,
            StringComparison.Ordinal));
        if (vehicle is null
            || vehicle.Numbers < 1
            || squadron is null
            || squadron.Numbers < 1
            || squadron.Callsigns.Count == 0
            || loadout is null)
        {
            return InvalidLaunch("The native flight deck no longer has the requested aircraft, squadron, callsign, or loadout.");
        }
        var nextCampaignAircraft = flightDeck._campaignUnitsOnBoard.FirstOrDefault(item =>
            string.Equals(item.IniRef, hosted.UnitTypeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SquadronRef, hosted.VariantId, StringComparison.OrdinalIgnoreCase));
        if (nextCampaignAircraft is null
            || !string.Equals(
                nextCampaignAircraft.CampaignTag,
                request.EntityId,
                StringComparison.Ordinal))
        {
            return InvalidLaunch(
                "Sea Power launches matching campaign aircraft in flight-deck order; launch the first matching campaign aircraft before this one.");
        }
        return null;
    }

    private static MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>?
        ExistingHostedIdentityError(RegisterOwnedHostedAircraftRequestPayload request)
    {
        var host = OwnedEntities[request.HostEntityId];
        var flightDeck = host.Unit!._obp!._flightDeck!;
        return OwnedEntities.ContainsKey(request.EntityId)
            || HostedEntities.ContainsKey(request.EntityId)
            || Singleton<ObjectsManager>.Instance._listOfAllObjects.Any(candidate =>
                candidate != null
                && string.Equals(candidate._campaignTag, request.EntityId, StringComparison.Ordinal))
            || flightDeck._campaignUnitsOnBoard.Any(item =>
                string.Equals(item.CampaignTag, request.EntityId, StringComparison.Ordinal))
            ? MutationAdapterResult<RegisterOwnedHostedAircraftResultPayload>.Failure(
                ProtocolErrorCodes.StaleRevision,
                "The requested hosted aircraft already has a live, retained, or onboard identity.")
            : null;
    }

    private static OwnedHostedAircraftStatePayload HostedState(
        RegisterOwnedHostedAircraftRequestPayload request,
        FlightDeck flightDeck) => new()
    {
        EntityId = request.EntityId,
        HostEntityId = request.HostEntityId,
        UnitTypeId = request.UnitTypeId,
        VariantId = request.VariantId,
        LoadoutId = request.LoadoutId,
        FuelFractionMilliunits = request.FuelFractionMilliunits ?? 1_000_000,
        LifecycleState = request.Context!.DryRun ? "validated-for-hosting" : "hosted-on-board",
        AircraftOnBoard = flightDeck.AircraftCurrentlyOnBoard
            + (request.Context.DryRun ? 1 : 0),
        AircraftCapacity = flightDeck.MaxAircraftOnBoard,
    };

    private static void InitializeNewHostedFlightDeckStores(
        string hostEntityId,
        FlightDeck flightDeck)
    {
        if (HostedEntities.Values.Any(item =>
            string.Equals(item.HostEntityId, hostEntityId, StringComparison.Ordinal)))
        {
            return;
        }

        flightDeck.LoadFlightDeckAirgroupAndAmmo(loadAirgroup: false, loadAmmo: true);
    }

    private static void ReconcileLaunchedHostedAircraft()
    {
        if (HostedEntities.Count == 0 || !Singleton<ObjectsManager>.InstanceExists())
        {
            return;
        }

        foreach (var (entityId, hosted) in HostedEntities.ToArray())
        {
            var launched = Singleton<ObjectsManager>.Instance._listOfAllObjects.FirstOrDefault(candidate =>
                candidate != null
                && !candidate.Disabled
                && candidate._obp is not null
                && string.Equals(candidate._campaignTag, entityId, StringComparison.Ordinal));
            if (launched == null)
            {
                continue;
            }

            ApplyFuel(launched, hosted.FuelFractionMilliunits);
            ApplyDisplayName(launched, hosted.DisplayName);
            OwnedEntities.Add(entityId, new(
                hosted.AssignmentId,
                hosted.AuthorityEpoch,
                hosted.Revision,
                hosted.TaskforceId,
                hosted.DisplayName,
                launched,
                StoreQuantity(launched),
                hostEntityId: hosted.HostEntityId));
            HostedEntities.Remove(entityId);
            HostedLaunchPending.Remove(entityId);
        }
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

    private static bool IsHelicopterProfile(string unitTypeId) =>
        ResolveObjectType(unitTypeId) == ObjectBase.ObjectType.Helicopter;

    private static OwnedSurfaceMaterializationProfilePayload Profile(
        string unitTypeId,
        string variantId,
        string loadoutId = "Default") => new()
    {
        UnitTypeId = unitTypeId,
        VariantId = variantId,
        LoadoutId = loadoutId,
    };

    private static OwnedSurfaceMaterializationProfilePayload CloneProfile(
        OwnedSurfaceMaterializationProfilePayload profile) => new()
    {
        UnitTypeId = profile.UnitTypeId,
        VariantId = profile.VariantId,
        LoadoutId = profile.LoadoutId,
    };

    private static ObjectBase? CreateOwnedUnit(
        string unitTypeId,
        string variantId,
        string loadoutId,
        GeoPosition position,
        int telegraph,
        Vector3 heading,
        Taskforce taskforce)
    {
        var objects = Singleton<ObjectsManager>.Instance;
        var unitNumber = objects._listOfAllObjects.Count + 1;
        var center = Singleton<SceneCreator>.Instance.GeoCenterPosition;
        switch (ResolveObjectType(unitTypeId))
        {
            case ObjectBase.ObjectType.Submarine:
                return objects.createSubmarine(
                    variantId,
                    loadoutId,
                    unitTypeId,
                    unitNumber,
                    center,
                    position,
                    telegraph,
                    heading,
                    taskforce,
                    loadAsync: false);
            case ObjectBase.ObjectType.Aircraft:
                return objects.createAircraft(
                    variantId,
                    loadoutId,
                    null,
                    null,
                    unitTypeId,
                    unitNumber,
                    center,
                    position,
                    heading,
                    taskforce,
                    createMinimapSymbol: true,
                    loadWeaponSystems: true,
                    loadAsync: false);
            case ObjectBase.ObjectType.Helicopter:
                return objects.createHelicopter(
                    variantId,
                    loadoutId,
                    null,
                    null,
                    unitTypeId,
                    unitNumber,
                    center,
                    position,
                    heading,
                    taskforce,
                    loadAsync: false);
            case ObjectBase.ObjectType.Vessel:
                return objects.createShip(
                    variantId,
                    loadoutId,
                    unitTypeId,
                    unitNumber,
                    center,
                    position,
                    telegraph,
                    heading,
                    taskforce,
                    loadAsync: false);
            default:
                return null;
        }
    }

    private static ObjectBase.ObjectType ResolveObjectType(string unitTypeId)
    {
        var unitData = Singleton<UnitMetadataProvider>.Instance?.UnitData;
        if (unitData is null)
        {
            return ObjectBase.ObjectType.Other;
        }

        foreach (var folder in new[] { "vessels", "aircraft", "land_units" })
        {
            if (unitData.TryGetValue((folder, unitTypeId), out var entry)
                && entry.Item2 is { } metadata)
            {
                return metadata._objectType;
            }
        }
        return ObjectBase.ObjectType.Other;
    }

    private static void InitializeAirborneState(ObjectBase unit, GeoPosition position)
    {
        ApplyAirborneConfiguration(unit, position);
        if (unit is Aircraft aircraft)
        {
            aircraft.giveControl(aircraft._velocityInKnots, resetAltitude: false);
        }
        else if (unit is Helicopter helicopter)
        {
            helicopter.GiveControl(0f);
        }
    }

    private static void ApplyAirborneConfiguration(ObjectBase unit, GeoPosition position)
    {
        if (unit is Aircraft aircraft)
        {
            aircraft._isInFlight = true;
            aircraft.DesiredAltitude.Value = (float)position._height;
            aircraft.AircraftAnimation.setAnimsForFlight();
            // Sea Power aircraft use either an Extend animation, where closed is
            // stowed, or a Retract animation, where open is stowed. Some catalog
            // aircraft only define the latter. Set both forms to their airborne
            // endpoint so an in-flight materialization never inherits taxi gear.
            aircraft.SetCodeAnim("Gear_Extend", setOpen: false);
            aircraft.SetCodeAnim("Gear_Retract", setOpen: true);
            aircraft.SetCodeAnim("Flaps_Takeoff_Retract", setOpen: true);
            aircraft.ImmediateStartEngines();
        }
        else if (unit is Helicopter helicopter)
        {
            helicopter._isInFlight = true;
            helicopter.DesiredAltitude.Value = (float)position._height;
            helicopter._helicopterAnimation.setAnimsForFlight();
            helicopter.gearsUp();
        }
    }

    private static float SpeedForTelegraph(ObjectBase unit, int telegraph)
    {
        if (unit is Vessel vessel)
        {
            return vessel.getSpeedForTelegraph(telegraph);
        }
        if (unit is Submarine submarine && submarine.SpeedCommand.HasValue)
        {
            return submarine.SpeedCommand.Value.GetDemandedSpeedInKnots(submarine);
        }
        if (unit is Helicopter helicopter && helicopter.SpeedCommand.Value is { } helicopterSpeed)
        {
            return helicopterSpeed.GetDemandedSpeedInKnots(helicopter);
        }
        if (unit is Aircraft aircraft && aircraft.SpeedCommand.Value is { } aircraftSpeed)
        {
            return aircraftSpeed.GetDemandedSpeedInKnots(aircraft);
        }

        throw new InvalidOperationException(
            $"The supported owned unit '{unit._obp?._objectIniName}' has no native telegraph speed command.");
    }

    private static void ApplyTelegraph(ObjectBase unit, int telegraph)
    {
        if (unit is Helicopter helicopter)
        {
            var demandedSpeed = telegraph <= 0
                ? 0f
                : helicopter._maxSpeedSeaLevel * Math.Clamp(telegraph, 0, 5) / 5f;
            helicopter.SetSpeedCommand(new ConstantSpeed(demandedSpeed, helicopter));
            return;
        }

        unit.setTelegraph(telegraph);
    }

    private static int CaptureTelegraph(ObjectBase unit)
    {
        if (unit is Aircraft aircraft)
        {
            return aircraft.getTelegraph() + 1;
        }
        if (unit is not Helicopter helicopter
            || helicopter.SpeedCommand.Value is not { } speedCommand
            || helicopter._maxSpeedSeaLevel <= 0f)
        {
            return unit.getTelegraph();
        }

        var demandedSpeed = speedCommand.GetDemandedSpeedInKnots(helicopter);
        return Math.Clamp(
            (int)Math.Round(demandedSpeed * 5f / helicopter._maxSpeedSeaLevel),
            0,
            5);
    }

    private static void ApplyDisplayName(ObjectBase vessel, string displayName)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            vessel.setName(normalized);

            // Sea Power's tactical-map label uses ObjectBaseParameters._shortName for
            // player objects rather than ObjectBase.Name. Vanilla scenario loading sets
            // both name overrides to the authored unit name, so mirror that path here.
            if (vessel._obp is not null)
            {
                vessel._obp._objectName = normalized;
                vessel._obp._shortName = normalized;
                vessel._obp._nameOverride = true;
                vessel._obp._shortNameOverride = true;
            }
        }
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

    private static bool TaskforceMatchesRelationship(Taskforce taskforce, string relationship)
    {
        var player = Globals._playerTaskforce;
        if (player is null)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(relationship)
            || string.Equals(relationship, "Friendly", StringComparison.Ordinal))
        {
            return taskforce == player;
        }
        return string.Equals(relationship, "Hostile", StringComparison.Ordinal)
            && taskforce != player
            && !string.Equals(
                taskforce.Side.ToString(),
                "Neutral",
                StringComparison.OrdinalIgnoreCase)
            && taskforce.Side != player.Side;
    }

    private static OwnedSurfaceUnitStatePayload Capture(
        ObjectBase vessel,
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
        AltitudeFeet = Finite(vessel.getHeightInFeet()),
        HeadingDegrees = Finite(vessel.getHeading()),
        SpeedKnots = Finite(vessel.getVelocityInKnots()),
        Telegraph = CaptureTelegraph(vessel),
        FuelFractionMilliunits = fuelFraction,
        StoresFractionMilliunits = storesFraction,
        DamageFractionMilliunits = damageFraction,
        IsDisabled = vessel.Disabled,
        IsDestroyed = vessel.IsDestroyed,
        };
    }

    private static long StoreQuantity(ObjectBase vessel) => vessel.AmmunitionAmountDictionary.Values
        .Where(value => value > 0)
        .Sum(value => checked((long)value));

    private static long? FractionMilliunits(double value) => double.IsFinite(value)
        ? checked((long)Math.Round(Math.Clamp(value, 0d, 1d) * 1_000_000d))
        : null;

    private static void AssertRealizedIdentity(
        MaterializeOwnedSurfaceUnitRequestPayload request,
        OwnedSurfaceUnitStatePayload realized,
        ObjectBase realizedObject)
    {
        var mismatches = new List<string>();
        IdentityMismatch(mismatches, "entity", request.EntityId, realized.EntityId);
        IdentityMismatch(mismatches, "unit-type", request.UnitTypeId, realized.UnitTypeId);
        IdentityMismatch(mismatches, "variant", request.VariantId, realized.VariantId);
        IdentityMismatch(mismatches, "loadout", request.LoadoutId, realized.LoadoutId);
        IdentityMismatch(mismatches, "taskforce", request.TaskforceId, realized.TaskforceId);
        PositionMismatch(mismatches, "latitude", request.Latitude, realized.Latitude, PositionToleranceDegrees);
        PositionMismatch(mismatches, "longitude", request.Longitude, realized.Longitude, PositionToleranceDegrees);
        if (request.AltitudeFeet is not null
            && realizedObject is Aircraft or Helicopter)
        {
            PositionMismatch(
                mismatches,
                "altitude-feet",
                request.AltitudeFeet,
                realized.AltitudeFeet,
                AltitudeToleranceFeet);
        }
        PositionMismatch(
            mismatches,
            "heading",
            NormalizeHeading(request.HeadingDegrees),
            realized.HeadingDegrees,
            HeadingToleranceDegrees);
        if (realized.Telegraph != request.Telegraph)
        {
            mismatches.Add($"telegraph expected={request.Telegraph} actual={realized.Telegraph}");
        }
        if (realized.IsDestroyed)
        {
            mismatches.Add("destroyed=true");
        }
        if (realized.IsDisabled)
        {
            mismatches.Add("disabled=true");
        }
        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"Sea Power realized a different unit identity: {string.Join("; ", mismatches)}.");
        }
    }

    private static void IdentityMismatch(
        ICollection<string> mismatches,
        string field,
        string? expected,
        string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            mismatches.Add($"{field} expected='{expected}' actual='{actual}'");
        }
    }

    private static void PositionMismatch(
        ICollection<string> mismatches,
        string field,
        double? expected,
        double? actual,
        double tolerance)
    {
        if (!Within(actual, expected, tolerance))
        {
            mismatches.Add($"{field} expected={expected} actual={actual} tolerance={tolerance}");
        }
    }

    private static void AssertUpdatedState(
        UpdateOwnedSurfaceUnitRequestPayload request,
        OwnedSurfaceUnitStatePayload realized)
    {
        if (!string.Equals(realized.EntityId, request.EntityId, StringComparison.Ordinal)
            || !Within(realized.Latitude, request.Latitude, PositionToleranceDegrees)
            || !Within(realized.Longitude, request.Longitude, PositionToleranceDegrees)
            || request.AltitudeFeet is not null
                && !Within(realized.AltitudeFeet, request.AltitudeFeet, AltitudeToleranceFeet)
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
        var mismatches = new List<string>();
        IdentityMismatch(mismatches, "stable-unit", expected.StableUnitId, realized.StableUnitId);
        IdentityMismatch(mismatches, "entity", expected.EntityId, realized.EntityId);
        IdentityMismatch(mismatches, "unit-type", expected.UnitTypeId, realized.UnitTypeId);
        IdentityMismatch(mismatches, "variant", expected.VariantId, realized.VariantId);
        IdentityMismatch(mismatches, "loadout", expected.LoadoutId, realized.LoadoutId);
        IdentityMismatch(mismatches, "taskforce", expected.TaskforceId, realized.TaskforceId);
        PositionMismatch(mismatches, "latitude", expected.Latitude, realized.Latitude, PositionToleranceDegrees);
        PositionMismatch(mismatches, "longitude", expected.Longitude, realized.Longitude, PositionToleranceDegrees);
        PositionMismatch(mismatches, "altitude-feet", expected.AltitudeFeet, realized.AltitudeFeet, AltitudeToleranceFeet);
        PositionMismatch(mismatches, "heading", expected.HeadingDegrees, realized.HeadingDegrees, HeadingToleranceDegrees);
        PositionMismatch(mismatches, "speed-knots", expected.SpeedKnots, realized.SpeedKnots, SpeedToleranceKnots);
        if (realized.Telegraph != expected.Telegraph)
        {
            mismatches.Add($"telegraph expected={expected.Telegraph} actual={realized.Telegraph}");
        }
        if (!WithinOptionalMilliunits(
                realized.FuelFractionMilliunits,
                expected.FuelFractionMilliunits,
                FuelFractionToleranceMilliunits))
        {
            mismatches.Add(
                $"fuel-milliunits expected={expected.FuelFractionMilliunits} "
                + $"actual={realized.FuelFractionMilliunits} "
                + $"tolerance={FuelFractionToleranceMilliunits}");
        }
        if (realized.IsDestroyed)
        {
            mismatches.Add("destroyed=true");
        }
        if (realized.IsDisabled != expectedDisabled)
        {
            mismatches.Add($"disabled expected={expectedDisabled} actual={realized.IsDisabled}");
        }
        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"Sea Power did not restore every supported checkpoint field: {string.Join("; ", mismatches)}.");
        }
    }

    private static bool Within(double? actual, double? expected, double tolerance) =>
        actual is double actualValue
        && expected is double expectedValue
        && Math.Abs(actualValue - expectedValue) <= tolerance;

    private static bool WithinOptionalMilliunits(long? actual, long? expected, long tolerance)
    {
        if (expected is null)
        {
            return true;
        }

        return actual is long actualValue
            && Math.Abs(actualValue - expected.Value) <= tolerance;
    }

    private static MaterializeOwnedSurfaceUnitResultPayload Clone(
        MaterializeOwnedSurfaceUnitResultPayload value) =>
        JsonSerializer.Deserialize<MaterializeOwnedSurfaceUnitResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException("Could not clone mutation result.");

    private static RegisterOwnedHostedAircraftResultPayload CloneHosted(
        RegisterOwnedHostedAircraftResultPayload value) =>
        JsonSerializer.Deserialize<RegisterOwnedHostedAircraftResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException(
                "Could not clone hosted-aircraft registration result.");

    private static LaunchOwnedHostedAircraftResultPayload CloneHostedLaunch(
        LaunchOwnedHostedAircraftResultPayload value) =>
        JsonSerializer.Deserialize<LaunchOwnedHostedAircraftResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException(
                "Could not clone hosted-aircraft launch result.");

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

    private static RollbackIncompleteOwnedMaterializationResultPayload
        CloneMaterializationRollback(
            RollbackIncompleteOwnedMaterializationResultPayload value) =>
        JsonSerializer.Deserialize<RollbackIncompleteOwnedMaterializationResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException(
                "Could not clone materialization rollback result.");

    private static ReleaseCommittedOwnedResourceResultPayload CloneCommittedRelease(
        ReleaseCommittedOwnedResourceResultPayload value) =>
        JsonSerializer.Deserialize<ReleaseCommittedOwnedResourceResultPayload>(
            JsonSerializer.Serialize(value, JsonOptions),
            JsonOptions) ?? throw new InvalidOperationException(
                "Could not clone committed resource release result.");

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

    private static double FeetToUnityHeight(double value) => value * 0.3048d * 0.0148809375d;

    private static bool ValidAltitude(double? altitudeFeet) =>
        altitudeFeet is null
        || double.IsFinite(altitudeFeet.Value)
            && altitudeFeet.Value is >= -2_000d and <= 100_000d;

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
        string displayName,
        ObjectBase unit,
        long initialStoresQuantity,
        bool recoveryRequired = false,
        string? hostEntityId = null)
    {
        public string AssignmentId { get; set; } = assignmentId;
        public long AuthorityEpoch { get; set; } = authorityEpoch;
        public long Revision { get; set; } = revision;
        public string TaskforceId { get; set; } = taskforceId;
        public string DisplayName { get; } = displayName;
        public long InitialStoresQuantity { get; } = initialStoresQuantity;
        public ObjectBase? Unit { get; set; } = unit;
        public OwnedSurfaceCheckpointPayload? Checkpoint { get; set; }
        public bool RecoveryRequired { get; set; } = recoveryRequired;
        public string? HostEntityId { get; } = hostEntityId;
        public MaterializationRollbackMode RollbackMode { get; set; }
    }

    private enum MaterializationRollbackMode
    {
        None,
        DestroyNewObject,
        RestoreRetainedPredecessor,
    }

    private sealed record IdempotencyRecord(
        string RequestJson,
        MaterializeOwnedSurfaceUnitResultPayload Result);

    private sealed record HostedIdempotencyRecord(
        string RequestJson,
        RegisterOwnedHostedAircraftResultPayload Result);

    private sealed record HostedLaunchIdempotencyRecord(
        string RequestJson,
        LaunchOwnedHostedAircraftResultPayload Result);

    private sealed record MaterializationRollbackIdempotencyRecord(
        string RequestJson,
        RollbackIncompleteOwnedMaterializationResultPayload Result);

    private sealed record HostedEntityRecord(
        string AssignmentId,
        long AuthorityEpoch,
        long Revision,
        string HostEntityId,
        string DisplayName,
        string UnitTypeId,
        string VariantId,
        string LoadoutId,
        string TaskforceId,
        long FuelFractionMilliunits);

    private sealed record LifecycleIdempotencyRecord(
        string RequestJson,
        OwnedSurfaceLifecycleResultPayload Result);

    private sealed record RecoveryReleaseIdempotencyRecord(
        string RequestJson,
        OwnedSurfaceRecoveryReleaseResultPayload Result);

    private sealed record CommittedReleaseIdempotencyRecord(
        string RequestJson,
        ReleaseCommittedOwnedResourceResultPayload Result);

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
