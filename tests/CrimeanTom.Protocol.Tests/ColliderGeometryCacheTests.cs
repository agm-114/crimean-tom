using CrimeanTom.LoadAcceleration;
using CrimeanTom.Protocol;

namespace CrimeanTom.Protocol.Tests;

public sealed class ColliderGeometryCacheTests
{
    [Fact]
    public void PersistsHullGeometryToDiskAndServesLaterLookupsFromMemory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "crimean-tom-collider-cache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = new ColliderGeometryCache(root);
            var written = first.Upsert(new ColliderUnitGeometryPayload
            {
                GameVersion = "1.2.3",
                UnitTypeId = "usn_ff_knox_84",
                ClassName = "Knox",
                Source = "test",
                CapturedAtUnixMilliseconds = 1,
                Colliders =
                [
                    new ColliderPrimitivePayload
                    {
                        Kind = "box",
                        Name = "hull",
                        Enabled = true,
                        Center = [0f, 1f, 0f],
                        Size = [10f, 4f, 20f],
                    },
                ],
            });

            Assert.Equal(64, written.Sha256.Length);
            Assert.True(File.Exists(first.FilePathFor("usn_ff_knox_84")));
            Assert.Equal(1, first.MemoryCount);
            Assert.Equal(1, first.FileCount);

            var reloaded = new ColliderGeometryCache(root);
            Assert.True(reloaded.TryGet("USN_FF_KNOX_84", out var fromFile));
            Assert.Equal(written.Sha256, fromFile.Sha256);
            Assert.Equal("box", fromFile.Colliders[0].Kind);
            Assert.Equal(1, fromFile.ColliderCount);

            File.Delete(reloaded.FilePathFor("usn_ff_knox_84"));
            Assert.True(reloaded.TryGet("usn_ff_knox_84", out var fromMemory));
            Assert.Equal(written.Sha256, fromMemory.Sha256);
            Assert.Equal(1, reloaded.MemoryCount);
            Assert.Equal(0, reloaded.FileCount);
            Assert.Equal(0, reloaded.RefreshFromDisk());
            Assert.False(reloaded.TryGet("usn_ff_knox_84", out _));

            var cold = new ColliderGeometryCache(root);
            Assert.False(cold.TryGet("usn_ff_knox_84", out _));
            Assert.Equal(0, cold.MemoryCount);

