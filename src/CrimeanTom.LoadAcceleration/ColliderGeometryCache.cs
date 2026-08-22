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
    private readonly string _root;

    public ColliderGeometryCache(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
        LoadFilesIntoMemory();
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

    public static string ResolveRoot(string? configuredDirectory, string? persistentDataPath, string gameVersion)
    {
        var version = SanitizeFileToken(string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.Combine(Path.GetFullPath(configuredDirectory), version);
        }

        var basePath = string.IsNullOrWhiteSpace(persistentDataPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrimeanTom")
            : persistentDataPath;
        return Path.Combine(basePath, "CrimeanTom", DirectoryName, version);
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

    private void LoadFilesIntoMemory()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var path in Directory.GetFiles(_root, "*.json"))
        {
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

                lock (_sync)
                {
                    _memory[key] = geometry;
                }
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
            FilePath = FilePath(geometry.UnitTypeId),
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
