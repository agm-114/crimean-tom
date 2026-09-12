using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaPriority
{
    internal static readonly Dictionary<string, MuseumObjectEntry> Details = new(StringComparer.OrdinalIgnoreCase);
    private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(
        AccessTools.TypeByName("SeaPower.EncyclopedicDataManager"), "LoadUnitsFromMetadata"));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(UnitMetadataProvider), nameof(UnitMetadataProvider.UnitData));
        var replacement = AccessTools.Method(typeof(EncyclopediaPriority), nameof(OrderedUnits));
        bool found = false;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                found = true;
            }
            yield return instruction;
        }
        if (!found) throw new InvalidOperationException("Encyclopedia unit enumeration no longer matches the verified game version.");
    }
    private static Dictionary<(string, string), (int, SimpleObjectParameters)> OrderedUnits(UnitMetadataProvider provider) =>
        provider.UnitData.OrderByDescending(item => item.Key.Item2 == UnitCatalogue.Requested).ToDictionary(item => item.Key, item => item.Value);
}

[HarmonyPatch]
internal static class EncyclopediaDetailCapture
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeaPower.EncyclopedicDataManager"), "GetIndividualVesselFromMetadata");
    private static void Postfix(MuseumObjectEntry classEntry)
    {
        if (!string.IsNullOrEmpty(classEntry.IniRef)) EncyclopediaPriority.Details[classEntry.IniRef] = classEntry;
    }
}
