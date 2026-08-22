using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CrimeanTom.Protocol;

namespace CrimeanTom.LoadAcceleration;

public sealed class ColliderGeometryCache
{
    public const int SchemaVersion = 1;
    public const string DirectoryName = "collider-catalog";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, ColliderUnitGeometryPayload> _memory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _writeTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;

    public ColliderGeometryCache(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            ImportTree();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string Root => _root;

    public int MemoryCount
    {
        get
        {
            lock (_sync)
            {
                return _memory.Count;
            }
        }
    }

    public int FileCount
    {
        get
        {
            lock (_sync)
            {
                var count = 0;
                foreach (var path in _paths.Values)
                {
                    if (File.Exists(path))
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public string NewestGameVersion
    {
        get
        {
            lock (_sync)
            {
                return _memory.Values
                    .OrderByDescending(item => item.CapturedAtUnixMilliseconds)
                    .Select(item => item.GameVersion)
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                    ?? string.Empty;
            }
        }
    }

    public static string CatalogHome(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrimeanTom",
            DirectoryName);
    }

    public static string ResolveRoot(string? configuredDirectory, string gameVersion)
    {
        var version = SanitizeFileToken(string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion);
        var home = CatalogHome(configuredDirectory);
        var leaf = Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, version, StringComparison.OrdinalIgnoreCase))
        {
            return home;
        }

        return Path.Combine(home, version);
    }

    public int RefreshFromDisk()
    {
        lock (_sync)
        {
            ImportTree();
            return _memory.Count;
        }
    }

    public bool TryGet(string unitTypeId, out ColliderUnitGeometryPayload geometry)
    {
        var key = NormalizeKey(unitTypeId);
        lock (_sync)
        {
            if (_memory.TryGetValue(key, out geometry!))
            {
                return true;
            }
        }

        geometry = null!;
        return false;
    }

    public ColliderUnitGeometryPayload Upsert(ColliderUnitGeometryPayload geometry)
    {
        if (geometry is null)
        {
            throw new ArgumentNullException(nameof(geometry));
        }
        var key = NormalizeKey(geometry.UnitTypeId);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Collider geometry requires a unit type id.", nameof(geometry));
        }
        if (key.Length > 256)
        {
            throw new ArgumentException("Collider unit type ids cannot exceed 256 characters.", nameof(geometry));
        }

        geometry.SchemaVersion = SchemaVersion;
        geometry.UnitTypeId = key;
        geometry.ColliderCount = geometry.Colliders?.Length ?? 0;
        ValidateGeometry(geometry);
        geometry.Sha256 = ComputeSha256(geometry);
        var path = FilePath(key);
        var json = JsonSerializer.Serialize(geometry, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllBytes(temporaryPath, bytes);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }

        lock (_sync)
        {
            _memory[key] = geometry;
            _paths[key] = path;
            try
            {
                _writeTicks[path] = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return geometry;
    }

    public IReadOnlyList<ColliderCatalogEntryPayload> List()
    {
        lock (_sync)
        {
            return _memory.Values
                .OrderBy(item => item.UnitTypeId, StringComparer.OrdinalIgnoreCase)
                .Select(item => ToEntry(item, inMemory: true))
                .ToArray();
        }
    }

    public string FilePathFor(string unitTypeId) => FilePath(NormalizeKey(unitTypeId));

    private void ImportTree()
    {
        // Rebuild the index so deleted, replaced, or newly-corrupt files cannot
        // leave stale geometry in memory. The runtime cache does not refresh
        // during capture, while the companion intentionally refreshes on reads.
        _memory.Clear();
        _paths.Clear();
        _writeTicks.Clear();
        ImportDirectory(_root);
        if (Directory.Exists(_root))
        {
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(_root);
            }
            catch (IOException)
            {
                directories = [];
            }
            catch (UnauthorizedAccessException)
            {
                directories = [];
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                ImportDirectory(directory);
            }
        }
    }

    private void ImportDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            long ticks;
            try
            {
                ticks = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var geometry = JsonSerializer.Deserialize<ColliderUnitGeometryPayload>(json, JsonOptions);
                if (geometry is null || string.IsNullOrWhiteSpace(geometry.UnitTypeId))
                {
                    continue;
                }

                var key = NormalizeKey(geometry.UnitTypeId);
                if (key.Length > 256)
                {
                    continue;
                }
                geometry.UnitTypeId = key;
                geometry.ColliderCount = geometry.Colliders?.Length ?? 0;
                ValidateGeometry(geometry);
                if (string.IsNullOrWhiteSpace(geometry.Sha256)
                    || !string.Equals(
                    geometry.Sha256,
                    ComputeSha256(geometry),
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_memory.ContainsKey(key)
                    && _paths.TryGetValue(key, out var existingPath)
                    && _writeTicks.TryGetValue(existingPath, out var existingTicks)
                    && (_memory[key].CapturedAtUnixMilliseconds > geometry.CapturedAtUnixMilliseconds
                        || _memory[key].CapturedAtUnixMilliseconds == geometry.CapturedAtUnixMilliseconds
                            && existingTicks > ticks))
                {
                    _writeTicks[path] = ticks;
                    continue;
                }

                _memory[key] = geometry;
                _paths[key] = path;
                _writeTicks[path] = ticks;
            }
            catch (JsonException)
            {
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private ColliderCatalogEntryPayload ToEntry(ColliderUnitGeometryPayload geometry, bool inMemory) =>
        new()
        {
            UnitTypeId = geometry.UnitTypeId,
            ClassName = geometry.ClassName,
            GameVersion = geometry.GameVersion,
            ColliderCount = geometry.ColliderCount,
            Sha256 = geometry.Sha256,
            InMemory = inMemory,
            FilePath = _paths.TryGetValue(geometry.UnitTypeId, out var stored)
                ? stored
                : FilePath(geometry.UnitTypeId),
        };

    private string FilePath(string key)
    {
        var token = SanitizeFileToken(key);
        if (token.Length > 80)
        {
            token = token[..80];
        }
        var hash = ComputeTextSha256(key)[..16];
        return Path.Combine(_root, $"{token}-{hash}.json");
    }

    private static string NormalizeKey(string unitTypeId) =>
        string.IsNullOrWhiteSpace(unitTypeId) ? string.Empty : unitTypeId.Trim().ToLowerInvariant();

    public static string SanitizeFileToken(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string ComputeSha256(ColliderUnitGeometryPayload geometry)
    {
        // Hash an explicit binary wire shape instead of serialized JSON. Sea Power
        // and the companion can carry different System.Text.Json builds whose
        // float formatting is semantically equivalent but byte-for-byte different.
        using var canonical = new MemoryStream();
        using (var writer = new BinaryWriter(canonical, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(geometry.SchemaVersion);
            writer.Write(geometry.GameVersion ?? string.Empty);
            writer.Write(geometry.UnitTypeId ?? string.Empty);
            writer.Write(geometry.ClassName ?? string.Empty);
            writer.Write(geometry.Source ?? string.Empty);
            var colliders = geometry.Colliders ?? Array.Empty<ColliderPrimitivePayload>();
            writer.Write(colliders.Length);
            foreach (var collider in colliders)
            {
                writer.Write(collider.Kind ?? string.Empty);
                writer.Write(collider.Name ?? string.Empty);
                writer.Write(collider.IsTrigger);
                writer.Write(collider.Enabled);
                WriteFloats(writer, collider.Center);
                WriteFloats(writer, collider.Size);
                WriteFloats(writer, collider.Rotation);
                writer.Write(collider.Radius);
                writer.Write(collider.Height);
                writer.Write(collider.Direction);
            }
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(canonical.ToArray());
        var result = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            result.Append(value.ToString("x2"));
        }

        return result.ToString();
    }

    private static void WriteFloats(BinaryWriter writer, float[]? values)
    {
        values ??= Array.Empty<float>();
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static void ValidateGeometry(ColliderUnitGeometryPayload geometry)
    {
        if (geometry.SchemaVersion != SchemaVersion
            || string.IsNullOrWhiteSpace(geometry.GameVersion)
            || string.IsNullOrWhiteSpace(geometry.UnitTypeId)
            || geometry.ColliderCount != (geometry.Colliders?.Length ?? 0))
        {
            throw new InvalidDataException("Collider geometry metadata is invalid.");
        }

        foreach (var collider in geometry.Colliders ?? [])
        {
            if (collider is null
                || collider.Center is not { Length: 0 or 3 }
                || collider.Center.Any(value => !IsFinite(value))
                || collider.Rotation is not { Length: 0 or 4 }
                || collider.Rotation.Any(value => !IsFinite(value)))
            {
                throw new InvalidDataException("Collider geometry contains an invalid transform.");
            }

            var boundsKind = collider.Kind is "box" or "meshBounds" or "worldBounds";
            if (boundsKind
                && (collider.Size is not { Length: 3 }
                    || collider.Size.Any(value => !IsFinite(value) || value < 0)))
            {
                throw new InvalidDataException("Collider geometry contains invalid bounds.");
            }
            if ((collider.Kind == "sphere"
                    && (!IsFinite(collider.Radius) || collider.Radius < 0))
                || (collider.Kind == "capsule"
                    && (!IsFinite(collider.Radius) || collider.Radius < 0
                        || !IsFinite(collider.Height) || collider.Height < 0
                        || collider.Direction is < 0 or > 2))
                || collider.Kind is not ("box" or "sphere" or "capsule" or "meshBounds" or "worldBounds"))
            {
                throw new InvalidDataException("Collider geometry contains an unsupported primitive.");
            }
        }
    }

    private static string ComputeTextSha256(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var result = new StringBuilder(bytes.Length * 2);
        foreach (var item in bytes)
        {
            result.Append(item.ToString("x2"));
        }
        return result.ToString();
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
