using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace CrimeanTom.SeaPower;

[HarmonyPatch(typeof(AI), nameof(AI.AIDetection))]
internal static class GeneratedSubmarineContactsPatch
{
    private static bool AcceptTaskforce(Taskforce candidate, Taskforce designatedEnemy, ObjectBase observer) =>
        observer is Submarine && (GeneratedScenarioRuntime.IsDelegatedTaskforce(observer._taskforce) ||
            GeneratedScenarioRuntime.IsDelegatedTaskforce(candidate))
            ? observer._taskforce.RelationshipTo(candidate) == RelationsState.Hostile
            : candidate == designatedEnemy;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        var taskforceGetter = AccessTools.PropertyGetter(typeof(ObjectBase), "_taskforce");
        var guards = Enumerable.Range(2, code.Count - 2).Where(index =>
            code[index - 2].Calls(taskforceGetter) && code[index - 1].opcode == OpCodes.Ldloc_1 &&
            (code[index].opcode == OpCodes.Bne_Un || code[index].opcode == OpCodes.Bne_Un_S)).ToArray();
        if (guards.Length != 1)
            throw new InvalidOperationException("Sea Power submarine contact filtering changed; recheck AIDetection before deploying.");

        for (int index = 0; index < code.Count; index++)
        {
            CodeInstruction instruction = code[index];
            if (index != guards[0]) { yield return instruction; continue; }
            // Replace only the taskforce identity gate. Native sensor, identification and firing checks remain intact.
            object skipContact = instruction.operand;
            instruction.opcode = OpCodes.Ldarg_0;
            instruction.operand = null;
            yield return instruction;
            yield return new(OpCodes.Ldfld, AccessTools.Field(typeof(AI), "_baseObject"));
            yield return new(OpCodes.Call, AccessTools.Method(typeof(GeneratedSubmarineContactsPatch), nameof(AcceptTaskforce)));
            yield return new(OpCodes.Brfalse, skipContact);
        }
    }
}
