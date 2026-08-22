using System.Text.Json;

using CrimeanTom.LoadAcceleration;
using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeColliderCatalog
{
    internal const int MaximumPipeCatalogEntries = 64;

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
        var cache = TryCache();
        if (cache is null)
        {
            return SummarizeCapture(null, scanned: 0, captured: 0, "collider-cache-unavailable");
        }

        var scanned = 0;
        var captured = 0;
        var wanted = string.IsNullOrWhiteSpace(request.UnitTypeId)
            ? null
            : request.UnitTypeId.Trim();
        if (!Singleton<ObjectsManager>.InstanceExists())
        {
            return SummarizeCapture(cache, scanned, captured, "no-objects-manager");
        }

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            if (!request.ForceRefresh && cache.TryGet(unitTypeId, out _))
            {
                continue;
            }

            if (!handled.Add(unitTypeId))
            {
                continue;
            }

            try
            {
                cache.Upsert(Extract(unit, unitTypeId));
                captured++;
            }
            catch (IOException exception)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Sea Power Runtime Bridge] collider cache write failed for {unitTypeId}: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Sea Power Runtime Bridge] collider cache write failed for {unitTypeId}: {exception.Message}");
            }
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
        var cache = TryCache();
        if (cache is null)
        {
            return new GetColliderCatalogResultPayload
            {
                SchemaVersion = ColliderGeometryCache.SchemaVersion,
                GameVersion = Application.version ?? string.Empty,
                Source = "plugin-memory",
            };
        }

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
            FileCount = cache.FileCount,
            Entries = PipeEntries(entries, request.UnitTypeId),
            Geometry = geometry,
            Source = "plugin-memory",
        };
    }

    private static CaptureColliderCatalogResultPayload SummarizeCapture(
        ColliderGeometryCache? cache,
        int scanned,
        int captured,
        string postcondition)
    {
        var entries = cache?.List();
        return new CaptureColliderCatalogResultPayload
        {
            SchemaVersion = ColliderGeometryCache.SchemaVersion,
            CacheDirectory = cache?.Root ?? string.Empty,
            GameVersion = _gameVersion,
            ScannedObjectCount = scanned,
            CapturedUnitCount = captured,
            MemoryCount = cache?.MemoryCount ?? 0,
            FileCount = cache?.FileCount ?? 0,
            Postcondition = postcondition,
            Entries = PipeEntries(entries ?? Array.Empty<ColliderCatalogEntryPayload>(), unitTypeId: null),
        };
    }

    private static ColliderCatalogEntryPayload[] PipeEntries(
        IReadOnlyList<ColliderCatalogEntryPayload> entries,
        string? unitTypeId)
    {
        IEnumerable<ColliderCatalogEntryPayload> selected = entries;
        if (!string.IsNullOrWhiteSpace(unitTypeId))
        {
            var wanted = unitTypeId.Trim();
            selected = selected.Where(item =>
                string.Equals(item.UnitTypeId, wanted, StringComparison.OrdinalIgnoreCase));
        }

        return selected.Take(MaximumPipeCatalogEntries).ToArray();
    }

    public static string CurrentRoot()
    {
        try
        {
            return TryCache()?.Root ?? string.Empty;
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogWarning(
                $"[Sea Power Runtime Bridge] collider cache root unavailable: {exception.Message}");
            return string.Empty;
        }
    }

    private static ColliderGeometryCache? TryCache()
    {
        lock (Sync)
        {
            var gameVersion = Application.version ?? "unknown";
            if (_cache is not null
                && string.Equals(_gameVersion, gameVersion, StringComparison.Ordinal))
            {
                return _cache;
            }

            try
            {
                var configured = System.Environment.GetEnvironmentVariable("CRIMEAN_TOM_COLLIDER_CACHE_DIR");
                _cache = new ColliderGeometryCache(
                    ColliderGeometryCache.ResolveRoot(configured, gameVersion));
                _gameVersion = gameVersion;
                return _cache;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Sea Power Runtime Bridge] collider cache unavailable: {exception.Message}");
                _cache = null;
                return null;
            }
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
            primitive.Enabled = collider.enabled && collider.gameObject.activeInHierarchy;
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
            Size = Vec(ToRootLocalSize(root, new Vector3(
                Mathf.Abs(box.size.x * scale.x),
                Mathf.Abs(box.size.y * scale.y),
                Mathf.Abs(box.size.z * scale.z)))),
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
            Radius = ToRootLocalLength(root, radius),
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
            Radius = ToRootLocalLength(root, capsule.radius * radiusScale),
            Height = ToRootLocalLength(root, capsule.height * heightScale),
            Direction = capsule.direction,
        };
    }

    private static ColliderPrimitivePayload MeshBounds(Transform root, MeshCollider mesh)
    {
        var shared = mesh.sharedMesh;
        if (shared is null)
        {
            return WorldBounds(root, mesh);
        }

        var bounds = shared.bounds;
        var worldCenter = mesh.transform.TransformPoint(bounds.center);
        var localRotation = Quaternion.Inverse(root.rotation) * mesh.transform.rotation;
        var scale = mesh.transform.lossyScale;
        return new ColliderPrimitivePayload
        {
            Kind = "meshBounds",
            Center = Vec(root.InverseTransformPoint(worldCenter)),
            Size = Vec(ToRootLocalSize(root, new Vector3(
                Mathf.Abs(bounds.size.x * scale.x),
                Mathf.Abs(bounds.size.y * scale.y),
                Mathf.Abs(bounds.size.z * scale.z)))),
            Rotation = Quat(localRotation),
        };
    }

    private static ColliderPrimitivePayload BoundsOf(Transform root, Collider collider) =>
        WorldBounds(root, collider);

    private static ColliderPrimitivePayload WorldBounds(Transform root, Collider collider)
    {
        var bounds = collider.bounds;
        var min = bounds.min;
        var max = bounds.max;
        var localMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var localMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (var index = 0; index < 8; index++)
        {
            var corner = new Vector3(
                (index & 1) == 0 ? min.x : max.x,
                (index & 2) == 0 ? min.y : max.y,
                (index & 4) == 0 ? min.z : max.z);
            var local = root.InverseTransformPoint(corner);
            localMin = Vector3.Min(localMin, local);
            localMax = Vector3.Max(localMax, local);
        }

        return new ColliderPrimitivePayload
        {
            Kind = "worldBounds",
            Center = Vec((localMin + localMax) * 0.5f),
            Size = Vec(new Vector3(
                Mathf.Abs(localMax.x - localMin.x),
                Mathf.Abs(localMax.y - localMin.y),
                Mathf.Abs(localMax.z - localMin.z))),
        };
    }

    private static Vector3 ToRootLocalSize(Transform root, Vector3 worldSize)
    {
        var scale = root.lossyScale;
        return new Vector3(
            DivideAbs(worldSize.x, scale.x),
            DivideAbs(worldSize.y, scale.y),
            DivideAbs(worldSize.z, scale.z));
    }

    private static float ToRootLocalLength(Transform root, float worldLength)
    {
        var scale = root.lossyScale;
        var max = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        return DivideAbs(worldLength, max);
    }

    private static float DivideAbs(float value, float scale)
    {
        var abs = Mathf.Abs(scale);
        return abs < 1e-8f ? Mathf.Abs(value) : Mathf.Abs(value) / abs;
    }

    private static float[] Vec(Vector3 value) => new[] { value.x, value.y, value.z };

    private static float[] Quat(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
}
