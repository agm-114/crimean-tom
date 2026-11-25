using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Quickload;

// Keel can start this same job during the splash or from the regular plugin entry point.
public static class StartupPrefetch
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaPowerQuickload");
    private static readonly string profilePath = Path.Combine(DirectoryPath, "last-startup-profile.txt");
    private static readonly object disk = new();
    private static readonly SemaphoreSlim jobGate = new(1, 1);
    private static Job? current;
    private static bool handedOff;

    public static (int Hashes, int Archives)? Progress =>
        Volatile.Read(ref current) is { } job ? (job.HashCount, job.ArchiveCount) : null;

    public static double? SecondsSinceWorkerFinished => Volatile.Read(ref current)?.SecondsSinceFinished;

    public static async Task<(string Path, Archive Archive)?> PreparedProfileArchive(string file)
    {
        var job = Volatile.Read(ref current);
        return job == null ? null : await job.ProfileArchive(file).ConfigureAwait(false);
    }

    public static void Prepare(string streamingAssets, string[] enabledMods, Action<string> report) =>
        Start(streamingAssets, enabledMods);

    public static void Start(string streamingAssets, IEnumerable<string> enabledMods)
    {
        bool cacheEnabled = File.Exists(Path.Combine(DirectoryPath, "cache-unit-loadouts.enabled"));
        SplashProgress.Show(cacheEnabled);
        jobGate.Wait();
        try
        {
            if (handedOff || current != null) return;
            string[] mods = enabledMods as string[] ?? enabledMods.ToArray();
            PreviousLaunch.Open(streamingAssets, mods, cacheEnabled);
            if (!cacheEnabled) return;
            var roots = new[] { Path.Combine(streamingAssets, "original"), Path.Combine(streamingAssets, "user") }
                .Concat(mods).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            current = new Job(roots);
            _ = Task.Run(current.Run);
        }
        finally { jobGate.Release(); }
    }

    public static Job? Take(string profile)
    {
        Job? job;
        jobGate.Wait();
        try
        {
            handedOff = true;
            job = current;
            current = null;
            job?.Handoff();
        }
        finally { jobGate.Release(); }
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(profilePath, profile);
        }
        catch (Exception error) when (FileError(error)) { }
        return job;
    }

    public static byte[] ReadFile(string path)
    {
        lock (disk) return File.ReadAllBytes(path);
    }

    public static bool IsInput(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ini" or ".json" or ".dll";

    public class Job
    {
        private static readonly string[] coreArchives =
            ["resolved-sensors.json.gz", "unit-worker-output.json.gz", "ini-warmup.bin.gz", "encyclopedia.json.gz"];
        private static readonly string[] earlyFolders = ["systems", "ammunition", "vessels", "aircraft", "land_units", "submarines"];
        private static readonly HashSet<string> earlyFolderNames = new(earlyFolders, StringComparer.OrdinalIgnoreCase);
        private const int MaxFile = 96 * 1024 * 1024;
        private const int MaxPrepared = 192 * 1024 * 1024;
        private readonly string[] roots;
        private readonly BlockingCollection<ReadItem> queue = new(8);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<FileHash?>> inputReads = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Archive> archives = new(StringComparer.OrdinalIgnoreCase);
        private bool handedOff;
        private bool released;
        private int preparedBytes;
        private int hashCount;
        private long finishedAt;
        private string? profileDirectory;
        private readonly Dictionary<string, TaskCompletionSource<bool>> profileReads = coreArchives.ToDictionary(
            file => file, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        internal Job(string[] roots) => this.roots = roots;

        public int HashCount => Volatile.Read(ref hashCount);
        public int ArchiveCount => archives.Count;
        internal double? SecondsSinceFinished
        {
            get
            {
                long end = Volatile.Read(ref finishedAt);
                return end == 0 ? null : (Stopwatch.GetTimestamp() - end) / (double)Stopwatch.Frequency;
            }
        }

        internal async Task<(string Path, Archive Archive)?> ProfileArchive(string file)
        {
            if (!profileReads.TryGetValue(file, out var ready) || !await ready.Task.ConfigureAwait(false)) return null;
            string path = Path.Combine(profileDirectory!, file);
            return archives.TryGetValue(path, out var archive) ? (path, archive) : null;
        }
        internal void Handoff() => Volatile.Write(ref handedOff, true);

        internal void Run()
        {
            Task[] workers = [StartWorker(), StartWorker()];
            try
            {
                if (File.Exists(profilePath))
                {
                    string profile = Encoding.UTF8.GetString(ReadFile(profilePath)).Trim();
                    if (profile.Length == 64 && profile.All(Uri.IsHexDigit))
                    {
                        profileDirectory = Path.Combine(DirectoryPath, profile);
                        foreach (string name in coreArchives)
                            if (!Read(Path.Combine(profileDirectory, name), archive: true, profileReads[name])) return;
                    }
                }
                foreach (var root in roots)
                {
                    foreach (string name in earlyFolders)
                        foreach (string path in Enumerate(Path.Combine(root, name), "*"))
                            if (IsInput(path) && !Read(path, archive: false)) return;
                    foreach (string path in OtherInputs(root))
                        if (!Read(path, archive: false)) return;
                }
                using var hash = SHA256.Create();
                foreach (string root in roots)
                {
                    string id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root.Replace('\\', '/').TrimEnd('/') + "/"))).Replace("-", "");
                    string layer = Path.Combine(DirectoryPath, "segments", id);
                    if (!Read(Path.Combine(layer, "inis.bin.gz"), archive: true)) return;
                    foreach (string units in Enumerate(layer, "units-*.json.gz").OrderByDescending(File.GetLastWriteTimeUtc).Take(2))
                        if (!Read(units, archive: true)) return;
                }
            }
            catch (Exception error) when (FileError(error)) { }
            finally
            {
                queue.CompleteAdding();
                try
                {
                    Task.WaitAll(workers);
                    Volatile.Write(ref finishedAt, Stopwatch.GetTimestamp());
                }
                finally
                {
                    queue.Dispose();
                    foreach (var ready in profileReads.Values) ready.TrySetResult(false);
                }
            }
        }

        private Task StartWorker() => Task.Factory.StartNew(ProcessQueue, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private bool Read(string path, bool archive, TaskCompletionSource<bool>? profileRead = null)
        {
            if (Volatile.Read(ref released)) return false;
            if (!archive && Volatile.Read(ref handedOff)) return true;
            try
            {
                if (!File.Exists(path)) return true;
                var stamp = FileStamp.Read(path);
                if (stamp.Size > MaxFile) return true;
                TaskCompletionSource<FileHash?>? result = null;
                if (!archive)
                {
                    result = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!inputReads.TryAdd(path, result)) return true;
                }
                bool queued = false;
                try
                {
                    byte[] bytes = ReadFile(path);
                    if (stamp == FileStamp.Read(path))
                    {
                        queue.Add(new(path, stamp, bytes, result, profileRead));
                        queued = true;
                    }
                }
                finally { if (!queued) result?.TrySetResult(null); }
            }
            catch (Exception error) when (FileError(error)) { }
            return true;
        }

        private void ProcessQueue()
        {
            using var hash = SHA256.Create();
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (item.HashResult is { } result)
                    {
                        try
                        {
                            var file = new FileHash(item.Stamp, hash.ComputeHash(item.Bytes), InputText(item.Path, item.Bytes));
                            Interlocked.Increment(ref hashCount);
                            result.TrySetResult(file);
                        }
                        finally { result.TrySetResult(null); }
                        continue;
                    }
                    if (Volatile.Read(ref released) || Volatile.Read(ref preparedBytes) >= MaxPrepared) continue;
                    using var source = new MemoryStream(item.Bytes, writable: false);
                    using var zip = new GZipStream(source, CompressionMode.Decompress);
                    using var buffer = new MemoryStream();
                    var block = new byte[64 * 1024];
                    int count;
                    while ((count = zip.Read(block, 0, block.Length)) > 0 && buffer.Length + count <= MaxFile)
                        buffer.Write(block, 0, count);
                    if (count != 0) continue;
                    int size = (int)buffer.Length;
                    if (Interlocked.Add(ref preparedBytes, size) <= MaxPrepared)
                    {
                        archives.TryAdd(item.Path, new(item.Stamp, buffer.ToArray()));
                        item.ProfileRead?.TrySetResult(true);
                    }
                    else Interlocked.Add(ref preparedBytes, -size);
                    if (Volatile.Read(ref released)) archives.Clear();
                }
                catch (Exception error) when (error is IOException or InvalidDataException) { }
            }
        }

        public FileHash? TakeHash(string path)
        {
            if (!inputReads.TryRemove(path, out var read)) return null;
            var hash = read.Task.GetAwaiter().GetResult();
            return hash != null && hash.Stamp == FileStamp.Read(path) ? hash : null;
        }
        public Archive? TakeArchive(string path) => archives.TryRemove(path, out var archive) && archive.Stamp == FileStamp.Read(path) ? archive : null;
        public void ReleaseArchives()
        {
            Volatile.Write(ref released, true);
            archives.Clear();
        }

        public void ReleaseHashes() => inputReads.Clear();

        private static IEnumerable<string> Enumerate(string root, string pattern)
        {
            try { return Directory.Exists(root) ? Directory.GetFiles(root, pattern, SearchOption.AllDirectories) : []; }
            catch (Exception error) when (FileError(error)) { return []; }
        }

        private static IEnumerable<string> OtherInputs(string root)
        {
            if (!Directory.Exists(root)) yield break;
            foreach (string path in Directory.GetFiles(root))
                if (IsInput(path)) yield return path;
            foreach (string folder in Directory.GetDirectories(root))
                if (!earlyFolderNames.Contains(Path.GetFileName(folder)) &&
                    !(Path.GetFileName(root).Equals("Quickload", StringComparison.OrdinalIgnoreCase) &&
                      Path.GetFileName(folder).Equals("CacheBundles", StringComparison.OrdinalIgnoreCase)))
                    foreach (string path in Enumerate(folder, "*"))
                        if (IsInput(path)) yield return path;
        }

        private record ReadItem(string Path, FileStamp Stamp, byte[] Bytes, TaskCompletionSource<FileHash?>? HashResult,
            TaskCompletionSource<bool>? ProfileRead);
    }

    public static string InputText(string path, byte[] bytes)
    {
        if (!Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase)) return "";
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException;

    public readonly record struct FileStamp(long Size, DateTime Written, DateTime Created)
    {
        public static FileStamp Read(string path)
        {
            var file = new FileInfo(path);
            return new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        }
    }

    public record FileHash(FileStamp Stamp, byte[] Hash, string Text);
    public record Archive(FileStamp Stamp, byte[] Data);
}
