using SeaPower;
using UnityEngine;

namespace CrimeanTom.SeaPower;

// Read the installed base DEM without creating a TerrainManager or loading a tactical scene.
internal static partial class ScenarioTerrain
{
    private static readonly Dictionary<string, byte[]> chunks = [];
    private static readonly Queue<string> oldest = new();

    internal static short HeightMeters(ChartPosition position)
    {
        GeoPosition geo = new() { _latitude = position.Latitude, _longitude = position.Longitude };
        // DEM samples are offset from whole-degree tile boundaries; use the same shifted tile as the pixel index.
        string key = Utils.longLatToDEMChunk(new GeoPosition
        {
            _latitude = position.Latitude + 0.008333333767950535,
            _longitude = position.Longitude - 0.025000001303851604,
        });
        if (!chunks.TryGetValue(key, out byte[] bytes))
        {
            string path = Path.Combine(Application.streamingAssetsPath, "original", "terrain", "data", key + ".lz4");
            byte[] compressed = File.ReadAllBytes(path);
            if (compressed.Length < 4 || BitConverter.ToInt32(compressed, compressed.Length - 4) != 2400 * 2400 * 2)
                throw new InvalidDataException($"Unexpected terrain tile format: {key}.");
            bytes = LZ4.decompressBuffer(compressed) ?? throw new InvalidDataException($"Could not read terrain tile {key}.");
            if (chunks.Count >= 4) chunks.Remove(oldest.Dequeue());
            chunks[key] = bytes;
            oldest.Enqueue(key);
        }
        return BitConverter.ToInt16(bytes, Utils.longLatToInDEMChunkPosition(geo));
    }

    internal static bool IsNavigable(ChartPosition first, ChartPosition second, float depth = 20)
    {
        int samples = Math.Max(1, (int)Math.Ceiling(first.DistanceTo(second) / 0.5));
        for (int sample = 0; sample <= samples; sample++)
            if (HeightMeters(first.Towards(second, (double)sample / samples)) > -depth) return false;
        return true;
    }

    internal static bool HasSpawnClearance(ChartPosition position, float depth)
    {
        // Check around the hull too, including rounding and coastal DEM transitions.
        foreach (float x in new[] { -0.15f, 0, 0.15f })
        foreach (float z in new[] { -0.15f, 0, 0.15f })
            if (HeightMeters(position.Offset(new(x, 0, z))) > -depth) return false;
        return true;
    }
}
