using System.Diagnostics;

using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeTacticalLocationAdapter
{
    public static MutationAdapterResult<PrepareTacticalLocationResultPayload> Prepare(
        PrepareTacticalLocationRequestPayload request,
        string commandSessionId)
    {
        if (!RuntimeOwnedMutationAdapter.IsArmedFor(commandSessionId))
        {
            return MutationAdapterResult<PrepareTacticalLocationResultPayload>.Failure(
                ProtocolErrorCodes.MutationDisabled,
                "Tactical-location preparation requires owned mutations armed for this session.");
        }
        if (!string.Equals(request.SessionId, commandSessionId, StringComparison.Ordinal))
        {
            return MutationAdapterResult<PrepareTacticalLocationResultPayload>.Failure(
                ProtocolErrorCodes.InvalidSession,
                "The tactical-location session does not match the executing pipe session.");
        }

        return PrepareValidated(request.Latitude, request.Longitude);
    }

    public static MutationAdapterResult<PrepareTacticalLocationResultPayload>
        PrepareForLocalMapSelection(double latitude, double longitude)
    {
        var resumeAfterPreparation = !GameTime.IsPaused();
        if (resumeAfterPreparation)
        {
            GameTime.Pause();
        }
        try
        {
            return PrepareValidated(latitude, longitude);
        }
        finally
        {
            if (resumeAfterPreparation && GameTime.IsPaused())
            {
                GameTime.UnPause();
            }
        }
    }

    private static MutationAdapterResult<PrepareTacticalLocationResultPayload> PrepareValidated(
        double latitude,
        double longitude)
    {
        if (!double.IsFinite(latitude)
            || !double.IsFinite(longitude)
            || latitude is < -88d or > 88d
            || longitude is < -180d or >= 180d)
        {
            return MutationAdapterResult<PrepareTacticalLocationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "The requested tactical location is outside Sea Power's supported world coordinates.");
        }
        if (!Singleton<GameMain>.InstanceExists()
            || !Singleton<SceneCreator>.InstanceExists()
            || !Singleton<SceneCreator>.Instance.IsLoadingDone
            || !Singleton<RenderPosition>.InstanceExists()
            || !Singleton<RelocationHandler>.InstanceExists()
            || !Singleton<TerrainManager>.InstanceExists()
            || !Singleton<TerrainManager>.Instance.IsInitialLoadingDone
            || !GameTime.IsPaused())
        {
            return MutationAdapterResult<PrepareTacticalLocationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                "A fully loaded, paused tactical world with finalized initial terrain is required.");
        }

        var beforeProbe = RuntimeTerrainPlacementAdapter.Probe(new()
        {
            Latitude = latitude,
            Longitude = longitude,
        });
        var previousCenter = Globals._currentCenterTile;
        var targetTile = Utils.longLatToTile(new GeoPosition(
            latitude,
            longitude,
            0d));
        var targetInsideActiveTerrainWindow = Math.Abs(targetTile.x - previousCenter.x) <= 1f
            && Math.Abs(targetTile.y - previousCenter.y) <= 1f;
        if (beforeProbe.PlacementReady && targetInsideActiveTerrainWindow)
        {
            return MutationAdapterResult<PrepareTacticalLocationResultPayload>.Success(new()
            {
                Outcome = "already-ready",
                Postcondition = "requested-tactical-location-terrain-already-finalized",
                Latitude = latitude,
                Longitude = longitude,
                PreviousCenterTileX = (int)previousCenter.x,
                PreviousCenterTileY = (int)previousCenter.y,
                CurrentCenterTileX = (int)previousCenter.x,
                CurrentCenterTileY = (int)previousCenter.y,
                Terrain = beforeProbe,
            });
        }

        var timer = Stopwatch.StartNew();
        try
        {
            var target = new GeoPosition(latitude, longitude, 0d);
            var renderPosition = Singleton<RenderPosition>.Instance;
            var localBeforeRelocation = Utils.longLatToLocalV3(
                target,
                previousCenter);

            renderPosition.deselectObjectAndDetachCamera();
            renderPosition.setGlobalCurrentTilePos(target);
            var localAfterRelocation = Utils.longLatToLocalV3(
                target,
                Globals._currentCenterTile);
            renderPosition.setGlobalPosition(target);
            var delta = localAfterRelocation - localBeforeRelocation;
            delta.y = 0f;
            Singleton<RelocationHandler>.Instance.relocate(delta);
            Physics.SyncTransforms();

            timer.Stop();
            var currentCenter = Globals._currentCenterTile;
            var afterProbe = RuntimeTerrainPlacementAdapter.Probe(new()
            {
                Latitude = latitude,
                Longitude = longitude,
            });
            return MutationAdapterResult<PrepareTacticalLocationResultPayload>.Success(new()
            {
                Outcome = "relocated",
                Postcondition = afterProbe.PlacementReady
                    ? "tactical-location-relocated-and-terrain-ready"
                    : "tactical-location-relocated-terrain-streaming-in-progress",
                Latitude = latitude,
                Longitude = longitude,
                PreviousCenterTileX = (int)previousCenter.x,
                PreviousCenterTileY = (int)previousCenter.y,
                CurrentCenterTileX = (int)currentCenter.x,
                CurrentCenterTileY = (int)currentCenter.y,
                Relocated = true,
                SynchronousDurationMicroseconds = ElapsedMicroseconds(timer),
                Terrain = afterProbe,
            });
        }
        catch (Exception exception)
        {
            return MutationAdapterResult<PrepareTacticalLocationResultPayload>.Failure(
                ProtocolErrorCodes.MutationPreconditionFailed,
                $"operation=prepare-tactical-location; failure={exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            timer.Stop();
        }
    }

    private static long ElapsedMicroseconds(Stopwatch stopwatch) =>
        (long)Math.Round(
            stopwatch.ElapsedTicks * 1_000_000d / Stopwatch.Frequency,
            MidpointRounding.AwayFromZero);
}
