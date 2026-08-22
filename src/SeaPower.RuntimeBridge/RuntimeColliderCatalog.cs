using System.Text.Json;

using CrimeanTom.LoadAcceleration;
using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeColliderCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly object Sync = new();
    private static ColliderGeometryCache? _cache;
    private static string _gameVersion = string.Empty;

    public static CaptureColliderCatalogResultPayload Capture(string payloadJson)
    {
        var request = string.IsNullOrWhiteSpace(payloadJson)
            ? new CaptureColliderCatalogRequestPayload()
            : JsonSerializer.Deserialize<CaptureColliderCatalogRequestPayload>(payloadJson, JsonOptions)
                ?? new CaptureColliderCatalogRequestPayload();
        var cache = Cache();
        var scanned = 0;
        var captured = 0;
        var wanted = string.IsNullOrWhiteSpace(request.UnitTypeId)
            ? null
            : request.UnitTypeId.Trim();
        if (!Singleton<ObjectsManager>.InstanceExists())
        {
            return SummarizeCapture(cache, scanned, captured, "no-objects-manager");
        }

        var objects = Singleton<ObjectsManager>.Instance._listOfAllObjects;
        foreach (var unit in objects)
        {
            if (unit is null)
            {
                continue;
            }

            scanned++;
            var unitTypeId = UnitTypeId(unit);
            if (string.IsNullOrWhiteSpace(unitTypeId))
            {
                continue;
            }

            if (wanted is not null
                && !string.Equals(wanted, unitTypeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extracted = Extract(unit, unitTypeId);
            if (!request.ForceRefresh
                && cache.TryGet(unitTypeId, out var existing)
                && existing.ColliderCount >= extracted.ColliderCount)
            {
                continue;
            }

            cache.Upsert(extracted);
            captured++;
        }

        return SummarizeCapture(
            cache,
            scanned,
            captured,
            captured > 0 || cache.MemoryCount > 0
                ? "collider-catalog-cached"
                : "no-unit-colliders-captured");
    }

    public static GetColliderCatalogResultPayload Get(string payloadJson)
    {
        var request = string.IsNullOrWhiteSpace(payloadJson)
            ? new CaptureColliderCatalogRequestPayload()
            : JsonSerializer.Deserialize<CaptureColliderCatalogRequestPayload>(payloadJson, JsonOptions)
                ?? new CaptureColliderCatalogRequestPayload();
        var cache = Cache();
        ColliderUnitGeometryPayload? geometry = null;
        if (!string.IsNullOrWhiteSpace(request.UnitTypeId)
            && cache.TryGet(request.UnitTypeId, out var found))
        {
            geometry = found;
        }

        var entries = cache.List();
        return new GetColliderCatalogResultPayload
        {
            SchemaVersion = ColliderGeometryCache.SchemaVersion,
            CacheDirectory = cache.Root,
            GameVersion = _gameVersion,
            MemoryCount = cache.MemoryCount,
            FileCount = entries.Count,
            Entries = entries.ToArray(),
            Geometry = geometry,
            Source = "plugin-memory",
        };
    }

    private static CaptureColliderCatalogResultPayload SummarizeCapture(
        ColliderGeometryCache cache,
        int scanned,
        int captured,
        string postcondition)
    {
        var entries = cache.List();
        return new CaptureColliderCatalogResultPayload
        {
            SchemaVersion = ColliderGeometryCache.SchemaVersion,
            CacheDirectory = cache.Root,
            GameVersion = _gameVersion,
            ScannedObjectCount = scanned,
            CapturedUnitCount = captured,
            MemoryCount = cache.MemoryCount,
            FileCount = entries.Count,
            Postcondition = postcondition,
            Entries = entries.ToArray(),
        };
    }

    public static string CurrentRoot() => Cache().Root;

    private static ColliderGeometryCache Cache()
    {
        lock (Sync)
        {
            var gameVersion = Application.version ?? "unknown";
            if (_cache is not null
                && string.Equals(_gameVersion, gameVersion, StringComparison.Ordinal))
            {
                return _cache;
            }

            _gameVersion = gameVersion;
            var configured = System.Environment.GetEnvironmentVariable("CRIMEAN_TOM_COLLIDER_CACHE_DIR");
            _cache = new ColliderGeometryCache(
                ColliderGeometryCache.ResolveRoot(configured, gameVersion));
            return _cache;
        }
    }

    private static string UnitTypeId(ObjectBase unit)
    {
        var iniName = unit._obp?._objectIniName;
        if (!string.IsNullOrWhiteSpace(iniName))
        {
            return iniName.Trim();
        }

        return string.IsNullOrWhiteSpace(unit.ClassName) ? string.Empty : unit.ClassName.Trim();
    }

    private static ColliderUnitGeometryPayload Extract(ObjectBase unit, string unitTypeId)
    {
        var root = unit.transform;
        var colliders = unit.GetComponentsInChildren<Collider>(true);
        var primitives = new List<ColliderPrimitivePayload>(colliders.Length);
        foreach (var collider in colliders)
        {
            if (collider is null
                || string.Equals(collider.GetType().Name, "TerrainCollider", StringComparison.Ordinal))
            {
                continue;
            }

            var primitive = collider switch
            {
                BoxCollider box => Box(root, box),
                SphereCollider sphere => Sphere(root, sphere),
                CapsuleCollider capsule => Capsule(root, capsule),
                MeshCollider mesh => MeshBounds(root, mesh),
                _ => BoundsOf(root, collider),
            };
            primitive.Name = collider.name ?? string.Empty;
            primitive.IsTrigger = collider.isTrigger;
            primitive.Enabled = collider.enabled;
            primitives.Add(primitive);
        }

        return new ColliderUnitGeometryPayload
        {
            SchemaVersion = ColliderGeometryCache.SchemaVersion,
            GameVersion = _gameVersion,
            UnitTypeId = unitTypeId,
            ClassName = unit.ClassName ?? string.Empty,
            Source = "live-objectbase",
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Colliders = primitives.ToArray(),
        };
    }

    private static ColliderPrimitivePayload Box(Transform root, BoxCollider box)
    {
        var worldCenter = box.transform.TransformPoint(box.center);
        var localRotation = Quaternion.Inverse(root.rotation) * box.transform.rotation;
        var scale = box.transform.lossyScale;
        return new ColliderPrimitivePayload
        {
            Kind = "box",
            Center = Vec(root.InverseTransformPoint(worldCenter)),
            Size = Vec(new Vector3(
                Mathf.Abs(box.size.x * scale.x),
                Mathf.Abs(box.size.y * scale.y),
                Mathf.Abs(box.size.z * scale.z))),
            Rotation = Quat(localRotation),
        };
    }

    private static ColliderPrimitivePayload Sphere(Transform root, SphereCollider sphere)
    {
        var worldCenter = sphere.transform.TransformPoint(sphere.center);
        var scale = sphere.transform.lossyScale;
        var radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        return new ColliderPrimitivePayload
        {
            Kind = "sphere",
            Center = Vec(root.InverseTransformPoint(worldCenter)),
            Radius = radius,
        };
    }

    private static ColliderPrimitivePayload Capsule(Transform root, CapsuleCollider capsule)
    {
        var worldCenter = capsule.transform.TransformPoint(capsule.center);
        var localRotation = Quaternion.Inverse(root.rotation) * capsule.transform.rotation;
        var scale = capsule.transform.lossyScale;
        var heightScale = capsule.direction switch
        {
            0 => Mathf.Abs(scale.x),
            2 => Mathf.Abs(scale.z),
            _ => Mathf.Abs(scale.y),
        };
        var radiusScale = capsule.direction switch
        {
            0 => Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)),
            2 => Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)),
            _ => Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)),
        };
        return new ColliderPrimitivePayload
        {
            Kind = "capsule",
            Center = Vec(root.InverseTransformPoint(worldCenter)),
            Rotation = Quat(localRotation),
            Radius = capsule.radius * radiusScale,
            Height = capsule.height * heightScale,
            Direction = capsule.direction,
        };
    }

    private static ColliderPrimitivePayload MeshBounds(Transform root, MeshCollider mesh) =>
        BoundsOf(root, mesh);

    private static ColliderPrimitivePayload BoundsOf(Transform root, Collider collider)
    {
        var bounds = collider.bounds;
        var min = root.InverseTransformPoint(bounds.min);
        var max = root.InverseTransformPoint(bounds.max);
        return new ColliderPrimitivePayload
        {
            Kind = collider is MeshCollider ? "meshBounds" : "bounds",
            Center = Vec((min + max) * 0.5f),
            Size = Vec(new Vector3(
                Mathf.Abs(max.x - min.x),
                Mathf.Abs(max.y - min.y),
                Mathf.Abs(max.z - min.z))),
        };
    }

    private static float[] Vec(Vector3 value) => new[] { value.x, value.y, value.z };

    private static float[] Quat(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
}
