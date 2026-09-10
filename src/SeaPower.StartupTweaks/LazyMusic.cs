using System.Collections;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeaPower.StartupTweaks;

[HarmonyPatch]
internal static class LazyMusic
{
    private class Bundle(string path, MusicClipData entry)
    {
        internal string Path { get; } = path;
        internal MusicClipData Entry { get; } = entry;
        internal bool Complete;
    }
    private static readonly List<Bundle> bundles = [];
    private static MusicManager? manager;
    private static MusicClipData? requested;
    private static bool requestedLoop;
    private static string? requestedName;
    internal static int Total => bundles.Count;
    internal static int Loaded => bundles.Count(bundle => bundle.Complete);
    internal static string[] TimingKeys => bundles.Select(bundle => "music:" + bundle.Path).ToArray();

    [HarmonyPrefix, HarmonyPatch(typeof(MusicManager), nameof(MusicManager.LoadMusicData))]
    private static bool Prepare(MusicManager __instance, bool checkForDisabled, ref IEnumerator __result)
    {
        // Explicit reloads retain the game's implementation once our initial jobs finish.
        if (!checkForDisabled)
        {
            if (Loaded < Total) { __result = Empty(); return false; }
            bundles.Clear();
            requested = null;
            return true;
        }
        if (manager != null) return true;
        string relative = "audio/music/original";
        string folder = Path.Combine(Application.streamingAssetsPath, "user", relative);
        if (!Directory.Exists(folder)) folder = Path.Combine(Application.streamingAssetsPath, "original", relative);
        if (!Directory.Exists(folder)) return true;
        string[] paths = Directory.GetFiles(folder).Where(path => !Path.GetFileName(path).Contains('.'))
            .OrderBy(path => Path.GetFileName(path) == "0_mainmenu" ? 0 : 1).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return true;
        manager = __instance;
        manager._allClips.Clear();
        foreach (string path in paths)
        {
            string side = Path.GetFileName(path);
            MusicClipData entry = new()
            {
                _name = side, _side = side == "0_mainmenu" ? "mainmenu" : side,
                _number = manager._allClips.Count, _mode = side == "0_mainmenu" ? MusicManagerMode.MainMenu : MusicManagerMode.Game,
            };
            bundles.Add(new(path, entry));
            manager._allClips.Add(entry);
        }
        manager._selectedMusic = manager._allClips[0];
        manager._isInitialized = true;
        __result = Empty();
        StartupTweaks.Log($"Indexed {Total} music bundles without loading audio.");
        return false;
    }

    private static IEnumerator Empty() { yield break; }

    [HarmonyPrefix, HarmonyPatch(typeof(MusicManager), "CreateAudioSource")]
    private static bool Request(MusicManager __instance, MusicClipData musicClipData, bool loop)
    {
        if (__instance != manager || musicClipData == null || musicClipData._clip != null) return true;
        var bundle = bundles.FirstOrDefault(bundle => bundle.Entry == musicClipData);
        if (bundle == null) return true;
        requested = musicClipData;
        requestedName = null;
        requestedLoop = loop;
        manager._selectedMusic = musicClipData;
        // Prevent Update from repeatedly selecting another track while this one is queued.
        manager._isPlaying = false;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MusicManager), nameof(MusicManager.Stop))]
    private static void Stop() { requested = null; requestedName = null; }

    [HarmonyPrefix, HarmonyPatch(typeof(MusicManager), nameof(MusicManager.PlayMusicByName))]
    private static bool ByName(string name)
    {
        if (Loaded >= Total || manager == null || manager._allClips.Any(clip => clip._name == name)) return true;
        requested = null;
        requestedName = name;
        manager._isPlaying = false;
        return false;
    }

    internal static IEnumerator Warm()
    {
        while (Loaded < Total && manager != null)
        {
            Bundle next = bundles.FirstOrDefault(bundle => !bundle.Complete && bundle.Entry == requested)
                ?? bundles.First(bundle => !bundle.Complete);
            var progress = StartupTweaks.Instance.Progress!;
            progress.Music.Begin("music:" + next.Path);
            progress.Music.File = next.Path;
            yield return AssetBundleManager.LoadAssetBundleFromFile(next.Path, 0);
            var assetBundle = AssetBundleManager.getAssetBundle(next.Path, 0);
            if (assetBundle != null)
            {
                Globals._assetBundleDictionary[Path.GetFileName(next.Path)] = assetBundle;
                var request = assetBundle.LoadAllAssetsAsync<AudioClip>();
                yield return request;
                bool first = true;
                foreach (AudioClip clip in request.allAssets.OfType<AudioClip>())
                {
                    var entry = first ? next.Entry : new MusicClipData
                    {
                        _side = next.Entry._side, _mode = next.Entry._mode, _number = manager._allClips.Count,
                    };
                    entry._clip = clip;
                    entry._name = clip.name;
                    if (!first) manager._allClips.Add(entry);
                    first = false;
                }
            }
            next.Complete = true;
            if (next.Entry._clip == null)
            {
                manager._allClips.Remove(next.Entry);
                StartupTweaks.Log($"Music bundle unavailable: {next.Path}");
            }
            progress.Music.End(next.Entry._clip != null);
            if (requested == next.Entry)
            {
                var entry = requested;
                requested = null;
                if (entry._clip != null) manager.PlayMusic(entry, requestedLoop);
            }
            if (requestedName != null && manager._allClips.FirstOrDefault(clip => clip._name == requestedName && clip._clip != null) is { } named)
            {
                requestedName = null;
                manager.PlayMusic(named);
            }
            StartupTweaks.Log($"Music ready {Loaded}/{Total}: {Path.GetFileName(next.Path)}");
            yield return null;
        }
        StartupTweaks.Instance.Progress?.SaveIfComplete();
    }
}
