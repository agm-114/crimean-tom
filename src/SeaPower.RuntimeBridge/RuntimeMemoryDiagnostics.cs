using System.Diagnostics;

using CrimeanTom.Protocol;

using UnityEngine;
using UnityEngine.Profiling;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeMemoryDiagnostics
{
    private const string ProxyRootPrefix = "CrimeanTom render proxy - ";
    private const int LargestAssetLimit = 24;

    public static RuntimeMemoryStatePayload Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var textures = Resources.FindObjectsOfTypeAll<Texture>();
        var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
        var materials = Resources.FindObjectsOfTypeAll<Material>();
        var audioClips = Resources.FindObjectsOfTypeAll<AudioClip>();
        var gameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        var proxyRoots = gameObjects
            .Where(item => item != null
                && item.name.StartsWith(ProxyRootPrefix, StringComparison.Ordinal))
            .ToArray();

        var assets = new[]
        {
            Summarize("Texture", textures),
            Summarize("Mesh", meshes),
            Summarize("Material", materials),
            Summarize("AudioClip", audioClips),
            Summarize("GameObject", gameObjects),
        };
        var largest = textures.Cast<UnityEngine.Object>()
            .Concat(meshes)
            .Concat(materials)
            .Concat(audioClips)
            .Where(item => item != null)
            .Select(item => new RuntimeMemoryLargeAssetPayload
            {
                Kind = item.GetType().Name,
                Name = string.IsNullOrWhiteSpace(item.name) ? "<unnamed>" : item.name,
                EstimatedRuntimeBytes = RuntimeSize(item),
            })
            .OrderByDescending(item => item.EstimatedRuntimeBytes)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(LargestAssetLimit)
            .ToArray();

        return new RuntimeMemoryStatePayload
        {
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ProcessWorkingSetBytes = process.WorkingSet64,
            ProcessPrivateBytes = process.PrivateMemorySize64,
            ProcessVirtualBytes = process.VirtualMemorySize64,
            ProcessHandleCount = process.HandleCount,
            ProcessThreadCount = process.Threads.Count,
            ManagedHeapBytes = System.GC.GetTotalMemory(forceFullCollection: false),
            UnityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong(),
            UnityReservedBytes = Profiler.GetTotalReservedMemoryLong(),
            UnityUnusedReservedBytes = Profiler.GetTotalUnusedReservedMemoryLong(),
            UnityMonoHeapBytes = Profiler.GetMonoHeapSizeLong(),
            UnityMonoUsedBytes = Profiler.GetMonoUsedSizeLong(),
            LoadedAssetBundleCount = AssetBundle.GetAllLoadedAssetBundles().Count(),
            CrimeanTomProxyRootCount = proxyRoots.Length,
            ActiveCrimeanTomProxyRootCount = proxyRoots.Count(item => item.activeInHierarchy),
            Assets = assets,
            LargestAssets = largest,
        };
    }

    private static RuntimeMemoryAssetSummaryPayload Summarize<T>(string kind, T[] assets)
        where T : UnityEngine.Object => new()
    {
        Kind = kind,
        Count = assets.Length,
        EstimatedRuntimeBytes = assets.Sum(item => RuntimeSize(item)),
    };

    private static long RuntimeSize(UnityEngine.Object item)
    {
        try
        {
            return item == null ? 0 : Profiler.GetRuntimeMemorySizeLong(item);
        }
        catch
        {
            return 0;
        }
    }
}
