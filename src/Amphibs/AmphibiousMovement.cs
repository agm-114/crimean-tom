using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace Amphibs;

[HarmonyPatch]
internal static class AmphibiousMovement
{
    [HarmonyTranspiler, HarmonyPatch(typeof(LandUnit), "ApplyMovement")]
    private static IEnumerable<CodeInstruction> AllowWater(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var height = AccessTools.Method(typeof(TerrainUtils), nameof(TerrainUtils.GetHeightAt), [typeof(GeoPosition), typeof(bool), typeof(bool)]);
        var replacement = AccessTools.Method(typeof(AmphibiousMovement), nameof(MovementHeight));
        if (code.Count(instruction => instruction.Calls(height)) != 2)
            throw new InvalidOperationException("LandUnit.ApplyMovement terrain checks changed.");
        foreach (var instruction in code)
        {
            if (instruction.Calls(height))
            {
                var unit = new CodeInstruction(OpCodes.Ldarg_0);
                unit.MoveLabelsFrom(instruction);
                unit.MoveBlocksFrom(instruction);
                yield return unit;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    private static float MovementHeight(GeoPosition position, bool log, bool isCalledFromJob, LandUnit unit)
    {
        float height = TerrainUtils.GetHeightAt(position, log, isCalledFromJob);
        return AmphibiousCatalog.TryGet(unit, out _) ? Math.Max(0, height) : height;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LandUnit), "ApplyMovement")]
    private static void KeepSteeringLevel(LandUnit __instance)
    {
        if (AmphibiousCatalog.TryGet(__instance, out _) && Afloat(__instance))
            __instance.transform.rotation = Quaternion.Euler(0, __instance.transform.eulerAngles.y, 0);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(LandUnit), "ApplyVelocity")]
    private static void LimitSpeed(LandUnit __instance, ref float wantedVelocity)
    {
        if (!AmphibiousCatalog.TryGet(__instance, out var profile) || !Afloat(__instance)) return;
        wantedVelocity = Mathf.Clamp(wantedVelocity, -profile.Speed, profile.Speed);
        __instance._velocityInKnots = Mathf.Clamp(__instance._velocityInKnots, -profile.Speed, profile.Speed);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.OnUpdateEveryFrame))]
    private static void FloatFrame(LandUnit __instance) => Float(__instance);

    [HarmonyPostfix, HarmonyPatch(typeof(LandUnit), nameof(LandUnit.OnFixedUpdate))]
    private static void Float(LandUnit __instance)
    {
        if (__instance.IsDestroyed || !AmphibiousCatalog.TryGet(__instance, out var profile)) return;
        __instance.GetComponent<AmphibiousFlotation>()?.Apply(__instance, profile);
        if (!Afloat(__instance)) return;
        if (__instance.RigidBody is { } body)
        {
            body.useGravity = false;
            if (!body.isKinematic) body.linearVelocity = new Vector3(body.linearVelocity.x, 0, body.linearVelocity.z);
        }
        __instance.LandUnitParams._dustEffectPS?.Stop();
    }

    internal static bool Afloat(ObjectBase unit) => TerrainUtils.GetHeightAt(unit._geoPosition) < 0;

    [HarmonyPrefix, HarmonyPatch(typeof(WeaponSystemGun), nameof(WeaponSystemGun.fire))]
    private static bool AllowGun(WeaponSystemGun __instance) => AllowFire(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(WeaponSystemLauncher), nameof(WeaponSystemLauncher.launch))]
    private static bool AllowMissile(WeaponSystemLauncher __instance) => AllowFire(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(WeaponSystemCIWS), nameof(WeaponSystemCIWS.StartFire))]
    private static bool AllowCiws(WeaponSystemCIWS __instance) => AllowFire(__instance);

    private static bool AllowFire(WeaponSystem weapon) =>
        !AmphibiousCatalog.TryGet(weapon._baseObject, out var profile) || profile.WeaponsAfloat || !Afloat(weapon._baseObject);
}
