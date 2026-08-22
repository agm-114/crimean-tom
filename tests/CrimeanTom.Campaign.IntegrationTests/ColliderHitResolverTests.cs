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
        Assert.True(miss.Performed);
    }

    [Fact]
    public void TriggerOnlyHullDoesNotCountAsAGeometricMiss()
    {
        var hull = new ColliderUnitGeometryPayload
        {
            UnitTypeId = "usn_knox",
            Colliders =
            [
                new ColliderPrimitivePayload
                {
                    Kind = "box",
                    Enabled = true,
                    IsTrigger = true,
                    Center = [0f, 0f, 0f],
                    Size = [12f, 8f, 40f],
                },
            ],
        };

        var hit = ColliderHitResolver.Cast(hull, new Vector3(0, 2, -80), new Vector3(0, 0, 80), maximumT: 1.5f);
        Assert.False(hit.Performed);
        Assert.False(hit.DidHit);
    }

    [Fact]
    public void ZeroRotationQuaternionDoesNotNaNTheRaycast()
    {
        var hull = KnoxBox("usn_knox");
        hull.Colliders[0].Rotation = [0f, 0f, 0f, 0f];
        var hit = ColliderHitResolver.Cast(hull, new Vector3(0, 2, -80), new Vector3(0, 0, 80), maximumT: 1.5f);
        Assert.True(hit.Performed);
        Assert.True(hit.DidHit);
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
    public void RotatesGeodeticOffsetByHullHeading()
    {
        var origin = GeoCoordinate.FromMicrodegrees(44_000_000, 34_000_000);
        var east = GeoCoordinate.FromMicrodegrees(44_000_000, 34_001_000);
        var local = ColliderHitResolver.ToHullLocal(
            east,
            0,
            origin,
            0,
            hullHeadingMilliDegrees: 90_000);

        Assert.InRange(local.X, -5, 5);
        Assert.True(local.Z > 50, local.ToString());
    }

    [Fact]
    public void UsesTheShortLongitudeArcAcrossTheDateLine()
    {
        var westOfLine = GeoCoordinate.FromMicrodegrees(0, 179_999_500);
        var eastOfLine = GeoCoordinate.FromMicrodegrees(0, -179_999_500);
        var local = ColliderHitResolver.ToHullLocal(
            eastOfLine,
            0,
            westOfLine,
            0,
            hullHeadingMilliDegrees: 0);

        Assert.InRange(local.X, 100, 120);
        Assert.InRange(local.Z, -1, 1);
    }

    [Fact]
    public void InvalidRayDoesNotClaimThatGeometryWasTested()
    {
        var hit = ColliderHitResolver.Cast(
            KnoxBox("usn_knox"),
            Vector3.Zero,
            new Vector3(float.NaN, 0, 1),
            maximumT: 1.5f);

        Assert.False(hit.Performed);
        Assert.False(hit.DidHit);
    }

    [Fact]
    public void HonorsUnityCapsuleAxisDirection()
    {
        var hull = new ColliderUnitGeometryPayload
        {
            UnitTypeId = "capsule",
            Colliders =
            [
                new ColliderPrimitivePayload
                {
                    Kind = "capsule",
                    Enabled = true,
                    Center = [0f, 0f, 0f],
                    Radius = 1f,
                    Height = 10f,
                    Direction = 0,
                },
            ],
        };

        var hit = ColliderHitResolver.Cast(
            hull,
            new Vector3(4, 0, -10),
            new Vector3(0, 0, 20),
            maximumT: 1f);
        Assert.True(hit.DidHit);
    }

    [Fact]
    public void CatalogOverrideDoesNotChangeDefaultTheaterHashPath()
    {
        ColliderHitResolution.ResetEnvironmentCatalog();
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
