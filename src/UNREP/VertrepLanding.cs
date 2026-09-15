using HarmonyLib;
using System.Reflection.Emit;
using SeaPower;
using UnityEngine;

namespace UNREP;

// Align at the native landing entry; native states own descent, touchdown and takeoff.
[HarmonyPatch]
internal partial class VertrepLanding : IDisposable
{
    private static readonly Dictionary<Helicopter, VertrepLanding> Active = [];
    private readonly Helicopter aircraft;
    private readonly Vessel ship;
    private readonly MovementLease flight;
    private readonly DeckQueue queue;
    private readonly double queuedAt = GameTime.missionSessionTime;
    private LandingSlot? slot;
    private FlightDeck Deck => ship._obp._flightDeck;
    private FlightDeckTask? task;
    private IState? state;
    private RecoveryPoint? point;
    private bool holding;
    private float orbitAngle;
    private bool reserved;
    private Vector3 gate;
    private Vector3 landingEntry;
    private HelicopterApproach? approach;
    private Vector3? previousOffset;
    private Vector3? arrivalVelocity;
    private readonly bool controllable;
    private bool landed;
    private bool takeoffRequested;
    private bool climbing;
    internal bool Landed => landed && !takeoffRequested;
    internal bool TakingOff => takeoffRequested;
    internal bool ShouldClearDeck => Landed && NormalFlightOpsPending;
    private bool NormalFlightOpsPending => queue.NormalOperationsPending(point, aircraft);
    internal bool Departed { get; private set; }
    internal bool ControlsPose => !Departed && (approach != null || landed || state != null);
    internal static bool OwnsMotion(ObjectBase unit) => unit is Helicopter helicopter && !helicopter.IsDestroyed
        && !helicopter.Disabled && Active.TryGetValue(helicopter, out var visit) && visit.ControlsPose && !visit.climbing;
    internal string Status { get; private set; } = "Flying to deck";

    internal VertrepLanding(Helicopter aircraft, Vessel ship, MovementLease flight)
    {
        this.aircraft = aircraft;
        this.ship = ship;
        this.flight = flight;
        controllable = aircraft.IsControllable;
        Active[aircraft] = this;
        queue = DeckQueue.For(ship);
        queue.Add(this);
    }

    private bool Eligible => !Departed && aircraft != null && !aircraft.IsDestroyed && !aircraft.Disabled
        && ship != null && !ship.IsDestroyed && flight.Owned && aircraft._isInFlight
        && !landed && !takeoffRequested && state == null && approach == null && point == null
        && !Deck.Inoperable.Value && Deck.CanRecoverHelicopterInCurrentSeaState(aircraft);
    private bool ClearOfAlignment => reserved && (state is HelicopterLanding || landed
        || Vector3.Dot(ship.transform.InverseTransformPoint(aircraft.transform.position) - gate,
            Quaternion.Euler(0, point!._rotation, 0) * Vector3.forward) >= Utils.heightInMeterToUnity(75));

    // All visits register before scheduling, so list iteration order cannot win a slot.
    internal static void Schedule() => DeckQueue.ScheduleAll();

    private Vector3 AlignmentPoint(RecoveryPoint candidate) => candidate._position + candidate._approachOffset
        - aircraft._obp._landingPivot - Quaternion.Euler(0, candidate._rotation, 0) * Vector3.forward * Utils.heightInMeterToUnity(300);

    private void AssignPoint(LandingSlot assigned)
    {
        slot = assigned;
        point = assigned.Point;
        holding = false;
        task = new() { Vehicle = aircraft, _flightDeck = Deck, _recoveryPoint = point, _ltp = new() };
        landingEntry = point._position + point._approachOffset - aircraft._obp._landingPivot;
        gate = AlignmentPoint(point);
        Follow(gate);
        Status = "Joining landing alignment";
    }

    internal static bool HasDeck(Vessel ship) => ship._obp?._flightDeck?._recoveryPoints
        .Any(point => point.IsAllowed(VehicleType.Helicopter)) == true;
    internal static bool Supports(Vessel ship, Helicopter aircraft) => Points(ship, aircraft).Any();
    private static IEnumerable<RecoveryPoint> Points(Vessel ship, Helicopter aircraft)
    {
        var metadata = Utils.GetSOP("aircraft", aircraft._obp._objectIniName);
        return ship._obp?._flightDeck?._recoveryPoints.Where(point => point.IsAllowed(VehicleType.Helicopter)
            && metadata != null && point.CheckSupport(metadata._objectIniName, metadata._unitRoles,
                metadata._isLarge, metadata._isVeryLarge)) ?? [];
    }

