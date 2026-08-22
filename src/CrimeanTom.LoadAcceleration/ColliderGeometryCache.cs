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
        Directory.CreateDirectory(_root);
        ImportTree();
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

        geometry.SchemaVersion = SchemaVersion;
        geometry.UnitTypeId = key;
        geometry.ColliderCount = geometry.Colliders?.Length ?? 0;
        geometry.Sha256 = ComputeSha256(geometry);
        var path = FilePath(key);
        var json = JsonSerializer.Serialize(geometry, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
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
        ImportDirectory(_root);
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(_root))
        {
            ImportDirectory(directory);
        }
    }

    private void ImportDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.GetFiles(directory, "*.json"))
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

            if (_writeTicks.TryGetValue(path, out var known) && known == ticks)
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
                geometry.UnitTypeId = key;
                geometry.ColliderCount = geometry.Colliders?.Length ?? 0;
                if (string.IsNullOrWhiteSpace(geometry.Sha256))
                {
                    geometry.Sha256 = ComputeSha256(geometry);
                }

                if (_memory.ContainsKey(key)
                    && _paths.TryGetValue(key, out var existingPath)
                    && _writeTicks.TryGetValue(existingPath, out var existingTicks)
                    && existingTicks > ticks)
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
            catch (IOException)
            {
            }
        }
    }

    private ColliderCatalogEntryPayload ToEntry(ColliderUnitGeometryPayload geometry, bool inMemory) =>
        new()
        {
            UnitTypeId = geometry.UnitTypeId,
            ClassName = geometry.ClassName,
            ColliderCount = geometry.ColliderCount,
            Sha256 = geometry.Sha256,
            InMemory = inMemory,
            FilePath = _paths.TryGetValue(geometry.UnitTypeId, out var stored)
                ? stored
                : FilePath(geometry.UnitTypeId),
        };

    private string FilePath(string key) => Path.Combine(_root, $"{SanitizeFileToken(key)}.json");

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
        var copy = new ColliderUnitGeometryPayload
        {
            SchemaVersion = geometry.SchemaVersion,
            GameVersion = geometry.GameVersion,
            UnitTypeId = geometry.UnitTypeId,
            ClassName = geometry.ClassName,
            Source = geometry.Source,
            ColliderCount = geometry.Colliders?.Length ?? 0,
            Colliders = geometry.Colliders ?? Array.Empty<ColliderPrimitivePayload>(),
        };
        var json = JsonSerializer.Serialize(copy, JsonOptions);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        var result = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            result.Append(value.ToString("x2"));
        }

        return result.ToString();
    }
}