            var later = first.Upsert(new ColliderUnitGeometryPayload
            {
                GameVersion = "1.2.3",
                UnitTypeId = "usn_ff_knox_84",
                ClassName = "Knox",
                Source = "test",
                CapturedAtUnixMilliseconds = 99,
                Colliders = written.Colliders,
            });
            Assert.Equal(written.Sha256, later.Sha256);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void RefreshFromDiskPicksUpVersionedFilesWithoutANewProcess()
    {
        var home = Path.Combine(
            Path.GetTempPath(),
            "crimean-tom-collider-cache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var catalog = new ColliderGeometryCache(home);
            Assert.Equal(0, catalog.MemoryCount);

            var versioned = new ColliderGeometryCache(ColliderGeometryCache.ResolveRoot(home, "0.8.2"));
            versioned.Upsert(new ColliderUnitGeometryPayload
            {
                GameVersion = "0.8.2",
                UnitTypeId = "usn_ff_knox_84",
                ClassName = "Knox",
                Source = "test",
                Colliders =
                [
                    new ColliderPrimitivePayload
                    {
                        Kind = "sphere",
                        Name = "hull",
                        Enabled = true,
                        Radius = 12f,
                    },
                ],
            });

            Assert.Equal(1, catalog.RefreshFromDisk());
            Assert.True(catalog.TryGet("usn_ff_knox_84", out var loaded));
            Assert.Equal("sphere", loaded.Colliders[0].Kind);
            Assert.Equal("0.8.2", catalog.NewestGameVersion);
            Assert.Equal("0.8.2", catalog.List()[0].GameVersion);
            Assert.Equal(1, catalog.FileCount);
            Assert.Contains(
                Path.DirectorySeparatorChar + "0.8.2" + Path.DirectorySeparatorChar,
                catalog.List()[0].FilePath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveRootNestsCacheUnderGameVersion()
    {
        var configured = Path.Combine(Path.GetTempPath(), "crimean-tom-collider-root");
        var resolved = ColliderGeometryCache.ResolveRoot(configured, "1.0.9f1");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(configured, "1.0.9f1")),
            resolved);
    }

    [Fact]
    public void ResolveRootDoesNotNestWhenConfiguredDirectoryIsAlreadyTheVersionLeaf()
    {
        var configured = Path.Combine(Path.GetTempPath(), "crimean-tom-collider-root", "1.0.9f1");
        var resolved = ColliderGeometryCache.ResolveRoot(configured, "1.0.9f1");
        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public void SanitizedUnitTypeCollisionsUseDistinctFilesAndReloadIndependently()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var cache = new ColliderGeometryCache(root);
            cache.Upsert(Geometry("unit/a", "Alpha", capturedAt: 1));
            cache.Upsert(Geometry("unit?a", "Bravo", capturedAt: 2));

            Assert.NotEqual(cache.FilePathFor("unit/a"), cache.FilePathFor("unit?a"));
            Assert.Equal(2, Directory.GetFiles(root, "*.json").Length);

            var reloaded = new ColliderGeometryCache(root);
            Assert.True(reloaded.TryGet("unit/a", out var alpha));
            Assert.True(reloaded.TryGet("unit?a", out var bravo));
            Assert.Equal("Alpha", alpha.ClassName);
            Assert.Equal("Bravo", bravo.ClassName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RefreshRejectsTamperedFilesInsteadOfServingStaleMemory()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var cache = new ColliderGeometryCache(root);
            cache.Upsert(Geometry("unit", "Original", capturedAt: 1));
            var path = cache.FilePathFor("unit");
            File.WriteAllText(
                path,
                File.ReadAllText(path).Replace(
                    "\"className\":\"Original\"",
                    "\"className\":\"Tampered\"",
                    StringComparison.Ordinal));

            Assert.Equal(0, cache.RefreshFromDisk());
            Assert.False(cache.TryGet("unit", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RefreshFallsBackToOlderVersionWhenSelectedFileIsDeleted()
    {
        var home = CreateTemporaryRoot();
        try
        {
            var older = new ColliderGeometryCache(ColliderGeometryCache.ResolveRoot(home, "1.0"));
            older.Upsert(Geometry("unit", "Older", capturedAt: 10, gameVersion: "1.0"));
            var newer = new ColliderGeometryCache(ColliderGeometryCache.ResolveRoot(home, "2.0"));
            newer.Upsert(Geometry("unit", "Newer", capturedAt: 20, gameVersion: "2.0"));

            var catalog = new ColliderGeometryCache(home);
            Assert.True(catalog.TryGet("unit", out var selected));
            Assert.Equal("Newer", selected.ClassName);

            File.Delete(newer.FilePathFor("unit"));
            Assert.Equal(1, catalog.RefreshFromDisk());
            Assert.True(catalog.TryGet("unit", out selected));
            Assert.Equal("Older", selected.ClassName);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void UpsertRejectsInvalidPrimitiveGeometry()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var geometry = Geometry("unit", "Invalid", capturedAt: 1);
            geometry.Colliders[0].Size = [10f, float.NaN, 20f];

            Assert.Throws<InvalidDataException>(() =>
                new ColliderGeometryCache(root).Upsert(geometry));
            Assert.Empty(Directory.GetFiles(root, "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "crimean-tom-collider-cache",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ColliderUnitGeometryPayload Geometry(
        string unitTypeId,
        string className,
        long capturedAt,
        string gameVersion = "1.2.3") =>
        new()
        {
            GameVersion = gameVersion,
            UnitTypeId = unitTypeId,
            ClassName = className,
            Source = "test",
            CapturedAtUnixMilliseconds = capturedAt,
            Colliders =
            [
                new ColliderPrimitivePayload
                {
                    Kind = "box",
                    Name = "hull",
                    Enabled = true,
                    Center = [0f, 1f, 0f],
                    Size = [10f, 4f, 20f],
                },
            ],
        };
}
