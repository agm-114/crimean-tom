using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace UNREP;

[HarmonyPatch(typeof(HelicopterFlightControlSystem), nameof(HelicopterFlightControlSystem.applyAIInputs))]
internal static class HelicopterSpeedPatch
{
    // Keep the native flight controller, including banking, yaw and altitude control.
    // Its distance-based speed is replaced only for our currently owned shuttle.
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Apply(IEnumerable<CodeInstruction> instructions)
    {
        var field = AccessTools.Field(typeof(HelicopterFlightControlSystem), nameof(HelicopterFlightControlSystem._allowedVelocityForDistance));
        var adjust = AccessTools.Method(typeof(HelicopterSpeedPatch), nameof(Adjust));
        int matches = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode != OpCodes.Stfld || !Equals(instruction.operand, field)) continue;
            matches++;
            yield return new(OpCodes.Ldarg_0);
            yield return new(OpCodes.Call, adjust);
        }
        if (matches == 0) throw new InvalidOperationException("UNREP helicopter speed hook no longer matches the game.");
    }

    private static void Adjust(HelicopterFlightControlSystem controls)
    {
        if (controls._baseObject is not Helicopter helicopter || helicopter.Aligning || helicopter._isLanding
            || helicopter.CurrentOrder.HasValue && helicopter.CurrentOrder.Value.OrderType == Order.Type.ReturnToBase) return;
        if (MovementLease.Demand(helicopter) is float speed) controls._allowedVelocityForDistance = speed;
    }

    [HarmonyPostfix]
    private static void HoldSpeed(HelicopterFlightControlSystem __instance, ref bool ____useVelocityFromAI)
    {
        if (__instance._baseObject is not Helicopter helicopter || helicopter.Aligning || helicopter._isLanding
            || helicopter.CurrentOrder.HasValue && helicopter.CurrentOrder.Value.OrderType == Order.Type.ReturnToBase) return;
        if (MovementLease.Demand(helicopter) is not float speed) return;
        // The native low-speed branch otherwise coasts with neutral cyclic.
        // Use its existing AI velocity path, with a bounded acceleration.
        __instance._allowedVelocityForDistance = Mathf.MoveTowards(helicopter._velocityInKnots, speed, 2 * GameTime.fixedDeltaTime);
        ____useVelocityFromAI = true;
    }
}
