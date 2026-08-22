using CrimeanTom.LoadAcceleration;
using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

public sealed class CompanionColliderCatalog
{
    private readonly object _sync = new();
    private readonly string _home;
    private readonly ColliderGeometryCache _cache;

    public CompanionColliderCatalog()
        : this(Environment.GetEnvironmentVariable("CRIMEAN_TOM_COLLIDER_CACHE_DIR"))
    {
    }

    public CompanionColliderCatalog(string? configuredDirectory)
    {
        _home = ColliderGeometryCache.CatalogHome(configuredDirectory);
        _cache = new ColliderGeometryCache(_home);
    }

    public string Home => _home;

    public GetColliderCatalogResultPayload Get(string? unitTypeId)
    {
        lock (_sync)
        {
            _cache.RefreshFromDisk();
            ColliderUnitGeometryPayload? geometry = null;
            if (!string.IsNullOrWhiteSpace(unitTypeId)
                && _cache.TryGet(unitTypeId, out var found))
            {
                geometry = found;
            }

            var entries = _cache.List();
            return new GetColliderCatalogResultPayload
            {
                SchemaVersion = ColliderGeometryCache.SchemaVersion,
                CacheDirectory = _home,
                GameVersion = geometry?.GameVersion ?? _cache.NewestGameVersion,
                MemoryCount = _cache.MemoryCount,
                FileCount = _cache.FileCount,
                Entries = entries.ToArray(),
                Geometry = geometry,
                Source = "file-cache",
            };
        }
    }
}