    internal void Update(double seconds)
    {
        if (Departed) return;
        if (aircraft == null || aircraft.IsDestroyed || aircraft.Disabled || ship == null || ship.IsDestroyed) { Dispose(); return; }
        if (seconds <= 0 || approach != null || state != null || landed) return;
        if (NormalFlightOpsPending) { Hold("Waiting for normal flight operations"); return; }
        if (Deck.Inoperable.Value || !Deck.CanRecoverHelicopterInCurrentSeaState(aircraft))
        { Hold("Waiting for flight deck"); return; }
        if (point == null) { Hold("Waiting for landing alignment slot"); return; }
        Vector3 local = ship.transform.InverseTransformPoint(aircraft.transform.position);
        if (Vector3.Distance(local, gate) <= Utils.heightInMeterToUnity(150)) BeginApproach();
        else Status = "Joining landing approach";
    }

    private void BeginApproach()
    {
        flight.ReleaseSpeed();
        approach = new(aircraft, ship, gate, landingEntry, point!._rotation,
            arrivalVelocity ?? aircraft._velocityVecInUnity);
        Status = "Approaching landing entry";
    }

    private void UpdateApproach()
    {
        if (NormalFlightOpsPending || Deck.Inoperable.Value || !Deck.CanRecoverHelicopterInCurrentSeaState(aircraft)
            || (reserved && !CorridorClear(point!)))
        {
            approach = null;
            previousOffset = null;
            arrivalVelocity = null;
            ReleasePoint(point);
            Deck._trafficManager?.ReleaseVehicle(aircraft);
            aircraft._isLanding = aircraft.IsLanding = false;
            aircraft._flightDeckState = "";
            aircraft.EnableColliders();
            aircraft.GiveControl(Math.Max(0, aircraft._velocityInKnots));
            Hold("Waiting for flight deck clearance");
            return;
        }
        if (!reserved && approach!.Aligned && slot!.CanAdvance(this) && SpotClear(point!))
        {
            point!.Claim(aircraft);
            reserved = true;
            slot!.Advance(this);
            point._incomingObjects.Add(aircraft);
            point._serviceState = FlightDeckPoint.ServiceState.Recovering;
            point._lastUsageStartTime = GameTime.missionElapsedTime;
            Deck._trafficManager?.ForceClaimCorridor(aircraft, point._position, point._exitPosition);
        }
        approach!.Update(GameTime.fixedDeltaTime, reserved);
        Status = reserved ? "Approaching landing entry" : approach.Aligned
            ? "Waiting aligned for landing spot" : "Moving into landing alignment";
        if (!approach.Finished) return;
        approach = null;
        Enter(new HelicopterLanding(task!));
        Status = "Landing";
    }
    private bool SpotClear(RecoveryPoint candidate) => !candidate.IsOccupied && !candidate.IsBusy
        && !candidate.IsBlockedByPoint && CorridorClear(candidate);
    private bool CorridorClear(RecoveryPoint candidate) => Deck.RecoveryStripClear(candidate, aircraft)
        && Deck._trafficManager?.IsRunwayZoneOccupied(candidate._position, aircraft) != true;

    private void Hold(string reason)
    {
        // Return to the shared pool without losing accumulated waiting priority.
        slot?.Release(this);
        slot = null;
        point = null;
        task = null;
        if (!holding)
        {
            flight.ReleaseSpeed();
            Vector3 local = ship.transform.InverseTransformPoint(aircraft.transform.position);
            orbitAngle = Mathf.Atan2(local.x, local.z);
            AdvanceOrbit();
            holding = true;
        }
        else if (flight.Distance < 0.1f) AdvanceOrbit();
        Status = reason;
    }
    private void AdvanceOrbit()
    {
        // This visit's ship is the pickup or delivery destination, not always the supplier.
        // Advance before reaching each point so native flight controls keep the aircraft moving.
        orbitAngle = Mathf.Repeat(orbitAngle + Mathf.PI / 6, Mathf.PI * 2);
        flight.Follow(ship, Mathf.Sin(orbitAngle) * 0.5f, -Mathf.Cos(orbitAngle) * 0.5f,
            "VERTREP holding orbit", height: Utils.heightInMeterToUnity(150));
    }
    private void Follow(Vector3 position)
    {
        flight.ReleaseSpeed();
        flight.Follow(ship, position.x / 27.559496f, -position.z / 27.559496f,
            "VERTREP approach", height: position.y);
    }

