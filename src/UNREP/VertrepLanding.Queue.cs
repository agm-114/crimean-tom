using SeaPower;
using UnityEngine;

namespace UNREP;

internal partial class VertrepLanding
{
    private class LandingSlot(RecoveryPoint point)
    {
        internal RecoveryPoint Point { get; } = point;
        internal VertrepLanding? Current { get; private set; }
        internal VertrepLanding? Next { get; private set; }

        internal bool CanStage(VertrepLanding visit) => Next == null
            && !visit.queue.NormalOperationsPending(Point, visit.aircraft)
            && (Current == null ? visit.SpotClear(Point) : Current.ClearOfAlignment);
        internal bool CanAdvance(VertrepLanding visit) => Current == null && Next == visit;

        internal void Stage(VertrepLanding visit)
        {
            Next = visit;
            visit.AssignPoint(this);
        }
        internal void Advance(VertrepLanding visit)
        {
            if (!CanAdvance(visit)) throw new InvalidOperationException("VERTREP landing slot is not available.");
            Current = visit;
            Next = null;
        }
        internal void Release(VertrepLanding visit)
        {
            if (Current == visit) Current = null;
            if (Next == visit) Next = null;
        }
    }

    // Each ship owns one shared pool and a two-position queue for every recovery point.
    private class DeckQueue(Vessel ship)
    {
        private const double PriorityWaitSeconds = 300;
        private static readonly Dictionary<Vessel, DeckQueue> Queues = [];
        private readonly List<VertrepLanding> visits = [];
        private readonly LandingSlot[] slots = ship._obp._flightDeck._recoveryPoints
            .Where(point => point.IsAllowed(VehicleType.Helicopter)).Select(point => new LandingSlot(point)).ToArray();
        private FlightDeck Deck => ship._obp._flightDeck;

        internal static DeckQueue For(Vessel ship)
        {
            if (!Queues.TryGetValue(ship, out var queue)) Queues[ship] = queue = new(ship);
            return queue;
        }
        internal static void ScheduleAll() => Queues.Values.ToList().ForEach(queue => queue.Schedule());
        internal void Add(VertrepLanding visit) => visits.Add(visit);
        internal void Remove(VertrepLanding visit)
        {
            visit.slot?.Release(visit);
            visits.Remove(visit);
            if (visits.Count == 0) Queues.Remove(ship);
        }

        private void Schedule()
        {
            double now = GameTime.missionSessionTime;
            var waiting = visits.Where(visit => visit.Eligible).ToList();
            while (waiting.Count > 0)
            {
                var next = waiting.SelectMany(visit => slots.Where(slot => slot.CanStage(visit)
                        && Points(ship, visit.aircraft).Contains(slot.Point))
                    .Select(slot => new { Visit = visit, Slot = slot,
                        Overdue = now - visit.queuedAt >= PriorityWaitSeconds,
                        Distance = (visit.aircraft.transform.position - ship.transform.TransformPoint(slot.Point._position)).sqrMagnitude }))
                    .OrderBy(pair => pair.Overdue ? 0 : 1)
                    .ThenBy(pair => pair.Overdue ? pair.Visit.queuedAt : 0)
                    .ThenBy(pair => pair.Distance).ThenBy(pair => pair.Visit.aircraft.GetInstanceID()).FirstOrDefault();
                if (next == null) break;
                next.Slot.Stage(next.Visit);
                waiting.Remove(next.Visit);
            }
        }

        internal bool NormalOperationsPending(RecoveryPoint? point, Helicopter aircraft)
        {
            var tasks = Deck.FlightDeckTasks.Concat(Deck._flightDeckTasksToAdd)
                .Where(task => !task._isCancelled && !Deck._flightDeckTasksToRemove.Contains(task)
                    && (task is LaunchTask or ReturnTask or RecoveryTask || task is PendingLaunchTask { launchAllowed: true })).ToArray();
            // A recovery without an allocated point must be allowed to choose one first.
            if (Deck._landingQueue.Any(unit => unit != null && !unit.IsDestroyed && unit != aircraft
                && !tasks.Any(task => task.Vehicle == unit && task._recoveryPoint != null))) return true;
            return tasks.Any(task => Conflicts(point, task is LaunchTask or PendingLaunchTask
                ? task._launchPoint : task._recoveryPoint));
        }

        private bool Conflicts(RecoveryPoint? point, FlightDeckPoint? other)
        {
            // Until a native task has a point, give it priority across the deck.
            if (point == null || other == null || point == other) return true;
            if (other._blockedRecoveryPoints.Contains(point)
                || other is RecoveryPoint recovery && point._blockedRecoveryPoints.Contains(recovery)
                || other is LaunchPoint launch && point._blockedLaunchPoints.Contains(launch)) return true;
            if (FlightDeckAccess.ZonesConflict(Deck, point._position, other._position)) return true;
            // Older ship definitions can share physical space without naming a zone.
            Vector3 offset = point._position - other._position;
            return Vector3.ProjectOnPlane(offset, Vector3.up).magnitude < Utils.heightInMeterToUnity(30);
        }
    }
}
