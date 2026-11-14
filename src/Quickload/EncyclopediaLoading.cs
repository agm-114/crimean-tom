using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaLoading
{
    private const double FrameBudgetMilliseconds = 4;
    internal static bool Ready { get; private set; }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var manager = AccessTools.TypeByName("SeaPower.EncyclopedicDataManager");
        return new[] { "LoadSensors", "LoadWeapons", "LoadUnitsFromMetadata" }.Select(name => AccessTools.Method(manager, name));
    }

    private static void Postfix(MethodBase __originalMethod, ref IEnumerator __result)
    {
        string part = __originalMethod.Name switch { "LoadSensors" => "sensors", "LoadWeapons" => "weapons", _ => "units" };
        __result = StartupLoading.BuildEncyclopediaPart(part, part == "units" ? BatchUnits(__result) : __result);
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
            Ready = true;
            UnityEngine.Debug.Log($"[Quickload] Encyclopedia completed with {batchedYields} per-unit frame waits batched.");
        }
        finally { (original as IDisposable)?.Dispose(); }
    }
}
