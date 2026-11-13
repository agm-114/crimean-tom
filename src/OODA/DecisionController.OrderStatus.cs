using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal partial class DecisionController
{
    private class OrderStatus(string id, string unitId, string action, double now)
    {
        internal string Id { get; } = id;
        internal string UnitId { get; } = unitId;
        internal string Action { get; } = action;
        internal double LastProgress = now;
        internal string State = "waiting";
        internal string Reason = "Awaiting order";
        internal string? TargetId;
        internal string? Weapon;
        internal string? AttackPhase;
        internal string? ReleaseBlocker;
        internal double? MissingAttackSince;
        internal double? AttackPassSince;
        internal double? SameGateSince;
        internal double? LastAttackClosure;
        internal bool ReportedStall;
        internal int Shots;
        internal GeoPosition? Destination;
        internal double Closest = double.PositiveInfinity;
        internal PendingLaunchTask? DeckTask;
        internal int Launches;
        internal int LaunchCount;
        internal bool Retryable;
        internal bool Active => State is "waiting" or "executing" || State == "blocked" && Retryable;

        internal void Set(string state, string reason, bool progress = false)
        {
            if (progress || State != state || Reason != reason) LastProgress = GameTime.missionElapsedTime;
            State = state;
            Reason = reason;
        }

        internal JObject Read() => new() { ["orderId"] = Id, ["unitId"] = UnitId, ["action"] = Action, ["targetId"] = TargetId,
            ["weapon"] = Weapon, ["state"] = State, ["reason"] = Reason, ["attackPhase"] = AttackPhase,
            ["releaseBlocker"] = ReleaseBlocker, ["ageSinceProgressSeconds"] = Math.Round(Math.Max(0, GameTime.missionElapsedTime - LastProgress), 1) };
    }

    private readonly Dictionary<string, OrderStatus> orderStatuses = [];
    private int nextOrderId;
    private double nextOrderStatusCheck;

    private OrderStatus NewOrder(string unitId, string action, string? id = null)
    {
        if (orderStatuses.Count >= 96 && orderStatuses.Values.Where(order => !order.Retryable)
            .OrderBy(order => order.Active).ThenBy(order => order.LastProgress).FirstOrDefault() is { } oldest)
            orderStatuses.Remove(oldest.Id);
        id ??= $"order-{++nextOrderId}";
        OrderStatus status = new(id, unitId, action, GameTime.missionElapsedTime);
        orderStatuses[id] = status;
        return status;
    }

    internal JArray ReadOrderStatuses(int limit, string? unitId = null) => new(orderStatuses.Values.Where(order => unitId == null || order.UnitId == unitId)
        .OrderByDescending(order => order.Active)
        .ThenByDescending(order => order.LastProgress).Take(limit).Select(order => order.Read()));

    private void TrackRoute(OrderStatus status, ObjectBase unit, string outcome)
    {
        if (OrderRejected(outcome))
        {
            status.Set("blocked", outcome);
            return;
        }
        var route = unit.GetAllWaypointsGeoPositions(getRelative: false);
        if (route.Count > 0)
        {
            GeoPosition destination = route[^1].wpGP;
            foreach (OrderStatus previous in orderStatuses.Values.Where(order => order != status && order.UnitId == status.UnitId
                && order.Active && order.Destination is { } point && !SameDestination(point, destination)))
                previous.Set("superseded", "A newer native route replaced this destination");
            status.Destination = destination;
            status.Set("executing", outcome);
        }
        else status.Set("blocked", $"{outcome} · no native waypoint to track");
    }

    private void TrackRoute(ObjectBase unit, string action, string outcome)
    {
        if (Mode != ControlMode.AiControl) return;
        string id = PlayerPicture.Id(unit);
        var route = unit.GetAllWaypointsGeoPositions(getRelative: false);
        if (!OrderRejected(outcome) && route.Count > 0 && orderStatuses.Values.Any(order => order.UnitId == id
            && order.Active && order.Destination is { } point && SameDestination(point, route[^1].wpGP))) return;
        TrackRoute(NewOrder(id, action), unit, outcome);
    }

    private void TrackFire(string id, string targetId, string? weapon, string outcome, string detail, bool accepted)
    {
        if (Mode != ControlMode.AiControl) return;
        OrderStatus? status = orderStatuses.Values.FirstOrDefault(order => order.Action == "Engage" && order.UnitId == id
            && order.TargetId == targetId && (order.Weapon == weapon || weapon == null) && order.Active && !order.Retryable);
        if (status != null && !accepted) return;
        if (status == null)
        {
            status = NewOrder(id, "Engage");
            status.TargetId = targetId;
            status.Weapon = weapon;
            status.Shots = PlayerCombat.OwnShotCount(id, targetId, weapon);
        }
        status.Set(accepted ? "executing" : "blocked", detail.Length == 0 ? outcome : $"{outcome} · {detail}");
    }

    private static bool AttackAccepted(string outcome) => outcome.Contains(" queued", StringComparison.Ordinal)
        || outcome.StartsWith("Attack ordered", StringComparison.Ordinal) || outcome.StartsWith("Already engaging", StringComparison.Ordinal)
        || outcome.StartsWith("Kept · attack already assigned", StringComparison.Ordinal);

    private static string? AirOrderWeapon(string outcome) => outcome.StartsWith("Attack ordered · ", StringComparison.Ordinal)
        ? outcome.Split('·').ElementAtOrDefault(1)?.Trim() : null;

    private void PollOrderStatuses(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (now < nextOrderStatusCheck || !orderStatuses.Values.Any(order => order.Active)) return;
        nextOrderStatusCheck = now + 2;
        Dictionary<string, ObjectBase> owned = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        foreach (OrderStatus order in orderStatuses.Values.Where(order => order.Active).ToArray())
        {
            string previousState = order.State;
            if (!owned.TryGetValue(order.UnitId, out ObjectBase unit))
            {
                if (order.Retryable) continue;
                order.Set("blocked", "Unit is no longer commandable");
                Revision++;
                continue;
            }
            if (order.Destination is { } destination) PollRoute(order, unit, destination);
            if (order.DeckTask is { } task) PollDeck(order, task);
            if (order.Action == "Engage" && !order.Retryable && order.TargetId is { } targetId)
            {
                int shots = PlayerCombat.OwnShotCount(order.UnitId, targetId, order.Weapon);
                if (shots > order.Shots)
                {
                    order.ReleaseBlocker = null;
                    if (unit is Aircraft or Helicopter) order.AttackPhase = "Released";
                    order.Set("completed", $"Weapon release observed ({shots - order.Shots} fired) · effect unconfirmed");
                }
                else if (unit is Aircraft or Helicopter) PollAirAttack(order, unit, PlayerPicture.LiveContact(side, targetId), now);
                else if (now - order.LastProgress >= 5 && PlayerPicture.LiveContact(side, targetId) == null)
                    order.Set("blocked", "Track lost before an observed release");
                else if (now - order.LastProgress >= 10 && PlayerPicture.LiveContact(side, targetId) is { } live
                    && !unit._currentEngageTasks.Any(task => task._targetObject == live.BaseObject) && unit._ai?._objectToDestroy != live.BaseObject)
                    order.Set("blocked", "Native engagement ended before an observed release");
            }
            if (order.State != previousState) Revision++;
        }
    }

    private void PollAirAttack(OrderStatus order, ObjectBase unit, Vehicle? track, double now)
    {
        var attack = track == null ? null : PlayerFlightDeck.ReadAttackProgress(unit, track.BaseObject, order.Weapon);
        if (attack is not { Active: true })
        {
            order.MissingAttackSince ??= now;
            if (now - order.MissingAttackSince < 10) return;
            string ended = track == null ? "Track lost before an observed release" : "Native attack ended without releasing a weapon";
            if (order.ReleaseBlocker != null) ended += $" · Last gate: {order.ReleaseBlocker}";
            order.AttackPhase = "Ended";
            order.Set("blocked", ended);
            ReportAirAttack(order, unit, "No release", order.Reason);
            return;
        }

        order.MissingAttackSince = null;
        if (attack.ReleasePass) order.AttackPassSince ??= now;
        bool changed = order.AttackPhase != attack.Phase || order.ReleaseBlocker != attack.Blocker;
        if (changed)
        {
            order.SameGateSince = now;
            order.AttackPhase = attack.Phase;
            order.ReleaseBlocker = attack.Blocker;
            order.Set("executing", attack.Blocker == null ? attack.Phase : $"{attack.Phase} · {attack.Blocker}");
            Revision++;
        }

        double? range = track?.PositionEstimate?.Item1 is { } fix ? GeoPosition.HaversineDistanceNmi(unit.getGeoPosition(), fix) : null;
        if (range is double distance && order.Closest - distance >= 0.2)
        {
            order.Closest = distance;
            order.LastAttackClosure = now;
            order.LastProgress = now;
        }

        bool repeatedGate = attack.ReleasePass && attack.Blocker != null && now - order.SameGateSince >= 12;
        bool passEnded = !attack.ReleasePass && order.AttackPassSince.HasValue && attack.Phase != "Evading";
        bool unreleasedPass = attack.ReleasePass && now - order.AttackPassSince >= 45;
        bool stalledIngress = !attack.ReleasePass && attack.NominalRangeNm is > 0 && range <= attack.NominalRangeNm
            && attack.Blocker != null && now - Math.Max(order.LastAttackClosure ?? now, order.SameGateSince ?? now) >= 35;
        if (order.ReportedStall || !(repeatedGate || passEnded || unreleasedPass || stalledIngress)) return;
        order.ReportedStall = true;
        string detail = repeatedGate ? order.Reason : passEnded ? $"Attack pass ended without a release · {order.Reason}"
            : stalledIngress ? $"Ingress stopped closing inside nominal range · {order.Reason}"
            : $"No release observed after 45 seconds on the attack pass · {order.Reason}";
        ReportAirAttack(order, unit, "Release stalled", detail);
    }

    private void ReportAirAttack(OrderStatus order, ObjectBase unit, string result, string detail)
    {
        Add("Attack progress", result, order.Weapon == null ? detail : $"{order.Weapon} · {detail}", order.UnitId,
            unit.DisplayNameShort, targetId: order.TargetId);
        nextTactical = 0;
        previousSituation = null;
    }

    private static void PollRoute(OrderStatus order, ObjectBase unit, GeoPosition destination)
    {
        double distance = GeoPosition.HaversineDistanceNmi(unit.getGeoPosition(), destination);
        if (distance <= 0.25) order.Set("completed", "Arrived within 0.25 nm of the native destination");
        else if (!unit.GetAllWaypointsGeoPositions(getRelative: false).Any(point => SameDestination(point.wpGP, destination)))
            order.Set("superseded", "Native route no longer contains the destination");
        else if (order.Closest - distance >= 0.05)
        {
            order.Closest = distance;
            order.Set("executing", $"Making progress · {distance:0.0} nm to destination", progress: true);
        }
        else if (GameTime.missionElapsedTime - order.LastProgress >= 30 && Math.Abs(unit._velocityInKnots) < 0.5)
            order.Reason = $"Native waypoint active · unit stationary · {distance:0.0} nm remaining";
    }

    private static bool SameDestination(GeoPosition first, GeoPosition second) => GeoPosition.HaversineDistanceNmi(first, second) < 0.06;

    private static void PollDeck(OrderStatus order, PendingLaunchTask task)
    {
        if (order.LaunchCount > 0 && order.Launches >= order.LaunchCount)
            order.Set("completed", $"Deck confirmed launch of {order.Launches} aircraft");
        else if (task._isCancelled || task.LaunchCount <= 0 || !task._flightDeck.FlightDeckTasks.Contains(task))
            order.Set("superseded", "Tracked deck batch ended before the requested milestone");
        else if (order.LaunchCount == 0 && task._stateMachine.CurrentState is HandleAwaitSpawnTask)
            order.Set("completed", $"Deck confirms aircraft ready · {task.FlightDeckTaskLabel}");
        else order.Set("executing", $"{task.FlightDeckTaskLabel} · {task.Info}");
    }
}
