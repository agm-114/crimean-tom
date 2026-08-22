using System.Numerics;
using System.Runtime.CompilerServices;

using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Protocol;

namespace CrimeanTom.Campaign.Engine;

public readonly record struct ColliderRayHit(bool DidHit, float DistanceMeters, string Kind, bool Performed = true);

/// <summary>
/// Raycasts cached ObjectBase-local primitives with BepuPhysics. This is not Unity
/// PhysX or Sea Power Blastzone armor penetration.
/// </summary>
public static class ColliderHitResolver
{
    private const double MeanEarthRadiusMeters = 6_371_000d;

    public static Vector3 ToHullLocal(
        GeoCoordinate point,
        float altitudeMeters,
        GeoCoordinate hullOrigin,
        float hullAltitudeMeters,
        int hullHeadingMilliDegrees)
    {
        var east = EastMeters(hullOrigin, point);
        var north = NorthMeters(hullOrigin, point);
        var up = altitudeMeters - hullAltitudeMeters;
        var heading = hullHeadingMilliDegrees / 1_000d * (Math.PI / 180d);
        var cos = Math.Cos(heading);
        var sin = Math.Sin(heading);
        return new Vector3(
            (float)(east * cos - north * sin),
            up,
            (float)(east * sin + north * cos));
    }

    public static ColliderRayHit Cast(
        ColliderUnitGeometryPayload geometry,
        Vector3 origin,
        Vector3 direction,
        float maximumT)
    {
        if (geometry.Colliders is not { Length: > 0 })
        {
            return new ColliderRayHit(false, 0, string.Empty, Performed: false);
        }

        var lengthSquared = direction.LengthSquared();
        if (lengthSquared < 1e-8f || maximumT <= 0)
        {
            return new ColliderRayHit(false, 0, string.Empty);
        }

        var pool = new BufferPool();
        var simulation = Simulation.Create(
            pool,
            new NarrowPhaseCallbacks(),
            new PoseIntegratorCallbacks(),
            new SolveDescription(1, 1, 1));
        try
        {
            var added = 0;
            foreach (var primitive in geometry.Colliders)
            {
                if (!TryAdd(simulation, primitive))
                {
                    continue;
                }

                added++;
            }

            if (added == 0)
            {
                return new ColliderRayHit(false, 0, string.Empty, Performed: false);
            }

            var handler = new ClosestHitHandler();
            simulation.RayCast(origin, direction, maximumT, ref handler);
            return handler.Hit
                ? new ColliderRayHit(true, handler.T * MathF.Sqrt(lengthSquared), handler.Kind)
                : new ColliderRayHit(false, 0, string.Empty);
        }
        finally
        {
            simulation.Dispose();
            pool.Clear();
        }
    }

    private static bool TryAdd(Simulation simulation, ColliderPrimitivePayload primitive)
    {
        if (!primitive.Enabled || primitive.IsTrigger)
        {
            return false;
        }

        var center = Vec3(primitive.Center);
        var rotation = Quat(primitive.Rotation);
        var kind = primitive.Kind ?? string.Empty;
        TypedIndex shape;
        if (kind.Equals("sphere", StringComparison.OrdinalIgnoreCase))
        {
            if (primitive.Radius <= 0)
            {
                return false;
            }

            shape = simulation.Shapes.Add(new Sphere(primitive.Radius));
        }
        else if (kind.Equals("capsule", StringComparison.OrdinalIgnoreCase))
        {
            if (primitive.Radius <= 0 || primitive.Height <= 0)
            {
                return false;
            }

            var inner = MathF.Max(0, primitive.Height - 2 * primitive.Radius);
            shape = simulation.Shapes.Add(new Capsule(primitive.Radius, inner));
            rotation = rotation * AxisRotation(primitive.Direction);
        }
        else if (kind.Equals("box", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("meshBounds", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("worldBounds", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("bounds", StringComparison.OrdinalIgnoreCase))
        {
            var size = Vec3(primitive.Size);
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0)
            {
                return false;
            }

            shape = simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));
        }
        else
        {
            return false;
        }

        simulation.Statics.Add(new StaticDescription(center, rotation, shape));
        return true;
    }

    private static Quaternion AxisRotation(int direction) =>
        direction switch
        {
            0 => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2),
            2 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2),
            _ => Quaternion.Identity,
        };

    private static Vector3 Vec3(float[] values) =>
        values is { Length: >= 3 } ? new Vector3(values[0], values[1], values[2]) : Vector3.Zero;

    private static Quaternion Quat(float[] values)
    {
        if (values is not { Length: >= 4 })
        {
            return Quaternion.Identity;
        }

        var quaternion = new Quaternion(values[0], values[1], values[2], values[3]);
        return quaternion.LengthSquared() < 1e-8f
            ? Quaternion.Identity
            : Quaternion.Normalize(quaternion);
    }

    private static double EastMeters(GeoCoordinate origin, GeoCoordinate destination)
    {
        var meanLatitude = (origin.LatitudeMicrodegrees + destination.LatitudeMicrodegrees) / 2d / 1_000_000d
            * (Math.PI / 180d);
        var deltaLon = (destination.LongitudeMicrodegrees - origin.LongitudeMicrodegrees) / 1_000_000d
            * (Math.PI / 180d);
        return deltaLon * MeanEarthRadiusMeters * Math.Cos(meanLatitude);
    }

    private static double NorthMeters(GeoCoordinate origin, GeoCoordinate destination)
    {
        var deltaLat = (destination.LatitudeMicrodegrees - origin.LatitudeMicrodegrees) / 1_000_000d
            * (Math.PI / 180d);
        return deltaLat * MeanEarthRadiusMeters;
    }

    private struct ClosestHitHandler : IRayHitHandler
    {
        public bool Hit;
        public float T;
        public string Kind;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRayHit(
            in RayData ray,
            ref float maximumT,
            float t,
            in Vector3 normal,
            CollidableReference collidable,
            int childIndex)
        {
            if (t >= maximumT)
            {
                return;
            }

            maximumT = t;
            T = t;
            Hit = true;
            Kind = "hull";
        }
    }

    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(
            int workerIndex,
            CollidableReference a,
            CollidableReference b,
            ref float speculativeMargin) =>
            a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) =>
            true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(
            int workerIndex,
            CollidablePair pair,
            ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial = new PairMaterialProperties
            {
                FrictionCoefficient = 1,
                MaximumRecoveryVelocity = 2,
                SpringSettings = new SpringSettings(30, 1),
            };
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB,
            ref ConvexContactManifold manifold) =>
            false;

        public void Dispose()
        {
        }
    }

    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

        public readonly bool AllowSubstepsForUnconstrainedBodies => false;

        public readonly bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation)
        {
        }

        public void PrepareForIntegration(float dt)
        {
        }

        public void IntegrateVelocity(
            Vector<int> bodyIndices,
            Vector3Wide position,
            QuaternionWide orientation,
            BodyInertiaWide localInertia,
            Vector<int> integrationMask,
            int workerIndex,
            Vector<float> dt,
            ref BodyVelocityWide velocity)
        {
        }
    }
}
