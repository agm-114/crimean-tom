using System.Security.Cryptography;
using System.Text;

namespace CrimeanTom.LoadAcceleration;

public sealed class PersistentFileHashCache
{
    private const string Header = "crimean-tom-file-hash-cache-v1";
    private readonly object _sync = new();
    private readonly string _cachePath;
    private readonly bool _enabled;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _hits;
    private int _misses;
    private string _state;

    public PersistentFileHashCache(string cachePath, bool enabled, bool refresh)
    {
        _cachePath = Path.GetFullPath(cachePath);
        _enabled = enabled;
        _state = enabled ? "empty" : "disabled";
        if (enabled && !refresh)
        {
            LoadFailOpen();
        }
        else if (enabled)
        {
            _state = "refreshing";
        }
    }

    public string GetOrComputeSha256(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("A cached hash input is unavailable.", fullPath);
        }

        lock (_sync)
        {
            if (_enabled
                && _entries.TryGetValue(fullPath, out var entry)
                && entry.Length == file.Length
                && entry.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks
                && entry.CreationUtcTicks == file.CreationTimeUtc.Ticks)
            {
                _hits++;
                return entry.Sha256;
            }

            var sha256 = ComputeSha256(fullPath);
            _misses++;
            if (_enabled)
            {
                _entries[fullPath] = new CacheEntry(
                    file.Length,
                    file.LastWriteTimeUtc.Ticks,
                    file.CreationTimeUtc.Ticks,
                    sha256);
            }
            return sha256;
        }
    }

    public void SaveFailOpen()
    {
        if (!_enabled)
        {
            return;
        }
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                using (var writer = new StreamWriter(
                           stream,
                           new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.WriteLine(Header);
                    foreach (var item in _entries.OrderBy(item => item.Key, StringComparer.Ordinal))
                    {
                        writer.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Key)));
                        writer.Write('\t');
                        writer.Write(item.Value.Length);
                        writer.Write('\t');
                        writer.Write(item.Value.LastWriteUtcTicks);
                        writer.Write('\t');
                        writer.Write(item.Value.CreationUtcTicks);
                        writer.Write('\t');
                        writer.WriteLine(item.Value.Sha256);
                    }
                }
                File.Copy(temporaryPath, _cachePath, overwrite: true);
                File.Delete(temporaryPath);
                _state = "ready";
            }
            catch
            {
                _state = "save-failed-fail-open";
            }
        }
    }

    public FileHashCacheSnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            return new FileHashCacheSnapshot(_state, _entries.Count, _hits, _misses);
        }
    }

    private void LoadFailOpen()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }
            using var stream = new FileStream(
                _cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (!string.Equals(reader.ReadLine(), Header, StringComparison.Ordinal))
            {
                _state = "schema-mismatch-fail-open";
                return;
            }
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var fields = line.Split('\t');
                if (fields.Length != 5
                    || !long.TryParse(fields[1], out var length)
                    || !long.TryParse(fields[2], out var lastWriteUtcTicks)
                    || !long.TryParse(fields[3], out var creationUtcTicks)
                    || fields[4].Length != 64)
                {
                    continue;
                }
                var path = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
                _entries[path] = new CacheEntry(
                    length,
                    lastWriteUtcTicks,
                    creationUtcTicks,
                    fields[4]);
            }
            _state = "ready";
        }
        catch
        {
            _entries.Clear();
            _state = "load-failed-fail-open";
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(stream);
        var result = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    private sealed record CacheEntry(
        long Length,
        long LastWriteUtcTicks,
        long CreationUtcTicks,
        string Sha256);
}

public sealed record FileHashCacheSnapshot(
    string State,
    int EntryCount,
    int HitCount,
    int MissCount);
