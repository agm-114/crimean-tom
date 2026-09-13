using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace Deckpark;

[HarmonyPatch]
internal static class DeckCollision
{
    // Other ships avoid the carrier's hull. Its deck equipment is not a separate vessel.
    [HarmonyTranspiler, HarmonyPatch(typeof(CollisionAvoidanceJobScheduler), nameof(CollisionAvoidanceJobScheduler.CreateAndSchedule))]
    private static IEnumerable<CodeInstruction> JobCandidates(IEnumerable<CodeInstruction> instructions) => Replace(instructions,
        AccessTools.PropertyGetter(typeof(ObjectBase), nameof(ObjectBase.IsDestroyed)), nameof(Exclude));

    [HarmonyTranspiler, HarmonyPatch(typeof(ObjectCollisionAvoidance), nameof(ObjectCollisionAvoidance.CheckCollisionAvoidance))]
    private static IEnumerable<CodeInstruction> LegacyCandidates(IEnumerable<CodeInstruction> instructions) => Replace(instructions,
        AccessTools.Method(typeof(ObjectBase), nameof(ObjectBase.isUnit)), nameof(IndependentUnit));

    private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, MethodInfo original, string replacement)
    {
        var code = instructions.ToList();
        if (code.Count(instruction => instruction.Calls(original)) != 1)
            throw new InvalidOperationException("Collision avoidance candidate check changed");
        foreach (var instruction in code)
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DeckCollision), replacement);
            }
        return code;
    }
    private static bool Exclude(ObjectBase unit) => unit.IsDestroyed || Mounted(unit);
    private static bool IndependentUnit(ObjectBase unit) => unit.isUnit() && !Mounted(unit);
    private static bool Mounted(ObjectBase unit) => unit is LandUnit ground && DeckOperations.Mounted(ground) != null;
}
