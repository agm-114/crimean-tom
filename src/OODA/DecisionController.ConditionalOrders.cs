using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal partial class DecisionController
{
    private record ConditionalOrder(OrderStatus Status, string Condition, string? ConditionId, string? FitId, int Count, int MinimumCoverage,
        GeoPosition[] Route, string OriginalRoute, double ExpiresAt, string Reason);
    private readonly Dictionary<string, ConditionalOrder> conditionalOrders = [];
    private double nextConditionalOrderCheck;

    private JArray ReadConditionalOrders() => new(conditionalOrders.Values.Select(order => new JObject
    {
        ["orderId"] = order.Status.Id, ["unitId"] = order.Status.UnitId, ["order"] = order.Status.Action,
        ["condition"] = order.Condition, ["conditionId"] = order.ConditionId, ["fitId"] = order.FitId,
        ["minimumCoverage"] = order.Condition == "CapCoverageBelow" ? order.MinimumCoverage : null,
        ["expiresAt"] = order.ExpiresAt, ["reason"] = order.Reason, ["status"] = order.Status.Read()
    }));

    private void ApplyConditionalOrders(JToken? orders, Taskforce side)
    {
        if (orders is not JArray { Count: > 0 } entries || LastInput is not { } input) return;
        Dictionary<string, ObjectBase> units = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        foreach (JObject item in entries.OfType<JObject>().Take(8))
        {
            if ((string?)item["orderId"] is not { Length: > 0 and <= 48 } id || !id.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')
                || !id.StartsWith("auto-", StringComparison.Ordinal)) continue;
            if ((string?)item["action"] == "Cancel")
            {
                if (Mode == ControlMode.AiControl && conditionalOrders.Remove(id, out ConditionalOrder canceled))
                {
                    canceled.Status.Retryable = false;
                    canceled.Status.Set("superseded", "Canceled by planner");
                }
                continue;
            }
            if ((string?)item["action"] != "Arm" || conditionalOrders.Count >= 16 || orderStatuses.ContainsKey(id)
                || (string?)item["unitId"] is not { } unitId || !units.TryGetValue(unitId, out ObjectBase unit)
                || (string?)item["condition"] is not { } condition || (string?)item["order"] is not { } action
                || (double?)item["expiresInSeconds"] is not double expiry || !double.IsFinite(expiry) || expiry <= 0) continue;
            string? conditionId = (string?)item["conditionId"], fitId = (string?)item["fitId"];
            int count = (int?)item["count"] ?? 1, minimum = (int?)item["minimumCoverage"] ?? 0;
            GeoPosition[] route = [];
            bool valid = (condition, action) switch
            {
                ("AirRouteClear", "ResumeTransport") => unit is Helicopter && SavedTransportRoute(side, unitId) is { Length: > 1 },
                ("CapCoverageBelow", "LaunchRelief") => conditionId != null && fitId != null && minimum is >= 1 and <= 8 && count is >= 1 and <= 4
                    && PlayerFlightDeck.FindCapStation(side, conditionId) != null
                    && input["airDecks"]?.Any(deck => (string?)deck["shipId"] == unitId && deck["fits"]?.Any(fit => (string?)fit["id"] == fitId
                        && fit["roles"]?.Values<string>().Contains("CAP") == true) == true) == true,
                ("ContactLost", "SetSurfaceRoute" or "DisengageFlight") => conditionId != null
                    && input["contacts"]?.Any(track => (string?)track["id"] == conditionId) == true && PlayerPicture.LiveContact(side, conditionId) != null
                    && (action == "SetSurfaceRoute" ? unit is Vessel ship && PlayerPicture.CanNavigate(ship)
                        : unit is (Aircraft or Helicopter) && PlayerFlightDeck.IsAirborne(unit) && !unit.IsFormationWingman)
                    && TryReadToolRoute(item["waypoints"], unit, out route),
                _ => false
            };
            string reason = Short((string?)item["reason"]);
            if (Mode == ControlMode.AiControl)
            {
                OrderStatus status = NewOrder(unitId, action, id);
                status.Retryable = valid;
                status.Set(valid ? "waiting" : "blocked", valid ? $"Waiting for {condition}" : "Condition or native order is unavailable in the current player picture");
                if (valid) conditionalOrders[id] = new(status, condition, conditionId, fitId, count, minimum, route,
                    ConditionalRouteSignature(side, unit, condition), GameTime.missionElapsedTime + Math.Min(expiry, 1800), reason);
            }
            Add("Conditional order", valid ? Mode == ControlMode.AiControl ? "Armed" : "Suggested" : "Rejected",
                $"{id} · {action} when {condition} · {reason}", unitId, unit.DisplayNameShort, targetId: conditionId);
        }
    }

    private static GeoPosition[]? SavedTransportRoute(Taskforce side, string id) => PlayerFlightDeck.HeldTransportRoutes(side)
        .FirstOrDefault(flight => flight.Id == id)?.Points;

    private void RunConditionalOrders(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (conditionalOrders.Count == 0 || now < nextConditionalOrderCheck) return;
        nextConditionalOrderCheck = now + 2;
        Dictionary<string, ObjectBase> owned = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        foreach (ConditionalOrder order in conditionalOrders.Values.ToArray())
        {
            OrderStatus status = order.Status;
            if (now >= order.ExpiresAt || !owned.TryGetValue(status.UnitId, out ObjectBase unit))
            {
                End("blocked", now >= order.ExpiresAt ? "Expired before execution" : "Unit is no longer commandable");
                continue;
            }
            try
            {
                switch (order.Condition)
                {
                    case "AirRouteClear":
                        if (SavedTransportRoute(side, status.UnitId) is not { Length: > 1 } held || unit.IsFormationWingman
                            || ConditionalRouteSignature(side, unit, order.Condition) != order.OriginalRoute)
                            End("superseded", "The held transport route changed");
                        else if (!airDefenseRoutes.TransportRouteBlocked(side, airDefenseAssessment, status.UnitId)
                            && defense.HelicopterRouteBlocker(side, held[0], held[1..], repeat: false) == null)
                        {
                            string outcome = PlayerFlightDeck.TaskAirborne(side, status.UnitId, "Transport", [], useRadar: false);
                            FinishRoute(unit, outcome);
                            RecordFlightAssignment(status.UnitId, "Transport", null, outcome);
                        }
                        else status.Set("waiting", "Saved route still crosses known air defense");
                        break;
                    case "CapCoverageBelow":
                        if (PlayerFlightDeck.FindCapStation(side, order.ConditionId!) is not { } zone)
                            End("superseded", "CAP station no longer exists");
                        else if (PlayerFlightDeck.CapCoverage(side, zone) < order.MinimumCoverage)
                        {
                            if (PlayerFlightDeck.HasQueuedCap(side, order.ConditionId!))
                            {
                                End("superseded", "Relief is already queued for this station");
                                break;
                            }
                            string outcome = PlayerFlightDeck.QueueAirMission(unit, side, order.FitId!, "CAP", order.Count, [], useRadar: false,
                                trackTask: task => status.DeckTask = task, launched: () => status.Launches++, capStationId: order.ConditionId);
                            if (OrderRejected(outcome)) status.Set("blocked", outcome);
                            else
                            {
                                status.LaunchCount = order.Count;
                                End("executing", outcome);
                            }
                        }
                        else status.Set("waiting", "CAP meets the armed aircraft coverage threshold");
                        break;
                    case "ContactLost":
                        if (ConditionalRouteSignature(side, unit, order.Condition) != order.OriginalRoute || unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase)
                            End("superseded", "Unit orders changed since the turnback was armed");
                        else if (PlayerPicture.LiveContact(side, order.ConditionId!) == null)
                        {
                            FinishRoute(unit, TurnBack(unit, order, side));
                        }
                        else status.Set("waiting", "Contact still has a live plotted fix");
                        break;
                }
            }
            catch (Exception error) { End("blocked", $"Native order failed · {Short(error.Message)}"); }

            void End(string state, string outcome)
            {
                conditionalOrders.Remove(status.Id);
                status.Retryable = false;
                status.Set(state, outcome);
                Add("Conditional order", state, $"{status.Id} · {outcome}", status.UnitId, targetId: order.ConditionId);
                nextTactical = 0;
            }

            void FinishRoute(ObjectBase recipient, string outcome)
            {
                if (OrderRejected(outcome)) status.Set("blocked", outcome);
                else
                {
                    End("executing", outcome);
                    TrackRoute(status, recipient, outcome);
                }
            }
        }
    }

    private string TurnBack(ObjectBase unit, ConditionalOrder order, Taskforce side)
    {
        if (order.Status.Action == "SetSurfaceRoute" && unit is Vessel ship)
            return NavigationOrders.SetSurfaceRoute(ship, side, order.Route, defense, observe: false);
        GeoPosition origin = unit.getGeoPosition();
        if (!airDefenseRoutes.CanWithdraw(side, airDefenseAssessment, origin, order.Route))
            return "Rejected · turnback route crosses known air defense";
        if (unit is Helicopter && defense.HelicopterRouteBlocker(side, origin, order.Route, repeat: false) is { } blocker)
            return $"Rejected · turnback route crosses {blocker}";
        return PlayerFlightDeck.TaskAirborne(side, order.Status.UnitId, "Disengage", order.Route, useRadar: false);
    }

    private void CancelConditionalOrders()
    {
        foreach (ConditionalOrder order in conditionalOrders.Values)
        {
            order.Status.Retryable = false;
            order.Status.Set("superseded", "AI control ended");
        }
        conditionalOrders.Clear();
    }

    private static string ConditionalRouteSignature(Taskforce side, ObjectBase unit, string condition)
    {
        if (condition == "AirRouteClear")
            return string.Join("|", SavedTransportRoute(side, PlayerPicture.Id(unit))?.Skip(1).Select(PointKey) ?? []);
        return $"{unit._patrolZone?._zoneID}|{PointKey(unit.GetAllWaypointsGeoPositions(getRelative: false).LastOrDefault().wpGP)}";

        static string PointKey(GeoPosition point) => FormattableString.Invariant($"{point._latitude:0.0000},{point._longitude:0.0000},{point._height:0.000}");
    }
}
