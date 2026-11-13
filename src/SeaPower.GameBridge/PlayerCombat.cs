using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPower.GameBridge;

[HarmonyPatch]
internal static class PlayerCombat
{
    internal const double EvidenceLifetime = 180;
    internal record Gunfire(string AttackerId, string VictimId, string Victim, bool FriendlyVictim, double LastSeen);
    internal record MissileImpact(string TargetId, string Target, string Category, string Posture, string? MissileId, string Detail);
    private record OwnFire(string UnitId, bool Aircraft, string TargetId, string Weapon, int Shots, double LastFired);

    private static readonly Dictionary<(string Attacker, string Victim), Gunfire> GunfireByPair = [];
    private static readonly Dictionary<(string Unit, string Target, string Weapon), OwnFire> OwnFireByWeapon = [];
    private static readonly Dictionary<WeaponBase, ObjectBase> OwnGuidedWeaponTargets = [];
    private static readonly Queue<MissileImpact> MissileImpacts = new();
    private static readonly Queue<string> OwnLaunchReports = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.Destruction))]
    private static void ObserveMissileImpact(WeaponBase __instance, ObjectBase targetObject)
    {
        if (__instance is not Missile || __instance.IsDestroyed || !targetObject || PlayerPicture.Side is not { } side) return;
        Vehicle? weapon = __instance.GetMapVehicle(side);
        Vehicle? target = targetObject.GetMapVehicle(side);
        bool owned = targetObject._taskforce == side;
        bool ownWeapon = __instance._taskforce == side;
        bool friendly = owned || target?.CurrentRelationship() == RelationsState.Friendly;
        if (!owned && (target == null || !PlayerPicture.IsVisual(target) && !(ownWeapon && friendly && PlayerPicture.IsLive(target))
            || !ownWeapon && (weapon == null || !PlayerPicture.IsVisual(weapon)))) return;
        string detail = ownWeapon && __instance._launchPlatform is { } shooter && shooter
            ? $"{shooter.DisplayNameShort}’s {__instance._ap._displayedName} hit {(friendly ? "a friendly target" : "the target")}" : "Missile hit target";

        if (MissileImpacts.Count == 32) MissileImpacts.Dequeue();
        MissileImpacts.Enqueue(new(owned ? PlayerPicture.Id(targetObject) : PlayerPicture.Id(target!),
            owned ? targetObject.DisplayNameShort : PlayerPicture.ContactLabel(target!),
            target?.GetMapType().ToString() ?? targetObject._type.ToString(),
            owned ? "Own" : target!.CurrentRelationship().ToString(),
            weapon != null && PlayerPicture.IsLive(weapon) ? PlayerPicture.Id(weapon) : null, detail));
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
        if (__instance._ws is not WeaponSystemGun || __instance._launchPlatform is not { } source || !source
            || __instance._targetObject is not { } target || !target || PlayerPicture.Side is not { } side) return;
        if (source._taskforce == side)
        {
            RecordOwnFire(side, source, target, __instance._pp._displayedName, missile: false);
            return;
        }
        if (source is not Vessel || target is not (Vessel or Submarine)
            || source.GetMapVehicle(side) is not { } attacker || !PlayerPicture.IsVisual(attacker)) return;

        Vehicle? victim = target.GetMapVehicle(side);
        bool owned = target._taskforce == side;
        if (!owned && (victim == null || !PlayerPicture.IsLive(victim)
            || victim.CurrentRelationship() != RelationsState.Friendly && !PlayerPicture.IsVisual(victim))) return;

        string attackerId = PlayerPicture.Id(attacker);
        string victimId = owned ? PlayerPicture.Id(target) : PlayerPicture.Id(victim!);
        string victimName = owned ? target.DisplayNameShort : PlayerPicture.ContactLabel(victim!);
        bool friendly = owned || victim!.CurrentRelationship() == RelationsState.Friendly;
        GunfireByPair[(attackerId, victimId)] = new(attackerId, victimId, victimName, friendly, GameTime.missionElapsedTime);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WeaponBase), nameof(WeaponBase.CommonLaunchSettings))]
    private static void ObserveLaunch(WeaponBase __instance, ObjectBase targetObject, bool isSubmunition)
    {
        bool guided = __instance is Missile or Torpedo;
        if (guided) OwnGuidedWeaponTargets.Remove(__instance);
        if (isSubmunition || __instance._launchPlatform is not (Vessel or Submarine or Aircraft or Helicopter)
            || __instance._launchPlatform is not { } source || !source || !targetObject
            || PlayerPicture.Side is not { } side || source._taskforce != side) return;
        if (source is Vessel && !guided) return;
        if (guided) OwnGuidedWeaponTargets[__instance] = targetObject;
        if (__instance._ap?._displayedName is { Length: > 0 } weapon)
            RecordOwnFire(side, source, targetObject, weapon, guided);
    }

    internal static IEnumerable<ObjectBase> MissileTargetsInFlight(bool includeAircraft = false)
    {
        PruneGuidedWeapons();
        return OwnGuidedWeaponTargets.Where(entry => entry.Key is Missile && (includeAircraft || entry.Key._launchPlatform is Vessel or Submarine))
            .Select(entry => entry.Value).Where(target => target && !target.IsDestroyed).Distinct();
    }

    internal static bool TorpedoInbound(ObjectBase target)
    {
        PruneGuidedWeapons();
        return OwnGuidedWeaponTargets.Any(entry => entry.Key is Torpedo && entry.Value == target);
    }

    private static void PruneGuidedWeapons()
    {
        foreach (WeaponBase weapon in OwnGuidedWeaponTargets.Keys.Where(weapon => !weapon || weapon.IsDestroyed
            || !weapon.gameObject.activeInHierarchy).ToArray()) OwnGuidedWeaponTargets.Remove(weapon);
    }

    internal static double? LastOwnFireAt(string targetId) => OwnFireByWeapon.Values.Where(item => item.TargetId == targetId)
        .Select(item => (double?)item.LastFired).Max();

    internal static int OwnShotCount(string unitId, string targetId, string? weapon = null) => OwnFireByWeapon.Values
        .Where(item => item.UnitId == unitId && item.TargetId == targetId && (weapon == null || item.Weapon == weapon)).Sum(item => item.Shots);

    private static void RecordOwnFire(Taskforce side, ObjectBase source, ObjectBase target, string weapon, bool missile)
    {
        if (target.GetMapVehicle(side) is not { } track || !track.IsVisible()) return;
        string unitId = PlayerPicture.Id(source);
        string targetId = PlayerPicture.Id(track);
        double now = GameTime.missionElapsedTime;
        var key = (unitId, targetId, weapon);
        OwnFire? previous = OwnFireByWeapon.GetValueOrDefault(key);
        OwnFireByWeapon[key] = new(unitId, source is Aircraft or Helicopter, targetId, weapon, (previous?.Shots ?? 0) + 1, now);
        if (missile || previous == null || now - previous.LastFired >= 30)
        {
            if (OwnLaunchReports.Count == 32) OwnLaunchReports.Dequeue();
            OwnLaunchReports.Enqueue($"{source.DisplayNameShort} fired {weapon} at {PlayerPicture.ContactLabel(track)} ({targetId}) · T+{now:0}s");
        }
    }

    internal static bool TryTakeLaunchReport(out string report) => OwnLaunchReports.TryDequeue(out report!);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoUnload))]
    private static void Clear()
    {
        GunfireByPair.Clear();
        OwnFireByWeapon.Clear();
        OwnGuidedWeaponTargets.Clear();
        OwnLaunchReports.Clear();
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

    internal static JArray OwnFireSnapshot(bool aircraft = false) => new(OwnFireByWeapon.Values.Where(item => item.Aircraft == aircraft)
        .OrderByDescending(item => item.LastFired).Take(24)
        .Select(item => new JObject { [aircraft ? "unitId" : "shipId"] = item.UnitId, ["targetId"] = item.TargetId, ["weapon"] = item.Weapon,
            ["shots"] = item.Shots, ["lastFiredSeconds"] = item.LastFired }));
}
