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

            var reloaded = new ColliderGeometryCache(root);
            Assert.True(reloaded.TryGet("USN_FF_KNOX_84", out var fromFile));
            Assert.Equal(written.Sha256, fromFile.Sha256);
            Assert.Equal("box", fromFile.Colliders[0].Kind);
            Assert.Equal(1, fromFile.ColliderCount);

            File.Delete(reloaded.FilePathFor("usn_ff_knox_84"));
            Assert.True(reloaded.TryGet("usn_ff_knox_84", out var fromMemory));
            Assert.Equal(written.Sha256, fromMemory.Sha256);

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
}
