using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaWeapons
{
    private const double BudgetMilliseconds = EncyclopediaLoading.FrameBudgetMilliseconds;
    private static readonly Type managerType = AccessTools.TypeByName("SeaPower.EncyclopedicDataManager");
    private static readonly MethodInfo load = AccessTools.Method(managerType, "LoadWeapons");
    private static readonly MethodInfo setup = AccessTools.Method(managerType, "SetupWeaponDisplayObject");
    private static readonly MethodInfo getEntries = AccessTools.PropertyGetter(managerType, "WeaponMuseumEntries");
    private static readonly MethodBase moveNext = AccessTools.EnumeratorMoveNext(load);
    private static readonly MethodInfo clone = AccessTools.Method(typeof(object), "MemberwiseClone");
    private static List<string>? files;
    private static bool batching;
    private static bool sliced;

    private static MethodBase TargetMethod() => moveNext;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> SplitLoop(IEnumerable<CodeInstruction> code)
    {
        var instructions = code.ToList();
        var getFiles = AccessTools.Method(typeof(FileManager), nameof(FileManager.GetFiles),
            [typeof(string), typeof(string), typeof(LayerKind)]);
        var enumerate = instructions.Where(instruction => instruction.Calls(getFiles)).ToArray();
        var clear = instructions.Where(instruction => instruction.operand is MethodInfo method
            && method.Name == "Clear" && method.DeclaringType?.GenericTypeArguments.Contains(typeof(MuseumWeaponEntry)) == true).ToArray();
        var finish = instructions.Where(instruction => instruction.Calls(setup)).ToArray();
        sliced = enumerate.Length == 1 && clear.Length == 1 && finish.Length == 1;
        if (!sliced) return instructions;
        Replace(enumerate[0], nameof(GetFiles));
        Replace(clear[0], nameof(Clear));
        Replace(finish[0], nameof(Setup));
        return instructions;
    }

    private static void Replace(CodeInstruction instruction, string method)
    {
        instruction.opcode = OpCodes.Call;
        instruction.operand = AccessTools.Method(typeof(EncyclopediaWeapons), method);
    }

    private static List<string> GetFiles(FileManager manager, string path, string pattern, LayerKind kind) =>
        files ?? manager.GetFiles(path, pattern, kind);

    private static void Clear(System.Collections.ObjectModel.Collection<MuseumWeaponEntry> entries)
    {
        if (!batching) entries.Clear();
    }

    private static void Setup(object manager, System.Collections.ObjectModel.ObservableCollection<MuseumEntry> entries)
    {
        if (!batching) setup.Invoke(manager, [entries]);
    }

    internal static IEnumerator Wrap(object manager, object display, IEnumerator original)
    {
        if (!sliced || !StartupLoading.DeferredEncyclopedia)
        {
            yield return original;
            yield break;
        }
        using var template = original as IDisposable;
        ((IList)getEntries.Invoke(manager, null)).Clear();
        var ammunition = Singleton<FileManager>.Instance.GetFiles("ammunition", "*.ini");
        var clock = Stopwatch.StartNew();
        var one = new List<string>(1) { "" };
        foreach (string file in ammunition)
        {
            one[0] = file;
            // Only redirect the immediate native MoveNext. Other loads can run between frames.
            var item = (IEnumerator)clone.Invoke(original, null);
            using var disposable = item as IDisposable;
            batching = true;
            files = one;
            try { item.MoveNext(); }
            finally
            {
                batching = false;
                files = null;
            }
            if (clock.Elapsed.TotalMilliseconds < BudgetMilliseconds) continue;
            yield return null;
            clock.Restart();
        }
        setup.Invoke(manager, [display]);
        yield return null;
    }
}
