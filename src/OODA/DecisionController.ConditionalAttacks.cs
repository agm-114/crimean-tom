using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal partial class DecisionController
{
    private record ConditionalAttack(string UnitId, string TargetId, string Attack, SurfaceWeapon Weapon, double? NotBefore,
        double ExpiresAt, string Reason, OrderStatus Status);
    private readonly Dictionary<(string Unit, string Target), ConditionalAttack> conditionalAttacks = [];
    private readonly Dictionary<string, string> hailedTracks = [];
    private double nextHailCheck;
    private double nextConditionalAttackCheck;

    private void HailSurfaceContacts(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (now < nextHailCheck || side.PlottingTable?.Vehicles is not { } tracks) return;
        nextHailCheck = now + 15;
        Vessel[] ships = PlayerPicture.Ships(side);
        foreach (Vehicle track in tracks.Where(track => !track.Identified.Value && PlayerOrders.CanStageOnContact(track)
            && track.CurrentRelationship() != RelationsState.Hostile).ToArray())
        {
            string id = PlayerPicture.Id(track);
            if (hailedTracks.ContainsKey(id) || track.PositionEstimate?.Item1 is not { } fix) continue;
            foreach (Vessel sender in ships.OrderBy(ship => GeoPosition.HaversineDistanceNmi(ship.getGeoPosition(), fix)))
            {
                if (PlayerOrders.RequestIdentification(side, sender, track) is not { } outcome) continue;
                hailedTracks.Add(id, outcome);
                Add("Contact identification", outcome, $"Requested identification from {id}", PlayerPicture.Id(sender), sender.DisplayNameShort, targetId: id);
                nextTactical = 0;
                previousSituation = null;
                return;
            }
        }
    }

    private JArray ReadIdentificationRequests() => new(hailedTracks.Select(track => new JObject
        { ["targetId"] = track.Key, ["result"] = track.Value }));

    private JArray ReadConditionalAttacks() => new(conditionalAttacks.Values.Select(order => new JObject
    {
        ["unitId"] = order.UnitId, ["targetId"] = order.TargetId, ["attack"] = order.Attack, ["weapons"] = order.Weapon.ToString(),
        ["notBeforeMissionSeconds"] = order.NotBefore, ["expiresAt"] = order.ExpiresAt, ["reason"] = order.Reason,
        ["orderId"] = order.Status.Id, ["status"] = order.Status.Reason
    }));

    private void ApplyConditionalAttacks(JToken? orders, Taskforce side)
    {
        if (orders is not JArray { Count: > 0 } entries || LastInput is not { } input) return;
        double now = GameTime.missionElapsedTime;
        Dictionary<string, ObjectBase> owned = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        foreach (JObject item in entries.OfType<JObject>().Take(8))
        {
            if ((string?)item["unitId"] is not { } id || (string?)item["targetId"] is not { } targetId) continue;
            string reason = Short((string?)item["reason"]);
            var key = (id, targetId);
            if ((string?)item["action"] == "Cancel")
            {
                if (conditionalAttacks.Remove(key, out ConditionalAttack canceled))
                {
                    FinishConditionalAttack(canceled, "superseded", "Canceled by planner");
                    Add("Conditional attack", "Canceled", reason, id, targetId: targetId);
                }
                continue;
            }
            if ((string?)item["action"] != "Arm" || !owned.TryGetValue(id, out ObjectBase? unit)
                || (string?)item["attack"] is not { } attack || !Enum.TryParse((string?)item["weapons"], out SurfaceWeapon weapon)
                || !CanArmAgainst(input, targetId, attack)) continue;
            double duration = (double?)item["expiresInSeconds"] ?? 900;
            double? notBefore = (double?)item["notBeforeMissionSeconds"];
            if (!double.IsFinite(duration) || duration <= 0 || notBefore is double start && !double.IsFinite(start)) continue;
            double expiry = now + Math.Min(duration, 3600);
            if (notBefore >= expiry) continue;
            if (PlayerPicture.LiveContact(side, targetId)?.CurrentRelationship() is RelationsState.Friendly or RelationsState.Neutral) continue;
            bool eligible = attack switch
            {
                "EngageShip" => unit is Vessel or Submarine,
                "Bombard" => unit is Vessel,
                "CAP" or "SEAD" or "Strike" or "Strafe" or "AntiShip" => input["airborne"]?.Any(flight => (string?)flight["id"] == id) == true,
                _ => false
            };
            conditionalAttacks.TryGetValue(key, out ConditionalAttack? prior);
            if (!eligible || prior == null && conditionalAttacks.Count >= 16) continue;
            if (prior == null && orderStatuses.Values.Any(status => status is { Action: "Engage", State: "executing" }
                && status.UnitId == id && status.TargetId == targetId)) continue;
            if (Mode == ControlMode.AiControl)
            {
                OrderStatus status = prior?.Status ?? NewOrder(id, "Engage");
                status.TargetId = targetId;
                status.Retryable = true;
                status.Set("waiting", notBefore > now ? $"Holding until mission T+{notBefore:0}s" : "Waiting for a usable live hostile fix");
                conditionalAttacks[key] = new(id, targetId, attack, weapon, notBefore, expiry, reason, status);
            }
            string timing = notBefore > now ? $"from mission T+{notBefore:0}s with a live hostile fix" : "when the track is live";
            Add("Conditional attack", Mode != ControlMode.AiControl ? "Suggested" : prior == null ? "Armed" : "Updated", $"{attack} {timing} · {reason}",
                id, unit.DisplayNameShort, targetId: targetId);
        }
    }

    private static bool CanArmAgainst(JObject input, string targetId, string attack)
    {
        JToken? contact = input["contacts"]?.FirstOrDefault(track => (string?)track["id"] == targetId);
        if (contact != null) return (string?)contact["posture"] is not ("Friendly" or "Neutral");
        return attack is ("Bombard" or "SEAD" or "Strike" or "Strafe")
            && input["airDefenseAssessment"]?.Any(site => (string?)site["targetId"] == targetId && site["position"] is JObject) == true;
    }

    private void RunConditionalAttacks(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (conditionalAttacks.Count == 0 || now < nextConditionalAttackCheck) return;
        nextConditionalAttackCheck = now + 3;
        Dictionary<string, ObjectBase> owned = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        HashSet<string> tasked = [];
        foreach (var (key, order) in conditionalAttacks.ToArray())
        {
            if (now >= order.ExpiresAt || !owned.TryGetValue(order.UnitId, out ObjectBase? unit)
                || unit is (Aircraft or Helicopter) && (!PlayerFlightDeck.IsAirborne(unit)
                    || unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase))
            {
                conditionalAttacks.Remove(key);
                bool expired = now >= order.ExpiresAt;
                FinishConditionalAttack(order, expired ? "blocked" : "superseded", expired ? "Attack window closed before execution" : "Shooter is no longer available");
                Add("Conditional attack", expired ? "Expired" : "Canceled", $"{order.Status.Reason} · {order.Reason}", order.UnitId, targetId: order.TargetId);
                nextTactical = 0;
                previousSituation = null;
                continue;
            }
            if (PlayerPicture.LiveContact(side, order.TargetId) is not { } track)
            {
                if (!(order.NotBefore > now)) order.Status.Set("waiting", "Waiting for a usable live hostile fix");
                continue;
            }
            if (track.CurrentRelationship() is RelationsState.Friendly or RelationsState.Neutral)
            {
                conditionalAttacks.Remove(key);
                FinishConditionalAttack(order, "superseded", "Contact is now friendly or neutral");
                Add("Conditional attack", "Canceled", "Contact is now friendly or neutral", order.UnitId, unit.DisplayNameShort,
                    targetId: order.TargetId);
                continue;
            }
            if (order.NotBefore > now) continue;
            if (track.CurrentRelationship() != RelationsState.Hostile || unit._weaponStatus == ObjectBase.WeaponStatus.Hold
                || unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase || tasked.Contains(order.UnitId)) continue;
            if (unit._currentEngageTasks.Any(task => task._targetObject && task._targetObject != track.BaseObject)
                || unit is (Aircraft or Helicopter) && PlayerFlightDeck.HasOtherAirAttack(unit, track.BaseObject)) continue;
            bool compatibleTarget = order.Attack switch
            {
                "EngageShip" or "AntiShip" => track.BaseObject is Vessel,
                "CAP" => track.BaseObject.IsAirUnit,
                _ => track.BaseObject is LandUnit
            };
            if (!compatibleTarget)
            {
                conditionalAttacks.Remove(key);
                FinishConditionalAttack(order, "blocked", "Contact type cannot take the planned attack");
                Add("Conditional attack", "Canceled", "Contact type cannot take the planned attack", order.UnitId, unit.DisplayNameShort,
                    targetId: order.TargetId);
                continue;
            }
            string status;
            try
            {
                status = order.Attack switch
                {
                    "EngageShip" => EngageMaritimeTarget(unit, side, track, order.Weapon, preferTorpedoes: false).Status,
                    "Bombard" => PlayerOrders.EngageLand((Vessel)unit, side, track, order.Weapon).Status,
                    _ => PlayerFlightDeck.TaskAirborne(side, order.UnitId, order.Attack, [], useRadar: false, order.TargetId)
                };
            }
            catch (Exception error)
            {
                conditionalAttacks.Remove(key);
                FinishConditionalAttack(order, "blocked", error.Message);
                Add("Conditional attack", "Failed", error.Message, order.UnitId, unit.DisplayNameShort, targetId: order.TargetId);
                continue;
            }
            if (!AttackAccepted(status))
            {
                order.Status.Set("blocked", status);
                continue;
            }
            conditionalAttacks.Remove(key);
            tasked.Add(order.UnitId);
            order.Status.Weapon = AirOrderWeapon(status);
            order.Status.Shots = PlayerCombat.OwnShotCount(order.UnitId, order.TargetId, order.Status.Weapon);
            FinishConditionalAttack(order, "executing", status);
            Add("Conditional attack", status, $"{PlayerPicture.ContactLabel(track)} · {order.Reason}", order.UnitId, unit.DisplayNameShort,
                targetId: order.TargetId);
            RecordFlightAssignment(order.UnitId, order.Attack, order.TargetId, status);
            nextTactical = 0;
        }
    }

    private static void FinishConditionalAttack(ConditionalAttack order, string state, string reason)
    {
        order.Status.Retryable = false;
        order.Status.Set(state, reason);
    }
}