    private void Enter(IState next)
    {
        state?.onExit();
        state = next;
        state.onEnter();
        // Targeting uses this flag too. The scoped movement hook, not this flag,
        // prevents native physics from fighting our approach and deck states.
        aircraft._hasControl = true;
        aircraft.EnableColliders();
    }
    private void Tick(bool fixedUpdate)
    {
        if (Departed || GameTime.deltaTime <= 0) return;
        if (aircraft == null || aircraft.IsDestroyed || aircraft.Disabled || ship == null || ship.IsDestroyed) { Dispose(); return; }
        if (fixedUpdate)
        {
            if (approach != null) UpdateApproach();
            else if (state != null) state.onFixedUpdate();
            else if (!landed && GameTime.fixedDeltaTime > 0)
            {
                // Native velocity telemetry omits vertical motion. Measure motion
                // relative to the ship to preserve it at capture and ignore origin shifts.
                Vector3 offset = aircraft.transform.position - ship.transform.position;
                if (previousOffset is Vector3 previous)
                    arrivalVelocity = (offset - previous) / GameTime.fixedDeltaTime + ship._velocityVecInUnity;
                previousOffset = offset;
            }
            return;
        }
        state?.onUpdate();
        if (!landed) return;
        if (landed && takeoffRequested && state == null && !climbing)
        {
            if (!CorridorClear(point!)) { Status = "Waiting for departure clearance"; return; }
            aircraft._isLanding = aircraft.IsLanding = false;
            aircraft.isLanding = false;
            Enter(new PreFlightAnim(task!));
            Status = "Preparing takeoff";
        }
        if (state is PreFlightAnim preflight && preflight.IsFinished)
        {
            if (!CorridorClear(point!)) { Status = "Waiting for departure clearance"; return; }
            // Give native flight controls a climb target before takeoff releases
            // deck control. Keep the spot reserved until the aircraft clears it.
            flight.Follow(ship, point!._position.x / 27.559496f, -point._position.z / 27.559496f,
                "VERTREP takeoff", height: point._position.y + Utils.heightInMeterToUnity(50), leadNm: 0.025f);
            flight.Speed(Math.Max(0, ship._velocityInKnots));
            Enter(new HelicopterTakeOff(task!));
            Status = "Taking off";
        }
        if (state is HelicopterTakeOff takeoff && takeoff._isFinished)
        {
            // Native onExit releases a hangar launch point and decrements onboard
            // inventory. Neither belongs to this turnaround, so finish locally.
            state = null;
            aircraft._isInFlight = true;
            climbing = true;
            Status = "Climbing clear of deck";
        }
        if (climbing && ship.transform.InverseTransformPoint(aircraft.transform.position).y
            + aircraft._obp._onGroundPivot.y >= point!._position.y + Utils.heightInMeterToUnity(20))
        {
            flight.ReleaseSpeed();
            Dispose();
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Helicopter), nameof(Helicopter.OnFixedUpdate))]
    private static void FixedUpdate(Helicopter __instance)
    {
        if (Active.TryGetValue(__instance, out var visit)) visit.Tick(true);
    }
    [HarmonyPostfix, HarmonyPatch(typeof(Helicopter), nameof(Helicopter.OnUpdateEveryFrame))]
    private static void FrameUpdate(Helicopter __instance)
    {
        if (Active.TryGetValue(__instance, out var visit)) visit.Tick(false);
    }

    private static bool Owns(FlightDeckTask task) => task.Vehicle is Helicopter helicopter
        && Active.TryGetValue(helicopter, out var visit) && ReferenceEquals(visit.task, task);

    [HarmonyTranspiler, HarmonyPatch(typeof(HelicopterLanding), nameof(HelicopterLanding.onFixedUpdate))]
    private static IEnumerable<CodeInstruction> KeepRotorsRunning(IEnumerable<CodeInstruction> instructions)
    {
        var stop = AccessTools.Method(typeof(HelicopterFlightControlSystem), nameof(HelicopterFlightControlSystem.stopRotors));
        var code = instructions.ToList();
        if (code.Count(instruction => instruction.Calls(stop)) != 1)
            throw new InvalidOperationException("Native helicopter rotor shutdown changed");
        foreach (var instruction in code)
        {
            if (!instruction.Calls(stop)) { yield return instruction; continue; }
            yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VertrepLanding), nameof(StopRotors)));
        }
    }
    private static void StopRotors(HelicopterFlightControlSystem controls, HelicopterLanding landing)
    {
        if (!Owns(landing._flightDeckTask)) controls.stopRotors();
    }

    [HarmonyPostfix, HarmonyPatch(typeof(HelicopterLanding), nameof(HelicopterLanding.onFixedUpdate))]
    private static void FinishAtTouchdown(HelicopterLanding __instance, bool ___hasLanded)
    {
        if (!___hasLanded || !Owns(__instance._flightDeckTask)) return;
        var visit = Active[(Helicopter)__instance._flightDeckTask.Vehicle];
        if (visit.landed) return;
        // End native landing at touchdown, before rotor shutdown or hangar work.
        __instance._isFinished = true;
        __instance.onExit();
        ((Helicopter)__instance._flightDeckTask.Vehicle)._isInFlight = false;
        visit.state = null;
        visit.landed = true;
        visit.Status = "Landed";
    }

    [HarmonyPostfix, HarmonyPatch(typeof(HelicopterLanding), nameof(HelicopterLanding.onEnter))]
    private static void VisitingAircraft(HelicopterLanding __instance)
    {
        if (!Owns(__instance._flightDeckTask)) return;
        __instance._flightDeckTask._flightDeck._launchedHelicoptersCount++;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(HelicopterTakeOff), nameof(HelicopterTakeOff.onEnter))]
    private static void SkipColdStart(FlightDeckTask ____flightDeckTask, ref double ____startTime, ref bool ____rotorsSpinning)
    {
        if (!Owns(____flightDeckTask) || ____flightDeckTask.Vehicle is not Helicopter helicopter
            || !helicopter._hfcs.areRotorsOn()) return;
        ____rotorsSpinning = true;
        ____startTime -= helicopter._hp._engineWarmUpTime + helicopter._hp._rotorsSpinUpTime + 0.01;
    }

    internal void TakeOff()
    {
        // An airborne cancellation frees its queue position immediately. Only a
        // committed native landing or deck turnaround needs a takeoff sequence.
        if (task == null || (!landed && state is not HelicopterLanding)) { Dispose(); return; }
        takeoffRequested = true;
    }
    public void Dispose()
    {
        if (Departed) return;
        if (!climbing) flight.Restore();
        Active.Remove(aircraft);
        ReleasePoint(point);
        queue.Remove(this);
        slot = null;
        if (task != null && aircraft != null && !aircraft.IsDestroyed)
        {
            if (ship == null || ship.IsDestroyed) aircraft.destroyObject();
            else
            {
                aircraft.IsControllable = controllable;
                aircraft._isLanding = aircraft.IsLanding = false;
                aircraft.isLanding = false;
                aircraft._flightDeckState = "";
                aircraft.EnableColliders();
                if ((ControlsPose && !climbing) || !aircraft.HasControl)
                {
                    aircraft.engageRotors();
                    aircraft.GiveControl(aircraft._velocityInKnots);
                }
                aircraft._isInFlight = true;
                aircraft.transform.SetParent(null, worldPositionStays: true);
                task._flightDeck._trafficManager?.ReleaseVehicle(aircraft);
            }
        }
        approach = null;
        state = null;
        Departed = true;
    }
    private void ReleasePoint(FlightDeckPoint? deckPoint)
    {
        if (deckPoint == null || !reserved) return;
        reserved = false;
        deckPoint._incomingObjects.Remove(aircraft);
        deckPoint.Release(aircraft);
        if (!deckPoint.IsBusy) deckPoint._serviceState = FlightDeckPoint.ServiceState.Free;
    }
}
