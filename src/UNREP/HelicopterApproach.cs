using SeaPower;
using UnityEngine;

namespace UNREP;

// Own final-approach motion, then relinquish it completely to HelicopterLanding.
// SI units keep the acceleration, stopping-distance and attitude limits consistent.
internal class HelicopterApproach
{
    private const float KnotsToMetres = 0.514444f;
    private static readonly float Metre = Utils.heightInMeterToUnity(1);
    private readonly Helicopter aircraft;
    private readonly Vessel ship;
    private readonly Vector3 gate;
    private readonly Vector3 entry;
    private readonly float heading;
    private readonly float horizontalAcceleration;
    private Vector3 velocity;
    private Vector3 acceleration;
    private bool inbound;
    internal bool Aligned { get; private set; }
    internal bool Finished { get; private set; }

    internal HelicopterApproach(Helicopter aircraft, Vessel ship, Vector3 gate, Vector3 entry,
        float heading, Vector3 initialVelocity)
    {
        this.aircraft = aircraft;
        this.ship = ship;
        this.gate = gate;
        this.entry = entry;
        this.heading = heading;
        velocity = initialVelocity / Metre;
        horizontalAcceleration = Mathf.Clamp(aircraft._mainThrustAccelerationFactor, 0.25f, 1.5f);
        aircraft._hasControl = true;
        aircraft.transform.SetParent(null, worldPositionStays: true);
        aircraft._isLanding = aircraft.IsLanding = true;
        aircraft._flightDeckState = nameof(HelicopterApproach);
        aircraft.SetCodeAnim("Gear_Extend", logErrors: false, setOpen: true);
        aircraft.EnableColliders();
    }

    internal void Update(float seconds, bool clearedToLand)
    {
        if (Finished || seconds <= 0) return;
        // Integrate all simulation time in small steps, including at time compression.
        int steps = Math.Max(1, Mathf.CeilToInt(seconds / 0.05f));
        float dt = seconds / steps;
        Vector3 position = aircraft.transform.position;
        Quaternion rig = ship.transform.rotation * Quaternion.Euler(0, heading, 0);
        for (int i = 0; i < steps; i++)
        {
            Vector3 target = ship.transform.TransformPoint(inbound ? entry : gate);
            Vector3 deckVelocity = VelocityAt(ship, target) / Metre;
            // The ship has already advanced this tick. Reconstruct its target over
            // the substeps instead of integrating against a frozen end-of-tick point.
            target -= deckVelocity * (seconds - (i + 1) * dt) * Metre;
            Vector3 error = Quaternion.Inverse(rig) * ((target - position) / Metre);
            Vector3 closure = new(
                ArrivalSpeed(error.x, Math.Min(3, aircraft._maxSidewardSpeed * KnotsToMetres), horizontalAcceleration),
                ArrivalSpeed(error.y, Math.Min(2, aircraft._climbRate * 0.3048f), 0.6f),
                ArrivalSpeed(error.z, error.z >= 0 ? Math.Min(12, aircraft._maxSpeedSeaLevel * KnotsToMetres)
                    : Math.Min(3, aircraft._maxBackwardSpeed * KnotsToMetres), horizontalAcceleration));
            Vector3 desired = deckVelocity + rig * closure;
            Vector3 requested = (desired - velocity) / 2;
            Vector3 horizontal = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(requested, Vector3.up), horizontalAcceleration);
            requested = horizontal + Vector3.up * Mathf.Clamp(requested.y, -0.6f, 0.6f);
            // Bound jerk as well as acceleration. Braking never flips a steering target.
            acceleration = Vector3.MoveTowards(acceleration, requested, 0.75f * dt);
            Vector3 nextVelocity = velocity + acceleration * dt;
            position += (velocity + nextVelocity) * (0.5f * dt * Metre);
            velocity = nextVelocity;
            Rotate(rig, dt);

            float headingError = Quaternion.Angle(aircraft.transform.rotation, rig);
            float relativeSpeed = (velocity - deckVelocity).magnitude;
            Aligned = !inbound && error.magnitude < 5 && relativeSpeed < 0.5f && headingError < 5;
            if (Aligned && clearedToLand) inbound = true;
        }
        aircraft.transform.position = position;
        aircraft._velocityVecInUnity = velocity * Metre;
        aircraft._velocityInUnity = aircraft._velocityVecInUnity.magnitude;
        aircraft._velocityInKnots = velocity.magnitude / KnotsToMetres;
        Vector3 entryPosition = ship.transform.TransformPoint(entry);
        Finished = inbound && Vector3.Distance(position, entryPosition) / Metre < 0.25f
            && (velocity - VelocityAt(ship, entryPosition) / Metre).magnitude < 0.05f
            && acceleration.magnitude < 0.1f && Quaternion.Angle(aircraft.transform.rotation, rig) < 1;
    }

    private static float ArrivalSpeed(float distance, float limit, float acceleration) => Mathf.Sign(distance)
        * Math.Min(limit, Math.Min(Math.Abs(distance) * 0.35f, Mathf.Sqrt(2 * acceleration * Math.Abs(distance))));

    private void Rotate(Quaternion rig, float dt)
    {
        Vector3 angles = aircraft.transform.eulerAngles;
        Quaternion yaw = Quaternion.Euler(0, angles.y, 0);
        Vector3 bodyAcceleration = Quaternion.Inverse(yaw) * acceleration;
        float pitchLimit = Math.Min(12, aircraft._maxPitchAngle);
        float rollLimit = Math.Min(12, aircraft._maxRollAngleHovering);
        float pitch = Mathf.Clamp(Mathf.Atan2(bodyAcceleration.z, 9.81f) * Mathf.Rad2Deg, -pitchLimit, pitchLimit);
        float roll = Mathf.Clamp(-Mathf.Atan2(bodyAcceleration.x, 9.81f) * Mathf.Rad2Deg, -rollLimit, rollLimit);
        aircraft.transform.rotation = Quaternion.Euler(
            Mathf.MoveTowardsAngle(angles.x, rig.eulerAngles.x + pitch, Math.Min(6, aircraft._maxPitchRate) * dt),
            Mathf.MoveTowardsAngle(angles.y, rig.eulerAngles.y, Math.Min(8, aircraft._maxYawRate) * dt),
            Mathf.MoveTowardsAngle(angles.z, rig.eulerAngles.z + roll, Math.Min(6, aircraft._maxRollRate) * dt));
    }

    internal static Vector3 VelocityAt(Vessel ship, Vector3 position) => ship._velocityVecInUnity
        + Vector3.Cross(ship.transform.up * (ship.CurrentTurnRate * Mathf.Deg2Rad), position - ship.transform.position);
}
