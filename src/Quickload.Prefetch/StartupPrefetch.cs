using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Quickload;

// Called by Keel on a worker during the splash. Only managed file IO belongs here.
public static class StartupPrefetch
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaPowerQuickload");
    private static readonly string profilePath = Path.Combine(DirectoryPath, "last-startup-profile.txt");
    private static readonly object disk = new();
    private static Job? current;

    public static void Prepare(string streamingAssets, string[] enabledMods, Action<string> report) =>
        Start(streamingAssets, enabledMods, report).GetAwaiter().GetResult();

    public static Task Start(string streamingAssets, IEnumerable<string> enabledMods, Action<string>? report = null)
    {
        if (!File.Exists(Path.Combine(DirectoryPath, "cache-unit-loadouts.enabled"))) return Task.CompletedTask;
        var existing = Volatile.Read(ref current);
        if (existing != null) return existing.Done;
        var roots = new[] { Path.Combine(streamingAssets, "original"), Path.Combine(streamingAssets, "user") }
            .Concat(enabledMods).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidate = new Job(roots, report);
        existing = Interlocked.CompareExchange(ref current, candidate, null);
        if (existing != null) return existing.Done;
        _ = Task.Run(candidate.Run);
        return candidate.Done;
    }

    public static Job? Take(string profile)
    {
        var job = Interlocked.Exchange(ref current, null);
        job?.Handoff();
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
        private readonly Action<string>? report;
        private readonly TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly BlockingCollection<ReadItem> queue = new(8);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<FileHash?>> inputReads = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Archive> archives = new(StringComparer.OrdinalIgnoreCase);
        private bool handedOff;
        private bool released;
        private int preparedBytes;
        private int hashCount;

        internal Job(string[] roots, Action<string>? report)
        {
            this.roots = roots;
            this.report = report;
        }

        public Task Done => done.Task;
        public int HashCount => Volatile.Read(ref hashCount);
        public int ArchiveCount => archives.Count;
        internal void Handoff() => Volatile.Write(ref handedOff, true);

        internal void Run()
        {
            Task[] workers = [StartWorker(), StartWorker()];
            try
            {
                report?.Invoke("Quickload · Reading startup snapshots");
                if (File.Exists(profilePath))
                {
                    string profile = Encoding.UTF8.GetString(ReadFile(profilePath)).Trim();
                    if (profile.Length == 64 && profile.All(Uri.IsHexDigit))
                        foreach (string name in coreArchives)
                            if (!Read(Path.Combine(DirectoryPath, profile, name), archive: true)) return;
                }
                report?.Invoke("Quickload · Checking startup input files");
                foreach (var root in roots)
                {
                    foreach (string name in earlyFolders)
                        foreach (string path in Enumerate(Path.Combine(root, name), "*"))
                            if (IsInput(path) && !Read(path, archive: false)) return;
                    foreach (string path in OtherInputs(root))
                        if (!Read(path, archive: false)) return;
                }
                report?.Invoke("Quickload · Reading reusable mod cache chunks");
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
                try { Task.WaitAll(workers); done.TrySetResult(true); }
                catch (AggregateException error) { done.TrySetException(error.InnerExceptions); }
                queue.Dispose();
            }
        }

        private Task StartWorker() => Task.Factory.StartNew(ProcessQueue, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private bool Read(string path, bool archive)
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
                        queue.Add(new(path, stamp, bytes, result));
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
                        archives.TryAdd(item.Path, new(item.Stamp, buffer.ToArray()));
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

        private record ReadItem(string Path, FileStamp Stamp, byte[] Bytes, TaskCompletionSource<FileHash?>? HashResult);
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
