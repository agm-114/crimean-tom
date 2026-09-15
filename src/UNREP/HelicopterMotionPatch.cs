using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace UNREP;

// HasControl is also a sensor/seeker eligibility flag. Keep it true for VERTREP
// and suppress only the movement and own-AI paths while our controller owns pose.
[HarmonyPatch]
internal static class HelicopterMotionPatch
{
    private static bool NativeControl(ObjectBase unit) => unit.HasControl && !VertrepLanding.OwnsMotion(unit);
    private static bool IndependentControl(ObjectBase unit) => unit.HasControl
        && !(VertrepLanding.OwnsMotion(unit) && unit.transform.parent != null);

    [HarmonyTranspiler, HarmonyPatch(typeof(Helicopter), nameof(Helicopter.OnFixedUpdate))]
    private static IEnumerable<CodeInstruction> Movement(IEnumerable<CodeInstruction> instructions) =>
        ReplaceControlRead(instructions, nameof(NativeControl), field: false);

    [HarmonyPrefix, HarmonyPatch(typeof(Helicopter), nameof(Helicopter.PerformMovement))]
    private static bool Guidance(Helicopter __instance) => !VertrepLanding.OwnsMotion(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(AI), nameof(AI.OnFixedUpdate))]
    private static bool Orders(ObjectBase ____baseObject) => !VertrepLanding.OwnsMotion(____baseObject);

    // Parented aircraft move with their ship during origin relocation. Preserve
    // that native exception even though they now remain eligible combat targets.
    [HarmonyTranspiler, HarmonyPatch(typeof(RelocationHandler), nameof(RelocationHandler.relocate))]
    private static IEnumerable<CodeInstruction> Relocate(IEnumerable<CodeInstruction> instructions) =>
        ReplaceControlRead(instructions, nameof(IndependentControl), field: true);

    [HarmonyTranspiler, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.OnFixedUpdate))]
    private static IEnumerable<CodeInstruction> RelocationFallback(IEnumerable<CodeInstruction> instructions) =>
        ReplaceControlRead(instructions, nameof(IndependentControl), field: false);

    private static IEnumerable<CodeInstruction> ReplaceControlRead(IEnumerable<CodeInstruction> instructions, string method, bool field)
    {
        var getter = AccessTools.PropertyGetter(typeof(ObjectBase), nameof(ObjectBase.HasControl));
        var control = AccessTools.Field(typeof(ObjectBase), nameof(ObjectBase._hasControl));
        var replacement = AccessTools.Method(typeof(HelicopterMotionPatch), method);
        int matches = 0;
        foreach (var instruction in instructions)
        {
            bool match = field ? instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, control) : instruction.Calls(getter);
            if (match)
            {
                matches++;
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
        if (matches != 1) throw new InvalidOperationException($"UNREP {method} hook expected one native control check, found {matches}.");
    }
}
