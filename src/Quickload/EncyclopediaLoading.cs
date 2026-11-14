using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaLoading
{
    internal const double FrameBudgetMilliseconds = 4;
    internal static bool Ready { get; private set; }
    internal static void Reset() => Ready = false;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var manager = AccessTools.TypeByName("SeaPower.EncyclopedicDataManager");
        return new[] { "LoadSensors", "LoadWeapons", "LoadUnitsFromMetadata" }.Select(name => AccessTools.Method(manager, name));
    }

    private static void Postfix(MethodBase __originalMethod, object __instance, object[] __args, ref IEnumerator __result)
    {
        string part = __originalMethod.Name switch { "LoadSensors" => "sensors", "LoadWeapons" => "weapons", _ => "units" };
        var routine = EncyclopediaCache.Wrap(part, __instance, __args, part switch
        {
            "weapons" => EncyclopediaWeapons.Wrap(__instance, __args[0], __result),
            "units" => BatchUnits(__result),
            _ => __result,
        });
        __result = StartupLoading.BuildEncyclopediaPart(part, part == "units" ? MarkReady(routine) : routine);
    }

    private static IEnumerator MarkReady(IEnumerator routine)
    {
        yield return routine;
        Ready = true;
    }

    private static IEnumerator BatchUnits(IEnumerator original)
    {
        Stopwatch budget = Stopwatch.StartNew();
        int batchedYields = 0;
        try
        {
            while (original.MoveNext())
            {
                // The native coroutine yields null after each unit. Keep other Unity waits intact.
                object? current = original.Current;
                if (current == null && budget.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds)
                {
                    batchedYields++;
                    continue;
                }
                yield return current;
                budget.Restart();
            }
            UnityEngine.Debug.Log($"[Quickload] Encyclopedia completed with {batchedYields} per-unit frame waits batched.");
        }
        finally { (original as IDisposable)?.Dispose(); }
    }
}
