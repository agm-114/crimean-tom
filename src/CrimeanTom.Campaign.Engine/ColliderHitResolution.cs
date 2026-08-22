using CrimeanTom.LoadAcceleration;
using CrimeanTom.Protocol;

namespace CrimeanTom.Campaign.Engine;

/// <summary>
/// Optional hull catalog for Bepu raycast terminal hits. Absent or empty catalogs
/// keep the seeded range-roll path so theater golden hashes stay stable.
/// </summary>
public static class ColliderHitResolution
{
    private static readonly object EnvironmentCatalogSync = new();
    private static readonly AsyncLocal<IReadOnlyDictionary<string, ColliderUnitGeometryPayload>?> OverrideLocal = new();
    private static IReadOnlyDictionary<string, ColliderUnitGeometryPayload>? _environmentCatalog;
    private static bool _environmentCatalogResolved;

    public static IReadOnlyDictionary<string, ColliderUnitGeometryPayload>? CatalogOverride
    {
        get => OverrideLocal.Value;
        set => OverrideLocal.Value = value;
    }

    public static IReadOnlyDictionary<string, ColliderUnitGeometryPayload>? ActiveCatalog()
    {
        if (CatalogOverride is not null)
        {
            return CatalogOverride.Count == 0 ? null : CatalogOverride;
        }

        lock (EnvironmentCatalogSync)
        {
            if (!_environmentCatalogResolved)
            {
                _environmentCatalog = TryLoadFromEnvironment();
                _environmentCatalogResolved = true;
            }

            return _environmentCatalog;
        }
    }

    public static void ResetEnvironmentCatalog()
    {
        lock (EnvironmentCatalogSync)
        {
            _environmentCatalog = null;
            _environmentCatalogResolved = false;
        }
    }

    public static IReadOnlyDictionary<string, ColliderUnitGeometryPayload>? TryLoadFromEnvironment()
    {
        var flag = Environment.GetEnvironmentVariable("CRIMEAN_TOM_COLLIDER_HIT_RESOLUTION");
        if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cache = new ColliderGeometryCache(
            ColliderGeometryCache.CatalogHome(
                Environment.GetEnvironmentVariable("CRIMEAN_TOM_COLLIDER_CACHE_DIR")));
        if (cache.MemoryCount == 0)
        {
            return null;
        }

        var hulls = new Dictionary<string, ColliderUnitGeometryPayload>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cache.List())
        {
            if (cache.TryGet(entry.UnitTypeId, out var geometry))
            {
                hulls[entry.UnitTypeId] = geometry;
            }
        }

        return hulls.Count == 0 ? null : hulls;
    }

    public static bool TryGetHull(
        IReadOnlyDictionary<string, ColliderUnitGeometryPayload>? catalog,
        string catalogUnitId,
        out ColliderUnitGeometryPayload geometry)
    {
        geometry = null!;
        return catalog is not null
            && !string.IsNullOrWhiteSpace(catalogUnitId)
            && catalog.TryGetValue(catalogUnitId, out geometry!)
            && geometry.Colliders is { Length: > 0 };
    }
}
