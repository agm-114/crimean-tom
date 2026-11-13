using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal partial class DecisionController
{
    private record ConditionalAttack(string UnitId, string TargetId, string Attack, SurfaceWeapon Weapon, double ExpiresAt, string Reason);
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
        ["expiresAt"] = order.ExpiresAt, ["reason"] = order.Reason
    }));

    private void ApplyConditionalAttacks(JToken? orders, Taskforce side)
    {
        if (LastInput is not { } input) return;
        Dictionary<string, ObjectBase> owned = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        foreach (JObject item in orders?.OfType<JObject>().Take(8) ?? [])
        {
            if ((string?)item["unitId"] is not { } id || (string?)item["targetId"] is not { } targetId) continue;
            string reason = Short((string?)item["reason"]);
            var key = (id, targetId);
            if ((string?)item["action"] == "Cancel")
            {
                if (conditionalAttacks.Remove(key)) Add("Conditional attack", "Canceled", reason, id, targetId: targetId);
                continue;
            }
            if ((string?)item["action"] != "Arm" || !owned.TryGetValue(id, out ObjectBase? unit)
                || (string?)item["attack"] is not { } attack || !Enum.TryParse((string?)item["weapons"], out SurfaceWeapon weapon)
                || (double?)item["expiresInSeconds"] is not double duration || !double.IsFinite(duration) || duration <= 0
                || input["contacts"]?.Any(track => (string?)track["id"] == targetId
                    && (string?)track["posture"] is not ("Friendly" or "Neutral")) != true) continue;
            if (PlayerPicture.LiveContact(side, targetId)?.CurrentRelationship() is RelationsState.Friendly or RelationsState.Neutral) continue;
            bool eligible = attack switch
            {
                "EngageShip" => unit is Vessel or Submarine,
                "Bombard" => unit is Vessel,
                "CAP" or "SEAD" or "Strike" or "Strafe" or "AntiShip" => input["airborne"]?.Any(flight => (string?)flight["id"] == id) == true,
                _ => false
            };
            if (!eligible || conditionalAttacks.ContainsKey(key) || conditionalAttacks.Count >= 16) continue;
            if (Mode == ControlMode.AiControl)
                conditionalAttacks[key] = new(id, targetId, attack, weapon, GameTime.missionElapsedTime + Math.Min(duration, 1800), reason);
            Add("Conditional attack", Mode == ControlMode.AiControl ? "Armed" : "Suggested", $"{attack} when the track is live · {reason}",
                id, unit.DisplayNameShort, targetId: targetId);
        }
    }

    private void RunConditionalAttacks(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (conditionalAttacks.Count == 0 || now < nextConditionalAttackCheck) return;
        nextConditionalAttackCheck = now + 3;
        Dictionary<string, ObjectBase> owned = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        foreach (var (key, order) in conditionalAttacks.ToArray())
        {
            if (now >= order.ExpiresAt || !owned.TryGetValue(order.UnitId, out ObjectBase? unit))
            {
                conditionalAttacks.Remove(key);
                Add("Conditional attack", "Expired", order.Reason, order.UnitId, targetId: order.TargetId);
                continue;
            }
            if (PlayerPicture.LiveContact(side, order.TargetId) is not { } track) continue;
            if (track.CurrentRelationship() is RelationsState.Friendly or RelationsState.Neutral)
            {
                conditionalAttacks.Remove(key);
                Add("Conditional attack", "Canceled", "Contact is now friendly or neutral", order.UnitId, unit.DisplayNameShort,
                    targetId: order.TargetId);
                continue;
            }
            if (track.CurrentRelationship() != RelationsState.Hostile || unit._weaponStatus == ObjectBase.WeaponStatus.Hold
                || unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase) continue;
            if (unit._currentEngageTasks.Any(task => task._targetObject && task._targetObject != track.BaseObject)) continue;
            bool compatibleTarget = order.Attack switch
            {
                "EngageShip" or "AntiShip" => track.BaseObject is Vessel,
                "CAP" => track.BaseObject.IsAirUnit,
                _ => track.BaseObject is LandUnit
            };
            if (!compatibleTarget)
            {
                conditionalAttacks.Remove(key);
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
                Add("Conditional attack", "Failed", error.Message, order.UnitId, unit.DisplayNameShort, targetId: order.TargetId);
                continue;
            }
            if (!status.Contains("queued", StringComparison.OrdinalIgnoreCase) && !status.StartsWith("Attack ordered", StringComparison.Ordinal)
                && !status.StartsWith("Already engaging", StringComparison.Ordinal) && !status.StartsWith("Kept · attack already assigned", StringComparison.Ordinal)) continue;
            conditionalAttacks.Remove(key);
            Add("Conditional attack", status, $"{PlayerPicture.ContactLabel(track)} · {order.Reason}", order.UnitId, unit.DisplayNameShort,
                targetId: order.TargetId);
            RecordFlightAssignment(order.UnitId, order.Attack, order.TargetId, status);
            nextTactical = 0;
        }
    }
}
