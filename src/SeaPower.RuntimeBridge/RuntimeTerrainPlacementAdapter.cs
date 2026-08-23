using CrimeanTom.Protocol;

using SeaPower;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeTerrainPlacementAdapter
{
    public static TerrainPlacementProbeResultPayload Probe(TerrainPlacementProbeRequestPayload request)
    {
        if (!double.IsFinite(request.Latitude)
            || !double.IsFinite(request.Longitude)
            || request.Latitude is < -90d or > 90d
            || request.Longitude is < -180d or >= 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Terrain probe coordinates are invalid.");
        }

        var result = new TerrainPlacementProbeResultPayload
        {
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            TerrainManagerAvailable = Singleton<TerrainManager>.InstanceExists(),
        };
        if (!result.TerrainManagerAvailable)
        {
            result.Postcondition = "terrain-manager-unavailable";
            return result;
        }

        var position = new GeoPosition(request.Latitude, request.Longitude, 0d);
        var tile = Utils.longLatToTile(position);
        result.TileId = tile.ToString();
        var manager = Singleton<TerrainManager>.Instance;
        result.InitialTerrainLoadingDone = manager.IsInitialLoadingDone;
        result.TileLoaded = manager.TerrainChunkDictionary.TryGetValue(tile, out var chunk)
            && chunk is not null;
        result.TileFinalized = result.TileLoaded && chunk!._isFinalized;
        result.FlatteningFinishedForLandUnits = result.TileLoaded
            && chunk!.FlatteningFinishedForLandUnits;
        if (!result.InitialTerrainLoadingDone)
        {
            result.Postcondition = "terrain-initial-loading-incomplete";
            return result;
        }
        if (!result.TileLoaded)
        {
            result.Postcondition = "terrain-tile-not-loaded";
            return result;
        }
        if (!result.TileFinalized)
        {
            result.Postcondition = "terrain-tile-not-finalized";
            return result;
        }
        var height = TerrainUtils.GetHeightAt(position);
        if (!float.IsFinite(height))
        {
            result.Postcondition = "terrain-height-invalid";
            return result;
        }
        var local = Utils.longLatToLocalV3(position, Globals._currentCenterTile);
        result.TerrainHeightUnity = height;
        result.TerrainHeightMeters = Utils.unityHeightToMeter(height);
        result.LocalX = local.x;
        result.LocalY = height;
        result.LocalZ = local.z;
        result.IsLand = height >= 0f;
        result.PlacementReady = result.IsLand;
        result.Postcondition = !result.IsLand
            ? "terrain-sample-is-water"
            : result.FlatteningFinishedForLandUnits
                ? "terrain-land-placement-ready"
                : "terrain-land-placement-ready-auto-flatten-pending";
        return result;
    }
}
