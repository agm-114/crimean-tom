using HarmonyLib;
using System.Reflection.Emit;
using SeaPower;
using UnityEngine;

namespace UNREP;

// Use the native flight states, omitting recovery's taxi/elevator/despawn stages.
[HarmonyPatch]
internal class VertrepLanding(Helicopter aircraft, Vessel ship, MovementLease flight) : IDisposable
{
    private static readonly Dictionary<Helicopter, VertrepLanding> Active = [];
    private FlightDeckTask? task;
    private IState? state;
    private RecoveryPoint? point;
    private bool landed;
    private bool takeoffRequested;
    private bool climbing;
    internal bool Landed => landed && !takeoffRequested;
    internal bool TakingOff => takeoffRequested;
    internal bool Departed { get; private set; }
    internal bool ControlsPose => task != null && !Departed;
    internal string Status { get; private set; } = "Flying to deck";

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
        if (aircraft == null || aircraft.IsDestroyed || ship == null || ship.IsDestroyed) { Dispose(); return; }
        if (task != null || seconds <= 0) return;
        var deck = ship._obp._flightDeck;
        if (deck == null || deck.Inoperable.Value) { Status = "Flight deck unavailable"; return; }
        if (!deck.CanRecoverHelicopterInCurrentSeaState(aircraft)) { Status = "Waiting for sea state"; return; }
        point = Points(ship, aircraft).FirstOrDefault(candidate => !candidate.IsOccupied && !candidate.IsBusy
            && !candidate.IsBlockedByPoint && Clear(candidate));
        if (point == null) { Status = "Waiting for a clear landing spot"; return; }
        point.Claim(aircraft);
        point._incomingObjects.Add(aircraft);
        point._serviceState = FlightDeckPoint.ServiceState.Recovering;
        point._lastUsageStartTime = GameTime.missionElapsedTime;
        task = new() { Vehicle = aircraft, _flightDeck = deck, _recoveryPoint = point, _ltp = new() };
        Active[aircraft] = this;
        // Approach owns its native waypoints and speed from here.
        flight.ReleaseSpeed();
        Enter(new Approach(task));
        flight.TrackNativeOrders();
        Status = "Approaching deck";
    }

    private void Enter(IState next)
    {
        state?.onExit();
        state = next;
        state.onEnter();
    }
    private void Tick(bool fixedUpdate)
    {
        if (Departed || GameTime.deltaTime <= 0) return;
        if (aircraft == null || aircraft.IsDestroyed || ship == null || ship.IsDestroyed) { Dispose(); return; }
        if (fixedUpdate) { state?.onFixedUpdate(); return; }
        state?.onUpdate();
        if (state is Approach approach && approach.IsFinished)
        {
            Enter(new HelicopterLanding(task!));
            // Native landing decrements the carrier's launched count. This visiting
            // aircraft never entered that carrier's hangar inventory.
            task!._flightDeck._launchedHelicoptersCount++;
            Status = "Landing";
        }
        else if (state is HelicopterLanding landing && landing._isFinished)
        {
            state.onExit();
            state = null;
            landed = true;
            aircraft._isInFlight = false;
            Status = "Landed";
        }
        if (landed && takeoffRequested && state == null && !climbing)
        {
            aircraft._isLanding = aircraft.IsLanding = false;
            aircraft.isLanding = false;
            Enter(new PreFlightAnim(task!));
            Status = "Preparing takeoff";
        }
        if (state is PreFlightAnim preflight && preflight.IsFinished)
        {
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
        if (___hasLanded && Owns(__instance._flightDeckTask)) __instance._isFinished = true;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(HelicopterTakeOff), nameof(HelicopterTakeOff.onEnter))]
    private static void SkipColdStart(FlightDeckTask ____flightDeckTask, ref double ____startTime, ref bool ____rotorsSpinning)
    {
        if (!Owns(____flightDeckTask) || ____flightDeckTask.Vehicle is not Helicopter helicopter
            || !helicopter._hfcs.areRotorsOn()) return;
        ____rotorsSpinning = true;
        ____startTime -= helicopter._hp._engineWarmUpTime + helicopter._hp._rotorsSpinUpTime + 0.01;
    }

    private bool Clear(RecoveryPoint candidate)
    {
        Vector3 position = ship.transform.TransformPoint(candidate._position);
        float ownRadius = Math.Max(15, aircraft._obp._length * 0.6f);
        return !Singleton<ObjectsManager>.Instance._listOfAllObjects.Any(unit => unit != null && unit != aircraft && unit != ship
            && !unit.IsDestroyed && (unit is LandUnit || unit.IsAirUnit)
            && Vector3.Distance(unit.transform.position, position) < Utils.heightInMeterToUnity(ownRadius + (unit._obp?._length ?? 0) * 0.5f));
    }
    internal void TakeOff()
    {
        if (task == null) { Dispose(); return; }
        takeoffRequested = true;
    }
    public void Dispose()
    {
        Active.Remove(aircraft);
        if (task != null && aircraft != null && !aircraft.IsDestroyed)
        {
            if (ship == null || ship.IsDestroyed) aircraft.destroyObject();
            else
            {
                aircraft._isLanding = aircraft.IsLanding = false;
                aircraft.isLanding = false;
                aircraft._flightDeckState = "";
                aircraft.EnableColliders();
                if (!aircraft.HasControl)
                {
                    aircraft.engageRotors();
                    aircraft.GiveControl(Math.Max(0, ship._velocityInKnots));
                    aircraft._isInFlight = true;
                }
                aircraft.transform.SetParent(null, worldPositionStays: true);
                task._flightDeck._trafficManager?.ReleaseVehicle(aircraft);
            }
        }
        if (point != null)
        {
            point._incomingObjects.Remove(aircraft);
            point.Release(aircraft);
            if (!point.IsBusy) point._serviceState = FlightDeckPoint.ServiceState.Free;
            point = null;
        }
        state = null;
        Departed = true;
    }
}
