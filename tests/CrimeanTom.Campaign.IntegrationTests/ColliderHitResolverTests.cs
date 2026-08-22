using System.Numerics;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Engine;
using CrimeanTom.Protocol;

namespace CrimeanTom.Campaign.IntegrationTests;

public sealed class ColliderHitResolverTests
{
    [Fact]
    public void BepuRayHitsABoxHullAndMissesWhenAimedBesideIt()
    {
        var hull = KnoxBox("usn_knox");
        var origin = new Vector3(0, 2, -80);
        var hit = ColliderHitResolver.Cast(hull, origin, new Vector3(0, 0, 80), maximumT: 1.5f);
        Assert.True(hit.DidHit);

        var miss = ColliderHitResolver.Cast(hull, new Vector3(80, 2, -80), new Vector3(0, 0, 80), maximumT: 1.5f);
        Assert.False(miss.DidHit);
    }

    [Fact]
    public void ConvertsGeodeticOffsetIntoHullLocalEastAndNorth()
    {
        var origin = GeoCoordinate.FromMicrodegrees(44_000_000, 34_000_000);
        var east = GeoCoordinate.FromMicrodegrees(44_000_000, 34_001_000);
        var local = ColliderHitResolver.ToHullLocal(east, 0, origin, 0, hullHeadingMilliDegrees: 0);
        Assert.True(local.X > 50, local.ToString());
        Assert.InRange(local.Z, -5, 5);
    }

    [Fact]
    public void CatalogOverrideDoesNotChangeDefaultTheaterHashPath()
    {
        Assert.Null(ColliderHitResolution.ActiveCatalog());
        ColliderHitResolution.CatalogOverride = new Dictionary<string, ColliderUnitGeometryPayload>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["usn_knox"] = KnoxBox("usn_knox"),
        };
        try
        {
            Assert.True(ColliderHitResolution.TryGetHull(
                ColliderHitResolution.ActiveCatalog(),
                "USN_KNOX",
                out var hull));
            Assert.Equal("box", hull.Colliders[0].Kind);
        }
        finally
        {
            ColliderHitResolution.CatalogOverride = null;
        }

        Assert.Null(ColliderHitResolution.ActiveCatalog());
    }

    private static ColliderUnitGeometryPayload KnoxBox(string unitTypeId) =>
        new()
        {
            UnitTypeId = unitTypeId,
            ClassName = "Knox",
            Source = "test",
            Colliders =
            [
                new ColliderPrimitivePayload
                {
                    Kind = "box",
                    Name = "hull",
                    Enabled = true,
                    Center = [0f, 0f, 0f],
                    Size = [12f, 8f, 40f],
                },
            ],
        };
}
