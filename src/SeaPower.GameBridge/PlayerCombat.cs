using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPower.GameBridge;

[HarmonyPatch]
internal static class PlayerCombat
{
    internal const double EvidenceLifetime = 180;
    internal record Gunfire(string AttackerId, string VictimId, string Victim, bool FriendlyVictim, double LastSeen);
    internal record MissileImpact(string TargetId, string Target, string Category, string Posture, string? MissileId);

    private static readonly Dictionary<(string Attacker, string Victim), Gunfire> GunfireByPair = [];
    private static readonly Queue<MissileImpact> MissileImpacts = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.Destruction))]
    private static void ObserveMissileImpact(WeaponBase __instance, ObjectBase targetObject)
    {
        if (__instance is not Missile || __instance.IsDestroyed || !targetObject || PlayerPicture.Side is not { } side) return;
        Vehicle? weapon = __instance.GetMapVehicle(side);
        Vehicle? target = targetObject.GetMapVehicle(side);
        bool owned = targetObject._taskforce == side;
        if (!owned && (target == null || !PlayerPicture.IsVisual(target)
            || __instance._taskforce != side && (weapon == null || !PlayerPicture.IsVisual(weapon)))) return;

        if (MissileImpacts.Count == 32) MissileImpacts.Dequeue();
        MissileImpacts.Enqueue(new(owned ? PlayerPicture.Id(targetObject) : PlayerPicture.Id(target!),
            owned ? targetObject.DisplayNameShort : PlayerPicture.ContactLabel(target!),
            target?.GetMapType().ToString() ?? targetObject._type.ToString(),
            owned ? "Own" : target!.CurrentRelationship().ToString(),
            weapon != null && PlayerPicture.IsLive(weapon) ? PlayerPicture.Id(weapon) : null));
    }

    internal static MissileImpact[] TakeMissileImpacts()
    {
        if (MissileImpacts.Count == 0) return [];
        MissileImpact[] events = MissileImpacts.ToArray();
        MissileImpacts.Clear();
        return events;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.fire))]
    private static void ObserveGunfire(Projectile __instance)
    {
        if (__instance._ws is not WeaponSystemGun || __instance._launchPlatform is not Vessel source || !source
            || __instance._targetObject is not Vessel target || !target || PlayerPicture.Side is not { } side
            || source._taskforce == side || source.GetMapVehicle(side) is not { } attacker || !PlayerPicture.IsVisual(attacker)) return;

        Vehicle? victim = target.GetMapVehicle(side);
        bool owned = target._taskforce == side;
        if (!owned && (victim == null || !PlayerPicture.IsVisual(victim))) return;

        string attackerId = PlayerPicture.Id(attacker);
        string victimId = owned ? PlayerPicture.Id(target) : PlayerPicture.Id(victim!);
        string victimName = owned ? target.DisplayNameShort : PlayerPicture.ContactLabel(victim!);
        bool friendly = owned || victim!.CurrentRelationship() == RelationsState.Friendly;
        GunfireByPair[(attackerId, victimId)] = new(attackerId, victimId, victimName, friendly, GameTime.missionElapsedTime);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoUnload))]
    private static void Clear()
    {
        GunfireByPair.Clear();
        MissileImpacts.Clear();
    }

    internal static IEnumerable<Gunfire> Recent()
    {
        double now = GameTime.missionElapsedTime;
        foreach (var key in GunfireByPair.Where(entry => now - entry.Value.LastSeen > EvidenceLifetime).Select(entry => entry.Key).ToArray())
            GunfireByPair.Remove(key);
        return GunfireByPair.Values;
    }

    internal static JArray Snapshot() => new(Recent().Select(item => new JObject
    {
        ["attackerId"] = item.AttackerId, ["victimId"] = item.VictimId, ["victim"] = item.Victim,
        ["friendlyVictim"] = item.FriendlyVictim, ["lastSeenSeconds"] = item.LastSeen
    }));
}
